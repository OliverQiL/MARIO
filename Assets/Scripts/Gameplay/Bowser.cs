using UnityEngine;

/// <summary>
/// The final boss (castle stage, Docs/GAME_DESIGN.md): stands guard in front of
/// the axe, pacing side to side and facing the player. Touching it costs a life.
/// How it attacks depends on how far away the player is:
///
/// - Far (beyond closeRange): every few seconds it roars (a warning glow) and
///   breathes a fireball at head height, which flies like a Bullet Bill:
///   squat or sidestep.
/// - Close: it crouches (a red target on the floor follows the player), then
///   leaps at where the player stood when it took off, landing with a
///   shockwave: get out of the red target, or jump as it lands. A player who
///   walks up to it doesn't get a free beating: once close, the next attack
///   comes within closeReactionSeconds. After landing it's stunned for a
///   moment (no contact damage), then hops back to its post.
///
/// It takes several hits: a punch, a stomp or a fireball (throw with the
/// flower) takes 1 health, a thrown Koopa shell 2. Nothing reaches it in the
/// air. When its health runs out it's knocked out (+5000) and the goal it
/// guards unlocks; reaching the goal then clears the game.
///
/// Movement lives in Tick(dt, player) so headless tests can drive it.
/// </summary>
public class Bowser : InteractableBase
{
    public enum Phase { Pacing, FireWarning, JumpWarning, Leaping, Stunned, Returning }

    [SerializeField] private int maxHealth = 5;
    [SerializeField] private int scoreValue = 5000;
    [Tooltip("Fired at head height toward the player (a BulletBill with flame visuals).")]
    [SerializeField] private BulletBill flamePrefab;
    [SerializeField] private float firstFlameDelay = 3f;
    [Tooltip("Seconds between attacks (fire or a leap).")]
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

    [Header("Jump attack (when the player is close)")]
    [Tooltip("Within this horizontal distance (metres) of the player it leaps instead of breathing fire.")]
    [SerializeField] private float closeRange = 1.5f;
    [Tooltip("Once the player is close, the next attack comes within this many seconds.")]
    [SerializeField] private float closeReactionSeconds = 1.2f;
    [Tooltip("The crouch before the leap; the red target follows the player.")]
    [SerializeField] private float jumpWarningSeconds = 0.9f;
    [SerializeField] private float leapSeconds = 0.9f;
    [SerializeField] private float leapHeight = 0.9f;
    [Tooltip("It leaps at most this far (metres) from where it is.")]
    [SerializeField] private float maxLeapDistance = 2f;
    [Tooltip("Landing hurts a player (not in the air) within this horizontal distance of where it lands.")]
    [SerializeField] private float shockRadius = 0.7f;
    [Tooltip("After landing: stunned (no contact damage), the moment to hit back.")]
    [SerializeField] private float stunSeconds = 1.6f;
    [SerializeField] private float returnSeconds = 0.8f;
    [Tooltip("A red target on the floor where it will land (shown while it crouches and leaps).")]
    [SerializeField] private GameObject landingMarker;
    [SerializeField] private GameObject landEffect;

    /// <summary>The boss on the current stage (for the HUD's health bar), or null.</summary>
    public static Bowser Active { get; private set; }

    public int Health => Mathf.Max(0, maxHealth - _damage);
    public int MaxHealth => maxHealth;
    public bool IsDefeated => Health <= 0;
    public Phase State => _phase;
    /// <summary>Roaring: a flame is coming. For tests and the HUD.</summary>
    public bool IsWarning => _phase == Phase.FireWarning;
    /// <summary>Crouching or in the air on a leap at the player: get out of the red target. For tests and the HUD.</summary>
    public bool IsJumpAttack => _phase is Phase.JumpWarning or Phase.Leaping;
    public bool IsStunned => _phase == Phase.Stunned;
    public bool IsAirborne => _phase is Phase.Leaping or Phase.Returning;
    /// <summary>Where the current leap lands (the player's position while it crouches).</summary>
    public Vector3 LandingPoint => _leapTo;
    public float CloseRange => closeRange;

