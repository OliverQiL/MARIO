# Comms ⇄ Visualizer Integration Guide

**For:** the laptop gateway (comms). **From:** the AR visualizer (iPhone app).
**In one line:** the phone opens a secure WebSocket to your gateway, you send it `GAME_EVENT`s, and it answers every one with an `EVENT_ACK` and sometimes a `HAPTIC_CMD` for you to forward to the wearables.

**Status (2026-10-22): integrated and working.** John's gateway (`integrated/laptop/visualizer_gateway.py` in the team repo, the comms bridge's visualizer mode, started by `integrated/run_integration.sh`) implements everything here, and the full chain (boards → Ultra96 → gateway → phone → haptics) has been playtested. The phone app **MARIO** is built for it (`BuildScript.BuildIOSJohn`); **MARIO Local** is built for Oliver's mock gateway. This guide stays as the reference for what the gateway must keep doing.

The wire format reference is [`GATEWAY_PROTOCOL.md`](GATEWAY_PROTOCOL.md). This guide is the practical companion: what your gateway must do, the traps, and how to test. A working reference gateway is [`tools/mock_gateway.py`](../tools/mock_gateway.py); copy from it freely.

---

## 1. The big picture

```
 Wearables ──BLE──► Laptop gateway (you) ──TLS──► Ultra96 (AI) ──► back to you
                         │  ▲
         GAME_EVENT JSON │  │ EVENT_ACK + HAPTIC_CMD JSON
                         ▼  │
                    iPhone visualizer (me)          wss://192.168.137.1:8765/ (laptop hotspot)
```

| | Gateway (you) | Phone (me) |
|---|---|---|
| Role | **WebSocket server** (TLS) | **client**: connects to you and reconnects by itself |
| Sends | `GAME_EVENT`: one classified action | `VISUALIZER_READY`, `EVENT_ACK`, `HAPTIC_CMD` |
| Decides | which action happened (from the AI) | whether it counts in the game, what happens, which haptic to play |

The phone never talks to the Ultra96 or the wearables directly. **Everything goes through your gateway.**

---

## 2. What your gateway must provide (connection)

| Requirement | Value / detail |
|---|---|
| Protocol | **WebSocket over TLS** (`wss://`), path `/`, one connection (the phone) |
| Port | **8765**. Any port works, but it's built into the app, so tell me if you change it. |
| Address | A fixed address the phone can reach. **In use:** the Windows Mobile Hotspot address `wss://192.168.137.1:8765/` (the campus Wi-Fi blocks device-to-device traffic). An mDNS name (`<host>.local`) also works where it resolves. |
| TLS version | **TLS 1.2 must be allowed.** The phone offers TLS 1.2 only. Don't set the server's minimum to TLS 1.3. |
| Certificate | Self-signed is fine. The phone **pins** it: it checks the certificate's SHA-256 fingerprint and ignores the CA chain and hostname. **Generate it once and keep it.** A new certificate = a new fingerprint = I must rebuild the app. |
| Network | The phone joins the **laptop's hotspot**. Allow incoming TCP on the port in the laptop firewall (Windows asks on the first run). John's gateway accepts only phones on the hotspot network (`192.168.137.0/24`; `VISUALIZER_ALLOW` changes it) and refuses connections with an `Origin` header (browsers); the app sends none. |

**Make the certificate once, then send me the fingerprint:**
```bash
openssl req -x509 -newkey rsa:2048 -nodes -days 365 \
  -keyout gateway.key -out gateway.crt -subj "/CN=cg4002-gateway"
openssl x509 -in gateway.crt -noout -fingerprint -sha256
# SHA256 Fingerprint=0F:BB:15:...   ← send me this line (colons are fine)
```

**In use:** John's certificate is `comms/security/server.pem` (made by `comms/laptop/setup_phone_tls.py --host 192.168.137.1`, see `integrated/SECURITY.md`). Its fingerprint, pinned in the MARIO build (`BuildScript.cs`, `John.Pin`), is `9A1017B8EAEE4D880A6029C0AA2303A37F1881CA0A7B4A81DEDD91FDBCA25027`; his gateway prints it at startup (`VISUALIZER GATEWAY: certificate SHA-256 ...`). Only the fingerprint is checked, not the 90-day expiry. **If the certificate is ever regenerated**, send me the new fingerprint and I rebuild. (Its private key `server-key.pem` was pushed to the team repo on 2026-10-18; anyone with repo access could pose as the laptop, so regenerating it is worth considering.)

---

## 3. Messages

Every message is **one JSON object in one WebSocket *text* frame**, UTF-8.
- Every message has `"type"` and `"protocol_version": 2`.
- Unknown fields are ignored on both sides, so you may add your own (e.g. debugging info).
- Never batch several messages into one frame or a JSON array. **Binary frames are ignored.**

