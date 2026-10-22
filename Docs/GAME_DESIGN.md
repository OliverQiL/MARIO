# Game Design — Actions, Stage Elements, Enemies

Status: agreed 2026-09-29. This extends design report §6.4, "Enabling AR of the various events". Passes 1 and 2 are implemented (pass 2 on 2026-09-30: its details under "Pass 2 details" were chosen during implementation and reviewed by Oliver). Pass 3 was implemented and playtested on 2026-09-30: see "Pass 3 details". **v2 (2026-10-12):** the action set is now six discrete actions, World 2 and Bowser's castle end the game, and the HUD has new elements: see "v2: actions, World 2 and the ending" at the end, which supersedes the older notes on `stand`, `walk`, `fireball` and continuous actions. All numbers are tunable in the Inspector. The values below are the defaults.

## Constraints that shape the design
- **Everything is physical.** The player can't really leap onto things or duck quickly, so all interaction happens around them at human scale.
- **The latency budget is 275 ms, measured from when the action *finishes*.** Anything that asks "is the player ducking or airborne?" therefore uses a **grace window** (about 1 s) that starts when the event *arrives*. Threats are telegraphed well before they hit.
- **Every action is discrete (v2).** `jump`, `stomp`, `squat`, `throw`, `punch` and `clap` each arrive once per physical action. The phone also deduplicates on `event_id`. (v1 had continuous `stand`, `walk` and `stomp`, re-sent every window.)
- **No touch input, no markers.** Restarting after a game over is also done with an action.

## Action → outcome
Every action still passes the six-gate chain (session → validity → capability → spatial → target state → resolution).

| Action | Outcome | Targeting (Gate 4) |
|---|---|---|
| `jump` | The player is **airborne** for 1.0 s: a Goomba reaching them does no damage and passes by. If a "?" block is overhead, the jump hits it. | Optional: a block **above eye level** within **0.5 m** horizontally |
| `squat` | The player is **ducked** for 1.0 s: projectiles at head height miss (pass 2). Standing on a warp pipe, it enters the pipe (pass 3). | Optional: a warp pipe within **0.35 m** horizontally |
| `throw` | **Holding a shell:** throws it; it slides along the floor the way the player faces for 2.5 s at 3 m/s and knocks out every enemy it touches (+100 chain bonus per extra hit). **Otherwise, with a fire flower:** throws a fireball the way the camera faces (aim by looking); it hits the first enemy or brick on its path. **Gate 3:** at most 2 fireballs in flight; nothing to throw without a shell or a flower. | — (the projectile finds its own targets) |
| `punch` | A forward punch: breaks a brick block (+50) or hits Bowser. | A target at chest height, within **0.8 m** and **±35°** of facing |
| `clap` | Picks up a Koopa shell from the floor; the player holds it (HUD: HELD, and a small shell in the corner of the view) until they throw it. **Gate 3:** hands already full. Taking damage drops it. | A shell **below eye level** within **0.8 m** and **±60°** of facing |
| `stomp` | A downward attack on an enemy at the player's feet. A Koopa needs two stomps: the first shells it. | An enemy **below eye level** within **0.7 m** and **±70°** of facing |

Game over and game clear: the gate chain is suspended, and a `jump`, at least 2 s after, starts a new game.

## Stage elements (pass 1)
- **"?" block.** Floats **0.3 m above the player's eye height**, measured when the stage appears. A `jump` underneath hits it:
  - it drops a **random power-up**: a **fire flower** or a **1-Up mushroom** (+1 life, pass 2), 50/50
  - the power-up falls from the block and lands 0.5 m past it, on the side away from the player (straight down it would land on the player and be collected unseen)
  - if the roll would be wasted (a flower while the player holds one, or a 1-Up at the life cap), it drops the other one
  - if both would be wasted, a **coin** pops out instead (+100)

  It then becomes spent: brown, and it can't be hit again (Gate 5, "already used").
