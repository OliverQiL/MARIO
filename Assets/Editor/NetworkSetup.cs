using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Wires NetworkClient into the scene (on GameManager, next to EventResolver)
/// and removes the DebugActionInput keyboard harness it replaces.
/// Idempotent.
///
/// Menu:     Tools ▸ MARIO ▸ Configure Network   (keeps the current URL/pin)
/// Headless: Unity -batchmode -quit -projectPath . -executeMethod NetworkSetup.Run
///                 [-gatewayUrl wss://host:port/] [-pin SHA256]
/// </summary>
public static class NetworkSetup
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";

    [MenuItem("Tools/MARIO/Configure Network")]
    public static void Run()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var resolver = UnityEngine.Object.FindAnyObjectByType<EventResolver>();
        if (resolver == null) throw new Exception("[NetworkSetup] No EventResolver in scene.");
        var go = resolver.gameObject;

        // Remove the keyboard harness, including a "missing script" left behind if its file is already gone.
        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
        var debugInput = go.GetComponent("DebugActionInput");
        if (debugInput != null) UnityEngine.Object.DestroyImmediate(debugInput, true);

        var client = go.GetComponent<NetworkClient>(); // not ??: Unity overloads == for destroyed/missing objects
        if (client == null) client = go.AddComponent<NetworkClient>();
        var so = new SerializedObject(client);
        so.FindProperty("eventResolver").objectReferenceValue = resolver;
        var url = Arg("-gatewayUrl");
        if (url != null) so.FindProperty("gatewayUrl").stringValue = url;
        var pin = Arg("-pin");
        if (pin != null) so.FindProperty("pinnedCertSha256").stringValue = pin;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[NetworkSetup] Done. NetworkClient on '{go.name}' → {so.FindProperty("gatewayUrl").stringValue}, pin '{so.FindProperty("pinnedCertSha256").stringValue}'.");
    }

    private static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
