using UnityEngine;

/// <summary>
/// Bullet Bill (Docs/GAME_DESIGN.md, pass 2): fired by a BulletBillCannon at
/// head height, then flies in a straight line; it doesn't home in on the
/// player. It can't be defeated (not an InteractableBase, so fireballs and
/// stomps ignore it). As it passes the player it hits unless they're ducked
/// (squat) or have stepped out of its path; then it flies on and is removed.
/// Frozen while the session isn't ready.
///
/// Lenient about the duck (playtest 2026-11-12: squatting players were still
/// hit; GameplaySmokeTest.BulletBillDuckChecks has the cases):
/// - The player counts as ducked if the game says so (a squat event's window,
///   or the phone seeing a held squat) **or** their head is physically below
///   the Bill (headClearance under its centre), so a squat no one reported counts.
/// - It's judged over a pass window, from passWindow before the player to
///   passWindow after: ducked at any moment in it is enough, so a squat the
///   classifier reports a moment late (~0.3 s) still counts. A Bill that
///   isn't ducked as it draws level decides at the end of the window.
///
/// Movement lives in Tick(dt) so headless tests can drive it.
/// </summary>
public class BulletBill : MonoBehaviour
{
    [SerializeField] private float speed = 2f;
    [Tooltip("Horizontal distance from its path within which the player is hit.")]
    [SerializeField] private float hitRadius = 0.35f;
    [SerializeField] private float flyPastDistance = 3f;
    [Tooltip("Metres either side of the player over which a duck counts (2 m/s: 0.6 m = 0.3 s each way).")]
    [SerializeField] private float passWindow = 0.6f;
    [Tooltip("A head this far (metres) or more below the Bill's centre is under it: ducked, squat event or not.")]
    [SerializeField] private float headClearance = 0.1f;

    public enum Phase { Idle, Flying, Passed }
    public Phase State { get; private set; } = Phase.Idle;
    /// <summary>True once it has passed the player and the hit landed.</summary>
    public bool HitPlayer { get; private set; }
    /// <summary>True once it has passed over the player while they were ducked (the training's duck step counts these).</summary>
    public bool DuckedUnder { get; private set; }
    public Vector3 Direction => _direction;
    /// <summary>Hit or not has been decided (State is Passed).</summary>
    public bool Decided => State == Phase.Passed;

    private Vector3 _direction;
    private float _travelledPast;
    private bool _ducked, _level, _inPath;

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
            float ahead = Vector3.Dot(toPlayer, _direction); // > 0: not level with the player yet
            if (ahead > passWindow) return; // not near yet

            if (gs.IsDucked || playerPosition.y < transform.position.y - headClearance) _ducked = true;
            if (ahead > 0f) return;

            if (!_level)
            {
                // Drawing level: out of its path is a miss; ducked (now or just before) passes under.
                _level = true;
                _inPath = Vector3.ProjectOnPlane(toPlayer, _direction).magnitude <= hitRadius;
                if (!_inPath) { Decide(); Debug.Log("[BulletBill] Missed — the player stepped aside."); return; }
            }
            if (_ducked) { DuckedUnder = true; Decide(); Debug.Log("[BulletBill] Ducked under."); return; }
            if (-ahead < passWindow) return; // a squat reported a moment late still counts
            HitPlayer = gs.TakeHit("Bullet Bill");
            Decide();
            return;
        }

        _travelledPast += step.magnitude;
        if (_travelledPast >= flyPastDistance) InteractableBase.SafeDestroy(gameObject);
    }

    private void Decide() => State = Phase.Passed;
}
