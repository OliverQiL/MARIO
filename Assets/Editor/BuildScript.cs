using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Builds the Xcode project into ios-build/ so it can be compiled and
/// deployed from the terminal (xcodebuild + devicectl).
///
/// Menu:     Tools ▸ MARIO ▸ Build iOS             (development build: profiler, debug logs, watermark)
///           Tools ▸ MARIO ▸ Build iOS (Release)   (what gets frozen and demoed: no watermark, faster)
/// Headless: Unity -batchmode -quit -projectPath . -buildTarget iOS -executeMethod BuildScript.BuildIOS
///           Unity -batchmode -quit -projectPath . -buildTarget iOS -executeMethod BuildScript.BuildIOSRelease
/// </summary>
public static class BuildScript
{
    private const string OutputPath = "ios-build";

    [MenuItem("Tools/MARIO/Build iOS")]
    public static void BuildIOS() => Build(BuildOptions.Development);

    [MenuItem("Tools/MARIO/Build iOS (Release)")]
    public static void BuildIOSRelease() => Build(BuildOptions.None);

    private static void Build(BuildOptions buildOptions)
    {
        // The game is landscape (the HUD is laid out for it; the phone sits sideways in the headset).
        // Either landscape direction, never portrait.
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
        PlayerSettings.allowedAutorotateToLandscapeRight = true;

        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = OutputPath,
            target = BuildTarget.iOS,
            targetGroup = BuildTargetGroup.iOS,
            options = buildOptions,
        };

        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;
        Debug.Log($"[BuildScript] iOS {(buildOptions.HasFlag(BuildOptions.Development) ? "development" : "release")} build {summary.result}: {summary.totalErrors} errors, {summary.totalTime.TotalSeconds:F0}s, output '{OutputPath}'.");

        if (Application.isBatchMode && summary.result != BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }
}
