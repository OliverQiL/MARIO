using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds the Xcode project for one of two gateway profiles, so both apps can
/// sit on the phone side by side:
///
///   Local  "MARIO Local", com.cg4002.mario.local → ios-build-local/
///          this Mac's tools/mock_gateway.py (pin read from tools/certs/gateway.crt)
///   John   "MARIO",       com.cg4002.mario       → ios-build-john/
///          John's laptop gateway on its Windows hotspot (integrated/laptop, visualizer mode)
///
/// The profile's URL and pin are written into the scene's NetworkClient only for
/// the build and restored afterwards, as are the product name and bundle ID, so
/// the Editor keeps playing against whatever the scene is set to.
///
/// Menu:     Tools ▸ MARIO ▸ Build iOS (Local | John) [Release]
///           development builds have the profiler, debug logs and watermark; Release is what gets demoed.
/// Headless: Unity -batchmode -quit -projectPath . -buildTarget iOS -executeMethod BuildScript.BuildIOSJohn
///                 [-gatewayUrl wss://host:port/] [-pin SHA256]   (override the profile for one build)
///           methods: BuildIOSLocal, BuildIOSLocalRelease, BuildIOSJohn, BuildIOSJohnRelease
/// </summary>
public static class BuildScript
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";

    private sealed class Profile
    {
        public string Name, ProductName, BundleId, OutputPath, GatewayUrl, Pin, PinCertFile;
    }

    private static readonly Profile Local = new()
    {
        Name = "Local",
        ProductName = "MARIO Local",
        BundleId = "com.cg4002.mario.local",
        OutputPath = "ios-build-local",
        GatewayUrl = "wss://Olis-MacBook-Pro.local:8765/",
        PinCertFile = "tools/certs/gateway.crt", // mock_gateway.py's self-signed certificate
    };

    private static readonly Profile John = new()
    {
        Name = "John",
        ProductName = "MARIO",
        BundleId = "com.cg4002.mario",
        OutputPath = "ios-build-john",
        GatewayUrl = "wss://192.168.137.1:8765/", // Windows Mobile Hotspot address
        // John's comms/security/server.pem fingerprint (integrated/SECURITY.md §2):
        //   openssl x509 -in comms/security/server.pem -noout -fingerprint -sha256
        // or his gateway's startup line "VISUALIZER GATEWAY: certificate SHA-256 ...". Either form pastes as is.
        // Only the fingerprint is checked (not expiry or hostname), so this holds until he makes a new certificate.
        Pin = "9A1017B8EAEE4D880A6029C0AA2303A37F1881CA0A7B4A81DEDD91FDBCA25027", // his server.pem, created 2026-10-18
    };

    [MenuItem("Tools/MARIO/Build iOS (Local)")]
    public static void BuildIOSLocal() => Build(Local, BuildOptions.Development);

    [MenuItem("Tools/MARIO/Build iOS (Local, Release)")]
    public static void BuildIOSLocalRelease() => Build(Local, BuildOptions.None);

    [MenuItem("Tools/MARIO/Build iOS (John)")]
    public static void BuildIOSJohn() => Build(John, BuildOptions.Development);

    [MenuItem("Tools/MARIO/Build iOS (John, Release)")]
    public static void BuildIOSJohnRelease() => Build(John, BuildOptions.None);

    private static void Build(Profile profile, BuildOptions buildOptions)
    {
        var url = Arg("-gatewayUrl") ?? profile.GatewayUrl;
        var pin = GatewayConnection.NormaliseFingerprint(Arg("-pin") ?? ResolvePin(profile));
        if (string.IsNullOrWhiteSpace(pin))
        {
            Fail($"[BuildScript] No certificate pin for the {profile.Name} profile. Set it in BuildScript.cs or pass -pin <SHA256>.");
            return;
        }

        // The game is landscape (the HUD is laid out for it; the phone sits sideways in the headset).
        // Either landscape direction, never portrait.
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
        PlayerSettings.allowedAutorotateToLandscapeRight = true;

        var oldProduct = PlayerSettings.productName;
        var oldBundle = PlayerSettings.GetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.iOS);
        var old = OpenNetworkClient();
        if (old == null) { Fail("[BuildScript] No NetworkClient in the scene (run NetworkSetup)."); return; }
        var oldUrl = old.FindProperty("gatewayUrl").stringValue;
        var oldPin = old.FindProperty("pinnedCertSha256").stringValue;

        BuildSummary summary;
        try
        {
            PlayerSettings.productName = profile.ProductName;
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.iOS, profile.BundleId);
            SetGateway(url, pin);

            var options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = profile.OutputPath,
                target = BuildTarget.iOS,
                targetGroup = BuildTargetGroup.iOS,
                options = buildOptions,
            };
            summary = BuildPipeline.BuildPlayer(options).summary;
        }
        finally
        {
            PlayerSettings.productName = oldProduct;
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.iOS, oldBundle);
            SetGateway(oldUrl, oldPin); // the build reloads the scene, so this reopens it
        }

        Debug.Log($"[BuildScript] iOS {profile.Name} {(buildOptions.HasFlag(BuildOptions.Development) ? "development" : "release")} build {summary.result}: " +
                  $"{summary.totalErrors} errors, {summary.totalTime.TotalSeconds:F0}s, '{profile.ProductName}' ({profile.BundleId}) → {url}, pin {pin}, output '{profile.OutputPath}'.");

        if (Application.isBatchMode && summary.result != BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }

    private static string ResolvePin(Profile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Pin)) return profile.Pin;
        if (profile.PinCertFile == null || !File.Exists(profile.PinCertFile)) return null;
        // X509Certificate reads PEM's DER body; its raw data is what the app hashes (GatewayConnection.Fingerprint).
        var pem = File.ReadAllText(profile.PinCertFile);
        var body = pem.Split(new[] { "-----BEGIN CERTIFICATE-----", "-----END CERTIFICATE-----" }, StringSplitOptions.None)[1];
        return GatewayConnection.Fingerprint(new X509Certificate(Convert.FromBase64String(body.Trim())));
    }

    private static SerializedObject OpenNetworkClient()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var client = UnityEngine.Object.FindAnyObjectByType<NetworkClient>();
        return client == null ? null : new SerializedObject(client);
    }

    private static void SetGateway(string url, string pin)
    {
        var so = OpenNetworkClient();
        so.FindProperty("gatewayUrl").stringValue = url;
        so.FindProperty("pinnedCertSha256").stringValue = pin;
        so.ApplyModifiedPropertiesWithoutUndo();
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    private static void Fail(string message)
    {
        Debug.LogError(message);
        if (Application.isBatchMode) EditorApplication.Exit(1);
    }

    private static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
