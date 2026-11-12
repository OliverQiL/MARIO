using UnityEngine;

/// <summary>
/// Helps the player find what to go to (Oliver 2026-11-12: new players lost
/// time finding the training targets and the stage, which can appear to the
/// side or behind them after the scan). Gold, to tell it from the red enemy
/// arrows (EnemyIndicator):
///
/// - Marker: in training, a gold arrow bobs above the current objective
///   (Tutorial.Objective: the ? block, the brick, the Koopa's shell, the
///   cannon, the flag), pointing down at it and turned to face the player.
///   A walking Goomba or Koopa has the red enemy arrows already, so it gets
///   no gold ones (Oliver 2026-11-12); a shell isn't walking, so it does.
/// - Floor arrow: in training, a gold arrow on the floor floorDistance from
///   the player towards the objective, until they're within nearHide of it.
/// - Edge pointer: whenever the objective is more than inViewDegrees off the
///   view (or behind), a gold arrow at the edge of the view points to it, and
///   <see cref="LookHint"/> says which way ("TURN AROUND", "LOOK LEFT", ...)
///   for the HUD banner.
/// - A new stage: with no training objective (the campaign, or the training
///   intro card), the objective is the stage itself until it has been in view
///   once (<see cref="StageUnseen"/>); from then on the enemy arrows and the
///   flag distance guide the player.
///
/// Arrows are instances of arrowPrefab (built by GameContentSetup: flat, +Z the
/// way it points, +Y its face). Logic is in Tick so headless tests can drive it.
/// </summary>
public class ObjectiveIndicator : MonoBehaviour
{
    [SerializeField] private GameObject arrowPrefab;
    [SerializeField] private Camera arCamera;
    [SerializeField] private LevelGenerator levelGenerator;
    [Tooltip("The objective counts as in view within this (degrees) of the view direction.")]
    [SerializeField] private float inViewDegrees = 25f;
    [Tooltip("A new stage counts as seen once its centre is within this (degrees) of the view, or the player is on it.")]
    [SerializeField] private float stageSeenDegrees = 35f;
    [Tooltip("How far from the player (metres) the floor arrow lies, towards the objective.")]
    [SerializeField] private float floorDistance = 0.9f;
    [Tooltip("Closer than this (metres, on the floor) the floor arrow goes: the objective is right there.")]
    [SerializeField] private float nearHide = 0.8f;
    [Tooltip("The marker floats this far (metres) above the top of the objective.")]
    [SerializeField] private float markerHeight = 0.12f;
    [SerializeField] private float markerScale = 0.9f;
    [Tooltip("Edge pointer placement in front of the camera (metres) and its offset from the centre of the view (further out than the enemy pointer).")]
    [SerializeField] private float pointerDistance = 0.6f;
    [SerializeField] private float pointerRadius = 0.21f;
    [SerializeField] private float pointerScale = 0.5f;

    /// <summary>The live indicator (the HUD reads <see cref="LookHint"/>).</summary>
    public static ObjectiveIndicator Active { get; private set; }

    /// <summary>What it's pointing at: the training objective, or the unseen stage.</summary>
    public GameObject Target { get; private set; }
    /// <summary>The objective is out of view: "TURN AROUND", "LOOK LEFT", "LOOK RIGHT", "LOOK UP", "LOOK DOWN"; else null.</summary>
    public string LookHint { get; private set; }
    /// <summary>A campaign stage is up but hasn't been in view yet.</summary>
    public bool StageUnseen { get; private set; }
    public bool MarkerShown => _marker != null && _marker.activeSelf;
    public bool FloorArrowShown => _floor != null && _floor.activeSelf;
    public bool PointerShown => _pointer != null && _pointer.activeSelf;
    public Vector3 PointerLocalPosition => _pointer != null ? _pointer.transform.localPosition : Vector3.zero;
    public Transform Marker => _marker != null ? _marker.transform : null;
    public Transform FloorArrow => _floor != null ? _floor.transform : null;

    private GameObject _marker, _floor, _pointer;
    private GameObject _seenStage;
    private float _clock;

    private void Awake()
    {
        if (arCamera == null) arCamera = Camera.main;
        if (levelGenerator == null) levelGenerator = FindAnyObjectByType<LevelGenerator>();
    }

    private void OnEnable() => Active = this;

    private void OnDisable()
    {
        if (Active == this) Active = null;
        Show(_marker, false);
        Show(_floor, false);
        Show(_pointer, false);
        LookHint = null;
    }

    private void Update() => Tick(Time.deltaTime);

