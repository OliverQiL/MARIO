# Visualizer ⇄ Gateway WebSocket Protocol (v2)

> **Comms integrators:** start with [`COMMS_INTEGRATION.md`](COMMS_INTEGRATION.md). It's a practical guide (what your gateway must do, a minimal working gateway, a test checklist, troubleshooting). This file is the wire-format reference.

Status (2026-10-22): **in use.** `NetworkClient.cs` (phone), `tools/mock_gateway.py` (Oliver's mock) and John's gateway (`integrated/laptop/visualizer_gateway.py`, visualizer mode of the comms bridge) all implement it; the full integration (boards → Ultra96 → gateway → phone) has been tested end to end. If a name ever changes, change `GatewayMessages.cs`; nothing else in the phone app depends on the wire format.

## Transport
- The phone is the **client**. The gateway (laptop) is the **server**.
- The URL is `wss://<gateway-ip>:<port>/`, port `8765`. The two phone builds (`BuildScript`): **MARIO** connects to John's laptop on its Windows hotspot, `wss://192.168.137.1:8765/`; **MARIO Local** connects to Oliver's Mac running the mock gateway, `wss://Olis-MacBook-Pro.local:8765/`. John's gateway only accepts phones on the hotspot (`192.168.137.0/24`; `VISUALIZER_ALLOW` changes it) and refuses connections that send an `Origin` header (browsers); the app sends none.
- TLS 1.2: the gateway's certificate needn't be trusted by iOS. The phone **pins the SHA-256 fingerprint of the certificate's DER bytes** (what `openssl x509 -noout -fingerprint -sha256` prints; the whole line can be pasted, colons and all). It checks only the fingerprint, not the expiry date or hostname. If the fingerprint doesn't match, the phone refuses to connect. A new certificate needs a new app build with its pin (`BuildScript.cs`, `John.Pin`).
- Every message is a **single UTF-8 JSON text frame**. Every message has `"type"` and `"protocol_version": 2`.
- **Version 2** (v2 action set): the labels are `jump` `stomp` `squat` `throw` `punch` `clap`, plus `idle` (the classifier's catch-all). Version 1's `stand`, `walk` and `fireball` are gone (the phone rejects them at gate 2 as unknown). A message with another `protocol_version` is still processed; the phone only logs a warning.
- Receivers **ignore unknown fields**. Both sides can therefore add fields without breaking the other.

## Messages

### Phone → gateway: `VISUALIZER_READY`
Sent immediately after every successful connection, including reconnects.
```json
{"type":"VISUALIZER_READY","protocol_version":2,"app_version":"0.1"}
```

### Gateway → phone: `GAME_EVENT`
One classified action.
```json
{"type":"GAME_EVENT","protocol_version":2,"session_id":"s-20260924-1",
 "event_id":"e-000042","window_id":1337,"action":"stomp","confidence":0.93,
 "timestamp":1727170000.123}
```
| field | type | notes |
|---|---|---|
| `session_id` | string | Owned by the gateway. The phone treats `(session_id, event_id)` as the unique key, so the gateway can restart numbering in a new session. |
| `event_id` | string | Unique within a session. |
| `window_id` | int | Classifier window. The phone only logs it. |
| `action` | string | One of `jump` `stomp` `squat` `throw` `punch` `clap` (v2). The gateway may forward **every** classification: the phone processes only the first event of a run of the same action and ACKs the rest as gate 2, reason `repeat of the same motion` (a report within 0.5 s of the previous one of that action; a run longer than 1.2 s counts as a new motion). Also `idle`, the classifier's class for any other movement: the phone ACKs it as resolved (gate 6, reason `idle`) in every state and otherwise ignores it (no haptic, no HUD, not logged, not counted). |
| `confidence` | float 0–1 | The phone rejects events below its threshold (currently 0.6). |
| `timestamp` | float, Unix seconds | Gateway clock. Used only for logging, since the clocks aren't synchronised. |

### Phone → gateway: `EVENT_ACK`
Sent for **every** `GAME_EVENT` received, including duplicates. The gateway can stop retransmitting once the ACK arrives.
```json
{"type":"EVENT_ACK","protocol_version":2,"session_id":"s-20260924-1",
 "event_id":"e-000042","outcome":"rejected","gate":4,"reason":"no target in range/facing",
 "processing_ms":0.4}
```
`idle` is always `resolved`, gate 6, reason `idle`. Repeats are `rejected`, gate 2, reason `repeat of the same motion`. `outcome` is `resolved` or `rejected`. `gate` is 1–5 on a rejection and 6 on success. The gates are:
1. session not ready (before or during the scan, tracking lost, stage transition, game over / game clear). A `jump` here still drives the scan (start, confirm) and restarts after game over.
2. invalid (low confidence, duplicate `event_id`, or a repeat of the same motion — including a jump or squat the phone already detected itself from its tracking within 1.5 s, see `GAME_DESIGN.md`)
3. not permitted (for example `throw` with nothing to throw: no shell held and no fire flower)
4. no target in range or facing
5. target not actionable
6. resolved

### Phone → gateway: `HAPTIC_CMD`
The phone decides on and **sends** haptics. The gateway forwards them to the wearable over BLE.
```json
{"type":"HAPTIC_CMD","protocol_version":2,"session_id":"s-20260924-1",
 "event_id":"e-000042","target":"ankle","pattern":"success"}
```
| field | values |
|---|---|
| `target` | `wrist`, `ankle`, `both`. throw / punch / clap → wrist, stomp → ankle, everything else → both. |
| `pattern` | `success` (a hit, or each impact of a thrown fireball or shell), `denied` (rejected at gates 3–5), `damage` (the player was hit; `target` is `both`, and `session_id`/`event_id` are empty because no single event caused it; also sent in the training round, where no life is lost). None for gates 1–2 or for state-only actions (a plain squat, a jump that hit nothing, `idle`). See `GAME_DESIGN.md`. |

Jumps and squats the phone detects itself from its head-height tracking (`PhoneMotionDetector`) aren't sent to the gateway and get no ACK; their haptics (`session_id` `phone-motion`) are sent like any other.

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
