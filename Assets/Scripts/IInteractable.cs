/// <summary>
/// Anything EventResolver's gate chain can target — a block, an enemy, etc.
/// Kept deliberately minimal: what action it responds to, whether it's
/// currently actionable (unspent block, living enemy), and how it resolves.
/// </summary>
public interface IInteractable
{
    bool IsActionable { get; }
    bool RespondsTo(string actionLabel);
    void ResolveHit(string actionLabel, GameStateManager gameState);
}
