# MARIO: AR visualizer

An iPhone app that turns a real room into a first-person, Mario-style platformer. The player wears the phone in a head-mounted viewer and plays with their body: jump under ? blocks, stomp Goombas, clap up a Koopa shell and throw it, squat under Bullet Bills, and beat Bowser.

It is the visualizer of **CG4002 Group B15** (NUS, 2026). In the full system, wearable motion sensors are classified by a neural network on an Ultra96 FPGA, and a laptop gateway sends each action to the phone. The phone decides what the action does in the game, renders it in AR and sends back haptic feedback. It also runs on its own, with keyboard input in the Unity Editor or with the bundled mock gateway.

**Built with** Unity 6 (6000.6.0f1), the Universal Render Pipeline, AR Foundation 6 and ARKit.

## Features

- **Markerless and room-fitted.** The player scans the floor; each stage is fitted to the scanned space (100–55 % of its size) and anchored where they stand.
- **Eight stages and a boss**, 1-1 to Bowser's castle, plus a training round that teaches the seven moves.
- **Touch-free.** Every control is a body action or a head gesture: nod and clap in the menu, jump to confirm, look up for 5 s to reset.
- **A six-gate event pipeline** that tells "not recognised" from "recognised but not allowed", with HUD, sound and haptic feedback for each.
- **Built for an imperfect classifier.** One event per motion, jumps and squats also detected from the phone's head height, and arm moves resolved to the one that fits.
- **Headset view** with lens pre-distortion, guidance arrows to enemies and objectives, and a see-through fade for safety.
- **All content generated from code:** prefabs, stages, pixel art, HUD and synthesised sound effects.

## Requirements

| For | You need |
|---|---|
| Playing in the Editor | macOS (or Windows) with **Unity 6000.6.0f1** and iOS Build Support |
| Running on a phone | A Mac with Xcode, an Apple developer team (a free personal team works; apps expire after 7 days), and an ARKit-capable iPhone. A **LiDAR** iPhone (Pro models) adds depth occlusion and furniture-aware placement. |
| The headset | Any phone VR viewer with two lenses; developed with a VRG Pro |
| The mock gateway and network test | Python 3 with `websockets` (`pip install websockets`) and `openssl` on the PATH |

## Quick start: play in the Editor

1. **Get the simulated rooms** (once per machine, with Unity closed): `tools/fetch_sim_environments.sh`. It downloads Unity's XR Simulation environments (~40 MB, gitignored). Set `UNITY=<path to the Unity binary>` if Unity isn't in the default macOS location.
2. Open the project in Unity Hub and open `Assets/Scenes/SampleScene.unity`.
3. Press **Play**. XR Simulation replaces the camera with a virtual room.

| Key | Does |
|---|---|
| Right-click drag, WASD | Look and move (planes are found as you look round) |
| **1** jump · **2** stomp · **3** squat · **4** throw · **5** punch · **6** clap | The classified actions, straight into the game |
| ↑ / ↓ | The start menu's head nods |
| **R** | Hard reset |

In the start menu pick with **6**, walk round to scan the floor, press **1** to confirm, then play.

## Playing against a gateway

The phone connects to a gateway over a secure WebSocket (`wss://`, port 8765) and pins the gateway certificate's SHA-256 fingerprint. To develop without the rest of the system, use the mock gateway:

```sh
python3 tools/mock_gateway.py --print-fingerprint   # creates tools/certs/ on first run; note the fingerprint
python3 tools/mock_gateway.py --keys                # 1-6 send actions; d duplicate, l low confidence, k kick, q quit
```

Point the app at it with **Tools ▸ MARIO ▸ Configure Network**, or headless with `NetworkSetup.Run -gatewayUrl wss://<this-mac>:8765/ -pin <fingerprint>`. The Editor connects too, so you can test the network path without a phone. `--script`, `--auto` and `--dummy` modes are described at the top of `tools/mock_gateway.py`.

The wire format is in [`Docs/GATEWAY_PROTOCOL.md`](Docs/GATEWAY_PROTOCOL.md), and what a gateway must do is in [`Docs/COMMS_INTEGRATION.md`](Docs/COMMS_INTEGRATION.md).

## Building for an iPhone

