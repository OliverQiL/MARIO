using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Safety in AR: when the player's head is in or right at a game object (a
/// bush, a block, a pipe, a wall, scenery), that object turns see-through, so
/// the real room behind it stays visible. Each frame every renderer of the
/// live stage (and the bonus room) whose bounds come within radius of the
/// camera swaps to a shared transparent ghost material, tinted with its own
/// colour and texture at opacity; it swaps back when the head leaves.
/// Enemies (Goombas, Koopas, Bowser, Bullet Bills) are never faded: they're
/// threats the player must keep seeing. Logic is in Tick so tests can drive it.
/// </summary>
public class ProximityFade : MonoBehaviour
{
    [SerializeField] private Camera arCamera;
    [SerializeField] private LevelGenerator levelGenerator;
    [Tooltip("A transparent material (URP Unlit, alpha blended); tinted per object.")]
    [SerializeField] private Material ghostMaterial;
    [Tooltip("An object turns see-through when the head is within this distance (metres) of its bounds, or inside it.")]
    [SerializeField] private float radius = 0.25f;
    [Range(0f, 1f)] [SerializeField] private float opacity = 0.3f;

    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");

    private readonly Dictionary<Renderer, Material[]> _ghosted = new();
    private readonly List<Renderer> _candidates = new();
    private readonly List<Renderer> _extra = new(); // added with Consider, kept across refreshes
    private readonly List<Renderer> _released = new();
    private GameObject _cachedStage, _cachedRoom;
    private int _cachedCount = -1;
    private MaterialPropertyBlock _block;

    public int GhostedCount => _ghosted.Count;
    public bool IsGhosted(Renderer r) => r != null && _ghosted.ContainsKey(r);

    private void Awake()
    {
        if (arCamera == null) arCamera = Camera.main;
        if (levelGenerator == null) levelGenerator = FindAnyObjectByType<LevelGenerator>();
    }

    private void LateUpdate()
    {
        if (arCamera != null) Tick(arCamera.transform.position);
    }

    public void Tick(Vector3 eye)
    {
        RefreshCandidates();
        float r2 = radius * radius;
        foreach (var r in _candidates)
        {
            if (r == null || !r.gameObject.activeInHierarchy) continue;
            bool near = r.bounds.SqrDistance(eye) <= r2;
            if (near && !_ghosted.ContainsKey(r)) Ghost(r);
        }

        _released.Clear();
        foreach (var kv in _ghosted)
            if (kv.Key == null || !kv.Key.gameObject.activeInHierarchy || kv.Key.bounds.SqrDistance(eye) > r2) _released.Add(kv.Key);
        foreach (var r in _released) Restore(r);
    }

    /// <summary>Restores everything (stage teardown, disabling).</summary>
    public void RestoreAll()
    {
        _released.Clear();
        _released.AddRange(_ghosted.Keys);
        foreach (var r in _released) Restore(r);
    }

    private void OnDisable() => RestoreAll();

    private void RefreshCandidates()
    {
        var stage = levelGenerator != null ? levelGenerator.CurrentStage : null;
        var room = levelGenerator != null ? levelGenerator.BonusRoom : null;
        // Re-gather when the stage, the room or what's in them changes (training rounds spawn copies).
        int count = (stage != null ? stage.transform.hierarchyCount : 0) + (room != null ? room.transform.hierarchyCount : 0);
        if (stage == _cachedStage && room == _cachedRoom && count == _cachedCount) return;
        _cachedStage = stage;
        _cachedRoom = room;
        _cachedCount = count;
        _candidates.Clear();
        _candidates.AddRange(_extra);
        foreach (var root in new[] { stage, room })
        {
            if (root == null) continue;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                if (!IsEnemy(r)) _candidates.Add(r);
        }
    }

    /// <summary>Registers extra objects to consider (tests, or anything outside the stage).</summary>
    public void Consider(GameObject root)
    {
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (IsEnemy(r) || _extra.Contains(r)) continue;
            _extra.Add(r);
            _candidates.Add(r);
        }
    }

    private static bool IsEnemy(Renderer r) =>
        r.GetComponentInParent<Goomba>(true) != null || r.GetComponentInParent<Bowser>(true) != null || r.GetComponentInParent<BulletBill>(true) != null;

    private void Ghost(Renderer r)
    {
        if (ghostMaterial == null || r.HasPropertyBlock()) return; // already customised (e.g. a tiled floor): leave it alone
        var originals = r.sharedMaterials;
        _ghosted[r] = originals;
        var ghosts = new Material[originals.Length];
        for (int i = 0; i < ghosts.Length; i++) ghosts[i] = ghostMaterial;
        r.sharedMaterials = ghosts;

        var source = originals.Length > 0 ? originals[0] : null;
        var tint = source != null && source.HasProperty(BaseColor) ? source.GetColor(BaseColor) : Color.white;
        _block ??= new MaterialPropertyBlock();
        r.GetPropertyBlock(_block);
        _block.SetColor(BaseColor, new Color(tint.r, tint.g, tint.b, opacity));
        var texture = source != null && source.HasProperty(BaseMap) ? source.GetTexture(BaseMap) : null;
        if (texture != null) _block.SetTexture(BaseMap, texture);
        r.SetPropertyBlock(_block);
    }

    private void Restore(Renderer r)
    {
        if (_ghosted.TryGetValue(r, out var originals) && r != null)
        {
            r.sharedMaterials = originals;
            r.SetPropertyBlock(null);
        }
        _ghosted.Remove(r);
    }
}
