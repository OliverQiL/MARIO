using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

/// <summary>
/// Patches the generated Xcode project's Info.plist after every iOS build.
/// NSLocalNetworkUsageDescription is required on iOS 14+ for the app to reach
/// the gateway on the LAN — without it the connection is silently blocked.
/// </summary>
public static class IOSPostProcess
{
    [PostProcessBuild]
    public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
    {
        if (target != BuildTarget.iOS) return;

        var plistPath = Path.Combine(pathToBuiltProject, "Info.plist");
        var plist = new PlistDocument();
        plist.ReadFromFile(plistPath);
        plist.root.SetString("NSLocalNetworkUsageDescription",
            "Connects to the game's laptop gateway on your local network to receive your classified movements.");
        plist.WriteToFile(plistPath);
    }
}
