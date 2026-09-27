using System.IO;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Scene/prefab wiring for the Step 6 stage loop. Idempotent.
/// - Adds a "Goal" flag (StageGoal) at the far edge of TestStage.
/// - Wires LevelGenerator.OnStageStarted/OnStageCompleted to
///   GameStateManager.BeginStage/CompleteStage (persistent listeners, same
///   pattern as ARSessionManager's tracking events).
/// - Points ScanHUD at LevelGenerator + GameStateManager.
///
/// Menu:     Tools ▸ MARIO ▸ Configure Stage Loop
/// Headless: Unity -batchmode -quit -projectPath . -executeMethod StageLoopSetup.Run
/// </summary>
public static class StageLoopSetup
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string StagePrefabPath = "Assets/TestStage.prefab";
    private const string FlagMaterialPath = "Assets/Materials/GoalFlag.mat";
    private const string PoleMaterialPath = "Assets/Materials/GoalPole.mat";

    [MenuItem("Tools/MARIO/Configure Stage Loop")]
    public static void Run()
    {
        AddGoalToStage();

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var levelGenerator = Object.FindAnyObjectByType<LevelGenerator>();
        var gameState = Object.FindAnyObjectByType<GameStateManager>();
        if (levelGenerator == null || gameState == null)
            throw new System.Exception("[StageLoopSetup] Scene needs a LevelGenerator and a GameStateManager.");

        AddListenerOnce(levelGenerator.OnStageStarted ??= new UnityEvent(), gameState.BeginStage);
        AddListenerOnce(levelGenerator.OnStageCompleted ??= new UnityEvent(), gameState.CompleteStage);
        EditorUtility.SetDirty(levelGenerator);

        var scanHud = Object.FindAnyObjectByType<ScanHUD>();
        if (scanHud != null)
        {
            var so = new SerializedObject(scanHud);
            so.FindProperty("levelGenerator").objectReferenceValue = levelGenerator;
            so.FindProperty("gameState").objectReferenceValue = gameState;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[StageLoopSetup] Done. TestStage has a goal flag; stage events wired to GameStateManager.");
    }

    private static void AddListenerOnce(UnityEvent evt, UnityAction action)
    {
        for (int i = 0; i < evt.GetPersistentEventCount(); i++)
        {
            if (evt.GetPersistentTarget(i) == (Object)action.Target && evt.GetPersistentMethodName(i) == action.Method.Name)
                return;
        }
        UnityEventTools.AddVoidPersistentListener(evt, action);
    }

    private static void AddGoalToStage()
    {
        var flagMat = EnsureMaterial(FlagMaterialPath, new Color(1f, 0.8f, 0.1f));
        var poleMat = EnsureMaterial(PoleMaterialPath, new Color(0.85f, 0.85f, 0.85f));

        var root = PrefabUtility.LoadPrefabContents(StagePrefabPath);
        var existing = root.transform.Find("Goal");
        if (existing != null) Object.DestroyImmediate(existing.gameObject);

        // TestStage's root is a 1 m cube centred on the floor point; the goal sits at its far (+Z) edge.
        var goal = new GameObject("Goal");
        goal.transform.SetParent(root.transform, false);
        goal.transform.localPosition = new Vector3(0f, 0f, 0.5f);
        goal.AddComponent<StageGoal>();

        MakeVisual(PrimitiveType.Cylinder, "Pole", goal.transform, new Vector3(0f, 0.9f, 0f), new Vector3(0.03f, 0.4f, 0.03f), poleMat);
        MakeVisual(PrimitiveType.Cube, "Flag", goal.transform, new Vector3(0.13f, 1.2f, 0f), new Vector3(0.25f, 0.15f, 0.01f), flagMat);

        PrefabUtility.SaveAsPrefabAsset(root, StagePrefabPath);
        PrefabUtility.UnloadPrefabContents(root);
    }

    private static void MakeVisual(PrimitiveType type, string name, Transform parent, Vector3 localPos, Vector3 localScale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>()); // purely visual: must not block placement or Gate 4
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = localScale;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
    }

    private static Material EnsureMaterial(string path, Color color)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", color);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }
}