    private int _damage;
    private float _clock;
    private float _paceClock;
    private float _nextAttackAt;
    private Phase _phase = Phase.Pacing;
    private float _phaseStart;
    private Vector3 _leapFrom, _leapTo;
    private Vector3 _home;
    private bool _homeSet;
    private float _flashUntil = float.NegativeInfinity;
    private Vector3 _knockVelocity;
    private StageGoal _goal;
    private Vector3 _modelPosition, _modelScale;

    public override bool IsActionable => !IsDefeated;
    public override bool RespondsTo(string actionLabel) =>
        !IsAirborne && (actionLabel == "fireball" || actionLabel == "punch" || actionLabel == "stomp" || actionLabel == ThrownShell.HitLabel);
    /// <summary>Chest height, scaled to the player, so a punch reaches it and a fireball aimed at it hits.</summary>
    public override Vector3 HitPoint => transform.position + Vector3.up * (0.6f * PlayerBody.EyeHeightOr(1.55f));

    public override bool TouchesFloorAt(Vector3 point, float radius)
    {
        if (IsAirborne) return false;
        var offset = transform.position - point;
        if (Mathf.Abs(offset.y) > 0.4f) return false;
        offset.y = 0f;
        return offset.magnitude <= radius + bodyRadius;
    }

    private void Awake()
    {
        if (warningGlow != null) warningGlow.SetActive(false);
        if (landingMarker != null) landingMarker.SetActive(false);
        if (model != null) { _modelPosition = model.localPosition; _modelScale = model.localScale; }
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
        if (landingMarker != null) landingMarker.SetActive(false);
        ResetModel();
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

    /// <summary>Paces, faces the player, attacks (fire from afar, a leap up close) and hurts on contact. Returns a flame if one was fired this step.</summary>
    public BulletBill Tick(float dt, Vector3 playerPosition)
    {
        var gs = GameStateManager.Instance;
        if (IsDefeated || gs == null || !gs.IsSessionReady) return null;

        // Its post (taken after any CompressLayout): it paces around it and returns to it after a leap.
        if (!_homeSet)
        {
            _home = transform.localPosition;
            _nextAttackAt = firstFlameDelay;
            _homeSet = true;
            if (model != null) { _modelPosition = model.localPosition; _modelScale = model.localScale; }
        }
        _clock += dt;

        var toPlayer = playerPosition - transform.position;
        toPlayer.y = 0f;
        float distance = toPlayer.magnitude;
        if (toPlayer.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(toPlayer.normalized, Vector3.up);
        float phaseAge = _clock - _phaseStart;

        switch (_phase)
        {
            case Phase.Pacing:
                Pace(dt);
                if (distance <= contactRadius) gs.TakeHit("Bowser");
                bool close = distance <= closeRange;
                if (close) _nextAttackAt = Mathf.Min(_nextAttackAt, _clock + closeReactionSeconds);
                if (_clock >= _nextAttackAt - (close ? jumpWarningSeconds : warningSeconds))
                {
                    if (close)
                    {
                        Enter(Phase.JumpWarning);
                        _leapTo = LeapTarget(playerPosition);
                        ShowMarker(_leapTo);
                    }
                    else
                    {
                        Enter(Phase.FireWarning);
                        if (warningGlow != null) warningGlow.SetActive(true);
                    }
                    AudioManager.Play(Sfx.Warning);
                }
                break;

            case Phase.FireWarning:
                Pace(dt);
                if (distance <= contactRadius) gs.TakeHit("Bowser");
                if (_clock < _nextAttackAt) break;
                if (warningGlow != null) warningGlow.SetActive(false);
                _nextAttackAt = _clock + flameInterval;
                Enter(Phase.Pacing);
                return BreatheFire(playerPosition);

            case Phase.JumpWarning:
                if (distance <= contactRadius) gs.TakeHit("Bowser");
                _leapTo = LeapTarget(playerPosition); // the target follows the player until take-off
                ShowMarker(_leapTo);
                Squash(1f - 0.25f * Mathf.Clamp01(phaseAge / jumpWarningSeconds));
                if (phaseAge < jumpWarningSeconds) break;
                Squash(1f);
                _leapFrom = transform.position;
                Enter(Phase.Leaping);
                AudioManager.Play(Sfx.Jump);
                break;

            case Phase.Leaping:
                if (Arc(phaseAge / leapSeconds)) break;
                Land(playerPosition, gs);
                break;

            case Phase.Stunned:
                if (phaseAge < stunSeconds) break;
                _leapFrom = transform.position;
                _leapTo = PostPosition();
                Enter(Phase.Returning);
                AudioManager.Play(Sfx.Jump);
                break;

            case Phase.Returning:
                if (Arc(phaseAge / returnSeconds)) break;
                _nextAttackAt = _clock + flameInterval;
                Enter(Phase.Pacing);
                break;
        }
        return null;
    }

    private void Enter(Phase phase)
    {
        _phase = phase;
        _phaseStart = _clock;
    }

    /// <summary>Side to side (along the stage's x) around its post.</summary>
    private void Pace(float dt)
    {
        _paceClock += dt;
        if (transform.parent != null) transform.position = PostPosition();
    }

    private Vector3 PostPosition()
    {
        if (transform.parent == null) return transform.position;
        float sway = Mathf.Sin(_paceClock * paceSpeed / Mathf.Max(0.01f, paceDistance)) * paceDistance;
        return transform.parent.TransformPoint(_home) + transform.parent.right * sway;
    }

    /// <summary>The player's spot on its floor, at most maxLeapDistance away.</summary>
    private Vector3 LeapTarget(Vector3 playerPosition)
    {
        var target = new Vector3(playerPosition.x, transform.position.y, playerPosition.z);
        var offset = target - transform.position;
        return transform.position + Vector3.ClampMagnitude(offset, maxLeapDistance);
    }

    /// <summary>Moves along the leap from _leapFrom to _leapTo at <paramref name="t"/> (0-1), the body arcing up; false once landed.</summary>
    private bool Arc(float t)
    {
        t = Mathf.Clamp01(t);
        transform.position = Vector3.Lerp(_leapFrom, _leapTo, t);
        if (model != null) model.localPosition = _modelPosition + Vector3.up * (4f * leapHeight * t * (1f - t) / Mathf.Max(0.01f, transform.lossyScale.y));
        if (_phase == Phase.Leaping) ShowMarker(_leapTo);
        return t < 1f;
    }

    /// <summary>The leap lands: a shockwave hurts a player on the ground near the target; then it's stunned.</summary>
    private void Land(Vector3 playerPosition, GameStateManager gs)
    {
        ResetModel();
        if (landingMarker != null) landingMarker.SetActive(false);
        if (landEffect != null) Instantiate(landEffect, transform.position, Quaternion.identity);
        AudioManager.Play(Sfx.BrickBreak);
        var offset = playerPosition - transform.position;
        offset.y = 0f;
        if (offset.magnitude <= shockRadius && !gs.IsAirborne) gs.TakeHit("Bowser's stomp");
        Debug.Log($"[Bowser] Landed {offset.magnitude:F2} m from the player{(gs.IsAirborne ? " (in the air)" : "")} — stunned.");
        Enter(Phase.Stunned);
    }

    private void ShowMarker(Vector3 at)
    {
        if (landingMarker == null) return;
        if (!landingMarker.activeSelf) landingMarker.SetActive(true);
        landingMarker.transform.position = at + Vector3.up * 0.006f;
    }

    /// <summary>Crouches to <paramref name="y"/> of its height, bulging out sideways by half as much.</summary>
    private void Squash(float y)
    {
        float bulge = 1.5f - 0.5f * y;
        if (model != null) model.localScale = new Vector3(_modelScale.x * bulge, _modelScale.y * y, _modelScale.z * bulge);
    }

    private void ResetModel()
    {
        if (model == null) return;
        model.localPosition = _modelPosition;
        model.localScale = _modelScale;
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