### 3.1 Phone → you: `VISUALIZER_READY`
Sent right after every connect, including reconnects.
```json
{"type":"VISUALIZER_READY","protocol_version":2,"app_version":"0.1.0"}
```
Use it to know the phone is live. Sending events before it arrives is harmless, but nothing will be listening to them.

### 3.2 You → phone: `GAME_EVENT`
```json
{"type":"GAME_EVENT","protocol_version":2,"session_id":"s-1727170000",
 "event_id":"e-000042","window_id":1337,"action":"stomp","confidence":0.93,
 "timestamp":1727170000.123}
```

| Field | Type | Rules |
|---|---|---|
| `session_id` | string | Pick a new one each time your gateway starts (e.g. `s-<unix time>`). |
| `event_id` | string | **Unique within the session.** The phone de-duplicates on `(session_id, event_id)`, remembering the last 1024. |
| `window_id` | integer | Your classifier window number. Logged only. |
| `action` | string | Exactly one of `jump` `stomp` `squat` `throw` `punch` `clap`, or `idle`: **lowercase, no spaces**. Anything else is rejected (gate 2), including v1's `stand`, `walk` and `fireball`. |
| `confidence` | number 0–1 | Below **0.6** the phone rejects it as "not recognised". |
| `timestamp` | number | Unix seconds (float) on *your* clock. Logged only; the clocks aren't synced. |

`type`, `event_id` and `action` are required. Without them the phone can't treat it as an event, so it drops the message **without an ACK**. A missing `confidence` counts as 0, so the event is rejected.

### 3.3 Phone → you: `EVENT_ACK` (one per `GAME_EVENT`, always)
```json
{"type":"EVENT_ACK","protocol_version":2,"session_id":"s-1727170000",
 "event_id":"e-000042","outcome":"resolved","gate":6,"reason":"resolved","processing_ms":0.31}
```
- `outcome`: `resolved` or `rejected`.
- `gate` says *why*:

| `gate` | Meaning | Example `reason` |
|---|---|---|
| 1 | Game not ready: scanning, stage changing, tracking lost, game over, or game clear | `session not ready`, `game over`, `game clear` |
| 2 | Invalid: low confidence, a duplicate `event_id`, or another report of a motion already processed (§4.1) | `confidence 0.42 < 0.60`, `duplicate event_id 'e-000042'`, `repeat of the same motion` |
| 3 | Not allowed right now | `fireballs recharging`, `nothing to throw`, `hands full` |
| 4 | No target in range or facing | `no target in range/facing` |
| 5 | Target already used | `target not actionable` |
| 6 | **Resolved**: the action did something (or `idle`, acknowledged and ignored) | `resolved`, `airborne`, `ducked`, `projectile` (fireball), `shell` (thrown shell), `goal` (a jump at the flag or axe), `restart` (a jump after game over), `idle` |

- `processing_ms` is the phone's own processing time, usually under 1 ms. Your *send → ACK* time is the network round trip; please log it.
- **Duplicates are ACKed too** (gate 2), so you can safely resend an event that wasn't ACKed with the **same `event_id`**. See §4.4.

### 3.4 Phone → you: `HAPTIC_CMD` (forward to the wearable over BLE)
```json
{"type":"HAPTIC_CMD","protocol_version":2,"session_id":"s-1727170000",
 "event_id":"e-000042","target":"ankle","pattern":"success"}
```

| `target` | Strap |
|---|---|
| `wrist` | arm strap (throw / punch / clap) |
| `ankle` | leg strap (stomp) |
| `both` | both straps (everything else, and damage) |

| `pattern` | When |
|---|---|
| `success` | an action hit something, or a thrown fireball or shell hit something (it arrives *later*, once per thing the projectile hits: a shell can hit several) |
| `denied` | an action was recognised but not allowed (gates 3–5) |
| `damage` | the player was hit (in the training round too, where no life is lost). `target` is `both`, and `session_id`/`event_id` are **empty strings**, because no single event caused it. |

The haptic for a jump or squat the phone detected itself (§4.1) carries `session_id` `phone-motion` and an `event_id` you never sent; forward it like any other. John's firmware plays `success` 1 × 150 ms, `denied` 2 × 100 ms, `damage` 1 × 600 ms on the wrist board (the thigh board has no motor).

What each pattern feels like is up to the wearable firmware.

---

## 4. Behaviour your gateway needs (the important part)