- **Brick block.** Sits at **chest height** (eye height − 0.35 m). A bare-handed strike or a fireball breaks it for +50.
- **Fire flower.** Collected by walking to it (within 0.45 m). It enables fireball projectiles and is **lost when the player takes damage**.
- **Flag.** Each stage ends at a flag, whatever state the enemies are in (report §6.4). The player grabs it by **jumping within 0.5 m of it** (since 2026-10-21; walking up to it isn't enough). The HUD shows "JUMP TO GRAB THE FLAG!" while they're in reach.

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
  | A rejection at gates 3–5 | `denied` | the limb that did it |
  | The player takes damage | `damage` | `both` |
  | State-only actions (a plain squat, a jump that hits nothing) | none | — |

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

## Scanning the room (2026-09-30, Oliver's design)
- **No fixed target.** The player walks round and scans all the floor they want to play on.
- **The HUD** shows the floor found so far, as square metres and a meter against the ~12 m² a full-size stage likes. Once there's enough to play (**3 m²**, the smallest stage), it asks the player to **jump when they've scanned everything**. The jump is the confirmation: no touch input, like restarting after game over.
- **Placement then uses that scanned space:**
  - it searches the whole room out to 6 m from the player
  - it prefers the campaign stage at full size, then compressed (85 / 70 / 55 %)
  - more scanned floor means bigger stages
- This replaced the fixed 4 → 7 → 12 m² target and the small-room fallback.
- **Player height.**
  - While scanning, the camera's height above the floor is sampled four times a second. When the scan is confirmed, the median becomes the player's **eye height** (`PlayerBody`), clamped to 0.9–2.1 m. The HUD shows it.
  - Every stage is built from that one number, whatever the player is doing when the stage appears:
    - **coins** at 60 % of eye height (waist)
    - **bricks** at 78 % (chest)
    - **? blocks** 0.35 m above the eyes, so the block's bottom clears the top of the player's head and they reach it by jumping
  - A shorter player therefore gets lower pieces, and a taller player higher ones.
- **The flag** is 1.8 m tall with a larger pennant, so it's easy to spot across a room.

## Maps and layouts (2026-09-30)
Decided with Oliver: a hand-designed campaign, full-size stages of about 2.5 × 4 m, collectable coins with a counter and a 1-Up every 100, and layouts that work in both labs and homes.
- **The campaign.**
  - It has four stages, 1-1 to 1-4, written as data in `Assets/Editor/StageDesigns.cs`, one prefab each in `Assets/Stages/Campaign`. Each design is a list of pieces (? block, brick, coin, spawner with its enemy sequence, cannon, warp pipe with a chance to appear, flag) at floor positions.
  - Stage N plays design (N−1) mod 4. After 1-4 comes 2-1, the same layouts again, with Difficulty making each round harder.
  - Stages are named like Mario's ("1-2"), in the HUD and the stage titles.
- **The stages.**

  | Stage | Theme |
  |---|---|
  | **1-1** | First steps: a line of coins, a ? block between two bricks, one Goomba, the flag |
  | **1-2** | Koopa road: two ? blocks, a Goomba then a Koopa, a zigzag of coins, and a warp pipe (always there) |
  | **1-3** | Bullet alley: two cannons, one firing across the path; coins lead the way; a warp pipe half the time |
  | **1-4** | Fortress: two spawners (3 + 1 enemies), a cannon, a ring of coins, and a warp pipe |
- **Fitting the room.** A clear 2.5 × 4 m of detected floor right in front of the player is rare in a furnished room: the 12 m² scan counts floor anywhere. (In the simulated living room, the campaign stages never fitted, which is why this was added.)
  - The campaign stage is therefore tried at **100 %, 85 %, 70 % and 55 %** of its size (`campaignScales`). Its pieces move closer together but keep their sizes and heights, so the player always gets the designed layout, just denser.
  - At 55 % it needs about 1.4 × 2.2 m.
  - The search covers the whole scanned room, out to 6 m around the player.
  - Only if even that doesn't fit is a generic fallback used (Large 2 × 3, Standard 1.2 × 2, Compact 0.6 × 1).
  - The search reaches 3 m from the player.
- **Coins.**
  - Floating coins at waist height, collected by walking through them (within 0.3 m): +100 each, and they count towards the COINS counter on the HUD. A coin from a ? block counts too.
  - Every 100 coins gives a 1-Up and the counter starts again. At the life cap of 8, you get the points only.
  - The counter resets at game over.
- **Warp pipes, final design** (this replaces the pass-3 placeholder).
  - Squatting on a stage's pipe hides the stage, freezing its enemies, and puts a **bonus room** in exactly the same place, so it always fits.
  - The bonus room lays itself out to that stage's footprint: an underground blue-tiled floor, a low blue-brick wall round the edge, a grid of coins (30 on a full-size stage), and an **exit pipe** at the far end.
  - Squatting on the exit pipe removes the bonus room and brings the stage back exactly as it was.
  - A stage's pipe works once. Squatting on it again just ducks.
  - The flag can't be reached from the bonus room; the player has to go back first.

## Later passes
- **Pass 3** (done, see above): warp pipes, difficulty scaling, audio, larger layouts. Art in place of primitives is still to come.
- **Warp pipes, redesigned** (Oliver, 2026-09-30; **built** with the maps, see "Maps and layouts"). A warp pipe should work like Mario's:
  - it isn't a stage clear
  - entering it **teleports the player to a separate, temporary area** (a bonus room, for example), which has its own **exit pipe**
  - the exit pipe **returns the player to the main stage**, and play continues there
  - This needs a second placed layout (or a swap of the current one) and a return point on the main stage. That's why it waits for the map work.

## v2: actions, World 2 and the ending (2026-10-12)
Implements VISUALIZER_STATUS §7.2 (A actions, B maps, C HUD and graphics). Protocol version 2.
- **Actions.** Six discrete actions; see the Action → outcome table above. Limbs for haptics: throw / punch / clap → wrist, stomp → ankle, others → both. Every refusal at gates 3–5 now gets a "denied" haptic, a bonk and a HUD reason ("Nothing to throw — clap to pick up a shell", "Hands full — throw it first", "Fireballs recharging").
- **Koopa shells.** Stomp a Koopa once: it becomes a shell. Then either stomp again (+200) or **clap** to pick it up and **throw** it: it slides along the floor, hitting each enemy it touches once (Goombas and Koopas knocked out, +100 each plus a chain bonus of +100 × hits so far; Bowser takes 2 damage).
- **The campaign** is now 8 stages and **ends**: completing the last stage (2-4) is **GAME CLEAR**, whether the castle or a fallback template was played for it.

  | Stage | Size | Scenery | Theme |
  |---|---|---|---|
  | 1-1 to 1-3 | 2.5 × 4 m | overworld | as before |
  | 1-4 | 2.5 × 4 m | castle | as before |
  | **2-1** Shell Hills | 3 × 5 m | overworld | Koopas to shell and throw down a line of 3 Goombas; tall pipes; two staircases frame the flag |
  | **2-2** Underground | 3 × 5 m | underground | Brick-and-? row to punch, stair pillars to weave through, a Goomba wave, a warp pipe, a coin line |
  | **2-3** Sky Road | 3 × 5 m | sky | Cannons on both sides, tall pipes, a Koopa for a shell, coin arcs |
  | **2-4** Bowser's Castle | 3 × 5 m | castle | Two ? blocks (flowers), a Koopa spawner for shells, Bowser guarding the axe |

- **Height ("more 3D").** Stair-block columns (1–3 blocks), tall decorative pipes, coin **arcs** that rise up to 35 cm in the middle, and scenery overhead.
- **Scenery** (decoration only: no targets, no colliders, kept to the stage edges and overhead):
  - overworld: green hills, bushes, drifting clouds with eyes, and a small castle beyond the flag
  - underground: blue-brick ceiling rows at 2.35 m and pillars along the sides
  - sky: clouds overhead and along the edges, giant mushrooms
  - castle: grey crenellated walls, lava strips outside them, flickering torches
- **Bowser** (2-4). About 1 m tall, 5 health. He paces side to side, faces the player and touching him costs a life. Every 4.5 s (first after 3 s) he **roars** (mouth glow, warning sound, HUD "BOWSER'S FIRE - SQUAT!") for 1 s, then breathes a fireball at head height that flies like a Bullet Bill: squat or sidestep. Punch or fireball = 1 damage, a thrown shell = 2; he can't be stomped. Defeated: +5000, knocked out, and the **axe** unlocks (until then the banner says "BEAT BOWSER TO REACH THE AXE" and standing at the axe does nothing). Reaching the axe: the axe drops, confetti, the GAME CLEAR fanfare, and after 4 s the stage is cleared away.
- **HUD and graphics.**
  - HELD slot in the top bar (shell icon), and the held shell shown in the bottom-right of the 3D view.
  - Boss health bar under the banner while Bowser is on the stage.
  - Intro card when a stage starts: "WORLD 2-1", the stage's name and a one-line tip (3.2 s).
  - "+100"-style score popups floating up in the world where points were scored.
  - End screen: GAME OVER or GAME CLEAR!, with score, coins, enemies defeated, play time and accuracy (resolved ÷ recognised-but-refused actions).
  - Soft blob shadows under enemies, shells and Bowser, to ground them on the real floor.

## Gameplay polish (2026-10-21, after the first integration test)

- **Clearing a stage needs a jump.** At the flag (or the axe in 2-4, once Bowser is beaten), a `jump` within 0.5 m grabs it: Gate 6, reason `goal`, a success haptic, then the usual stage clear. Standing there does nothing; the banner prompts "JUMP TO GRAB THE FLAG!" / "AXE!".
- **Scene changes iris out and in.** Between stages and through warp pipes, the view closes to black in a circle, the scene changes, and it opens again (`ScreenTransition`, about 1 s in all). For safety in a real room it's never black for more than about 1.2 s; the HUD text stays on top.
- **Sizes.** Gameplay objects (blocks, pipes, cannons, enemies, pickups, coins, the flag and the axe) are 13 % smaller than before, for comfort; reach and hit distances are unchanged. Scenery is sized to fill the space round the stage, outside the play area or overhead: the 1-1 castle is about 2.3 m tall, behind the flag; hills, bushes, giant mushrooms, cloud banks, cave pillars and walls, and castle walls with lava moats.
- **One event per motion, filtered on the phone.** The gateway forwards every classification, so one motion arrives as a run of events with the same action. `EventResolver` processes the first and drops the rest at Gate 2 (`repeat of the same motion`), in every state: a report within 0.5 s of the previous one of that action continues the run; a run over 1.2 s is a new motion (two stomps back to back). The scan ignores repeated jumps, so one jump never starts and confirms it at once.
- **Hard reset from inside the headset.** Look straight up (more than 65° above the horizon) for **5 s**. The HUD counts down ("KEEP LOOKING UP TO RESTART... 4") after the first second, so glancing up at a ? block shows nothing; looking down cancels. The game irises out and goes back to the very start: "jump to start scanning", the scan, a jump to confirm. Score, lives and stage are reset; on the phone the AR session is reset too, so the scan starts from no planes.

## Training round (WORLD 0-1, 2026-10-21)

After every scan (first launch and every hard reset, not after a game over), a training lane (1.6 × 3.2 m, compressible) teaches the moves before 1-1. Its stations appear one at a time, each with a big card and a banner line (`TRAINING n/7 - ...`):

| # | Card | Teaches | Done when |
|---|---|---|---|
| 1 | JUMP! | jump under a ? block | the block is hit (it always drops the fire flower) |
| 2 | PUNCH! | punch a brick | the brick breaks |
| 3 | STOMP! | stomp a half-speed Goomba | it's defeated |
| 4 | SQUAT! | duck a Bullet Bill (the cannon keeps firing) | a squat while a Bill is coming |
| 5 | THROW! | fireball at a slow, distant Goomba (the flower is given if missing) | it's defeated |
| 6 | STOMP, CLAP, THROW! | Koopa: shell, pick up, throw at two Goombas | all three cleared |
| 7 | JUMP AT THE FLAG! | jump at the goal | the flag is grabbed |

- **Infinite lives:** a hit flashes, buzzes and says "Ouch! (no lives lost in training)", but costs no life, flower or held item. The HUD shows `INF`.
- **Wrong move:** a move the step isn't about (that did nothing) gets "That was STOMP — try JUMP", which also shows what the classifier read.
- **Never stuck:** a step not done in 40 s says "Let's move on" and the next starts; its leftover enemies and cannon are cleared. The flag doesn't time out (it's the way out); it can't be grabbed while hidden.
- **Then 1-1, fresh:** "TRAINING COMPLETE!", the iris wipe, and 1-1 with score 0, 3 lives and no flower; no clear bonus or stage count for training. No skip.

