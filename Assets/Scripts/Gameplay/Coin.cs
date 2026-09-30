using UnityEngine;

/// <summary>
/// A floating coin in a stage layout (Docs/GAME_DESIGN.md, maps). Collected by
/// walking through it (horizontal distance, like pickups): score, the coin
/// counter, and a 1-Up every 100 (GameStateManager.AddCoin). Spins while it waits.
/// Its height is set relative to the player's eyes by EyeHeightPlacement.
/// </summary>
public class Coin : MonoBehaviour
{
    [SerializeField] private float collectRadius = 0.3f;
    [SerializeField] private float spinDegreesPerSecond = 180f;
    [SerializeField] private GameObject collectEffect;

    private void Update()
    {
        transform.Rotate(Vector3.up, spinDegreesPerSecond * Time.deltaTime, Space.World);
        var cam = Camera.main;
        if (cam != null) TryCollect(cam.transform.position);
    }

    /// <summary>Collects if the player is within reach. Public so headless tests can drive it.</summary>
    public bool TryCollect(Vector3 playerPosition)
    {
        var gs = GameStateManager.Instance;
        if (gs == null || !gs.IsSessionReady) return false;

        var d = playerPosition - transform.position;
        d.y = 0f;
        if (d.magnitude > collectRadius) return false;

        gs.AddCoin();
        AudioManager.Play(Sfx.Coin);
        if (collectEffect != null) Instantiate(collectEffect, transform.position, Quaternion.identity);
        InteractableBase.SafeDestroy(gameObject);
        return true;
    }
}
