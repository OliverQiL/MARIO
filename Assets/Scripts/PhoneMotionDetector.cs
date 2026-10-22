using UnityEngine;

/// <summary>
/// A second way to see jumps and squats, live on the phone: with the phone
/// in the headset, its tracked height is the player's head height, so a quick
/// rise is a jump and a deep drop is a squat. It backs up the classifier
/// (still unreliable at these two): its events go through EventResolver like
/// the gateway's, with session_id <see cref="EventResolver.PhoneMotionSession"/>, and the
/// resolver treats the classifier's report of the same jump or squat (and
/// vice versa) as a repeat, so one motion counts once. Nothing is sent to the
/// gateway (no ACK); haptics work as for any event.
///
/// Baseline (standing head height): after the scan, the floor plus the
/// calibrated eye height (PlayerBody); before it, the head height while
/// standing still. Either way it follows slow tracking drift only while the
/// player stands still near it, so a held squat is never absorbed.
///
/// - Jump: the head rises more than jumpRise above the baseline, moving up
///   faster than jumpSpeed. One event; it re-arms after landing.
/// - Squat: the head drops more than squatDrop below it (deeper than looking
///   down at the feet). One event, and while the player stays down they stay
///   ducked (GameStateManager.Duck), until they rise above squatRelease.
///
/// Active on the phone in headset mode (handheld, raising the arm would look
/// like a jump) and always in the Editor (XR Simulation). Logic is in
/// Tick(dt, headY) so headless tests can drive it.
/// </summary>
public class PhoneMotionDetector : MonoBehaviour
{
    public enum Mode { Auto, Always, Off }

    [SerializeField] private Mode mode = Mode.Auto;
    [SerializeField] private Camera arCamera;
    [SerializeField] private EventResolver eventResolver;
    [SerializeField] private ARSessionManager sessionManager;

    [Header("Jump")]
    [Tooltip("Metres above the standing head height that count as a jump (walking bob is 2-4 cm; tiptoe about 5-8).")]
    [SerializeField] private float jumpRise = 0.10f;
    [Tooltip("Minimum upward speed (m/s) at that height, so slowly rising onto tiptoe isn't a jump.")]
    [SerializeField] private float jumpSpeed = 0.5f;
    [Tooltip("Seconds after a jump before the next can count (once back down).")]
    [SerializeField] private float jumpRearmSeconds = 0.6f;

    [Header("Squat")]
    [Tooltip("Metres below the standing head height that count as a squat (looking down at the feet drops it ~5-15 cm).")]
    [SerializeField] private float squatDrop = 0.28f;
    [Tooltip("The squat lasts until the head is back above this depth (hysteresis).")]
    [SerializeField] private float squatRelease = 0.20f;

    [Header("Baseline")]
    [Tooltip("Time constant (s) for following slow drift of the standing height.")]
    [SerializeField] private float baselineSeconds = 2f;
    [Tooltip("Head speed (m/s) under which the player counts as standing still.")]
    [SerializeField] private float stillSpeed = 0.15f;
    [Tooltip("Before the scan: a still, non-squat head this far from the baseline for resettleSeconds becomes the new baseline (e.g. the app started seated).")]
    [SerializeField] private float baselineBand = 0.06f;
    [SerializeField] private float resettleSeconds = 3f;
    [Tooltip("A height change bigger than this in one frame is tracking (start-up, relocalisation), not a person: start over.")]
    [SerializeField] private float teleportMetres = 0.25f;
    [Tooltip("Seconds after (re)starting before anything is detected, while the baseline settles.")]
    [SerializeField] private float warmUpSeconds = 1f;

    public float Baseline { get; private set; }
    /// <summary>Head height relative to the baseline (+ up), metres.</summary>
    public float Offset { get; private set; }
    public bool IsSquatting { get; private set; }
    public bool IsActive => mode == Mode.Always || (mode == Mode.Auto && (Application.isEditor || (_headset != null && _headset.IsOn)));

    private bool _started;
    private float _prevY, _speed, _clock, _jumpAt = float.NegativeInfinity, _offBandFor, _startedAt;
    private bool _jumpArmed = true;
    private int _nextId;
    private HeadsetView _headset;

