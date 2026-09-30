using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Screen-space HUD (design report module table): stage/score/lives, gateway
/// connection status, degraded-mode messaging, and per-action feedback.
/// Replaces the scan-only ScanHUD.
///
/// Styled after the classic Mario HUD (pixel font, drop shadows), laid out
/// by HUDSetup:
/// - top bar:            SCORE (6 digits) · STAGE · LIVES (heart ×N) · POWER
///                       (fire-flower icon while held)
/// - connection (top-right, small)
/// - banner (top-centre): the current *state* — scanning, tracking lost, no
///                       room to place a stage, placing, distance to the flag.
///                       Highest priority wins; polled every frame.
/// - scan panel:         while scanning: "SCAN THE FLOOR", a progress bar with
///                       the percentage of floor found, and rotating hints;
///                       "READY!" pops in when the scan completes
/// - title (centre):     big pop-in "STAGE N" when a stage starts, "STAGE CLEAR!"
///                       or "WARP!" when it ends
/// - feedback (centre):  transient *events* — action results from
///                       EventResolver.OnEventProcessed and score gains. Fades.
/// - game-over screen:   dimmed overlay with the final score and the restart prompt
/// - damage:             a red screen flash when a life is lost, and the heart
///                       blinks while the player is invulnerable (the player is
///                       the camera, so there's no character to blink)
///
/// Feedback keeps the report's distinction: Gates 3-5 ("recognised but not
/// permitted") are shown prominently with the reason; Gate 2 low confidence
/// ("not registered") is a quiet hint; duplicates and Gate 1 are silent (the
/// banner already explains why nothing is being accepted). State-only actions
/// (stand/walk/squat, a jump that hit nothing) are silent too. Damage and
/// fireball impacts come from GameStateManager.OnLifeLost / EventResolver.OnImpact.
/// </summary>
public class HUDController : MonoBehaviour
{
    [Header("Sources")]
    [SerializeField] private ARSessionManager sessionManager;
    [SerializeField] private LevelGenerator levelGenerator;
    [SerializeField] private GameStateManager gameState;
    [SerializeField] private EventResolver eventResolver;
    [Tooltip("Optional — connection status is hidden without it.")]
    [SerializeField] private NetworkClient networkClient;

    [Header("Top bar")]
    [SerializeField] private TMP_Text scoreText;
    [SerializeField] private TMP_Text stageText;
    [SerializeField] private TMP_Text livesText;
    [SerializeField] private Image livesIcon;
    [SerializeField] private Image powerIcon;
    [SerializeField] private TMP_Text powerEmptyText;
    [SerializeField] private TMP_Text connectionText;

    [Header("Centre")]
    [SerializeField] private TMP_Text bannerText;
    [SerializeField] private TMP_Text feedbackText;
    [SerializeField] private TMP_Text titleText;

    [Header("Scan")]
    [SerializeField] private GameObject scanPanel;
    [SerializeField] private Image scanFill;
    [SerializeField] private TMP_Text scanPercentText;
    [SerializeField] private TMP_Text scanHintText;
    [SerializeField] private float hintSeconds = 3.5f;

    [Header("Damage")]
    [SerializeField] private Image damageFlash;
    [SerializeField] private float damageFlashSeconds = 0.5f;
    [SerializeField] private float damageFlashAlpha = 0.45f;

    [Header("Game over")]
    [SerializeField] private GameObject gameOverPanel;
    [SerializeField] private TMP_Text gameOverStatsText;
    [SerializeField] private TMP_Text gameOverPromptText;

    [Header("Title")]
    [SerializeField] private float titleSeconds = 1.8f;
    [SerializeField] private float titlePopSeconds = 0.18f;

    [Header("Feedback")]
    [SerializeField] private float feedbackSeconds = 1.5f;
    [SerializeField] private float feedbackFadeSeconds = 0.4f;
    [Tooltip("How long the 'no room here' prompt stays up after a failed placement search (the search retries automatically).")]
    [SerializeField] private float placementFailedHoldSeconds = 3f;

    [Header("Colours")]
    [SerializeField] private Color goodColor = new(0.3f, 1f, 0.4f);
    [SerializeField] private Color deniedColor = new(1f, 0.6f, 0.15f);
    [SerializeField] private Color quietColor = new(0.8f, 0.8f, 0.8f);
    [SerializeField] private Color warningColor = new(1f, 0.35f, 0.3f);
    [SerializeField] private Color titleColor = new(1f, 0.85f, 0.2f);

