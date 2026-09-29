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
    [Tooltip("Horizontal distance past the block (away from the player) where the flower lands. Must exceed the pickup's collect radius.")]
    [SerializeField] private float flowerLandingOffset = 0.5f;

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
            // The player is under the block, so a flower dropped straight down
            // would be collected before it's seen. Land it on the far side,
            // at least flowerLandingOffset from the player, falling from the block.
            float floorY = transform.parent != null ? transform.parent.position.y : transform.position.y - 1.5f;
            var at = transform.position + AwayFromPlayer() * flowerLandingOffset;
            at.y = floorY;
            var flower = Instantiate(fireFlowerPrefab, at, Quaternion.identity, transform.parent);
            flower.DropFrom(transform.position);
            Debug.Log("[QuestionBlock] Fire flower dropped.");
        }
        else
        {
            gameState.AddScore(coinValue);
            if (coinPopPrefab != null) Instantiate(coinPopPrefab, transform.position + Vector3.up * 0.15f, Quaternion.identity);
        }
    }

    /// <summary>Horizontal unit vector from the player to the block (the camera's facing if directly beneath).</summary>
    private Vector3 AwayFromPlayer()
    {
        var cam = Camera.main;
        if (cam == null) return Vector3.forward;
        var d = transform.position - cam.transform.position;
        d.y = 0f;
        if (d.sqrMagnitude < 0.05f * 0.05f)
        {
            d = cam.transform.forward;
            d.y = 0f;
        }
        return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
    }
}
