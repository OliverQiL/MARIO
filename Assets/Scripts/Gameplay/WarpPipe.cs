using System;
using UnityEngine;

/// <summary>
/// Warp pipe (Docs/GAME_DESIGN.md, maps): a short green pipe on the floor.
/// Squatting while standing on it enters it. A stage's pipe takes the player
/// to a bonus room, a temporary area in the same space with coins and an exit
/// pipe; the exit pipe (isExit) brings the main stage back as it was.
/// LevelGenerator does the swap, listening to OnEntered. A stage's pipe can
/// be used once, and some stages roll whether it appears at all.
/// </summary>
public class WarpPipe : InteractableBase
{
    [Range(0f, 1f)] [SerializeField] private float appearChance = 1f;
    [Tooltip("The bonus room's way back to the main stage.")]
    [SerializeField] private bool isExit;
    [SerializeField] private float height = 0.3f;

    /// <summary>Raised when the player enters a pipe (LevelGenerator swaps stage and bonus room).</summary>
    public static event Action<WarpPipe> OnEntered;

    private bool _used;

    public bool IsExit => isExit;
    public override bool IsActionable => !_used;
    public override bool RespondsTo(string actionLabel) => actionLabel == "squat";
    public override Vector3 HitPoint => transform.position + Vector3.up * height;

    private void Start()
    {
        if (!isExit && UnityEngine.Random.value >= appearChance) gameObject.SetActive(false); // also leaves the target registry
    }

    public override void ResolveHit(string actionLabel, GameStateManager gameState)
    {
        _used = true;
        AudioManager.Play(Sfx.Warp);
        Debug.Log($"[WarpPipe] Entered {(isExit ? "the exit pipe" : "a warp pipe")}.");
        OnEntered?.Invoke(this);
    }
}