    private float _feedbackShownAt = float.NegativeInfinity;
    private Color _feedbackColor;
    private float _placementFailedAt = float.NegativeInfinity;
    private int _lastScore;
    private float _titleShownAt = float.NegativeInfinity;
    private Color _titleColor;
    private float _damageAt = float.NegativeInfinity;

    private void Reset()
    {
        // Convenience: auto-find references the first time this component is added.
        if (sessionManager == null) sessionManager = FindAnyObjectByType<ARSessionManager>();
        if (levelGenerator == null) levelGenerator = FindAnyObjectByType<LevelGenerator>();
        if (gameState == null) gameState = FindAnyObjectByType<GameStateManager>();
        if (eventResolver == null) eventResolver = FindAnyObjectByType<EventResolver>();
        if (networkClient == null) networkClient = FindAnyObjectByType<NetworkClient>();
    }

    private void OnEnable()
    {
        if (eventResolver != null)
        {
            eventResolver.OnEventProcessed += HandleEventProcessed;
            eventResolver.OnImpact += HandleImpact;
        }
        if (gameState != null) gameState.OnLifeLost.AddListener(HandleLifeLost);
        if (levelGenerator != null)
        {
            levelGenerator.OnPlacementFailed.AddListener(HandlePlacementFailed);
            levelGenerator.OnStageStarted.AddListener(HandleStageStarted);
            levelGenerator.OnStageCompleted.AddListener(HandleStageCompleted);
        }
        if (sessionManager != null) sessionManager.OnScanComplete.AddListener(HandleScanComplete);
        if (gameState != null) _lastScore = gameState.Score;
        BulletBillCannon.OnWarning += HandleBulletBillWarning;
    }

    private void OnDisable()
    {
        if (eventResolver != null)
        {
            eventResolver.OnEventProcessed -= HandleEventProcessed;
            eventResolver.OnImpact -= HandleImpact;
        }
        if (gameState != null) gameState.OnLifeLost.RemoveListener(HandleLifeLost);
        if (levelGenerator != null)
        {
            levelGenerator.OnPlacementFailed.RemoveListener(HandlePlacementFailed);
            levelGenerator.OnStageStarted.RemoveListener(HandleStageStarted);
            levelGenerator.OnStageCompleted.RemoveListener(HandleStageCompleted);
        }
        if (sessionManager != null) sessionManager.OnScanComplete.RemoveListener(HandleScanComplete);
        BulletBillCannon.OnWarning -= HandleBulletBillWarning;
    }

    private void Update()
    {
        UpdateStats();
        UpdateConnection();
        UpdateBanner();
        UpdateFeedbackFade();
        UpdateTitle();
        UpdateGameOver();
        UpdateDamage();
        UpdateScan();
    }

    // ---- persistent state ----

    private void UpdateStats()
    {
        if (gameState == null) return;

        // Score gains (hits, stage-clear bonus) flash as feedback.
        int delta = gameState.Score - _lastScore;
        _lastScore = gameState.Score;
        if (delta > 0) ShowFeedback(FeedbackPrefix() + $"+{delta}", goodColor);

        if (scoreText != null) scoreText.text = Mathf.Min(gameState.Score, 999999).ToString("D6");
        if (stageText != null) stageText.text = gameState.StageIndex.ToString();
        if (livesText != null) livesText.text = $"x{Mathf.Max(0, gameState.Lives)}";
        if (powerIcon != null) powerIcon.enabled = gameState.HasFireFlower;
        if (powerEmptyText != null) powerEmptyText.enabled = !gameState.HasFireFlower;
    }

    private void UpdateConnection()
    {
        if (connectionText == null) return;
        if (networkClient == null) { connectionText.text = ""; return; }

        switch (networkClient.State)
        {
            case GatewayConnection.ConnectionState.Connected:
                connectionText.text = "<size=160%><color=#4CFF66>●</color></size> ONLINE";
                break;
            case GatewayConnection.ConnectionState.Connecting:
                connectionText.text = "<size=160%><color=#FFB020>●</color></size> CONNECTING";
                break;
            default:
                connectionText.text = "<size=160%><color=#FF5A4D>●</color></size> OFFLINE";
                break;
        }
    }

