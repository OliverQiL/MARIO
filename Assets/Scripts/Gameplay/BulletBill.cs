using UnityEngine;

/// <summary>
/// Bullet Bill (Docs/GAME_DESIGN.md, pass 2): fired by a BulletBillCannon at
/// head height, then flies in a straight line; it doesn't home in on the
/// player. It can't be defeated (not an InteractableBase, so fireballs and
/// stomps ignore it). As it passes the player it hits unless they're ducked
/// (squat) or have stepped out of its path; then it flies on and is removed.
/// Frozen while the session isn't ready.
///
/// Movement lives in Tick(dt) so headless tests can drive it.
/// </summary>
public class BulletBill : MonoBehaviour
{
    [SerializeField] private float speed = 2f;
    [Tooltip("Horizontal distance from its path within which the player is hit.")]
    [SerializeField] private float hitRadius = 0.35f;
    [SerializeField] private float flyPastDistance = 3f;

    public enum Phase { Idle, Flying, Passed }
    public Phase State { get; private set; } = Phase.Idle;
    /// <summary>True once it has passed the player and the hit landed.</summary>
    public bool HitPlayer { get; private set; }
    public Vector3 Direction => _direction;

    private Vector3 _direction;
    private float _travelledPast;

    /// <summary>Sets off along <paramref name="direction"/> (made horizontal); it never turns after this.</summary>
    public void Launch(Vector3 direction)
    {
        direction.y = 0f;
        _direction = direction.sqrMagnitude > 1e-6f ? direction.normalized : transform.forward;
        transform.rotation = Quaternion.LookRotation(_direction, Vector3.up);
        State = Phase.Flying;
    }

    private void Update()
    {
        var cam = Camera.main;
        if (cam != null) Tick(Time.deltaTime, cam.transform.position);
    }

    public void Tick(float dt, Vector3 playerPosition)
    {
        var gs = GameStateManager.Instance;
        if (State == Phase.Idle || gs == null || !gs.IsSessionReady) return;

        var step = _direction * (speed * dt);
        transform.position += step;

        if (State == Phase.Flying)
        {
            var toPlayer = playerPosition - transform.position;
            toPlayer.y = 0f;
            if (Vector3.Dot(toPlayer, _direction) > 0f) return; // not level with the player yet

            State = Phase.Passed;
            float lateral = Vector3.ProjectOnPlane(toPlayer, _direction).magnitude;
            if (lateral > hitRadius) Debug.Log("[BulletBill] Missed — the player stepped aside.");
            else if (gs.IsDucked) Debug.Log("[BulletBill] Ducked under.");
            else HitPlayer = gs.TakeHit("Bullet Bill");
            return;
        }

        _travelledPast += step.magnitude;
        if (_travelledPast >= flyPastDistance) InteractableBase.SafeDestroy(gameObject);
    }
}
