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
/// Step 3: once the checkpoint anchor exists, searches for a free floor
/// region for the stage's footprint. Every stage faces directly away from
/// the player (its start nearest them, its goal on the far side), and of all
/// the spots that fit (detected floor under the footprint and goal, clear of
/// the player and, with LiDAR, of the room mesh) it takes the best-centred
/// one: the largest clear margin of floor round the footprint, refined by
/// small nudges. A fit with less than preferredMargin to spare first tries
/// the compressed scales and fallback templates, then takes the
/// best-centred fit found. If nothing fits anywhere, it signals
/// OnPlacementFailed (for an HUD move-prompt) and retries after a delay.
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
/// player jumps at it, OnStageCompleted fires (closing Gate 1 via
/// GameStateManager), and after a short pause the view irises out
/// (ScreenTransition), the completed stage and its anchor are destroyed, a fresh checkpoint anchor is created at the
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
/// fallback that does. Completing the last campaign stage (Bowser's castle)
/// clears the game: GameStateManager.ClearGame, a celebration, then the loop
/// halts until a jump restarts it (RestartLoop). Warp pipes: entering one irises out, hides the stage and puts
/// the bonus room in the same place; its exit pipe removes the bonus room and brings the
/// stage back as it was. The next stage irises in once it's placed.
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

    [Header("Stage Templates")]
    [Tooltip("The hand-designed campaign, in order: stage N plays entry (N-1) % count, and after the last it loops (harder, via Difficulty).")]
    [SerializeField] private List<GameObject> campaignStages = new();
    [Tooltip("The training round (Tutorial), played after every scan (launch and hard reset) before 1-1. Empty = no training.")]
    [SerializeField] private GameObject trainingStage;
    [Tooltip("Fallback stage prefabs, each with a StageFootprint component, for when the campaign stage doesn't fit the room. Tried largest-footprint first.")]
    [SerializeField] private List<GameObject> stageTemplates = new();
    [Tooltip("If the campaign stage doesn't fit at full size, it's tried at these scales (positions only; pieces keep their size) before the fallbacks.")]
    [SerializeField] private float[] campaignScales = { 1f, 0.85f, 0.7f, 0.55f };
    [Tooltip("The temporary area a warp pipe leads to, placed where the stage is.")]
    [SerializeField] private GameObject bonusRoomPrefab;

    [Header("Placement Search")]
    [Tooltip("Distances (metres) from the checkpoint anchor to try, closest first.")]
    [SerializeField] private float[] searchDistances = { 0.5f, 1f, 1.5f, 2f, 2.5f, 3f, 3.5f, 4f, 4.5f, 5f, 5.5f, 6f };
    [Tooltip("Half-angle (degrees) either side of where the player faces: candidates there get forwardBonus, so the stage tends to appear in view.")]
    [SerializeField] private float forwardArcHalfAngle = 60f;
    [Tooltip("Angular step (degrees) between search directions.")]
    [SerializeField] private float angleStep = 15f;

    [Header("Centring")]
    [Tooltip("The clear floor margin round the footprint is measured up to this (metres): the bigger, the more centred. Beyond it, closer to the player wins.")]
    [SerializeField] private float centringMargin = 1f;
    [Tooltip("Resolution (metres) of the margin measurement.")]
    [SerializeField] private float centringStep = 0.1f;
    [Tooltip("A fit with at least this margin (metres) is taken at once; with less, smaller scales and fallbacks are tried first and the best-centred fit overall wins.")]
    [SerializeField] private float preferredMargin = 0.3f;
    [Tooltip("Score bonus (metres of margin) for a spot within the forward arc, so the stage appears where the player is looking when it can.")]
    [SerializeField] private float forwardBonus = 0.15f;
    [Tooltip("Score penalty per metre of distance from the player, so among equally centred spots the nearer wins.")]
    [SerializeField] private float distancePenalty = 0.05f;
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
    [Tooltip("After the final stage's goal: seconds the stage stays up for the celebration before it's cleared away.")]
    [SerializeField] private float finalCelebrationSeconds = 4f;

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
    /// <summary>The next stage placed is the training round: true after each scan (launch, hard reset), false after it or a game over.</summary>
    private bool _trainingPending = true;
    /// <summary>Raised when the training round's flag is grabbed (the HUD shows TRAINING COMPLETE!).</summary>
    public event System.Action TrainingCompleted;
    public bool InTraining => CurrentStage != null && CurrentStage.GetComponent<Tutorial>() != null;
    /// <summary>Bumped by a hard reset, so an anchor still being created for the old game is dropped.</summary>
    private int _generation;
    private float _scanCompleteTime;
    private int _searchObstructed;
    private int _searchNoFloor;
    private int _searchTooClose;
    private int _searchOffFloor;
    private bool _halted;
    private bool _warping;
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
        if (_warping) return;
        _warping = true;
        // Iris out, swap stage and room while the view is black, iris in.
        ScreenTransition.Run(() =>
        {
            _warping = false;
            if (pipe.IsExit) ExitBonusRoom();
            else EnterBonusRoom();
        });
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
            // The floor right under the player is the part they least often look at (more so
            // with a head-mounted phone, looking ahead), so it's often not detected even after a
            // big scan. The scan knows the floor's height: put the checkpoint there, under the
            // player. Stage placement still checks for detected floor under the whole stage.
            if (sessionManager.FloorHeight is not { } floorY)
            {
                Debug.LogWarning("[LevelGenerator] No floor level known yet — retrying checkpoint shortly.");
                Invoke(nameof(CreateCheckpointAnchorAsync), retryDelaySeconds);
                return;
            }
            var feet = arCamera.transform.position;
            pose = new Pose(new Vector3(feet.x, floorY, feet.z), Quaternion.identity);
            Debug.Log($"[LevelGenerator] No detected floor directly below the player — using the scanned floor level ({floorY:F2} m).");
        }

        // Keep only yaw: the stage should face the way the player is looking, level with the floor.
        var forward = Vector3.ProjectOnPlane(arCamera.transform.forward, Vector3.up);
        if (forward.sqrMagnitude > 1e-4f)
            pose.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);

        _creatingAnchor = true;
        int generation = _generation;
        var result = await anchorManager.TryAddAnchorAsync(pose);
        if (generation != _generation)
        {
            // A hard reset happened while it was being created: it belongs to the old game.
            if (result.status.IsSuccess() && result.value != null && !anchorManager.TryRemoveAnchor(result.value)) Destroy(result.value.gameObject);
            return;
        }
        _creatingAnchor = false;
        if (!result.status.IsSuccess())
        {
            Debug.LogError($"[LevelGenerator] Failed to create checkpoint anchor: {result.status} — retrying shortly.");
            Invoke(nameof(CreateCheckpointAnchorAsync), retryDelaySeconds);
            return;
        }

        CheckpointAnchor = result.value;
        Debug.Log($"[LevelGenerator] Checkpoint anchor created at {pose.position}, yaw {pose.rotation.eulerAngles.y:F0}°");

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
        // After a scan, the training round first (at full size, then compressed); if it can't fit, play on without it.
        if (_trainingPending && trainingStage != null && trainingStage.GetComponent<StageFootprint>() != null)
            foreach (var scale in campaignScales) candidates.Add((trainingStage, scale));
        var campaign = CampaignStageFor(campaignStages, gs != null ? gs.StageIndex : 1);
        if (campaign != null && campaign.GetComponent<StageFootprint>() != null)
            foreach (var scale in campaignScales) candidates.Add((campaign, scale));
        foreach (var t in stageTemplates
                     .Where(t => t != null && t.GetComponent<StageFootprint>() != null)
                     .OrderByDescending(t => t.GetComponent<StageFootprint>().FootprintArea))
            candidates.Add((t, 1f));

        // Take the first (most preferred) layout that fits with a comfortable margin; otherwise
        // the best-centred fit of any of them (ties go to the more preferred).
        (GameObject template, float scale, Pose pose, float margin)? best = null;
        foreach (var (template, scale) in candidates)
        {
            var footprint = template.GetComponent<StageFootprint>();
            _searchScale = scale;
            bool found = TryFindValidPlacement(footprint, out var pose, out float margin);
            _searchScale = 1f;
            if (!found) continue;
            Debug.Log($"[LevelGenerator] '{template.name}' at {scale:P0} fits with {margin:F1} m clear all round.");
            if (best == null || margin > best.Value.margin + 0.01f) best = (template, scale, pose, margin);
            if (margin >= preferredMargin) break;
        }
        if (best is not { } chosen) return false;

        if (chosen.template != trainingStage) _trainingPending = false;
        CurrentStage = Instantiate(chosen.template, chosen.pose.position, chosen.pose.rotation, CheckpointAnchor.transform);
        if (!Mathf.Approximately(chosen.scale, 1f)) CompressLayout(CurrentStage, chosen.scale);
        LastPlacementMargin = chosen.margin;
        Debug.Log($"[LevelGenerator] Placed '{chosen.template.name}'{(Mathf.Approximately(chosen.scale, 1f) ? "" : $" at {chosen.scale:P0} size")} at {chosen.pose.position}, yaw {chosen.pose.rotation.eulerAngles.y:F0}° (facing away from the player), " +
                  $"{chosen.margin:F1} m clear floor all round — skipped {SearchSummary()} ({MeshChunkCount} mesh chunks).");
        StartStage();
        return true;
    }

    /// <summary>The clear floor margin (metres, capped at centringMargin) round the last stage placed.</summary>
    public float LastPlacementMargin { get; private set; }

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

    /// <summary>
    /// The best-centred valid placement for the footprint at the scale being searched: every
    /// candidate on the search rings that fits (facing away from the player) is scored by its clear
    /// floor margin, plus a bonus in the player's forward arc and a small penalty for distance; the
    /// winner is then nudged to centre it further. <paramref name="margin"/> is its clear margin.
    /// </summary>
    private bool TryFindValidPlacement(StageFootprint footprint, out Pose pose, out float margin)
    {
        var anchorTransform = CheckpointAnchor.transform;
        float floorY = anchorTransform.position.y;
        var player = arCamera.transform.position;
        var facing = Vector3.ProjectOnPlane(arCamera.transform.forward, Vector3.up);
        if (facing.sqrMagnitude < 1e-4f) facing = anchorTransform.forward;

        pose = default;
        margin = 0f;
        float bestScore = float.NegativeInfinity;
        for (float angle = 0f; angle < 360f; angle += angleStep)
        {
            var direction = Quaternion.Euler(0f, angle, 0f) * anchorTransform.forward;
            foreach (var distance in searchDistances)
            {
                if (!TryGetFloorProjectedPose(anchorTransform.position + direction.normalized * distance, out var floorPose, floorY))
                {
                    _searchNoFloor++;
                    continue;
                }
                if (Score(centringMargin, floorPose.position, player, facing) <= bestScore) continue; // can't beat the best even fully centred
                if (!IsValidPlacement(footprint, floorPose.position, floorY, out var rotation)) continue;
                float m = ClearMargin(footprint, floorPose.position, rotation, floorY);
                float score = Score(m, floorPose.position, player, facing);
                if (score <= bestScore) continue;
                bestScore = score;
                pose = new Pose(floorPose.position, rotation);
                margin = m;
            }
        }
        if (float.IsNegativeInfinity(bestScore)) return false;

        // Nudge it sideways and along its length while that centres it more (the rings are coarse).
        foreach (float step in new[] { 0.3f, 0.15f })
        {
            for (int i = 0; i < 8; i++)
            {
                bool moved = false;
                foreach (var nudge in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
                {
                    var p = pose.position + pose.rotation * (nudge * step);
                    if (!TryGetFloorProjectedPose(p, out var floorPose, floorY)) continue;
                    if (!IsValidPlacement(footprint, floorPose.position, floorY, out var rotation, countRejections: false)) continue;
                    float m = ClearMargin(footprint, floorPose.position, rotation, floorY);
                    if (m <= margin + 0.001f) continue;
                    pose = new Pose(floorPose.position, rotation);
                    margin = m;
                    moved = true;
                }
                if (!moved) break;
            }
        }
        return true;
    }

    private float Score(float margin, Vector3 position, Vector3 player, Vector3 facing)
    {
        var toSpot = position - player;
        toSpot.y = 0f;
        bool ahead = Vector3.Angle(facing, toSpot) <= forwardArcHalfAngle;
        return margin + (ahead ? forwardBonus : 0f) - distancePenalty * toSpot.magnitude;
    }

    /// <summary>The stage at a floor point faces straight away from the player: its start nearest them, its goal on the far side.</summary>
    private Quaternion FacingAwayFromPlayer(Vector3 floorPosition)
    {
        var away = floorPosition - arCamera.transform.position;
        away.y = 0f;
        if (away.sqrMagnitude < 1e-4f) away = Vector3.ProjectOnPlane(CheckpointAnchor.transform.forward, Vector3.up);
        return Quaternion.LookRotation(away.normalized, Vector3.up);
    }

    /// <summary>Faces the stage away from the player and checks it fits there: clear of the player, on detected floor, clear of the room mesh.</summary>
    private bool IsValidPlacement(StageFootprint footprint, Vector3 floorPosition, float floorY, out Quaternion rotation, bool countRejections = true)
    {
        rotation = FacingAwayFromPlayer(floorPosition);
        if (!KeepsClearOfPlayer(footprint, floorPosition, rotation))
        {
            if (countRejections) _searchTooClose++;
            return false;
        }
        if (!IsFullyOnFloor(footprint, floorPosition, rotation, floorY))
        {
            if (countRejections) _searchOffFloor++;
            return false;
        }
        return IsPlacementClear(footprint, floorPosition, rotation, countRejections);
    }

    /// <summary>
    /// How much clear floor surrounds the footprint (metres, in centringStep steps up to
    /// centringMargin): the footprint grown by that much still has detected floor all along its
    /// edges and, with LiDAR, nothing of the room mesh inside it. A stage with equal margins on
    /// both sides scores highest, so maximising this centres it in the scanned space.
    /// </summary>
    private float ClearMargin(StageFootprint footprint, Vector3 floorPosition, Quaternion rotation, float floorY)
    {
        float clear = 0f;
        for (float m = centringStep; m <= centringMargin + 0.001f; m += centringStep)
        {
            float hx = footprint.width * _searchScale * 0.5f + m, hz = footprint.depth * _searchScale * 0.5f + m;
            if (!EdgeOnFloor(floorPosition, rotation, hx, hz, floorY)) break;
            var halfExtents = SearchScaled(footprint.HalfExtents) + new Vector3(m, 0f, m);
            if (!BoxClear(footprint, floorPosition, rotation, halfExtents)) break;
            clear = m;
        }
        return clear;
    }

    /// <summary>Detected floor at points every ~0.5 m round a rectangle (half-sizes hx, hz) centred at floorPosition.</summary>
    private bool EdgeOnFloor(Vector3 floorPosition, Quaternion rotation, float hx, float hz, float floorY)
    {
        int nx = Mathf.Max(2, Mathf.CeilToInt(hx * 2f / 0.5f) + 1), nz = Mathf.Max(2, Mathf.CeilToInt(hz * 2f / 0.5f) + 1);
        for (int i = 0; i < nx; i++)
        {
            float x = Mathf.Lerp(-hx, hx, i / (nx - 1f));
            if (!TryGetFloorProjectedPose(floorPosition + rotation * new Vector3(x, 0f, -hz), out _, floorY)) return false;
            if (!TryGetFloorProjectedPose(floorPosition + rotation * new Vector3(x, 0f, hz), out _, floorY)) return false;
        }
        for (int i = 1; i < nz - 1; i++)
        {
            float z = Mathf.Lerp(-hz, hz, i / (nz - 1f));
            if (!TryGetFloorProjectedPose(floorPosition + rotation * new Vector3(-hx, 0f, z), out _, floorY)) return false;
            if (!TryGetFloorProjectedPose(floorPosition + rotation * new Vector3(hx, 0f, z), out _, floorY)) return false;
        }
        return true;
    }

    // ---- Step 6: stage loop ----

    private void StartStage()
    {
        var gs = GameStateManager.Instance;
        if (InTraining && gs != null) gs.StartTraining();
        CurrentGoal = CurrentStage.GetComponentInChildren<StageGoal>(true); // training: its flag is hidden until the last step
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
        if (InTraining)
        {
            // Training done: no clear bonus or stage count; a fresh game starts at 1-1.
            _trainingPending = false;
            var trainee = GameStateManager.Instance;
            if (trainee != null) trainee.EndTraining();
            AudioManager.Play(Sfx.StageClear);
            Debug.Log("[LevelGenerator] Training complete — 1-1 next.");
            TrainingCompleted?.Invoke();
            StartCoroutine(TransitionToNextStage());
            return;
        }
        StagesCompleted++;
        Debug.Log($"[LevelGenerator] Stage complete ({StagesCompleted} so far) — next stage in {transitionDelaySeconds:F1}s.");
        // The last campaign stage ends the game, even if a fallback template stood in for it.
        var gs = GameStateManager.Instance;
        bool final = goal.EndsGame || (campaignStages != null && campaignStages.Count > 0 && gs != null && gs.StageIndex >= campaignStages.Count);
        OnStageCompleted?.Invoke();
        if (final)
        {
            // The final stage: the game is won. Keep the stage up for the celebration, then clear it.
            Debug.Log("[LevelGenerator] Final stage cleared — GAME CLEAR.");
            if (gs != null) gs.ClearGame();
            StartCoroutine(FinishAfterCelebration());
            return;
        }
        StartCoroutine(TransitionToNextStage());
    }

    private IEnumerator FinishAfterCelebration()
    {
        yield return new WaitForSeconds(finalCelebrationSeconds);
        _halted = true;
        IsTransitioning = false;
        TearDownStage();
    }

    private IEnumerator TransitionToNextStage()
    {
        yield return new WaitForSeconds(transitionDelaySeconds); // the flag slides down, confetti

        // Iris out; while it's black, clear the stage and re-anchor at the player's current
        // pose (then Step 3 again); iris in once the next stage is placed (or the wipe's time limit).
        ScreenTransition.Run(() =>
        {
            TearDownStage();
            IsTransitioning = false;
            CreateCheckpointAnchorAsync();
        }, () => CurrentStage != null || _halted);
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
        _warping = false;
        StopAllCoroutines();
        CancelInvoke();
        IsTransitioning = false;
        IsWaitingForMesh = false;
        TearDownStage();
        Debug.Log("[LevelGenerator] Game over — loop halted.");
    }

    /// <summary>Hard reset (HardReset): clears the stage and waits, like at launch, for the next scan to complete (HandleScanComplete).</summary>
    public void ResetToScan()
    {
        StopAllCoroutines();
        CancelInvoke();
        _generation++;
        _trainingPending = true; // a new player: training again after the scan
        _halted = false;
        _warping = false;
        _creatingAnchor = false;
        IsTransitioning = false;
        IsWaitingForMesh = false;
        StagesCompleted = 0;
        TearDownStage();
        Debug.Log("[LevelGenerator] Hard reset — waiting for a new scan.");
    }

    /// <summary>Wired to GameStateManager.OnGameRestarted: re-anchors at the player and places a fresh stage.</summary>
    public void RestartLoop()
    {
        _trainingPending = false; // after a game over they know the moves
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

    private bool IsPlacementClear(StageFootprint footprint, Vector3 floorPosition, Quaternion rotation, bool countRejections = true)
    {
        if (BoxClear(footprint, floorPosition, rotation, SearchScaled(footprint.HalfExtents))) return true;
        if (!countRejections) return false;
        _searchObstructed++;
        TotalObstructedCandidates++;
        LastObstructionName = s_Overlap[0].name;
        return false;
    }

    /// <summary>No room mesh (LiDAR) inside the box: lifted off the floor by floorClearanceMargin (keeping
    /// the same top), so the floor's own mesh triangles don't register as obstructions.</summary>
    private bool BoxClear(StageFootprint footprint, Vector3 floorPosition, Quaternion rotation, Vector3 halfExtents)
    {
        float lift = Mathf.Min(floorClearanceMargin, footprint.clearance * 0.5f);
        halfExtents.y -= lift * 0.5f;
        var boxCenter = floorPosition + Vector3.up * (lift + halfExtents.y);
        return Physics.OverlapBoxNonAlloc(boxCenter, halfExtents, s_Overlap, rotation, obstructionLayerMask, QueryTriggerInteraction.Ignore) == 0;
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
