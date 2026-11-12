# Visualizer journal: changes since the comms integration

A short log of each problem met after the visualizer was connected to John's gateway, and the design choice made to solve it. Newest last. Details live in `GAME_DESIGN.md` and `VISUALIZER_STATUS.md`.

---

## 2026-10-18 · Connecting to the real gateway

**1. One app, two gateways**
- **Problem:** the app pointed at one gateway. Switching between Oliver's mock gateway and John's laptop meant editing the scene and rebuilding.
- **Choice:** two build profiles in `BuildScript`:
  - **MARIO Local** connects to the mock gateway on the Mac.
  - **MARIO** connects to John's hotspot, `192.168.137.1`, pinned to his certificate.

  Each has its own bundle ID, so both can be installed side by side. The profile's settings apply only during the build, and the scene is left unchanged.

**2. Certificate pin format**
- **Problem:** John's `openssl` command prints the fingerprint as `sha256 Fingerprint=9A:10:…`, not as plain hex.
- **Choice:** the app and the build script accept that whole line as pasted.

---

## 2026-10-21 · First integrated playtest

**3. Objects too big, scenery too small**
- **Problem:** blocks and enemies felt bulky up close, while the backdrop castle was smaller than the flagpole.
- **Choice:**
  - all gameplay objects built at 87 %;
  - scenery resized to fill the space round the stage (castle about 2.3 m), always outside the play area;
  - after the next test the overworld hills were halved again.

**4. Abrupt scene changes**
- **Problem:** stages and warp pipes swapped instantly, which was disorienting.
- **Choice:** a classic iris wipe (`ScreenTransition`). Because the player is walking in a real room, the screen is never black for more than about 1.2 s.

**5. Stages cleared by accident**
- **Problem:** walking up to the flag cleared the stage, even when just passing by.
- **Choice:** the player must **jump** within 0.5 m of the flag (or the axe). The HUD prompts "JUMP TO GRAB THE FLAG!".

**6. AI idle class**
- **Problem:** the classifier gained an `idle` class for movement that isn't one of the six actions.
- **Choice:** the phone acknowledges `idle` as resolved and otherwise ignores it: no haptic, no HUD message, and it doesn't count towards stats.

**7. One motion, many events**
- **Problem:** the gateway forwards every AI inference, so one jump arrived as several jump events.
- **Choice:** a repeat filter on the phone.
  - Only the first event of a run of the same action counts. Reports less than 0.5 s apart belong to the same motion; a run longer than 1.2 s counts as a new motion.
  - Repeats are acknowledged as gate 2 "repeat of the same motion".
  - The scan ignores repeated jumps.

**8. No restart from inside the headset**
- **Problem:** restarting meant taking the phone out of the headset.
- **Choice:** look straight up for 5 s, with a HUD countdown, to hard-reset back to "jump to start scanning".
  - Everything resets in place, because reloading the scene breaks XR Simulation.
  - On the phone the AR session is reset as well.

**9. New players don't know the moves**
- **Problem:** the AI needs specific motions, and first-time players don't know them.
- **Choice:** a training round (WORLD 0-1) after every scan.
  - Seven stations appear one at a time: jump, punch, stomp, squat, throw, stomp-clap-throw, then the flag.
  - Lives are infinite, misread moves get tips, and each step has a timeout.

---

## 2026-10-22 · Unreliable AI

**10. Can't see what the AI sends**
- **Problem:** during playtests there was no way to tell what the classifier had actually sent.
- **Choice:** a small HUD label in the bottom-right corner: `LAST ACTION: <class> <confidence> OK/GATE n`.

**11. AI misses jumps and squats**
- **Problem:** the classifier was unreliable at jumps and squats in particular.
- **Choice:** the phone detects them itself from its tracked head height (`PhoneMotionDetector`).
  - **Jump:** the head rises more than 10 cm above standing height, moving faster than 0.5 m/s.
  - **Squat:** the head drops more than 28 cm. While the player stays down, they stay ducked.
  - **No double counting:** the AI's report of the same action within 1.5 s counts as a repeat.
  - **Startup glitches:** tracking jumps (more than 25 cm in one frame) and the first second after start are ignored. This was added after a false jump at startup.

