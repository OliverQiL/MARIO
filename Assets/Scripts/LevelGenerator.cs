using System.Collections;
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
/// On LiDAR devices the scanned room mesh (ARMeshManager, on its own
/// ARMesh layer) is what the CheckBox tests against. Because that mesh
/// includes the floor itself, the test box is lifted by
/// floorClearanceMargin so floor triangles and depth noise don't count as
/// obstructions. Placement also waits briefly for the mesh to settle after
/// the scan completes, so stages don't land inside unmeshed furniture.
/// On non-LiDAR devices meshing never runs and there is no wait.
///
/// Step 6 (stage loop): each stage prefab carries a StageGoal. When the
/// player physically reaches it, OnStageCompleted fires (closing Gate 1 via
/// GameStateManager), and after a short pause the completed stage and its
/// anchor are destroyed, a fresh checkpoint anchor is created at the
/// player's current floor position/heading, and Step 3 runs again. Re-anchoring
/// every stage bounds both memory and accumulated tracking drift.
/// OnStageStarted fires each time a stage is placed and playable.
/// </summary>
[RequireComponent(typeof(ARSessionManager), typeof(ARAnchorManager), typeof(ARRaycastManager))]
public class LevelGenerator : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ARSessionManager sessionManager;
    [SerializeField] private ARAnchorManager anchorManager;
    [SerializeField] private ARRaycastManager raycastManager;
    [SerializeField] private Camera arCamera;
    [Tooltip("Optional: the ARMeshManager (on the 'AR Meshes' child). Auto-found in children if left empty.")]
    [SerializeField] private ARMeshManager meshManager;

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
    [Tooltip("Yaw offsets tried at each candidate, relative to facing directly away from the player (0 = stage's +Z points away, so its goal is on the far side).")]
    [SerializeField] private float[] rotationCandidates = { 0f, 90f, 270f, 180f };
    [Tooltip("Minimum horizontal gap (metres) between the player and the nearest edge of a placed stage's footprint.")]
    [SerializeField] private float playerClearance = 0.3f;
    [Tooltip("The goal must be at least this far (metres) beyond its reach radius from the player when the stage appears, so it takes deliberate walking.")]
    [SerializeField] private float minGoalWalkDistance = 0.5f;
    [Tooltip("Layers that count as obstructions. Should be the ARMesh layer only, so debug markers and stage colliders don't block placement.")]
    [SerializeField] private LayerMask obstructionLayerMask = ~0;
    [Tooltip("Placement candidates must land on a plane within this height (metres) of the checkpoint's floor, so stages never go on beds/tables.")]
    [SerializeField] private float floorHeightTolerance = 0.15f;
    [Tooltip("Height (metres) the obstruction test box is lifted off the floor, so the LiDAR floor mesh and its noise don't count as obstructions.")]
    [SerializeField] private float floorClearanceMargin = 0.1f;
    [Tooltip("Seconds to wait before retrying if no placement is found anywhere.")]
    [SerializeField] private float retryDelaySeconds = 2f;

    [Header("LiDAR Mesh Settling")]
    [Tooltip("Seconds after scan completion to let the room mesh fill in before searching for placements (LiDAR devices only).")]
    [SerializeField] private float meshSettleSeconds = 3f;
    [Tooltip("Minimum number of mesh chunks required before searching for placements (LiDAR devices only).")]
    [SerializeField] private int minMeshChunks = 1;
    [Tooltip("Give up waiting for mesh chunks after this many seconds and place anyway.")]
    [SerializeField] private float maxMeshWaitSeconds = 10f;
    [Tooltip("Seconds between checks while waiting for the mesh to settle.")]
    [SerializeField] private float meshWaitPollSeconds = 0.5f;

    [Header("Stage Loop")]
    [Tooltip("Seconds between reaching a goal and the next stage appearing, so the player registers the completion.")]
    [SerializeField] private float transitionDelaySeconds = 1.5f;

    [Header("Events")]
    [Tooltip("Fired each time a full search (all templates, all candidates) fails to find a placement.")]
    public UnityEvent OnPlacementFailed;
    [Tooltip("Fired when a stage has been placed and is playable. Wire to GameStateManager.BeginStage.")]
    public UnityEvent OnStageStarted;
    [Tooltip("Fired when the player reaches the current stage's goal. Wire to GameStateManager.CompleteStage.")]
    public UnityEvent OnStageCompleted;

    public ARAnchor CheckpointAnchor { get; private set; }
    /// <summary>The live stage instance, or null while scanning/placing/transitioning.</summary>
    public GameObject CurrentStage { get; private set; }
    public StageGoal CurrentGoal { get; private set; }
    public bool IsTransitioning { get; private set; }
    public int StagesCompleted { get; private set; }

    /// <summary>True while placement is deferred waiting for the LiDAR mesh to settle.</summary>
    public bool IsWaitingForMesh { get; private set; }
    /// <summary>Number of room-mesh chunks currently tracked (0 on non-LiDAR devices).</summary>
    public int MeshChunkCount => IsMeshingActive ? meshManager.meshes.Count : 0;
    public bool IsMeshingActive => meshManager != null && meshManager.enabled
        && meshManager.subsystem != null && meshManager.subsystem.running;
    /// <summary>Candidates rejected because something on the obstruction layers was in the way, across all searches.</summary>
    public int TotalObstructedCandidates { get; private set; }
    /// <summary>Name of the collider that most recently blocked a candidate.</summary>
    public string LastObstructionName { get; private set; } = "";

    private bool _stagePlaced;
    private bool _creatingAnchor;
    private float _scanCompleteTime;
    private int _searchObstructed;
    private int _searchNoFloor;
    private int _searchTooClose;
    private static readonly List<ARRaycastHit> s_Hits = new();
    private static readonly Collider[] s_Overlap = new Collider[1];

    private void Awake()
    {
        if (sessionManager == null) sessionManager = GetComponent<ARSessionManager>();
        if (anchorManager == null) anchorManager = GetComponent<ARAnchorManager>();
        if (raycastManager == null) raycastManager = GetComponent<ARRaycastManager>();
        if (arCamera == null) arCamera = Camera.main;
        if (meshManager == null) meshManager = GetComponentInChildren<ARMeshManager>();
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
        _scanCompleteTime = Time.time;
        CreateCheckpointAnchorAsync();
    }

    private async void CreateCheckpointAnchorAsync()
    {
        if (CheckpointAnchor != null || _creatingAnchor) return;

        if (!TryGetFloorProjectedPose(arCamera.transform.position, out var pose))
        {
            Debug.LogWarning("[LevelGenerator] No detected floor directly below the player — retrying checkpoint shortly.");
            Invoke(nameof(CreateCheckpointAnchorAsync), retryDelaySeconds);
            return;
        }

        // Keep only yaw: the stage should face the way the player is looking, level with the floor.
        var forward = Vector3.ProjectOnPlane(arCamera.transform.forward, Vector3.up);
        if (forward.sqrMagnitude > 1e-4f)
            pose.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);

        _creatingAnchor = true;
        var result = await anchorManager.TryAddAnchorAsync(pose);
        _creatingAnchor = false;
        if (!result.status.IsSuccess())
        {
            Debug.LogError($"[LevelGenerator] Failed to create checkpoint anchor: {result.status} — retrying shortly.");
            Invoke(nameof(CreateCheckpointAnchorAsync), retryDelaySeconds);
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

        if (ShouldWaitForMesh())
        {
            IsWaitingForMesh = true;
            Invoke(nameof(TryPlaceStage), meshWaitPollSeconds);
            return;
        }
        IsWaitingForMesh = false;

        _searchObstructed = 0;
        _searchNoFloor = 0;
        _searchTooClose = 0;

        if (FindAndInstantiateStage())
        {
            _stagePlaced = true;
            return;
        }

        Debug.LogWarning($"[LevelGenerator] No valid placement found for any template ({SearchSummary()}, last obstruction '{LastObstructionName}') — prompting player to move and retrying shortly.");
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
                CurrentStage = Instantiate(template, pose.position, pose.rotation, CheckpointAnchor.transform);
                Debug.Log($"[LevelGenerator] Placed '{template.name}' at {pose.position}, yaw {pose.rotation.eulerAngles.y:F0}° — skipped {SearchSummary()} ({MeshChunkCount} mesh chunks).");
                StartStage();
                return true;
            }
        }

        return false;
    }

    private string SearchSummary() =>
        $"{_searchNoFloor} no-floor, {_searchTooClose} too-close-to-player, {_searchObstructed} obstructed";

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

                if (!TryGetFloorProjectedPose(approximatePoint, out var floorPose, anchorTransform.position.y))
                {
                    _searchNoFloor++;
                    continue;
                }

                // Face the stage away from the player so its goal is on the far side.
                var awayFromPlayer = Quaternion.LookRotation(direction, Vector3.up);
                foreach (var rotationY in rotationCandidates)
                {
                    var candidateRotation = awayFromPlayer * Quaternion.Euler(0f, rotationY, 0f);
                    if (!KeepsClearOfPlayer(footprint, floorPose.position, candidateRotation))
                    {
                        _searchTooClose++;
                        continue;
                    }
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

    // ---- Step 6: stage loop ----

    private void StartStage()
    {
        CurrentGoal = CurrentStage.GetComponentInChildren<StageGoal>();
        if (CurrentGoal != null)
        {
            CurrentGoal.Initialise(arCamera.transform);
            CurrentGoal.OnReached += HandleGoalReached;
        }
        else
        {
            Debug.LogWarning($"[LevelGenerator] Stage '{CurrentStage.name}' has no StageGoal — it can never be completed.");
        }

        OnStageStarted?.Invoke();
    }

    private void HandleGoalReached(StageGoal goal)
    {
        if (IsTransitioning || goal != CurrentGoal) return;

        goal.OnReached -= HandleGoalReached;
        IsTransitioning = true;
        StagesCompleted++;
        Debug.Log($"[LevelGenerator] Stage complete ({StagesCompleted} so far) — next stage in {transitionDelaySeconds:F1}s.");
        OnStageCompleted?.Invoke();
        StartCoroutine(TransitionToNextStage());
    }

    private IEnumerator TransitionToNextStage()
    {
        yield return new WaitForSeconds(transitionDelaySeconds);

        if (CurrentStage != null) Destroy(CurrentStage);
        CurrentStage = null;
        CurrentGoal = null;

        var oldAnchor = CheckpointAnchor;
        CheckpointAnchor = null;
        if (oldAnchor != null && !anchorManager.TryRemoveAnchor(oldAnchor))
            Destroy(oldAnchor.gameObject);

        _stagePlaced = false;
        IsTransitioning = false;
        CreateCheckpointAnchorAsync(); // re-anchor at the player's current pose, then Step 3 again
    }

    /// <summary>
    /// On LiDAR devices, defers placement until the room mesh has had time to
    /// fill in. Gives up after maxMeshWaitSeconds so a sparse mesh can't stall
    /// the game forever. Always false when meshing isn't running.
    /// </summary>
    private bool ShouldWaitForMesh()
    {
        if (!IsMeshingActive) return false;

        float elapsed = Time.time - _scanCompleteTime;
        if (elapsed >= maxMeshWaitSeconds)
        {
            if (MeshChunkCount < minMeshChunks)
                Debug.LogWarning($"[LevelGenerator] Mesh still sparse after {maxMeshWaitSeconds}s ({MeshChunkCount} chunks) — placing anyway.");
            return false;
        }

        bool settled = elapsed >= meshSettleSeconds && MeshChunkCount >= minMeshChunks;
        if (!settled && !IsWaitingForMesh)
            Debug.Log($"[LevelGenerator] Waiting for room mesh to settle ({MeshChunkCount} chunks so far).");
        return !settled;
    }

    /// <summary>
    /// Rejects candidates that would appear on top of the player or whose goal
    /// would already be (nearly) within reach — otherwise a stationary player
    /// clears stage after stage without moving (seen on-device).
    /// </summary>
    private bool KeepsClearOfPlayer(StageFootprint footprint, Vector3 floorPosition, Quaternion rotation)
    {
        var player = arCamera.transform.position;

        // Horizontal distance from the player to the footprint rectangle.
        var local = Quaternion.Inverse(rotation) * (player - floorPosition);
        float dx = Mathf.Max(0f, Mathf.Abs(local.x) - footprint.width * 0.5f);
        float dz = Mathf.Max(0f, Mathf.Abs(local.z) - footprint.depth * 0.5f);
        if (dx * dx + dz * dz < playerClearance * playerClearance) return false;

        var goal = footprint.GetComponentInChildren<StageGoal>(true);
        if (goal == null) return true;
        var goalOffset = footprint.transform.InverseTransformPoint(goal.transform.position);
        var goalWorld = floorPosition + rotation * goalOffset;
        var toGoal = goalWorld - player;
        toGoal.y = 0f;
        return toGoal.magnitude >= goal.ReachRadius + minGoalWalkDistance;
    }

    private bool IsPlacementClear(StageFootprint footprint, Vector3 floorPosition, Quaternion rotation)
    {
        // Lift the box off the floor by the margin (shrinking it to keep the same
        // top), so the floor's own mesh triangles don't register as obstructions.
        var halfExtents = footprint.HalfExtents;
        float margin = Mathf.Min(floorClearanceMargin, footprint.clearance * 0.5f);
        halfExtents.y -= margin * 0.5f;
        var boxCenter = floorPosition + Vector3.up * (margin + halfExtents.y);

        int hits = Physics.OverlapBoxNonAlloc(boxCenter, halfExtents, s_Overlap, rotation, obstructionLayerMask, QueryTriggerInteraction.Ignore);
        if (hits == 0) return true;

        _searchObstructed++;
        TotalObstructedCandidates++;
        LastObstructionName = s_Overlap[0].name;
        return false;
    }

    /// <summary>
    /// Projects an approximate world point onto the floor by raycasting straight
    /// down against detected floor planes. Used both for the checkpoint anchor
    /// (from the camera) and for placement candidates (from search points).
    ///
    /// A downward ray can pass through several horizontal planes (a bed or
    /// table above the floor), and the first hit is the highest one. So only
    /// floor planes (ARSessionManager.IsFloorPlane) are considered and, when <paramref name="floorHeight"/>
    /// is known (placement candidates), only those within floorHeightTolerance
    /// of it; otherwise (the checkpoint itself) the lowest plane is taken.
    /// </summary>
    private bool TryGetFloorProjectedPose(Vector3 approximatePoint, out Pose pose, float? floorHeight = null)
    {
        pose = default;
        var ray = new Ray(approximatePoint + Vector3.up * 2f, Vector3.down);
        if (!raycastManager.Raycast(ray, s_Hits, TrackableType.PlaneWithinPolygon)) return false;

        bool found = false;
        float best = float.PositiveInfinity;
        foreach (var hit in s_Hits)
        {
            if (hit.trackable is ARPlane plane && !sessionManager.IsFloorPlane(plane)) continue;

            float y = hit.pose.position.y;
            float score = floorHeight.HasValue ? Mathf.Abs(y - floorHeight.Value) : y;
            if (floorHeight.HasValue && score > floorHeightTolerance) continue;
            if (score >= best) continue;

            best = score;
            pose = hit.pose;
            found = true;
        }
        return found;
    }
}
