using System;
using System.Linq;
using UnityEngine;

/// <summary>
/// The training round (WORLD 0-1, Docs/GAME_DESIGN.md "Training round"): a
/// lane of stations, each teaching one action, shown one at a time. It runs
/// after every scan (first launch and hard reset), before 1-1; LevelGenerator
/// places it and, when its flag is grabbed, starts 1-1 fresh.
///
/// Each step's station is a child object that starts hidden and is shown
/// when the step begins. A step is done when its condition is met (blocks
/// used, enemies cleared, a duck under a Bullet Bill, the flag), or after
/// stepTimeoutSeconds so a misread move never leaves the player stuck
/// (except the flag, which is the way out). Leftover enemies and cannons of a
/// finished step are cleared. During training GameStateManager.InTraining
/// gives infinite lives.
///
/// The HUD reads <see cref="Active"/> for the step card and banner, asks
/// <see cref="WrongMoveTip"/> what to say when the classifier reads another
/// move, and shows <see cref="OnMessage"/> notes. Logic runs in Tick(dt) so
/// headless tests can drive it.
/// </summary>
public class Tutorial : MonoBehaviour
{
    public enum Done { BlocksUsed, EnemiesCleared, DuckUnderBill, Goal }

    [Serializable]
    public class Step
    {
        public string title;
        [Tooltip("One line under the title and in the banner.")]
        public string hint;
        [Tooltip("The actions this step teaches; another move gets a 'try ...' tip.")]
        public string[] actions;
        public GameObject station;
        public Done done;
        [Tooltip("Gives the fire flower at the start if the player hasn't got it (the fireball step).")]
        public bool needsFlower;
    }

    [SerializeField] private Step[] steps = Array.Empty<Step>();
    [Tooltip("Seconds before the first step, while the TRAINING intro card is up.")]
    [SerializeField] private float startDelaySeconds = 3.5f;
    [Tooltip("A step not done after this long moves on anyway (not the flag).")]
    [SerializeField] private float stepTimeoutSeconds = 40f;

    /// <summary>The live training round, if any.</summary>
    public static Tutorial Active { get; private set; }
    /// <summary>Short notes for the HUD's feedback line ("Let's move on", "Here's a fire flower!").</summary>
    public static event Action<string> OnMessage;

    /// <summary>The current step (0-based), or -1 before the first one starts.</summary>
    public int StepIndex { get; private set; } = -1;
    public int StepCount => steps.Length;
    public Step Current => StepIndex >= 0 && StepIndex < steps.Length ? steps[StepIndex] : null;
    /// <summary>Raised when a step starts (the HUD shows its card).</summary>
    public event Action<Step> StepStarted;

    private float _waited;
    private float _age;
    private bool _ducked;
    private EventResolver _resolver;

    private void Awake()
    {
        foreach (var s in steps)
            if (s.station != null) s.station.SetActive(false);
    }

    private void OnEnable()
    {
        Active = this;
        _resolver = FindAnyObjectByType<EventResolver>();
        if (_resolver != null) _resolver.OnEventProcessed += HandleEvent;
    }

    private void OnDisable()
    {
        if (Active == this) Active = null;
        if (_resolver != null) _resolver.OnEventProcessed -= HandleEvent;
    }

    private void Update() => Tick(Time.deltaTime);

    /// <summary>Advances the round: starts the first step after the delay, then moves on when a step is done or times out.</summary>
    public void Tick(float dt)
    {
        if (StepIndex < 0)
        {
            _waited += dt;
            if (_waited >= startDelaySeconds) Begin(0);
            return;
        }
        var step = Current;
        if (step == null || step.done == Done.Goal) return; // the flag ends the round (LevelGenerator)
        _age += dt;
        // A squat held down (PhoneMotionDetector keeps the player ducked) counts once a Bill is coming.
        if (step.done == Done.DuckUnderBill && GameStateManager.Instance is { IsDucked: true } && BillComing(step)) _ducked = true;
        if (IsDone(step)) Advance();
        else if (_age >= stepTimeoutSeconds)
        {
            OnMessage?.Invoke("Let's move on");
            Advance();
        }
    }