**12. Docs out of date**
- **Problem:** the docs still described the pre-integration protocol and setup.
- **Choice:** brought all five docs in line with the build.

---

## 2026-10-29 · Placement and awareness

**13. Stages off-centre, pieces near the edge**
- **Problem:** the search took the first spot that fit, often right against the edge of the scanned floor.
- **Choice:** score every spot that fits by how much clear floor surrounds it, take the best-centred one, then nudge it to even the margins. If the full-size stage would have less than 30 cm to spare, a smaller scale is tried first.

**14. Stage orientation inconsistent**
- **Problem:** stages could be rotated sideways or turned round, putting the flag near the player.
- **Choice:** every stage faces straight away from the player, with its flag on the far side. The rotated fallbacks were removed.

**15. Stomps and claps too strict**
- **Problem:** stomps and claps often missed because the target was slightly too far away.
- **Choice:** both now reach 1.0 m (they were 0.7 m and 0.8 m).

**16. Enemies hard to locate**
- **Problem:** enemies coming from the side or from behind went unnoticed.
- **Choice:**
  - red floor arrows point at the 3 nearest approaching enemies;
  - a red arrow at the edge of the view appears when the nearest one is out of sight.

**17. Enemies hard to see**
- **Problem:** at 87 % size, enemies were hard to pick out in the room.
- **Choice:** enemies are 18 % larger. Hit distances are unchanged.

**18. Unclear when a stomp will land**
- **Problem:** players couldn't tell whether a stomp or clap would register.
- **Choice:** a ring on the floor round the player's feet at reach distance. It turns **green** when a stomp would land and **gold** when there's a shell to clap. It uses the same targeting as the game itself.

**19. Flaky network test**
- **Problem:** the mock gateway's scripted moves were 0.6 s apart, too close to the 0.5 s repeat window.
- **Choice:** scripted moves are now 0.8 s apart.

---

## 2026-11-03 · Playtest round 2

**20. Enemies sped up each stage**
- **Problem:** enemies got faster on every stage, which became unfair in a real room.
- **Choice:** speeds are constant for the whole game. Later stages still add more enemies and cannon shots.

**21. Walking into virtual objects (safety)**
- **Problem:** a bush, block or wall the player's head was inside hid the real room.
- **Choice:** any stage object the head is in or right next to turns about 70 % transparent until the player steps out. Enemies stay solid.

**22. Key HUD items not prominent**
- **Problem:** lives and power-up status were easy to miss.
- **Choice:** light highlight boxes round LIVES, POWER and HELD.

**23. Power-ups too small**
- **Problem:** the fire flower and 1-Up were hard to spot.
- **Choice:** both are 1.5× larger, still collected at the same distance.

**24. Bullet Bill not intuitive**
- **Problem:** the cannon was easy to miss, and it fired too soon and from too close.
- **Choice:**
  - a thick red tower with yellow warning bands;
  - **one cannon per stage**, at the far end;
  - first shot after 8 s, a 2 s warning, and it holds fire while the player is within 2 m.

**25. Training could be passed without learning**
- **Problem:** a step could be finished with the wrong move, such as stomping the Goomba in the fireball step.
- **Choice:**
  - each step must be done **3 times with its own move**, with a fresh station for each round;
  - doing it any other way resets the round with "That one didn't count";
  - the shell step takes the fire flower away;
  - a 90 s fallback per step stops a misreading classifier trapping the player.

---

## 2026-11-05 · Playtest round 3

**26. Spent ? blocks kept their "?" look**
- **Problem:** some ? blocks still looked unspent after being hit (the gameplay side was right: a second jump was refused). The see-through safety fade (21) had saved the block's "?" material when the head came close, and put it back after the hit had swapped in the spent one.
- **Choice:** when a see-through object changes its own material, the fade adopts the new one, so stepping away restores the current look.

