using UnityEngine;

/// <summary>
/// Warp pipe (Docs/GAME_DESIGN.md, pass 3): a short green pipe on the floor.
/// Squatting while standing on it warps: the stage is cleared on the spot
/// (through its StageGoal, so the normal stage-clear flow runs) and the
/// stage counter skips skipStages extra stages. Only appears on some stages
/// (appearChance, rolled when the stage is placed).
/// </summary>
public class WarpPipe : InteractableBase
{
    [Range(0f, 1f)] [SerializeField] private float appearChance = 0.5f;
    [SerializeField] private int skipStages = 1;
    [SerializeField] private float height = 0.3f;

    private bool _used;

    public override bool IsActionable => !_used;
    public override bool RespondsTo(string actionLabel) => actionLabel == "squat";
    public override Vector3 HitPoint => transform.position + Vector3.up * height;

    private void Start()
    {
        if (Random.value >= appearChance) gameObject.SetActive(false); // also leaves the target registry
    }

    public override void ResolveHit(string actionLabel, GameStateManager gameState)
    {
        _used = true;
        var goal = transform.parent != null ? transform.parent.GetComponentInChildren<StageGoal>() : null;
        if (goal == null)
        {
            Debug.LogWarning("[WarpPipe] No StageGoal on this stage — nothing to warp past.");
            return;
        }
        gameState.QueueWarp(skipStages);
        AudioManager.Play(Sfx.Warp);
        Debug.Log($"[WarpPipe] Warp — skipping {skipStages} stage(s).");
        goal.Reach();
    }
}
