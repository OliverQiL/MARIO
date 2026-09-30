using System;

/// <summary>
/// Wire-format DTOs for the gateway WebSocket protocol (Docs/GATEWAY_PROTOCOL.md).
/// Field names match the JSON exactly because JsonUtility maps by name. If the
/// gateway's format changes, this file is the only thing that should need to.
/// </summary>
public static class GatewayMessages
{
    public const int ProtocolVersion = 1;

    public const string VisualizerReady = "VISUALIZER_READY";
    public const string GameEventType = "GAME_EVENT";
    public const string EventAck = "EVENT_ACK";
    public const string HapticCmd = "HAPTIC_CMD";
    /// <summary>Development only (the mock gateway's --keys mode; the real gateway never sends it): live headset tuning.</summary>
    public const string DevTune = "DEV_TUNE";

    /// <summary>Parsed first to find out which concrete message this is.</summary>
    [Serializable]
    public class Envelope
    {
        public string type;
        public int protocol_version;
    }

    /// <summary>DEV_TUNE: nudge a HeadsetView setting ("eyeImageScale", "lensSeparation") by delta, or "reset" it.</summary>
    [Serializable]
    public class DevTuneMsg
    {
        public string type;
        public string param;
        public float delta;
    }

    [Serializable]
    public class ReadyMsg
    {
        public string type = VisualizerReady;
        public int protocol_version = ProtocolVersion;
        public string app_version;
    }

    [Serializable]
    public class GameEventMsg
    {
        public string type;
        public int protocol_version;
        public string session_id;
        public string event_id;
        public long window_id;
        public string action;
        public float confidence;
        public double timestamp;
    }

    [Serializable]
    public class AckMsg
    {
        public string type = EventAck;
        public int protocol_version = ProtocolVersion;
        public string session_id;
        public string event_id;
        public string outcome;
        public int gate;
        public string reason;
        public float processing_ms;
    }

    [Serializable]
    public class HapticMsg
    {
        public string type = HapticCmd;
        public int protocol_version = ProtocolVersion;
        public string session_id;
        public string event_id;
        public string target;
        public string pattern;
    }
}
