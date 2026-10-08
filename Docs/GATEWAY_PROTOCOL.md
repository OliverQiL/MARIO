# Visualizer ⇄ Gateway WebSocket Protocol (v1 — proposal)

> **Comms integrators:** start with [`COMMS_INTEGRATION.md`](COMMS_INTEGRATION.md). It's a practical guide (what your gateway must do, a minimal working gateway, a test checklist, troubleshooting). This file is the wire-format reference.

Status: **proposed by the visualizer side, needs John's sign-off.** `NetworkClient.cs` and `tools/mock_gateway.py` implement exactly this. If the gateway uses different names, change `GatewayMessages.cs`. Nothing else in the phone app depends on the wire format.

## Transport
- The phone is the **client**. The gateway (laptop) is the **server**.
- The URL is `wss://<gateway-ip>:<port>/`. The mock gateway uses port `8765`.
- TLS: the gateway uses a self-signed certificate. The phone **pins the SHA-256 fingerprint of the certificate's DER bytes** (the same value `openssl x509 -noout -fingerprint -sha256` prints, without the colons). If the fingerprint doesn't match, the phone refuses to connect.
- Every message is a **single UTF-8 JSON text frame**. Every message has `"type"` and `"protocol_version": 1`.
- Receivers **ignore unknown fields**. Both sides can therefore add fields without breaking the other.

## Messages

### Phone → gateway: `VISUALIZER_READY`
Sent immediately after every successful connection, including reconnects.
```json
{"type":"VISUALIZER_READY","protocol_version":1,"app_version":"0.1"}
```

### Gateway → phone: `GAME_EVENT`
One classified action.
```json
{"type":"GAME_EVENT","protocol_version":1,"session_id":"s-20260924-1",
 "event_id":"e-000042","window_id":1337,"action":"stomp","confidence":0.93,
 "timestamp":1727170000.123}
```
| field | type | notes |
|---|---|---|
| `session_id` | string | Owned by the gateway. The phone treats `(session_id, event_id)` as the unique key, so the gateway can restart numbering in a new session. |
| `event_id` | string | Unique within a session. |
| `window_id` | int | Classifier window. The phone only logs it. |
| `action` | string | One of `stand` `walk` `jump` `squat` `fireball` `stomp` |
| `confidence` | float 0–1 | The phone rejects events below its threshold (currently 0.6). |
| `timestamp` | float, Unix seconds | Gateway clock. Used only for logging, since the clocks aren't synchronised. |

### Phone → gateway: `EVENT_ACK`
Sent for **every** `GAME_EVENT` received, including duplicates. The gateway can stop retransmitting once the ACK arrives.
```json
{"type":"EVENT_ACK","protocol_version":1,"session_id":"s-20260924-1",
 "event_id":"e-000042","outcome":"rejected","gate":4,"reason":"no target in range/facing",
 "processing_ms":0.4}
```
`outcome` is `resolved` or `rejected`. `gate` is 1–5 on a rejection and 6 on success. The gates are:
1. session not ready (scanning, relocalising, stage transition)
2. invalid (low confidence or duplicate)
3. not permitted (for example fireball without a fire flower)
4. no target in range or facing
5. target not actionable
6. resolved

### Phone → gateway: `HAPTIC_CMD`
The phone decides on and **sends** haptics. The gateway forwards them to the wearable over BLE.
```json
{"type":"HAPTIC_CMD","protocol_version":1,"session_id":"s-20260924-1",
 "event_id":"e-000042","target":"ankle","pattern":"success"}
```
| field | values |
|---|---|
| `target` | `wrist`, `ankle`, `both`. Fireball → wrist, stomp → ankle, everything else → both. |
| `pattern` | `success` (a hit, or a fireball impact), `denied` (rejected at gates 3–5 for a *discrete* action: jump, squat or fireball), `damage` (the player lost a life; `target` is `both`, and `session_id`/`event_id` are empty because no single event caused it). None for gates 1–2, for state-only actions (stand, walk, squat, a jump that hit nothing), or for "denied" on continuous actions (stand, walk, stomp are re-sent every window). See `GAME_DESIGN.md`. |

How each pattern maps to motor pulses is up to the wearable firmware (Neo/John).

## Connection behaviour (phone)
- The phone reconnects with exponential backoff: 0.5 s, doubling each time, capped at 8 s, with ±20 % random variation. Every successful reconnect is followed by a fresh `VISUALIZER_READY`.
- Only one connection exists at a time. The phone never connects to the Ultra96 directly.

## Development only: `DEV_TUNE` (mock gateway → phone)
Not part of the protocol with the real gateway, which never sends it; the phone ignores it unless it arrives. The mock gateway's `--keys` mode sends it so headset settings can be tuned live while wearing the phone:

```json
{ "type": "DEV_TUNE", "param": "eyeImageScale", "delta": 0.05 }
```

`param` is `eyeImageScale` (the eye window's size), `lensSeparation` (the gap between the eye images) or `reset`. The phone applies it (`HeadsetView.Tune`), remembers it, and shows the new values on its HUD. There's no ACK.
