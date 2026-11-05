using UnityEngine;

/// <summary>
/// Stage-by-stage difficulty (Docs/GAME_DESIGN.md, pass 3). Lives on
/// GameManager; stage elements read it when they start or spawn something.
/// Everything is a function of the stage number: stage 1 is the authored
/// baseline, and each step adds a little, up to a cap. Without a Difficulty
/// in the scene every stage plays like stage 1. Enemy and Bullet Bill
/// **speeds don't scale** (Oliver 2026-10-29: constant through the game), and
/// **nor does the number of enemies** (2026-11-05: each stage has one wave,
/// its designed size); only the cannon's shots and the time between them do.
/// </summary>
public class Difficulty : MonoBehaviour
{
    [Header("Bullet Bill cannon")]
    [Tooltip("Extra shots per cannon: +1 every this many stages.")]
    [SerializeField] private int stagesPerExtraShot = 3;
    [SerializeField] private int maxExtraShots = 2;
    [Tooltip("Time between shots shrinks by this fraction per stage.")]
    [SerializeField] private float intervalShrinkPerStage = 0.08f;
    [SerializeField] private float minIntervalMultiplier = 0.5f;

    private static Difficulty s_Instance;

    private void Awake() => s_Instance = this;

    /// <summary>The scene's Difficulty, or null (Unity's null check, so a destroyed one from a previous Play session doesn't count).</summary>
    private static Difficulty Current => s_Instance != null ? s_Instance : (s_Instance = FindAnyObjectByType<Difficulty>());

    /// <summary>0 on stage 1, 1 on stage 2, ...</summary>
    private static int Steps
    {
        get
        {
            var gs = GameStateManager.Instance;
            return gs != null ? Mathf.Max(0, gs.StageIndex - 1) : 0;
        }
    }

    public static int ExtraShots { get { var d = Current; return d != null ? Mathf.Min(d.maxExtraShots, Steps / Mathf.Max(1, d.stagesPerExtraShot)) : 0; } }
    public static float ShotIntervalMultiplier { get { var d = Current; return d != null ? Mathf.Max(d.minIntervalMultiplier, 1f - d.intervalShrinkPerStage * Steps) : 1f; } }
}
