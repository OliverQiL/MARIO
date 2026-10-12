using UnityEngine;

/// <summary>
/// Brick block at chest height. Broken by a forward punch or by a fireball
/// projectile.
/// </summary>
public class BrickBlock : InteractableBase
{
    [SerializeField] private int scoreValue = 50;
    [SerializeField] private GameObject coinPopPrefab;
    [SerializeField] private GameObject debrisPrefab;

    private bool _broken;

    public override bool IsActionable => !_broken;
    public override bool RespondsTo(string actionLabel) => actionLabel == "punch" || actionLabel == "fireball";

    public override void ResolveHit(string actionLabel, GameStateManager gameState)
    {
        _broken = true;
        gameState.AddScore(scoreValue);
        if (coinPopPrefab != null) Instantiate(coinPopPrefab, transform.position, Quaternion.identity);
        if (debrisPrefab != null) Instantiate(debrisPrefab, transform.position, transform.rotation);
        SafeDestroy(gameObject);
    }
}
