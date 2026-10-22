using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Editor-only stand-in for the wearables + classifier, for playtesting in
/// Play Mode with XR Simulation: number keys become classified actions fed
/// straight into EventResolver (no network), so responses are instant.
///
///   1 jump   2 stomp   3 squat   4 throw   5 punch   6 clap
///
/// Every action is discrete (protocol v2): one event per key press, as the
/// classifier sends one event per motion.
/// Disables itself outside the Editor, so it's inert in device builds.
/// Uses the Input System package (the XR Simulation camera does too).
/// </summary>
public class EditorActionInput : MonoBehaviour
{
    [SerializeField] private EventResolver eventResolver;
    [SerializeField] private float confidence = 0.95f;
    [SerializeField] private bool showKeyLegend = true;

    private static readonly (Key key, string action)[] Bindings =
    {
        (Key.Digit1, "jump"),
        (Key.Digit2, "stomp"),
        (Key.Digit3, "squat"),
        (Key.Digit4, "throw"),
        (Key.Digit5, "punch"),
        (Key.Digit6, "clap"),
    };

    private int _nextId;
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

        foreach (var (key, action) in Bindings)
        {
            if (!keyboard[key].wasPressedThisFrame) continue;
            var result = eventResolver.Resolve(new GameEvent(action, confidence, $"kb-{_nextId++}", "editor"));
            _last = $"{action} → {(result.Resolved ? "ok" : $"gate {result.Gate} ({result.Reason})")}";
        }
        if (keyboard[Key.R].wasPressedThisFrame)
        {
            var reset = FindAnyObjectByType<HardReset>(); // on a device: look up for 5 s
            if (reset != null) { _last = "hard reset"; reset.Trigger(); }
        }
    }

    private void OnGUI()
    {
        if (!showKeyLegend) return;
        GUI.Label(new Rect(12, Screen.height - 44, 900, 22), "Keys: 1 jump  2 stomp  3 squat  4 throw  5 punch  6 clap  R hard reset   |   right-drag look, WASD move");
        GUI.Label(new Rect(12, Screen.height - 24, 900, 22), $"Last: {_last}");
    }
}
