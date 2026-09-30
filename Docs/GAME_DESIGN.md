# Game Design — Actions, Stage Elements, Enemies

Status: agreed 2026-09-29. This extends design report §6.4, "Enabling AR of the various events". Passes 1 and 2 are implemented (pass 2 on 2026-09-30: its details under "Pass 2 details" were chosen during implementation and reviewed by Oliver). Pass 3 was implemented and playtested on 2026-09-30: see "Pass 3 details". The warp pipe is a placeholder until real maps exist (see "Later passes"). All numbers are tunable in the Inspector. The values below are the defaults.

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
| `squat` | The player is **ducked** for 1.0 s: projectiles at head height miss (pass 2). Standing on a warp pipe, it enters the pipe (pass 3). | Optional: a warp pipe within **0.35 m** horizontally |
| `fireball` **with** a fire flower | Throws a fireball in the direction the camera faces (aim by looking). It hits the first enemy or brick along its path. **Gate 3:** at most 2 fireballs can be in flight. | — (the projectile finds its own target) |
| `fireball` **without** a flower | A **bare-handed strike**, per the report: it breaks a brick block. | A brick at chest height, within **0.8 m** and **±35°** of facing |
| `stomp` | A downward attack on an enemy at the player's feet. A Koopa needs two *separate* stomps (see Pass 2 details). | An enemy **below eye level** within **0.7 m** and **±70°** of facing |

Game over: the gate chain is suspended, and a `jump`, at least 2 s after the game over, restarts the game.

## Stage elements (pass 1)
- **"?" block.** Floats **0.3 m above the player's eye height**, measured when the stage appears. A `jump` underneath hits it:
  - it drops a **random power-up**: a **fire flower** or a **1-Up mushroom** (+1 life, pass 2), 50/50
  - the power-up falls from the block and lands 0.5 m past it, on the side away from the player (straight down it would land on the player and be collected unseen)
  - if the roll would be wasted (a flower while the player holds one, or a 1-Up at the life cap), it drops the other one
  - if both would be wasted, a **coin** pops out instead (+100)

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
- The player starts with **3 lives**. 1-Ups can raise that to at most **8**.
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
| **Large** (pass 3) | 2.0 × 3.0 m | 2 "?" blocks, 2 bricks, enemy spawner (3 enemies: Goomba, Koopa, Goomba), Bullet Bill cannon, warp pipe (near half), flag (far end) |
| **Standard** | 1.2 × 2.0 m | "?" block (near side), brick (side), enemy spawner (far side: a Goomba, then a Koopa, one at a time), Bullet Bill cannon (far corner), warp pipe, flag (far end) |
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

## Pass 2 details
These fill gaps in the agreed table above. They were chosen while implementing and are all tunable in the Inspector.
- **Koopa, two stomps.** `stomp` is re-sent every classification window, so one held stomp would shell and clear a Koopa within 0.1 s. A stomp therefore only clears a shell if its *motion* started after the shell appeared. A new motion is a stomp event arriving more than **0.3 s** after the previous one (`GameStateManager.newStompGapSeconds`). Until then the shell is at Gate 5, which is silent for continuous actions.
  - **To check with Xavier:** does the classifier's stomp stream actually pause for 0.3 s between two real stomps?
- **Koopa scoring.** The first stomp scores nothing; clearing the shell or a fireball kill gives +200. A shell is stationary and harmless, and it stays until it's cleared or the stage ends.
- **Bullet Bill cannon.**
  - **The tower:** it stands on the Standard stage's far corner. Its head is at the player's head height (0.1 m below the eyes, set when the stage appears) and keeps turning to face the player at 120°/s.
  - **Firing:** up to 2 Bills per stage, one at a time: the first 6 s into the stage, then every 10 s.
  - **Warning:** before each shot the muzzle glows and the HUD shows "Bullet Bill! Squat!" for 1.5 s. The cannon then fires along its barrel.
  - **Too close:** it holds fire while the player is within 1.2 m of it, which would leave no time to react.
- **Bullet Bill.**
  - **Flight:** it flies in a **straight line** at 2 m/s. It doesn't home in on the player. From the far corner the player is usually 2–3 m away, so there's about 2.5–3 s to react in all.
  - **Hit:** it hits if, as it passes, the player is within 0.35 m of its path and not ducked. Stepping aside also dodges it.
  - **Can't be defeated:** fireballs and stomps ignore it.
  - **Compact stages** have no cannon and no enemies.
- **Power-ups:** see "Stage elements": a random flower or 1-Up, with a life cap of 8. This replaced an earlier rule where a 1-Up only came when the player held a flower and was below 3 lives.

## Pass 3 details
Defaults chosen and playtested on 2026-09-30. All are tunable in the Inspector.
- **Bigger layouts.**
  - A **Large** template (2.0 × 3.0 m = 6 m²) is tried before Standard and Compact.
  - The scan target rose from 4 to **7 m²**, so a Large stage can fit, then to **12 m²** (2026-09-30) to leave room for larger maps.
  - **Small-room fallback:** once the scanned floor stops growing (less than 0.1 m² in 15 s), the scan accepts what it has, provided that's at least 3 m². Placement then falls back to Standard or Compact. Without this, a room smaller than the target would never finish scanning.
- **Warp pipe.**
  - A green pipe on the floor of Large and Standard stages, present on about half of them (a 50 % roll when the stage appears).
  - **Squatting** while standing within 0.35 m of it enters it: the stage clears at once (+500, the normal clear bonus), the stage counter **skips one stage**, and the HUD shows "Warp!".
  - It's a shortcut: fewer enemies faced now, but a harder stage next.
  - **Placeholder:** this will be redesigned (see "Later passes").
- **Difficulty by stage number** (stage 1 is the baseline; `Difficulty` on GameManager):

  | | Per stage | Cap |
  |---|---|---|
  | Extra enemies per spawner | +1 every 2 stages | +3 |
  | Enemy walking speed | +10 % | ×1.8 |
  | Extra cannon shots | +1 every 3 stages | +2 |
  | Time between cannon shots | −8 % | ×0.5 |
  | Bullet Bill speed | +5 % | ×1.5 |
- **Audio** (`AudioManager` on GameManager).
  - Chiptune-style sound effects are synthesised in code at startup, so there are no audio files to license:
    - jump, coin, power-up appears, power-up, 1-Up, stomp, kick (a fireball hitting an enemy), brick break, fireball
    - damage, the Bullet Bill warning, cannon fire, stage clear, warp, game over
    - a soft "bonk" when a discrete action is refused (never for continuous actions)
  - There's no background music yet.
- **Art:** still primitives. It's deferred until after this playtest, so it can target what reads worst in the room (as the cannon restyle did).

## Later passes
- **Pass 3** (done, see above): warp pipes, difficulty scaling, audio, larger layouts. Art in place of primitives is still to come.
- **Warp pipes, redesigned** (Oliver, 2026-09-30; to build when real maps and stages are made). A warp pipe should work like Mario's:
  - it isn't a stage clear
  - entering it **teleports the player to a separate, temporary area** (a bonus room, for example), which has its own **exit pipe**
  - the exit pipe **returns the player to the main stage**, and play continues there
  - This needs a second placed layout (or a swap of the current one) and a return point on the main stage. That's why it waits for the map work.
