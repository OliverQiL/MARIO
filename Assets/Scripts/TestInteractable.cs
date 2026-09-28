using UnityEngine;

/// <summary>
/// Generic test target (NetworkSmokeTest). Responds to a configurable list of
/// actions, awards a configurable score, and is permanently spent after one
/// hit — enough to exercise Gates 4-6 without real game content.
/// </summary>
public class TestInteractable : InteractableBase
{
    [SerializeField] private string[] respondsToActions = { "stomp" };
    [SerializeField] private int scoreValue = 100;

    private bool _spent;

    public override bool IsActionable => !_spent;

    public override bool RespondsTo(string actionLabel) => System.Array.IndexOf(respondsToActions, actionLabel) >= 0;

    public override void ResolveHit(string actionLabel, GameStateManager gameState)
    {
        _spent = true;
        gameState.AddScore(scoreValue);
        Debug.Log($"[TestInteractable] '{name}' resolved by '{actionLabel}' — now spent.");
    }
}
