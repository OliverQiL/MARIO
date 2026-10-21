using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A classified action, as delivered by the gateway's GAME_EVENT (NetworkClient
/// builds these from the wire format). sessionId + eventId together are the
/// unique key for dedup, so the gateway can restart numbering per session.
/// </summary>
public struct GameEvent
{
    public string actionLabel;
    public float confidence;
    public string eventId;
    public string sessionId;
    public long windowId;
    /// <summary>Gateway clock (Unix seconds). Not comparable with the phone's clock — logging only.</summary>
    public double timestamp;

    public GameEvent(string actionLabel, float confidence, string eventId, string sessionId = "local", long windowId = 0, double timestamp = 0)
    {
        this.actionLabel = actionLabel;
        this.confidence = confidence;
        this.eventId = eventId;
        this.sessionId = sessionId;
        this.windowId = windowId;
        this.timestamp = timestamp;
    }
}

/// <summary>
/// Outcome of running one event through the gate chain. Gate is 1-5 for the
/// rejecting gate, 6 for a successful resolution.
/// </summary>
public readonly struct ResolutionResult
{
    public readonly int Gate;
    public readonly string Reason;
    public readonly Component Target;
    /// <summary>Resolved as a player-state change only (a duck, a jump that hit nothing): no target, no feedback, no haptic.</summary>
    public readonly bool StateOnly;

    public bool Resolved => Gate == 6;
    /// <summary>Gates 1-2: the action was never registered (wrong phase, low confidence, duplicate).</summary>
    public bool NotRegistered => Gate <= 2;
    /// <summary>Gates 3-5: the action was recognised but isn't permitted right now.</summary>
    public bool NotPermitted => Gate >= 3 && Gate <= 5;
    /// <summary>A projectile (fireball or shell) was launched; its impacts (if any) arrive later via OnImpact.</summary>
    public bool LaunchedProjectile => Resolved && (Reason == EventResolver.ProjectileReason || Reason == EventResolver.ShellReason);
    /// <summary>Gate 2: another report of a motion that was already resolved (the gateway forwards every classification).</summary>
    public bool IsRepeat => Gate == 2 && Reason == EventResolver.RepeatReason;
    /// <summary>A held shell was thrown.</summary>
    public bool ThrewShell => Resolved && Reason == EventResolver.ShellReason;

    public ResolutionResult(int gate, string reason, Component target = null, bool stateOnly = false)
    {
        Gate = gate;
        Reason = reason;
        Target = target;
        StateOnly = stateOnly;
    }

    public static ResolutionResult State(string reason) => new(6, reason, null, true);
}

/// <summary>
/// Design report Step 5: resolves an incoming classified action through an
/// ordered six-gate chain. Rejection at any gate stops processing there, so
/// "not registered" (Gates 1-2) is distinguishable from "not permitted" (3-5).
///
/// Per-action outcomes and targeting follow Docs/GAME_DESIGN.md. Every action
/// is discrete (one event per motion; protocol v2):
/// - jump: airborne window; grabs the stage goal (flag or axe) if the player
///   is at it, else hits an overhead block if there is one
/// - stomp: attack on an enemy at the player's feet (a Koopa takes two)
/// - squat: ducked window; enters a warp pipe if the player is standing on one
/// - throw: throws the held item (a shell) if there is one, else a fireball
///   with the fire flower (Gate 3 caps how many are in flight); else Gate 3
/// - punch: forward punch on a brick (or the boss) in reach at chest height
/// - clap: picks up a Koopa shell from the floor in front (Gate 3 if the
///   hands are already full)
/// - idle: the classifier's catch-all for other movement; resolved state-only
///   in any state and otherwise ignored
/// The gateway forwards every classification, so one motion arrives as a run
/// of events with the same action: only the first is processed, the rest are
/// dropped at Gate 2 ("repeat of the same motion"), in every state.
/// During game over or game clear everything is dropped at Gate 1, except a
/// jump (after a short grace period), which starts a new game.
///
/// Targets come from InteractableBase's registry (no physics). Resolve()
/// returns the outcome and raises OnEventProcessed; projectile hits land
/// later and raise OnImpact. NetworkClient (ACK/haptics) and the HUD listen.
/// EventResolver itself depends only on GameStateManager.
/// </summary>
[RequireComponent(typeof(GameStateManager))]
public class EventResolver : MonoBehaviour
{
    public const string ProjectileReason = "projectile";
    public const string ShellReason = "shell";
    public const string GoalReason = "goal";
    /// <summary>The classifier's "none of the six" class: movement that isn't a game action. Resolved quietly, ignored.</summary>
    public const string IdleAction = "idle";
    public const string RepeatReason = "repeat of the same motion";

