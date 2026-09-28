using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// The phone's single network connection: a secure WebSocket to the laptop
/// gateway (design report module table; wire format in Docs/GATEWAY_PROTOCOL.md).
///
/// - Connects with certificate pinning (GatewayConnection) and reconnects with
///   jittered exponential backoff; sends VISUALIZER_READY after every connect.
/// - Drains received GAME_EVENTs on the main thread, feeds each into
///   EventResolver.Resolve, and replies with an EVENT_ACK carrying the outcome.
///   Duplicates are still ACKed (so the gateway stops retrying) — dedup itself
///   stays in EventResolver's Gate 2, the single source of truth.
/// - Issues HAPTIC_CMD (Docs/GAME_DESIGN.md): "success" on a hit and on a
///   fireball impact (EventResolver.OnImpact), "denied" on Gates 3-5 for
///   discrete actions only (continuous stand/walk/stomp are re-sent every
///   window and would buzz constantly), "damage" to both straps when the
///   player loses a life. Nothing for state-only actions or Gates 1-2.
/// </summary>
public class NetworkClient : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private EventResolver eventResolver;

    [Header("Gateway")]
    [Tooltip("wss://<gateway-ip>:<port>/  — ws:// is allowed for local debugging only.")]
    [SerializeField] private string gatewayUrl = "wss://192.168.0.111:8765/";
    [Tooltip("SHA-256 fingerprint of the gateway's certificate (hex, colons optional). Printed by tools/mock_gateway.py and `openssl x509 -noout -fingerprint -sha256`.")]
    [SerializeField] private string pinnedCertSha256 = "";
    [SerializeField] private bool connectOnStart = true;

    [Header("Reconnect")]
    [SerializeField] private float initialBackoffSeconds = 0.5f;
    [SerializeField] private float maxBackoffSeconds = 8f;
    [Range(0f, 0.5f)]
    [SerializeField] private float backoffJitter = 0.2f;

    [Header("Haptics")]
    [SerializeField] private bool sendHaptics = true;

    public GatewayConnection.ConnectionState State => _connection?.State ?? GatewayConnection.ConnectionState.Disconnected;
    public bool IsConnected => State == GatewayConnection.ConnectionState.Connected;
    public string LastError { get; private set; }
    public int EventsReceived { get; private set; }
    public float LastProcessingMs { get; private set; }
    public string GatewayUrl => gatewayUrl;

    /// <summary>Raised on the main thread whenever the connection state changes.</summary>
    public event Action<GatewayConnection.ConnectionState> OnConnectionStateChanged;

    private GatewayConnection _connection;
    private CancellationTokenSource _cts;
    private GatewayConnection.ConnectionState _lastState = GatewayConnection.ConnectionState.Disconnected;
    private readonly System.Random _random = new();

    private void Awake()
    {
        if (eventResolver == null) eventResolver = FindAnyObjectByType<EventResolver>();
    }

    private void OnEnable()
    {
        eventResolver.OnEventProcessed += HandleEventProcessed;
        eventResolver.OnImpact += HandleImpact;
        var gs = GameStateManager.Instance;
        if (gs != null) gs.OnLifeLost.AddListener(HandleLifeLost);
    }

    private void Start()
    {
        if (connectOnStart) Connect();
    }

    private void OnDisable()
    {
        eventResolver.OnEventProcessed -= HandleEventProcessed;
        eventResolver.OnImpact -= HandleImpact;
        var gs = GameStateManager.Instance;
        if (gs != null) gs.OnLifeLost.RemoveListener(HandleLifeLost);
        Disconnect();
    }

    public void Connect()
    {
        if (_cts != null) return;
        if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "wss" && uri.Scheme != "ws"))
        {
            Debug.LogError($"[NetworkClient] Invalid gateway URL '{gatewayUrl}'.");
            return;
        }
        if (uri.Scheme == "ws") Debug.LogWarning("[NetworkClient] Using unencrypted ws:// — debugging only.");

        _cts = new CancellationTokenSource();
        ConnectionLoop(uri, _cts.Token);
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _cts = null;
        _connection?.Dispose();
    }

    private async void ConnectionLoop(Uri uri, CancellationToken token)
    {
        float backoff = initialBackoffSeconds;

        while (!token.IsCancellationRequested)
        {
            Debug.Log($"[NetworkClient] Connecting to {uri}…");
            _connection = new GatewayConnection(uri, pinnedCertSha256);
            await _connection.RunAsync(token);
            if (token.IsCancellationRequested) break;

            LastError = _connection.LastError;
            if (_connection.EverConnected) backoff = initialBackoffSeconds;

            float delay = backoff * (1f + (float)(_random.NextDouble() * 2 - 1) * backoffJitter);
            Debug.LogWarning($"[NetworkClient] Disconnected ({LastError ?? "no error"}) — retrying in {delay:F1}s.");
            if (!_connection.EverConnected && _connection.PresentedFingerprint != null)
                Debug.LogWarning($"[NetworkClient] Gateway presented certificate SHA-256 {_connection.PresentedFingerprint} (pinned: '{pinnedCertSha256}').");

            try { await Task.Delay(TimeSpan.FromSeconds(delay), token); }
            catch (OperationCanceledException) { break; }
            backoff = Mathf.Min(backoff * 2f, maxBackoffSeconds);
        }
    }

    private void Update()
    {
        var state = State;
        if (state != _lastState)
        {
            _lastState = state;
            if (state == GatewayConnection.ConnectionState.Connected)
            {
                Debug.Log("[NetworkClient] Connected — sending VISUALIZER_READY.");
                Send(new GatewayMessages.ReadyMsg { app_version = Application.version });
            }
            OnConnectionStateChanged?.Invoke(state);
        }

        if (_connection == null) return;
        while (_connection.Inbox.TryDequeue(out var json))
            HandleMessage(json);
    }

    private void HandleMessage(string json)
    {
        GatewayMessages.Envelope envelope;
        try { envelope = JsonUtility.FromJson<GatewayMessages.Envelope>(json); }
        catch (Exception e)
        {
            Debug.LogWarning($"[NetworkClient] Unparseable message ({e.Message}): {json}");
            return;
        }

        if (envelope == null || envelope.type != GatewayMessages.GameEventType)
        {
            Debug.LogWarning($"[NetworkClient] Ignoring message of type '{envelope?.type}'.");
            return;
        }
        if (envelope.protocol_version != GatewayMessages.ProtocolVersion)
            Debug.LogWarning($"[NetworkClient] Protocol version {envelope.protocol_version} != {GatewayMessages.ProtocolVersion}; processing anyway.");

        var msg = JsonUtility.FromJson<GatewayMessages.GameEventMsg>(json);
        if (string.IsNullOrEmpty(msg.event_id) || string.IsNullOrEmpty(msg.action))
        {
            Debug.LogWarning($"[NetworkClient] GAME_EVENT missing event_id/action: {json}");
            return;
        }

        EventsReceived++;
        var stopwatch = Stopwatch.StartNew();
        var evt = new GameEvent(msg.action, msg.confidence, msg.event_id, msg.session_id ?? "", msg.window_id, msg.timestamp);
        var result = eventResolver.Resolve(evt);
        LastProcessingMs = (float)stopwatch.Elapsed.TotalMilliseconds;

        Send(new GatewayMessages.AckMsg
        {
            session_id = msg.session_id,
            event_id = msg.event_id,
            outcome = result.Resolved ? "resolved" : "rejected",
            gate = result.Gate,
            reason = result.Reason,
            processing_ms = LastProcessingMs,
        });
    }

    private static readonly string[] ContinuousActions = { "stand", "walk", "stomp" };

    private void HandleEventProcessed(GameEvent evt, ResolutionResult result)
    {
        if (result.StateOnly || result.LaunchedProjectile) return; // projectile haptic comes on impact

        if (result.Resolved)
            SendHaptic(evt.sessionId, evt.eventId, LimbFor(evt.actionLabel), "success");
        else if (result.NotPermitted && Array.IndexOf(ContinuousActions, evt.actionLabel) < 0)
            SendHaptic(evt.sessionId, evt.eventId, LimbFor(evt.actionLabel), "denied");
        // Gates 1-2 (not registered): no haptic.
    }

    private void HandleImpact(GameEvent evt, Component target) =>
        SendHaptic(evt.sessionId, evt.eventId, LimbFor(evt.actionLabel), "success");

    private void HandleLifeLost(int livesLeft) => SendHaptic("", "", "both", "damage");

    private void SendHaptic(string sessionId, string eventId, string limb, string pattern)
    {
        if (!sendHaptics || !IsConnected) return;
        Send(new GatewayMessages.HapticMsg
        {
            session_id = sessionId,
            event_id = eventId,
            target = limb,
            pattern = pattern,
        });
    }

    private static string LimbFor(string action) => action switch
    {
        "fireball" => "wrist",
        "stomp" => "ankle",
        _ => "both",
    };

    private void Send(object message)
    {
        var connection = _connection;
        if (connection == null) return;
        _ = connection.SendAsync(JsonUtility.ToJson(message));
    }
}