**27. No way to choose how to play**
- **Problem:** every game went through training, and there was no easier mode for demos or first-timers.
- **Choice:** a start menu before the scan, replacing "jump to start scanning". The options are:
  - TRAINING + GAME;
  - GAME ONLY;
  - GOD MODE: OFF/ON. On, it gives infinite lives in the game too; training always has them.

  The menu works without touching the phone:
  - **Nod down / tilt up:** moves the selection (about 15°, quick). A slow look round doesn't move it.
  - **Clap:** picks the selected option and starts the scan.
  - **Choices are kept:** a hard reset brings the menu back with the last choices, and god mode carries over to a new game after a game over.

**28. Bowser too passive up close**
- **Problem:** Bowser only breathed fire, so a player standing next to him could punch him freely. He also couldn't be stomped.
- **Choice:** his attack depends on distance.
  - **Beyond 1.5 m:** he breathes fire, as before.
  - **Up close:** he crouches while a red target on the floor follows the player, then leaps at that spot.
  - **Landing:** a shockwave hits anyone within 0.7 m who is on the ground. To dodge it, step out of the red target or jump.
  - **After landing:** he's stunned for 1.6 s, then hops back to his post. Standing close brings a leap within about 1.2 s.
  - **Hitting him:** punch, **stomp** and fireball do 1 damage, a shell 2. Nothing reaches him in the air.

**29. Fireballs hard to follow**
- **Problem:** the fire flower's fireballs were small in flight.
- **Choice:** they're 35 % larger. Bowser's fire and the hit distance are unchanged.

**30. Enemies kept coming**
- **Problem:** spawners sent a new enemy after each defeat, so stages never felt cleared.
- **Choice:** one wave per stage, at the start. The enemies come out 1.5 s apart so they don't overlap, and nothing respawns. Waves are the designed sizes: 1 to 4 enemies, 2 Koopas plus Bowser on 2-4. Difficulty no longer adds enemies; later stages still add cannon shots.

**31. Shell training step confusing**
- **Problem:** the Koopa and the Goombas all appeared together, so it wasn't clear what to do first.
- **Choice:** only the Koopa appears at first. Once its shell is in hand, three Goombas appear ("Got it! Now throw it at a Goomba"). Each round starts again with the Koopa alone.

---

## 2026-11-10 · Demo prep

**32. The boss is too far in for a demo**
- **Problem:** reaching Bowser meant clearing all seven stages before him, too long for a demo.
- **Choice:** a **GAME: FULL / SHORT** option on the start menu, toggled with a clap.
  - **Short:** World 1 (1-1 to 1-4), then straight to Bowser's castle (2-4).
  - **Full** (the default): all eight stages.

  The choice carries over to a new game and survives a hard reset, like god mode.

**33. Fireballs passed through Bowser's body**
- **Problem:** only fireballs near Bowser's head hit him. A fireball checked its distance to a single point on each target, and his was at head height, so shots at his belly or legs missed.
- **Choice:** each target decides whether a shot hits it. Bowser's hitbox is now a box round his whole body art, legs to horns, so a fireball anywhere on him hits. Other targets are unchanged. His intro tip was also updated for his jump attack.

**34. Shells stomped by accident**
- **Problem:** a player stepping up to pick up a shell often stomped it by accident, clearing it before they could clap.
- **Choice:** a shell can't be stomped. The only way to use it is to clap, pick it up and throw it. A stomp on a shell is refused with "Clap to pick up the shell". A fireball still clears one.

