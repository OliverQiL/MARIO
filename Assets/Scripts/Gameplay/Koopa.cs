using UnityEngine;

/// <summary>
/// Koopa (Docs/GAME_DESIGN.md): walks and hurts like a Goomba. The first stomp
/// knocks it into a harmless, stationary shell; a second stomp clears it (+200).
/// A fireball or a thrown shell defeats it outright, walking or shelled.
///
/// A shell on the floor can be picked up with a clap: it leaves the stage and
/// the player holds it (GameStateManager.Held) until they throw it, when it
/// slides along the floor as a ThrownShell, knocking out enemies in its path.
/// </summary>
public class Koopa : Goomba
{
    [Tooltip("Children hidden once it's a shell (head, feet).")]
    [SerializeField] private GameObject[] hideInShell;
    [SerializeField] private float shellHitHeight = 0.08f;

    private bool _inShell;

    public bool InShell => _inShell;
    protected override bool IsWalking => !_inShell;
    public override Vector3 HitPoint => transform.position + Vector3.up * (_inShell ? shellHitHeight : bodyHeight);

    public override bool RespondsTo(string actionLabel) =>
        base.RespondsTo(actionLabel) || (actionLabel == "clap" && _inShell);

    public override bool IsActionableFor(string actionLabel)
    {
        if (_dead) return false;
        if (actionLabel == "clap")
        {
            var gs = GameStateManager.Instance;
            return _inShell && (gs == null || gs.Held == HeldItem.None);
        }
        return true;
    }

    public override void ResolveHit(string actionLabel, GameStateManager gameState)
    {
        if (actionLabel == "stomp" && !_inShell)
        {
            _inShell = true;
            foreach (var go in hideInShell) if (go != null) go.SetActive(false);
            Debug.Log("[Koopa] Knocked into its shell.");
            return;
        }
        if (actionLabel == "clap")
        {
            if (!gameState.PickUp(HeldItem.Shell)) return;
            _dead = true; // gone from the stage; the player has it now
            Debug.Log("[Koopa] Shell picked up.");
            SafeDestroy(gameObject);
            return;
        }
        Defeat(actionLabel, gameState);
    }
}
