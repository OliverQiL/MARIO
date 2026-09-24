using UnityEngine;

/// <summary>
/// Placeholder target for testing EventResolver — stands in for a real
/// question-mark block or enemy until those exist. Attach to any GameObject
/// with a Collider.
/// </summary>
public class TestInteractable : MonoBehaviour, IInteractable
{
    [SerializeField] private string[] respondsToActions = { "stomp" };
    [SerializeField] private int scoreValue = 100;

    private bool _spent;

    public bool IsActionable => !_spent;

    public bool RespondsTo(string actionLabel)
    {
        return System.Array.IndexOf(respondsToActions, actionLabel) >= 0;
    }

    public void ResolveHit(string actionLabel, GameStateManager gameState)
    {
        _spent = true;
        gameState.AddScore(scoreValue);
        Debug.Log($"[TestInteractable] '{name}' resolved by '{actionLabel}' — now spent.");
        // Real spawn effects / destroy timing come later; spent state alone
        // is enough to prove Gate 5 correctly rejects a second hit.
    }
}
