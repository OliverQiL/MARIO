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

    [Tooltip("Seconds of the hop out of the ? block to where it lands.")]
    [SerializeField] private float dropSeconds = 0.8f;
    [Tooltip("Sparkle shown when collected.")]
    [SerializeField] private GameObject collectEffect;

    public Kind PickupKind => kind;

    private Vector3 _dropStart, _dropEnd;
    private float _dropElapsed = float.PositiveInfinity, _hopHeight;

    /// <summary>Hops from <paramref name="start"/> (the block), up <paramref name="height"/>, then down to the current (landing) position.</summary>
    public void HopFrom(Vector3 start, float height)
    {
        _dropEnd = transform.position;
        _dropStart = start;
        _hopHeight = height;
        _dropElapsed = 0f;
        transform.position = start;
    }

    public bool IsLanding => _dropElapsed < dropSeconds;

    private void Update()
    {
        transform.Rotate(Vector3.up, spinDegreesPerSecond * Time.deltaTime, Space.World);
        if (_dropElapsed < dropSeconds)
        {
            _dropElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(_dropElapsed / dropSeconds);
            // A hop: glides across while it rises a little above the block and falls to the floor.
            var p = Vector3.Lerp(_dropStart, _dropEnd, t);
            p.y = Mathf.Lerp(_dropStart.y, _dropEnd.y, t * t) + 4f * _hopHeight * t * (1f - t);
            transform.position = p;
        }
        var cam = Camera.main;
        if (cam != null) TryCollect(cam.transform.position);
    }

    /// <summary>Collects if the player is within reach. Public so headless tests can drive it.</summary>
    public bool TryCollect(Vector3 playerPosition)
    {
        var gs = GameStateManager.Instance;
        if (gs == null || !gs.IsSessionReady) return false;

        // Measured to the landing spot, so a falling flower isn't grabbed on its way past.
        var d = playerPosition - (_dropElapsed < dropSeconds ? _dropEnd : transform.position);
        d.y = 0f;
        if (d.magnitude > collectRadius) return false;

        if (kind == Kind.FireFlower) gs.SetFireFlower(true);
        else gs.AddLife();
        AudioManager.Play(kind == Kind.OneUp ? Sfx.OneUp : Sfx.PowerUp);
        if (collectEffect != null) Instantiate(collectEffect, transform.position + Vector3.up * 0.15f, Quaternion.identity);
        Debug.Log($"[Pickup] Collected {kind}.");
        InteractableBase.SafeDestroy(gameObject);
        return true;
    }
}
