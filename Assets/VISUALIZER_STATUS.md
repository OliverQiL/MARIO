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
- **iPhone 16 Pro Max** — the actual project device (has LiDAR), used by the team for final integration and anything LiDAR-dependent. Access has been limited during solo development.

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
| `NetworkClient` | Secure WebSocket to gateway, cert pinning, JSON (de)serialisation, dedup, `EVENT_ACK`, `HAPTIC_CMD`, reconnect w/ backoff | `System.Net.WebSockets` / `NativeWebSocket` |
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
│  └─ Main Camera                  (ARCameraManager, ARCameraBackground, TrackedPoseDriver)
│     └─ [RenderManager should live here — see §5, not yet attached]
└─ AR Meshes                        (child GameObject — ARMeshManager MUST live on a
                                     child of XR Origin, not on XR Origin itself; mesh
                                     prefab = empty GO + MeshFilter + MeshCollider,
                                     Convex unchecked, no MeshRenderer)

GameManager                         (plain GameObject, not under XR Origin)
├─ GameStateManager     [script]
├─ EventResolver         [script]
└─ DebugActionInput      [script]   (temporary — see §5)

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
showing live scan progress.
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

### `ScanHUD.cs` — ✅
Simple screen-space `TMP_Text` readout: shows `"Scanning floor: X / Y m²"` while scanning, switches to green `"Scan complete! (X m² scanned)"` once done. Polls `ARSessionManager`'s public properties every `Update()`. Confirmed rendering correctly on-device, including the "Dynamic Island in landscape = pill-shaped camera-active indicator" system UI that's easy to mistake for a bug but isn't.

