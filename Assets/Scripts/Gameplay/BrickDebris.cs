using UnityEngine;

/// <summary>Visual only: a broken brick's fragments fly up and out, fall, and vanish.</summary>
public class BrickDebris : MonoBehaviour
{
    [SerializeField] private float lifetime = 0.9f;
    [SerializeField] private float gravity = 6f;
    [SerializeField] private float outSpeed = 0.9f;
    [SerializeField] private float upSpeed = 1.6f;
    [SerializeField] private float spinDegreesPerSecond = 540f;

    private Transform[] _pieces;
    private Vector3[] _velocities;
    private float _age;

    private void Start()
    {
        _pieces = new Transform[transform.childCount];
        _velocities = new Vector3[_pieces.Length];
        for (int i = 0; i < _pieces.Length; i++)
        {
            _pieces[i] = transform.GetChild(i);
            var outward = _pieces[i].localPosition;
            outward.y = 0f;
            _velocities[i] = transform.TransformDirection(outward.normalized) * outSpeed
                             + Vector3.up * (upSpeed * (_pieces[i].localPosition.y > 0f ? 1f : 0.6f));
        }
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        _age += dt;
        for (int i = 0; i < _pieces.Length; i++)
        {
            _velocities[i] += Vector3.down * (gravity * dt);
            _pieces[i].position += _velocities[i] * dt;
            _pieces[i].Rotate(Vector3.right, spinDegreesPerSecond * dt, Space.Self);
        }
        if (_age >= lifetime) Destroy(gameObject);
    }
}
