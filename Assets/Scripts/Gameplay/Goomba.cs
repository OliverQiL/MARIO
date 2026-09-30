using UnityEngine;

/// <summary>
/// Goomba (Docs/GAME_DESIGN.md): walks along the floor straight toward the
/// player. Defeated by stomp or fireball. Touching a player who isn't
/// airborne costs a life (and the Goomba is removed); if the player is
/// airborne from a jump it passes by, carries on briefly, then turns back.
/// Frozen while the session isn't ready (tracking lost, game over, transitions).
///
/// Movement lives in Tick(dt) with its own clock so headless tests can drive it.
/// Koopa derives from this and overrides the hit and the walking condition.
/// </summary>
public class Goomba : InteractableBase
{
    [SerializeField] private float speed = 0.25f;
    [SerializeField] private float contactRadius = 0.35f;
    [Tooltip("After passing under an airborne player, keep going this long before turning back.")]
    [SerializeField] private float passSeconds = 1.2f;
    [SerializeField] protected int scoreValue = 100;
    [SerializeField] protected float bodyHeight = 0.12f;

    protected bool _dead;
    private float _clock;
    private float _passUntil = float.NegativeInfinity;
    private Vector3 _passDirection;

    /// <summary>Set by the spawner from Difficulty.</summary>
    public float SpeedMultiplier { get; set; } = 1f;

    public override bool IsActionable => !_dead;
    public override bool RespondsTo(string actionLabel) => actionLabel == "stomp" || actionLabel == "fireball";
    public override Vector3 HitPoint => transform.position + Vector3.up * bodyHeight;

    /// <summary>Whether it walks and hurts on contact (a Koopa shell doesn't).</summary>
    protected virtual bool IsWalking => true;
    /// <summary>Alive and walking (for the waddle animation).</summary>
    public bool IsMoving => !_dead && IsWalking;

    public override void ResolveHit(string actionLabel, GameStateManager gameState) => Defeat(actionLabel, gameState);

    protected void Defeat(string actionLabel, GameStateManager gameState)
    {
        _dead = true;
        gameState.AddScore(scoreValue);
        // Squash, then remove.
        transform.localScale = new Vector3(transform.localScale.x * 1.2f, transform.localScale.y * 0.25f, transform.localScale.z * 1.2f);
        SafeDestroy(gameObject, 0.4f);
        Debug.Log($"[{GetType().Name}] Defeated by {actionLabel}.");
    }

    private void Update()
    {
        var cam = Camera.main;
        if (cam != null) Tick(Time.deltaTime, cam.transform.position);
    }

    public void Tick(float dt, Vector3 playerPosition)
    {
        var gs = GameStateManager.Instance;
        if (_dead || !IsWalking || gs == null || !gs.IsSessionReady) return;
        _clock += dt;

        var toPlayer = playerPosition - transform.position;
        toPlayer.y = 0f;
        float distance = toPlayer.magnitude;

        Vector3 direction;
        if (_clock < _passUntil) direction = _passDirection;
        else if (distance > 0.01f) direction = toPlayer / distance;
        else direction = transform.forward;

        transform.position += direction * (speed * SpeedMultiplier * dt);
        if (direction.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(direction, Vector3.up);

        if (_clock < _passUntil || distance > contactRadius) return;

        if (gs.IsAirborne)
        {
            // Jumped over: keep walking the same way for a moment, then turn back.
            _passUntil = _clock + passSeconds;
            _passDirection = distance > 0.01f ? toPlayer / distance : transform.forward;
            Debug.Log($"[{GetType().Name}] Jumped over.");
            return;
        }

        if (gs.TakeHit(GetType().Name))
        {
            _dead = true;
            SafeDestroy(gameObject);
        }
    }
}
