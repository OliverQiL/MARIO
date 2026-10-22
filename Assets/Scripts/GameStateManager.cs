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
    [SerializeField] private int coins;
    [Tooltip("What the player is carrying (picked up with clap, thrown with throw).")]
    [SerializeField] private HeldItem heldItem;

    [Header("Scoring")]
    [SerializeField] private int stageClearBonus = 500;
    [SerializeField] private int coinValue = 100;
    [Tooltip("Collecting this many coins gives a 1-Up (the counter wraps to 0).")]
    [SerializeField] private int coinsPerLife = 100;

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
    [Tooltip("Fired when a new game starts after a game over or the game is cleared. Wire to LevelGenerator.RestartLoop.")]
    public UnityEvent OnGameRestarted = new();
    [Tooltip("Fired when the final stage is cleared (the game is won).")]
    public UnityEvent OnGameClear = new();

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
    [SerializeField] private bool isGameClear;
    private float _airborneUntil = float.NegativeInfinity;
    private float _duckedUntil = float.NegativeInfinity;
    private float _invulnerableUntil = float.NegativeInfinity;
    private float _gameOverAt = float.NegativeInfinity;
    private float _gameStartedAt = float.NegativeInfinity;

    /// <summary>End-of-game stats (GAME OVER / GAME CLEAR screens).</summary>
    public struct Stats { public int actionsResolved, actionsRefused, enemiesDefeated, coinsCollected; }
    private Stats _stats;

    public int Score => score;
    public int Lives => lives;
    public int StageIndex => stageIndex;
    /// <summary>Stages per world in the names: stage 5 is "2-1" with 4 per world (the campaign's length).</summary>
    public const int StagesPerWorld = 4;
    /// <summary>Mario-style name of the current stage: "1-1", "1-2", ... "2-1" ...</summary>
    public string StageName => InTraining ? "0-1" : NameOf(stageIndex);
    /// <summary>The training round (Tutorial) is on: hits cost no lives, and nothing scored carries into 1-1.</summary>
    public bool InTraining { get; private set; }
    public static string NameOf(int stage) => $"{(stage - 1) / StagesPerWorld + 1}-{(stage - 1) % StagesPerWorld + 1}";
    public bool HasFireFlower => hasFireFlower;
    public int Coins => coins;
    public bool IsTracking => isTracking;
    public bool IsStageActive => isStageActive;
    /// <summary>EventResolver's Gate 1: tracking is good, a stage is live, and the game isn't over or won.</summary>
    public bool IsSessionReady => isTracking && isStageActive && !isGameOver && !isGameClear;
    public bool IsGameOver => isGameOver;
    /// <summary>The final stage was cleared: the game is won (a jump starts a new game).</summary>
    public bool IsGameClear => isGameClear;
    /// <summary>Game over or game clear: play has ended until a jump restarts it.</summary>
    public bool IsFinished => isGameOver || isGameClear;
    /// <summary>Seconds since the game ended (over or cleared).</summary>
    public float SecondsSinceGameOver => Time.time - _gameOverAt;
    public HeldItem Held => heldItem;
    public Stats GameStats => _stats;
    /// <summary>Seconds of play since the first stage of this game started.</summary>
    public float PlaySeconds => float.IsInfinity(_gameStartedAt) ? 0f : (IsFinished ? _gameOverAt : Time.time) - _gameStartedAt;
    public bool IsAirborne => Time.time < _airborneUntil;
    public bool IsDucked => Time.time < _duckedUntil;
    public bool IsInvulnerable => Time.time < _invulnerableUntil;
    public int StartingLives => startingLives;
    public int MaxLives => maxLives;

    private void Awake() => s_Instance = this;

    // ---- player action states ----

    public void Jump(float seconds) => _airborneUntil = Time.time + seconds;
    public void Duck(float seconds) => _duckedUntil = Time.time + seconds;

    /// <summary>Picks up an item (clap). Returns false if the hands are already full.</summary>
    public bool PickUp(HeldItem item)
    {
        if (heldItem != HeldItem.None || item == HeldItem.None) return false;
        heldItem = item;
        Debug.Log($"[GameStateManager] Holding {item}.");
        return true;
    }

    /// <summary>Lets go of the held item (throw) and returns what it was.</summary>
    public HeldItem ThrowHeld()
    {
        var item = heldItem;
        heldItem = HeldItem.None;
        return item;
    }

    /// <summary>Counts an action's outcome for the end-of-game stats (EventResolver calls this).</summary>
    public void RecordAction(bool resolved, bool refused)
    {
        if (resolved) _stats.actionsResolved++;
        else if (refused) _stats.actionsRefused++;
    }

    public void RecordDefeat() => _stats.enemiesDefeated++;

    /// <summary>
    /// Damage from an enemy or hazard. Ignored while invulnerable. Costs a
    /// life and the fire flower; the last life ends the game.
    /// Returns true if the hit landed.
    /// </summary>
    public bool TakeHit(string source)
    {
        if (isGameOver || IsInvulnerable) return false;

        if (InTraining)
        {
            // Infinite lives: the hit still shows (flash, damage buzz, tip) but costs nothing.
            _invulnerableUntil = Time.time + invulnerableSeconds;
            Debug.Log($"[GameStateManager] Hit by {source} in training — no life lost.");
            OnLifeLost?.Invoke(lives);
            return true;
        }

        lives = Mathf.Max(0, lives - 1);
        hasFireFlower = false;
        heldItem = HeldItem.None; // dropped, as in Mario
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

    /// <summary>The final stage is cleared: the game is won. LevelGenerator calls this after CompleteStage.</summary>
    public void ClearGame()
    {
        if (isGameClear) return;
        isGameClear = true;
        isStageActive = false;
        _gameOverAt = Time.time;
        Debug.Log($"[GameStateManager] GAME CLEAR — score {score}, {_stats.enemiesDefeated} enemies, {PlaySeconds:F0}s.");
        OnGameClear?.Invoke();
    }

    /// <summary>Starts a fresh game after a game over or game clear (EventResolver calls this on a jump).</summary>
    public void RestartGame()
    {
        ResetState();
        Debug.Log("[GameStateManager] New game.");
        OnGameRestarted?.Invoke();
    }

    /// <summary>The training stage has started (LevelGenerator): infinite lives until EndTraining.</summary>
    public void StartTraining()
    {
        InTraining = true;
        Debug.Log("[GameStateManager] Training round.");
    }

    /// <summary>Training is done: a fresh game for 1-1 (score, coins, stats and the flower from training don't carry over).</summary>
    public void EndTraining()
    {
        ResetState();
        Debug.Log("[GameStateManager] Training complete — new game from 1-1.");
    }

    /// <summary>Hard reset (HardReset): a new game's state, but no OnGameRestarted (the game goes back to the scan, not straight to a stage).</summary>
    public void ResetForScan()
    {
        ResetState();
        Debug.Log("[GameStateManager] Hard reset: new game after the scan.");
    }

    private void ResetState()
    {
        InTraining = false;
        score = 0;
        lives = startingLives;
        stageIndex = 1;
        hasFireFlower = false;
        heldItem = HeldItem.None;
        coins = 0;
        isGameOver = false;
        isGameClear = false;
        isStageActive = false;
        _stats = default;
        _gameStartedAt = float.NegativeInfinity;
        _airborneUntil = _duckedUntil = _invulnerableUntil = float.NegativeInfinity;
    }

    public void AddScore(int amount)
    {
        score += amount;
        Debug.Log($"[GameStateManager] Score +{amount} -> {score}");
    }

    /// <summary>A coin (collected, or from a ? block): score, the coin counter, and a 1-Up every coinsPerLife.</summary>
    public void AddCoin()
    {
        AddScore(coinValue);
        coins++;
        _stats.coinsCollected++;
        if (coins < coinsPerLife) return;
        coins = 0;
        Debug.Log($"[GameStateManager] {coinsPerLife} coins!");
        AddLife();
        AudioManager.Play(Sfx.OneUp);
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
        if (float.IsInfinity(_gameStartedAt)) _gameStartedAt = Time.time;
        Debug.Log($"[GameStateManager] Stage {StageName} started.");
    }

    /// <summary>Wired to LevelGenerator.OnStageCompleted: closes Gate 1 for the transition, awards the clear bonus, advances the stage counter.</summary>
    public void CompleteStage()
    {
        isStageActive = false;
        Debug.Log($"[GameStateManager] Stage {StageName} cleared.");
        AddScore(stageClearBonus);
        stageIndex++;
    }
}

/// <summary>Things the player can carry (picked up with clap, thrown with throw).</summary>
public enum HeldItem { None, Shell }
