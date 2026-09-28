using UnityEngine;

/// <summary>
/// A power-up on the floor, collected by walking to it (horizontal distance).
/// Fire flower: enables fireball projectiles until the player takes damage.
/// One-up: +1 life (pass 2).
/// </summary>
public class Pickup : MonoBehaviour
{
    public enum Kind { FireFlower, OneUp }

    [SerializeField] private Kind kind = Kind.FireFlower;
    [SerializeField] private float collectRadius = 0.45f;
    [SerializeField] private float spinDegreesPerSecond = 90f;

    public Kind PickupKind => kind;

    private void Update()
    {
        transform.Rotate(Vector3.up, spinDegreesPerSecond * Time.deltaTime, Space.World);
        var cam = Camera.main;
        if (cam != null) TryCollect(cam.transform.position);
    }

    /// <summary>Collects if the player is within reach. Public so headless tests can drive it.</summary>
    public bool TryCollect(Vector3 playerPosition)
    {
        var gs = GameStateManager.Instance;
        if (gs == null || !gs.IsSessionReady) return false;

        var d = playerPosition - transform.position;
        d.y = 0f;
        if (d.magnitude > collectRadius) return false;

        if (kind == Kind.FireFlower) gs.SetFireFlower(true);
        else gs.AddLife();
        Debug.Log($"[Pickup] Collected {kind}.");
        InteractableBase.SafeDestroy(gameObject);
        return true;
    }
}
