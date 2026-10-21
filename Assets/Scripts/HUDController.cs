using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Screen-space HUD (design report module table): stage/score/lives, gateway
/// connection status, degraded-mode messaging, and per-action feedback.
///
/// Styled after the classic Mario HUD (pixel font, drop shadows), laid out
/// by HUDSetup:
/// - top bar:            SCORE (6 digits) · COINS (coin ×NN) · STAGE ("1-2") · LIVES (heart ×N) · POWER
///                       (fire-flower icon while held) · HELD (shell icon while carrying one)
/// - boss bar (top):     BOWSER and his health, while he's on the stage
/// - connection (top-right, small)
/// - banner (top-centre): the current *state* — scanning, tracking lost, no
///                       room to place a stage, placing, distance to the flag.
///                       Highest priority wins; polled every frame.
/// - scan panel:         "GET READY … JUMP TO START SCANNING" until the player
///                       jumps; then "SCAN THE FLOOR", the floor found so far
///                       (a meter against what a full-size stage likes), and
///                       "JUMP WHEN DONE" once enough is found — the player
///                       confirms the scan; "READY!" pops in when they do
/// - title (centre):     big pop-in intro card "WORLD 2-1" with the stage's name and
///                       a tip underneath (StageInfo) when a stage starts, "STAGE CLEAR!"
///                       when it ends, "WARP!" through a pipe
/// - feedback (centre):  transient *events* — action results from
///                       EventResolver.OnEventProcessed and score gains. Fades.
/// - score popups:       "+100" floating up in the world where the points were scored
/// - end screen:         dimmed overlay, GAME OVER or GAME CLEAR!, the game's stats
///                       (score, coins, enemies, time, accuracy) and the restart prompt
/// - damage:             a red screen flash when a life is lost, and the heart
///                       blinks while the player is invulnerable (the player is
///                       the camera, so there's no character to blink)
///
/// Feedback keeps the report's distinction: Gates 3-5 ("recognised but not
/// permitted") are shown prominently with the reason; Gate 2 low confidence
/// ("not registered") is a quiet hint; duplicates and Gate 1 are silent (the
/// banner already explains why nothing is being accepted). State-only actions
/// (a plain duck, a jump that hit nothing) are silent too. Damage and
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
    [SerializeField] private TMP_Text coinsText;
    [SerializeField] private TMP_Text stageText;
    [SerializeField] private TMP_Text livesText;
    [SerializeField] private Image livesIcon;
    [SerializeField] private Image powerIcon;
    [SerializeField] private TMP_Text powerEmptyText;
    [SerializeField] private Image heldIcon;
    [SerializeField] private TMP_Text heldEmptyText;
    [SerializeField] private TMP_Text connectionText;

    [Header("Centre")]
    [SerializeField] private TMP_Text bannerText;
    [SerializeField] private TMP_Text feedbackText;
    [SerializeField] private TMP_Text titleText;
    [Tooltip("Under the title: the stage's name and tip on its intro card.")]
    [SerializeField] private TMP_Text subtitleText;

    [Header("Boss")]
    [SerializeField] private GameObject bossPanel;
    [SerializeField] private Image bossFill;

    [Header("Scan")]
    [SerializeField] private GameObject scanPanel;
    [SerializeField] private TMP_Text scanTitleText;
    [Tooltip("The progress bar's parts (hidden until the scan starts).")]
    [SerializeField] private GameObject scanBar;
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
    [SerializeField] private TMP_Text gameOverTitleText;
    [SerializeField] private TMP_Text gameOverStatsText;
    [SerializeField] private TMP_Text gameOverPromptText;

    [Header("Title")]
    [SerializeField] private float titleSeconds = 1.8f;
    [SerializeField] private float titlePopSeconds = 0.18f;
    [Tooltip("How long an intro card (stage name and tip) stays up; other titles use Title Seconds.")]
    [SerializeField] private float introCardSeconds = 3.2f;

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
    private bool _introCard;
    private Color _titleColor;
    private float _damageAt = float.NegativeInfinity;
    private Vector3 _popupAt;
    private int _popupFrame = int.MinValue;

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
        HeadsetView.OnTuned += HandleHeadsetTuned;
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
        HeadsetView.OnTuned -= HandleHeadsetTuned;
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
        UpdateBoss();
    }

    // ---- persistent state ----

    private void UpdateStats()
    {
        if (gameState == null) return;

        // Score gains (hits, stage-clear bonus) flash as feedback.
        int delta = gameState.Score - _lastScore;
        _lastScore = gameState.Score;
        if (delta > 0)
        {
            ShowFeedback(FeedbackPrefix() + $"+{delta}", goodColor);
            // In the world too, where it was scored (if we know where).
            if (Time.frameCount - _popupFrame <= 2 && feedbackText != null)
                ScorePopup.Spawn($"+{delta}", _popupAt, titleColor, feedbackText.font, feedbackText.fontSharedMaterial);
            _popupFrame = int.MinValue;
        }

        if (scoreText != null) scoreText.text = Mathf.Min(gameState.Score, 999999).ToString("D6");
        if (coinsText != null) coinsText.text = $"x{gameState.Coins:D2}";
        if (stageText != null) stageText.text = gameState.StageName;
        if (livesText != null) livesText.text = $"x{Mathf.Max(0, gameState.Lives)}";
        if (powerIcon != null) powerIcon.enabled = gameState.HasFireFlower;
        if (powerEmptyText != null) powerEmptyText.enabled = !gameState.HasFireFlower;
        bool holding = gameState.Held != HeldItem.None;
        if (heldIcon != null) heldIcon.enabled = holding;
        if (heldEmptyText != null) heldEmptyText.enabled = !holding;
    }

    private void UpdateBoss()
    {
        if (bossPanel == null) return;
        var boss = Bowser.Active;
        bool show = boss != null && !boss.IsDefeated && gameState != null && !gameState.IsFinished
                    && levelGenerator != null && levelGenerator.CurrentStage != null && !levelGenerator.InBonusRoom;
        if (bossPanel.activeSelf != show) bossPanel.SetActive(show);
        if (show && bossFill != null)
            bossFill.fillAmount = Mathf.MoveTowards(bossFill.fillAmount, (float)boss.Health / boss.MaxHealth, Time.deltaTime);
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

        var boss = Bowser.Active;
        if (gameState != null && gameState.IsFinished)
        {
            message = null; // the end screen says it all
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
        else if (levelGenerator != null && levelGenerator.InBonusRoom)
        {
            message = "BONUS ROOM - SQUAT ON THE PIPE TO GO BACK";
            color = titleColor;
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
        else if (boss != null && !boss.IsDefeated && boss.IsWarning)
        {
            message = "BOWSER'S FIRE - SQUAT!";
            color = warningColor;
        }
        else if (levelGenerator != null && levelGenerator.CurrentGoal != null && levelGenerator.CurrentGoal.Locked)
        {
            message = "BEAT BOWSER TO REACH THE AXE";
            color = deniedColor;
        }
        else if (levelGenerator != null && levelGenerator.CurrentGoal != null && levelGenerator.CurrentGoal.InReach)
        {
            message = $"JUMP TO GRAB THE {(levelGenerator.CurrentGoal.EndsGame ? "AXE" : "FLAG")}!";
            color = goodColor;
        }
        else if (levelGenerator != null && levelGenerator.CurrentGoal != null
                 && !float.IsInfinity(levelGenerator.CurrentGoal.PlayerDistance))
        {
            message = $"{(levelGenerator.CurrentGoal.EndsGame ? "AXE" : "FLAG")} {levelGenerator.CurrentGoal.PlayerDistance:F1} M";
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

        if (result.ThrewShell)
        {
            ShowFeedback("Shell!", goodColor);
            return;
        }
        if (result.LaunchedProjectile)
        {
            ShowFeedback("Fireball!", deniedColor);
            return;
        }

        if (result.Resolved)
        {
            // The score change is picked up in UpdateStats; prefix it with the action.
            string label = result.Target is WarpPipe ? "Warp"
                         : result.Target is StageGoal goal ? (goal.EndsGame ? "Axe" : "Flag")
                         : action;
            if (result.Target is InteractableBase hitTarget) NotePopupAt(hitTarget.HitPoint);
            if (evt.actionLabel == "clap")
            {
                ShowFeedback("Got a shell! Throw it", goodColor);
                return;
            }
            _pendingPrefix = $"{label}!  ";
            _pendingPrefixFrame = Time.frameCount;
            ShowFeedback($"{label}!", goodColor);
            if (result.Target is WarpPipe) ShowTitle("WARP!", goodColor);
            return;
        }

        switch (result.Gate)
        {
            case 3:
                ShowFeedback(result.Reason switch
                {
                    "fireballs recharging" => "Fireballs recharging",
                    "nothing to throw" => "Nothing to throw — clap to pick up a shell",
                    "hands full" => "Hands full — throw it first",
                    _ => $"Can't {evt.actionLabel} right now",
                }, deniedColor);
                break;
            case 4:
                ShowFeedback($"{action}: nothing in range", deniedColor);
                break;
            case 5:
                ShowFeedback($"{action}: already used", deniedColor);
                break;
            case 2 when !result.Reason.StartsWith("duplicate", StringComparison.Ordinal) && !result.IsRepeat:
                ShowFeedback("Move not recognised — try again", quietColor);
                break;
            // Gate 1 (session not ready), duplicates and repeats: silent.
        }
    }

    private void HandleImpact(GameEvent evt, Component target)
    {
        if (target is InteractableBase hitTarget) NotePopupAt(hitTarget.HitPoint);
        string hit = evt.actionLabel == "throw" && gameState != null && !gameState.HasFireFlower ? "Shell hit!" : "Hit!";
        _pendingPrefix = $"{hit}  ";
        _pendingPrefixFrame = Time.frameCount;
        ShowFeedback(hit, goodColor);
    }

    /// <summary>Where the next score gain (within a couple of frames) pops up in the world.</summary>
    private void NotePopupAt(Vector3 position)
    {
        _popupAt = position + Vector3.up * 0.15f;
        _popupFrame = Time.frameCount;
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
            livesIcon.enabled = !gameState.IsInvulnerable || gameState.IsFinished || Mathf.Repeat(Time.time, 0.25f) < 0.13f;
    }

    private void HandleHeadsetTuned(string text) => ShowFeedback(text, quietColor);

    private void HandleBulletBillWarning() => ShowFeedback("Bullet Bill! Squat!", warningColor);

    private void HandlePlacementFailed() => _placementFailedAt = Time.time;

    private void HandleStageStarted()
    {
        _placementFailedAt = float.NegativeInfinity;
        if (gameState == null) return;
        // Intro card: WORLD 2-1, the stage's name, and a tip (campaign stages only).
        ShowTitle($"WORLD {gameState.StageName}", Color.white);
        var info = levelGenerator != null && levelGenerator.CurrentStage != null ? levelGenerator.CurrentStage.GetComponent<StageInfo>() : null;
        _introCard = true;
        if (info != null && subtitleText != null && !string.IsNullOrEmpty(info.Title))
            subtitleText.text = string.IsNullOrEmpty(info.Tip) ? info.Title : $"{info.Title}\n<size=60%><color=#FFE08A>{info.Tip.ToUpperInvariant()}</color></size>";
    }

    private void HandleStageCompleted() => ShowTitle("STAGE CLEAR!", titleColor);

    private void HandleScanComplete()
    {
        ShowTitle("READY!", goodColor);
        if (PlayerBody.EyeHeight is { } eyes) ShowFeedback($"Eye height {eyes:F2} m", quietColor); // stages are sized to it
    }

    // ---- scan panel ----

    private void UpdateScan()
    {
        if (scanPanel == null || sessionManager == null) return;
        bool scanning = sessionManager.IsTracking && !sessionManager.IsScanComplete && (gameState == null || !gameState.IsFinished);
        if (scanPanel.activeSelf != scanning) scanPanel.SetActive(scanning);
        if (!scanning) return;

        // Before the scan: the player gets set up, then jumps to start.
        bool started = sessionManager.IsScanStarted;
        if (scanTitleText != null) scanTitleText.text = started ? "SCAN THE FLOOR" : "GET READY";
        if (scanBar != null && scanBar.activeSelf != started) scanBar.SetActive(started);
        if (!started)
        {
            if (scanHintText != null)
            {
                scanHintText.text = Mathf.Repeat(Time.time, 1f) < 0.7f ? "FACE THE ROOM, THEN JUMP TO START SCANNING" : "";
                scanHintText.color = titleColor;
            }
            return;
        }

        float area = sessionManager.TotalScannedArea;
        float meter = Mathf.Clamp01(area / Mathf.Max(0.01f, sessionManager.FullSizeFloorArea));
        if (scanFill != null) scanFill.fillAmount = meter;
        if (scanPercentText != null) scanPercentText.text = $"{area:F1} M²";
        if (scanHintText != null)
        {
            if (sessionManager.CanConfirmScan)
            {
                // Enough to play: the player decides when they've scanned everything.
                bool blinkOn = Mathf.Repeat(Time.time, 1f) < 0.7f;
                scanHintText.text = (int)(Time.time / hintSeconds) % 2 == 0
                    ? (blinkOn ? "SCANNED EVERYTHING? JUMP TO START" : "")
                    : "MORE FLOOR = BIGGER STAGES";
                scanHintText.color = titleColor;
            }
            else
            {
                scanHintText.text = (int)(Time.time / hintSeconds) % 2 == 0
                    ? "WALK AROUND - SCAN ALL THE FLOOR YOU CAN PLAY ON"
                    : "ONLY FLOOR COUNTS - NOT TABLES OR BEDS";
                scanHintText.color = new Color(1f, 0.92f, 0.75f);
            }
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
        if (subtitleText != null) subtitleText.text = ""; // HandleStageStarted sets it for intro cards
        _introCard = false;
        _titleColor = color;
        _titleShownAt = Time.time;
        UpdateTitle();
    }

    /// <summary>Pops in (overshoot then settle), holds, fades.</summary>
    private void UpdateTitle()
    {
        if (titleText == null) return;
        float age = Time.time - _titleShownAt;
        float hold = _introCard ? introCardSeconds : titleSeconds;
        float alpha = age <= hold ? 1f : 1f - Mathf.Clamp01((age - hold) / feedbackFadeSeconds);
        titleText.color = new Color(_titleColor.r, _titleColor.g, _titleColor.b, alpha);
        float pop = Mathf.Clamp01(age / titlePopSeconds);
        titleText.rectTransform.localScale = Vector3.one * Mathf.LerpUnclamped(1.6f, 1f, pop);
        if (subtitleText != null) subtitleText.color = new Color(1f, 1f, 1f, alpha * Mathf.Clamp01((age - titlePopSeconds) / 0.25f));
    }

    // ---- end screen: game over or game clear ----

    private void UpdateGameOver()
    {
        if (gameOverPanel == null || gameState == null) return;
        bool over = gameState.IsFinished;
        if (gameOverPanel.activeSelf != over) gameOverPanel.SetActive(over);
        if (!over) return;

        bool won = gameState.IsGameClear;
        if (gameOverTitleText != null)
        {
            gameOverTitleText.text = won ? "GAME CLEAR!" : "GAME OVER";
            gameOverTitleText.color = won ? titleColor : new Color(1f, 0.3f, 0.25f);
        }
        if (gameOverStatsText != null)
        {
            var st = gameState.GameStats;
            int attempts = st.actionsResolved + st.actionsRefused;
            int seconds = Mathf.RoundToInt(gameState.PlaySeconds);
            string reached = won ? "ALL STAGES" : $"STAGE {gameState.StageName}";
            gameOverStatsText.text =
                $"SCORE {Mathf.Min(gameState.Score, 999999):D6}   {reached}\n" +
                $"<size=75%>COINS {st.coinsCollected}   ENEMIES {st.enemiesDefeated}   TIME {seconds / 60}:{seconds % 60:D2}" +
                (attempts > 0 ? $"   ACCURACY {Mathf.RoundToInt(100f * st.actionsResolved / attempts)}%" : "") + "</size>";
        }
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
