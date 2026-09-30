using System;
using UnityEngine;
using TMPro;

/// <summary>
/// Screen-space HUD (design report module table): stage/score/lives, gateway
/// connection status, degraded-mode messaging, and per-action feedback.
/// Replaces the scan-only ScanHUD.
///
/// Four text elements:
/// - stats (top-left):   Stage N · Score S · lives
/// - connection (top-right)
/// - banner (centre):    the current *state* — scanning, tracking lost, no
///                       room to place a stage, placing, stage clear. Highest
///                       priority wins; polled every frame.
/// - feedback (below):   transient *events* — action results from
///                       EventResolver.OnEventProcessed and score gains. Fades.
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

    [Header("Text Elements")]
    [SerializeField] private TMP_Text statsText;
    [SerializeField] private TMP_Text connectionText;
    [SerializeField] private TMP_Text bannerText;
    [SerializeField] private TMP_Text feedbackText;

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

    private float _feedbackShownAt = float.NegativeInfinity;
    private Color _feedbackColor;
    private float _placementFailedAt = float.NegativeInfinity;
    private int _lastScore;

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
        }
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
        }
        BulletBillCannon.OnWarning -= HandleBulletBillWarning;
    }

    private void Update()
    {
        UpdateStats();
        UpdateConnection();
        UpdateBanner();
        UpdateFeedbackFade();
    }

    // ---- persistent state ----

    private void UpdateStats()
    {
        if (statsText == null || gameState == null) return;

        // Score gains (hits, stage-clear bonus) flash as feedback.
        int delta = gameState.Score - _lastScore;
        _lastScore = gameState.Score;
        if (delta > 0) ShowFeedback(FeedbackPrefix() + $"+{delta}", goodColor);

        string flower = gameState.HasFireFlower ? "   <color=#FF8A1F>FIRE</color>" : "";
        statsText.text = $"Stage {gameState.StageIndex}   Score {gameState.Score}   {Hearts(gameState.Lives)}{flower}";
    }

    private void UpdateConnection()
    {
        if (connectionText == null) return;
        if (networkClient == null) { connectionText.text = ""; return; }

        switch (networkClient.State)
        {
            case GatewayConnection.ConnectionState.Connected:
                connectionText.text = "<color=#4CFF66>●</color> Gateway";
                break;
            case GatewayConnection.ConnectionState.Connecting:
                connectionText.text = "<color=#FFB020>●</color> Connecting…";
                break;
            default:
                connectionText.text = "<color=#FF5A4D>●</color> Gateway offline";
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
            message = gameState.SecondsSinceGameOver >= 2f ? "GAME OVER — jump to play again" : "GAME OVER";
            color = warningColor;
        }
        else if (sessionManager != null && !sessionManager.IsTracking)
        {
            message = "Tracking lost — look around slowly";
            color = warningColor;
        }
        else if (sessionManager != null && !sessionManager.IsScanComplete)
        {
            message = $"Scan the floor: {sessionManager.TotalScannedArea:F1} / {sessionManager.RequiredFloorArea:F1} m²";
        }
        else if (levelGenerator != null && levelGenerator.IsTransitioning)
        {
            message = "Stage clear!";
            color = goodColor;
        }
        else if (levelGenerator != null && levelGenerator.CurrentStage == null)
        {
            bool recentlyFailed = Time.time - _placementFailedAt < placementFailedHoldSeconds;
            message = recentlyFailed ? "No room here — move to a clearer area"
                    : levelGenerator.IsWaitingForMesh ? "Mapping the room…"
                    : "Placing stage…";
            color = recentlyFailed ? warningColor : Color.white;
        }
        else if (levelGenerator != null && levelGenerator.CurrentGoal != null
                 && !float.IsInfinity(levelGenerator.CurrentGoal.PlayerDistance))
        {
            message = $"Walk to the flag ({levelGenerator.CurrentGoal.PlayerDistance:F1} m)";
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

    private void HandleLifeLost(int livesLeft) =>
        ShowFeedback(livesLeft > 0 ? "Ouch! −1 life" : "Out of lives!", warningColor);

    private void HandleBulletBillWarning() => ShowFeedback("Bullet Bill! Squat!", warningColor);

    private void HandlePlacementFailed() => _placementFailedAt = Time.time;

    private void HandleStageStarted() => _placementFailedAt = float.NegativeInfinity;

    private void ShowFeedback(string message, Color color)
    {
        if (feedbackText == null) return;
        feedbackText.text = message;
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

    // ---- helpers ----

    private static string Hearts(int lives) => lives <= 0 ? "<color=#FF5A4D>no lives</color>" : $"<color=#FF5A4D>{new string('♥', lives)}</color>";

    private static string Capitalise(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
