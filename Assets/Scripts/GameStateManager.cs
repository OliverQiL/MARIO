using UnityEngine;

/// <summary>
/// Authoritative game state: score, lives, stage progress, power-ups.
/// No AR or networking dependency — this is pure state, mutated by
/// EventResolver's resolution gate and (once wired) by ARSessionManager's
/// tracking-loss events via SetSessionReady/SetSessionNotReady.
/// </summary>
public class GameStateManager : MonoBehaviour
{
    [Header("State")]
    [SerializeField] private int score;
    [SerializeField] private int lives = 3;
    [SerializeField] private int stageIndex = 1;
    [SerializeField] private bool hasFireFlower;

    [Header("Session")]
    [Tooltip("False while scanning, relocalising, or transitioning between stages. EventResolver's Gate 1 checks this.")]
    [SerializeField] private bool isSessionReady = true;

    public int Score => score;
    public int Lives => lives;
    public int StageIndex => stageIndex;
    public bool HasFireFlower => hasFireFlower;
    public bool IsSessionReady { get => isSessionReady; set => isSessionReady = value; }

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

    /// <summary>Parameterless wrapper so this can be wired directly into a UnityEvent (e.g. ARSessionManager.OnTrackingLost) in the Inspector.</summary>
    public void SetSessionNotReady() => IsSessionReady = false;

    /// <summary>Parameterless wrapper so this can be wired directly into a UnityEvent (e.g. ARSessionManager.OnTrackingRestored) in the Inspector.</summary>
    public void SetSessionReady() => IsSessionReady = true;
}