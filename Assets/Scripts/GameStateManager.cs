using UnityEngine;

/// <summary>
/// Authoritative game state: score, lives, stage progress, power-ups.
/// No AR or networking dependency — this is pure state, mutated by
/// EventResolver's resolution gate and, through Inspector-wired UnityEvents,
/// by ARSessionManager (tracking) and LevelGenerator (stage lifecycle).
///
/// Session readiness (EventResolver's Gate 1) is two independent conditions,
/// so neither source can accidentally reopen the gate the other closed:
/// - tracking OK      — ARSessionManager.OnTrackingLost/Restored
/// - a stage is live  — false while scanning, placing, and between stages
/// </summary>
public class GameStateManager : MonoBehaviour
{
    [Header("State")]
    [SerializeField] private int score;
    [SerializeField] private int lives = 3;
    [SerializeField] private int stageIndex = 1;
    [SerializeField] private bool hasFireFlower;

    [Header("Scoring")]
    [SerializeField] private int stageClearBonus = 500;

    [Header("Session (read-only at runtime)")]
    [Tooltip("Driven by ARSessionManager tracking events.")]
    [SerializeField] private bool isTracking = true;
    [Tooltip("Driven by LevelGenerator: true only while a placed stage is being played.")]
    [SerializeField] private bool isStageActive;

    public int Score => score;
    public int Lives => lives;
    public int StageIndex => stageIndex;
    public bool HasFireFlower => hasFireFlower;
    public bool IsTracking => isTracking;
    public bool IsStageActive => isStageActive;
    /// <summary>EventResolver's Gate 1: tracking is good and a stage is live.</summary>
    public bool IsSessionReady => isTracking && isStageActive;

    public void AddScore(int amount)
    {
        score += amount;
        Debug.Log($"[GameStateManager] Score +{amount} -> {score}");
    }

    public void LoseLife()
    {
        lives = Mathf.Max(0, lives - 1);
        Debug.Log($"[GameStateManager] Life lost -> {lives} remaining");
    }

    public void SetFireFlower(bool value)
    {
        hasFireFlower = value;
        Debug.Log($"[GameStateManager] Fire flower: {value}");
    }

    /// <summary>Wired to ARSessionManager.OnTrackingLost. (Name kept for the existing persistent listener.)</summary>
    public void SetSessionNotReady() => isTracking = false;

    /// <summary>Wired to ARSessionManager.OnTrackingRestored. (Name kept for the existing persistent listener.)</summary>
    public void SetSessionReady() => isTracking = true;

    /// <summary>Wired to LevelGenerator.OnStageStarted.</summary>
    public void BeginStage()
    {
        isStageActive = true;
        Debug.Log($"[GameStateManager] Stage {stageIndex} started.");
    }

    /// <summary>Wired to LevelGenerator.OnStageCompleted: closes Gate 1 for the transition, awards the clear bonus, advances the stage counter.</summary>
    public void CompleteStage()
    {
        isStageActive = false;
        Debug.Log($"[GameStateManager] Stage {stageIndex} cleared.");
        AddScore(stageClearBonus);
        stageIndex++;
    }
}