    private void UpdateBanner()
    {
        if (bannerText == null) return;

        string message = null;
        Color color = Color.white;

        if (gameState != null && gameState.IsGameOver)
        {
            message = null; // the game-over screen says it all
        }
        else if (sessionManager != null && !sessionManager.IsTracking)
        {
            message = "TRACKING LOST - LOOK AROUND SLOWLY";
            color = warningColor;
        }
        else if (sessionManager != null && !sessionManager.IsScanComplete)
        {
            message = null; // the scan panel shows progress
        }
        else if (levelGenerator != null && levelGenerator.IsTransitioning)
        {
            message = null; // the centre title shows STAGE CLEAR!
        }
        else if (levelGenerator != null && levelGenerator.CurrentStage == null)
        {
            bool recentlyFailed = Time.time - _placementFailedAt < placementFailedHoldSeconds;
            message = recentlyFailed ? "NO ROOM HERE - MOVE TO A CLEARER AREA"
                    : levelGenerator.IsWaitingForMesh ? "MAPPING THE ROOM..."
                    : "PLACING STAGE...";
            color = recentlyFailed ? warningColor : Color.white;
        }
        else if (levelGenerator != null && levelGenerator.CurrentGoal != null
                 && !float.IsInfinity(levelGenerator.CurrentGoal.PlayerDistance))
        {
            message = $"FLAG {levelGenerator.CurrentGoal.PlayerDistance:F1} M";
            color = quietColor;
        }

        bannerText.text = message ?? "";
        bannerText.color = color;
    }

    // ---- transient feedback ----

    // A resolved action and its score gain are observed separately (event vs
    // score polling, possibly a frame apart); pair them within a short window.
    private string _pendingPrefix;
    private int _pendingPrefixFrame;

    private string FeedbackPrefix()
    {
        var prefix = Time.frameCount - _pendingPrefixFrame <= 2 ? _pendingPrefix : null;
        _pendingPrefix = null;
        return prefix;
    }

    private void HandleEventProcessed(GameEvent evt, ResolutionResult result)
    {
        string action = Capitalise(evt.actionLabel);

        if (result.StateOnly) return;

        if (result.LaunchedProjectile)
        {
            ShowFeedback("Fireball!", deniedColor);
            return;
        }

        if (result.Resolved)
        {
            // The score change is picked up in UpdateStats; prefix it with the action.
            string label = result.Target is WarpPipe ? "Warp" : action;
            _pendingPrefix = $"{label}!  ";
            _pendingPrefixFrame = Time.frameCount;
            ShowFeedback($"{label}!", goodColor);
            if (result.Target is WarpPipe) ShowTitle("WARP!", goodColor); // replaces STAGE CLEAR! raised a moment ago
            return;
        }

        // Held stomp/walk repeat every window: after a stomp lands, the next
        // windows would flash "already used" then "nothing in range".
        if (result.NotPermitted && EventResolver.IsContinuous(evt.actionLabel)) return;

        switch (result.Gate)
        {
            case 3:
                ShowFeedback(evt.actionLabel == "fireball" ? "Fireballs recharging" : $"Can't {evt.actionLabel} right now", deniedColor);
                break;
            case 4:
                ShowFeedback($"{action}: nothing in range", deniedColor);
                break;
            case 5:
                ShowFeedback($"{action}: already used", deniedColor);
                break;
            case 2 when !result.Reason.StartsWith("duplicate", StringComparison.Ordinal):
                ShowFeedback("Move not recognised — try again", quietColor);
                break;
            // Gate 1 (session not ready) and duplicates: silent.
        }
    }

    private void HandleImpact(GameEvent evt, Component target)
    {
        _pendingPrefix = "Fireball hit!  ";
        _pendingPrefixFrame = Time.frameCount;
        ShowFeedback("Fireball hit!", goodColor);
    }

    private void HandleLifeLost(int livesLeft)
    {
        ShowFeedback(livesLeft > 0 ? "Ouch! -1 life" : "Out of lives!", warningColor);
        _damageAt = Time.time;
    }

    private void UpdateDamage()
    {
        if (damageFlash != null)
        {
            float t = Mathf.Clamp01((Time.time - _damageAt) / damageFlashSeconds);
            var c = damageFlash.color;
            damageFlash.color = new Color(c.r, c.g, c.b, damageFlashAlpha * (1f - t));
            damageFlash.enabled = t < 1f;
        }
        if (livesIcon != null && gameState != null)
            livesIcon.enabled = !gameState.IsInvulnerable || gameState.IsGameOver || Mathf.Repeat(Time.time, 0.25f) < 0.13f;
    }