    /// <summary>The v2 action labels the classifier sends (Docs/GATEWAY_PROTOCOL.md).</summary>
    public static readonly string[] Actions = { "jump", "stomp", "squat", "throw", "punch", "clap" };

    [Header("References")]
    [SerializeField] private GameStateManager gameState;
    [SerializeField] private Camera arCamera;

    [Header("Gate 2 - Validity")]
    [SerializeField] private float minConfidence = 0.6f;
    [Tooltip("Oldest event IDs are forgotten beyond this many, so dedup memory stays bounded over a long session.")]
    [SerializeField] private int dedupHistorySize = 1024;
    private readonly HashSet<string> _seenEventIds = new();
    private readonly Queue<string> _seenOrder = new();

    [Header("Gate 2 - Repeats (one event per motion)")]
    [Tooltip("The gateway forwards every classification, so one motion arrives as several events with the same action. " +
             "A report within this many seconds of the previous report of that action is the same motion.")]
    [SerializeField] private float repeatReleaseSeconds = 0.5f;
    [Tooltip("A run of reports of one action lasting longer than this counts as a new motion " +
             "(e.g. two stomps back to back). Longer than the classifier's 1 s window.")]
    [SerializeField] private float repeatMaxRunSeconds = 1.2f;
    private readonly Dictionary<string, (double start, double last)> _runs = new();
    /// <summary>Seconds, for the repeat filter. Tests replace it with a controlled clock.</summary>
    public Func<double> Clock = () => Time.realtimeSinceStartupAsDouble;

    [Header("Gate 4 - Targeting (metres / degrees, horizontal)")]
    [SerializeField] private float stompRange = 0.7f;
    [SerializeField] private float stompHalfAngle = 70f;
    [SerializeField] private float strikeRange = 0.8f;
    [SerializeField] private float strikeHalfAngle = 35f;
    [Tooltip("Clap: how far in front a shell on the floor can be picked up from.")]
    [SerializeField] private float clapRange = 0.8f;
    [SerializeField] private float clapHalfAngle = 60f;
    [Tooltip("Max horizontal offset of an overhead block for a jump to hit it.")]
    [SerializeField] private float jumpBlockRadius = 0.5f;
    [Tooltip("Max horizontal distance from a warp pipe's centre for a squat to enter it.")]
    [SerializeField] private float warpPipeRadius = 0.35f;

    [Header("Player State Windows (seconds after the event arrives)")]
    [SerializeField] private float airborneSeconds = 1f;
    [SerializeField] private float duckSeconds = 1f;

    [Header("Fireball")]
    [SerializeField] private Fireball fireballPrefab;
    [Tooltip("Gate 3: fireballs allowed in flight at once.")]
    [SerializeField] private int maxFireballsInFlight = 2;

    [Header("Thrown Shell")]
    [SerializeField] private ThrownShell thrownShellPrefab;

    [Header("Game Over")]
    [Tooltip("Seconds after game over before a jump can restart, so the jump that was in progress doesn't.")]
    [SerializeField] private float restartGraceSeconds = 2f;

    /// <summary>Raised after every Resolve(), whatever the outcome.</summary>
    public event Action<GameEvent, ResolutionResult> OnEventProcessed;
    /// <summary>Raised when a deferred effect (a fireball) hits its target.</summary>
    public event Action<GameEvent, Component> OnImpact;

    private void Awake()
    {
        if (gameState == null) gameState = GetComponent<GameStateManager>();
        if (arCamera == null) arCamera = Camera.main;
    }

    public ResolutionResult Resolve(GameEvent evt)
    {
        var result = RunGates(evt);
        gameState.RecordAction(result.Resolved && !result.StateOnly, result.NotPermitted);

        if (evt.actionLabel == IdleAction) { } // several a second: not logged
        else if (result.Resolved)
            Debug.Log($"[EventResolver] RESOLVED: '{evt.actionLabel}' ({(result.Target != null ? "on " + result.Target.name : result.Reason)})");
        else
            Debug.Log($"[EventResolver] REJECTED @ Gate {result.Gate} ({result.Reason}): '{evt.actionLabel}'");

        OnEventProcessed?.Invoke(evt, result);
        return result;
    }

