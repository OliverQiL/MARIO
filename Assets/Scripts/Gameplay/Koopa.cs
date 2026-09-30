using UnityEngine;

/// <summary>
/// Koopa (Docs/GAME_DESIGN.md, pass 2): walks and hurts like a Goomba. The
/// first stomp knocks it into a harmless, stationary shell; a second, separate
/// stomp clears it (+200). A fireball defeats it outright, walking or shelled.
///
/// Stomp is continuous (re-sent every classification window), so "separate"
/// means a stomp motion that started after the shell appeared
/// (GameStateManager.StompStartedAt). Otherwise one held stomp would shell and
/// clear it within 0.1 s. Until then the shell is at Gate 5 for stomp, which
/// the HUD and haptics keep silent for continuous actions.
/// </summary>
public class Koopa : Goomba
{
    [Tooltip("Children hidden once it's a shell (head, feet).")]
    [SerializeField] private GameObject[] hideInShell;
    [SerializeField] private float shellHitHeight = 0.08f;

    private bool _inShell;
    private float _shelledAt = float.NegativeInfinity;

    public bool InShell => _inShell;
    protected override bool IsWalking => !_inShell;
    public override Vector3 HitPoint => transform.position + Vector3.up * (_inShell ? shellHitHeight : bodyHeight);

    public override bool IsActionableFor(string actionLabel)
    {
        if (_dead) return false;
        if (!_inShell || actionLabel != "stomp") return true;
        var gs = GameStateManager.Instance;
        return gs != null && gs.StompStartedAt > _shelledAt;
    }

    public override void ResolveHit(string actionLabel, GameStateManager gameState)
    {
        if (actionLabel == "stomp" && !_inShell)
        {
            _inShell = true;
            _shelledAt = Time.time;
            foreach (var go in hideInShell) if (go != null) go.SetActive(false);
            Debug.Log("[Koopa] Knocked into its shell.");
            return;
        }
        Defeat(actionLabel, gameState);
    }
}