    /// <summary>Starts the first step now, skipping the intro delay (tests).</summary>
    public void StartNow()
    {
        if (StepIndex < 0) Begin(0);
    }

    /// <summary>
    /// What to tell the player when the classifier reads a move this step isn't about
    /// ("That was a STOMP — try JUMP"), or null when there's nothing to say.
    /// </summary>
    public string WrongMoveTip(GameEvent evt, ResolutionResult result)
    {
        var step = Current;
        if (step == null || step.actions == null || step.actions.Length == 0) return null;
        if (evt.actionLabel == EventResolver.IdleAction || result.IsRepeat || result.Gate == 1) return null;
        if (result.Resolved && !result.StateOnly) return null; // it did something: no need to correct it
        if (result.Gate == 2 && !result.Reason.StartsWith("confidence", StringComparison.Ordinal)) return null; // duplicates
        if (step.actions.Contains(evt.actionLabel)) return null;
        string expected = string.Join(" or ", step.actions.Select(a => a.ToUpperInvariant()));
        return $"That was {evt.actionLabel.ToUpperInvariant()} — try {expected}";
    }

    private void Begin(int index)
    {
        StepIndex = index;
        _age = 0f;
        _ducked = false;
        var step = steps[index];
        if (step.station != null) step.station.SetActive(true);
        var gs = GameStateManager.Instance;
        if (step.needsFlower && gs != null && !gs.HasFireFlower)
        {
            gs.SetFireFlower(true);
            OnMessage?.Invoke("Here's a fire flower!");
        }
        AudioManager.Play(Sfx.PowerUp);
        Debug.Log($"[Tutorial] Step {index + 1}/{steps.Length}: {step.title}");
        StepStarted?.Invoke(step);
    }

    private void Advance()
    {
        CleanUp(Current);
        if (StepIndex + 1 < steps.Length) Begin(StepIndex + 1);
    }

    private bool IsDone(Step step)
    {
        if (step.station == null) return true;
        switch (step.done)
        {
            case Done.BlocksUsed:
                return step.station.GetComponentsInChildren<InteractableBase>()
                    .Where(t => t is QuestionBlock || t is BrickBlock)
                    .All(t => !t.IsActionable);
            case Done.EnemiesCleared:
                return !step.station.GetComponentsInChildren<InteractableBase>()
                    .Any(t => (t is Goomba || t is Koopa) && t.IsActionable);
            case Done.DuckUnderBill:
                return _ducked;
            default:
                return false;
        }
    }

    /// <summary>A finished (or timed-out) step's cannons stop and its enemies go, so they don't follow the player into the next one.</summary>
    private static void CleanUp(Step step)
    {
        if (step?.station == null) return;
        foreach (var cannon in step.station.GetComponentsInChildren<BulletBillCannon>()) cannon.gameObject.SetActive(false);
        foreach (var bill in FindObjectsByType<BulletBill>()) InteractableBase.SafeDestroy(bill.gameObject);
        foreach (var enemy in step.station.GetComponentsInChildren<InteractableBase>().Where(t => t is Goomba || t is Koopa))
            InteractableBase.SafeDestroy(enemy.gameObject);
    }

    private void HandleEvent(GameEvent evt, ResolutionResult result)
    {
        // The duck step: a squat while a Bullet Bill is coming (warning glow or in flight).
        var step = Current;
        if (step == null || step.done != Done.DuckUnderBill || evt.actionLabel != "squat" || !result.Resolved) return;
        if (BillComing(step)) _ducked = true;
    }

    private static bool BillComing(Step step) =>
        FindObjectsByType<BulletBill>().Length > 0
        || (step.station != null && step.station.GetComponentsInChildren<BulletBillCannon>().Any(c => c.IsWarning));
}
