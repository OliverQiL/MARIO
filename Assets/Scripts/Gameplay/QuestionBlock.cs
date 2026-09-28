using UnityEngine;

/// <summary>
/// "?" block (Docs/GAME_DESIGN.md). Floats overhead; a jump underneath hits it.
/// Drops a fire flower to the floor if the player doesn't have one, otherwise
/// pops a coin. Then it's spent (Gate 5 rejects further hits).
/// </summary>
public class QuestionBlock : InteractableBase
{
    [SerializeField] private int coinValue = 100;
    [SerializeField] private GameObject coinPopPrefab;
    [SerializeField] private Pickup fireFlowerPrefab;
    [SerializeField] private Material spentMaterial;

    private bool _spent;

    public override bool IsActionable => !_spent;
    public override bool RespondsTo(string actionLabel) => actionLabel == "jump";

    public override void ResolveHit(string actionLabel, GameStateManager gameState)
    {
        _spent = true;
        if (spentMaterial != null)
            foreach (var r in GetComponentsInChildren<Renderer>()) r.sharedMaterial = spentMaterial;

        if (!gameState.HasFireFlower && fireFlowerPrefab != null)
        {
            // Drop onto the floor under the block (the stage root is on the floor).
            float floorY = transform.parent != null ? transform.parent.position.y : transform.position.y - 1.5f;
            var at = new Vector3(transform.position.x, floorY, transform.position.z);
            Instantiate(fireFlowerPrefab, at, Quaternion.identity, transform.parent);
            Debug.Log("[QuestionBlock] Fire flower dropped.");
        }
        else
        {
            gameState.AddScore(coinValue);
            if (coinPopPrefab != null) Instantiate(coinPopPrefab, transform.position + Vector3.up * 0.15f, Quaternion.identity);
        }
    }
}