### 4.1 Actions, and one event per motion (protocol v2)
| Action | Motion | What it does in the game |
|---|---|---|
| `jump` | jump | hits a ? block overhead; grabs the flag (or the axe) when you're at it; also starts the scan, confirms it, and restarts after game over / game clear |
| `stomp` | stomp a foot | defeats an enemy at your feet (a Koopa takes two stomps) |
| `squat` | squat | ducks Bullet Bills and Bowser's fire; enters a warp pipe you're standing on |
| `throw` | throwing motion | throws the shell you're holding; otherwise a fireball if you have the fire flower |
| `punch` | forward punch | breaks a brick (or hits Bowser) in front of you at chest height |
| `clap` | clap | picks up a Koopa shell from the floor in front of you |
| `idle` | any other movement (the classifier's catch-all class) | nothing: the phone ACKs it as resolved (gate 6, reason `idle`) in every state and ignores it. Sending it is optional (John's gateway currently skips `IDLE`); it never starts or extends a repeat run |

**Repeats are filtered on the phone (since 2026-10-21).** The gateway may forward every classification: if the classifier labels 5 consecutive windows "stomp", the phone processes the first and ACKs the other four as rejected at gate 2, reason `repeat of the same motion` (no haptic, no HUD, not counted). The rule, per action and across both boards: a report within **0.5 s** of the previous report of that action is the same motion; a run of reports lasting over **1.2 s** counts as a new motion, so two stomps back to back still register twice. `idle` and low-confidence reports don't start or extend a run. De-duplicating on the gateway as well is fine but no longer needed. The phone also detects **jumps and squats itself** from its head-height tracking; a classifier `jump`/`squat` within 1.5 s of one the phone saw (or during a held squat) is ACKed as a repeat too, so keep sending them — they still count when the phone misses one.

### 4.2 Send jumps even before the game starts
The phone has no touch input during play, so a **jump controls the setup**:
- the 1st jump starts the room scan
- the 2nd jump finishes it; the **training round** (WORLD 0-1) follows, then 1-1
- after a game over, a jump (2 s later) restarts (straight to 1-1)

So forward classified jumps at all times. They come back as gate 1 ACKs while the game isn't running, which is expected. (The phone also detects jumps itself, §4.1, so the setup works even when the classifier misses one.) A **hard reset** back to the scan is done on the phone: look straight up for 5 s; nothing is needed from the gateway.

### 4.3 Latency
- The system budget is about **275 ms** from the end of a movement to the visual result. The phone uses under 1 ms of that.
- **Send each event as soon as it's classified**, one per frame, with no batching or buffering.
- Disable Nagle if your stack allows (`TCP_NODELAY`). The phone already does on its side.
- We measured 100–185 ms round trips on a home router. On John's hotspot, an idle iPhone's Wi-Fi radio dozing made about half the events take 250–930 ms; his gateway now pings the phone every 100 ms to keep it awake (the app answers pings by itself), and push mode estimates 150–230 ms from the end of a motion to the phone (`integrated/README.md`).

### 4.4 Disconnects and retries
- If the phone drops (Wi-Fi blip, app paused), it **reconnects by itself**: after 0.5 s, then 1, 2, 4, 8 s (±20 %). Then it sends `VISUALIZER_READY` again.
- Your server must **accept a new connection** after the old one closes, and should treat the newest connection as *the* phone.
- **Don't replay old events after a reconnect.** A jump from 3 seconds ago is wrong in a live game. Drop anything older than about 0.5 s.
- If an event got no ACK within ~200 ms while still connected, you *may* resend it once with the **same `event_id`**. The phone de-duplicates it.

### 4.5 Things not to send
- `DEV_TUNE`: that's my mock gateway's development-only message.
- Arrays, binary frames, or several JSON objects in one frame.
- Actions in other spellings (`Jump`, `fire_ball`).

---

## 5. A minimal gateway (Python, the same stack as the mock)

`pip install websockets`. This is the whole server side of the protocol. Replace `next_action()` with your classifier pipeline.

```python
import asyncio, json, ssl, time, websockets

CERT, KEY, PORT = "gateway.crt", "gateway.key", 8765
SESSION = f"s-{int(time.time())}"
phone = None            # the current phone connection (newest wins)
next_id = 1

async def handle_phone(ws):
    global phone
    phone = ws
    try:
        async for raw in ws:                                  # messages FROM the phone
            msg = json.loads(raw)
            if msg["type"] == "VISUALIZER_READY":
                print("phone ready", msg.get("app_version"))
            elif msg["type"] == "EVENT_ACK":
                print("ack", msg["event_id"], msg["outcome"], "gate", msg["gate"], msg["reason"])
            elif msg["type"] == "HAPTIC_CMD":
                print("haptic", msg["target"], msg["pattern"])  # → forward to the wearable over BLE
    finally:
        if phone is ws:
            phone = None

async def send_action(action, confidence, window_id):
    """Call this for each classified event (the phone keeps one per physical motion — see §4.1)."""
    global next_id
    if phone is None:
        return                                                # not connected: drop it, don't queue
    evt = {"type": "GAME_EVENT", "protocol_version": 2, "session_id": SESSION,
           "event_id": f"e-{next_id:06d}", "window_id": window_id,
           "action": action, "confidence": round(float(confidence), 3), "timestamp": time.time()}
    next_id += 1
    await phone.send(json.dumps(evt))                         # one JSON object per text frame

async def main():
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2              # the phone needs TLS 1.2 available
    ctx.load_cert_chain(CERT, KEY)
    async with websockets.serve(handle_phone, None, PORT, ssl=ctx):   # None = all interfaces
        print(f"gateway on wss://<this-laptop>.local:{PORT}/  session {SESSION}")
        window = 0
        while True:                                           # demo: replace with your pipeline
            await asyncio.sleep(1.0)
            window += 1
            await send_action("jump", 0.95, window)

asyncio.run(main())
```

