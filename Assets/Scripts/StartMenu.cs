using UnityEngine;

/// <summary>
/// The start menu, before the scan (it replaces "jump to start scanning";
/// a hard reset brings it back). Touch-free, like everything in the headset:
/// a quick head nod down moves the selection down, a quick tilt up moves it
/// up (wrapping round), and a clap picks the selected item (any arm action:
/// the classifier confuses clap, throw and punch, and here only a pick makes sense).
///
///   TRAINING + GAME   the training round (WORLD 0-1), then 1-1
///   GAME ONLY         straight to 1-1
///   GAME: FULL/SHORT  toggles; short is World 1 (1-1 to 1-4), then straight to
///                     Bowser's castle (2-4), so a demo reaches the boss sooner
///   GOD MODE: ON/OFF  toggles, on by default; on, the game has infinite lives too (training always has)
///
/// Picking either game option sets the training choice (LevelGenerator),
/// god mode and the game length (GameStateManager) and starts the scan (ARSessionManager); the
/// rest is unchanged (scan, jump to confirm, play). While the menu is open
/// (<see cref="Active"/>), a jump doesn't start the scan.
///
/// Nods: the view's pitch is compared with a neutral pitch that slowly
/// follows the head, so a quick dip of nodDegrees (and back) counts while a
/// slow look round doesn't; each nod counts once, when it passes the
/// threshold, and the head has to come back near neutral before the next.
/// Holding the head away for longer than holdResetSeconds just becomes the
/// new neutral. Logic is in Tick so tests can drive it; in the Editor the
/// up/down arrow keys move the selection (EditorActionInput).
/// </summary>
public class StartMenu : MonoBehaviour
{
    public enum Item { TrainingAndGame, GameOnly, GameLength, GodMode }
    public const int ItemCount = 4;

    [SerializeField] private Camera arCamera;
    [SerializeField] private EventResolver eventResolver;
    [SerializeField] private ARSessionManager sessionManager;
    [SerializeField] private LevelGenerator levelGenerator;
    [Tooltip("Picking an item: a clap with at least this confidence.")]
    [SerializeField] private float confirmMinConfidence = 0.6f;

    [Header("Nods")]
    [Tooltip("How far (degrees) the view must dip below / rise above neutral to count as a nod.")]
    [SerializeField] private float nodDegrees = 15f;
    [Tooltip("The head counts as back once within this (degrees) of neutral, ready for the next nod.")]
    [SerializeField] private float returnDegrees = 7f;
    [Tooltip("Seconds for the neutral pitch to follow the head; slow movements become the new neutral, quick nods don't.")]
    [SerializeField] private float neutralSeconds = 1.2f;
    [Tooltip("Held away from neutral this long (looking down at something), it becomes the new neutral.")]
    [SerializeField] private float holdResetSeconds = 1.5f;

    /// <summary>The open menu, or null once an item has started the game.</summary>
    public static StartMenu Active { get; private set; }

    public bool IsOpen { get; private set; } = true;
    public Item Selected { get; private set; } = Item.TrainingAndGame;
    /// <summary>On by default (Oliver 2026-11-12: demos shouldn't end in a game over).</summary>
    public bool GodMode { get; private set; } = true;
    /// <summary>World 1, then Bowser's castle (instead of both worlds).</summary>
    public bool ShortGame { get; private set; }

    /// <summary>Raised whenever the selection, god mode or open state changes (the HUD redraws, a blip plays).</summary>
    public event System.Action Changed;

    private float _neutral = float.NaN;
    private int _excursion; // 0 at neutral, -1 nodding down, +1 tilting up
    private float _excursionAge;

    private void Awake()
    {
        if (arCamera == null) arCamera = Camera.main;
        if (eventResolver == null) eventResolver = FindAnyObjectByType<EventResolver>();
        if (sessionManager == null) sessionManager = FindAnyObjectByType<ARSessionManager>();
        if (levelGenerator == null) levelGenerator = FindAnyObjectByType<LevelGenerator>();
    }