    private void HandleBulletBillWarning() => ShowFeedback("Bullet Bill! Squat!", warningColor);

    private void HandlePlacementFailed() => _placementFailedAt = Time.time;

    private void HandleStageStarted()
    {
        _placementFailedAt = float.NegativeInfinity;
        if (gameState != null) ShowTitle($"STAGE {gameState.StageIndex}", Color.white);
    }

    private void HandleStageCompleted() => ShowTitle("STAGE CLEAR!", titleColor);

    private void HandleScanComplete() => ShowTitle("READY!", goodColor);

    // ---- scan panel ----

    private void UpdateScan()
    {
        if (scanPanel == null || sessionManager == null) return;
        bool scanning = sessionManager.IsTracking && !sessionManager.IsScanComplete && (gameState == null || !gameState.IsGameOver);
        if (scanPanel.activeSelf != scanning) scanPanel.SetActive(scanning);
        if (!scanning) return;

        float area = sessionManager.TotalScannedArea;
        float progress = Mathf.Clamp01(area / Mathf.Max(0.01f, sessionManager.RequiredFloorArea));
        if (scanFill != null) scanFill.fillAmount = progress;
        if (scanPercentText != null) scanPercentText.text = $"{Mathf.FloorToInt(progress * 100f)}%";
        if (scanHintText != null)
        {
            // Rotate hints; mention the small-room fallback once it can apply.
            bool enoughForSmallRoom = area >= sessionManager.MinFloorArea;
            int count = enoughForSmallRoom ? 3 : 2;
            int i = (int)(Time.time / hintSeconds) % count;
            scanHintText.text = i == 0 ? "SWEEP THE CAMERA SLOWLY OVER THE FLOOR"
                              : i == 1 ? "ONLY FLOOR COUNTS - NOT TABLES OR BEDS"
                              : "SMALL ROOM? HOLD STILL - IT WILL FINISH";
        }
    }

    private void ShowFeedback(string message, Color color)
    {
        if (feedbackText == null) return;
        feedbackText.text = message.ToUpperInvariant();
        _feedbackColor = color;
        _feedbackShownAt = Time.time;
        UpdateFeedbackFade();
    }

    private void UpdateFeedbackFade()
    {
        if (feedbackText == null) return;
        float age = Time.time - _feedbackShownAt;
        float alpha = age <= feedbackSeconds ? 1f : 1f - Mathf.Clamp01((age - feedbackSeconds) / feedbackFadeSeconds);
        feedbackText.color = new Color(_feedbackColor.r, _feedbackColor.g, _feedbackColor.b, alpha);
    }

    // ---- centre title ----

    private void ShowTitle(string message, Color color)
    {
        if (titleText == null) return;
        titleText.text = message;
        _titleColor = color;
        _titleShownAt = Time.time;
        UpdateTitle();
    }

    /// <summary>Pops in (overshoot then settle), holds, fades.</summary>
    private void UpdateTitle()
    {
        if (titleText == null) return;
        float age = Time.time - _titleShownAt;
        float alpha = age <= titleSeconds ? 1f : 1f - Mathf.Clamp01((age - titleSeconds) / feedbackFadeSeconds);
        titleText.color = new Color(_titleColor.r, _titleColor.g, _titleColor.b, alpha);
        float pop = Mathf.Clamp01(age / titlePopSeconds);
        titleText.rectTransform.localScale = Vector3.one * Mathf.LerpUnclamped(1.6f, 1f, pop);
    }

    // ---- game over ----

    private void UpdateGameOver()
    {
        if (gameOverPanel == null || gameState == null) return;
        bool over = gameState.IsGameOver;
        if (gameOverPanel.activeSelf != over) gameOverPanel.SetActive(over);
        if (!over) return;

        if (gameOverStatsText != null)
            gameOverStatsText.text = $"SCORE {Mathf.Min(gameState.Score, 999999):D6}   STAGE {gameState.StageIndex}";
        if (gameOverPromptText != null)
        {
            // The restart prompt appears once a jump can actually restart, then blinks.
            bool ready = gameState.SecondsSinceGameOver >= 2f;
            gameOverPromptText.enabled = ready && Mathf.Repeat(Time.time, 1f) < 0.7f;
        }
    }

    // ---- helpers ----

    private static string Capitalise(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
