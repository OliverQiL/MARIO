using UnityEngine;

/// <summary>Scenery: a flame that flickers (its scale jitters smoothly).</summary>
public class Flicker : MonoBehaviour
{
    [SerializeField] private float amount = 0.18f;
    [SerializeField] private float speed = 9f;

    private Vector3 _base;
    private float _seed;

    private void Awake()
    {
        _base = transform.localScale;
        _seed = Random.value * 100f;
    }

    private void Update()
    {
        float n = Mathf.PerlinNoise(_seed, Time.time * speed) - 0.5f;
        transform.localScale = new Vector3(_base.x * (1f - n * amount), _base.y * (1f + n * amount * 2f), _base.z * (1f - n * amount));
    }
}