1. **Signing:** set your Apple team in `Assets/Editor/LidarSetup.cs` (`AppleTeamId`) and run **Tools ▸ MARIO ▸ Configure LiDAR**, or set the team in Xcode after exporting.
2. **Export the Xcode project** with **Tools ▸ MARIO ▸ Build iOS (...)** or headless (Unity closed):

   ```sh
   U=/Applications/Unity/Hub/Editor/6000.6.0f1/Unity.app/Contents/MacOS/Unity
   $U -batchmode -quit -projectPath . -buildTarget iOS -executeMethod BuildScript.BuildIOSLocalRelease \
      -gatewayUrl wss://<gateway-host>:8765/ -pin <fingerprint>
   ```

   Two profiles exist so both apps can be installed side by side: **Local** ("MARIO Local", the mock gateway) and **John** ("MARIO", the team's laptop gateway on its Windows hotspot, `192.168.137.1`). `-gatewayUrl` and `-pin` override either profile; edit `Assets/Editor/BuildScript.cs` to change the defaults.
3. Open `ios-build-local/Unity-iPhone.xcodeproj` (or `ios-build-john/`) in Xcode and run it on the phone.
4. **In the headset:** the app opens in headset mode on a device. A two-finger tap toggles it before the phone goes into the viewer. The phone must be on the same network as the gateway.

## Tests

Run headless with Unity closed (`$U` as above). Tests run without `-quit`; they exit themselves.

```sh
$U -batchmode -nographics -projectPath . -executeMethod GameplaySmokeTest.Run      # ~260 gameplay checks
$U -batchmode -projectPath . -executeMethod SimulationSmokeTest.Run -scan          # scan, training, 1-1 in XR Simulation
$U -batchmode -projectPath . -executeMethod SimulationSmokeTest.Run -scan -reset   # plus the look-up reset and a rescan
```

Network test, against the mock gateway on a spare port:

```sh
python3 tools/mock_gateway.py --port 8766 --script "stomp,rep,rep,stomp,dup,punch,low:jump,squat,rep,idle" --exit-after-acks --timeout 300 &
$U -batchmode -nographics -projectPath . -executeMethod NetworkSmokeTest.Run \
   -gatewayUrl wss://127.0.0.1:8766/ -pin <fingerprint> -expect 6,2,2,5,2,4,2,6,2,6
```

`ArtPreview.Run -out <dir>` renders every prefab, stage and the HUD to PNGs, for checking art without a device.

Headless runs rewrite the dynamic TMP font asset; revert it before committing: `git checkout -- "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset"`.

## Project layout

```
Assets/
  Scenes/SampleScene.unity   the only scene
  Scripts/                   runtime code: network, gate chain, game state, AR, HUD, audio, headset view
    Gameplay/                stage elements, enemies, projectiles, player body
  Editor/                    content generators, setup tools (Tools ▸ MARIO), tests, ArtPreview, BuildScript
  Stages/ Prefabs/ Materials/ Textures/   generated by GameContentSetup: don't edit by hand
  Shaders/HeadsetLens.shader the headset view's lens pre-distortion
  Fonts/                     Press Start 2P (SIL Open Font Licence, included)
  Audio/                     background music (see Licensing)
Docs/                        architecture, game design, protocol, development log
tools/                       mock gateway, XR Simulation fetcher, team-repo sync script
```

## Documentation

| File | What it covers |
|---|---|
| [`Docs/ARCHITECTURE.md`](Docs/ARCHITECTURE.md) | **Start here.** Modules, the gate chain, a frame, a session, and how to extend it. |
| [`Docs/GAME_DESIGN.md`](Docs/GAME_DESIGN.md) | The game's rules and numbers: actions, stages, enemies, training, menu. The last sections are the most recent. |
| [`Docs/GATEWAY_PROTOCOL.md`](Docs/GATEWAY_PROTOCOL.md) | The WebSocket wire format (protocol v2). |
| [`Docs/COMMS_INTEGRATION.md`](Docs/COMMS_INTEGRATION.md) | A practical guide for whoever writes the gateway. |
| [`Docs/VISUALIZER_STATUS.md`](Docs/VISUALIZER_STATUS.md) | The full development log and handoff: every module as built, setup, known issues. |
| [`Docs/JOURNAL.md`](Docs/JOURNAL.md) | A problem → decision log from the integration onwards. |
| [`Docs/DEMO_GUIDE.md`](Docs/DEMO_GUIDE.md) | How the course demo was run. |

## Working on it

- **Content is code.** Change `Assets/Editor/GameContentSetup.cs`, `StageDesigns.cs`, `PixelArt.cs` or `HUDSetup.cs`, re-run it (Tools ▸ MARIO or `-executeMethod ...Run -quit`), then commit the regenerated assets with the script.
- **Keep the module rules** in [`Docs/ARCHITECTURE.md`](Docs/ARCHITECTURE.md): `EventResolver` depends only on `GameStateManager`, presentation only listens, logic goes in `Tick` methods so tests can drive it.
- **Run `GameplaySmokeTest`** before committing, and add checks for new behaviour.
- **In the team repository** (`visualizer/` of CG4002-G15) this folder is a copy synced from the visualizer's own repository by `tools/push_to_integration.sh`. Team members should change the source repository and sync; a fork can edit this folder directly.

## Licensing

- Code and generated content: written for the CG4002 course; no licence has been chosen yet, so ask the authors before reusing it.
- **Press Start 2P** font: SIL Open Font Licence (`Assets/Fonts/PressStart2P-OFL.txt`).
- Mario, Goomba, Koopa, Bowser and Bullet Bill are Nintendo's characters; this is a non-commercial student project.
- **`Assets/Audio/`** holds a copyrighted recording of the Super Mario Bros. theme, for private use only. Delete it before making a copy public; the game runs without music (`AudioManager.music` is simply empty).
