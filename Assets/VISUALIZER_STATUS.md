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

## 3. Project structure on disk

- A **fresh Unity 6 (URP) project** is the actual working project. Scripts live under `Assets/Scripts/`.
- A **separate clone of the `arfoundation-samples` GitHub repo** exists purely for reference/onboarding (this is where Unity/AR Foundation setup was first learned and verified). **Do not develop inside this repo** — see Known Issues below for why.
- Unity Player Settings: iOS Bundle ID, Camera Usage Description, target min iOS version, and Target Architectures = arm64 only are all configured. **Active Input Handling is set to "Both"** (see Known Issues).

### Scene hierarchy (as currently built)

```
AR Session                          (ARSession component)
XR Origin                           (XROrigin)
├─ ARPlaneManager                   (plane prefab = "AR Default Plane", horizontal-only detection)
├─ ARAnchorManager
├─ ARRaycastManager
├─ ARSessionManager     [script]
├─ LevelGenerator       [script]
├─ Camera Offset
│  └─ Main Camera                  (ARCameraManager, ARCameraBackground, TrackedPoseDriver,
│                                     AROcclusionManager, RenderManager)
└─ AR Meshes                        (child GameObject — ARMeshManager MUST live on a
                                     child of XR Origin, not on XR Origin itself; mesh
                                     prefab = "AR Mesh (Debug)" during LiDAR bring-up
                                     (AR Mesh + translucent cyan MeshRenderer); switch to
                                     plain "AR Mesh" via Tools ▸ MARIO ▸ Mesh Debug View ▸ Off.
                                     Both are on the ARMesh layer (6), Convex unchecked)

GameManager                         (plain GameObject, not under XR Origin)
├─ GameStateManager     [script]
├─ EventResolver         [script]
└─ NetworkClient         [script]   (gateway URL + pinned cert SHA-256 set in Inspector)

[Scene-only test objects, not prefabs, used for Play Mode testing:]
- A plain cube with TestInteractable attached (for EventResolver testing)

[Prefabs in Assets:]
- "AR Default Plane" — plane visualization prefab, assigned to ARPlaneManager
- AR Mesh prefab — assigned to ARMeshManager's Mesh Prefab field
- Debug marker — small sphere (~0.1 scale), instantiated under the checkpoint anchor
  purely for visual confirmation; has a default SphereCollider (see Known Issues)
- TestStage — 1×1×1m cube with StageFootprint (width=1, depth=1, clearance=1);
  the only entry in LevelGenerator's Stage Templates list so far

A screen-space Canvas + TextMeshPro text object exists with ScanHUD attached,
showing live scan progress, plus a "LidarDebug (TMP)" object with LidarDebugHUD.
```

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

### `ScanHUD.cs` — 🗑️ replaced by `HUDController` (2026-09-28)

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

### `DebugActionInput.cs` — 🗑️ deleted (2026-09-25)
Replaced by NetworkClient + `tools/mock_gateway.py` (interactive mode = type action names). Active Input Handling is still "Both"; it could go back to the new Input System only.

### `RenderManager.cs` — ✅ (rewritten 2026-09-24; the earlier version was never saved in the project)
Occlusion configuration + camera background (NOT tracking-state, which is `ARSessionManager`'s job). Lives on **Main Camera** next to `AROcclusionManager`, not on XR Origin. Checks `descriptor.environmentDepthImageSupported` every frame until it stops being `Unknown`, then:
- if supported: environment depth `Best`, temporal smoothing on, `PreferEnvironmentOcclusion`
- if unsupported: logs a warning and disables the `AROcclusionManager`

Human segmentation is always off. Exposes `IsResolved`, `OcclusionSupported`, `OcclusionActive` and the `OnOcclusionStateResolved(bool)` event.
- **Confirmed on the LiDAR iPhone**: the HUD reports occlusion active, and a person and a desk correctly hide virtual content behind them.

### `LidarDebugHUD.cs` — ✅ (temporary)
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
- `BuildScript.cs` — **Tools ▸ MARIO ▸ Build iOS** (or `-executeMethod BuildScript.BuildIOS`). Writes a development build of the Xcode project to `ios-build/`, which is now gitignored.
- Headless build → device loop (Unity must be closed):
  ```
  Unity -batchmode -quit -projectPath . -buildTarget iOS -executeMethod BuildScript.BuildIOS
  xcodebuild -project ios-build/Unity-iPhone.xcodeproj -scheme Unity-iPhone -configuration Debug \
    -destination generic/platform=iOS -derivedDataPath <dd> -allowProvisioningUpdates build
  xcrun devicectl device install app --device <id> <dd>/Build/Products/Debug-iphoneos/MARIO.app
  xcrun devicectl device process launch --device <id> --console com.cg4002.mario
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
- **Simulated room:** `LivingRoom_20ftx18.5ft`, from Unity's official XR Simulation sample environments.
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

### `AudioManager` — ❌ not started
No audio code exists at all.

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
- **The debug marker sphere has its own default `SphereCollider`** (from being created as a plain Unity 3D primitive) and sits at the checkpoint anchor. It has already caused a real, correct `Physics.CheckBox` rejection during placement search testing — worth remembering this exists when interpreting *any* future placement-search behavior, since it's easy to mistake for either a bug or evidence the physics validation is testing against real room geometry (it isn't, yet).
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

## 7. Suggested next steps, roughly in priority order

1. ~~Confirm LiDAR hardware access~~ — done 2026-09-24. Occlusion and mesh-based placement work on the LiDAR iPhone. Before release, switch the mesh debug view off and delete `LidarDebugHUD`.
2. ~~Build `NetworkClient`~~: done, and tested on the iPhone 17 on 2026-09-28. What's left: investigate the ~100 ms Wi-Fi round trip, then integrate with John's real gateway (his URL and certificate pin).
3. ~~Build Step 6 (stage completion loop)~~: done and tested on the device on 2026-09-28. Re-check the furniture-plane fix on the next device session.
4. ~~Expand `HUDController`~~: built on 2026-09-28; the on-device check is pending.
5. ~~Add a second, smaller stage template~~: `CompactStage` added 2026-09-29. Next: check the fallback fires on the device in a cramped spot. After that comes gameplay pass 2 (Koopa, Bullet Bill + squat, 1-Up), per `Docs/GAME_DESIGN.md`.
6. **Build `AudioManager`**: deferred to a later enhancement (decided 2026-09-29).
7. Once `NetworkClient` exists and Xavier's classifier is producing real events, revisit `minConfidence` and Gate 2's dedup strategy against real traffic characteristics.

---

*This document reflects the state of the project as of the conversation it was generated from. If significant work has happened since, treat sections above as a strong starting point rather than perfectly current — verify against the actual codebase before assuming any given item's status.*
