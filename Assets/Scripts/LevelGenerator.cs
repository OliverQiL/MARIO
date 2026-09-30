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
///
/// Game over: EndGame (wired from GameStateManager.OnGameOver) tears down
/// the stage and halts the loop; RestartLoop (OnGameRestarted) re-anchors at
/// the player and starts again from Step 3.
///
/// Campaign (Docs/GAME_DESIGN.md, maps): stage N plays campaignStages[(N-1) %
/// count] if it fits the room — at full size, or compressed (campaignScales:
/// pieces closer together, same sizes) — otherwise the largest stageTemplates
/// fallback that does. Warp pipes: entering one hides the stage and puts the bonus room
/// in the same place; its exit pipe removes the bonus room and brings the
/// stage back as it was.
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
    [Tooltip("The hand-designed campaign, in order: stage N plays entry (N-1) % count, and after the last it loops (harder, via Difficulty).")]
    [SerializeField] private List<GameObject> campaignStages = new();
    [Tooltip("Fallback stage prefabs, each with a StageFootprint component, for when the campaign stage doesn't fit the room. Tried largest-footprint first.")]
    [SerializeField] private List<GameObject> stageTemplates = new();
    [Tooltip("If the campaign stage doesn't fit at full size, it's tried at these scales (positions only; pieces keep their size) before the fallbacks.")]
    [SerializeField] private float[] campaignScales = { 1f, 0.85f, 0.7f, 0.55f };
    [Tooltip("The temporary area a warp pipe leads to, placed where the stage is.")]
    [SerializeField] private GameObject bonusRoomPrefab;

    [Header("Placement Search")]
    [Tooltip("Distances (metres) from the checkpoint anchor to try, closest first.")]
    [SerializeField] private float[] searchDistances = { 0.5f, 1f, 1.5f, 2f, 2.5f, 3f, 3.5f, 4f, 4.5f, 5f, 5.5f, 6f };
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
    [Tooltip("Footprint corners are checked this far (metres) inside the edge when requiring detected floor under the whole stage.")]
    [SerializeField] private float floorCoverageInset = 0.1f;
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
    /// <summary>True while the player is in a warp pipe's bonus room (the stage is hidden).</summary>
    public bool InBonusRoom => _bonusRoom != null;

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
    private GameObject _bonusRoom;
    private float _searchScale = 1f; // layout scale being tried: footprint and piece positions (x, z) shrink together
    private bool _creatingAnchor;
    private float _scanCompleteTime;
    private int _searchObstructed;
    private int _searchNoFloor;
    private int _searchTooClose;
    private int _searchOffFloor;
    private bool _halted;
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
        WarpPipe.OnEntered += HandlePipeEntered;
    }

    private void OnDisable()
    {
        sessionManager.OnScanComplete.RemoveListener(HandleScanComplete);
        WarpPipe.OnEntered -= HandlePipeEntered;
    }

    // ---- warp pipes: bonus room ----

    private void HandlePipeEntered(WarpPipe pipe)
    {
        if (pipe.IsExit) ExitBonusRoom();
        else EnterBonusRoom();
    }

    private void EnterBonusRoom()
    {
        if (bonusRoomPrefab == null || CurrentStage == null || InBonusRoom || IsTransitioning) return;
        var stage = CurrentStage.transform;
        CurrentStage.SetActive(false); // freezes its enemies and hides its targets until the player returns
        _bonusRoom = Instantiate(bonusRoomPrefab, stage.position, stage.rotation, stage.parent);
        var footprint = CurrentStage.GetComponent<StageFootprint>();
        var room = _bonusRoom.GetComponent<BonusRoom>();
        if (room != null && footprint != null) room.Build(footprint.width, footprint.depth);
        Debug.Log($"[LevelGenerator] Warped into the bonus room ({(room != null ? room.CoinCount : 0)} coins).");
    }

    private void ExitBonusRoom()
    {
        if (!InBonusRoom) return;
        Destroy(_bonusRoom);
        _bonusRoom = null;
        if (CurrentStage != null) CurrentStage.SetActive(true);
        Debug.Log("[LevelGenerator] Back to the stage.");
    }

    private void HandleScanComplete()
    {
        if (CheckpointAnchor != null) return; // one checkpoint per session
        _scanCompleteTime = Time.time;
        CreateCheckpointAnchorAsync();
    }

    private async void CreateCheckpointAnchorAsync()
    {
        if (_halted || CheckpointAnchor != null || _creatingAnchor) return;

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
        if (_halted || CheckpointAnchor == null || _stagePlaced) return;

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
        _searchOffFloor = 0;

        if (FindAndInstantiateStage())
        {
            _stagePlaced = true;
            return;
        }

        Debug.LogWarning($"[LevelGenerator] No valid placement found for any template ({SearchSummary()}, last obstruction '{LastObstructionName}') — prompting player to move and retrying shortly.");
        OnPlacementFailed?.Invoke();
        Invoke(nameof(TryPlaceStage), retryDelaySeconds);
    }

    /// <summary>The campaign stage for stage number <paramref name="stage"/> (1-based), looping; null if there's no campaign.</summary>
    public static GameObject CampaignStageFor(IReadOnlyList<GameObject> campaign, int stage)
    {
        if (campaign == null || campaign.Count == 0) return null;
        return campaign[((Mathf.Max(1, stage) - 1) % campaign.Count)];
    }

    private bool FindAndInstantiateStage()
    {
        // This stage's campaign layout first, at full size and then compressed;
        // then the fallbacks, largest first, at full size.
        var gs = GameStateManager.Instance;
        var candidates = new List<(GameObject template, float scale)>();
        var campaign = CampaignStageFor(campaignStages, gs != null ? gs.StageIndex : 1);
        if (campaign != null && campaign.GetComponent<StageFootprint>() != null)
            foreach (var scale in campaignScales) candidates.Add((campaign, scale));
        foreach (var t in stageTemplates
                     .Where(t => t != null && t.GetComponent<StageFootprint>() != null)
                     .OrderByDescending(t => t.GetComponent<StageFootprint>().FootprintArea))
            candidates.Add((t, 1f));

        foreach (var (template, scale) in candidates)
        {
            var footprint = template.GetComponent<StageFootprint>();
            _searchScale = scale;
            bool found = TryFindValidPlacement(footprint, out var pose);
            _searchScale = 1f;
            if (!found) continue;

            CurrentStage = Instantiate(template, pose.position, pose.rotation, CheckpointAnchor.transform);
            if (!Mathf.Approximately(scale, 1f)) CompressLayout(CurrentStage, scale);
            Debug.Log($"[LevelGenerator] Placed '{template.name}'{(Mathf.Approximately(scale, 1f) ? "" : $" at {scale:P0} size")} at {pose.position}, yaw {pose.rotation.eulerAngles.y:F0}° — skipped {SearchSummary()} ({MeshChunkCount} mesh chunks).");
            StartStage();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Fits a layout into less floor: moves its pieces closer together (their
    /// x/z positions scale; sizes and heights don't) and shrinks its footprint
    /// to match, so a bonus room built on it fits too.
    /// </summary>
    public static void CompressLayout(GameObject stage, float scale)
    {
        foreach (Transform piece in stage.transform)
        {
            var p = piece.localPosition;
            piece.localPosition = new Vector3(p.x * scale, p.y, p.z * scale);
        }
        var footprint = stage.GetComponent<StageFootprint>();
        if (footprint == null) return;
        footprint.width *= scale;
        footprint.depth *= scale;
    }

    /// <summary>A template-space point as it will be after CompressLayout at the scale being searched.</summary>
    private Vector3 SearchScaled(Vector3 local) => new(local.x * _searchScale, local.y, local.z * _searchScale);

    private string SearchSummary() =>
        $"{_searchNoFloor} no-floor, {_searchTooClose} too-close-to-player, {_searchOffFloor} partly-off-floor, {_searchObstructed} obstructed";

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
                    if (!IsFullyOnFloor(footprint, floorPose.position, candidateRotation, anchorTransform.position.y))
                    {
                        _searchOffFloor++;
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

        TearDownStage();
        IsTransitioning = false;
        CreateCheckpointAnchorAsync(); // re-anchor at the player's current pose, then Step 3 again
    }

    private void TearDownStage()
    {
        if (_bonusRoom != null) Destroy(_bonusRoom);
        _bonusRoom = null;
        if (CurrentStage != null) Destroy(CurrentStage);
        CurrentStage = null;
        CurrentGoal = null;

        var oldAnchor = CheckpointAnchor;
        CheckpointAnchor = null;
        if (oldAnchor != null && !anchorManager.TryRemoveAnchor(oldAnchor))
            Destroy(oldAnchor.gameObject);

        _stagePlaced = false;
    }

    /// <summary>Wired to GameStateManager.OnGameOver: removes the stage and halts the loop.</summary>
    public void EndGame()
    {
        _halted = true;
        StopAllCoroutines();
        CancelInvoke();
        IsTransitioning = false;
        IsWaitingForMesh = false;
        TearDownStage();
        Debug.Log("[LevelGenerator] Game over — loop halted.");
    }

    /// <summary>Wired to GameStateManager.OnGameRestarted: re-anchors at the player and places a fresh stage.</summary>
    public void RestartLoop()
    {
        _halted = false;
        StagesCompleted = 0;
        TearDownStage();
        Debug.Log("[LevelGenerator] New game — placing first stage.");
        CreateCheckpointAnchorAsync();
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
        float dx = Mathf.Max(0f, Mathf.Abs(local.x) - footprint.width * _searchScale * 0.5f);
        float dz = Mathf.Max(0f, Mathf.Abs(local.z) - footprint.depth * _searchScale * 0.5f);
        if (dx * dx + dz * dz < playerClearance * playerClearance) return false;

        var goal = footprint.GetComponentInChildren<StageGoal>(true);
        if (goal == null) return true;
        var goalOffset = SearchScaled(footprint.transform.InverseTransformPoint(goal.transform.position));
        var goalWorld = floorPosition + rotation * goalOffset;
        var toGoal = goalWorld - player;
        toGoal.y = 0f;
        return toGoal.magnitude >= goal.ReachRadius + minGoalWalkDistance;
    }

    /// <summary>
    /// Requires detected floor under all four footprint corners (inset slightly)
    /// and under the goal. Only the centre used to be checked, so on devices
    /// without LiDAR the flag could end up over a cabinet or sofa. Floor planes
    /// stop at furniture edges, so this works without the room mesh.
    /// </summary>
    private bool IsFullyOnFloor(StageFootprint footprint, Vector3 floorPosition, Quaternion rotation, float floorHeight)
    {
        float hx = Mathf.Max(0f, footprint.width * _searchScale * 0.5f - floorCoverageInset);
        float hz = Mathf.Max(0f, footprint.depth * _searchScale * 0.5f - floorCoverageInset);
        for (int sx = -1; sx <= 1; sx += 2)
        for (int sz = -1; sz <= 1; sz += 2)
        {
            if (!TryGetFloorProjectedPose(floorPosition + rotation * new Vector3(sx * hx, 0f, sz * hz), out _, floorHeight))
                return false;
        }

        var goal = footprint.GetComponentInChildren<StageGoal>(true);
        if (goal == null) return true;
        var goalOffset = SearchScaled(footprint.transform.InverseTransformPoint(goal.transform.position));
        return TryGetFloorProjectedPose(floorPosition + rotation * goalOffset, out _, floorHeight);
    }

    private bool IsPlacementClear(StageFootprint footprint, Vector3 floorPosition, Quaternion rotation)
    {
        // Lift the box off the floor by the margin (shrinking it to keep the same
        // top), so the floor's own mesh triangles don't register as obstructions.
        var halfExtents = SearchScaled(footprint.HalfExtents);
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