---

## 6. Testing, step by step

1. **See the phone side work first** with my reference gateway: `python3 tools/mock_gateway.py --keys`, then press `1`–`6`. Every line it prints is a message described above, so it doubles as a live example of the protocol. `--dummy --every 2` sends random events; `--script "stomp,rep,..."` scripted ones (`rep` = another report of the same motion).
2. **Without the phone:** John's `integrated/laptop/fake_phone.py` stands in for the app (TLS 1.2, pinned certificate, gate 2 checks, haptic keys).
3. **With the phone:** install the **MARIO** build (pointed at the hotspot gateway), join the laptop's hotspot and open it. In the Unity Editor you can instead set `GameManager ▸ NetworkClient ▸ Gateway Url` and `Pinned Cert Sha256` and press Play; keys `1`–`6` still work there too.
4. **Checklist:**
   - [ ] The phone's top-right shows **● ONLINE**; you receive `VISUALIZER_READY`.
   - [ ] A `jump` → ACK gate 1 while not scanning yet (expected), and the phone starts scanning.
   - [ ] Every `GAME_EVENT` gets exactly one `EVENT_ACK` with the same `event_id`.
   - [ ] Re-sending an `event_id` → ACK gate 2 `duplicate`.
   - [ ] `confidence` 0.3 → ACK gate 2.
   - [ ] One physical stomp → one *resolved* `stomp` on the phone; its other reports come back as gate 2 `repeat of the same motion` (§4.1).
   - [ ] All labels arrive spelled exactly `jump` `stomp` `squat` `throw` `punch` `clap` `idle`.
   - [ ] The phone's bottom-right **LAST ACTION** shows each class it receives (with `(PHONE)` for jumps and squats it saw itself).
   - [ ] A Goomba hits the player → `HAPTIC_CMD target=both pattern=damage` (empty IDs) reaches the wearable.
   - [ ] Kill the gateway and restart it → the phone reconnects within ~8 s without a restart.
   - [ ] Send → ACK round trip logged; under ~100 ms on the lab Wi-Fi.

## 7. Troubleshooting

| Symptom (phone HUD / Xcode log) | Likely cause |
|---|---|
| Stays **CONNECTING**, then "connection refused" | Gateway not running, wrong port, or the laptop firewall is blocking it |
| Times out, and the first launch showed a permission prompt | iOS **Local Network** permission: tap Allow (Settings ▸ Privacy ▸ Local Network ▸ MARIO) |
| Refused at once by John's gateway ("not on an allowed network") | The phone isn't on the laptop's hotspot (`192.168.137.0/24`) |
| "certificate … pinned" mismatch in the log | The certificate changed. Send me the new fingerprint (the log prints the one your gateway presented). |
| TLS handshake fails immediately | The server allows TLS 1.3 only. Allow 1.2 (§2). |
| Connects, but **no ACK comes back** | The message wasn't a valid `GAME_EVENT`: wrong `type`, missing `event_id` or `action`, not JSON, or a binary frame. The phone logs and drops these without an ACK. |
| ACK arrives but nothing happens | Check the ACK's `gate` and `reason`: e.g. gate 2 `unknown action 'Jump'` (spelling), gate 2 low confidence, gate 1 (game not running yet). |
| Two separate moves count as one | They were under 0.5 s apart (or the classifier's report came within 1.5 s of one the phone saw), so they're treated as one motion (§4.1); pause briefly between them |
| A motion seems to count twice | Check the phone's LAST ACTION and the ACK reasons: a second motion more than 1.2 s into a run counts as new by design |
| Can't resolve `xxx.local` | Not on the same network, or mDNS blocked. Use a fixed IP. |

**Contact:** ping me (Oliver) with the ACK lines from your log. The `gate` and `reason` usually say exactly what's wrong.
