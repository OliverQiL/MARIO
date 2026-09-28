using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Headless check that the game boots under XR Simulation in Play Mode: the
/// simulated room loads, the AR session reaches tracking, and no
/// errors/exceptions are logged. Plane counts are reported for information
/// only: XR Simulation rescans only when the camera moves (≥2.5 cm / ≥4°,
/// XRSimulationRuntimeSettings), and nothing moves it here — so this verifies
/// the setup, not scanning or gameplay.
/// Exits 0 on success, 1 otherwise.
///
///   Unity -batchmode -projectPath . -executeMethod SimulationSmokeTest.Run   (no -quit)
/// </summary>
[InitializeOnLoad]
public static class SimulationSmokeTest
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string ActiveKey = "SimulationSmokeTest.Active";
    private const string ErrorsKey = "SimulationSmokeTest.Errors";
    private const float RunSeconds = 30f;

    private static double s_PlayStartedAt = -1;

    // Entering Play Mode may or may not reload the domain (this project has
    // Enter Play Mode Options on, so it doesn't), so state lives in SessionState,
    // hooks attach in Run, and this static constructor re-attaches after a reload.
    static SimulationSmokeTest()
    {
        if (SessionState.GetBool(ActiveKey, false)) Attach();
    }

    public static void Run()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        SessionState.SetBool(ActiveKey, true);
        SessionState.SetString(ErrorsKey, "");
        Attach();
        EditorApplication.EnterPlaymode();
    }

    private static void Attach()
    {
        Application.logMessageReceived -= RecordErrors;
        Application.logMessageReceived += RecordErrors;
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
    }

    private static void RecordErrors(string condition, string stackTrace, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        SessionState.SetString(ErrorsKey, SessionState.GetString(ErrorsKey, "") + $"\n  {type}: {condition}");
    }

    private static void Tick()
    {
        if (!EditorApplication.isPlaying) return;
        if (s_PlayStartedAt < 0) s_PlayStartedAt = EditorApplication.timeSinceStartup;
        if (EditorApplication.timeSinceStartup - s_PlayStartedAt < RunSeconds) return;

        EditorApplication.update -= Tick;
        Application.logMessageReceived -= RecordErrors;
        SessionState.SetBool(ActiveKey, false);

        var session = Object.FindAnyObjectByType<ARSessionManager>();
        var planes = Object.FindObjectsByType<ARPlane>();
        var environment = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include)
            .FirstOrDefault(t => t.name.StartsWith("LivingRoom"));
        string errors = SessionState.GetString(ErrorsKey, "");

        Debug.Log($"[SimulationSmokeTest] ARSession state {ARSession.state}; simulated room {(environment != null ? environment.name : "NOT FOUND")}; " +
                  $"{planes.Length} planes ({planes.Count(p => p.alignment == UnityEngine.XR.ARSubsystems.PlaneAlignment.HorizontalUp)} horizontal-up); " +
                  $"floor scanned {session?.TotalScannedArea:F2} / {session?.RequiredFloorArea:F1} m²; floor level {session?.FloorHeight?.ToString("F2") ?? "none"}");

        bool ok = string.IsNullOrEmpty(errors) && environment != null && ARSession.state == ARSessionState.SessionTracking;
        if (ok) Debug.Log("[SimulationSmokeTest] PASS");
        else Debug.LogError($"[SimulationSmokeTest] FAIL{(string.IsNullOrEmpty(errors) ? "" : " — errors:" + errors)}");

        EditorApplication.Exit(ok ? 0 : 1);
    }
}
