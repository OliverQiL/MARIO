# CG4002 AR Visualizer — Project Status & Handoff

**Purpose of this document:** a complete, self-contained summary of the Unity/ARKit AR visualizer subsystem for a university capstone project (CG4002, NUS, Group B15). Written so an AI coding agent with zero prior context on this conversation can pick up development immediately. If you are that agent: read this fully before touching any code, and treat the design report (not included here, but referenced throughout) as the authoritative spec whenever this document is ambiguous or silent.

---

## 1. Project context

CG4002 is a capstone engineering project. The team is building a **wearable, wireless, AI-powered augmented reality exergame** — a first-person game inspired by Mario/Donkey Kong, where a player physically moves through a real room while body-worn sensors detect and classify their movements, and a phone-based AR app renders the game world and resolves the effects of those movements in real time.

**Owner of this subsystem:** Oliver, an NUS student with no prior Unity, AR, or Unix development experience before this project. This document covers **only his component** — the AR visualizer — not the rest of the system, though the rest of the system is described below for context since the visualizer integrates tightly with it.

**Team (Group B15):**
| Person | Owns |
|---|---|
| Oliver (this document's owner) | AR visualizer — iPhone app, Unity + AR Foundation/ARKit |
| John | Communications / laptop gateway |
| Xavier | AI classifier + FPGA (Ultra96) |
| Neo (Yin Qi) | Wearable sensors (FireBeetle ESP32 hardware) |

**Overall system data path:**
```
Wrist/ankle FireBeetle ESP32 wearables (IMU)
  --BLE-->  Laptop gateway (John)
  --TLS 1.3-->  Ultra96 FPGA (Xavier) — stateless 1D-CNN classifier, 6 action classes
  --back to gateway-->
  --secure WebSocket (WSS), JSON GAME_EVENT-->  iPhone (Oliver, this subsystem)
```
- Six classified action labels: `stand`, `walk`, `jump`, `squat`, `fireball`, `stomp`
- Wrist strap = fireball arm; ankle strap = stomp leg
- The phone holds **exactly one** network connection: a secure WebSocket to the laptop gateway. It never talks to the Ultra96 directly.
- The phone is the **authoritative owner of game state**; the gateway owns session state and event ID assignment.
- **Haptic feedback originates from the phone** and routes back phone → gateway → BLE → wearables. The Ultra96 is *not* in the haptic return path. (Note: as of the last report proofread, Section 4 of the design report reportedly still incorrectly attributes `HAPTIC_CMD` origin to the Ultra96 — this has been flagged to John to fix on the comms side. Not something this subsystem needs to change.)
- **Latency budget: 275ms**, measured from physical action completion (not action onset) to visible/haptic effect.
- Design is **fully markerless** — no fiducial markers, no pre-placed references, no touch input (the phone is inside a headset during play). All interaction derives from classified actions plus the phone's own spatial reasoning.

The design report is a 33-page PDF and is the single source of truth for anything this document doesn't cover in enough detail.

---

## 2. This subsystem's architecture

**Stack:** Unity 6 (LTS), AR Foundation (main branch, ~6.3, requires Unity 6000.0+), Apple ARKit XR Plugin, targeting iOS/ARKit. Universal Render Pipeline (URP). Chosen over direct SceneKit/RealityKit for Unity's mature physics/animation and AR Foundation's abstraction over ARKit's tracking, meshing, and occlusion subsystems.

**Test hardware:**
- **iPhone 17 (non-Pro)** — Oliver's personal device. Supports basic ARKit tracking (world tracking, plane detection, tracking-state reporting) but **has no LiDAR scanner**, so anything depending on `ARMeshManager` (scene mesh reconstruction) or `AROcclusionManager` (depth occlusion) produces no data / reports unsupported on this device. This is a known, permanent limitation of this specific phone, not a bug.
- **"Wen Le" — iPhone 17 Pro Max (iPhone18,2), has LiDAR** — used from 2026-09-24 for all LiDAR work (occlusion, scene mesh). Occlusion and meshing confirmed working on it.
- **iPhone 16 Pro Max** — the team's project device (has LiDAR), for final integration.

### The six-stage algorithm (design report Section 2.3)

Stages 1–2 run once per session; stages 3–6 form a repeating loop, one iteration per level stage.

1. **Session initialisation and environment scan** — `ARSession` in world-tracking config; `ARMeshManager` exposes room geometry with mesh colliders (LiDAR-dependent); `ARPlaneManager` identifies the floor and its boundary polygon; player scans until detected floor area exceeds the largest stage template's footprint; `NetworkClient` opens the WebSocket in parallel.
2. **Checkpoint anchoring** — an `ARAnchor` at the player's position projected onto the floor, yaw-only (pitch/roll discarded); stage geometry parents to this anchor.
3. **Stage generation** — stage prefabs declare a footprint + vertical clearance; search for a free region (preferring the anchor's forward arc), validate with `Physics.CheckBox` against mesh colliders, instantiate under the checkpoint anchor. On failure: fall back through rotated placements, then a compact stage variant, then prompt the player to move and repeat.
4. **Continuous tracking and rendering** — 60Hz ARKit pose updates drive the AR camera; each frame: camera feed as background, virtual geometry rendered, LiDAR depth occlusion applied, physics stepped, and tracking state polled — a reported `NotTrackingReason` suspends gameplay and triggers relocalisation. Entirely local, no network dependency.
5. **Event ingestion and resolution** — a `GAME_EVENT` arrives over the WebSocket (action label, confidence, IDs); resolved through an ordered six-gate chain (see module table below); rejection at any gate produces distinct feedback so "not registered" is distinguishable from "not permitted."
6. **Stage completion and loop** — on reaching the stage goal, destroy the completed stage's geometry and anchor, create a new anchor at the player's current pose, return to step 3. Bounds both memory growth and accumulated tracking drift. Loop ends on player exit or loss of all lives.

### Module table (design report Section 6.2)

| Module | Responsibility | Key dependencies |
|---|---|---|
| `ARSessionManager` | Session lifecycle, tracking-state monitoring, scan-phase orchestration, relocalisation handling | `ARSession`, `ARPlaneManager`, `ARMeshManager` |
| `LevelGenerator` | Checkpoint anchoring, template selection, placement search, mesh validation, fallback ladder | `ARAnchorManager`, `ARRaycastManager` |
| `GameStateManager` | Authoritative game state: score, lives, stage progress, power-ups, object lifecycle | — |
| `EventResolver` | Gate chain of Section 2.3 step 5; maps classified actions to game outcomes | `GameStateManager` |
| `NetworkClient` | Secure WebSocket to gateway, cert pinning, JSON (de)serialisation, dedup, `EVENT_ACK`, `HAPTIC_CMD`, reconnect w/ backoff | `GatewayConnection` (TcpClient + SslStream + `WebSocket.CreateFromStream`) |
| `RenderManager` | Occlusion configuration, material/effect management, camera background | `AROcclusionManager`, `ARCameraBackground` |
| `HUDController` | Screen-space UI, connection status, degraded-mode messaging | Unity UI |
| `AudioManager` | Impact, collect, and rejection audio cues | Unity Audio |

**Important nuance:** Step 4 in the *algorithm* description spans two different *modules* — the render/occlusion half belongs to `RenderManager`, but tracking-state polling and relocalisation belong to `ARSessionManager`. Don't conflate these when extending Step 4 work.

The gate chain (Step 5 / `EventResolver`), in exact order:
1. **Session state** — drop events arriving during scan, relocalisation, or stage transition
2. **Validity** — reject duplicate `event_id` or sub-threshold confidence
3. **Player capability** — does game state permit this action (e.g. `has_fire_flower`)
4. **Spatial gating** — is a target within interaction radius and facing angle of the device pose
5. **Target state** — is that target actionable (unspent block, living enemy)
6. **Resolution** — apply outcome, mutate state, spawn effects, play audio, issue `HAPTIC_CMD`

`GAME_EVENT` JSON fields (from the gateway): protocol version, session ID, event ID, window ID, action label, confidence, event timestamp. Phone replies with `EVENT_ACK` and discards duplicate event IDs. On startup the phone validates the gateway's pinned certificate and sends `VISUALIZER_READY`.

---

## 3. Project structure on disk (current as of 2026-09-30, after the clean-up)

- **The project:** a Unity 6 (URP) project; all game code is ours, under `Assets/Scripts` and `Assets/Editor`. The `arfoundation-samples` clone lives *outside* the project and is for reference only (see Known Issues).
- **Player settings:**
  - iOS bundle `com.cg4002.mario`, landscape only (either direction, set by `BuildScript`)
  - arm64
  - Active Input Handling "Both"
  - 60 fps (set at runtime)

```
Assets/
  Scenes/SampleScene.unity      the only scene
  Scripts/                      runtime: AR session, level generation, gate chain, network, HUD, audio, headset view
    Gameplay/                   stage elements, enemies, pickups, effects, difficulty, player body
  Editor/                       one-click setup (Tools ▸ MARIO ▸ …), content builders, headless tests, art preview, iOS build
  Stages/                       fallback stage prefabs (Large, Standard, Compact) + Campaign/Stage 1-1 … 1-4
  Prefabs/                      element prefabs, Effects/, bonus room parts, AR Scan Plane, BlockCube/Pennant meshes
  Materials/ Textures/          generated by GameContentSetup / PixelArt (pixel-art PNGs)
  Fonts/                        Press Start 2P (SIL OFL, licence included) + its TMP font asset
  Shaders/HeadsetLens.shader    headset view lens pre-distortion
  Audio/                        background music (copyrighted Super Mario Bros. theme: private repo only)
  AR Mesh.prefab                room-mesh prefab (ARMesh layer)
  UnityXRContent/               XR Simulation rooms (gitignored; tools/fetch_sim_environments.sh)
Docs/  GAME_DESIGN.md, GATEWAY_PROTOCOL.md
tools/ mock_gateway.py (incl. --keys), fetch_sim_environments.sh
```

### Scene hierarchy (SampleScene)

```
AR Session
XR Origin            ARPlaneManager (plane prefab "AR Scan Plane"), ARAnchorManager, ARRaycastManager,
                     ARSessionManager, LevelGenerator
├─ Camera Offset
│  └─ Main Camera    ARCameraManager, ARCameraBackground, TrackedPoseDriver, AROcclusionManager, RenderManager
└─ AR Meshes         ARMeshManager (mesh prefab "AR Mesh"; must be a child of XR Origin)
GameManager          GameStateManager, EventResolver, NetworkClient, AudioManager (+AudioSources), Difficulty,
                     EditorActionInput (Editor only)
Canvas               HUDController, HeadsetView; the HUD's text/icons/panels (built by HUDSetup);
                     "LidarDebug (TMP)" with LidarDebugHUD (hidden unless Show is ticked)
```

### Setup tools (Tools ▸ MARIO), all idempotent
- **Build Game Content** (`GameContentSetup`): every prefab, material, texture and stage, plus the scene wiring.
- **Configure HUD** (`HUDSetup`): the HUD and the headset view.
- **Configure Scan** (`ScanSetup`): the scan plane, with development visuals off.
- **Configure Network** (`NetworkSetup`), **Configure LiDAR** (`LidarSetup`) and **Mesh Debug View ▸ On/Off**.
- **Configure Simulation** (`SimulationSetup`) and **List Simulation Environments**.
- **Render Art Preview** (`ArtPreview`) and **Build iOS (Local | John) [Release]** (`BuildScript`).

### Sharing with the team's integration repo
`tools/push_to_integration.sh` copies this repo's committed `main` into `visualizer/` of https://github.com/icknee/CG4002-G15 (git subtree, squashed: one commit per sync, naming the MARIO commit), then pushes to its `main`.
- **First run:** `tools/push_to_integration.sh <url>`; the URL is remembered in git config.
- **Afterwards:** `tools/push_to_integration.sh`.
- `DRY_RUN=1` shows the result without pushing.
- **Treat `visualizer/` there as read-only:** change things here in MARIO and sync.

### Tests
Run headless, with Unity closed (see memory/notes for the editor path):
- `GameplaySmokeTest` (75 checks)
- `SimulationSmokeTest` (`-scan`: jump to start, sweep, jump to finish, check 1-1 is placed; `-headset`: save the side-by-side frame)
- `NetworkSmokeTest` (needs the mock gateway running with `--script`)

---

## 4. What's been built and validated, module by module

Status legend: ✅ built & tested on real hardware · 🟡 built & tested in Editor only · ⚠️ built but not yet tested · ❌ not started

### `ARSessionManager.cs` — ✅
Two responsibilities, both implemented:
- **Scan-phase orchestration**: subscribes to `ARPlaneManager.trackablesChanged`, filters to `PlaneAlignment.HorizontalUp` only (floor, not walls/ceiling), sums plane areas, fires `OnScanComplete` (`UnityEvent`) once total ≥ `requiredFloorArea` (currently tuned to 10 m² in testing, default field value 4 m²). Logging is throttled — only logs on ≥0.2 m² increments and stops entirely once scan is complete, to avoid console spam.
- **Tracking-state monitoring / relocalisation**: polls `ARSession.notTrackingReason` every frame, detects transitions, fires `OnTrackingLost` / `OnTrackingRestored` (`UnityEvent`s), exposes `IsTracking` and `LastNotTrackingReason`.
- `[RequireComponent(typeof(ARPlaneManager))]`. Lives on the XR Origin GameObject.
- **Confirmed on-device**: scan completion fires correctly and once; tracking loss/restore cycles correctly on real `InsufficientFeatures` events, logged in both directions repeatedly without issue.
- **Wired to `GameStateManager`**: `OnTrackingLost` → `GameStateManager.SetSessionNotReady()`, `OnTrackingRestored` → `GameStateManager.SetSessionReady()`, via persistent Inspector listeners (not code coupling — keeps the module dependency graph clean per the report's table, since `ARSessionManager` has no listed dependency on `GameStateManager`). This automates what was previously a manual Inspector toggle for testing Gate 1.

### Clean-up (2026-09-30)
Removed as redundant or deprecated:
- **Template content:** `Readme.asset`, `TutorialInfo/`, TextMesh Pro's `Examples & Extras/` (284 files, 6.4 MB, unreferenced)
- **Replaced assets:** `AR Default Plane.prefab` (now `AR Scan Plane`), and the checkpoint debug `Sphere.prefab` with `LevelGenerator.debugMarkerPrefab` (plus an empty `OnScanComplete` listener that pointed at it)
- **Debug mesh:** `AR Mesh (Debug).prefab` and `ARMeshDebug.mat`. Mesh Debug View ▸ On regenerates them, and `LidarSetup` no longer switches the debug mesh on.
- **`StageLoopSetup`:** folded into `GameContentSetup`
- **Migration code for long-gone objects:** ScanHUD, DebugActionInput, the old Stats text
- **Deprecated Unity APIs:** `FindObjectsByType(FindObjectsSortMode)` → `FindObjectsByType<T>()`; `AssetDatabase.ImportPackage` → `AssetPackage.Package.Import`. Our code now compiles with no warnings.
- **Build output:** `ios-build_BackUpThisFolder_ButDontShipItWithYourGame/` is now untracked and gitignored.

Also fixed that day: the first stage could take a long time to appear. The checkpoint needed a detected plane directly under the player's feet, the patch least often looked at, especially with jump-to-start and a head-mounted phone looking ahead. The Editor log showed it failing every 2 s after a 97.7 m² scan. It now falls back to the scanned floor level under the player; placement still requires detected floor under the whole stage. `SimulationSmokeTest -scan` sees the floor under itself, so it doesn't exercise that fallback.

Also fixed that day: without a fire flower, the HUD labelled the bare-handed strike on a brick "Fireball!" (and "Fireball: nothing in range"); it now says "Punch".

Earlier removals: `ScanHUD.cs` (replaced by `HUDController`, 2026-09-28) and `DebugActionInput.cs` (2026-09-25).


### `LevelGenerator.cs` — ✅ (with caveats)
- **Step 2 (checkpoint anchoring)**: on `OnScanComplete`, raycasts straight down from the camera against `TrackableType.PlaneWithinPolygon` to find the floor, retains only yaw, creates an `ARAnchor` via `anchorManager.TryAddAnchorAsync(pose)` (the modern async AR Foundation anchor API). Optionally instantiates a debug marker prefab under the anchor.
- **Step 3 (stage generation)**: `TryPlaceStage()` orders `stageTemplates` (each requiring a `StageFootprint` component) by footprint area descending. For each template, searches the anchor's forward arc (±60°, configurable) at several distances (0.5–2.5m), falling back to the full circle if the arc yields nothing; at each candidate, tries several yaw rotations (0/90/180/270°); validates each with `Physics.CheckBox` against the candidate's footprint half-extents. On success, instantiates the prefab as a child of the checkpoint anchor. On total failure across all templates, fires `OnPlacementFailed` and retries automatically after `retryDelaySeconds` (default 2s) via `Invoke` — no tap-to-retry, consistent with the no-touch-input constraint.
- `[RequireComponent(typeof(ARSessionManager), typeof(ARAnchorManager), typeof(ARRaycastManager))]`. Lives on XR Origin.
- **Confirmed on-device**: anchor stays correctly pinned to the floor across movement (ARKit's anchor-correction working as expected). Stage placement succeeds; one real multi-retry-then-success case was observed and diagnosed as the floor plane's *polygon boundary* not yet having grown to cover the candidate point at the exact moment scanning completed (not a Physics.CheckBox rejection) — a few seconds of continued passive tracking resolved it. This is expected behavior, not a bug.
- **LiDAR additions (2026-09-24), ✅ confirmed on the LiDAR iPhone**:
  - `obstructionLayerMask` = `ARMesh` layer only, so the debug marker sphere and stage colliders no longer block placement.
  - The LiDAR mesh includes the floor, so `IsPlacementClear` lifts the test box by `floorClearanceMargin` (0.1 m). Without this, every candidate hits the floor mesh.
  - On LiDAR devices, placement waits `meshSettleSeconds` (3 s) after scan completion and for ≥ `minMeshChunks` chunks, capped at `maxMeshWaitSeconds` (10 s). Non-LiDAR devices don't wait.
  - Uses `Physics.OverlapBoxNonAlloc`, not `CheckBox`, so it can record the blocking collider. It exposes `TotalObstructedCandidates`, `LastObstructionName`, `MeshChunkCount`, `IsMeshingActive` and `IsWaitingForMesh`.
  - First on-device run (a small bedroom): 144 candidates were rejected by real room-mesh chunks, then the stage was placed on open floor.
- **Caveats / not yet exercised**:
  - Only **one stage template** (`TestStage`) exists — the "fall back to a smaller compact template" tier has never actually been tested since there's nothing smaller to fall back to.
  - The obstruction test has now run against real room mesh, but it hasn't been measured systematically (for example, how often valid floor near furniture is wrongly rejected). ARKit mesh classification (removing floor-classified faces instead of using a margin) is a possible refinement.
  - Currently places **exactly one stage per checkpoint** and stops (a `_stagePlaced` guard) — chaining further stages is Step 6, not yet built.

### `StageFootprint.cs` — ✅
Metadata component: `width`, `depth`, `clearance` (metres), exposing `HalfExtents` (for `Physics.CheckBox`) and `FootprintArea` (for template ranking). Includes an editor-only Gizmo drawing the footprint box in the Scene view. Attached to `TestStage`.

### `GameStateManager.cs` — 🟡
Authoritative state: `score`, `lives` (default 3), `stageIndex`, `hasFireFlower`, and `isSessionReady`. Public methods `AddScore(int)`, `LoseLife()`, `SetFireFlower(bool)`, plus parameterless `SetSessionReady()` / `SetSessionNotReady()` wrappers specifically added for Inspector `UnityEvent` wiring. Lives on a standalone `GameManager` GameObject (not under XR Origin), matching the report's "no dependencies" listing. Tested via `EventResolver`'s Editor Play Mode tests — score increments, session-ready toggling, all confirmed via console logs.

### `IInteractable.cs` — ✅ (interface, nothing to "test" standalone)
`bool IsActionable { get; }`, `bool RespondsTo(string actionLabel)`, `void ResolveHit(string actionLabel, GameStateManager gameState)`. Anything `EventResolver` can target implements this.

### `EventResolver.cs` — 🟡 (most thoroughly validated module, zero device dependency)
Implements the full six-gate chain exactly as specified above. `Resolve(GameEvent)` returns a `ResolutionResult`:
- `Gate` is 1–5 for the gate that rejected the event, or 6 when it resolved.
- `Reason` is the rejection reason.
- `Target` is the object that was hit.
- `NotRegistered` is true for gates 1–2; `NotPermitted` is true for gates 3–5.

It also raises the C# event `OnEventProcessed(GameEvent, ResolutionResult)`. NetworkClient listens to it for ACKs and haptics; the HUD and AudioManager should use it as well. EventResolver itself still depends only on `GameStateManager`.

Other details:
- `GameEvent` carries `actionLabel`, `confidence`, `eventId`, `sessionId`, `windowId` and `timestamp` (the gateway's clock, used for logging only).
- Gate 2's duplicate check uses `sessionId/eventId` as the key, with a bounded history of the last 1024 IDs (`dedupHistorySize`).
- `minConfidence` defaults to 0.6.
- Gate 3's capability check only maps `"fireball"` to `hasFireFlower` so far.
- Every rejection is logged as `REJECTED @ Gate N (reason)`.
- **Confirmed** in Editor Play Mode earlier. On 2026-09-25 it was confirmed again end to end through NetworkClient (see below).
- Still TODO at Gate 6: spawning effects and the AudioManager cue. Haptics are handled by NetworkClient.

### `TestInteractable.cs` — test-only
Now derives from `InteractableBase`. It's used only by `NetworkSmokeTest`. Real stage content replaced it on 2026-09-29; see "Gameplay (pass 1)".


### `RenderManager.cs` — ✅ (rewritten 2026-09-24; the earlier version was never saved in the project)
Occlusion configuration + camera background (NOT tracking-state, which is `ARSessionManager`'s job). Lives on **Main Camera** next to `AROcclusionManager`, not on XR Origin. Checks `descriptor.environmentDepthImageSupported` every frame until it stops being `Unknown`, then:
- if supported: environment depth `Best`, temporal smoothing on, `PreferEnvironmentOcclusion`
- if unsupported: logs a warning and disables the `AROcclusionManager`

Human segmentation is always off. Exposes `IsResolved`, `OcclusionSupported`, `OcclusionActive` and the `OnOcclusionStateResolved(bool)` event.
- **Confirmed on the LiDAR iPhone**: the HUD reports occlusion active, and a person and a desk correctly hide virtual content behind them.

### `LidarDebugHUD.cs` — ✅ development readout (hidden by default: tick Show)
A TMP readout under the scan text: occlusion state, mesh chunk count, obstructed candidate count and the last collider that blocked a placement. Delete once `HUDController` exists.

### Editor tooling (`Assets/Editor/`) — ✅
- `LidarSetup.cs` — **Tools ▸ MARIO ▸ Configure LiDAR** (or `-executeMethod LidarSetup.Run`). Safe to re-run. It:
  - creates the `ARMesh` layer
  - adds the occlusion components to Main Camera
  - builds `AR Mesh (Debug).prefab` and `Assets/Materials/ARMeshDebug.mat`
  - sets up LevelGenerator's mask and mesh reference
  - creates the HUD object
  - sets iOS signing (team U5T7NBV8MB, automatic)

  Also **Tools ▸ MARIO ▸ Mesh Debug View ▸ On/Off**.
- `BuildScript.cs` — two gateway profiles, each a separate app so both can be installed at once:

  | Profile | App (bundle ID) | Gateway | Pin | Output |
  |---|---|---|---|---|
  | **Local** | "MARIO Local" (`com.cg4002.mario.local`) | this Mac's `tools/mock_gateway.py`, `wss://Olis-MacBook-Pro.local:8765/` | read from `tools/certs/gateway.crt` | `ios-build-local/` |
  | **John** | "MARIO" (`com.cg4002.mario`) | John's laptop (`integrated/laptop`, visualizer mode) on its hotspot, `wss://192.168.137.1:8765/` | `John.Pin` in `BuildScript.cs`, from his gateway's startup line `VISUALIZER GATEWAY: certificate SHA-256 ...`; the build refuses to run without one | `ios-build-john/` |

  Menu **Tools ▸ MARIO ▸ Build iOS (Local)** / **(John)**, plus **Release** variants; headless `BuildIOSLocal`, `BuildIOSLocalRelease`, `BuildIOSJohn`, `BuildIOSJohnRelease`, with optional `-gatewayUrl` / `-pin` overrides for one build. The profile's URL, pin, product name and bundle ID apply only during the build; the scene and Player Settings are restored afterwards, so Editor Play still uses the scene's NetworkClient settings. John gets the pin with `openssl x509 -in comms/security/server.pem -noout -fingerprint -sha256` (integrated `SECURITY.md` §2); the whole output line can be pasted. The app checks only the fingerprint, not the certificate's 90-day expiry or hostname, so the pin holds until he creates a new certificate, which needs a rebuild. The phone must be on John's hotspot (his gateway refuses other networks unless he sets `VISUALIZER_ALLOW`).
- Headless build → device loop (Unity must be closed; `<profile>` is `local` or `john`):
  ```
  Unity -batchmode -quit -projectPath . -buildTarget iOS -executeMethod BuildScript.BuildIOSLocal   # or BuildIOSJohn
  xcodebuild -project ios-build-<profile>/Unity-iPhone.xcodeproj -scheme Unity-iPhone -configuration Debug \
    -destination "id=00008150-001259891EF8401C" -derivedDataPath <dd> -allowProvisioningUpdates build
  xcrun devicectl device install app --device <id> <dd>/Build/Products/Debug-iphoneos/<App>.app
  xcrun devicectl device process launch --device <id> --console com.cg4002.mario.local   # or com.cg4002.mario
  ```

### Gameplay (pass 1) — 🟡 built; headless tests pass; on-device check pending (2026-09-29)
**Spec:** `Docs/GAME_DESIGN.md`, agreed with the team, extending report §6.4. Scripts are in `Assets/Scripts/Gameplay/`. Content is built from primitives by `GameContentSetup.cs` (**Tools ▸ MARIO ▸ Build Game Content**).

**Elements and scripts:**
- **`InteractableBase`:** the registry of targets. Targeting is a geometric query over this registry, not a physics query, so stage elements have no colliders. `IInteractable` and `TestInteractable` now go through it.
- **`QuestionBlock`:** sits at eye height + 0.3 m and is hit by a jump. It drops a fire flower if the player has none, otherwise gives a coin (+100), then it's spent.
- **`BrickBlock`:** sits at chest height and is broken by a bare-handed strike or a fireball (+50).
- **`Pickup`:** the fire flower. It's collected by walking to it.
- **`Goomba`:** walks towards the player at 0.25 m/s. Stomp or fireball defeats it (+100). Contact costs a life, unless the player is airborne, in which case it passes by. It's frozen while the session isn't ready.
- **`EnemySpawner`:** spawns 2 Goombas, one at a time.
- **`Fireball`:** a projectile aimed along the camera; at most 2 are in flight (Gate 3).
- **`CoinPop`** (visual only) and **`EyeHeightPlacement`**, which sets block heights relative to the player's eyes.

**`EventResolver`** now has a rule per action:
- stand, walk and squat, and a jump that hits nothing, are state-only (`ResolutionResult.StateOnly`)
- jump hits an overhead block
- fireball launches a projectile if the player has a flower, otherwise it's a bare-handed strike
- stomp hits an enemy at the player's feet
- during game over, only a jump (after a 2 s grace period) does anything: it restarts
- a new `OnImpact` event reports deferred fireball hits

**`GameStateManager`** has:
- windows for airborne (1 s), ducked (1 s) and invulnerable (2 s)
- `TakeHit`, which costs a life and the flower
- game over, and `RestartGame`
- the events `OnLifeLost`, `OnGameOver` and `OnGameRestarted`
- a lazy `Instance`, used by elements spawned at runtime

**`LevelGenerator`:**
- **Templates:** `Assets/Stages/StandardStage` (1.2 × 2.0 m: ? block, brick, spawner, flag) and `CompactStage` (0.6 × 1.0 m: ? block, flag). TestStage was deleted.
- **Placement validity:** the footprint needs **detected floor under all 4 corners and under the flag**. This fixes flags over furniture on phones without LiDAR.
- **Game over and restart:** `EndGame`/`RestartLoop` are wired from `OnGameOver`/`OnGameRestarted`.
- **Scan target:** raised to 4 m², which is more than the largest footprint (2.4 m²).

**Other changes:**
- **Haptics:** there's a new `damage` pattern. "Denied" is no longer sent for continuous actions, and nothing is sent for state-only actions.
- **HUD:**
  - banner: "GAME OVER — jump to play again"
  - feedback: "Ouch! −1 life", "Fireball!", "Fireball hit!"
  - stats: a FIRE indicator while the player has the flower

**Tests:**
- `GameplaySmokeTest.cs` checks the rules above against the real prefabs: **31/31 pass** (2026-09-30). Run it with `-executeMethod GameplaySmokeTest.Run` (no `-quit`).
- `NetworkSmokeTest` now expects gates `6,5,2,4,2,6` and passes.

### Editor playtesting with XR Simulation — ✅ set up (2026-09-29)
Day-to-day gameplay testing happens in the Editor: open `SampleScene` and press Play. AR Foundation's XR Simulation, which is the Editor/Standalone XR loader, supplies a virtual room in place of the camera.
- **Moving:** hold right-click and drag to look; WASD to move. Planes are only discovered while the camera moves, so look around and walk to scan.
- **Actions:** keys **1 stand, 2 walk, 3 jump, 4 squat, 5 fireball, 6 stomp** (`EditorActionInput`, on GameManager). They go straight into `EventResolver`.
  - Held continuous actions (stand, walk, stomp) repeat every 0.1 s, like classifier windows.
  - Discrete actions (jump, squat, fireball) fire once per press.
  - The component disables itself outside the Editor and uses the Input System package.
- **Simulated room:** `Museum_69ftx48ft` since 2026-09-30, chosen for its open floor; it was `LivingRoom_20ftx18.5ft`, which was too cramped for full-size stages. Both come from Unity's official XR Simulation sample environments.
  - **Each machine runs `tools/fetch_sim_environments.sh` once, with Unity closed.** It downloads the environments into `ContentPackages/`, imports them into `Assets/UnityXRContent/` (both gitignored, ~40 MB), and runs `SimulationSetup`.
  - To switch rooms: **Tools ▸ MARIO ▸ List Simulation Environments**, then `SimulationSetup.Run -environment NAME`.
- **Floor rule:** simulation doesn't classify planes (`supportsClassification` is false), so `IsFloorPlane` uses the height fallback there. ARKit's Floor labels are used on devices.
- **Network in the Editor:** `NetworkClient` also connects from the Editor if the mock gateway is running. It was seen connecting over 127.0.0.1.
- **Play Mode setting:** this project has **Enter Play Mode Options** on, so there's no domain reload. Static state (the `InteractableBase` registry, `Fireball` in-flight list, `GameStateManager.Instance`) survives between Play sessions, and it's pruned using Unity's null check.
- **`SimulationSmokeTest.cs`:** a headless Play Mode check (no `-quit`). It verifies the room loads, the session tracks and nothing errors. It does **not** scan, because the camera doesn't move.

**Testing layers:**
- headless rule tests (`GameplaySmokeTest`, `NetworkSmokeTest`) after every change
- Editor + simulation for gameplay feel
- a device at milestones, for real tracking, rooms, LiDAR, Wi-Fi and performance

**Also on 2026-09-29, both from the pass-1 device test:**
- `EnemySpawner` waits while the player is within 1.2 m of its spawn point. On the device, a Goomba had hit the player about 1 s after spawning.
- `GatewayConnection` tries routable IPv4 first, then link-local 169.254.x.x (a USB/peer-to-peer link that comes and goes, and caused a ~30 s reconnect), then IPv6.

**Pass-1 device test (iPhone 17, 2026-09-29):**
- Confirmed: the floor-only scan, the compact fallback (the first time it has fired), the standard stage and spawner, Goomba damage with the `damage` haptic, game over, and jump-to-restart.
- Not yet exercised on the device: the ? block and flower, fireballs, stomping and the brick strike. Relaying actions through chat was too slow for enemy encounters, which is why Editor simulation testing was set up.

**Pass-1 Editor playtest (XR Simulation, 2026-09-30):** every pass-1 rule was exercised by hand and works: ? block, flower, fireballs, brick strike, stomp, damage, jump-over, game over and restart, stage clear, and the HUD. Two fixes came out of it:
- **The flower was collected before it could be seen.** It dropped straight under the block, inside its 0.45 m pickup radius, where the player was already standing. It now falls from the block (`Pickup.DropFrom`, 0.5 s) and lands `flowerLandingOffset` (0.5 m) past the block, away from the player. `TryCollect` measures to the landing spot.
- **Holding stomp spammed the HUD.** After a stomp landed, the repeats showed "already used", then "nothing in range". The HUD now shows nothing when stand, walk or stomp is refused. The haptics already worked this way, and both now use `EventResolver.IsContinuous`.

Two things that looked like bugs aren't:
- A spent ? block rejects further jumps ("already used"). A coin only comes from hitting a fresh block while already holding a flower.
- CompactStage has no brick and no Goombas.

Running the tests from the command line:
- `Unity` on this Mac's PATH is a different CLI. Use `/Applications/Unity/Hub/Editor/6000.6.0f1/Unity.app/Contents/MacOS/Unity`.
- `NetworkSmokeTest` needs the gateway started with `--script "stomp,stomp,dup,fireball,low:jump,walk" --exit-after-acks`.

### Gameplay (pass 2) — ✅ done and Editor-playtested (2026-09-30); on-device check deferred to the end
The rules are in `Docs/GAME_DESIGN.md`, "Pass 2 details".
- **`Koopa`** (derives from `Goomba`): the first stomp turns it into a stationary, harmless shell, and a second *separate* stomp clears it (+200). A fireball defeats it outright.
  - "Separate" is decided by `GameStateManager.NoteStomp`/`StompStartedAt`: a stomp event arriving more than 0.3 s after the previous one starts a new stomp motion.
  - Gate 5 is per action (`InteractableBase.IsActionableFor`).
- **`BulletBillCannon`** (Standard stage, far corner):
  - a tower whose head is set to head height at `Start` (the column is stretched to meet it)
  - the head turns to face the player at 120°/s
  - up to 2 shots per stage; each is preceded by a 1.5 s warning (muzzle glow and `OnWarning` → HUD)
  - it holds fire within 1.2 m of the player
- **`BulletBill`:** `Launch(direction)`, then a straight line at 2 m/s, with no homing. It hits unless the player is ducked or has stepped aside, and it can't be targeted. This replaced the first version's `BulletBillLauncher`, where a Bill appeared 5 m ahead of the player with no cannon.
- **? block power-up:** random, `oneUpChance` 0.5. A roll that would be wasted gives the other power-up, and a coin comes only if both would be wasted. `GameStateManager.maxLives` = 8 caps `AddLife`.
- **`EnemySpawner`:** cycles through `enemyPrefabs`, a Goomba then a Koopa.
- **Tests:** `GameplaySmokeTest` passes 51/51. The simulation and network smoke tests also pass.

**Playtest history:**
- **First Editor playtest (2026-09-30):** the Koopa, Bullet Bill and 1-Up rules all worked. Oliver then asked for:
  - a random power-up
  - an 8-life cap
  - a visible cannon that tracks the player and fires Bills that fly straight

- **Second Editor playtest (2026-09-30):** the logic was fine. The cannon read as a bare rod, and the warning was a jarring red ball about 1 m across, because the pulse overwrote the glow's authored scale (a bug).
  - The fix pulses the glow ±12 % around its own scale.
  - The cannon was restyled: base, steel column, collar, black housing with skull plates, a grey barrel with a silver rim and black bore (so it reads head-on), and an amber emissive glow inside the bore.

**Playtest checklist for pass 2** (reuse it on the device):
- **Koopa:** stomp once, and check it becomes a shell and nothing is shown while stomp is held. Release, stomp again, and check it's cleared for +200. A fireball kills a walking Koopa outright.
- **Cannon:** it stands on the far corner of a Standard stage and turns to follow you as you move. About 6 s in, the muzzle glows and the warning shows, then it fires.
- **Bill:** squat (key 4) as it arrives and it misses. Stand still and you lose a life. Step aside after it's fired and it misses, because it doesn't turn. Walk within 1.2 m of the cannon and it shouldn't start a shot.
- **? blocks:** over several stages you should see both flowers and green 1-Up mushrooms. Hearts go up to 8 at most.

### Gameplay (pass 3) — ✅ done and Editor-playtested (2026-09-30); warp pipe to be redesigned with the real maps
The rules are in `Docs/GAME_DESIGN.md`, "Pass 3 details". They're proposed defaults.
- **Bigger layouts:** a `LargeStage` template (2.0 × 3.0 m) is tried first.
  - The scan target is 7 m² (`requiredFloorArea`).
  - `ARSessionManager.CheckScanPlateau` accepts at least 3 m² (`minFloorArea`) once the floor stops growing for 15 s, so small rooms still finish scanning.
- **`WarpPipe`:** on Large and Standard stages, with a 50 % chance per stage. `EventResolver`'s squat looks for a pipe within 0.35 m; entering one calls `StageGoal.Reach()` and `GameStateManager.QueueWarp(1)`, which makes `CompleteStage` advance 2 stages.
- **`Difficulty`** (on GameManager) is a pure function of the stage number, with caps. It's read by:
  - `EnemySpawner`: the enemy count, and `Goomba.SpeedMultiplier`
  - `BulletBillCannon`: the number of shots and the interval between them
  - `BulletBill`: `Launch(dir, speedMultiplier)`
- **`AudioManager`** (on GameManager, with an AudioSource) synthesises its clips at `Awake`.
  - It listens to `EventResolver`, `GameStateManager` and `LevelGenerator`.
  - The ? block, pickups, warp pipe and cannon call the static `AudioManager.Play(Sfx)` directly. It does nothing when there's no AudioManager.
  - There's no background music yet.
- **Content:** `GameContentSetup` builds `LargeStage` and the warp pipes, and adds Difficulty and AudioManager to GameManager.
- **Tests:** `GameplaySmokeTest` passes 62/62, including 11 pass-3 checks. The simulation and network smoke tests also pass.
- **Deferred:** art in place of primitives, until after the playtest.

**Editor playtest (2026-09-30):** everything worked as intended, and Oliver was especially pleased with the sound effects.
- **Follow-up:** the warp pipe shouldn't clear the stage. It should teleport the player to a temporary side area that has an exit pipe back to the main stage, as in Mario. It waits for the real map and stage work; see `Docs/GAME_DESIGN.md`, "Later passes". The current `WarpPipe` stage-clear-and-skip behaviour is a placeholder.

**Playtest checklist for pass 3:**
- **Layout:** scanning now needs 7 m². In the LivingRoom simulation you should get a **Large** stage, with 2 ? blocks, 2 bricks, a cannon and 3 enemies.
- **Warp pipe:** look for a short green pipe; it's only on some stages. Walk onto it, squat (key 4), and check for "Warp!", the warp sound, a stage clear, and a stage number that jumps by 2.
- **Difficulty:** by stage 5 or so there should be more enemies, faster walkers and an extra cannon shot.
- **Audio:** check the Game view isn't muted. Listen for jump, coin, power-ups, stomp, brick, fireball, damage, the cannon warning and fire, stage clear and game over. A refused jump, squat or fireball gives a soft bonk; a held stomp or walk gives nothing.

### Art pass 1: blocks and enemies — ✅ Editor-playtested (2026-09-30)
- **Blocks:** pixel-art textures drawn as data in `Assets/Editor/PixelArt.cs`: the ? block (gold, rivets, bevel, "?" with a drop shadow), the spent block and the brick.
  - They're saved as point-filtered PNGs in `Assets/Textures/`.
  - Blocks use a generated `BlockCube` mesh, because Unity's cube draws some faces upside down ("¿").
  - A ? block bumps when hit (`QuestionBlock`); a brick bursts into `BrickDebris`.
- **Enemies:** remodelled from primitives after the Mario designs.
  - **Goomba:** brown head with the face on it (eyes, angry brows, fangs), tan body, dark feet.
  - **Koopa:** scute-textured green shell (`PixelArt.KoopaShell`), white rim, yellow head with a snout and big eyes, four feet. In the shell it hides `Model/Head` and `Model/Feet`.
  - **Bullet Bill:** glossy black, eyes with angry lids, white gloves, tail ring.
  - Walking enemies waddle (`EnemyWaddle` on `Model`, which reads `Goomba.IsMoving`).
- **`ArtPreview`** (Tools ▸ MARIO ▸ Render Art Preview, or `-executeMethod ArtPreview.Run -out <dir>`) renders the blocks and enemies to PNGs without Play Mode. It turns URP's SRP batcher off while rendering, because in batch mode the batcher drew every block with the first block's texture.
- **Background music:** `AudioManager` has a **Music** clip slot and a **Music Volume** setting. It loops from the first stage, stops at game over and restarts with the next game.
- **Tests:** 62/62, and the simulation and network tests pass.

### Art pass 2: items — ✅ Editor-playtested (2026-09-30)
- **Fire flower:** red petals, a yellow ring, and a cream centre with eyes on both faces (it spins), on a stem with two leaves.
- **1-Up:** green cap with white spots placed flat on its curve (one on top, four around the sides), and a cream stalk with eyes.
- **Coin:** a slightly glowing gold disc with a darker rim and a raised shine bar.
- **Fireball:** a glowing orange ball with yellow flame licks, spinning (`Spinner` on `Model`).
- **Warp pipe:** body and a wider lip textured with `PixelArt.PipeSide`, a band texture that wraps once around the cylinder, giving the highlight stripe.
- **Goal:** a spent-block base, a 1 m green pole with a ball on top, and a white double-sided pennant (a generated `Pennant` mesh) with a green emblem. The flag used to be a yellow cube on a 0.5 m pole.
- **`ArtPreview`** now also renders `items.png`.
- **Background music:** Oliver added `Assets/Audio/Super Mario Bros. Theme Song.mp3` and assigned it to AudioManager's Music slot. **It's copyrighted.** It's committed because the repo is private (Oliver's decision, 2026-09-30). Remove it before the repo, a demo video or a build goes public.

### HUD styling — ✅ Editor-playtested (2026-09-30)
- **Font:** Press Start 2P, a pixel font under the SIL OFL (`Assets/Fonts`, license included).
  - `HUDSetup` makes a TMP font asset from it with ASCII baked in and the **atlas frozen (Static)**. With a dynamic atlas, Play Mode hit Unity's "External Gfx Allocation has already been registered" assert (`SimulationSmokeTest` failed consistently); freezing it fixed that.
  - Symbols the font lacks (●) fall back to LiberationSans.
  - A material preset (`PressStart2P Shadow.mat`) adds a hard drop shadow and a thin outline, so text reads over any camera image.
- **Top bar:**
  - labels: SCORE (6 digits), STAGE, LIVES (pixel heart ×N) and POWER (a pixel fire-flower icon while held, otherwise "-")
  - connection status small at the top right (ONLINE / CONNECTING / OFFLINE)
  - the icons are sprites from `PixelArt.HeartIcon` and `PixelArt.FlowerIcon`
- **Centre title:** a big pop-in "STAGE N" when a stage starts; "STAGE CLEAR!" (gold) or "WARP!" when it ends.
- **Game-over screen:** a dimmed overlay with a red GAME OVER, the final score and stage, and "JUMP TO PLAY AGAIN", which blinks once the 2 s restart grace has passed.
- **Messages:** banner and feedback text are ASCII uppercase ("SCAN THE FLOOR 2.3/7.0 M²", "FLAG 2.6 M").
- **`ArtPreview`** now also renders `hud.png` and `hud_gameover.png` at iPhone 17 landscape resolution (2622×1206).
- **Tests:** gameplay 62/62; the simulation and network smoke tests pass.

### Effects — ✅ Editor-playtested (2026-09-30)
- **`Burst`:** a reusable one-shot particle burst made of child primitives, with a random spray, gravity, drag, spin and shrink. `GameContentSetup.BuildEffects` makes five variants in `Assets/Prefabs/Effects`:
  - **Sparkle:** gold, when a power-up is collected (`Pickup.collectEffect`)
  - **Poof:** white, when an enemy is stomped (`Goomba.stompEffect`)
  - **FireBurst:** when a fireball hits (`Fireball.impactEffect`)
  - **Smoke:** out of the barrel when the cannon fires (`BulletBillCannon.fireEffect`)
  - **Confetti:** at the flag on stage clear (`StageGoal.celebrationEffect`)
- **Defeats:** a stomp squashes the enemy flat with a puff. A fireball knocks it out as in the classic games: it flips upside down, pops up and falls (`Goomba` handles this; `Koopa` inherits it).
- **Flag:** the pennant and its emblems are grouped as `Goal/Pennant`, which slides down the pole (smoothstep, 1 s) when the goal is reached, by walking or by warp.
- **Damage (HUD):** a full-screen red flash fades over 0.5 s when a life is lost, and the LIVES heart blinks during the 2 s of invulnerability.
- **Tests:** gameplay 62/62; the simulation and network smoke tests pass. These effects are motion-only, so they're checked by playtesting rather than by the headless tests.

### Scanning phase — ✅ Editor-playtested (2026-09-30)
- **Scan target** raised from 7 to **12 m²** (`requiredFloorArea`, set by `GameContentSetup`), leaving room for the map and layout work. The small-room fallback (3 m² once growth stops for 15 s) still applies.
- **`AR Scan Plane`** (`Assets/Prefabs`, built by `ScanSetup`, Tools ▸ MARIO ▸ Configure Scan) is now ARPlaneManager's plane prefab, in place of ARFoundation's yellow `AR Default Plane`.
  - `ScanPlaneVisual` shows **only floor planes**, using `ARSessionManager.IsFloorPlane`: a transparent gold pixel grid in 25 cm squares, with a gentle pulse and a thin edge line.
  - Tables, beds and walls stay invisible.
  - Everything fades out once the scan completes, so stages stand on the real floor.
  - It fades through the colour's alpha, because ARPlaneMeshVisualizer switches the renderers on and off itself.
  - Plane UVs are plane-space metres, so the material tiling of 4 gives 25 cm squares.
- **HUD scan panel:** "SCAN THE FLOOR", a pixel progress bar showing the percentage of `requiredFloorArea` found, and rotating hints:
  - "sweep the camera slowly over the floor"
  - "only floor counts"
  - "small room? hold still", once the 3 m² minimum is reached
  - "READY!" pops in when the scan completes.
- **Development visuals are off by default:**
  - the cyan LiDAR debug mesh (back to `AR Mesh`; Tools ▸ MARIO ▸ Mesh Debug View ▸ On restores it)
  - the checkpoint debug sphere (`LevelGenerator.debugMarkerPrefab` cleared)
  - the LiDAR readout (`LidarDebugHUD.show` = false)
- **Tests:** gameplay 62/62; the simulation and network smoke tests pass. `SimulationSmokeTest` doesn't move the camera, so it finds no planes and doesn't exercise `ScanPlaneVisual`; the playtest covers that.

### Maps and layouts — ✅ Editor-playtested (2026-09-30)
The design is in `Docs/GAME_DESIGN.md`, "Maps and layouts".
- **Campaign:**
  - `StageDesigns.cs` (editor) holds four data designs, 1-1 to 1-4, each 2.5 × 4 m.
  - `GameContentSetup.BuildCampaignStage` turns each into `Assets/Stages/Campaign/Stage 1-N.prefab`.
  - `LevelGenerator.campaignStages` tries `CampaignStageFor(stage)` first, then the `stageTemplates` fallbacks, largest first.
  - `searchDistances` now reaches 3 m.
  - **Compression, after the first playtest:** in the simulated living room the 2.5 × 4 m campaign stages never fitted (600+ too-close and 250+ partly-off-floor rejections per search), so every stage fell back to Standard or Compact. `LevelGenerator` now tries the campaign stage at `campaignScales` {1, 0.85, 0.7, 0.55} first. Placement checks scale the footprint and goal via `_searchScale`, and `CompressLayout` scales the placed pieces' x/z and the footprint.
  - **Simulated room → Museum (2026-09-30).**
    - In LivingRoom_20ftx18.5ft, placement failed for lack of open floor. Oliver's session placed only Compact and Standard (958–1498 too-close and 610–855 off-floor rejections per search), and an automated scan there fitted 1-1 only at 55 %.
    - `SimulationSetup`'s default is now **Museum_69ftx48ft**, a round hall about 18 m across with mostly open floor.
    - Top-down renders were used to compare the Museum, Office_36ft, Backyard, Kitchen and Bedroom.
  - **Waiting for a bigger fit** (`biggerFitWaitSeconds`, `IsSeekingSpace`) was briefly added, then **removed** when the scan became player-confirmed (below).
  - **Scan confirmed by the player (Oliver's idea, 2026-09-30).**
    - `ARSessionManager` no longer completes on a fixed area: `requiredFloorArea` became the guidance-only `fullSizeFloorArea` (12 m², via FormerlySerializedAs), and the plateau fallback is gone.
    - It listens to `EventResolver.OnEventProcessed`: a jump (confidence ≥ 0.6) while scanning calls `ConfirmScan()`, allowed once `CanConfirmScan` (≥ `minFloorArea`, 3 m²).
    - The HUD scan panel shows the square metres found and a meter against 12 m²; once confirmable, "SCANNED EVERYTHING? JUMP TO START" blinks.
    - `LevelGenerator.searchDistances` reaches 6 m.
    - `SimulationSmokeTest -scan` confirms with a jump after 25 s of sweeping.
  - **Height-relative pieces (Oliver's request):**
    - `ARSessionManager` samples the camera height above `FloorHeight` during the scan, and `ConfirmScan` stores the median in `PlayerBody.EyeHeight` (clamped 0.9–2.1).
    - `EyeHeightPlacement` uses it (the live camera before calibration), with `fractionOfEyes` for coins (0.6) and bricks (0.78) and an offset for ? blocks (+0.35).
    - New gameplay checks cover eyes at 1.2 and 1.85 m, head clearance and the clamp.
    - The flag is now 1.8 m, with a pennant ×1.6.
  - **Floating stage (Museum):**
    - **Cause:** the Museum's simulated camera starts at 2.55 m, and pieces are placed relative to eye height, so ? blocks sat at about 2.85 m and coins at about 2 m above the floor.
    - **Fix 1:** `SimulationSetup.SetStartingEyeHeight` sets the chosen room's `m_CameraStartingPose` y to 1.55 m on every setup (the imported rooms are gitignored).
    - **Fix 2:** `EyeHeightPlacement` clamps the measured eye height to 1.0–1.9 m above the floor.
    - **Batch-mode quirk:** XR Origin's `CameraYOffset` (1.1176, tracking mode Device) lifts the camera but not the trackables, so the camera reads 2.67 m over a floor at 0. In the interactive Editor both are offset (floor 1.12). `-scan` logs the heights.
  - **`SimulationSmokeTest -scan`** steers the simulated camera (by reflection on `SimulationCameraPoseProvider.UpdatePose`) until a stage is placed, and passes if it's the campaign's 1-1.
    - In the Museum it's placed at the full 2.5 × 4 m after about 14 s (two runs out of two).
    - The room check now finds AR Foundation's internal `SimulationEnvironment` component, so it's no longer hard-coded to "LivingRoom".
  - `GameStateManager.StageName` / `NameOf` give the "1-2" names, with `StagesPerWorld` = 4.
- **Coins:** `Coin` (walk through, 0.3 m) calls `GameStateManager.AddCoin`: +100 score, `Coins` +1, and at 100 a 1-Up with the counter reset. A ? block's coin uses `AddCoin` too. The HUD has a COINS column (pixel coin icon ×NN).
- **Warp pipes → bonus room:**
  - `WarpPipe.OnEntered` (static event), with an `isExit` flag.
  - `LevelGenerator` hides `CurrentStage` (SetActive false) and instantiates `BonusRoom` at the stage's pose, then calls `Build(footprint.width, footprint.depth)`: a tiled underground floor, a wall of `UndergroundBlock`s, a grid of `Coin`s and an `ExitPipe`.
  - Exiting destroys the room and reactivates the stage. `TearDownStage` also removes a live bonus room.
  - The HUD banner reads "BONUS ROOM - SQUAT ON THE PIPE TO GO BACK".
  - The pass-3 stage-skip (`QueueWarp`) is gone.
- **`ArtPreview`** renders `stage_1-1.png` to `stage_1-4.png` and `bonus_room.png`.
- **Tests:** `GameplaySmokeTest` passes 69/69, with new checks for:
  - pipe enter and exit events, and that a pipe works once
  - the bonus room layout (coin count, exit pipe)
  - coin collection and the 100-coin 1-Up
  - stage names, and the campaign's order and looping
  - every campaign stage's size, flag and coins

  The simulation and network smoke tests pass too. The bonus-room swap itself, which runs in `LevelGenerator`, needs AR and so is covered by the playtest.

### Device testing without the wearables (2026-09-30)
- **`tools/mock_gateway.py --keys`:** single-key control from a terminal, with no Enter needed: 1 stand, 2 walk, 3 jump, 4 squat, 5 fireball, 6 stomp.
  - Holding 1, 2 or 6 resends every 0.1 s, like classifier windows (it uses the Mac's key auto-repeat).
  - 3, 4 and 5 send once per press; auto-repeats within 0.3 s are ignored.
  - `l` sends the next action at low confidence; `d` duplicates the last event; `k` kicks the connection; `q` quits.
  - The logic is in `KeyMapper`, which was unit-tested with simulated timings and tested end to end through a pty and a websocket client.
- **Demo tooling (2026-09-30), for the subcomponent test rubric:**
  - `--dummy [FILE] --every S` sends a random packet from `tools/dummy_packets.json` (real `GAME_EVENT` format; the file is re-read whenever it changes, so packets can be edited live), and `r` sends one in `--keys` mode.
  - Terminal output is colour-coded by message kind, with a coloured `[phoneN ip]` tag per connection.
  - The same lines, uncoloured, are appended to `Logs/mock_gateway.log` (`--log`).
  - The walk-through, diagrams (state engine, concurrency, FSMs), video script and fallbacks are in `Docs/DEMO_GUIDE.md`.
- **The run:** the player wears the phone in a head mount and holds the Mac's keyboard. Actions go over Wi-Fi to the phone exactly as the real gateway's would, with ACKs and haptics.
- **Headset mode (`HeadsetView`, on the HUD Canvas; added by `HUDSetup`):** for a phone VR viewer worn on the head.
  - The AR camera renders to a screen-sized RenderTexture, so AR Foundation's projection and camera-image mapping are untouched.
  - Each half of the screen shows its centre crop through `MARIO/HeadsetLens` (barrel pre-distortion `k1`/`k2`, `zoom`, `lensSeparation`, `cropWidth`). There's one rear camera, so both eyes see the same view.
  - The HUD moves into a centred safe frame at half its iPhone-17 scale.
  - Mode is Auto (on on a device, off in the Editor); a two-finger tap toggles it before the phone goes in the headset.
  - **Revised after the first headset test (2026-09-30).** Oliver found it disorienting, with depth "messed up", and much of the HUD missing after turning the phone to landscape.
    - **Scale:** each eye had shown the middle half of the view across a lens covering about 90°, roughly 3× magnification. Each eye now shows the **whole view in true proportions** at `eyeImageScale` 0.75 of its half (about 983 × 453 px on the iPhone 17): a window near real-world scale.
    - **Distortion:** lowered to k1 0.15, k2 0.05.
    - **HUD:** hudScale 0.9 in a 92 × 90 % safe frame.
    - **HUDSetup** now writes these values to the scene, because saved scene values override code defaults.
    - **Rotation:** the view is rebuilt whenever the screen size changes, and `BuildScript` restricts the app to landscape (either direction, no portrait). The app had been starting in portrait, which built the headset view for the wrong shape.
    - **Depth:** there's only one camera, so there's no true stereo; scale is the lever for comfort.
  - **Live tuning and nausea (2026-09-30).**
    - The mock gateway's `--keys` mode sends `DEV_TUNE` (= / - window size, ] / [ gap, 0 reset; documented as development-only in GATEWAY_PROTOCOL.md). `HeadsetView.Tune` applies it, stores it in PlayerPrefs and shows it on the HUD. The default window is 0.85.
    - Oliver still found no setting quite right and felt nauseous. No published VRG Pro viewer profile or iPhone 17 settings were found.
    - **Frame rate:** the project had never set one, so it ran at **30 fps** on iOS. It now runs at 60 (`ARSessionManager.Awake`: `targetFrameRate` 60, `vSyncCount` 0), matching the camera.
    - Builds are still Unity development builds with the Xcode Debug configuration; a Release build would lower overhead further.
  - `SimulationSmokeTest -scan -headset` saves the side-by-side frame to `Logs/headset_view.png`.
- **Build and install** (Unity closed):
  1. `BuildScript.BuildIOSLocal` (or `BuildIOSJohn`)
  2. `xcodebuild … -allowProvisioningUpdates build`
  3. `xcrun devicectl device install app --device <id> …/MARIO.app`

  The iPhone 17's device id is `E0885DCE-CD0A-5C36-B1ED-B2B5869A3B8B`.

### `HUDController.cs` — ⚠️ built; on-device check pending (2026-09-28)
On the Canvas. It reads ARSessionManager, LevelGenerator, GameStateManager, EventResolver and NetworkClient; it only reads them and doesn't change anything. It has four TMP elements, laid out by `HUDSetup.cs` (**Tools ▸ MARIO ▸ Configure HUD**):
- **Stats (top-left):** `Stage N   Score S   ♥♥♥`.
- **Connection (top-right):** a coloured dot, showing Gateway, Connecting… or Gateway offline.
- **Banner (top-centre):** the current state, polled every frame, highest priority first:
  1. tracking lost ("look around slowly")
  2. scan progress
  3. "Stage clear!"
  4. "No room here — move to a clearer area" (held 3 s after `OnPlacementFailed`)
  5. "Mapping the room…" or "Placing stage…"
  6. "Walk to the flag (X m)"
- **Feedback (centre):** transient messages from `EventResolver.OnEventProcessed` and score changes. They fade after 1.5 s.
  - Resolved: green `Stomp!  +100`. The stage-clear bonus shows as `+500`.
  - Gates 3–5 ("not permitted"): orange, with the reason, e.g. "Need a fire flower…", "nothing in range", "already used".
  - Gate 2 low confidence ("not registered"): grey "Move not recognised — try again".
  - Duplicates and Gate 1: silent. The banner already explains why nothing is accepted during scanning, tracking loss or transitions.

Layout: landscape, Canvas at constant pixel size, 180 px left margin to clear the Dynamic Island. The LiDAR debug readout moved to the bottom-left. `LidarSetup` and `StageLoopSetup` no longer depend on ScanHUD.
Not yet: game-over screen (lives are never lost yet); audio cues (AudioManager).

### `NetworkClient` — ✅ tested on the iPhone 17 against the mock gateway over Wi-Fi (2026-09-28)
The wire format is `Docs/GATEWAY_PROTOCOL.md`. It is **a proposal that still needs John's sign-off**. `GatewayMessages.cs` is the only file to change if the field names change.

**`GatewayConnection.cs`** (plain C#, no Unity dependency) handles the transport:
- TcpClient → SslStream (TLS 1.2) → the RFC 6455 upgrade handshake → `WebSocket.CreateFromStream`.
- **Cert pinning** compares the SHA-256 of the certificate's DER bytes against the pinned value. With a pin set, chain errors are ignored (the gateway cert is self-signed), but an unpinned CA-signed cert is still rejected.
- **Why not `ClientWebSocket`:** Unity's implementation ignores both `ServicePointManager.ServerCertificateValidationCallback` and `ClientWebSocketOptions.RemoteCertificateValidationCallback` (set by reflection). A pinned self-signed cert therefore always fails with `UNITYTLS_X509VERIFY_FLAG_NOT_TRUSTED`. This was verified; don't switch back.
- Receiving runs off the main thread into a `ConcurrentQueue`.

**`NetworkClient.cs`** (on GameManager):
- Drains that queue in `Update` and calls `EventResolver.Resolve`.
- Replies with an `EVENT_ACK` for **every** event, including duplicates, carrying the outcome, gate, reason and processing time.
- Duplicate detection stays in Gate 2 only.
- Sends `HAPTIC_CMD` from `OnEventProcessed`:
  - "success" on resolve, "denied" on gates 3–5, nothing on gates 1–2
  - fireball→wrist, stomp→ankle, anything else→both
- Reconnects with backoff starting at 0.5 s, doubling to a cap of 8 s, with ±20 % jitter. It sends `VISUALIZER_READY` after every connect.
- Exposes `State`, `IsConnected`, `LastError`, `EventsReceived`, `LastProcessingMs` and `OnConnectionStateChanged` for the HUD.

**`IOSPostProcess.cs`** adds `NSLocalNetworkUsageDescription` to Info.plist. Without it, iOS 14+ blocks LAN connections.

**Tooling:**
- `tools/mock_gateway.py` stands in for the gateway. It needs `pip install websockets`. It generates a self-signed cert into the gitignored `tools/certs/` and prints the fingerprint.
  - Modes: interactive, `--auto <s>`, and `--script "stomp,dup,low:jump,…" --exit-after-acks`.
  - Commands: `dup`, `newsession`, `kick` (tests reconnect).
- `NetworkSmokeTest.cs` is a headless end-to-end test. Run it with Unity closed and **no `-quit`**:
  `Unity -batchmode -projectPath . -executeMethod NetworkSmokeTest.Run -gatewayUrl wss://127.0.0.1:8765/ -pin <sha>`
  It builds a throwaway scene with a stomp target 1 m ahead, expects gates `6,5,2,3,2,4`, and exits 0 or 1.
- `NetworkSetup.cs` (**Tools ▸ MARIO ▸ Configure Network**, or run it headless with `-gatewayUrl`/`-pin`) adds the client to the scene.

**Results:**
- The smoke test passed: all six gate outcomes, their ACKs and haptics were correct. Local round trip was 1–5 ms, and processing on the phone side under 0.5 ms.
- A wrong pin was refused, and the log printed the fingerprint the gateway actually presented.
- Reconnect-with-backoff was seen when the gateway went away.

**On-device test** (iPhone 17 connected to the dev Mac over Wi-Fi, 2026-09-28):
- The first attempts timed out (errno 60) while the Local Network permission prompt was showing. After "Allow" was tapped, the retry connected by itself.
- `VISUALIZER_READY` arrived. All 6 scripted events were ACKed with the correct gates, with no target in view on the phone: 4, 2 (duplicate), 3, 2 (low confidence), 4 and 4.
- Every HAPTIC_CMD had the right target and pattern.
- After the gateway exited, the phone kept retrying with backoff (errno 61 = connection refused).
- Phone-side processing took 0.7–3.8 ms.
- **Round trip was about 100–185 ms, compared with 1–5 ms in the local headless test.** That is a large share of the 275 ms budget and needs investigating before integration. Likely causes are iPhone Wi-Fi power-save and the wait for the next frame's `Update` before the phone processes an event. To measure, have the gateway timestamp each event and compare with the ACK, and test on the team's router.

**Scene values:** `wss://Olis-MacBook-Pro.local:8765/` and the mock gateway's pin.
- The URL is the dev Mac's mDNS hostname, so it keeps working when DHCP changes the Mac's IP. That happened on 2026-09-29: .111 became .105.
- `GatewayConnection` resolves the name itself and tries IPv4 addresses first, because `.local` names also return IPv6 link-local addresses.
- **Change both the URL and the pin for John's gateway.** His laptop's `.local` name works the same way.

### `AudioManager` — 🟡 built 2026-09-30 (pass 3)
Synthesised sound effects, no music yet. See "Gameplay (pass 3)" above.

### Step 6 (stage completion loop) — ✅ tested on the iPhone 17 (2026-09-28)
- **`StageGoal.cs`** goes on a child of a stage prefab. It counts as reached when the player (the AR camera) stays within `reachRadius` (0.6 m, measured horizontally) for `dwellSeconds` (0.5 s). It fires `OnReached` once and exposes `PlayerDistance`. `TestStage` now has a "Goal" child at its far (+Z) edge, with a pole and a yellow flag (visual only, no colliders), added by `StageLoopSetup`.
- **The loop in `LevelGenerator`:**
  - Placing a stage finds its `StageGoal` and fires `OnStageStarted`.
  - Reaching the goal fires `OnStageCompleted`. After `transitionDelaySeconds` (1.5 s), the stage is destroyed, the anchor is removed (`TryRemoveAnchor`), a new checkpoint anchor is created at the player's current floor position and heading, and placement runs again.
  - Exposes `CurrentStage`, `CurrentGoal`, `IsTransitioning` and `StagesCompleted`.
  - Creating the checkpoint now retries if there's no floor under the player or the anchor call fails, and is guarded against running twice at once.
- **Fixed the checkpoint yaw:** it used the floor plane's arbitrary rotation. It now uses the camera's heading projected onto the floor, as the design intends, so the "forward arc" search really is in front of the player.
- **Session readiness in `GameStateManager`** (Gate 1) is now `isTracking && isStageActive`:
  - `SetSessionReady`/`SetSessionNotReady` (the method names are kept for the existing listeners) now only drive tracking.
  - `BeginStage()` / `CompleteStage()` drive the stage flag. `CompleteStage` also awards `stageClearBonus` (500) and increments `stageIndex`.
  - As a result, Gate 1 now correctly drops events during scanning, placement and stage transitions. Before this change it defaulted to open.
- Wiring: persistent UnityEvent listeners (LevelGenerator → GameStateManager), added by **Tools ▸ MARIO ▸ Configure Stage Loop** (`StageLoopSetup.cs`).
- The `ScanHUD` line shows `Stage N · Score S — walk to the flag (X m)` after the scan, then "placing stage…" or "Stage clear!".
- **Walking test on the device:** 2 full cycles, stage 1 → 2 → 3, score 1000. Goal detection, the clear bonus, stage removal, re-anchoring and the next placement all worked. Tracking loss and recovery during play were handled.
- **Bug found and fixed during that test:**
  - **What happened:** stage 2 was placed 0.74 m above the floor, on a bed or table.
  - **Cause:** the downward floor ray took the *first* plane hit, which is the highest one.
  - **Fix:** `TryGetFloorProjectedPose` now considers only HorizontalUp planes. The checkpoint takes the lowest plane; placement candidates must be within `floorHeightTolerance` (0.15 m) of the anchor's floor height. Built and installed; **not yet re-tested by walking.**
  - **Related, fixed 2026-09-28:** see "Floor-only scanning" below.
- **Placement fix (2026-09-28): a stationary player was clearing stages.** On the device, stages 5–19 cleared back to back while the player stood still. Stages were placed 0.5 m away at fixed world yaws, which put the goal within reach.
  - Candidates now face away from the player, so the goal is on the far side.
  - They must keep `playerClearance` (0.3 m) between the player and the footprint.
  - The goal must be at least `reachRadius + minGoalWalkDistance` (0.5 m) from the player.
  - `StageGoal` only arms after the player has been outside its reach.
  - Placement logs now give skip reasons: no-floor, too close to the player, obstructed.
  - Confirmed on the device: 10 stages cleared (stage 11, score 5000 in the screenshots), with flags appearing 2–4 m away.
- **Floor-only scanning** (2026-09-28, ✅ confirmed on the device; 1.57 m² of desk/bed excluded):
  - `ARSessionManager.IsFloorPlane(ARPlane)` decides what counts as floor. The scan area uses it, and so does `LevelGenerator`'s floor projection, so the two always agree.
    - ARKit's semantic plane classification wins when present: `Floor` counts, and any other label (Table, Seat, …) never does. It needs an A12 or newer chip, not LiDAR; the iPhone 17 qualifies.
    - On devices that support classification, **only** Floor-labelled planes count. Before this rule, a desk was counted as floor for the second or so before ARKit labelled it.
    - Only on devices without classification do unlabelled planes count, and then only within `floorHeightTolerance` (0.15 m) of `FloorHeight`.
  - `FloorHeight` is the height of the largest Floor-classified plane. Without one, it's the lowest unclassified plane of at least 0.25 m².
  - `ExcludedArea` reports the area rejected as not-floor, and it's logged alongside scan progress.
  - `requiredFloorArea` in the scene was lowered from 10 m² to **3 m²**: with beds and tables no longer counted, 10 m² can be unreachable in a bedroom.
- **TestStage resized** (2026-09-28): the root is now an empty pivot on the floor point.
  - A 30 cm "Block" child rests on the floor. The old 1 m cube was half-sunk into it.
  - The goal flag is 0.4 m past the block's centre line, 0.5 m tall, with `reachRadius` 0.35 m.
  - Footprint is 0.6 × 1.0 m, clearance 0.5 m.
  - Rebuilt by `StageLoopSetup`.
- **Not yet:**
  - A goal can't require clearing the stage's targets first.
  - Lives aren't lost anywhere, so the "loss of all lives" exit doesn't exist.
  - Reaching a goal while tracking is lost isn't specially handled. The camera pose freezes, so in practice it can't happen.

---

## 5. Known issues, hazards, and environment gotchas

These cost real debugging time once already — don't rediscover them:

- **The `arfoundation-samples` clone is a hazard.** It contains stale/extra assets (an `XRReferenceImageLibrary` from its Image Tracking sample) that broke a clean build with an opaque `actool` failure — Unity's `ARKitReferenceImageLibraryBuildProcessor` scans the **whole project** for these assets regardless of which scenes are in the build. Keep the working project separate from this repo; only use the repo for reference.
- **Unity 6 defaults to the new Input System exclusively.** Any script using legacy `UnityEngine.Input` (like `DebugActionInput`) throws `InvalidOperationException` until Player Settings → Active Input Handling is set to **"Both"** (requires an Editor restart).
- **`ARMeshManager` must be a child GameObject of XR Origin, not XR Origin itself** — unlike `ARPlaneManager`, `ARAnchorManager`, and `ARRaycastManager`, which all go directly on XR Origin's root. Unity throws a hierarchy error if you get this wrong.
- **`MeshCollider.convex` must stay unchecked** on the AR Mesh prefab — room geometry is non-convex, and Convex mode would collapse it into a simplified hull, losing gaps like the space under furniture. Static (non-Rigidbody) concave MeshColliders work fine with `Physics.CheckBox`/`OverlapSphere` queries.
- **The first install on a new phone won't launch** ("invalid code signature… not explicitly trusted") until you trust the developer profile on the phone: Settings ▸ General ▸ VPN & Device Management.
- **The yellow translucent planes with black edges** in the camera view are the `AR Default Plane` visualiser. The **cyan** overlay is the LiDAR debug mesh. The debug mesh sits right on real surfaces, so parts of it flicker or disappear under depth occlusion. That's expected.
- **The non-Pro iPhone 17 has no LiDAR.** `ARMeshManager` produces zero mesh data and `AROcclusionManager.descriptor.environmentDepthImageSupported` reports `Unsupported` on this device — permanently, not a transient bug. This blocks real-world validation of: `LevelGenerator`'s obstruction-rejection against actual furniture, and all of `RenderManager`'s occlusion behavior. Both need the team's iPhone 16 Pro Max (or another LiDAR-capable device) to properly test. Don't burn time trying to make these work on the non-Pro phone.
- **The checkpoint debug sphere is gone** (2026-09-30). Its default `SphereCollider` once caused a real `Physics.CheckBox` rejection during placement testing, so if you ever add a visible debug object, remove its collider or keep it off the ARMesh layer.
- **New/very recent iPhone hardware sometimes needs the latest Xcode** to mount a compatible on-device "developer disk image" — if you see `kAMDMobileImageMounterPersonalizedBundleMissingVariantError`, the fix is almost always updating Xcode (and macOS, if the App Store won't offer the update otherwise).
- **`AROcclusionManager`'s support flag needs polling, not a one-shot check** — `descriptor.environmentDepthImageSupported` can report `Supported.Unknown` for a few frames after the AR session starts before resolving to a real answer. `RenderManager.cs` already handles this correctly; don't "simplify" it back to a single `Awake()`-time check.
- **Design report Section 4 (Comms, owned by John) reportedly still attributes `HAPTIC_CMD` origin to the Ultra96** rather than the phone/gateway, per the last proofread — not something to fix in this codebase, but worth knowing when reading the report or integrating with comms, since the actual architecture (confirmed above) has the phone originating haptics.

---

## 6. Open coordination items with teammates

- **Xavier**: inference hop-rate alignment (~100ms windows, 90% overlap) needs to be matched against the BLE batch size on the wearable side — unresolved as of last note, relevant once real `GAME_EVENT` traffic starts arriving and timing/dedup assumptions in `EventResolver`/`NetworkClient` need to match reality.
- **John**: review and agree to `Docs/GATEWAY_PROTOCOL.md`. In particular:
  - field names
  - EVENT_ACK semantics (ACK every event, including duplicates)
  - the `HAPTIC_CMD` target and pattern values

  He also needs to provide his gateway certificate's SHA-256 fingerprint and the gateway URL. Separately, there's the Section 4 haptic-origin correction mentioned above.
- **General**: `EventResolver`'s `minConfidence` (currently 0.6, arbitrarily chosen for testing) has not been validated against what Xavier's actual classifier outputs for true positives/negatives — worth revisiting once real data is available.

---

## 7. Next steps

### 7.1 Now (while comms integrates)
1. ~~Prepare the live demo~~: done (`Docs/DEMO_GUIDE.md`, week 7 freeze, MD5 submitted).
2. **Integrate John's gateway.** He works from `Docs/COMMS_INTEGRATION.md`.
   - Get his URL and certificate fingerprint, and put them in `NetworkClient` (on GameManager).
   - Sync with `tools/push_to_integration.sh`.
   - Measure the send → ACK round trip on the lab Wi-Fi.
3. **Integrate Xavier's classifier.** Check `minConfidence` (0.6) and duplicate handling against real traffic. With action set v2 (below), the stomp-gap logic goes away.
4. **Keep the device build alive.** The free signing team expires installs every 7 days; re-sign as in memory/ios-resign-expired-app, or move to a paid or university team.
5. **Before anything goes public:** remove the copyrighted theme music (`Assets/Audio/`).

### 7.2 v2 (planned by Oliver 2026-10-08; implemented 2026-10-12, awaiting Editor playtest)

**Status (2026-10-12):** A, B and C are built. All headless tests pass: `GameplaySmokeTest` (96 checks), `SimulationSmokeTest` and `-scan`, and `NetworkSmokeTest` against the mock gateway (gates 6,5,2,4,2,6). `ArtPreview` renders were checked. Still to do: an **Editor playtest**, then a device check at the end, and **confirming the six label strings with Xavier and John** (the protocol is now v2). The design as built is in `Docs/GAME_DESIGN.md` § "v2: actions, World 2 and the ending".

What was built:
- **A (actions):**
  - `jump` `stomp` `squat` `throw` `punch` `clap`, all discrete; `IsContinuous`, `NoteStomp` and `SetMovementState` removed.
  - `GameStateManager.Held` (`HeldItem.None`/`Shell`) with `PickUp`/`ThrowHeld`; taking a hit drops it.
  - New `ThrownShell`: it slides along the floor, hits each target once, and adds a chain bonus. A Koopa takes two plain stomps.
  - Gate 3 reasons: `nothing to throw`, `hands full`, `fireballs recharging`.
  - `GatewayMessages.ProtocolVersion = 2`; keys, mock gateway, dummy packets and docs updated.
- **B (maps):**
  - World 2 (2-1 to 2-4) at 3 × 5 m: stair columns, tall pipes, coin arcs.
  - Themed scenery per stage (overworld, underground, sky, castle; 1-4 is now castle-themed).
  - **Bowser** (`Bowser.cs`): 5 health, fire breath you squat under, guards a locked axe (`StageGoal.Locked`/`EndsGame`).
  - Finishing the last stage is **GAME CLEAR** (`GameStateManager.ClearGame`, `LevelGenerator.FinishAfterCelebration`); the campaign no longer loops.
- **C (HUD and graphics):**
  - HELD slot, boss bar, "WORLD x-y" intro card with stage name and tip (`StageInfo`).
  - World-space score popups (`ScorePopup`).
  - GAME OVER / GAME CLEAR end screen with stats (coins, enemies, time, accuracy).
  - Held shell in view (`HeldItemView`), blob shadows, `Drift` clouds and `Flicker` torches, and a game-clear fanfare.

Not done from the plan below (candidates for later): fitting layouts to the scanned floor polygon / chained segments; enemies from several sides and a Paratroopa; LiDAR occlusion; ground dressing; credits; imported models and walk cycles; a preview fly-over.

**Gameplay polish (2026-10-21, after the comms integration test passed):**
- **Goal needs a jump:** `StageGoal` no longer dwells; `StageGoal.Current.TryJump()` from `EventResolver`'s jump grabs it within `reachRadius` (now 0.5 m for the flag and the axe). Result: Gate 6, reason `EventResolver.GoalReason` ("goal"), target the goal. HUD banner "JUMP TO GRAB THE FLAG!/AXE!" while `InReach`.
- **Iris wipe** (`ScreenTransition`, added to the HUD canvas by `HUDSetup`): `ScreenTransition.Run(atBlack, readyToOpen)` closes, runs the change, waits for the next scene (max 1.2 s), opens. Used by `LevelGenerator` between stages (opens when the next stage is placed) and for warp pipes in and out. Without a live instance (edit mode, tests) the change runs at once.
- **Sizes:** `GameContentSetup.ObjectScale` = 0.87 on every gameplay object's root (prefabs and stage pieces); `BulletBillCannon` sizes its column for a scaled cannon. Scenery rebuilt larger, outside the play area or overhead (`BlockWall` for cave and castle walls).
- Tests: `GameplaySmokeTest` 103 checks (goal jump, locked axe, 87 % scale, castle taller than the flag, wipe in edit mode), `SimulationSmokeTest` and `-scan` pass; `ArtPreview` renders checked.
- **`idle`** (the classifier's catch-all) is resolved state-only in every state and otherwise ignored.
- **Training round** (`Tutorial` on `Assets/Stages/TrainingStage.prefab`, built by `GameContentSetup.BuildTrainingStage`, wired to `LevelGenerator.trainingStage`): seven `Tutorial.Step`s, each a hidden station child shown in turn; done conditions `BlocksUsed` / `EnemiesCleared` / `DuckUnderBill` / `Goal`, 40 s timeout (not the flag), leftovers cleared, the flower given for the fireball step. `LevelGenerator._trainingPending` (true at launch and on `ResetToScan`, false after training or on `RestartLoop`) puts it first after a scan; the training goal calls `GameStateManager.EndTraining` (fresh state) instead of `OnStageCompleted`, raises `TrainingCompleted`, then the usual transition to 1-1. `GameStateManager.InTraining` (`StartTraining`): `TakeHit` costs nothing, `StageName` "0-1". `StageGoal.InReach` needs the goal active. HUD: step cards, `TRAINING n/7` banner, `WrongMoveTip`, `Tutorial.OnMessage`, `INF` lives, "TRAINING COMPLETE!". Tests: `GameplaySmokeTest` 136 checks (a full walkthrough), `SimulationSmokeTest -scan` (training first, played through, then a fresh 1-1) and `-scan -reset`; `ArtPreview` `stage_training.png`.
- **Hard reset** (`HardReset` on GameManager, added by `GameContentSetup`): look up ≥ 65° (counting continues down to 55°) for 5 s; HUD countdown after 1 s; Editor key R. It resets in place, not by reloading the scene (a reload also unloads XR Simulation's room): `LevelGenerator.ResetToScan` (with a generation counter, so an anchor still being created is dropped), `GameStateManager.ResetForScan` (a new game's state, no `OnGameRestarted`), `ARSessionManager.ResetScan`, `PlayerBody.Reset`, `AudioManager.StopMusic`, projectiles destroyed; `ARSession.Reset()` on device only (in the Editor it makes XR Simulation's mesh subsystem throw). Tests: `GameplaySmokeTest` 118 checks (gesture), `SimulationSmokeTest -scan -reset` (stage up → look up → back at the scan → rescan to 1-1).
- **Repeat filter** (after the full-integration playtest: the gateway forwards every inference): `EventResolver.IsRepeat` keeps one run per action (`repeatReleaseSeconds` 0.5, `repeatMaxRunSeconds` 1.2, injectable `Clock` for tests); repeats are Gate 2 `RepeatReason`, `ResolutionResult.IsRepeat`; `ARSessionManager` and the HUD ignore them. Mock gateway: scripted commands 0.6 s apart, `rep` sends a repeat at once. Tests: `GameplaySmokeTest` 113 checks; `NetworkSmokeTest` with `stomp,rep,rep,stomp,dup,punch,low:jump,squat,rep,idle` → `6,2,2,5,2,4,2,6,2,6`.

The original plan, for reference:

#### A. Action set v2: all actions discrete
New list, **six discrete actions**, each sent once per physical movement. There are no continuous actions any more: `stand` and `walk` are dropped, since moving is real walking that AR tracking already follows.

| v2 action | Replaces | Does |
|---|---|---|
| `jump` | `jump` | Hits ? blocks overhead; airborne window (Goombas pass under); starts/confirms the scan; restarts after game over |
| `stomp` | `stomp` (was continuous) | Defeats an enemy at the feet; a Koopa takes two stomp events |
| `squat` | `squat` | Ducks under Bullet Bills; enters warp pipes |
| `throw` | `fireball` | Throws a fireball (with the flower), or **throws the held item** (e.g. a Koopa shell) |
| `punch` | bare-handed `fireball` | Forward punch: breaks bricks in reach, and hits enemies at chest height |
| `clap` | — (new) | **Picks up** an item from the ground within reach, e.g. a Koopa shell, which is then held until thrown |

**Code that changes:**
- **`EventResolver.RunGates`:** cases `throw`, `punch` and `clap`. Remove `stand`/`walk`. Delete `IsContinuous` and the continuous-action special cases in `NetworkClient` (denied haptics), `HUDController` (silent refusals) and `AudioManager`.
- **`GameStateManager`:** a new `HeldItem` (none / shell, …), plus `PickUp` and `ThrowHeld`. Remove `NoteStomp`/`StompStartedAt`; `Koopa` takes simply two stomp events.
- **New gameplay:**
  - **Shell:** `Koopa` shell becomes `IsPickable` and is picked up by `clap`. When thrown, it slides along the floor and knocks out enemies in its path, as in Mario (a new `ThrownShell` projectile).
  - **HUD:** shows the held item in the POWER slot.
- **Haptics (`LimbFor`):** `throw`/`punch`/`clap` → `wrist`, `stomp` → `ankle`, others → `both`.
- **Input and tooling:** `EditorActionInput` key bindings (1 jump, 2 stomp, 3 squat, 4 throw, 5 punch, 6 clap); `tools/mock_gateway.py` `ACTIONS` and `--keys`; `tools/dummy_packets.json`.
- **Docs and protocol:**
  - **Wire format:** `GATEWAY_PROTOCOL.md` → protocol v2 (new `action` enum; continuous rules removed).
  - **Comms and design docs:** update `COMMS_INTEGRATION.md` §4.1, and `GAME_DESIGN.md`.
  - **Agree the exact label strings with Xavier (classifier) and John (gateway)**, e.g. `punch` vs `forward_punch`.
- **Tests:** `GameplaySmokeTest`; `NetworkSmokeTest` expected gates/script.

#### B. Maps: more, bigger, more 3D, and an ending
- **More stages:**
  - **Worlds:** at least 2 worlds × 4 stages, with themes (overworld, underground, castle).
  - **Mechanics:** each stage introduces one mechanic (clap and throw a shell, punch a brick wall, squat under cannon crossfire).
- **Bigger scale:**
  - **Use the floor actually scanned:** fit the layout to the scanned floor polygon instead of a fixed rectangle, or chain several anchored "segments" across the room.
  - **Fewer fixed sizes:** with that, the 100/85/70/55 % compression and the fallback templates become less necessary.
- **More 3D:**
  - **Height:** use the vertical space, with blocks at several heights, coins in arcs and multi-level brick walls.
  - **Enemies:** approach from several sides; a flying Paratroopa that needs a jump or throw.
  - **Hazards:** pipes enemies emerge from.
- **Background art / open-world feel:**
  - **Distant scenery:** hills, clouds, a castle silhouette, placed beyond the stage edges at a distance, so the real room feels like part of the Mushroom Kingdom.
  - **Occlusion:** use it where available (LiDAR), so real walls hide what's behind them.
  - **Ground dressing:** grass patches and path tiles on the floor between pieces.
- **Final stage and ending:**
  - **Last stage:** a castle with a boss (e.g. a Bowser-like enemy defeated by thrown shells or fireballs).
  - **Ending sequence:** "GAME CLEAR", the final score, stats and credits, then jump to play again.
  - **State:** `GameStateManager` gains a `GameClear` state; `LevelGenerator` stops at the last campaign entry instead of looping.

#### C. HUD and graphics
- **Pixel-art sprites:** for HUD items (held item, power-ups), plus a clearer layout for the headset view.
- **World-space popups:** "+100" floating at the hit point.
- **Better models:** low-poly imported models or improved primitives for enemies and items; animated enemies (walk cycles, death animations).
- **Stage transitions:** fade or iris wipe; a stage-intro card ("WORLD 1-2") with a short preview fly-over.

### 7.3 Suggested improvements (Claude, 2026-10-08)
Ordered by impact on how real and comfortable it feels:
1. **Ground shadows and light estimation.** Blob shadows under every enemy, block and pickup, plus ARKit light estimation to match the room's brightness and colour. This is the biggest single realism win for AR: without shadows, objects look like they float.
2. **Off-screen indicators.** Arrows at the screen edge pointing to the flag, approaching enemies and an incoming Bullet Bill, especially with the headset's narrow view.
3. **Spatial (3D) audio.** Enemies, cannons and pickups heard from their direction; a cue as an enemy approaches from behind.
4. **Tutorial stage 0.** Teaches each action with an on-screen prompt and waits for it. Doubles as a **classifier check** for the demo: shows each action being recognised, with its confidence.
5. **Play-area safety.** Warn when the player nears the edge of the scanned floor or furniture (like a VR boundary); pause if they leave it.
6. **End-of-game stats (exergame).** Reps per action, accuracy (recognised vs refused), active time, an estimated effort score. Useful for the report and for players.
7. **Adaptive difficulty.** Scale enemy speed and density from the player's recent performance (hits taken, time per stage), not just the stage number.
8. **Session robustness.** Save an ARKit world map after the scan, so a relaunch or tracking loss relocalises without rescanning. Re-measure eye height when a new player starts.
9. **Latency and telemetry.** Log each event's gateway timestamp vs ACK time on the phone and gateway; show a small latency readout in a debug overlay for integration testing.
10. **Build and release hygiene.** Bump `bundleVersion` per build; a paid or university Apple team (no 7-day expiry); profile on device (frame time, thermal) once the bigger maps and models land.

---

*This document reflects the state of the project as of the conversation it was generated from. If significant work has happened since, treat sections above as a strong starting point rather than perfectly current — verify against the actual codebase before assuming any given item's status.*
