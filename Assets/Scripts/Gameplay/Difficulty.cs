using UnityEngine;

/// <summary>
/// Stage-by-stage difficulty (Docs/GAME_DESIGN.md, pass 3). Lives on
/// GameManager; stage elements read it when they start or spawn something.
/// Everything is a function of the stage number: stage 1 is the authored
/// baseline, and each step adds a little, up to a cap. Without a Difficulty
/// in the scene every stage plays like stage 1.
/// </summary>
public class Difficulty : MonoBehaviour
{
    [Header("Enemies")]
    [Tooltip("Extra enemies per spawner: +1 every this many stages.")]
    [SerializeField] private int stagesPerExtraEnemy = 2;
    [SerializeField] private int maxExtraEnemies = 3;
    [Tooltip("Enemy walking speed grows by this fraction per stage.")]
    [SerializeField] private float enemySpeedPerStage = 0.1f;
    [SerializeField] private float maxEnemySpeedMultiplier = 1.8f;

    [Header("Bullet Bill cannon")]
    [Tooltip("Extra shots per cannon: +1 every this many stages.")]
    [SerializeField] private int stagesPerExtraShot = 3;
    [SerializeField] private int maxExtraShots = 2;
    [Tooltip("Time between shots shrinks by this fraction per stage.")]
    [SerializeField] private float intervalShrinkPerStage = 0.08f;
    [SerializeField] private float minIntervalMultiplier = 0.5f;
    [Tooltip("Bill speed grows by this fraction per stage.")]
    [SerializeField] private float billSpeedPerStage = 0.05f;
    [SerializeField] private float maxBillSpeedMultiplier = 1.5f;

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

    public static int ExtraEnemies { get { var d = Current; return d != null ? Mathf.Min(d.maxExtraEnemies, Steps / Mathf.Max(1, d.stagesPerExtraEnemy)) : 0; } }
    public static float EnemySpeedMultiplier { get { var d = Current; return d != null ? Mathf.Min(d.maxEnemySpeedMultiplier, 1f + d.enemySpeedPerStage * Steps) : 1f; } }
    public static int ExtraShots { get { var d = Current; return d != null ? Mathf.Min(d.maxExtraShots, Steps / Mathf.Max(1, d.stagesPerExtraShot)) : 0; } }
    public static float ShotIntervalMultiplier { get { var d = Current; return d != null ? Mathf.Max(d.minIntervalMultiplier, 1f - d.intervalShrinkPerStage * Steps) : 1f; } }
    public static float BillSpeedMultiplier { get { var d = Current; return d != null ? Mathf.Min(d.maxBillSpeedMultiplier, 1f + d.billSpeedPerStage * Steps) : 1f; } }
}
