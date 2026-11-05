using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// The training round (WORLD 0-1, Docs/GAME_DESIGN.md "Training round"): a
/// lane of stations, each teaching one move, shown one at a time. It runs
/// after every scan (first launch and hard reset), before 1-1; LevelGenerator
/// places it and, when its flag is grabbed, starts 1-1 fresh.
///
/// Each step is played as <see cref="Step.rounds"/> rounds (3; the flag 1),
/// and a round only counts when the step's own move does it (Oliver
/// 2026-10-29: a fireball Goomba stomped doesn't teach the fireball):
///   HitBlock       a jump hits the ? block
///   BreakBrick     a punch breaks the brick
///   StompEnemy     a stomp defeats the Goomba
///   DuckBill       a Bullet Bill passes over the ducked player (BulletBill.DuckedUnder)
///   FireballEnemy  a fireball hits the Goomba (the flower is handed out)
///   ShellEnemy     a thrown shell hits a Goomba (the flower is taken away, so
///                  throw can only throw the shell). Only the Koopa is out at
///                  first; the Goombas (the station's hidden "Targets" group)
///                  appear once the shell is in hand (Oliver 2026-11-05: all
///                  of them at once was confusing)
///   Flag           the flag is grabbed (ends the round; LevelGenerator)
/// For the target kinds the step's station is a hidden template and each
/// round plays a fresh copy of it; if the copy's target is used up any other
/// way (stomped instead of fireballed, a Goomba walking into the player), the
/// round restarts with a tip. The duck and flag steps use the station itself
/// (the cannon keeps firing; the flag is LevelGenerator's goal).
///
/// A step not finished after stepTimeoutSeconds (90 s) moves on anyway, so a
/// misreading classifier can't trap the player; the flag never times out.
/// During training GameStateManager.InTraining gives infinite lives. The HUD
/// reads <see cref="Active"/> (step, <see cref="RoundsDone"/>/<see cref="RoundsRequired"/>),
/// asks <see cref="WrongMoveTip"/> about misread moves, and shows
/// <see cref="OnMessage"/> notes. Logic runs in Tick(dt) for headless tests.
/// </summary>
public class Tutorial : MonoBehaviour
{
    public enum Kind { HitBlock, BreakBrick, StompEnemy, DuckBill, FireballEnemy, ShellEnemy, Flag }
    /// <summary>A station's child group hidden until the player holds a shell (the shell step's Goombas).</summary>
    public const string RevealOnHoldName = "Targets";

    [Serializable]
    public class Step
    {
        public string title;
        [Tooltip("One line under the title and in the banner.")]
        public string hint;
        [Tooltip("The actions this step teaches; another move gets a 'try ...' tip.")]
        public string[] actions;
        public GameObject station;
        public Kind kind;
        [Tooltip("How many times the move must be done (the flag: 1).")]
        public int rounds = 3;
        [Tooltip("Gives the fire flower at the start if the player hasn't got it (the fireball step).")]
        public bool needsFlower;
        [Tooltip("Takes the fire flower away at the start, so throw only throws a held shell (the shell step).")]
        public bool removeFlower;
    }

    [SerializeField] private Step[] steps = Array.Empty<Step>();
    [Tooltip("Seconds before the first step, while the TRAINING intro card is up.")]
    [SerializeField] private float startDelaySeconds = 3.5f;
    [Tooltip("A step not finished after this long moves on anyway (not the flag).")]
    [SerializeField] private float stepTimeoutSeconds = 90f;
    [Tooltip("Pause after a counted round before the next copy appears.")]
    [SerializeField] private float roundBeatSeconds = 1f;

    /// <summary>The live training round, if any.</summary>
    public static Tutorial Active { get; private set; }
    /// <summary>Short notes for the HUD's feedback line ("Nice! 1/3", "Let's move on", ...).</summary>
    public static event Action<string> OnMessage;

    /// <summary>The current step (0-based), or -1 before the first one starts.</summary>
    public int StepIndex { get; private set; } = -1;
    public int StepCount => steps.Length;
    public Step Current => StepIndex >= 0 && StepIndex < steps.Length ? steps[StepIndex] : null;
    public int RoundsDone { get; private set; }
    public int RoundsRequired => Current != null ? Mathf.Max(1, Current.rounds) : 0;
    /// <summary>What the player is playing right now: the round's copy (target steps) or the station itself.</summary>
    public GameObject CurrentRound { get; private set; }
    /// <summary>Raised when a step starts (the HUD shows its card).</summary>
    public event Action<Step> StepStarted;

