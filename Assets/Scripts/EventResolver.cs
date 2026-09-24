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

    public bool Resolved => Gate == 6;
    /// <summary>Gates 1-2: the action was never registered (wrong phase, low confidence, duplicate).</summary>
    public bool NotRegistered => Gate <= 2;
    /// <summary>Gates 3-5: the action was recognised but isn't permitted right now.</summary>
    public bool NotPermitted => Gate >= 3 && Gate <= 5;

    public ResolutionResult(int gate, string reason, Component target = null)
    {
        Gate = gate;
        Reason = reason;
        Target = target;
    }
}

/// <summary>
/// Design report Step 5: resolves an incoming classified action through an
/// ordered six-gate chain. Rejection at any gate stops processing there and
/// logs which gate rejected it, so "not registered" (Gates 1-2) is
/// distinguishable from "not permitted" (Gates 3-5).
///
/// Resolve() returns the outcome and also raises OnEventProcessed, which
/// NetworkClient (EVENT_ACK / HAPTIC_CMD), the HUD and AudioManager listen to.
/// EventResolver itself stays dependent only on GameStateManager.
/// </summary>
[RequireComponent(typeof(GameStateManager))]
public class EventResolver : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameStateManager gameState;
    [SerializeField] private Camera arCamera;

    [Header("Gate 2 - Validity")]
    [SerializeField] private float minConfidence = 0.6f;
    [Tooltip("Oldest event IDs are forgotten beyond this many, so dedup memory stays bounded over a long session.")]
    [SerializeField] private int dedupHistorySize = 1024;
    private readonly HashSet<string> _seenEventIds = new();
    private readonly Queue<string> _seenOrder = new();

    [Header("Gate 4 - Spatial gating")]
    [SerializeField] private float interactionRadius = 1.5f;
    [SerializeField] private float facingAngleThreshold = 45f;
    [SerializeField] private LayerMask interactableLayerMask = ~0;

    /// <summary>Raised after every Resolve(), whatever the outcome.</summary>
    public event Action<GameEvent, ResolutionResult> OnEventProcessed;

    private void Awake()
    {
        if (gameState == null) gameState = GetComponent<GameStateManager>();
        if (arCamera == null) arCamera = Camera.main;
    }

    public ResolutionResult Resolve(GameEvent evt)
    {
        var result = RunGates(evt);

        if (result.Resolved)
            Debug.Log($"[EventResolver] RESOLVED: '{evt.actionLabel}' on {result.Target.name}");
        else
            Debug.Log($"[EventResolver] REJECTED @ Gate {result.Gate} ({result.Reason}): '{evt.actionLabel}'");

        OnEventProcessed?.Invoke(evt, result);
        return result;
    }

    private ResolutionResult RunGates(GameEvent evt)
    {
        // Gate 1 — Session state
        if (!gameState.IsSessionReady)
            return new ResolutionResult(1, "session not ready");

        // Gate 2 — Validity: confidence threshold + duplicate event_id
        if (evt.confidence < minConfidence)
            return new ResolutionResult(2, $"confidence {evt.confidence:F2} < {minConfidence:F2}");
        if (!MarkSeen($"{evt.sessionId}/{evt.eventId}"))
            return new ResolutionResult(2, $"duplicate event_id '{evt.eventId}'");

        // Gate 3 — Player capability
        if (!HasCapabilityFor(evt.actionLabel))
            return new ResolutionResult(3, "capability");

        // Gate 4 — Spatial gating: a responsive target within radius and facing angle
        if (!TryFindTargetInRange(evt.actionLabel, out var target))
            return new ResolutionResult(4, "no target in range/facing");

        // Gate 5 — Target state
        if (!target.IsActionable)
            return new ResolutionResult(5, "target not actionable");

        // Gate 6 — Resolution. Effects, audio and haptics hang off OnEventProcessed.
        target.ResolveHit(evt.actionLabel, gameState);
        return new ResolutionResult(6, "resolved", (Component)target);
    }

    /// <summary>Records an event key; false if it was already seen. Bounded FIFO history.</summary>
    private bool MarkSeen(string key)
    {
        if (!_seenEventIds.Add(key)) return false;
        _seenOrder.Enqueue(key);
        while (_seenOrder.Count > dedupHistorySize)
            _seenEventIds.Remove(_seenOrder.Dequeue());
        return true;
    }

    private bool HasCapabilityFor(string actionLabel)
    {
        switch (actionLabel)
        {
            case "fireball":
                return gameState.HasFireFlower;
            default:
                return true;
        }
    }

    private bool TryFindTargetInRange(string actionLabel, out IInteractable target)
    {
        var colliders = Physics.OverlapSphere(arCamera.transform.position, interactionRadius, interactableLayerMask);

        IInteractable best = null;
        float bestAngle = facingAngleThreshold;

        foreach (var col in colliders)
        {
            var candidate = col.GetComponent<IInteractable>();
            if (candidate == null) continue;
            if (!candidate.RespondsTo(actionLabel)) continue;

            var toTarget = (col.transform.position - arCamera.transform.position).normalized;
            var angle = Vector3.Angle(arCamera.transform.forward, toTarget);
            if (angle <= bestAngle)
            {
                bestAngle = angle;
                best = candidate;
            }
        }

        target = best;
        return best != null;
    }
}
