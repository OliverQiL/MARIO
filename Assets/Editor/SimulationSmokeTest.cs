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
/// (looking down at the floor, turning and walking a small circle) after
/// jumping to start the scan, confirms it with another jump as a player would, waits for a stage, then checks
/// that the training round comes first, plays it through (skipping each
/// step as its timeout would, then grabbing the flag), checks that the
/// campaign's 1-1 follows fresh (not a fallback), and reports
/// the size it fitted at.
///
/// With -scan -reset, once the first stage is up it looks straight up (the
/// hard-reset gesture) until the game resets, checks it's back at "jump to
/// start scanning" (no stage, scan not started, eye height cleared, stage 1),
/// then scans again and expects the training round and 1-1 again.
///
///   Unity -batchmode -projectPath . -executeMethod SimulationSmokeTest.Run [-scan [-reset]]   (no -quit)
/// </summary>
[InitializeOnLoad]
public static class SimulationSmokeTest
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string ActiveKey = "SimulationSmokeTest.Active";
    private const string ErrorsKey = "SimulationSmokeTest.Errors";
    private const string ScanKey = "SimulationSmokeTest.Scan";
    private const string HeadsetKey = "SimulationSmokeTest.Headset";
    private const string ResetKey = "SimulationSmokeTest.Reset";
    private const string ResetResultKey = "SimulationSmokeTest.ResetResult";
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
        SessionState.SetBool(HeadsetKey, System.Environment.GetCommandLineArgs().Contains("-headset"));
        SessionState.SetBool(ResetKey, System.Environment.GetCommandLineArgs().Contains("-reset"));
        SessionState.SetString(ResetResultKey, "");
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
    private static double s_ScanStartedAt = double.MaxValue;
    // -reset: 0 = first round, 1 = looking up until the scene reloads, 2 = second round after the reset.
    private static int s_ResetPhase;
    private static double s_RoundStartedAt;
    private static int s_LastDiag = -1;
    private static bool s_TrainingSeen;

    private static void Tick()
    {
        if (!EditorApplication.isPlaying) return;
        if (s_PlayStartedAt < 0) s_PlayStartedAt = EditorApplication.timeSinceStartup;
        double elapsed = EditorApplication.timeSinceStartup - s_PlayStartedAt;
        bool scan = SessionState.GetBool(ScanKey, false);
        var headset = Object.FindAnyObjectByType<HeadsetView>();
        if (SessionState.GetBool(HeadsetKey, false) && headset != null && !headset.IsOn) headset.TurnOn();
        var level = Object.FindAnyObjectByType<LevelGenerator>();

        if (scan)
        {
            bool placed = level != null && level.CurrentStage != null;
            double round = elapsed - s_RoundStartedAt;

            // -reset: after the first stage, look straight up (the hard-reset gesture) until the scene reloads.
            if (placed && s_ResetPhase == 0 && SessionState.GetBool(ResetKey, false))
            {
                s_ResetPhase = 1;
                s_RoundStartedAt = elapsed;
                Debug.Log("[SimulationSmokeTest] Stage up: looking up to hard-reset.");
                round = 0;
            }
            if (s_ResetPhase == 1)
            {
                var scanNow = Object.FindAnyObjectByType<ARSessionManager>();
                if (scanNow != null && !scanNow.IsScanStarted)
                {
                    var fresh = Object.FindAnyObjectByType<ARSessionManager>();
                    bool back = fresh != null && !fresh.IsScanStarted && !fresh.IsScanComplete && level.CurrentStage == null
                                && PlayerBody.EyeHeight == null && GameStateManager.Instance != null && GameStateManager.Instance.StageIndex == 1;
                    SessionState.SetString(ResetResultKey, back ? "ok" : "not back at the scan");
                    Debug.Log($"[SimulationSmokeTest] Hard reset after {round:F1} s looking up; back at 'jump to start scanning': {back}.");
                    s_ResetPhase = 2;
                    s_RoundStartedAt = elapsed;
                    s_ScanStartedAt = double.MaxValue;
                    s_StartPose = null;
                    return;
                }
                if (round > 15) { SessionState.SetString(ResetResultKey, "no reset after 15 s looking up"); }
                else
                {
                    LookUp();
                    if (Mathf.FloorToInt((float)round) != s_LastDiag)
                    {
                        s_LastDiag = Mathf.FloorToInt((float)round);
                        var hr = Object.FindAnyObjectByType<HardReset>();
                        var cam = Camera.main;
                        var provider = Object.FindAnyObjectByType<UnityEngine.XR.Simulation.SimulationCameraPoseProvider>();
                        Debug.Log($"[SimulationSmokeTest] reset diag t={round:F1}: main camera '{cam?.name}' elevation {(cam != null ? Mathf.Asin(cam.transform.forward.y) * Mathf.Rad2Deg : float.NaN):F0}°, " +
                                  $"provider euler {provider?.transform.rotation.eulerAngles}, HardReset {(hr == null ? "MISSING" : $"enabled {hr.isActiveAndEnabled}, held {hr.Held:F1}")}");
                    }
                    return;
                }
            }

            // The training round: skip each step (as its 40 s timeout would), grab the flag, then wait for 1-1.
            if (placed && level.InTraining && (!SessionState.GetBool(ResetKey, false) || s_ResetPhase == 2))
            {
                s_TrainingSeen = true;
                var tutorial = Tutorial.Active;
                if (tutorial != null && tutorial.StepIndex >= 0 && tutorial.StepIndex < tutorial.StepCount - 1) tutorial.Tick(41f);
                else if (tutorial != null && tutorial.StepIndex == tutorial.StepCount - 1 && level.CurrentGoal != null && !level.CurrentGoal.IsReached)
                {
                    Debug.Log("[SimulationSmokeTest] Training: every step shown; grabbing the flag.");
                    level.CurrentGoal.Reach();
                }
                if (round < ScanTimeoutSeconds) return;
            }
            if (s_TrainingSeen && !placed && round < ScanTimeoutSeconds) return; // the iris wipe into 1-1

            if (!placed && round < ScanTimeoutSeconds && SessionState.GetString(ResetResultKey, "") is "" or "ok")
            {
                // As a player would: jump to start scanning, look around, jump again to finish.
                var scanSession = Object.FindAnyObjectByType<ARSessionManager>();
                var resolver = Object.FindAnyObjectByType<EventResolver>();
                if (scanSession != null && resolver != null && !scanSession.IsScanStarted && round > 2)
                {
                    resolver.Resolve(new GameEvent("jump", 0.95f, $"scan-start-{s_ResetPhase}", "smoketest"));
                    s_ScanStartedAt = elapsed;
                }
                if (scanSession != null && scanSession.IsScanStarted) SweepCamera((float)(elapsed - s_ScanStartedAt));
                if (scanSession != null && resolver != null && !scanSession.IsScanComplete && elapsed - s_ScanStartedAt > ConfirmAfterSeconds && scanSession.CanConfirmScan)
                    resolver.Resolve(new GameEvent("jump", 0.95f, $"scan-confirm-{s_ResetPhase}-{elapsed:F0}", "smoketest"));
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
            // After the scan comes the training round (Tutorial), then a fresh 1-1.
            var gs = GameStateManager.Instance;
            bool handedOver = s_TrainingSeen && stage != null && stage.name.StartsWith("Stage 1-1") && gs != null && !gs.InTraining && gs.StageIndex == 1;
            Debug.Log($"[SimulationSmokeTest] Training round played first: {s_TrainingSeen}; then {(stage != null ? stage.name : "NONE")}, score {gs?.Score}, lives {gs?.Lives}.");
            ok &= handedOver;

            // Placement: facing away from the player (they're behind its start edge, on its axis), the goal on the far side.
            if (stage != null && footprint != null && level != null)
            {
                var player = stage.transform.InverseTransformPoint(Camera.main.transform.position);
                var goal = stage.GetComponentInChildren<StageGoal>(true);
                float goalZ = goal != null ? stage.transform.InverseTransformPoint(goal.transform.position).z : float.NaN;
                bool facingAway = player.z < -footprint.depth * 0.5f && Mathf.Abs(player.x) < 0.3f && goalZ > 0f;
                Debug.Log($"[SimulationSmokeTest] Placement: player at local ({player.x:F2}, {player.z:F2}) of a {footprint.width:F2} x {footprint.depth:F2} m stage, goal at z {goalZ:F2}; " +
                          $"{level.LastPlacementMargin:F1} m clear all round; facing away with the goal on the far side: {facingAway}.");
                ok &= facingAway;
            }
            if (SessionState.GetBool(ResetKey, false))
            {
                string reset = SessionState.GetString(ResetResultKey, "");
                Debug.Log($"[SimulationSmokeTest] Hard reset: {(reset == "" ? "never triggered" : reset)}; rescanned to {(stage != null ? stage.name : "NONE")}.");
                ok &= reset == "ok" && s_ResetPhase == 2;
            }
        }
        if (headset != null && headset.IsOn) SaveHeadsetFrame(headset);
        if (ok) Debug.Log("[SimulationSmokeTest] PASS");
        else Debug.LogError($"[SimulationSmokeTest] FAIL{(string.IsNullOrEmpty(errors) ? "" : " — errors:" + errors)}");

        EditorApplication.Exit(ok ? 0 : 1);
    }

    /// <summary>Saves what the phone's screen shows in headset mode (both eyes, through the lens shader) to Logs/headset_view.png.</summary>
    private static void SaveHeadsetFrame(HeadsetView headset)
    {
        var view = (RenderTexture)typeof(HeadsetView).GetField("_view", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(headset);
        var eyes = Object.FindObjectsByType<UnityEngine.UI.RawImage>().Where(r => r.name.EndsWith("Eye")).OrderBy(r => r.name == "RightEye").ToArray();
        if (view == null || eyes.Length != 2) { Debug.LogError("[SimulationSmokeTest] Headset view not found."); return; }
        int w = view.width, h = view.height;
        var output = new Texture2D(w, h, TextureFormat.RGB24, false);
        output.SetPixels32(new Color32[w * h]); // black, like the screen around the eye images
        foreach (var eye in eyes)
        {
            var rect = (RectTransform)eye.transform;
            int ew = Mathf.RoundToInt(rect.sizeDelta.x), eh = Mathf.RoundToInt(rect.sizeDelta.y);
            var eyeRT = RenderTexture.GetTemporary(ew, eh);
            Graphics.Blit(view, eyeRT, eye.material);
            RenderTexture.active = eyeRT;
            int x = Mathf.RoundToInt(rect.anchorMin.x * w - ew / 2f), y = Mathf.RoundToInt(h / 2f - eh / 2f);
            output.ReadPixels(new Rect(0, 0, ew, eh), x, y);
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(eyeRT);
        }
        output.Apply();
        System.IO.File.WriteAllBytes("Logs/headset_view.png", output.EncodeToPNG());
        Debug.Log($"[SimulationSmokeTest] Headset view ({w}x{h}) saved to Logs/headset_view.png");
    }

    /// <summary>Looks straight up at the ceiling from where the camera is (the hard-reset gesture).</summary>
    private static void LookUp()
    {
        var provider = Object.FindAnyObjectByType<UnityEngine.XR.Simulation.SimulationCameraPoseProvider>();
        if (provider == null) return;
        var pose = new Pose(provider.transform.position, Quaternion.Euler(-80f, provider.transform.rotation.eulerAngles.y, 0f));
        typeof(UnityEngine.XR.Simulation.SimulationCameraPoseProvider)
            .GetMethod("UpdatePose", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(provider, new object[] { pose });
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