    private float _waited, _age, _beat = -1f;
    private bool _roundCounted;
    private readonly HashSet<BulletBill> _countedBills = new();
    /// <summary>The round's targets, by reference: a target destroyed by the very hit that counts is Unity-null by the time we hear of it.</summary>
    private readonly List<Component> _roundTargets = new();
    private EventResolver _resolver;

    private static bool UsesCopies(Kind k) => k != Kind.DuckBill && k != Kind.Flag;

    private void Awake()
    {
        foreach (var s in steps)
            if (s.station != null) s.station.SetActive(false);
    }

    private void OnEnable()
    {
        Active = this;
        _resolver = FindAnyObjectByType<EventResolver>();
        if (_resolver != null)
        {
            _resolver.OnEventProcessed += HandleEvent;
            _resolver.OnImpact += HandleImpact;
        }
    }

    private void OnDisable()
    {
        if (Active == this) Active = null;
        if (_resolver != null)
        {
            _resolver.OnEventProcessed -= HandleEvent;
            _resolver.OnImpact -= HandleImpact;
        }
    }

    private void Update() => Tick(Time.deltaTime);

    /// <summary>Advances the training: starts the first step after the delay, then plays rounds, moving on when a step is done or times out.</summary>
    public void Tick(float dt)
    {
        if (StepIndex < 0)
        {
            _waited += dt;
            if (_waited >= startDelaySeconds) Begin(0);
            return;
        }
        var step = Current;
        if (step == null || step.kind == Kind.Flag) return; // the flag ends the training (LevelGenerator)
        _age += dt;

        if (step.kind == Kind.DuckBill)
            foreach (var bill in FindObjectsByType<BulletBill>())
                if (bill.DuckedUnder && _countedBills.Add(bill)) Count(step);

        if (_beat >= 0f)
        {
            // After a counted round: the next one (or the next step) after a short beat.
            _beat -= dt;
            if (_beat < 0f)
            {
                if (RoundsDone >= RoundsRequired) Advance();
                else if (UsesCopies(step.kind)) NewRound(step);
            }
            return;
        }

        if (step.kind == Kind.ShellEnemy) RevealTargetsOnHold();

        if (UsesCopies(step.kind) && !_roundCounted && TargetSpent(step))
        {
            // Used up some other way: this round didn't count. A fresh one.
            OnMessage?.Invoke($"That one didn't count — use {string.Join(", then ", step.actions.Select(a => a.ToUpperInvariant()))}");
            NewRound(step);
            return;
        }

        if (_age >= stepTimeoutSeconds)
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
        _beat = -1f;
        RoundsDone = 0;
        _countedBills.Clear();
        var step = steps[index];
        var gs = GameStateManager.Instance;
        if (step.needsFlower && gs != null && !gs.HasFireFlower)
        {
            gs.SetFireFlower(true);
            OnMessage?.Invoke("Here's a fire flower!");
        }
        if (step.removeFlower && gs != null && gs.HasFireFlower)
        {
            gs.SetFireFlower(false);
            OnMessage?.Invoke("No fireballs here — use the shell");
        }
        if (UsesCopies(step.kind)) NewRound(step);
        else
        {
            CurrentRound = step.station;
            if (step.station != null) step.station.SetActive(true);
        }
        AudioManager.Play(Sfx.PowerUp);
        Debug.Log($"[Tutorial] Step {index + 1}/{steps.Length}: {step.title} ({RoundsRequired}×)");
        StepStarted?.Invoke(step);
    }

    /// <summary>Replaces the round's copy of the station with a fresh one.</summary>
    private void NewRound(Step step)
    {
        ClearRound(step);
        _roundCounted = false;
        if (step.station == null) return;
        CurrentRound = Instantiate(step.station, step.station.transform.parent);
        CurrentRound.name = $"{step.station.name} (round {RoundsDone + 1})";
        CurrentRound.SetActive(true);
        _roundTargets.Clear();
        _roundTargets.AddRange(CurrentRound.GetComponentsInChildren<InteractableBase>(true));
    }

    private void Count(Step step)
    {
        if (_roundCounted && UsesCopies(step.kind)) return;
        _roundCounted = true;
        RoundsDone++;
        AudioManager.Play(Sfx.Coin);
        OnMessage?.Invoke(RoundsDone >= RoundsRequired ? $"Nice! {RoundsDone}/{RoundsRequired} — done" : $"Nice! {RoundsDone}/{RoundsRequired}");
        Debug.Log($"[Tutorial] {step.title} {RoundsDone}/{RoundsRequired}");
        if (UsesCopies(step.kind) || RoundsDone >= RoundsRequired) _beat = roundBeatSeconds;
        else _roundCounted = false; // the duck: keep counting Bills
    }

