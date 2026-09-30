using UnityEngine;

/// <summary>
/// "?" block (Docs/GAME_DESIGN.md). Floats overhead; a jump underneath hits it.
/// Drops a random power-up: a fire flower or a 1-Up mushroom. If the one
/// rolled would be wasted (a flower while holding one, a 1-Up at the life
/// cap), it drops the other; if both would be, it pops a coin instead.
/// Then it's spent (Gate 5 rejects further hits).
/// </summary>
public class QuestionBlock : InteractableBase
{
    [SerializeField] private int coinValue = 100;
    [SerializeField] private GameObject coinPopPrefab;
    [SerializeField] private Pickup fireFlowerPrefab;
    [SerializeField] private Pickup oneUpPrefab;
    [Tooltip("Chance the power-up is a 1-Up rather than a fire flower.")]
    [Range(0f, 1f)] [SerializeField] private float oneUpChance = 0.5f;
    [SerializeField] private Material spentMaterial;
    [Tooltip("Horizontal distance past the block (away from the player) where a dropped pickup lands. Must exceed the pickup's collect radius.")]
    [SerializeField] private float flowerLandingOffset = 0.5f;

    [Tooltip("The block bumps up by this much when hit, then settles back.")]
    [SerializeField] private float bumpHeight = 0.08f;
    [SerializeField] private float bumpSeconds = 0.18f;

    private bool _spent;
    private float _bumpAge = float.PositiveInfinity;
    private Vector3 _bumpFrom;

    public override bool IsActionable => !_spent;
    public override bool RespondsTo(string actionLabel) => actionLabel == "jump";

    private void Update()
    {
        if (_bumpAge >= bumpSeconds) return;
        _bumpAge += Time.deltaTime;
        float t = Mathf.Clamp01(_bumpAge / bumpSeconds);
        transform.position = _bumpFrom + Vector3.up * (bumpHeight * Mathf.Sin(t * Mathf.PI));
    }

    public override void ResolveHit(string actionLabel, GameStateManager gameState)
    {
        _spent = true;
        _bumpFrom = transform.position;
        _bumpAge = 0f;
        if (spentMaterial != null)
            foreach (var r in GetComponentsInChildren<Renderer>()) r.sharedMaterial = spentMaterial;

        bool flowerUseful = !gameState.HasFireFlower && fireFlowerPrefab != null;
        bool oneUpUseful = gameState.Lives < gameState.MaxLives && oneUpPrefab != null;
        bool rollOneUp = Random.value < oneUpChance;
        var drop = rollOneUp ? (oneUpUseful ? oneUpPrefab : flowerUseful ? fireFlowerPrefab : null)
                             : (flowerUseful ? fireFlowerPrefab : oneUpUseful ? oneUpPrefab : null);
        if (drop != null)
        {
            // The player is under the block, so a pickup dropped straight down
            // would be collected before it's seen. Land it on the far side,
            // at least flowerLandingOffset from the player, falling from the block.
            float floorY = transform.parent != null ? transform.parent.position.y : transform.position.y - 1.5f;
            var at = transform.position + AwayFromPlayer() * flowerLandingOffset;
            at.y = floorY;
            var pickup = Instantiate(drop, at, Quaternion.identity, transform.parent);
            pickup.DropFrom(transform.position);
            AudioManager.Play(Sfx.PowerUpAppear);
            Debug.Log($"[QuestionBlock] Dropped {drop.PickupKind}.");
        }
        else
        {
            gameState.AddScore(coinValue);
            AudioManager.Play(Sfx.Coin);
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
