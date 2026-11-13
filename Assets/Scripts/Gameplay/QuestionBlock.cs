using UnityEngine;

/// <summary>
/// "?" block (Docs/GAME_DESIGN.md). Floats overhead; a jump underneath hits it.
/// Drops a random power-up: a fire flower or a 1-Up mushroom. If the one
/// rolled would be wasted (a flower while holding one, a 1-Up at the life
/// cap), it drops the other; if both would be, it pops a coin instead.
/// Then it's spent (Gate 5 rejects further hits).
///
/// The power-up hops out and lands in front of the player, the way they're
/// facing (Oliver 2026-11-13: dropped just past the block it was easy to
/// miss), at landingDistance from them. A soft "?" mark on the floor below
/// (floorMark) breathes gently while the block is unused, a reminder of a
/// block overhead in the narrow AR view; it goes once the block is spent.
/// </summary>
public class QuestionBlock : InteractableBase
{
    [SerializeField] private GameObject coinPopPrefab;
    [SerializeField] private Pickup fireFlowerPrefab;
    [SerializeField] private Pickup oneUpPrefab;
    [Tooltip("Chance the power-up is a 1-Up rather than a fire flower.")]
    [Range(0f, 1f)] [SerializeField] private float oneUpChance = 0.5f;
    [SerializeField] private Material spentMaterial;
    [Tooltip("Horizontal distance in front of the player (the way they face) where a power-up lands. Must exceed the pickup's collect radius.")]
    [SerializeField] private float landingDistance = 0.9f;
    [Tooltip("How high above the block the power-up hops before it lands.")]
    [SerializeField] private float hopHeight = 0.25f;
    [Tooltip("The '?' mark on the floor below the block; hidden once it's spent.")]
    [SerializeField] private GameObject floorMark;

    [Tooltip("The block bumps up by this much when hit, then settles back.")]
    [SerializeField] private float bumpHeight = 0.08f;
    [SerializeField] private float bumpSeconds = 0.18f;

    private bool _spent;
    private float _bumpAge = float.PositiveInfinity;
    private Vector3 _bumpFrom;

    public override bool IsActionable => !_spent;
    public override bool RespondsTo(string actionLabel) => actionLabel == "jump";

    private float _markClock;
    private Vector3 _markScale;

    private void Update()
    {
        if (floorMark != null && floorMark.activeSelf && floorMark.scene.IsValid())
        {
            // A slow, gentle breathing: noticeable at a glance, not busy.
            if (_markScale == Vector3.zero) _markScale = floorMark.transform.localScale;
            _markClock += Time.deltaTime;
            floorMark.transform.localScale = _markScale * (1f + 0.08f * Mathf.Sin(_markClock * 2.5f));
        }
        if (_bumpAge >= bumpSeconds) return;
        _bumpAge += Time.deltaTime;
        float t = Mathf.Clamp01(_bumpAge / bumpSeconds);
        transform.position = _bumpFrom + Vector3.up * (bumpHeight * Mathf.Sin(t * Mathf.PI));
    }

    public override void ResolveHit(string actionLabel, GameStateManager gameState)
    {
        _spent = true;
        if (floorMark != null && floorMark.scene.IsValid()) floorMark.SetActive(false); // never a prefab asset's (a block copied out alone)
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
            // The player is under the block: hop the power-up out and land it in
            // front of them, where they're facing, so they see it and walk to it.
            float floorY = transform.parent != null ? transform.parent.position.y : transform.position.y - 1.5f;
            var at = LandingSpot();
            at.y = floorY;
            var pickup = Instantiate(drop, at, Quaternion.identity, transform.parent);
            pickup.HopFrom(transform.position, hopHeight);
            AudioManager.Play(Sfx.PowerUpAppear);
            Debug.Log($"[QuestionBlock] Dropped {drop.PickupKind}.");
        }
        else
        {
            gameState.AddCoin();
            AudioManager.Play(Sfx.Coin);
            if (coinPopPrefab != null) Instantiate(coinPopPrefab, transform.position + Vector3.up * 0.15f, Quaternion.identity);
        }
    }

    /// <summary>Where a power-up lands: landingDistance in front of the player, the way they face (on the floor's x/z).</summary>
    public Vector3 LandingSpot()
    {
        var cam = Camera.main;
        if (cam == null) return transform.position + Vector3.forward * landingDistance;
        var eye = cam.transform.position;
        return new Vector3(eye.x, 0f, eye.z) + Facing(cam.transform) * landingDistance;
    }

    /// <summary>
    /// The horizontal way the player faces. Looking up at the block, the view's forward is nearly
    /// vertical, so its flat part is tiny: then the head's "down" vector (looking up) or "up"
    /// (looking down) points the way the face is turned.
    /// </summary>
    private static Vector3 Facing(Transform cam)
    {
        var f = new Vector3(cam.forward.x, 0f, cam.forward.z);
        var u = cam.forward.y > 0f ? -cam.up : cam.up;
        f += new Vector3(u.x, 0f, u.z) * Mathf.Abs(cam.forward.y);
        return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
    }
}
