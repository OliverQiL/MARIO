using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Shows where walking enemies are coming from, since one approaching from
/// the side or behind is easy to miss in the headset:
///
/// - Floor arrows: for each of the nearest maxArrows walking enemies
///   (Goombas, Koopas not in their shell) within range, a red arrow lies on
///   the floor floorDistance from the player in its direction, pointing at it.
///   They pulse faster as the enemy closes in and go once it's at the
///   player's feet (stomp range), where it can be seen.
/// - Edge pointer: when the nearest one is outside the view (more than
///   inViewDegrees off the view direction, or behind), a small red arrow near
///   the edge of the view points towards it. It's a child of the AR camera,
///   so it's in both headset eyes.
///
/// The arrows are instances of arrowPrefab (built by GameContentSetup:
/// flat, +Z = the way it points). Logic is in Tick so headless tests can drive it.
/// </summary>
public class EnemyIndicator : MonoBehaviour
{
    [SerializeField] private GameObject arrowPrefab;
    [SerializeField] private Camera arCamera;
    [SerializeField] private int maxArrows = 3;
    [Tooltip("Enemies further than this (metres) get no arrow.")]
    [SerializeField] private float range = 8f;
    [Tooltip("Enemies closer than this (metres) get no arrow: they're at the player's feet.")]
    [SerializeField] private float nearHide = 0.7f;
    [Tooltip("How far from the player (metres) the floor arrow lies, towards the enemy.")]
    [SerializeField] private float floorDistance = 1.2f;
    [Tooltip("The edge pointer shows when the nearest enemy is more than this (degrees) off the view direction.")]
    [SerializeField] private float inViewDegrees = 25f;
    [Tooltip("Edge pointer placement in front of the camera (metres) and its offset from the centre of the view.")]
    [SerializeField] private float pointerDistance = 0.6f;
    [SerializeField] private float pointerRadius = 0.15f;
    [SerializeField] private float pointerScale = 0.45f;

    private readonly List<GameObject> _arrows = new();
    private GameObject _pointer;
    private float _clock;

    /// <summary>How many floor arrows are showing; whether the edge pointer is (and its camera-local position).</summary>
    public int ShownArrows => _arrows.Count(a => a != null && a.activeSelf);
    public IReadOnlyList<GameObject> Arrows => _arrows;
    public bool PointerShown => _pointer != null && _pointer.activeSelf;
    public Vector3 PointerLocalPosition => _pointer != null ? _pointer.transform.localPosition : Vector3.zero;

    private void Awake()
    {
        if (arCamera == null) arCamera = Camera.main;
    }

    private void Update() => Tick(Time.deltaTime);

    public void Tick(float dt)
    {
        _clock += dt;
        if (arCamera == null || arrowPrefab == null) return;
        var gs = GameStateManager.Instance;
        bool live = gs == null || (gs.IsStageActive && !gs.IsFinished);

        var cam = arCamera.transform;
        var player = cam.position;
        var enemies = live
            ? InteractableBase.All.OfType<Goomba>()
                .Where(g => g != null && g.isActiveAndEnabled && g.IsMoving)
                .Select(g => (enemy: g, distance: Flat(g.transform.position - player).magnitude))
                .Where(e => e.distance > nearHide && e.distance < range)
                .OrderBy(e => e.distance)
                .Take(maxArrows)
                .ToList()
            : new List<(Goomba enemy, float distance)>();

        // Floor arrows.
        while (_arrows.Count < enemies.Count) _arrows.Add(Spawn(null, "EnemyArrow"));
        for (int i = 0; i < _arrows.Count; i++)
        {
            var arrow = _arrows[i];
            bool show = i < enemies.Count;
            if (arrow.activeSelf != show) arrow.SetActive(show);
            if (!show) continue;
            var (enemy, distance) = enemies[i];
            var direction = Flat(enemy.transform.position - player).normalized;
            float along = Mathf.Min(floorDistance, distance - 0.4f);
            var at = player + direction * along;
            arrow.transform.SetPositionAndRotation(new Vector3(at.x, enemy.transform.position.y + 0.01f, at.z), Quaternion.LookRotation(direction, Vector3.up));
            float urgency = Mathf.InverseLerp(range, nearHide, distance); // 0 far … 1 at the feet
            float pulse = 1f + 0.15f * Mathf.Sin(_clock * Mathf.Lerp(4f, 14f, urgency));
            arrow.transform.localScale = Vector3.one * (Mathf.Lerp(0.9f, 1.3f, urgency) * pulse);
        }

        // Edge pointer for the nearest, when it's out of view.
        if (_pointer == null) _pointer = Spawn(cam, "EnemyPointer");
        bool pointer = false;
        if (enemies.Count > 0)
        {
            var local = cam.InverseTransformPoint(enemies[0].enemy.HitPoint);
            float offView = Vector3.Angle(Vector3.forward, local);
            if (offView > inViewDegrees)
            {
                var v = new Vector2(local.x, local.y);
                if (local.z < 0f || v.sqrMagnitude < 1e-4f) v = new Vector2(local.x >= 0f ? 1f : -1f, 0f); // behind: point to the nearer side
                v.Normalize();
                _pointer.transform.localPosition = new Vector3(v.x * pointerRadius, v.y * pointerRadius, pointerDistance);
                _pointer.transform.localRotation = Quaternion.LookRotation(new Vector3(v.x, v.y, 0f), Vector3.back); // flat arrow facing the eye
                _pointer.transform.localScale = Vector3.one * pointerScale * (1f + 0.12f * Mathf.Sin(_clock * 10f));
                pointer = true;
            }
        }
        if (_pointer.activeSelf != pointer) _pointer.SetActive(pointer);
    }

    private GameObject Spawn(Transform parent, string name)
    {
        var go = Instantiate(arrowPrefab, parent, false);
        go.name = name;
        go.SetActive(false);
        return go;
    }

    private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

    private void OnDisable()
    {
        foreach (var a in _arrows) if (a != null) a.SetActive(false);
        if (_pointer != null) _pointer.SetActive(false);
    }
}