**35. Clap, throw and punch confused by the classifier**
- **Problem:** the three arm movements look alike, so the classifier often read one as another, and the move did nothing (e.g. a throw in front of a brick).
- **Choice:** clap, throw and punch are treated as one "arm" move, and the game picks the one that makes sense right now:
  - **Exactly one fits** (a brick or Bowser in reach for punch; a shell in reach with empty hands for clap; a held shell or a ready fire flower for throw): that one resolves, whatever was read. LAST ACTION shows `(READ AS THROW)`.
  - **Several fit, or none:** the classifier's reading stands, as before.
  - **Repeats:** a run of arm moves counts as one move, whichever labels it mixes. In the start menu any arm move picks.

**36. Training rounds all in the same spot**
- **Problem:** each training step's three rounds put the ? block, brick or enemy in the same place, so the player never had to move.
- **Choice:** each round's copy stands somewhere new: the first where it was, then a short walk to one side and the other (about 0.5 m apart, within the lane). A round that restarts stays at its spot.

---

## 2026-11-12 · Finding things, a fixed stage height

**37. New players couldn't find the targets or the stage**
- **Problem:** after the scan, players rarely stood at an edge facing the stage, and it could appear beside or behind them. In the game the red enemy arrows lead to the stage, but in training players spent a while looking for the ? block, the brick or the shell.
- **Choice:** gold arrows to the objective (red stays for enemies):
  - **In training:** a gold arrow bobs above the current target, pointing down at it; a gold floor arrow points towards it; and when it's out of view an arrow at the edge of the view points to it while the banner says "TURN AROUND / LOOK LEFT …: FOLLOW THE GOLD ARROW".
  - **Not for walking enemies:** a walking Goomba or Koopa already has the red arrows, so it gets no gold ones. The ? block, brick, shell, cannon and flag do.
  - **A new stage:** until it has been in view once, the edge arrow points to it and the banner says "TURN AROUND - THE STAGE IS THIS WAY". Then it goes quiet.

**38. The training squat step dragged**
- **Problem:** the cannon fired every 5 s, so the squat rounds were mostly waiting.
- **Choice:** the training cannon fires every 2.5 s. Campaign cannons are unchanged.

**39. Stages changed with whoever scanned**
- **Problem:** stage heights came from the eye height measured during the scan, so the same stage looked different for each player, and a phone held low made everything low.
- **Choice:** stages are always built for a **1.70 m** eye height (? blocks at 2.05 m, bricks 1.33 m, coins 1.02 m). The measured eye height is still used where it must be personal: jump and squat detection, and the floor estimate for targeting.

**40. Training too long**
- **Problem:** with 3 rounds per step, training took too long for a demo.
- **Choice:** 2 rounds per step (the flag still 1).

**41. Too many enemies, Koopa lost in the crowd**
- **Problem:** up to 4 enemies at once was hard to handle, and the Koopa (the shell's source) often came last.
- **Choice:** at most **2 enemies per stage** (Bowser aside). Koopas spawn first: a spawner sends its Koopas before its Goombas, and a spawner about to send a Goomba waits while another on the stage still has a Koopa to send.

**42. Demos ending in a game over**
- **Choice:** god mode is **on by default** in the start menu; a clap on it turns it off.

**43. Bullet Bills hit squatting players**
- **Problem:** players squatting under a Bullet Bill were still hit. Tests showed why: the Bill checked only, in the one frame it drew level, whether a 1 s "ducked" window from the last squat event was still open. So it hit:
  - a squat held from a little early (the window ran out);
  - a held squat the classifier kept reporting (repeats were ignored);
  - a squat reported a moment late (classifier latency);
  - a head clearly below the Bill with no squat event (head height was never looked at). The phone's own squat detection only helps for squats deeper than 28 cm.
- **Choice:** the duck is lenient.
  - A head 10 cm or more below the Bill's centre counts as ducked, squat event or not.
  - The Bill judges over a window from 0.6 m before the player to 0.6 m after (0.3 s each way), so a duck anywhere in it counts.
  - Repeated squat reports keep the player ducked, and a squat event now lasts 1.5 s (was 1).
  - Standing up, or a quick squat long before the Bill arrives, still gets hit. Bowser's fire, which flies like a Bill, gets the same rules.
