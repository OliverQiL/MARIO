using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Builds the Xcode project into ios-build/ so it can be compiled and
/// deployed from the terminal (xcodebuild + devicectl).
///
/// Menu:     Tools ▸ MARIO ▸ Build iOS
/// Headless: Unity -batchmode -quit -projectPath . -buildTarget iOS -executeMethod BuildScript.BuildIOS
/// </summary>
public static class BuildScript
{
    private const string OutputPath = "ios-build";

    [MenuItem("Tools/MARIO/Build iOS")]
    public static void BuildIOS()
    {
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = OutputPath,
            target = BuildTarget.iOS,
            targetGroup = BuildTargetGroup.iOS,
            options = BuildOptions.Development,
        };

        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;
        Debug.Log($"[BuildScript] iOS build {summary.result}: {summary.totalErrors} errors, {summary.totalTime.TotalSeconds:F0}s, output '{OutputPath}'.");

        if (Application.isBatchMode && summary.result != BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }
}