    private ResolutionResult RunGates(GameEvent evt)
    {
        // Idle: the player moved, but not one of the six actions. Acknowledged and ignored in every
        // state — no gates, no haptic, no HUD feedback, not counted towards accuracy.
        if (evt.actionLabel == IdleAction) return ResolutionResult.State(IdleAction);

        // Gate 2 (first, in every state) — repeats: the gateway sends every classification, so one
        // motion arrives as several events. Only the first of a run is processed; the rest are
        // ACKed and ignored. Low-confidence reports don't count (they're rejected below as usual).
        if (evt.confidence >= minConfidence && IsRepeat(evt.actionLabel))
            return new ResolutionResult(2, RepeatReason);

        // Game over / game clear: only a (deliberate, later) jump does anything — it restarts.
        if (gameState.IsFinished)
        {
            if (evt.actionLabel == "jump" && evt.confidence >= minConfidence
                && gameState.SecondsSinceGameOver >= restartGraceSeconds && MarkSeen(Key(evt)))
            {
                gameState.RestartGame();
                return ResolutionResult.State("restart");
            }
            return new ResolutionResult(1, gameState.IsGameClear ? "game clear" : "game over");
        }

        // Gate 1 — Session state
        if (!gameState.IsSessionReady)
            return new ResolutionResult(1, "session not ready");

        // Gate 2 — Validity: confidence threshold + duplicate event_id
        if (evt.confidence < minConfidence)
            return new ResolutionResult(2, $"confidence {evt.confidence:F2} < {minConfidence:F2}");
        if (!MarkSeen(Key(evt)))
            return new ResolutionResult(2, $"duplicate event_id '{evt.eventId}'");

        // Gates 3-6, per action.
        switch (evt.actionLabel)
        {
            case "squat":
            {
                gameState.Duck(duckSeconds);
                // Standing on a warp pipe? Squatting enters it. Otherwise just a duck.
                var pipe = FindTarget("squat", warpPipeRadius, 180f, dy => dy < -0.5f);
                if (pipe == null || !pipe.IsActionableFor("squat")) return ResolutionResult.State("ducked");
                return Hit(pipe, "squat");
            }

            case "jump":
            {
                gameState.Jump(airborneSeconds);
                // At the flag (or the axe): the jump grabs it and clears the stage.
                var goal = StageGoal.Current;
                if (goal != null && goal.TryJump()) return new ResolutionResult(6, GoalReason, goal);
                var block = FindTarget("jump", jumpBlockRadius, 180f, dy => dy > -0.1f);
                if (block == null) return ResolutionResult.State("airborne"); // a jump needs no target
                return Hit(block, "jump");
            }

            case "throw":
                // A held item goes first: throwing a shell you're carrying.
                if (gameState.Held == HeldItem.Shell)
                {
                    gameState.ThrowHeld();
                    LaunchShell(evt);
                    return new ResolutionResult(6, ShellReason);
                }
                // Gate 3 — capability: nothing to throw without a fire flower.
                if (!gameState.HasFireFlower)
                    return new ResolutionResult(3, "nothing to throw");
                if (Fireball.InFlight >= maxFireballsInFlight)
                    return new ResolutionResult(3, "fireballs recharging");
                LaunchFireball(evt);
                return new ResolutionResult(6, ProjectileReason);

            case "punch":
                return Hit(FindTarget("punch", strikeRange, strikeHalfAngle, dy => dy > -0.9f && dy < 0.3f), "punch");

            case "clap":
                // Gate 3 — capability: one item at a time.
                if (gameState.Held != HeldItem.None)
                    return new ResolutionResult(3, "hands full");
                return Hit(FindTarget("clap", clapRange, clapHalfAngle, dy => dy < -0.5f), "clap");

            case "stomp":
                return Hit(FindTarget("stomp", stompRange, stompHalfAngle, dy => dy < -0.5f), "stomp");

            default:
                return new ResolutionResult(2, $"unknown action '{evt.actionLabel}'");
        }
    }