    public void Tick(float dt)
    {
        _clock += dt;
        if (arCamera == null || arrowPrefab == null) return;
        var cam = arCamera.transform;
        var eye = cam.position;

        GameObject target = null;
        bool training = false;
        StageUnseen = false;
        var gs = GameStateManager.Instance;
        bool live = gs == null || !gs.IsFinished;
        var stage = levelGenerator != null ? levelGenerator.CurrentStage : null;
        bool busy = levelGenerator != null && (levelGenerator.IsTransitioning || levelGenerator.InBonusRoom);

        if (live && !busy && Tutorial.Active is { } tutorial && tutorial.Objective is { } objective && !HasEnemyArrow(objective))
        {
            target = objective;
            training = true;
        }
        else if (live && !busy && stage != null && stage.activeInHierarchy)
        {
            if (_seenStage != stage && StageInView(stage, eye, cam)) _seenStage = stage;
            if (_seenStage != stage)
            {
                StageUnseen = true;
                target = stage;
            }
        }
        Target = target;

        // Where to point: the top of the objective (its renderers), or the stage's centre.
        Vector3 point = Vector3.zero, top = Vector3.zero;
        if (target != null)
        {
            if (training && TopOf(target, out var bounds)) { point = bounds.center; top = new Vector3(bounds.center.x, bounds.max.y, bounds.center.z); }
            else { point = target.transform.position; top = point; }
        }

        // Marker above the training objective, bobbing, pointing down and facing the player.
        if (_marker == null) _marker = Spawn(null, "ObjectiveMarker");
        bool marker = training;
        if (marker)
        {
            var toEye = Flat(eye - top);
            if (toEye.sqrMagnitude < 1e-4f) toEye = Vector3.forward;
            float bob = 0.05f * Mathf.Sin(_clock * 4f);
            _marker.transform.SetPositionAndRotation(top + Vector3.up * (markerHeight + 0.15f * markerScale + bob),
                                                     Quaternion.LookRotation(Vector3.down, toEye.normalized));
            _marker.transform.localScale = Vector3.one * markerScale;
        }
        Show(_marker, marker);

        // Floor arrow towards the training objective.
        if (_floor == null) _floor = Spawn(null, "ObjectiveArrow");
        bool floor = false;
        if (training)
        {
            var flat = Flat(point - eye);
            float distance = flat.magnitude;
            if (distance > nearHide)
            {
                var direction = flat / distance;
                var at = eye + direction * Mathf.Min(floorDistance, distance - 0.4f);
                float floorY = Tutorial.Active != null ? Tutorial.Active.transform.position.y : target.transform.position.y; // the training lane's floor
                _floor.transform.SetPositionAndRotation(new Vector3(at.x, floorY + 0.012f, at.z), Quaternion.LookRotation(direction, Vector3.up));
                _floor.transform.localScale = Vector3.one * (1.1f + 0.12f * Mathf.Sin(_clock * 5f));
                floor = true;
            }
        }
        Show(_floor, floor);

        // Edge pointer and the banner hint when it's out of view.
        if (_pointer == null) _pointer = Spawn(cam, "ObjectivePointer");
        LookHint = null;
        bool pointer = false;
        if (target != null)
        {
            var local = cam.InverseTransformPoint(point);
            if (Vector3.Angle(Vector3.forward, local) > inViewDegrees && Flat(point - eye).magnitude > 0.3f)
            {
                var v = new Vector2(local.x, local.y);
                bool behind = local.z < 0f;
                if (behind || v.sqrMagnitude < 1e-4f) v = new Vector2(local.x >= 0f ? 1f : -1f, 0f);
                v.Normalize();
                _pointer.transform.localPosition = new Vector3(v.x * pointerRadius, v.y * pointerRadius, pointerDistance);
                _pointer.transform.localRotation = Quaternion.LookRotation(new Vector3(v.x, v.y, 0f), Vector3.back);
                _pointer.transform.localScale = Vector3.one * pointerScale * (1f + 0.12f * Mathf.Sin(_clock * 8f));
                pointer = true;

                // The horizontal angle decides between turning round and looking to a side.
                var facing = Flat(cam.forward).sqrMagnitude > 1e-4f ? Flat(cam.forward) : Flat(cam.up);
                float bearing = Vector3.SignedAngle(facing, Flat(point - eye), Vector3.up);
                if (Mathf.Abs(bearing) > 110f) LookHint = "TURN AROUND";
                else if (Mathf.Abs(bearing) > inViewDegrees) LookHint = bearing > 0f ? "LOOK RIGHT" : "LOOK LEFT";
                else LookHint = local.y > 0f ? "LOOK UP" : "LOOK DOWN";
            }
        }
        Show(_pointer, pointer);
    }

    /// <summary>A walking Goomba or Koopa already gets the red enemy arrows (EnemyIndicator): no gold ones as well.</summary>
    private static bool HasEnemyArrow(GameObject objective) =>
        objective.TryGetComponent<Goomba>(out var enemy) && enemy.IsMoving;

    private bool StageInView(GameObject stage, Vector3 eye, Transform cam)
    {
        var centre = stage.transform.position;
        var footprint = stage.GetComponent<StageFootprint>();
        if (footprint != null)
        {
            var local = stage.transform.InverseTransformPoint(eye);
            var half = footprint.HalfExtents;
            if (Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.z) <= half.z) return true; // standing on it
        }
        var flatForward = Flat(cam.forward);
        if (flatForward.sqrMagnitude < 1e-4f) return false;
        return Vector3.Angle(flatForward, Flat(centre - eye)) <= stageSeenDegrees;
    }

    private static bool TopOf(GameObject target, out Bounds bounds)
    {
        bounds = default;
        bool any = false;
        foreach (var r in target.GetComponentsInChildren<Renderer>())
        {
            if (!r.enabled || r is ParticleSystemRenderer) continue;
            if (!any) { bounds = r.bounds; any = true; }
            else bounds.Encapsulate(r.bounds);
        }
        return any;
    }

    private GameObject Spawn(Transform parent, string name)
    {
        var go = Instantiate(arrowPrefab, parent, false);
        go.name = name;
        go.SetActive(false);
        return go;
    }

    private static void Show(GameObject go, bool show)
    {
        if (go != null && go.activeSelf != show) go.SetActive(show);
    }

    private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);
}