    private void OnEnable()
    {
        if (IsOpen) Active = this;
        if (eventResolver != null) eventResolver.OnEventProcessed += HandleEventProcessed;
    }

    private void OnDisable()
    {
        if (Active == this) Active = null;
        if (eventResolver != null) eventResolver.OnEventProcessed -= HandleEventProcessed;
    }

    private void Update()
    {
        if (IsOpen && arCamera != null) Tick(Time.unscaledDeltaTime, arCamera.transform.forward);
    }

    /// <summary>Advances nod detection with the view direction <paramref name="forward"/>; returns the move made (-1 up, +1 down, 0 none).</summary>
    public int Tick(float dt, Vector3 forward)
    {
        if (!IsOpen) return 0;
        float pitch = Mathf.Asin(Mathf.Clamp(forward.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        if (float.IsNaN(_neutral)) _neutral = pitch;
        float offset = pitch - _neutral;

        if (_excursion != 0)
        {
            _excursionAge += dt;
            if (Mathf.Abs(offset) <= returnDegrees || _excursionAge >= holdResetSeconds)
            {
                _excursion = 0;
                _neutral = pitch; // back (or settled somewhere new): measure the next nod from here
            }
            return 0;
        }

        if (Mathf.Abs(offset) >= nodDegrees)
        {
            _excursion = offset < 0f ? -1 : 1;
            _excursionAge = 0f;
            int move = offset < 0f ? 1 : -1; // nod down: next item; tilt up: previous
            Move(move);
            return move;
        }

        _neutral = Mathf.Lerp(_neutral, pitch, 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, neutralSeconds)));
        return 0;
    }

    /// <summary>Moves the selection by <paramref name="delta"/> items, wrapping round.</summary>
    public void Move(int delta)
    {
        if (!IsOpen || delta == 0) return;
        Selected = (Item)(((int)Selected + delta % ItemCount + ItemCount) % ItemCount);
        AudioManager.Play(Sfx.Coin);
        Changed?.Invoke();
    }

    /// <summary>Picks the selected item: toggles god mode or the game length, or starts the game (with or without training).</summary>
    public void Confirm()
    {
        if (!IsOpen) return;
        if (Selected == Item.GodMode)
        {
            GodMode = !GodMode;
            AudioManager.Play(GodMode ? Sfx.PowerUp : Sfx.Denied);
            Debug.Log($"[StartMenu] God mode {(GodMode ? "on" : "off")}.");
            Changed?.Invoke();
            return;
        }
        if (Selected == Item.GameLength)
        {
            ShortGame = !ShortGame;
            AudioManager.Play(Sfx.PowerUp);
            Debug.Log($"[StartMenu] {(ShortGame ? "Short" : "Full")} game.");
            Changed?.Invoke();
            return;
        }

        bool training = Selected == Item.TrainingAndGame;
        IsOpen = false;
        if (Active == this) Active = null;
        var gs = GameStateManager.Instance;
        if (gs != null)
        {
            gs.GodMode = GodMode;
            gs.ShortGame = ShortGame;
        }
        if (levelGenerator != null) levelGenerator.PlayTrainingNext(training);
        AudioManager.Play(Sfx.OneUp);
        Debug.Log($"[StartMenu] {(training ? "Training + game" : "Game only")}, {(ShortGame ? "short" : "full")} game, god mode {(GodMode ? "on" : "off")} — scan next.");
        if (sessionManager != null) sessionManager.StartScan();
        Changed?.Invoke();
    }

    /// <summary>Hard reset (HardReset): back to the menu, keeping the last choices.</summary>
    public void Open()
    {
        IsOpen = true;
        if (isActiveAndEnabled) Active = this;
        _neutral = float.NaN;
        _excursion = 0;
        Changed?.Invoke();
    }

    private void HandleEventProcessed(GameEvent evt, ResolutionResult result)
    {
        if (!IsOpen || !EventResolver.IsArmAction(evt.actionLabel) || evt.confidence < confirmMinConfidence || result.IsRepeat) return;
        Confirm();
    }
}