    private void Awake()
    {
        if (arCamera == null) arCamera = Camera.main;
        if (eventResolver == null) eventResolver = FindAnyObjectByType<EventResolver>();
        if (sessionManager == null) sessionManager = FindAnyObjectByType<ARSessionManager>();
        _headset = FindAnyObjectByType<HeadsetView>();
    }

    private void Update()
    {
        if (arCamera == null || !IsActive) { _started = false; return; }
        if (sessionManager != null && !sessionManager.IsTracking) { _started = false; return; } // jumps in tracking, not glitches
        Tick(Time.deltaTime, arCamera.transform.position.y);
    }

    /// <summary>
    /// Advances with the head's height <paramref name="headY"/>; returns "jump" or "squat" when
    /// one is detected this tick (and sends it to the resolver), else null.
    /// </summary>
    public string Tick(float dt, float headY)
    {
        if (dt <= 0f) return null;
        _clock += dt;
        if (_started && Mathf.Abs(headY - _prevY) > teleportMetres)
        {
            _started = false; // tracking jumped (start-up, relocalisation): not a jump or a squat
            IsSquatting = false;
            _jumpArmed = true;
        }
        if (!_started)
        {
            _started = true;
            _startedAt = _clock;
            _prevY = headY;
            _speed = 0f;
            Baseline = CalibratedBaseline() ?? headY;
            Offset = headY - Baseline;
            return null;
        }

        _speed = Mathf.Lerp(_speed, (headY - _prevY) / dt, 0.5f); // light smoothing of tracking jitter
        _prevY = headY;
        if (CalibratedBaseline() is { } calibrated && !IsSquatting && _jumpArmed && Mathf.Abs(Baseline - calibrated) > baselineBand)
            Baseline = calibrated; // the scan just calibrated the standing eye height
        Offset = headY - Baseline;

        string detected = null;
        if (_clock - _startedAt < warmUpSeconds)
        {
            // Settling: learn where standing is, detect nothing yet.
            Baseline = Mathf.Lerp(Baseline, headY, Mathf.Clamp01(dt / 0.2f));
            return null;
        }

        // Squat: one event going down; ducked for as long as they stay down.
        if (!IsSquatting && Offset < -squatDrop)
        {
            IsSquatting = true;
            detected = "squat";
        }
        else if (IsSquatting && Offset > -squatRelease)
        {
            IsSquatting = false;
        }
        if (IsSquatting)
        {
            GameStateManager.Instance?.Duck(0.25f);
            if (eventResolver != null) eventResolver.NotePhoneMotionOngoing("squat"); // the classifier's squats meanwhile are this one
        }

        // Jump: high enough, and still going up fast.
        if (_jumpArmed && !IsSquatting && Offset > jumpRise && _speed > jumpSpeed)
        {
            _jumpArmed = false;
            _jumpAt = _clock;
            detected = "jump";
        }
        else if (!_jumpArmed && Offset < jumpRise * 0.4f && _clock - _jumpAt > jumpRearmSeconds)
        {
            _jumpArmed = true;
        }

        // Follow slow drift while standing still near the baseline.
        bool still = !IsSquatting && _jumpArmed && Mathf.Abs(_speed) < stillSpeed;
        if (still && Mathf.Abs(Offset) < baselineBand)
        {
            Baseline = Mathf.Lerp(Baseline, headY, dt / Mathf.Max(0.01f, baselineSeconds));
            _offBandFor = 0f;
        }
        else if (still && CalibratedBaseline() == null && Offset > -squatRelease)
        {
            // Before the scan only: standing still at a new height (not crouched) for a while resets the baseline.
            _offBandFor += dt;
            if (_offBandFor >= resettleSeconds) { Baseline = headY; _offBandFor = 0f; }
        }
        else _offBandFor = 0f;

        if (detected != null) Send(detected);
        return detected;
    }

    private float? CalibratedBaseline() =>
        PlayerBody.EyeHeight is { } eyes && sessionManager != null && sessionManager.FloorHeight is { } floor ? floor + eyes : null;

    private void Send(string action)
    {
        Debug.Log($"[PhoneMotionDetector] {action} (head {Offset:+0.00;-0.00} m from standing)");
        if (eventResolver != null)
            eventResolver.Resolve(new GameEvent(action, 1f, $"m-{_nextId++}", EventResolver.PhoneMotionSession));
    }
}
