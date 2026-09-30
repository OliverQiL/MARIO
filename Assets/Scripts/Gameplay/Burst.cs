using UnityEngine;

/// <summary>
/// Visual only: a one-shot particle burst built from its child objects (small
/// primitives). Each child flies off in a random direction (biased upward),
/// optionally falls, spins and shrinks away; then the burst removes itself.
/// Used for power-up sparkles, defeat puffs, fireball impacts, cannon smoke
/// and stage-clear confetti; GameContentSetup builds the variants.
/// </summary>
public class Burst : MonoBehaviour
{
    [SerializeField] private float lifetime = 0.6f;
    [SerializeField] private float speed = 1.2f;
    [Tooltip("Added to each random direction before normalising: >0 sprays upward.")]
    [SerializeField] private float upBias = 0.5f;
    [Tooltip("Extra push along the burst's own forward (e.g. smoke out of a barrel).")]
    [SerializeField] private float forwardBias;
    [SerializeField] private float gravity = 3f;
    [Tooltip("Velocity lost per second (0..1): puffs slow down and hang.")]
    [SerializeField] private float drag;
    [SerializeField] private float spinDegreesPerSecond = 360f;
    [SerializeField] private bool shrink = true;

    private Transform[] _pieces;
    private Vector3[] _velocities;
    private Vector3[] _scales;
    private Vector3[] _spinAxes;
    private float _age;

    private void Start()
    {
        int n = transform.childCount;
        _pieces = new Transform[n];
        _velocities = new Vector3[n];
        _scales = new Vector3[n];
        _spinAxes = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            _pieces[i] = transform.GetChild(i);
            _scales[i] = _pieces[i].localScale;
            var dir = (Random.onUnitSphere + Vector3.up * upBias + transform.forward * forwardBias).normalized;
            _velocities[i] = dir * (speed * Random.Range(0.6f, 1.2f));
            _spinAxes[i] = Random.onUnitSphere;
        }
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        _age += dt;
        float t = Mathf.Clamp01(_age / lifetime);
        for (int i = 0; i < _pieces.Length; i++)
        {
            _velocities[i] += Vector3.down * (gravity * dt);
            _velocities[i] *= Mathf.Clamp01(1f - drag * dt);
            _pieces[i].position += _velocities[i] * dt;
            _pieces[i].Rotate(_spinAxes[i], spinDegreesPerSecond * dt, Space.World);
            if (shrink) _pieces[i].localScale = _scales[i] * (1f - t * t);
        }
        if (_age >= lifetime) Destroy(gameObject);
    }
}
