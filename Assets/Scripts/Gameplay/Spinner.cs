using UnityEngine;

/// <summary>Visual only: spins its object around a local axis (fireballs, coins).</summary>
public class Spinner : MonoBehaviour
{
    [SerializeField] private Vector3 axis = Vector3.right;
    [SerializeField] private float degreesPerSecond = 720f;

    private void Update() => transform.Rotate(axis, degreesPerSecond * Time.deltaTime, Space.Self);
}
