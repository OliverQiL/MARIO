using UnityEngine;

/// <summary>
/// A ring on the floor round the player's feet at stomp/clap reach, so they
/// can see when a stomp or clap will register:
///
/// - a faint full circle at the reach (EventResolver's stomp range), and a
///   brighter arc in front of the player for the zone a stomp actually covers
///   (±stomp half-angle of where they face); both turn with the player;
/// - green, pulsing, when a stomp would hit something right now;
/// - gold, pulsing, when there's a shell a clap would pick up;
/// - otherwise a dim white.
///
/// "Would register" is EventResolver.TargetInReach, the same targeting as
/// the gate chain. Shown while a stage is live. Its meshes are built at
/// runtime from the resolver's ranges; the material (an opaque, emissive
/// one) comes from GameContentSetup. Logic is in Tick so tests can drive it.
/// </summary>
public class RangeRing : MonoBehaviour
{
    public enum State { Hidden, Idle, Stomp, Clap }

    [SerializeField] private EventResolver eventResolver;
    [SerializeField] private Camera arCamera;
    [SerializeField] private ARSessionManager sessionManager;
    [SerializeField] private Material material;
    [SerializeField] private float lineWidth = 0.035f;
    [SerializeField] private Color idleRing = new(1f, 1f, 1f, 1f);
    [SerializeField] private float idleBrightness = 0.25f;
    [SerializeField] private Color stompColor = new(0.3f, 1f, 0.4f);
    [SerializeField] private Color clapColor = new(1f, 0.8f, 0.15f);

    public State Current { get; private set; } = State.Hidden;

    private Transform _root;
    private Renderer _ring, _arc;
    private MaterialPropertyBlock _block;
    private float _builtRadius = -1f, _builtHalfAngle = -1f, _clock;

    private void Awake()
    {
        if (arCamera == null) arCamera = Camera.main;
        if (eventResolver == null) eventResolver = FindAnyObjectByType<EventResolver>();
        if (sessionManager == null) sessionManager = FindAnyObjectByType<ARSessionManager>();
    }

    private void Update() => Tick(Time.deltaTime);

    public void Tick(float dt)
    {
        _clock += dt;
        if (arCamera == null || eventResolver == null) { Show(State.Hidden); return; }
        var gs = GameStateManager.Instance;
        bool live = gs == null || (gs.IsStageActive && !gs.IsFinished);
        if (!live) { Show(State.Hidden); return; }

        Build(eventResolver.StompRange, eventResolver.StompHalfAngle);

        // Follow the player's feet, turned the way they face.
        var cam = arCamera.transform;
        float floorY = FloorY(cam.position.y);
        var facing = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
        if (facing.sqrMagnitude < 1e-4f) facing = Vector3.ProjectOnPlane(cam.up, Vector3.up); // looking straight down
        _root.SetPositionAndRotation(new Vector3(cam.position.x, floorY + 0.012f, cam.position.z),
                                     Quaternion.LookRotation(facing.normalized, Vector3.up));

        var state = eventResolver.TargetInReach("clap") != null ? State.Clap
                  : eventResolver.TargetInReach("stomp") != null ? State.Stomp
                  : State.Idle;
        Show(state);
    }

    private float FloorY(float eyeY)
    {
        if (sessionManager != null && sessionManager.FloorHeight is { } floor) return floor;
        return eyeY - PlayerBody.EyeHeightOr(1.55f);
    }

    private void Show(State state)
    {
        Current = state;
        if (_root == null) return;
        bool visible = state != State.Hidden;
        if (_root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        if (!visible) return;

        float pulse = 0.75f + 0.25f * Mathf.Sin(_clock * 9f);
        Color ring, arc;
        switch (state)
        {
            case State.Stomp: ring = stompColor * 0.8f; arc = stompColor * (0.8f + 0.6f * pulse); break;
            case State.Clap: ring = clapColor * 0.8f; arc = clapColor * (0.8f + 0.6f * pulse); break;
            default: ring = idleRing * idleBrightness; arc = idleRing * (idleBrightness * 2.2f); break;
        }
        Paint(_ring, ring);
        Paint(_arc, arc);
    }

    private void Paint(Renderer r, Color c)
    {
        _block ??= new MaterialPropertyBlock();
        r.GetPropertyBlock(_block);
        _block.SetColor("_BaseColor", new Color(c.r, c.g, c.b, 1f));
        _block.SetColor("_EmissionColor", new Color(c.r, c.g, c.b, 1f) * 0.8f);
        r.SetPropertyBlock(_block);
    }

    /// <summary>(Re)builds the ring and arc meshes for the reach and half-angle.</summary>
    private void Build(float radius, float halfAngle)
    {
        if (_root != null && Mathf.Approximately(radius, _builtRadius) && Mathf.Approximately(halfAngle, _builtHalfAngle)) return;
        if (_root == null)
        {
            _root = new GameObject("RangeRingVisual").transform;
            _ring = Part("Ring");
            _arc = Part("FrontArc");
        }
        _ring.GetComponent<MeshFilter>().sharedMesh = Annulus(radius - lineWidth * 0.5f, radius + lineWidth * 0.5f, -180f, 180f, 96);
        _arc.GetComponent<MeshFilter>().sharedMesh = Annulus(radius - lineWidth * 1.5f, radius + lineWidth * 0.5f, -halfAngle, halfAngle, 48, 0.002f);
        _builtRadius = radius;
        _builtHalfAngle = halfAngle;
    }

    private Renderer Part(string name)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(_root, false);
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return r;
    }

    /// <summary>A flat ring segment in the XZ plane facing up, from angle a0 to a1 (degrees, 0 = +Z).</summary>
    private static Mesh Annulus(float inner, float outer, float a0, float a1, int segments, float lift = 0f)
    {
        var verts = new Vector3[(segments + 1) * 2];
        var tris = new int[segments * 6];
        for (int i = 0; i <= segments; i++)
        {
            float a = Mathf.Deg2Rad * Mathf.Lerp(a0, a1, i / (float)segments);
            var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            verts[i * 2] = dir * inner + Vector3.up * lift;
            verts[i * 2 + 1] = dir * outer + Vector3.up * lift;
            if (i == segments) break;
            int v = i * 2, t = i * 6;
            tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 3;
            tris[t + 3] = v; tris[t + 4] = v + 3; tris[t + 5] = v + 2;
        }
        var mesh = new Mesh { name = "RangeRing" };
        mesh.vertices = verts;
        mesh.triangles = tris;
        var normals = new Vector3[verts.Length];
        for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
        mesh.normals = normals;
        mesh.RecalculateBounds();
        // Seen from above: make sure the winding faces up.
        if (Vector3.Dot(Vector3.Cross(verts[1] - verts[0], verts[3] - verts[0]), Vector3.up) < 0f)
        {
            for (int t = 0; t < tris.Length; t += 3) (tris[t + 1], tris[t + 2]) = (tris[t + 2], tris[t + 1]);
            mesh.triangles = tris;
        }
        return mesh;
    }

    private void OnDisable()
    {
        if (_root != null) _root.gameObject.SetActive(false);
    }
}
