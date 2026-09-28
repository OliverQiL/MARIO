using System.IO;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Scene/prefab wiring for the Step 6 stage loop. Idempotent.
/// - Rebuilds TestStage at test-friendly size: a 30 cm block resting on the
///   floor, with a "Goal" flag (StageGoal) 40 cm beyond it.
/// - Wires LevelGenerator.OnStageStarted/OnStageCompleted to
///   GameStateManager.BeginStage/CompleteStage (persistent listeners, same
///   pattern as ARSessionManager's tracking events).
/// - Points ScanHUD at LevelGenerator + GameStateManager.
/// - Removes the old scene-only 1 m test cube (TestInteractable); the stage
///   Block carries TestInteractable instead, so stomps have a real target.
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
        BuildTestStage();

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var levelGenerator = Object.FindAnyObjectByType<LevelGenerator>();
        var gameState = Object.FindAnyObjectByType<GameStateManager>();
        if (levelGenerator == null || gameState == null)
            throw new System.Exception("[StageLoopSetup] Scene needs a LevelGenerator and a GameStateManager.");

        AddListenerOnce(levelGenerator.OnStageStarted ??= new UnityEvent(), gameState.BeginStage);
        AddListenerOnce(levelGenerator.OnStageCompleted ??= new UnityEvent(), gameState.CompleteStage);
        EditorUtility.SetDirty(levelGenerator);

        // Scan target: only real floor counts now (no beds/tables), so the old 10 m² test value can be
        // unreachable in a small room. The design only needs more than the largest stage footprint.
        var sessionManager = Object.FindAnyObjectByType<ARSessionManager>();
        var sessionSo = new SerializedObject(sessionManager);
        sessionSo.FindProperty("requiredFloorArea").floatValue = 3f;
        sessionSo.ApplyModifiedPropertiesWithoutUndo();

        // The early EventResolver test cube sat at a fixed world position near the session origin.
        foreach (var interactable in Object.FindObjectsByType<TestInteractable>(FindObjectsInactive.Include))
        {
            if (interactable.gameObject.scene == scene)
                Object.DestroyImmediate(interactable.gameObject);
        }

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
        Debug.Log("[StageLoopSetup] Done. TestStage rebuilt (30 cm block + goal flag); stage events wired to GameStateManager.");
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

    private static void BuildTestStage()
    {
        var flagMat = EnsureMaterial(FlagMaterialPath, new Color(1f, 0.8f, 0.1f));
        var poleMat = EnsureMaterial(PoleMaterialPath, new Color(0.85f, 0.85f, 0.85f));

        var root = PrefabUtility.LoadPrefabContents(StagePrefabPath);

        // The root used to be the (1 m, half-sunk) cube itself. Make it an empty
        // pivot on the floor point; all visuals are children so they can be sized freely.
        Object.DestroyImmediate(root.GetComponent<Collider>());
        Object.DestroyImmediate(root.GetComponent<MeshRenderer>());
        Object.DestroyImmediate(root.GetComponent<MeshFilter>());
        root.transform.localScale = Vector3.one;
        for (int i = root.transform.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(root.transform.GetChild(i).gameObject);

        // Footprint covers the block and the goal area beyond it.
        var footprint = root.GetComponent<StageFootprint>();
        footprint.width = 0.6f;
        footprint.depth = 1.0f;
        footprint.clearance = 0.5f;

        // 30 cm block resting on the floor, towards the near (-Z) side. Keeps its collider for later gameplay targeting.
        var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.name = "Block";
        block.transform.SetParent(root.transform, false);
        block.transform.localPosition = new Vector3(0f, 0.15f, -0.15f);
        block.transform.localScale = Vector3.one * 0.3f;
        block.AddComponent<TestInteractable>(); // stomp target (was a separate scene cube)

        // Goal 40 cm beyond the block's far face: with a 0.35 m reach it can't be triggered from the near side.
        var goal = new GameObject("Goal");
        goal.transform.SetParent(root.transform, false);
        goal.transform.localPosition = new Vector3(0f, 0f, 0.4f);
        var stageGoal = goal.AddComponent<StageGoal>();
        var so = new SerializedObject(stageGoal);
        so.FindProperty("reachRadius").floatValue = 0.35f;
        so.ApplyModifiedPropertiesWithoutUndo();

        MakeVisual(PrimitiveType.Cylinder, "Pole", goal.transform, new Vector3(0f, 0.25f, 0f), new Vector3(0.02f, 0.25f, 0.02f), poleMat);
        MakeVisual(PrimitiveType.Cube, "Flag", goal.transform, new Vector3(0.075f, 0.44f, 0f), new Vector3(0.15f, 0.1f, 0.01f), flagMat);

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
