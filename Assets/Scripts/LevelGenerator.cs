using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Owns checkpoint anchoring and stage generation (design report Steps 2-3).
///
/// Step 2 (implemented previously): once ARSessionManager signals scan
/// complete, creates a single world-locked checkpoint anchor at the
/// player's position projected onto the floor, retaining only yaw.
///
/// Step 3 (this pass): once the checkpoint anchor exists, searches for a
/// free floor region matching a stage template's footprint — preferring
/// the anchor's forward arc, falling back through rotated placements and
/// smaller "compact" templates, and validating each candidate with a
/// Physics.CheckBox test against the scanned mesh's colliders. If nothing
/// fits anywhere, it signals OnPlacementFailed (for an HUD move-prompt)
/// and retries automatically after a short delay.
///
/// Note: this places a single stage. Chaining further stages across a
/// repeating loop is Step 6, and depends on stage-completion logic that
/// doesn't exist yet.
/// </summary>
[RequireComponent(typeof(ARSessionManager), typeof(ARAnchorManager), typeof(ARRaycastManager))]
public class LevelGenerator : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ARSessionManager sessionManager;
    [SerializeField] private ARAnchorManager anchorManager;
    [SerializeField] private ARRaycastManager raycastManager;
    [SerializeField] private Camera arCamera;

    [Header("Debug")]
    [Tooltip("Optional: a simple visible placeholder instantiated under the checkpoint anchor, so you can confirm placement before real stage prefabs exist.")]
    [SerializeField] private GameObject debugMarkerPrefab;

    [Header("Stage Templates")]
    [Tooltip("Stage prefabs, each with a StageFootprint component. Tried largest-footprint first, falling back to smaller ones.")]
    [SerializeField] private List<GameObject> stageTemplates = new();

    [Header("Placement Search")]
    [Tooltip("Distances (metres) from the checkpoint anchor to try, closest first.")]
    [SerializeField] private float[] searchDistances = { 0.5f, 1f, 1.5f, 2f, 2.5f };
    [Tooltip("Half-angle (degrees) either side of the anchor's forward direction searched before expanding to the full circle.")]
    [SerializeField] private float forwardArcHalfAngle = 60f;
    [Tooltip("Angular step (degrees) between search directions.")]
    [SerializeField] private float angleStep = 15f;
    [Tooltip("Yaw rotations tried at each candidate position.")]
    [SerializeField] private float[] rotationCandidates = { 0f, 90f, 180f, 270f };
    [SerializeField] private LayerMask obstructionLayerMask = ~0;
    [Tooltip("Seconds to wait before retrying if no placement is found anywhere.")]
    [SerializeField] private float retryDelaySeconds = 2f;

    [Header("Events")]
    [Tooltip("Fired each time a full search (all templates, all candidates) fails to find a placement.")]
    public UnityEvent OnPlacementFailed;

    public ARAnchor CheckpointAnchor { get; private set; }

    private bool _stagePlaced;
    private static readonly List<ARRaycastHit> s_Hits = new();

    private void Awake()
    {
        if (sessionManager == null) sessionManager = GetComponent<ARSessionManager>();
        if (anchorManager == null) anchorManager = GetComponent<ARAnchorManager>();
        if (raycastManager == null) raycastManager = GetComponent<ARRaycastManager>();
        if (arCamera == null) arCamera = Camera.main;
    }

    private void OnEnable()
    {
        sessionManager.OnScanComplete.AddListener(HandleScanComplete);
    }

    private void OnDisable()
    {
        sessionManager.OnScanComplete.RemoveListener(HandleScanComplete);
    }

    private void HandleScanComplete()
    {
        if (CheckpointAnchor != null) return; // one checkpoint per session
        CreateCheckpointAnchorAsync();
    }

    private async void CreateCheckpointAnchorAsync()
    {
        if (!TryGetFloorProjectedPose(arCamera.transform.position, out var pose))
        {
            Debug.LogWarning("[LevelGenerator] Scan completed but found no floor directly below the player.");
            return;
        }

        var result = await anchorManager.TryAddAnchorAsync(pose);
        if (!result.status.IsSuccess())
        {
            Debug.LogError($"[LevelGenerator] Failed to create checkpoint anchor: {result.status}");
            return;
        }

        CheckpointAnchor = result.value;
        Debug.Log($"[LevelGenerator] Checkpoint anchor created at {pose.position}, yaw {pose.rotation.eulerAngles.y:F0}°");

        if (debugMarkerPrefab != null)
        {
            Instantiate(debugMarkerPrefab, CheckpointAnchor.transform);
        }

        TryPlaceStage();
    }

    // ---- Step 3: stage placement ----

    public void TryPlaceStage()
    {
        if (CheckpointAnchor == null || _stagePlaced) return;

        if (FindAndInstantiateStage())
        {
            _stagePlaced = true;
            return;
        }

        Debug.LogWarning("[LevelGenerator] No valid placement found for any template — prompting player to move and retrying shortly.");
        OnPlacementFailed?.Invoke();
        Invoke(nameof(TryPlaceStage), retryDelaySeconds);
    }

    private bool FindAndInstantiateStage()
    {
        var orderedTemplates = stageTemplates
            .Where(t => t != null && t.GetComponent<StageFootprint>() != null)
            .OrderByDescending(t => t.GetComponent<StageFootprint>().FootprintArea)
            .ToList();

        foreach (var template in orderedTemplates)
        {
            var footprint = template.GetComponent<StageFootprint>();
            if (TryFindValidPlacement(footprint, out var pose))
            {
                Instantiate(template, pose.position, pose.rotation, CheckpointAnchor.transform);
                Debug.Log($"[LevelGenerator] Placed '{template.name}' at {pose.position}.");
                return true;
            }
        }

        return false;
    }

    private bool TryFindValidPlacement(StageFootprint footprint, out Pose pose)
    {
        var anchorTransform = CheckpointAnchor.transform;

        // Pass 1: the anchor's forward arc. Pass 2: the rest of the circle,
        // only attempted if pass 1 finds nothing.
        if (TrySearchAngleRange(footprint, anchorTransform, -forwardArcHalfAngle, forwardArcHalfAngle, out pose))
            return true;
        if (TrySearchAngleRange(footprint, anchorTransform, forwardArcHalfAngle, 360f - forwardArcHalfAngle, out pose))
            return true;

        pose = default;
        return false;
    }

    private bool TrySearchAngleRange(StageFootprint footprint, Transform anchorTransform, float fromDeg, float toDeg, out Pose pose)
    {
        for (float angle = fromDeg; angle <= toDeg; angle += angleStep)
        {
            var direction = Quaternion.Euler(0f, angle, 0f) * anchorTransform.forward;

            foreach (var distance in searchDistances)
            {
                var approximatePoint = anchorTransform.position + direction.normalized * distance;

                if (!TryGetFloorProjectedPose(approximatePoint, out var floorPose))
                    continue;

                foreach (var rotationY in rotationCandidates)
                {
                    var candidateRotation = Quaternion.Euler(0f, rotationY, 0f);
                    if (IsPlacementClear(footprint, floorPose.position, candidateRotation))
                    {
                        pose = new Pose(floorPose.position, candidateRotation);
                        return true;
                    }
                }
            }
        }

        pose = default;
        return false;
    }

    private bool IsPlacementClear(StageFootprint footprint, Vector3 floorPosition, Quaternion rotation)
    {
        var halfExtents = footprint.HalfExtents;
        var boxCenter = floorPosition + Vector3.up * halfExtents.y;
        return !Physics.CheckBox(boxCenter, halfExtents, rotation, obstructionLayerMask);
    }

    /// <summary>
    /// Projects an approximate world point onto the floor by raycasting straight
    /// down against detected floor planes. Used both for the checkpoint anchor
    /// (from the camera) and for placement candidates (from search points).
    /// </summary>
    private bool TryGetFloorProjectedPose(Vector3 approximatePoint, out Pose pose)
    {
        var ray = new Ray(approximatePoint + Vector3.up * 2f, Vector3.down);

        if (raycastManager.Raycast(ray, s_Hits, TrackableType.PlaneWithinPolygon))
        {
            pose = s_Hits[0].pose;
            return true;
        }

        pose = default;
        return false;
    }
}
