using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Session lifecycle, tracking-state monitoring, scan-phase orchestration,
/// and relocalisation handling (design report Step 1 and Step 4's second
/// half).
///
/// Scan-phase orchestration: tracks total scanned floor area from AR
/// Foundation's plane detection and signals when enough floor space has
/// been scanned to place a stage. Only counts upward-facing horizontal
/// planes (floors) — walls, ceilings, and anything not yet classified as
/// horizontal-up are ignored.
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
    [Tooltip("Total horizontal floor area (in square metres) required before scanning is considered complete.")]
    [SerializeField] private float requiredFloorArea = 4.0f;

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

    private readonly Dictionary<TrackableId, float> _floorPlaneAreas = new();
    private float _totalScannedArea;
    private float _lastLoggedArea;
    private bool _scanComplete;

    public float TotalScannedArea => _totalScannedArea;
    public float RequiredFloorArea => requiredFloorArea;
    public bool IsScanComplete => _scanComplete;

    public bool IsTracking { get; private set; } = true;
    public NotTrackingReason LastNotTrackingReason { get; private set; } = NotTrackingReason.None;

    private void Awake()
    {
        if (planeManager == null)
        {
            planeManager = GetComponent<ARPlaneManager>();
        }
    }

    private void OnEnable()
    {
        planeManager.trackablesChanged.AddListener(OnTrackablesChanged);
    }

    private void OnDisable()
    {
        planeManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
    }

    private void Update()
    {
        PollTrackingState();
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

    private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARPlane> changes)
    {
        foreach (var plane in changes.added) RegisterOrUpdatePlane(plane);
        foreach (var plane in changes.updated) RegisterOrUpdatePlane(plane);
        foreach (var kvp in changes.removed) _floorPlaneAreas.Remove(kvp.Key);

        RecalculateTotalArea();
    }

    private void RegisterOrUpdatePlane(ARPlane plane)
    {
        if (plane.alignment != PlaneAlignment.HorizontalUp)
        {
            _floorPlaneAreas.Remove(plane.trackableId);
            return;
        }

        _floorPlaneAreas[plane.trackableId] = plane.size.x * plane.size.y;
    }

    private void RecalculateTotalArea()
    {
        _totalScannedArea = 0f;
        foreach (var area in _floorPlaneAreas.Values)
        {
            _totalScannedArea += area;
        }

        if (_scanComplete) return; // area still climbs as ARKit refines planes — not worth logging once we're done

        if (_totalScannedArea - _lastLoggedArea >= logAreaIncrement)
        {
            _lastLoggedArea = _totalScannedArea;
            Debug.Log($"[ARSessionManager] Scanned floor area: {_totalScannedArea:F2} / {requiredFloorArea:F2} m²");
        }

        if (_totalScannedArea >= requiredFloorArea)
        {
            _scanComplete = true;
            Debug.Log($"[ARSessionManager] Scan complete — enough floor area found ({_totalScannedArea:F2} m²).");
            OnScanComplete?.Invoke();
        }
    }
}