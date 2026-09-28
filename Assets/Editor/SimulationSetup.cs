using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Sets up in-Editor testing with AR Foundation's XR Simulation (already the
/// Editor/Standalone XR loader): imports Unity's sample simulation
/// environments, selects one as the simulated room, and adds the keyboard
/// action input (EditorActionInput) to the scene. Idempotent.
///
/// The environments come from Unity's official content package:
/// tools/fetch_sim_environments.sh downloads it into ContentPackages/ and
/// imports it headlessly (Unity's -importPackage); the assets land in
/// Assets/UnityXRContent. Both are gitignored (~40 MB) — each machine runs
/// the script once. From the menu, a missing import is started interactively.
///
/// Menu:     Tools ▸ MARIO ▸ Set Up Editor Simulation
/// Headless: Unity -batchmode -quit -projectPath . -executeMethod SimulationSetup.Run [-environment NAME]
/// </summary>
public static class SimulationSetup
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string PreferencesPath = "Assets/XR/UserSimulationSettings/Resources/XRSimulationPreferences.asset";
    private const string ContentPackage = "ContentPackages/com.unity.xr-content.xr-sim-environments-2.1.1.tgz";
    // 6 × 5.6 m with furniture and open floor: fits StandardStage and exercises the placement checks.
    private const string DefaultEnvironment = "LivingRoom_20ftx18.5ft";

    [MenuItem("Tools/MARIO/Set Up Editor Simulation")]
    public static void Run()
    {
        try { ImportEnvironments(); }
        catch (OperationCanceledException e) { Debug.Log(e.Message); return; }
        SelectEnvironment(Arg("-environment") ?? DefaultEnvironment);
        AddKeyboardInput();
    }

    [MenuItem("Tools/MARIO/List Simulation Environments")]
    public static void ListEnvironments()
    {
        foreach (var path in EnvironmentPrefabPaths()) Debug.Log($"[SimulationSetup] Environment: {Path.GetFileNameWithoutExtension(path)}  ({path})");
    }

    private static void ImportEnvironments()
    {
        if (EnvironmentPrefabPaths().Any()) return; // already imported

        if (Application.isBatchMode || !File.Exists(ContentPackage))
            throw new Exception("[SimulationSetup] Simulation environments not imported — run tools/fetch_sim_environments.sh (Unity closed) first.");

        // The content package wraps a .unitypackage; extract and import it directly
        // (no need to add the content package itself as a project dependency).
        var temp = Path.Combine(Path.GetTempPath(), "mario-simenv");
        Directory.CreateDirectory(temp);
        var tar = System.Diagnostics.Process.Start("tar", $"-xzf \"{Path.GetFullPath(ContentPackage)}\" -C \"{temp}\" \"package/UnityPackage~/XRSimEnvironments.unitypackage\"");
        tar.WaitForExit();
        var unityPackage = Path.Combine(temp, "package/UnityPackage~/XRSimEnvironments.unitypackage");
        // Interactive import is asynchronous: select the room once it finishes.
        AssetDatabase.importPackageCompleted += _ => { SelectEnvironment(DefaultEnvironment); AddKeyboardInput(); };
        AssetDatabase.ImportPackage(unityPackage, false);
        throw new OperationCanceledException("[SimulationSetup] Importing environments — setup continues when the import finishes.");
    }

    private static void SelectEnvironment(string name)
    {
        var path = EnvironmentPrefabPaths().FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase));
        if (path == null)
            throw new Exception($"[SimulationSetup] No simulation environment named '{name}'. Use Tools ▸ MARIO ▸ List Simulation Environments.");

        var prefs = AssetDatabase.LoadMainAssetAtPath(PreferencesPath);
        var so = new SerializedObject(prefs);
        so.FindProperty("m_EnvironmentPrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        Debug.Log($"[SimulationSetup] Simulated room: {name} ({path}).");
    }

    private static void AddKeyboardInput()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var resolver = UnityEngine.Object.FindAnyObjectByType<EventResolver>();
        var input = resolver.GetComponent<EditorActionInput>();
        if (input == null) input = resolver.gameObject.AddComponent<EditorActionInput>();
        var so = new SerializedObject(input);
        so.FindProperty("eventResolver").objectReferenceValue = resolver;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[SimulationSetup] EditorActionInput on GameManager (keys 1-6, Editor only).");
    }

    /// <summary>Prefabs carrying a SimulationEnvironment component, i.e. selectable simulated rooms.</summary>
    private static System.Collections.Generic.IEnumerable<string> EnvironmentPrefabPaths() =>
        AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => p.Contains("SimulationEnvironments") || p.Contains("UnityXRContent"))
            .Where(p => AssetDatabase.LoadAssetAtPath<GameObject>(p)?.GetComponent("SimulationEnvironment") != null);

    private static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
