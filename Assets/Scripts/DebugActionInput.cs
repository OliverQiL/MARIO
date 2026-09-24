using UnityEngine;

/// <summary>
/// Dev-only input harness: binds number keys to fake classified actions and
/// feeds them into EventResolver exactly as NetworkClient will once it
/// exists. Lets the gate chain be tested in Play Mode with no device, no
/// comms pipeline, and no classifier.
/// 1 Stand, 2 Walk, 3 Jump, 4 Squat, 5 Fireball, 6 Stomp.
/// </summary>
public class DebugActionInput : MonoBehaviour
{
    [SerializeField] private EventResolver eventResolver;
    [SerializeField] private float fakeConfidence = 0.95f;

    private static readonly (KeyCode key, string action)[] Bindings =
    {
        (KeyCode.Alpha1, "stand"),
        (KeyCode.Alpha2, "walk"),
        (KeyCode.Alpha3, "jump"),
        (KeyCode.Alpha4, "squat"),
        (KeyCode.Alpha5, "fireball"),
        (KeyCode.Alpha6, "stomp"),
    };

    private int _nextEventId;

    private void Awake()
    {
        if (eventResolver == null) eventResolver = FindAnyObjectByType<EventResolver>();
    }

    private void Update()
    {
        foreach (var (key, action) in Bindings)
        {
            if (Input.GetKeyDown(key))
            {
                var evt = new GameEvent(action, fakeConfidence, $"debug-{_nextEventId++}");
                Debug.Log($"[DebugActionInput] Firing fake action '{action}'");
                eventResolver.Resolve(evt);
            }
        }
    }
}