    private void Advance()
    {
        var step = Current;
        ClearRound(step);
        if (step != null && !UsesCopies(step.kind) && step.station != null && step.kind != Kind.Flag) step.station.SetActive(false);
        CleanUpBills();
        if (StepIndex + 1 < steps.Length) Begin(StepIndex + 1);
    }

    /// <summary>The round's target has gone without the round counting (wrong move, or the enemy reached the player).</summary>
    private bool TargetSpent(Step step)
    {
        if (CurrentRound == null) return true;
        var targets = CurrentRound.GetComponentsInChildren<InteractableBase>();
        switch (step.kind)
        {
            case Kind.HitBlock: return !targets.OfType<QuestionBlock>().Any(t => t.IsActionable);
            case Kind.BreakBrick: return !targets.OfType<BrickBlock>().Any(t => t.IsActionable);
            case Kind.StompEnemy:
            case Kind.FireballEnemy:
                return !targets.OfType<Goomba>().Any(t => !(t is Koopa) && t.IsActionable);
            case Kind.ShellEnemy:
            {
                bool shellAvailable = targets.OfType<Koopa>().Any(k => k.IsActionable)
                                      || GameStateManager.Instance is { Held: HeldItem.Shell } || ThrownShell.InFlight > 0;
                if (!shellAvailable) return true;
                var hidden = CurrentRound.transform.Find(RevealOnHoldName);
                if (hidden != null && !hidden.gameObject.activeSelf) return false; // the Goombas aren't out yet
                return !targets.OfType<Goomba>().Any(t => !(t is Koopa) && t.IsActionable);
            }
            default: return false;
        }
    }

    /// <summary>The shell step: once the player holds the shell, the round's Goombas appear to throw it at.</summary>
    private void RevealTargetsOnHold()
    {
        if (CurrentRound == null || GameStateManager.Instance is not { Held: HeldItem.Shell }) return;
        var hidden = CurrentRound.transform.Find(RevealOnHoldName);
        if (hidden == null || hidden.gameObject.activeSelf) return;
        hidden.gameObject.SetActive(true);
        AudioManager.Play(Sfx.PowerUpAppear);
        OnMessage?.Invoke("Got it! Now throw it at a Goomba");
        Debug.Log("[Tutorial] Shell in hand: Goombas out.");
    }

    private bool InRound(Component target)
    {
        if (ReferenceEquals(target, null)) return false;
        foreach (var t in _roundTargets) if (ReferenceEquals(t, target)) return true;
        return false;
    }

    private void HandleEvent(GameEvent evt, ResolutionResult result)
    {
        var step = Current;
        if (step == null || _beat >= 0f || !result.Resolved || !InRound(result.Target)) return;
        bool counts = step.kind switch
        {
            Kind.HitBlock => evt.actionLabel == "jump" && result.Target is QuestionBlock,
            Kind.BreakBrick => evt.actionLabel == "punch" && result.Target is BrickBlock,
            Kind.StompEnemy => evt.actionLabel == "stomp" && result.Target is Goomba && !(result.Target is Koopa),
            _ => false,
        };
        if (counts) Count(step);
    }

    private void HandleImpact(GameEvent evt, Component target)
    {
        var step = Current;
        if (step == null || _beat >= 0f || evt.actionLabel != "throw" || !InRound(target)) return;
        if (!(target is Goomba) || target is Koopa) return;
        // Fireball step: the flower is handed out and no shell can be held; shell step: the flower is taken away.
        if (step.kind == Kind.FireballEnemy || step.kind == Kind.ShellEnemy) Count(step);
    }

    /// <summary>Removes the round's copy and anything it spawned (pickups, effects), and the enemies of a station used in place.</summary>
    private void ClearRound(Step step)
    {
        if (step == null) return;
        if (UsesCopies(step.kind))
        {
            if (CurrentRound != null && CurrentRound != step.station) InteractableBase.SafeDestroy(CurrentRound);
        }
        else if (step.station != null)
        {
            foreach (var cannon in step.station.GetComponentsInChildren<BulletBillCannon>()) cannon.gameObject.SetActive(false);
        }
        CurrentRound = null;
        _roundTargets.Clear();
    }

    private static void CleanUpBills()
    {
        foreach (var bill in FindObjectsByType<BulletBill>()) InteractableBase.SafeDestroy(bill.gameObject);
    }
}
