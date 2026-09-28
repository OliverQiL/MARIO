using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Scene wiring for the Step 6 stage loop. Idempotent.
/// Wires LevelGenerator.OnStageStarted/OnStageCompleted to
/// GameStateManager.BeginStage/CompleteStage (persistent listeners, same
/// pattern as ARSessionManager's tracking events). Stage content itself is
/// built by GameContentSetup.
///
/// Menu:     Tools ▸ MARIO ▸ Configure Stage Loop
/// Headless: Unity -batchmode -quit -projectPath . -executeMethod StageLoopSetup.Run
/// </summary>
public static class StageLoopSetup
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";

    [MenuItem("Tools/MARIO/Configure Stage Loop")]
    public static void Run()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var levelGenerator = Object.FindAnyObjectByType<LevelGenerator>();
        var gameState = Object.FindAnyObjectByType<GameStateManager>();
        if (levelGenerator == null || gameState == null)
            throw new System.Exception("[StageLoopSetup] Scene needs a LevelGenerator and a GameStateManager.");

        AddListenerOnce(levelGenerator.OnStageStarted ??= new UnityEvent(), gameState.BeginStage);
        AddListenerOnce(levelGenerator.OnStageCompleted ??= new UnityEvent(), gameState.CompleteStage);
        EditorUtility.SetDirty(levelGenerator);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[StageLoopSetup] Done. Stage events wired to GameStateManager.");
    }

    public static void AddListenerOnce(UnityEvent evt, UnityAction action)
    {
        for (int i = 0; i < evt.GetPersistentEventCount(); i++)
        {
            if (evt.GetPersistentTarget(i) == (Object)action.Target && evt.GetPersistentMethodName(i) == action.Method.Name)
                return;
        }
        UnityEventTools.AddVoidPersistentListener(evt, action);
    }
}
