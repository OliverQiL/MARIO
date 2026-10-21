using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Session lifecycle, tracking-state monitoring, scan-phase orchestration,
/// and relocalisation handling (design report Step 1 and Step 4's second
/// half).
///
/// Scan-phase orchestration: tracks total scanned floor area from AR
/// Foundation's plane detection. The player controls the scan with jumps
/// (touch-free, like restarting after game over): one jump starts it, once
/// they're set up and positioned (plane detection is off until then), and
/// after walking round scanning all the floor they want to play on, another
/// jump completes it, once at least minFloorArea has been found. fullSizeFloorArea is
/// only guidance for the HUD (roughly what a full-size stage likes).
/// Only real floor counts — see IsFloorPlane:
/// ARKit's semantic classification is trusted when present (Floor counts;
/// Table/Seat/etc. never do), and still-unclassified upward-facing planes
/// count only if they sit at the floor height (the lowest sizeable
/// horizontal plane). LevelGenerator uses the same test for placement.
///
/// Tracking-state monitoring: polls ARSession.notTrackingReason each frame.
/// A transition away from None means tracking was lost — OnTrackingLost
/// fires so gameplay can be suspended (this is what should eventually drive
/// GameStateManager.IsSessionReady, closing the loop with EventResolver's
/// Gate 1). A transition back to None is relocalisation succeeding.
/// </summary>
[RequireComponent(typeof(ARPlaneManager))]
public class ARSessionManager : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Auto-filled from this GameObject if left empty.")]
    [SerializeField] private ARPlaneManager planeManager;

    [Header("Scan Settings")]
    [Tooltip("Guidance only (the HUD's meter): about how much floor a full-size stage likes. The player ends the scan themselves.")]
    [FormerlySerializedAs("requiredFloorArea")]
    [SerializeField] private float fullSizeFloorArea = 12f;
    [Tooltip("The scan can only be confirmed once at least this much floor (m²) has been found (enough for the smallest stage).")]
    [SerializeField] private float minFloorArea = 3.0f;
    [Tooltip("Confirming the scan: a jump with at least this confidence.")]
    [SerializeField] private EventResolver eventResolver;
    [SerializeField] private float confirmMinConfidence = 0.6f;
    [Tooltip("The camera used to measure the player's eye height while they scan (defaults to Camera.main).")]
    [SerializeField] private Camera playerCamera;

    [Header("Floor Detection")]
    [Tooltip("Unclassified planes count as floor only within this height (metres) of the detected floor level.")]
    [SerializeField] private float floorHeightTolerance = 0.15f;
    [Tooltip("Minimum area (m²) for an unclassified plane to define the floor level, so a small spurious low plane can't.")]
    [SerializeField] private float minFloorReferenceArea = 0.25f;

    [Header("Events")]
    [Tooltip("Fired once, the first time scanned floor area reaches the required amount.")]
    public UnityEvent OnScanComplete;
    [Tooltip("Fired when tracking transitions from good to lost (any NotTrackingReason). Wire this to suspend gameplay.")]
    public UnityEvent OnTrackingLost;
    [Tooltip("Fired when tracking transitions back to good after being lost.")]
    public UnityEvent OnTrackingRestored;

    [Header("Debug")]
    [Tooltip("Minimum change in scanned area (m²) before logging progress again, so every minor plane refinement doesn't spam the console.")]
    [SerializeField] private float logAreaIncrement = 0.2f;

    private readonly Dictionary<TrackableId, ARPlane> _horizontalPlanes = new();
    private float _totalScannedArea;
    private float _excludedArea;
    private float _lastLoggedArea;
    private bool _scanStarted;
    private bool _scanComplete;

    public float TotalScannedArea => _totalScannedArea;
    /// <summary>Guidance for the HUD: roughly the floor a full-size stage likes.</summary>
    public float FullSizeFloorArea => fullSizeFloorArea;
    /// <summary>Enough floor found for the player to confirm the scan.</summary>
    public bool CanConfirmScan => _scanStarted && !_scanComplete && _totalScannedArea >= minFloorArea;
    /// <summary>The small-room fallback's minimum: at least this much floor finishes the scan once it stops growing.</summary>
    public float MinFloorArea => minFloorArea;
    public bool IsScanComplete => _scanComplete;
    /// <summary>The player has jumped to start scanning (plane detection is on).</summary>
    public bool IsScanStarted => _scanStarted;
    /// <summary>World-space height of the floor, once a floor plane has been found.</summary>
    public float? FloorHeight { get; private set; }
    /// <summary>Upward-facing area rejected as not-floor (beds, tables, seats…), for diagnostics.</summary>
    public float ExcludedArea => _excludedArea;
    /// <summary>Whether this device labels planes semantically (Floor, Table, Seat…).</summary>
    public bool SupportsClassification => planeManager.descriptor?.supportsClassification ?? false;

    public bool IsTracking { get; private set; } = true;
    public NotTrackingReason LastNotTrackingReason { get; private set; } = NotTrackingReason.None;

    // Camera heights above the floor sampled while scanning; their median calibrates PlayerBody.
    private readonly List<float> _eyeSamples = new();
    private float _nextEyeSampleAt;

    private void Awake()
    {
        PlayerBody.Reset(); // statics survive between Play sessions (no domain reload)
        // Unity on iOS runs at 30 fps unless told otherwise; the camera image arrives at 60.
        // 30 fps lag behind head movement is a known cause of motion sickness in a headset.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
        if (planeManager == null)
        {
            planeManager = GetComponent<ARPlaneManager>();
        }
    }

    private void Start()
    {
        // No scanning until the player jumps to start (they may still be putting the headset on).
        if (!_scanStarted) planeManager.enabled = false;
    }

    private void OnEnable()
    {
        planeManager.trackablesChanged.AddListener(OnTrackablesChanged);
        if (eventResolver == null) eventResolver = FindAnyObjectByType<EventResolver>();
        if (eventResolver != null) eventResolver.OnEventProcessed += HandleEventProcessed;
    }

    private void OnDisable()
    {
        planeManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
        if (eventResolver != null) eventResolver.OnEventProcessed -= HandleEventProcessed;
    }

    private void Update()
    {
        PollTrackingState();
        SampleEyeHeight();
    }

    /// <summary>While scanning (and tracking, with the floor known), samples the camera's height above the floor 4× a second.</summary>
    private void SampleEyeHeight()
    {
        if (!_scanStarted || _scanComplete || !IsTracking || FloorHeight == null || Time.time < _nextEyeSampleAt) return;
        if (playerCamera == null) playerCamera = Camera.main;
        if (playerCamera == null) return;
        _nextEyeSampleAt = Time.time + 0.25f;
        _eyeSamples.Add(playerCamera.transform.position.y - FloorHeight.Value);
    }

    /// <summary>The median sampled height becomes the player's eye height (robust to crouching or lowering the phone now and then).</summary>
    private void CalibrateEyeHeight()
    {
        if (_eyeSamples.Count == 0) return;
        _eyeSamples.Sort();
        float median = _eyeSamples[_eyeSamples.Count / 2];
        PlayerBody.Calibrate(median);
        Debug.Log($"[ARSessionManager] Player eye height {PlayerBody.EyeHeight:F2} m (median of {_eyeSamples.Count} samples, raw {median:F2} m).");
    }

    /// <summary>
    /// Ends the scan: the player has scanned all the floor they want to play on.
    /// Only once at least minFloorArea has been found. Returns whether it completed.
    /// </summary>
    public bool ConfirmScan()
    {
        if (!CanConfirmScan) return false;
        _scanComplete = true;
        CalibrateEyeHeight();
        Debug.Log($"[ARSessionManager] Scan confirmed by the player — {_totalScannedArea:F2} m² of floor ({_excludedArea:F2} m² of non-floor ignored).");
        OnScanComplete?.Invoke();
        return true;
    }

    /// <summary>Before and during the scan every action is dropped at Gate 1; a jump starts the scan, and a later one confirms it.</summary>
    private void HandleEventProcessed(GameEvent evt, ResolutionResult result)
    {
        if (_scanComplete || evt.actionLabel != "jump" || evt.confidence < confirmMinConfidence || result.IsRepeat) return; // one jump, one step
        if (!_scanStarted) StartScan();
        else ConfirmScan();
    }

    /// <summary>Starts the scan: turns plane detection on. The player jumps once they're set up.</summary>
    public void StartScan()
    {
        if (_scanStarted) return;
        _scanStarted = true;
        planeManager.enabled = true;
        Debug.Log("[ARSessionManager] Scan started by the player.");
    }

    // ---- Tracking-state monitoring & relocalisation (Step 4, second half) ----

    private void PollTrackingState()
    {
        var reason = ARSession.notTrackingReason;
        bool wasTracking = IsTracking;
        IsTracking = reason == NotTrackingReason.None;

        if (wasTracking && !IsTracking)
        {
            LastNotTrackingReason = reason;
            Debug.LogWarning($"[ARSessionManager] Tracking lost ({reason}) — suspending gameplay.");
            OnTrackingLost?.Invoke();
        }
        else if (!wasTracking && IsTracking)
        {
            LastNotTrackingReason = NotTrackingReason.None;
            Debug.Log("[ARSessionManager] Tracking restored.");
            OnTrackingRestored?.Invoke();
        }
        else if (!IsTracking && reason != LastNotTrackingReason)
        {
            // Still not tracking, but the reason changed (e.g. ExcessiveMotion -> Relocalizing).
            LastNotTrackingReason = reason;
            Debug.LogWarning($"[ARSessionManager] Still not tracking, reason changed: {reason}.");
        }
    }

    // ---- Scan-phase orchestration (Step 1) ----

    /// <summary>
    /// True if this plane is part of the floor.
    /// On devices with semantic plane classification (every A12+ iPhone, no
    /// LiDAR needed), only ARKit-labelled Floor planes count. Classification
    /// arrives a second or so after a plane appears; a height heuristic here
    /// counted a desk as floor during that window (seen on-device), so
    /// unlabelled planes simply wait. Devices without classification fall
    /// back to height relative to the floor level.
    /// </summary>
    public bool IsFloorPlane(ARPlane plane)
    {
        if (plane == null || plane.alignment != PlaneAlignment.HorizontalUp) return false;

        var classes = plane.classifications;
        if ((classes & PlaneClassifications.Floor) != 0) return true;
        if (classes != PlaneClassifications.None || SupportsClassification) return false;

        return FloorHeight.HasValue && Mathf.Abs(plane.center.y - FloorHeight.Value) <= floorHeightTolerance;
    }

    private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARPlane> changes)
    {
        foreach (var plane in changes.added) RegisterOrUpdatePlane(plane);
        foreach (var plane in changes.updated) RegisterOrUpdatePlane(plane);
        foreach (var kvp in changes.removed) _horizontalPlanes.Remove(kvp.Key);

        UpdateFloorHeight();
        RecalculateTotalArea();
    }

    private void RegisterOrUpdatePlane(ARPlane plane)
    {
        if (plane.alignment == PlaneAlignment.HorizontalUp)
            _horizontalPlanes[plane.trackableId] = plane;
        else
            _horizontalPlanes.Remove(plane.trackableId);
    }

    /// <summary>
    /// Floor level = the largest Floor-classified plane's height if any;
    /// otherwise the lowest unclassified plane of meaningful size.
    /// </summary>
    private void UpdateFloorHeight()
    {
        float bestClassifiedArea = 0f, lowestUnclassified = float.PositiveInfinity;
        float? classifiedHeight = null;

        foreach (var plane in _horizontalPlanes.Values)
        {
            float area = plane.size.x * plane.size.y;
            if ((plane.classifications & PlaneClassifications.Floor) != 0)
            {
                if (area > bestClassifiedArea) { bestClassifiedArea = area; classifiedHeight = plane.center.y; }
            }
            else if (plane.classifications == PlaneClassifications.None && area >= minFloorReferenceArea)
            {
                lowestUnclassified = Mathf.Min(lowestUnclassified, plane.center.y);
            }
        }

        var height = classifiedHeight ?? (float.IsPositiveInfinity(lowestUnclassified) ? (float?)null : lowestUnclassified);
        if (height.HasValue && (!FloorHeight.HasValue || Mathf.Abs(height.Value - FloorHeight.Value) > 0.02f))
        {
            FloorHeight = height;
            Debug.Log($"[ARSessionManager] Floor level {height.Value:F2} m ({(classifiedHeight.HasValue ? "ARKit-classified" : "lowest plane")}).");
        }
    }

    private void RecalculateTotalArea()
    {
        _totalScannedArea = 0f;
        _excludedArea = 0f;
        foreach (var plane in _horizontalPlanes.Values)
        {
            float area = plane.size.x * plane.size.y;
            if (IsFloorPlane(plane)) _totalScannedArea += area;
            else _excludedArea += area;
        }

        if (_scanComplete) return; // area still climbs as ARKit refines planes — not worth logging once we're done

        if (_totalScannedArea - _lastLoggedArea >= logAreaIncrement)
        {
            _lastLoggedArea = _totalScannedArea;
            Debug.Log($"[ARSessionManager] Scanned floor area: {_totalScannedArea:F2} m² ({_excludedArea:F2} m² of non-floor surfaces ignored)");
        }
    }
}