    /// <summary>Gates 4-6 for a targeted action.</summary>
    private ResolutionResult Hit(InteractableBase target, string label)
    {
        // Gate 4 — Spatial gating
        if (target == null) return new ResolutionResult(4, "no target in range/facing");
        // Gate 5 — Target state
        if (!target.IsActionableFor(label)) return new ResolutionResult(5, "target not actionable");
        // Gate 6 — Resolution
        target.ResolveHit(label, gameState);
        return new ResolutionResult(6, "resolved", target);
    }

    private void LaunchFireball(GameEvent evt)
    {
        if (fireballPrefab == null)
        {
            Debug.LogWarning("[EventResolver] No fireball prefab assigned.");
            return;
        }
        var cam = arCamera.transform;
        var origin = cam.position + cam.forward * 0.3f - Vector3.up * 0.2f;
        var fireball = Instantiate(fireballPrefab, origin, Quaternion.identity);
        fireball.Launch(cam.forward, target => OnImpact?.Invoke(evt, target));
    }

    private void LaunchShell(GameEvent evt)
    {
        if (thrownShellPrefab == null)
        {
            Debug.LogWarning("[EventResolver] No thrown shell prefab assigned.");
            return;
        }
        var cam = arCamera.transform;
        var facing = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
        if (facing.sqrMagnitude < 1e-4f) facing = Vector3.ProjectOnPlane(cam.up, Vector3.up);
        facing.Normalize();
        // Dropped at the player's feet, just in front, and sent sliding along the floor.
        float floorY = cam.position.y - PlayerBody.EyeHeightOr(1.55f); // calibrated eye height above the floor
        var origin = new Vector3(cam.position.x, floorY + 0.06f, cam.position.z) + facing * 0.35f;
        var shell = Instantiate(thrownShellPrefab, origin, Quaternion.identity);
        shell.Launch(facing, target => OnImpact?.Invoke(evt, target));
    }

    /// <summary>
    /// Nearest registered target responding to <paramref name="label"/> within
    /// <paramref name="range"/> (horizontal) and <paramref name="halfAngle"/> of
    /// facing, whose height relative to the eyes passes <paramref name="heightFilter"/>.
    /// Actionable targets are preferred, so Gate 5 only fires when the only
    /// candidate in reach is spent.
    /// </summary>
    private InteractableBase FindTarget(string label, float range, float halfAngle, Func<float, bool> heightFilter)
    {
        var eye = arCamera.transform.position;
        var facing = Vector3.ProjectOnPlane(arCamera.transform.forward, Vector3.up);

        InteractableBase best = null;
        float bestScore = float.PositiveInfinity;
        foreach (var t in InteractableBase.All)
        {
            if (t == null || !t.RespondsTo(label)) continue;

            var offset = t.HitPoint - eye;
            if (!heightFilter(offset.y)) continue;
            offset.y = 0f;
            float distance = offset.magnitude;
            if (distance > range) continue;
            if (halfAngle < 180f && distance > 0.05f && facing.sqrMagnitude > 1e-4f
                && Vector3.Angle(facing, offset) > halfAngle) continue;

            float score = distance + (t.IsActionableFor(label) ? 0f : 1000f);
            if (score < bestScore) { bestScore = score; best = t; }
        }
        return best;
    }

    /// <summary>
    /// Records a report of <paramref name="action"/> and says whether it continues a run already
    /// processed: the previous report was under repeatReleaseSeconds ago and the run has lasted
    /// under repeatMaxRunSeconds. Otherwise it starts a new run (a new motion).
    /// </summary>
    private bool IsRepeat(string action)
    {
        double now = Clock();
        if (_runs.TryGetValue(action, out var run) && now - run.last < repeatReleaseSeconds && now - run.start < repeatMaxRunSeconds)
        {
            _runs[action] = (run.start, now);
            return true;
        }
        _runs[action] = (now, now);
        return false;
    }

    private static string Key(GameEvent evt) => $"{evt.sessionId}/{evt.eventId}";

    /// <summary>Records an event key; false if it was already seen. Bounded FIFO history.</summary>
    private bool MarkSeen(string key)
    {
        if (!_seenEventIds.Add(key)) return false;
        _seenOrder.Enqueue(key);
        while (_seenOrder.Count > dedupHistorySize)
            _seenEventIds.Remove(_seenOrder.Dequeue());
        return true;
    }
}
