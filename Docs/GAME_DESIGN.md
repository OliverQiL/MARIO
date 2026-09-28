# Game Design — Actions, Stage Elements, Enemies

Status: agreed 2026-09-29. This extends design report §6.4, "Enabling AR of the various events". Pass 1 is implemented; passes 2–3 are planned. All numbers are tunable in the Inspector. The values below are the defaults.

## Constraints that shape the design
- **Everything is physical.** The player can't really leap onto things or duck quickly, so all interaction happens around them at human scale.
- **The latency budget is 275 ms, measured from when the action *finishes*.** Anything that asks "is the player ducking or airborne?" therefore uses a **grace window** (about 1 s) that starts when the event *arrives*. Threats are telegraphed well before they hit.
- **Continuous vs discrete.** `stand`, `walk` and `stomp` are continuous: they are re-sent every classification window. `jump`, `squat` and `fireball` fire once per physical action. The gateway makes this distinction; the phone also deduplicates on `event_id`. Continuous actions never trigger "denied" haptics, which would otherwise repeat every window.
- **No touch input, no markers.** Restarting after a game over is also done with an action.

## Action → outcome
Every action still passes the six-gate chain (session → validity → capability → spatial → target state → resolution).

| Action | Outcome | Targeting (Gate 4) |
|---|---|---|
| `stand` / `walk` | Updates the player's movement state. No target, and no HUD feedback. | — |
| `jump` | The player is **airborne** for 1.0 s: a Goomba reaching them does no damage and passes by. If a "?" block is overhead, the jump hits it. | Optional: a block **above eye level** within **0.5 m** horizontally |
| `squat` | The player is **ducked** for 1.0 s: projectiles at head height miss (pass 2). Later: enter a warp pipe (pass 3). | — |
| `fireball` **with** a fire flower | Throws a fireball in the direction the camera faces (aim by looking). It hits the first enemy or brick along its path. **Gate 3:** at most 2 fireballs can be in flight. | — (the projectile finds its own target) |
| `fireball` **without** a flower | A **bare-handed strike**, per the report: it breaks a brick block. | A brick at chest height, within **0.8 m** and **±35°** of facing |
| `stomp` | A downward attack that defeats an enemy at the player's feet. | An enemy **below eye level** within **0.7 m** and **±70°** of facing |

Game over: the gate chain is suspended, and a `jump`, at least 2 s after the game over, restarts the game.

## Stage elements (pass 1)
- **"?" block.** Floats **0.3 m above the player's eye height**, measured when the stage appears. A `jump` underneath hits it:
  - if the player has no fire flower, a **fire flower** drops to the floor below the block
  - otherwise a **coin** pops out (+100)

  It then becomes spent: brown, and it can't be hit again (Gate 5, "already used").
- **Brick block.** Sits at **chest height** (eye height − 0.35 m). A bare-handed strike or a fireball breaks it for +50.
- **Fire flower.** Collected by walking to it (within 0.45 m). It enables fireball projectiles and is **lost when the player takes damage**.
- **Flag.** Each stage ends at a flag, whatever state the enemies are in (report §6.4). The player reaches it by standing within 0.35 m of it for 0.5 s.

## Enemies
Enemies spawn from a spawner on the stage, within a bounded distance of the player, and walk straight towards the player (report §6.4). They freeze while the session isn't ready, for example when tracking is lost.

| Enemy | Pass | Behaviour | Defeat | Damage |
|---|---|---|---|---|
| **Goomba** | 1 | Walks along the floor at 0.25 m/s | `stomp` or fireball (+100) | Contact within 0.35 m while the player isn't airborne. The Goomba is removed after the hit. If the player **is** airborne, it passes by, keeps going for 1.2 s, then turns back. |
| **Koopa** | 2 | Same as a Goomba | A `stomp` turns it into a harmless shell; a second stomp clears it (+200). A fireball defeats it outright. | Contact while it's still walking |
| **Bullet Bill** | 2 | Flies at head height from about 5 m away. A warning marker appears first, giving about 3 s to react. | Can't be defeated | Passes through the player's position while the player isn't **ducked** |

## Lives and game over
- The player starts with **3 lives**.
- Taking damage costs one life and the fire flower, and gives **2 s of invulnerability**.
- At 0 lives it's **game over**: the stage and enemies are removed and every action is ignored, except `jump` after 2 s, which restarts. Restarting resets score, lives and stage, then re-anchors and places a new stage.

## Stage templates
LevelGenerator tries larger templates first. A placement is valid only if:
- there is **detected floor under all four footprint corners and under the flag** (so the flag can't hang over furniture)
- the footprint keeps clear of the player, and the flag takes deliberate walking to reach
- on LiDAR devices, the space is clear of the room mesh

Each template's +Z axis points away from the player.

| Template | Footprint | Contents |
|---|---|---|
| **Standard** | 1.2 × 2.0 m | "?" block (near side), brick (side), Goomba spawner (far side, 2 Goombas, one at a time), flag (far end) |
| **Compact** | 0.6 × 1.0 m | "?" block, flag. This is the fallback for cramped spaces. |

## Feedback
- **Haptics** (`HAPTIC_CMD`, see `GATEWAY_PROTOCOL.md`):

  | When | `pattern` | `target` |
  |---|---|---|
  | A hit or a projectile impact | `success` | the limb that did it |
  | A rejection at gates 3–5 for a discrete action | `denied` | the limb that did it |
  | The player takes damage | `damage` | `both` |
  | State-only actions (stand, walk, squat, a jump that hits nothing) | none | — |

- **HUD:**
  - hits and score gains in green
  - "not permitted" rejections in orange, with the reason
  - "not recognised" in grey
  - damage in red ("Ouch! −1 life")
  - "GAME OVER — jump to play again" as the top banner

## Later passes
- **Pass 2:** Koopa, Bullet Bill with squat-dodging, a 1-Up mushroom from "?" blocks (+1 life).
- **Pass 3:** warp pipes (squat while standing on one), difficulty that scales with stage number, audio (AudioManager), and art in place of primitives.