### `LevelGenerator.cs` — ✅ (with caveats)
- **Step 2 (checkpoint anchoring)**: on `OnScanComplete`, raycasts straight down from the camera against `TrackableType.PlaneWithinPolygon` to find the floor, retains only yaw, creates an `ARAnchor` via `anchorManager.TryAddAnchorAsync(pose)` (the modern async AR Foundation anchor API). Optionally instantiates a debug marker prefab under the anchor.
- **Step 3 (stage generation)**: `TryPlaceStage()` orders `stageTemplates` (each requiring a `StageFootprint` component) by footprint area descending. For each template, searches the anchor's forward arc (±60°, configurable) at several distances (0.5–2.5m), falling back to the full circle if the arc yields nothing; at each candidate, tries several yaw rotations (0/90/180/270°); validates each with `Physics.CheckBox` against the candidate's footprint half-extents. On success, instantiates the prefab as a child of the checkpoint anchor. On total failure across all templates, fires `OnPlacementFailed` and retries automatically after `retryDelaySeconds` (default 2s) via `Invoke` — no tap-to-retry, consistent with the no-touch-input constraint.
- `[RequireComponent(typeof(ARSessionManager), typeof(ARAnchorManager), typeof(ARRaycastManager))]`. Lives on XR Origin.
- **Confirmed on-device**: anchor stays correctly pinned to the floor across movement (ARKit's anchor-correction working as expected). Stage placement succeeds; one real multi-retry-then-success case was observed and diagnosed as the floor plane's *polygon boundary* not yet having grown to cover the candidate point at the exact moment scanning completed (not a Physics.CheckBox rejection) — a few seconds of continued passive tracking resolved it. This is expected behavior, not a bug.
- **Caveats / not yet exercised**:
  - Only **one stage template** (`TestStage`) exists — the "fall back to a smaller compact template" tier has never actually been tested since there's nothing smaller to fall back to.
  - `Physics.CheckBox` has only ever encountered the **debug marker sphere's own default collider** as an obstruction (this actually happened once, correctly rejecting close-in candidates) — it has never been tested against real room geometry, since that requires the LiDAR mesh collider pipeline, which produces no data on the non-Pro test device.
  - Currently places **exactly one stage per checkpoint** and stops (a `_stagePlaced` guard) — chaining further stages is Step 6, not yet built.

### `StageFootprint.cs` — ✅
Metadata component: `width`, `depth`, `clearance` (metres), exposing `HalfExtents` (for `Physics.CheckBox`) and `FootprintArea` (for template ranking). Includes an editor-only Gizmo drawing the footprint box in the Scene view. Attached to `TestStage`.

### `GameStateManager.cs` — 🟡
Authoritative state: `score`, `lives` (default 3), `stageIndex`, `hasFireFlower`, and `isSessionReady`. Public methods `AddScore(int)`, `LoseLife()`, `SetFireFlower(bool)`, plus parameterless `SetSessionReady()` / `SetSessionNotReady()` wrappers specifically added for Inspector `UnityEvent` wiring. Lives on a standalone `GameManager` GameObject (not under XR Origin), matching the report's "no dependencies" listing. Tested via `EventResolver`'s Editor Play Mode tests — score increments, session-ready toggling, all confirmed via console logs.

### `IInteractable.cs` — ✅ (interface, nothing to "test" standalone)
`bool IsActionable { get; }`, `bool RespondsTo(string actionLabel)`, `void ResolveHit(string actionLabel, GameStateManager gameState)`. Anything `EventResolver` can target implements this.

### `EventResolver.cs` — 🟡 (most thoroughly validated module, zero device dependency)
Implements the full six-gate chain exactly as specified above. Contains the `GameEvent` struct (`actionLabel`, `confidence`, `eventId`, `timestamp`) — the stand-in shape for the eventual real `GAME_EVENT` payload; once `NetworkClient` exists, it deserializes into one of these and calls `Resolve()` directly, no other change needed. Rejection at each gate logs distinctly (`REJECTED @ Gate N (reason): 'action'`). `minConfidence` defaults to 0.6; Gate 2 dedup uses an in-memory, unbounded `HashSet<string>` of seen event IDs (fine for a test session, worth reconsidering for a long real one). Gate 3's capability check is currently a `switch` with only `"fireball"` mapped to `hasFireFlower`; everything else passes through. `[RequireComponent(typeof(GameStateManager))]`. Lives on `GameManager`.
- **Confirmed in Unity Editor Play Mode** (this logic has zero AR/device dependency and can be tested without building at all): a real target (a test cube with `TestInteractable`) was successfully resolved via a fake `"stomp"` action, incrementing score; a second `"stomp"` on the same target was correctly rejected at Gate 5 (already spent); `"fireball"` was correctly rejected at Gate 3 (no fire flower); moving the target out of range/facing correctly triggered Gate 4 rejection; manually toggling `IsSessionReady` off correctly triggered Gate 1 rejection. **Every gate has been individually exercised and confirmed correct.**
- Gate 6's resolution currently only logs and calls the target's `ResolveHit` — spawn effects, `AudioManager` cues, and `NetworkClient`'s `HAPTIC_CMD` are explicitly TODO-commented, pending those modules.

### `TestInteractable.cs` — 🟡 (test-only placeholder, not real game content)
Implements `IInteractable`. Responds to `"stomp"` by default (configurable string array), awards a configurable score (default 100), becomes permanently spent after one hit. Attached to a plain Scene cube, separate from the `TestStage` prefab — used purely to validate `EventResolver`.

### `DebugActionInput.cs` — 🟡 (explicitly temporary, will be deleted/replaced)
Binds keyboard keys 1–6 to fake `GameEvent`s for the six action labels, fixed confidence 0.95, auto-incrementing fake event IDs, feeding directly into `EventResolver.Resolve()`. Uses legacy `UnityEngine.Input`, which required switching **Active Input Handling to "Both"** in Player Settings (Unity 6 templates default to the new Input System exclusively, which throws on legacy `Input` calls). Only works with a real keyboard — i.e., in Unity Editor Play Mode, or on-device only if a Bluetooth keyboard is paired (untested). **This script's entire purpose is to be deleted once `NetworkClient` exists and produces real events.**

### `RenderManager.cs` — ⚠️ NOT YET TESTED
Occlusion configuration + camera background (the narrower, correct scope per the module table — NOT tracking-state, which is `ARSessionManager`'s job). Polls `AROcclusionManager.descriptor.environmentDepthImageSupported` every frame until it resolves from `Supported.Unknown` to a definite answer (this can take a few frames after session start, per AR Foundation's own docs — don't check only once in `Awake`). If supported: sets `requestedEnvironmentDepthMode = EnvironmentDepthMode.Best` and `requestedOcclusionPreferenceMode = OcclusionPreferenceMode.PreferEnvironmentOcclusion`. If unsupported: logs a warning (the expected outcome on the non-Pro test device). `[RequireComponent(typeof(AROcclusionManager))]`.
- **Must be attached to the actual AR Camera GameObject** (the one with `ARCameraManager`/`ARCameraBackground`, a child of XR Origin under Camera Offset) — **not** to XR Origin's root, unlike every other manager script in this project. This is a placement detail specific to this one script; get it wrong and it'll either fail to find its dependencies or configure the wrong camera.
- **Status as of this document**: code was written and delivered, but as of the last exchange, the developer had not yet confirmed access to LiDAR-capable hardware, so this has likely **not yet even been attached to the scene**, let alone tested. Confirm hardware access before spending more time here.

### `HUDController` — ❌ mostly not started
Only the narrow `ScanHUD.cs` (scan-progress-specific) exists. The report's actual `HUDController` scope — score/lives/stage display, connection status, degraded-mode messaging — has not been built. None of `EventResolver`'s six distinct rejection reasons, nor `LevelGenerator`'s `OnPlacementFailed` event, are currently wired to any player-visible feedback; they only reach `Debug.Log`.

### `NetworkClient` — ❌ not started
No WebSocket code exists at all. `DebugActionInput` is the explicit placeholder. When building this: it can be developed against a local mock WebSocket server (e.g. a small Python script) without waiting on John's real gateway, then pointed at the real URL once available. Needs: secure WebSocket connection, certificate pinning, `VISUALIZER_READY` handshake on connect, `GAME_EVENT` JSON deserialization into the existing `GameEvent` struct, `EVENT_ACK` replies, duplicate event ID handling (note: `EventResolver`'s Gate 2 already does dedup — decide whether that logic should move here, be duplicated, or stay put and have `NetworkClient` just feed everything through), `HAPTIC_CMD` sending, and reconnect-with-backoff.

### `AudioManager` — ❌ not started
No audio code exists at all.

### Step 6 (stage completion loop) — ❌ not started
`LevelGenerator` currently has no concept of a stage being "completed" (no goal-detection logic), no destruction of spent stage geometry/anchor, and no re-anchoring-and-looping-back-to-Step-3 logic. This needs `EventResolver`'s Gate 6 resolution path to actually signal stage completion somehow (not yet designed), and probably a `_stagePlaced` reset in `LevelGenerator` plus a new anchor-creation call.

---

## 5. Known issues, hazards, and environment gotchas

These cost real debugging time once already — don't rediscover them:

- **The `arfoundation-samples` clone is a hazard.** It contains stale/extra assets (an `XRReferenceImageLibrary` from its Image Tracking sample) that broke a clean build with an opaque `actool` failure — Unity's `ARKitReferenceImageLibraryBuildProcessor` scans the **whole project** for these assets regardless of which scenes are in the build. Keep the working project separate from this repo; only use the repo for reference.
- **Unity 6 defaults to the new Input System exclusively.** Any script using legacy `UnityEngine.Input` (like `DebugActionInput`) throws `InvalidOperationException` until Player Settings → Active Input Handling is set to **"Both"** (requires an Editor restart).
- **`ARMeshManager` must be a child GameObject of XR Origin, not XR Origin itself** — unlike `ARPlaneManager`, `ARAnchorManager`, and `ARRaycastManager`, which all go directly on XR Origin's root. Unity throws a hierarchy error if you get this wrong.
- **`MeshCollider.convex` must stay unchecked** on the AR Mesh prefab — room geometry is non-convex, and Convex mode would collapse it into a simplified hull, losing gaps like the space under furniture. Static (non-Rigidbody) concave MeshColliders work fine with `Physics.CheckBox`/`OverlapSphere` queries.
- **The non-Pro iPhone 17 has no LiDAR.** `ARMeshManager` produces zero mesh data and `AROcclusionManager.descriptor.environmentDepthImageSupported` reports `Unsupported` on this device — permanently, not a transient bug. This blocks real-world validation of: `LevelGenerator`'s obstruction-rejection against actual furniture, and all of `RenderManager`'s occlusion behavior. Both need the team's iPhone 16 Pro Max (or another LiDAR-capable device) to properly test. Don't burn time trying to make these work on the non-Pro phone.
- **The debug marker sphere has its own default `SphereCollider`** (from being created as a plain Unity 3D primitive) and sits at the checkpoint anchor. It has already caused a real, correct `Physics.CheckBox` rejection during placement search testing — worth remembering this exists when interpreting *any* future placement-search behavior, since it's easy to mistake for either a bug or evidence the physics validation is testing against real room geometry (it isn't, yet).
- **New/very recent iPhone hardware sometimes needs the latest Xcode** to mount a compatible on-device "developer disk image" — if you see `kAMDMobileImageMounterPersonalizedBundleMissingVariantError`, the fix is almost always updating Xcode (and macOS, if the App Store won't offer the update otherwise).
- **`AROcclusionManager`'s support flag needs polling, not a one-shot check** — `descriptor.environmentDepthImageSupported` can report `Supported.Unknown` for a few frames after the AR session starts before resolving to a real answer. `RenderManager.cs` already handles this correctly; don't "simplify" it back to a single `Awake()`-time check.
- **Design report Section 4 (Comms, owned by John) reportedly still attributes `HAPTIC_CMD` origin to the Ultra96** rather than the phone/gateway, per the last proofread — not something to fix in this codebase, but worth knowing when reading the report or integrating with comms, since the actual architecture (confirmed above) has the phone originating haptics.

---

## 6. Open coordination items with teammates

- **Xavier**: inference hop-rate alignment (~100ms windows, 90% overlap) needs to be matched against the BLE batch size on the wearable side — unresolved as of last note, relevant once real `GAME_EVENT` traffic starts arriving and timing/dedup assumptions in `EventResolver`/`NetworkClient` need to match reality.
- **John**: `HAPTIC_CMD` protocol/format on the wire hasn't been coordinated yet from the visualizer side — needed before `NetworkClient` can actually send it. Also the Section 4 haptic-origin correction mentioned above.
- **General**: `EventResolver`'s `minConfidence` (currently 0.6, arbitrarily chosen for testing) has not been validated against what Xavier's actual classifier outputs for true positives/negatives — worth revisiting once real data is available.

---

## 7. Suggested next steps, roughly in priority order

1. **Confirm LiDAR hardware access.** This unblocks `RenderManager` testing and real obstruction-testing for `LevelGenerator`'s placement search — both currently unverified against real geometry.
2. **Build `NetworkClient`.** Can start immediately against a local mock WebSocket server without waiting on John. This is the biggest remaining structural piece and is what finally lets `DebugActionInput` be deleted.
3. **Build Step 6 (stage completion loop)** in `LevelGenerator` — needed before there's any actual "game" rather than a single static stage.
4. **Expand `HUDController`** beyond scan progress — score/lives/stage/connection status, and surfacing `EventResolver`'s distinct gate-rejection feedback and `LevelGenerator`'s `OnPlacementFailed` to the player instead of just the console.
5. **Add a second, smaller stage template** with `StageFootprint` — needed to actually exercise `LevelGenerator`'s compact-template fallback tier, which has never fired.
6. **Build `AudioManager`** — currently entirely unstarted.
7. Once `NetworkClient` exists and Xavier's classifier is producing real events, revisit `minConfidence` and Gate 2's dedup strategy against real traffic characteristics.

---

*This document reflects the state of the project as of the conversation it was generated from. If significant work has happened since, treat sections above as a strong starting point rather than perfectly current — verify against the actual codebase before assuming any given item's status.*
