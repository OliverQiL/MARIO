using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Editor-only stand-in for the wearables + classifier, for playtesting in
/// Play Mode with XR Simulation: number keys become classified actions fed
/// straight into EventResolver (no network), so responses are instant.
///
///   1 stand   2 walk   3 jump   4 squat   5 fireball   6 stomp
///
/// Mirrors the real event stream (Docs/GAME_DESIGN.md): continuous actions
/// (stand, walk, stomp) repeat every classification window while the key is
/// held; discrete actions (jump, squat, fireball) fire once per press.
/// Disables itself outside the Editor, so it's inert in device builds.
/// Uses the Input System package (the XR Simulation camera does too).
/// </summary>
public class EditorActionInput : MonoBehaviour
{
    [SerializeField] private EventResolver eventResolver;
    [SerializeField] private float confidence = 0.95f;
    [Tooltip("Repeat interval for held continuous actions — the classifier's hop (~100 ms).")]
    [SerializeField] private float continuousInterval = 0.1f;
    [SerializeField] private bool showKeyLegend = true;

    private static readonly (Key key, string action, bool continuous)[] Bindings =
    {
        (Key.Digit1, "stand", true),
        (Key.Digit2, "walk", true),
        (Key.Digit3, "jump", false),
        (Key.Digit4, "squat", false),
        (Key.Digit5, "fireball", false),
        (Key.Digit6, "stomp", true),
    };

    private int _nextId;
    private float _nextRepeat;
    private string _last = "";

    private void Awake()
    {
        if (!Application.isEditor) { enabled = false; return; }
        if (eventResolver == null) eventResolver = FindAnyObjectByType<EventResolver>();
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null || eventResolver == null) return;

        bool repeatDue = Time.time >= _nextRepeat;
        foreach (var (key, action, continuous) in Bindings)
        {
            var control = keyboard[key];
            if (control.wasPressedThisFrame || (continuous && control.isPressed && repeatDue))
            {
                var result = eventResolver.Resolve(new GameEvent(action, confidence, $"kb-{_nextId++}", "editor"));
                _last = $"{action} → {(result.Resolved ? "ok" : $"gate {result.Gate}")}";
                if (continuous) _nextRepeat = Time.time + continuousInterval;
            }
        }
    }

    private void OnGUI()
    {
        if (!showKeyLegend) return;
        GUI.Label(new Rect(12, Screen.height - 44, 900, 22), "Keys: 1 stand  2 walk  3 jump  4 squat  5 fireball  6 stomp (hold)   |   right-drag look, WASD move");
        GUI.Label(new Rect(12, Screen.height - 24, 900, 22), $"Last: {_last}");
    }
}
