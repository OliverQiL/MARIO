using UnityEngine;
using UnityEngine.Events;

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
/// plus "not game over".
///
/// Also holds the player's short-lived action states (Docs/GAME_DESIGN.md):
/// airborne after a jump, ducked after a squat, invulnerable after damage.
/// These are grace windows measured from when the event *arrives*, because
/// events land up to ~275 ms after the physical action finished.
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

    [Header("Lives")]
    [SerializeField] private int startingLives = 3;
    [Tooltip("1-Ups can't raise lives above this.")]
    [SerializeField] private int maxLives = 8;
    [Tooltip("Seconds of invulnerability after taking damage.")]
    [SerializeField] private float invulnerableSeconds = 2f;

    [Header("Events")]
    [Tooltip("Fired after a life is lost (argument: lives remaining).")]
    public UnityEvent<int> OnLifeLost = new();
    [Tooltip("Fired when the last life is lost. Wire to LevelGenerator.EndGame.")]
    public UnityEvent OnGameOver = new();
    [Tooltip("Fired when a new game starts after a game over. Wire to LevelGenerator.RestartLoop.")]
    public UnityEvent OnGameRestarted = new();

    [Header("Session (read-only at runtime)")]
    [Tooltip("Driven by ARSessionManager tracking events.")]
    [SerializeField] private bool isTracking = true;
    [Tooltip("Driven by LevelGenerator: true only while a placed stage is being played.")]
    [SerializeField] private bool isStageActive;

    private static GameStateManager s_Instance;
    /// <summary>The scene's GameStateManager, for stage elements spawned at runtime (blocks, enemies, pickups).</summary>
    public static GameStateManager Instance => s_Instance != null ? s_Instance : (s_Instance = FindAnyObjectByType<GameStateManager>());

    [Header("Player (read-only at runtime)")]
    [SerializeField] private bool isGameOver;
    [SerializeField] private string movementState = "stand";
    private float _airborneUntil = float.NegativeInfinity;
    private float _duckedUntil = float.NegativeInfinity;
    private float _invulnerableUntil = float.NegativeInfinity;
    private float _gameOverAt = float.NegativeInfinity;
    private float _lastStompAt = float.NegativeInfinity;

    [Tooltip("Stomp is re-sent every classification window; a pause this long between stomp events starts a new stomp.")]
    [SerializeField] private float newStompGapSeconds = 0.3f;

    public int Score => score;
    public int Lives => lives;
    public int StageIndex => stageIndex;
    public bool HasFireFlower => hasFireFlower;
    public bool IsTracking => isTracking;
    public bool IsStageActive => isStageActive;
    /// <summary>EventResolver's Gate 1: tracking is good, a stage is live, and the game isn't over.</summary>
    public bool IsSessionReady => isTracking && isStageActive && !isGameOver;
    public bool IsGameOver => isGameOver;
    public float SecondsSinceGameOver => Time.time - _gameOverAt;
    public string MovementState => movementState;
    public bool IsAirborne => Time.time < _airborneUntil;
    public bool IsDucked => Time.time < _duckedUntil;
    public bool IsInvulnerable => Time.time < _invulnerableUntil;
    public int StartingLives => startingLives;
    public int MaxLives => maxLives;
    /// <summary>When the current stomp motion began (its first event after a pause).</summary>
    public float StompStartedAt { get; private set; } = float.NegativeInfinity;

    private void Awake() => s_Instance = this;

    // ---- player action states ----

    public void SetMovementState(string state) => movementState = state;
    public void Jump(float seconds) => _airborneUntil = Time.time + seconds;
    public void Duck(float seconds) => _duckedUntil = Time.time + seconds;

    /// <summary>Records a stomp event; one arriving after a pause of newStompGapSeconds starts a new stomp motion.</summary>
    public void NoteStomp()
    {
        if (Time.time - _lastStompAt > newStompGapSeconds) StompStartedAt = Time.time;
        _lastStompAt = Time.time;
    }

    /// <summary>
    /// Damage from an enemy or hazard. Ignored while invulnerable. Costs a
    /// life and the fire flower; the last life ends the game.
    /// Returns true if the hit landed.
    /// </summary>
    public bool TakeHit(string source)
    {
        if (isGameOver || IsInvulnerable) return false;

        lives = Mathf.Max(0, lives - 1);
        hasFireFlower = false;
        _invulnerableUntil = Time.time + invulnerableSeconds;
        Debug.Log($"[GameStateManager] Hit by {source} — {lives} lives left.");
        OnLifeLost?.Invoke(lives);

        if (lives == 0)
        {
            isGameOver = true;
            isStageActive = false;
            _gameOverAt = Time.time;
            Debug.Log($"[GameStateManager] GAME OVER — score {score}, stage {stageIndex}.");
            OnGameOver?.Invoke();
        }
        return true;
    }

    /// <summary>Starts a fresh game after a game over (EventResolver calls this on a jump).</summary>
    public void RestartGame()
    {
        score = 0;
        lives = startingLives;
        stageIndex = 1;
        hasFireFlower = false;
        isGameOver = false;
        isStageActive = false;
        _airborneUntil = _duckedUntil = _invulnerableUntil = _lastStompAt = float.NegativeInfinity;
        StompStartedAt = float.NegativeInfinity;
        Debug.Log("[GameStateManager] New game.");
        OnGameRestarted?.Invoke();
    }

    public void AddScore(int amount)
    {
        score += amount;
        Debug.Log($"[GameStateManager] Score +{amount} -> {score}");
    }

    public void AddLife()
    {
        lives = Mathf.Min(lives + 1, maxLives);
        Debug.Log($"[GameStateManager] Extra life -> {lives}");
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