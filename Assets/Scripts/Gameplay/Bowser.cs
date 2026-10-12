using UnityEngine;

/// <summary>
/// The final boss (castle stage, Docs/GAME_DESIGN.md): stands guard in front of
/// the axe, pacing side to side and facing the player. Every few seconds it
/// roars (a warning glow) and breathes a fireball at head height, which flies
/// like a Bullet Bill: squat or sidestep. Touching it costs a life.
///
/// It takes several hits: a fireball (throw with the flower) or a punch takes
/// 1 health, a thrown Koopa shell 2. Not stompable. When its health runs out
/// it's knocked out (+5000) and the goal it guards unlocks; reaching the goal
/// then clears the game.
///
/// Movement lives in Tick(dt, player) so headless tests can drive it.
/// </summary>
public class Bowser : InteractableBase
{
    [SerializeField] private int maxHealth = 5;
    [SerializeField] private int scoreValue = 5000;
    [Tooltip("Fired at head height toward the player (a BulletBill with flame visuals).")]
    [SerializeField] private BulletBill flamePrefab;
    [SerializeField] private float firstFlameDelay = 3f;
    [SerializeField] private float flameInterval = 4.5f;
    [SerializeField] private float warningSeconds = 1f;
    [Tooltip("Glows during the warning before a flame.")]
    [SerializeField] private GameObject warningGlow;
    [SerializeField] private float paceDistance = 0.35f;
    [SerializeField] private float paceSpeed = 0.12f;
    [SerializeField] private float contactRadius = 0.5f;
    [Tooltip("Horizontal radius of its body: a sliding shell hits anywhere inside it.")]
    [SerializeField] private float bodyRadius = 0.3f;
    [SerializeField] private Transform model;
    [SerializeField] private GameObject hitEffect;
    [SerializeField] private GameObject defeatEffect;

    /// <summary>The boss on the current stage (for the HUD's health bar), or null.</summary>
    public static Bowser Active { get; private set; }

    public int Health => Mathf.Max(0, maxHealth - _damage);
    public int MaxHealth => maxHealth;
    public bool IsDefeated => Health <= 0;
    /// <summary>Roaring: a flame is coming. For tests and the HUD.</summary>
    public bool IsWarning => _warning;

    private int _damage;
    private float _clock;
    private float _nextFlameAt;
    private bool _warning;
    private Vector3 _home;
    private bool _homeSet;
    private float _flashUntil = float.NegativeInfinity;
    private Vector3 _knockVelocity;
    private StageGoal _goal;

    public override bool IsActionable => !IsDefeated;
    public override bool RespondsTo(string actionLabel) =>
        actionLabel == "fireball" || actionLabel == "punch" || actionLabel == ThrownShell.HitLabel;
    /// <summary>Chest height, scaled to the player, so a punch reaches it and a fireball aimed at it hits.</summary>
    public override Vector3 HitPoint => transform.position + Vector3.up * (0.6f * PlayerBody.EyeHeightOr(1.55f));

    public override bool TouchesFloorAt(Vector3 point, float radius)
    {
        var offset = transform.position - point;
        if (Mathf.Abs(offset.y) > 0.4f) return false;
        offset.y = 0f;
        return offset.magnitude <= radius + bodyRadius;
    }

    private void Awake()
    {
        if (warningGlow != null) warningGlow.SetActive(false);
    }

    protected override void OnEnable()
    {
        base.OnEnable();
        Active = this;
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        if (Active == this) Active = null;
    }

    private void Start()
    {
        // Guard the goal: it can't be reached until the boss is beaten.
        _goal = transform.parent != null ? transform.parent.GetComponentInChildren<StageGoal>() : null;
        if (_goal != null && !IsDefeated) _goal.Locked = true;
    }

    public override void ResolveHit(string actionLabel, GameStateManager gameState)
    {
        if (IsDefeated) return;
        _damage += actionLabel == ThrownShell.HitLabel ? 2 : 1;
        _flashUntil = Time.time + 0.3f;
        if (hitEffect != null) Instantiate(hitEffect, HitPoint, Quaternion.identity);
        Debug.Log($"[Bowser] Hit by {actionLabel}: {Health}/{maxHealth}.");
        if (Health > 0) return;

        gameState.AddScore(scoreValue);
        gameState.RecordDefeat();
        if (_goal == null && transform.parent != null) _goal = transform.parent.GetComponentInChildren<StageGoal>();
        if (_goal != null) _goal.Locked = false;
        if (warningGlow != null) warningGlow.SetActive(false);
        if (defeatEffect != null) Instantiate(defeatEffect, HitPoint, Quaternion.identity);
        _knockVelocity = Vector3.up * 1.6f;
        SafeDestroy(gameObject, 1.6f);
        Debug.Log("[Bowser] Defeated! The axe is open.");
    }

    private void Update()
    {
        if (IsDefeated)
        {
            // Knocked out: flips and falls away, as in the classic games.
            float dt = Time.deltaTime;
            _knockVelocity += Vector3.down * (5f * dt);
            transform.position += _knockVelocity * dt;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.Euler(0f, transform.eulerAngles.y, 180f), 540f * dt);
            return;
        }
        if (model != null) model.gameObject.SetActive(Time.time >= _flashUntil || Mathf.Repeat(Time.time, 0.1f) < 0.05f);
        var cam = Camera.main;
        if (cam != null) Tick(Time.deltaTime, cam.transform.position);
    }

    /// <summary>Paces, faces the player, warns then breathes fire, and hurts on contact. Returns a flame if one was fired this step.</summary>
    public BulletBill Tick(float dt, Vector3 playerPosition)
    {
        var gs = GameStateManager.Instance;
        if (IsDefeated || gs == null || !gs.IsSessionReady) return null;

        // Pace side to side (along the stage's x) around its spot (taken after any CompressLayout).
        if (!_homeSet) { _home = transform.localPosition; _nextFlameAt = firstFlameDelay; _homeSet = true; }
        var right = transform.parent != null ? transform.parent.right : Vector3.right;
        _clock += dt;
        float sway = Mathf.Sin(_clock * paceSpeed / Mathf.Max(0.01f, paceDistance)) * paceDistance;
        if (transform.parent != null)
            transform.position = transform.parent.TransformPoint(_home) + right * sway;

        var toPlayer = playerPosition - transform.position;
        toPlayer.y = 0f;
        if (toPlayer.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(toPlayer.normalized, Vector3.up);

        if (toPlayer.magnitude <= contactRadius) gs.TakeHit("Bowser");

        // Roar (glow) then fire.
        bool warn = _clock >= _nextFlameAt - warningSeconds;
        if (warn != _warning)
        {
            _warning = warn;
            if (warningGlow != null) warningGlow.SetActive(warn);
            if (warn) AudioManager.Play(Sfx.Warning);
        }
        if (_clock < _nextFlameAt) return null;
        _nextFlameAt = _clock + flameInterval;
        _warning = false;
        if (warningGlow != null) warningGlow.SetActive(false);
        return BreatheFire(playerPosition);
    }

    private BulletBill BreatheFire(Vector3 playerPosition)
    {
        if (flamePrefab == null) return null;
        // At the player's head height, from in front of the mouth.
        var forward = transform.forward;
        var origin = transform.position + forward * 0.35f;
        origin.y = playerPosition.y - 0.1f;
        var flame = Instantiate(flamePrefab, origin, Quaternion.LookRotation(forward), transform.parent);
        flame.Launch(playerPosition - origin);
        AudioManager.Play(Sfx.CannonFire);
        return flame;
    }
}
