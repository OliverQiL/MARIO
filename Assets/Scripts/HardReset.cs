using UnityEngine;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Hard reset without touching the phone (it's in the headset): look
/// straight up at the ceiling and hold it for holdSeconds. The game goes back
/// to the very start, as if the app had been relaunched: "jump to start
/// scanning", the scan, a jump to confirm it.
///
/// The countdown appears on the HUD (HUDController reads <see cref="ShowCountdown"/>)
/// only after showAfterSeconds, so glancing up at a ? block shows nothing;
/// looking back down at any point cancels it (with a little hysteresis, so
/// tracking jitter at the threshold doesn't).
///
/// The reset irises out and, while the view is black, puts every system back
/// as at launch: projectiles cleared, the stage torn down
/// (LevelGenerator.ResetToScan), a new game's state (GameStateManager.ResetForScan),
/// the scan back to "jump to start" (ARSessionManager.ResetScan), the
/// calibrated eye height cleared, the music stopped; on the phone the AR
/// session is reset too, so the scan starts from no planes. It resets in
/// place rather than reloading the scene: a reload also unloads XR
/// Simulation's room in the Editor, and in-place is what SimulationSmokeTest
/// -scan -reset checks. In the Editor, R does it at once (EditorActionInput).
/// </summary>
public class HardReset : MonoBehaviour
{
    [SerializeField] private Camera arCamera;
    [Tooltip("How far above the horizon (degrees) the view must point to count as looking up.")]
    [SerializeField] private float lookUpDegrees = 65f;
    [Tooltip("Looking up stops counting below this (degrees), so jitter at the threshold doesn't cancel it.")]
    [SerializeField] private float releaseDegrees = 55f;
    [SerializeField] private float holdSeconds = 5f;
    [Tooltip("The HUD countdown appears only after this long, so a glance up shows nothing.")]
    [SerializeField] private float showAfterSeconds = 1f;

    /// <summary>Seconds the player has been looking up (0 when not).</summary>
    public float Held { get; private set; }
    public bool ShowCountdown => Held >= showAfterSeconds;
    /// <summary>Whole seconds left before the reset (5, 4, ... 1).</summary>
    public int SecondsLeft => Mathf.Max(1, Mathf.CeilToInt(holdSeconds - Held));
    public float HoldSeconds => holdSeconds;

    private bool _resetting;

    private void Awake()
    {
        if (arCamera == null) arCamera = Camera.main;
    }

    private void Update()
    {
        if (arCamera == null || _resetting) return;
        if (Tick(Time.unscaledDeltaTime, arCamera.transform.forward)) Trigger();
    }

    /// <summary>Advances the gesture with the view direction <paramref name="forward"/>; true once it completes.</summary>
    public bool Tick(float dt, Vector3 forward)
    {
        float elevation = Mathf.Asin(Mathf.Clamp(forward.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        bool up = elevation >= (Held > 0f ? releaseDegrees : lookUpDegrees);
        Held = up ? Held + dt : 0f;
        if (Held < holdSeconds) return false;
        Held = 0f;
        return true;
    }

    /// <summary>Resets now: back to "jump to start scanning".</summary>
    public void Trigger()
    {
        if (_resetting) return;
        _resetting = true;
        Debug.Log("[HardReset] Hard reset: back to the scan.");
        AudioManager.Play(Sfx.Warp);
        ScreenTransition.Run(ResetEverything);
    }

    private void ResetEverything()
    {
        foreach (var f in FindObjectsByType<Fireball>()) Destroy(f.gameObject);       // not children of the stage
        foreach (var t in FindObjectsByType<ThrownShell>()) Destroy(t.gameObject);
        var level = FindAnyObjectByType<LevelGenerator>();
        if (level != null) level.ResetToScan();
        var gs = GameStateManager.Instance;
        if (gs != null) gs.ResetForScan();
        var scan = FindAnyObjectByType<ARSessionManager>();
        if (scan != null) scan.ResetScan();
        PlayerBody.Reset();
        AudioManager.StopMusic();
#if !UNITY_EDITOR
        // On the phone, forget planes and anchors so the new scan starts from nothing. Not in the
        // Editor: resetting XR Simulation's session makes its mesh subsystem throw on the simulated
        // room it rebuilds (AR Foundation, SimulationMeshSubsystem.AddMeshes).
        var session = FindAnyObjectByType<ARSession>();
        if (session != null) session.Reset();
#endif
        Held = 0f;
        _resetting = false;
    }
}
