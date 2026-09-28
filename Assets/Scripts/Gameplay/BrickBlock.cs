using UnityEngine;

/// <summary>
/// Brick block at chest height. Broken by a bare-handed strike (fireball
/// without a flower) or by a fireball projectile.
/// </summary>
public class BrickBlock : InteractableBase
{
    [SerializeField] private int scoreValue = 50;
    [SerializeField] private GameObject coinPopPrefab;

    private bool _broken;

    public override bool IsActionable => !_broken;
    public override bool RespondsTo(string actionLabel) => actionLabel == "strike" || actionLabel == "fireball";

    public override void ResolveHit(string actionLabel, GameStateManager gameState)
    {
        _broken = true;
        gameState.AddScore(scoreValue);
        if (coinPopPrefab != null) Instantiate(coinPopPrefab, transform.position, Quaternion.identity);
        SafeDestroy(gameObject);
    }
}
