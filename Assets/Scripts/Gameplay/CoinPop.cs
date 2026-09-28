using UnityEngine;

/// <summary>Visual only: a coin that pops up out of a block, spins and vanishes.</summary>
public class CoinPop : MonoBehaviour
{
    [SerializeField] private float riseHeight = 0.35f;
    [SerializeField] private float lifetime = 0.6f;
    [SerializeField] private float spinDegreesPerSecond = 720f;

    private Vector3 _start;
    private float _age;

    private void Start() => _start = transform.position;

    private void Update()
    {
        _age += Time.deltaTime;
        float t = Mathf.Clamp01(_age / lifetime);
        transform.position = _start + Vector3.up * (riseHeight * Mathf.Sin(t * Mathf.PI * 0.5f));
        transform.Rotate(Vector3.up, spinDegreesPerSecond * Time.deltaTime, Space.World);
        if (_age >= lifetime) Destroy(gameObject);
    }
}
