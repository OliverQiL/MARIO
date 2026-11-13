# MARIO: AR visualizer

The AR visualizer of CG4002 Group B15: an iPhone app (Unity 6, AR Foundation and ARKit, URP) that turns the room into a Mario-style level. The wearables' actions arrive from the laptop gateway as `GAME_EVENT`s over a secure WebSocket. The phone resolves each one against the game and answers with an `EVENT_ACK`, plus a `HAPTIC_CMD` when the wearables should buzz.

## Docs

| File | What it covers |
|---|---|
| [`Assets/VISUALIZER_STATUS.md`](Assets/VISUALIZER_STATUS.md) | Status and handoff: architecture, every script, setup, history. Start here. |
| [`Docs/GAME_DESIGN.md`](Docs/GAME_DESIGN.md) | The game: actions, gates, stages, enemies, training, menu. The last sections are the most recent. |
| [`Docs/JOURNAL.md`](Docs/JOURNAL.md) | A problem → choice log of every change since the comms integration. |
| [`Docs/DEMO_GUIDE.md`](Docs/DEMO_GUIDE.md) | How to run the demo, mapped to the rubric. |
| [`Docs/COMMS_INTEGRATION.md`](Docs/COMMS_INTEGRATION.md), [`Docs/GATEWAY_PROTOCOL.md`](Docs/GATEWAY_PROTOCOL.md) | For the gateway: a practical guide, and the wire format. |

## Layout

- `Assets/Scripts/`: runtime code. `EventResolver` (the gate chain) and `GameStateManager` (all state) are the core; gameplay elements are in `Gameplay/`.
- `Assets/Editor/`: generators, tests and builds.
  - `GameContentSetup` builds every prefab and stage from code (with `StageDesigns`, `PixelArt`); `HUDSetup` builds the HUD.
  - Tests: `GameplaySmokeTest`, `SimulationSmokeTest`, `NetworkSmokeTest`. `ArtPreview` renders PNGs for checking art.
  - `BuildScript` makes the iOS builds.
- `tools/`:
  - `mock_gateway.py`: a stand-in gateway, interactive or scripted.
  - `push_to_integration.sh`: syncs this repo into the team repo's `visualizer/`.
  - `fetch_sim_environments.sh`: fetches the XR Simulation rooms.

## Build and test (headless, with Unity closed)

```sh
U=/Applications/Unity/Hub/Editor/6000.6.0f1/Unity.app/Contents/MacOS/Unity
$U -batchmode -nographics -projectPath . -executeMethod GameContentSetup.Run -quit   # regenerate prefabs and stages
$U -batchmode -nographics -projectPath . -executeMethod HUDSetup.Run -quit
$U -batchmode -nographics -projectPath . -executeMethod GameplaySmokeTest.Run -quit  # gameplay, ~260 checks
$U -batchmode -projectPath . -executeMethod SimulationSmokeTest.Run -scan           # scan → training → 1-1 in XR Simulation
$U -batchmode -nographics -projectPath . -executeMethod BuildScript.BuildIOSJohn -quit  # → ios-build-john/, open in Xcode
```

- **Network test:** start the mock gateway first (with a Python that has `websockets`), e.g. `python3 tools/mock_gateway.py --port 8766 --script "stomp,rep,rep,stomp,dup,punch,low:jump,squat,rep,idle" --exit-after-acks`. Then run `NetworkSmokeTest.Run -gatewayUrl wss://127.0.0.1:8766/ -pin <fingerprint> -expect 6,2,2,5,2,4,2,6,2,6`.
- **Builds:** `BuildIOSJohn` targets John's gateway (app "MARIO"). `BuildIOSLocal` targets the mock gateway on this Mac (app "MARIO Local").
