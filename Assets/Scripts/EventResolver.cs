using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Stands in for the eventual GAME_EVENT JSON from the gateway. Once
/// NetworkClient exists, it'll deserialize into one of these and call
/// EventResolver.Resolve directly — nothing else changes.
/// </summary>
public struct GameEvent
{
    public string actionLabel;
    public float confidence;
    public string eventId;
    public double timestamp;

    public GameEvent(string actionLabel, float confidence, string eventId)
    {
        this.actionLabel = actionLabel;
        this.confidence = confidence;
        this.eventId = eventId;
        timestamp = Time.realtimeSinceStartupAsDouble;
    }
}

/// <summary>
/// Design report Step 5: resolves an incoming classified action through an
/// ordered six-gate chain. Rejection at any gate stops processing there and
/// logs which gate rejected it, so "not registered" (Gates 1-2) is
/// distinguishable from "not permitted" (Gates 3-5).
/// </summary>
[RequireComponent(typeof(GameStateManager))]
public class EventResolver : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameStateManager gameState;
    [SerializeField] private Camera arCamera;

    [Header("Gate 2 - Validity")]
    [SerializeField] private float minConfidence = 0.6f;
    private readonly HashSet<string> _seenEventIds = new();

    [Header("Gate 4 - Spatial gating")]
    [SerializeField] private float interactionRadius = 1.5f;
    [SerializeField] private float facingAngleThreshold = 45f;
    [SerializeField] private LayerMask interactableLayerMask = ~0;

    private void Awake()
    {
        if (gameState == null) gameState = GetComponent<GameStateManager>();
        if (arCamera == null) arCamera = Camera.main;
    }

    public void Resolve(GameEvent evt)
    {
        // Gate 1 — Session state
        if (!gameState.IsSessionReady)
        {
            Debug.Log($"[EventResolver] REJECTED @ Gate 1 (session not ready): '{evt.actionLabel}'");
            return;
        }

        // Gate 2 — Validity: confidence threshold + duplicate event_id
        if (evt.confidence < minConfidence)
        {
            Debug.Log($"[EventResolver] REJECTED @ Gate 2 (confidence {evt.confidence:F2} < {minConfidence:F2}): '{evt.actionLabel}'");
            return;
        }
        if (!_seenEventIds.Add(evt.eventId))
        {
            Debug.Log($"[EventResolver] REJECTED @ Gate 2 (duplicate event_id '{evt.eventId}'): '{evt.actionLabel}'");
            return;
        }

        // Gate 3 — Player capability
        if (!HasCapabilityFor(evt.actionLabel))
        {
            Debug.Log($"[EventResolver] REJECTED @ Gate 3 (capability): '{evt.actionLabel}'");
            return;
        }

        // Gate 4 — Spatial gating: a responsive target within radius and facing angle
        if (!TryFindTargetInRange(evt.actionLabel, out var target))
        {
            Debug.Log($"[EventResolver] REJECTED @ Gate 4 (no target in range/facing): '{evt.actionLabel}'");
            return;
        }

        // Gate 5 — Target state
        if (!target.IsActionable)
        {
            Debug.Log($"[EventResolver] REJECTED @ Gate 5 (target not actionable): '{evt.actionLabel}'");
            return;
        }

        // Gate 6 — Resolution
        target.ResolveHit(evt.actionLabel, gameState);
        Debug.Log($"[EventResolver] RESOLVED: '{evt.actionLabel}' on {((Component)target).name}");
        // TODO once those modules exist: spawn effects, AudioManager cue, NetworkClient HAPTIC_CMD.
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
