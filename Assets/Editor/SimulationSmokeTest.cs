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
/// With -scan it also plays the opening: it steers the simulated camera
/// (looking down at the floor, turning and walking a small circle), confirms
/// the scan with a jump as a player would, waits for a stage, then checks
/// that the campaign's first stage was placed (not a fallback) and reports
/// the size it fitted at.
///
///   Unity -batchmode -projectPath . -executeMethod SimulationSmokeTest.Run [-scan]   (no -quit)
/// </summary>
[InitializeOnLoad]
public static class SimulationSmokeTest
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string ActiveKey = "SimulationSmokeTest.Active";
    private const string ErrorsKey = "SimulationSmokeTest.Errors";
    private const string ScanKey = "SimulationSmokeTest.Scan";
    private const float RunSeconds = 30f;
    private const float ScanTimeoutSeconds = 150f;
    private const float ConfirmAfterSeconds = 25f; // two full turns of the sweep

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
        SessionState.SetBool(ScanKey, System.Environment.GetCommandLineArgs().Contains("-scan"));
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

    private static Pose? s_StartPose;

    private static void Tick()
    {
        if (!EditorApplication.isPlaying) return;
        if (s_PlayStartedAt < 0) s_PlayStartedAt = EditorApplication.timeSinceStartup;
        double elapsed = EditorApplication.timeSinceStartup - s_PlayStartedAt;
        bool scan = SessionState.GetBool(ScanKey, false);
        var level = Object.FindAnyObjectByType<LevelGenerator>();

        if (scan)
        {
            bool placed = level != null && level.CurrentStage != null;
            if (!placed && elapsed < ScanTimeoutSeconds)
            {
                SweepCamera((float)elapsed);
                // The player confirms the scan with a jump once they've looked around: do the same after a full sweep.
                var scanSession = Object.FindAnyObjectByType<ARSessionManager>();
                if (scanSession != null && !scanSession.IsScanComplete && elapsed > ConfirmAfterSeconds && scanSession.CanConfirmScan)
                    Object.FindAnyObjectByType<EventResolver>()?.Resolve(new GameEvent("jump", 0.95f, $"scan-confirm-{elapsed:F0}", "smoketest"));
                return;
            }
        }
        else if (elapsed < RunSeconds) return;

        EditorApplication.update -= Tick;
        Application.logMessageReceived -= RecordErrors;
        SessionState.SetBool(ActiveKey, false);

        var session = Object.FindAnyObjectByType<ARSessionManager>();
        var planes = Object.FindObjectsByType<ARPlane>();
        var environment = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include)
            .FirstOrDefault(m => m.GetType().Name == "SimulationEnvironment"); // internal to AR Foundation
        string errors = SessionState.GetString(ErrorsKey, "");

        Debug.Log($"[SimulationSmokeTest] ARSession state {ARSession.state}; simulated room {(environment != null ? environment.name : "NOT FOUND")}; " +
                  $"{planes.Length} planes ({planes.Count(p => p.alignment == UnityEngine.XR.ARSubsystems.PlaneAlignment.HorizontalUp)} horizontal-up); " +
                  $"floor scanned {session?.TotalScannedArea:F2} m²; floor level {session?.FloorHeight?.ToString("F2") ?? "none"}");

        bool ok = string.IsNullOrEmpty(errors) && environment != null && ARSession.state == ARSessionState.SessionTracking;
        if (scan)
        {
            var stage = level != null ? level.CurrentStage : null;
            var footprint = stage != null ? stage.GetComponent<StageFootprint>() : null;
            Debug.Log($"[SimulationSmokeTest] Scan: complete {session?.IsScanComplete}; stage {(stage != null ? stage.name : "NONE")}" +
                      (footprint != null ? $" ({footprint.width:F2} x {footprint.depth:F2} m)" : "") + $" after {elapsed:F0} s");
            var origin = Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
            Debug.Log($"[SimulationSmokeTest] Heights: camera y {Camera.main.transform.position.y:F2}, floor {session?.FloorHeight?.ToString("F2") ?? "none"}, " +
                      $"stage root y {(stage != null ? stage.transform.position.y.ToString("F2") : "-")}, origin mode {origin?.CurrentTrackingOriginMode}, " +
                      $"camera offset y {origin?.CameraFloorOffsetObject?.transform.localPosition.y:F2}; " +
                      $"? block {(stage != null && stage.GetComponentInChildren<QuestionBlock>() is { } qb ? (qb.transform.position.y - stage.transform.position.y).ToString("F2") : "-")} m above the stage floor");
            ok &= stage != null && stage.name.StartsWith("Stage 1-1");
        }
        if (ok) Debug.Log("[SimulationSmokeTest] PASS");
        else Debug.LogError($"[SimulationSmokeTest] FAIL{(string.IsNullOrEmpty(errors) ? "" : " — errors:" + errors)}");

        EditorApplication.Exit(ok ? 0 : 1);
    }

    /// <summary>Looks down at the floor and turns, walking a slow circle round the start, so XR Simulation discovers the floor.</summary>
    private static void SweepCamera(float t)
    {
        var provider = Object.FindAnyObjectByType<UnityEngine.XR.Simulation.SimulationCameraPoseProvider>();
        if (provider == null) return;
        if (s_StartPose == null) s_StartPose = new Pose(provider.transform.position, provider.transform.rotation);
        var start = s_StartPose.Value;

        float yaw = start.rotation.eulerAngles.y + t * 30f;           // a full turn every 12 s
        float pitch = 35f + 10f * Mathf.Sin(t * 0.7f);               // looking down at the floor
        float r = Mathf.Min(1.2f, t * 0.05f);                        // spiral out to a 1.2 m circle
        var offset = new Vector3(Mathf.Sin(t * 0.25f), 0f, Mathf.Cos(t * 0.25f)) * r;
        var pose = new Pose(start.position + offset, Quaternion.Euler(pitch, yaw, 0f));
        typeof(UnityEngine.XR.Simulation.SimulationCameraPoseProvider)
            .GetMethod("UpdatePose", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(provider, new object[] { pose });
    }
}
