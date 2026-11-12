using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Headless check of the pass-1 gameplay rules (Docs/GAME_DESIGN.md) against
/// the real prefabs, with no device, AR or network: builds a throwaway scene
/// (eye-height camera, resolver, game state) and drives EventResolver and the
/// elements' Tick methods directly. Exits 0 if every check passes, 1 otherwise.
///
/// Run with Unity closed (after GameContentSetup has built the prefabs):
///   Unity -batchmode -projectPath . -executeMethod GameplaySmokeTest.Run
/// </summary>
public static class GameplaySmokeTest
{
    private static readonly List<string> s_Failures = new();
    private static int s_Checks;
    private static int s_NextId;

    private static Camera s_Cam;
    private static GameStateManager s_State;
    private static EventResolver s_Resolver;
    private static Transform s_Stage;

    public static void Run()
    {
        try { RunChecks(); }
        catch (Exception e) { s_Failures.Add($"exception: {e}"); }

        if (s_Failures.Count == 0)
        {
            Debug.Log($"[GameplaySmokeTest] PASS — {s_Checks} checks.");
            EditorApplication.Exit(0);
        }
        else
        {
            foreach (var f in s_Failures) Debug.LogError($"[GameplaySmokeTest] FAIL — {f}");
            Debug.LogError($"[GameplaySmokeTest] {s_Failures.Count} of {s_Checks} checks failed.");
            EditorApplication.Exit(1);
        }
    }

    private static void RunChecks()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var camGO = new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" };
        s_Cam = camGO.GetComponent<Camera>();
        s_Cam.transform.position = new Vector3(0f, 1.5f, 0f);
        FaceForward();

        var manager = new GameObject("GameManager");
        s_State = manager.AddComponent<GameStateManager>();
        s_Resolver = manager.AddComponent<EventResolver>();
        SetField(s_Resolver, "gameState", s_State);
        SetField(s_Resolver, "arCamera", s_Cam);
        SetField(s_Resolver, "fireballPrefab", Load<Fireball>(GameContentSetup.FireballPath));
        SetField(s_Resolver, "thrownShellPrefab", Load<ThrownShell>(GameContentSetup.ThrownShellPath));
        s_Resolver.Clock = () => s_Now;
        s_State.BeginStage();
        s_Stage = new GameObject("StageRoot").transform; // floor at y = 0

        // -- state-only actions; retired v1 labels are unknown
        var r = Resolve("walk");
        Check(r.Gate == 2, "v1 'walk' is an unknown action (Gate 2)", r);
        r = Resolve("fireball");
        Check(r.Gate == 2, "v1 'fireball' is an unknown action (Gate 2)", r);
        r = Resolve("throw");
        Check(r.Gate == 3 && r.Reason == "nothing to throw", "throw with no flower and nothing held is Gate 3", r);
        var statsBefore = s_State.GameStats;
        r = Resolve("idle");
        Check(r.Resolved && r.StateOnly && r.Reason == "idle" && s_State.GameStats.actionsResolved == statsBefore.actionsResolved
              && s_State.GameStats.actionsRefused == statsBefore.actionsRefused, "idle resolves quietly (state-only, not counted)", r);
        r = Resolve("squat");
        Check(r.Resolved && r.StateOnly && s_State.IsDucked, "squat ducks the player", r);

        // -- repeats: the gateway forwards every classification, so one motion is a run of events
        var reps = new List<ResolutionResult> { ResolveAfter(0.25, "squat"), ResolveAfter(0.25, "squat"), ResolveAfter(0.25, "squat") };
        Check(reps.All(x => x.IsRepeat && !x.Resolved), "more reports of the same squat (0.25 s apart) are repeats (Gate 2)", reps[0]);
        Check(statsBefore.actionsRefused == s_State.GameStats.actionsRefused, "repeats don't count as refused actions");
        r = ResolveAfter(0.1, "jump");
        Check(r.Resolved && !r.IsRepeat, "a different action during the run is processed", r);
        r = ResolveAfter(0.25, "squat");
        Check(r.IsRepeat, "the squat run continues past the jump", r);
        r = ResolveAfter(0.25, "squat");
        Check(r.Resolved && !r.IsRepeat, "a run longer than 1.2 s counts as a new motion (back-to-back moves)", r);
        r = ResolveAfter(0.6, "squat");
        Check(r.Resolved && !r.IsRepeat, "after a 0.6 s gap the same action is a new motion", r);
        r = ResolveAfter(0.2, "squat", 0.3f);
        Check(r.Gate == 2 && !r.IsRepeat, "a low-confidence report is rejected as usual, not as a repeat", r);
        r = ResolveAfter(0.6, "jump", 0.3f);
        r = ResolveAfter(0.2, "jump");
        Check(r.Resolved && !r.IsRepeat, "a low-confidence report doesn't start a run", r);
        r = Resolve("jump");
        Check(r.Resolved && r.StateOnly && s_State.IsAirborne, "jump with no block is airborne, state-only", r);

        // -- "?" block: jump underneath drops a power-up (rolled a flower here), then it's spent
        var block = Spawn<QuestionBlock>(GameContentSetup.StandardStagePath, "QuestionBlock", new Vector3(0.1f, 1.8f, 0.2f));
        SetField(block, "oneUpChance", 0f);
        r = Resolve("jump");
        Check(r.Resolved && r.Target == block, "jump under ? block hits it", r);
        var flower = s_Stage.GetComponentInChildren<Pickup>();
        Check(flower != null && flower.PickupKind == Pickup.Kind.FireFlower, "? block drops a fire flower when it rolls one");
        r = Resolve("jump");
        Check(r.Gate == 5, "second jump on the spent block is Gate 5", r);

        // -- fire flower lands past the block, away from the player; collected by walking to it
        var away = block.transform.position - s_Cam.transform.position;
        away.y = 0f;
        var landing = block.transform.position + away.normalized * 0.5f;
        Check(flower != null && !flower.TryCollect(s_Cam.transform.position) && !s_State.HasFireFlower, "the flower isn't collected where the player stands under the block");
        Check(flower != null && flower.TryCollect(landing) && s_State.HasFireFlower, "walking to the flower's landing spot collects it");

        // -- fireball projectile hits a Goomba the player is looking at
        var g1 = SpawnPrefab<Goomba>(GameContentSetup.GoombaPath, new Vector3(0f, 0f, 2f));
        s_Cam.transform.LookAt(g1.HitPoint);
        int scoreBefore = s_State.Score;
        Component impacted = null;
        s_Resolver.OnImpact += (_, t) => impacted = t;
        r = Resolve("throw");
        Check(r.LaunchedProjectile && !r.ThrewShell && Fireball.InFlight == 1, "throw with the flower launches a fireball", r);
        var fb = UnityEngine.Object.FindObjectsByType<Fireball>().FirstOrDefault();
        InteractableBase hit = null;
        for (int i = 0; i < 40 && fb != null && hit == null; i++) hit = fb.Tick(0.05f);
        Check(hit == g1 && !g1.IsActionable && s_State.Score == scoreBefore + 100 && impacted == g1, "fireball hits the Goomba (+100, OnImpact)");
        Check(Fireball.InFlight == 0, "fireball is gone after the hit");
        FaceForward();

        // -- Gate 3: at most 2 fireballs in flight
        Resolve("throw");
        Resolve("throw");
        r = Resolve("throw");
        Check(r.Gate == 3, "third fireball in flight is Gate 3", r);
        foreach (var f in UnityEngine.Object.FindObjectsByType<Fireball>()) UnityEngine.Object.DestroyImmediate(f.gameObject);

        // -- Goomba walks toward the player; stomp defeats it once in range
        var g2 = SpawnPrefab<Goomba>(GameContentSetup.GoombaPath, new Vector3(0f, 0f, 1.2f));
        float startDistance = Horizontal(g2.transform.position);
        ClearAirborne();
        for (int i = 0; i < 12; i++) g2.Tick(0.2f, s_Cam.transform.position); // 2.4 s at 0.25 m/s ≈ 0.6 m
        Check(Horizontal(g2.transform.position) < startDistance - 0.4f, "Goomba walks toward the player");
        r = Resolve("stomp");
        Check(r.Resolved && r.Target == g2, "stomp defeats a Goomba at the player's feet", r);

        // -- damage and the jump-over
        s_State.SetFireFlower(true);
        var g3 = SpawnPrefab<Goomba>(GameContentSetup.GoombaPath, new Vector3(0f, 0f, 0.3f));
        ClearAirborne();
        g3.Tick(0.02f, s_Cam.transform.position);
        Check(s_State.Lives == 2 && !s_State.HasFireFlower && s_State.IsInvulnerable, "Goomba contact costs a life and the flower");
        SetField(s_State, "_invulnerableUntil", float.NegativeInfinity);
        var g4 = SpawnPrefab<Goomba>(GameContentSetup.GoombaPath, new Vector3(0f, 0f, 0.3f));
        s_State.Jump(1f);
        g4.Tick(0.02f, s_Cam.transform.position);
        Check(s_State.Lives == 2 && g4 != null && g4.IsActionable, "an airborne player isn't hurt; the Goomba passes");
        UnityEngine.Object.DestroyImmediate(g4.gameObject);

        // -- punch: brick at chest height in reach; nothing in reach → Gate 4
        var brick = Spawn<BrickBlock>(GameContentSetup.StandardStagePath, "BrickBlock", new Vector3(0.1f, 1.15f, 0.5f));
        scoreBefore = s_State.Score;
        r = Resolve("punch");
        Check(r.Resolved && r.Target == brick && s_State.Score == scoreBefore + 50, "punch breaks a brick (+50)", r);
        r = Resolve("punch");
        Check(r.Gate == 4, "punch with no brick in reach is Gate 4", r);
        r = Resolve("stomp");
        Check(r.Gate == 4, "stomp with no enemy in range is Gate 4", r);

        // ==== pass 2 ====

        // -- Koopa: stomp → shell (harmless); a shell can't be stomped, only picked up
        var k1 = SpawnPrefab<Koopa>(GameContentSetup.KoopaPath, new Vector3(0f, 0f, 0.5f));
        scoreBefore = s_State.Score;
        r = Resolve("stomp");
        Check(r.Resolved && r.Target == k1 && k1.InShell && s_State.Score == scoreBefore, "first stomp knocks a Koopa into its shell (no score)", r);
        int livesBefore = s_State.Lives;
        k1.transform.position = new Vector3(0f, 0f, 0.1f);
        ClearAirborne();
        k1.Tick(0.02f, s_Cam.transform.position);
        Check(s_State.Lives == livesBefore && k1.transform.position.z == 0.1f, "a shell doesn't move or hurt");
        r = Resolve("stomp");
        Check(r.Gate == 5 && r.Reason == EventResolver.ShellStompReason && k1 != null && k1.InShell && k1.IsActionableFor("clap") && s_State.Score == scoreBefore,
              "a second stomp on the shell is refused (Gate 5: clap to pick it up); the shell stays", r);
        UnityEngine.Object.DestroyImmediate(k1.gameObject);

        // -- clap picks up a shell; throw sends it sliding through a line of Goombas
        s_State.SetFireFlower(false);
        var k3 = SpawnPrefab<Koopa>(GameContentSetup.KoopaPath, new Vector3(0f, 0f, 0.5f));
        r = Resolve("clap");
        Check(r.Gate == 4, "clap at a walking Koopa is Gate 4 (only shells can be picked up)", r);
        Resolve("stomp");
        r = Resolve("clap");
        Check(r.Resolved && r.Target == k3 && s_State.Held == HeldItem.Shell, "clap picks up a Koopa shell", r);
        // (A clap while holding it would throw it: the only arm action that fits; see ArmActionChecks.)
        var line1 = SpawnPrefab<Goomba>(GameContentSetup.GoombaPath, new Vector3(0.05f, 0f, 1.2f));
        var line2 = SpawnPrefab<Goomba>(GameContentSetup.GoombaPath, new Vector3(-0.05f, 0f, 1.8f));
        scoreBefore = s_State.Score;
        impacted = null;
        r = Resolve("throw");
        Check(r.ThrewShell && r.LaunchedProjectile && s_State.Held == HeldItem.None && ThrownShell.InFlight == 1, "throw while holding a shell throws it", r);
        var shell = UnityEngine.Object.FindObjectsByType<ThrownShell>().FirstOrDefault();
        Check(shell != null && shell.transform.position.y < 0.2f, "the thrown shell starts on the floor");
        for (int i = 0; i < 60 && shell != null; i++) shell.Tick(0.05f);
        Check(!line1.IsActionable && !line2.IsActionable && s_State.Score == scoreBefore + 100 + 100 + 100 && impacted == line2,
            "the shell knocks out both Goombas in its path (+100 each, +100 chain bonus)");
        Check(ThrownShell.InFlight == 0, "the shell is gone after its lifetime");
        s_State.PickUp(HeldItem.Shell);
        s_State.TakeHit("test");
        Check(s_State.Held == HeldItem.None, "taking a hit drops the held shell");
        SetField(s_State, "_invulnerableUntil", float.NegativeInfinity);
        SetField(s_State, "lives", 2);

        // -- Koopa: a fireball defeats it outright
        s_State.SetFireFlower(true);
        var k2 = SpawnPrefab<Koopa>(GameContentSetup.KoopaPath, new Vector3(0f, 0f, 2f));
        s_Cam.transform.LookAt(k2.HitPoint);
        scoreBefore = s_State.Score;
        r = Resolve("throw");
        fb = UnityEngine.Object.FindObjectsByType<Fireball>().FirstOrDefault();
        hit = null;
        for (int i = 0; i < 40 && fb != null && hit == null; i++) hit = fb.Tick(0.05f);
        Check(r.LaunchedProjectile && hit == k2 && !k2.IsActionable && !k2.InShell && s_State.Score == scoreBefore + 200, "a fireball defeats a walking Koopa outright (+200)", r);
        FaceForward();

        // -- ? block power-up is random; a roll that would be wasted gives the other; both wasted → coin
        Check(s_State.HasFireFlower && s_State.Lives < s_State.MaxLives, "(setup) flower held, below the life cap");
        var oneUp = HitFreshBlock(1f);
        Check(oneUp != null && oneUp.PickupKind == Pickup.Kind.OneUp, "? block drops a 1-Up when it rolls one");
        livesBefore = s_State.Lives;
        Check(oneUp != null && oneUp.TryCollect(landing) && s_State.Lives == livesBefore + 1, "collecting the 1-Up adds a life");
        var p2 = HitFreshBlock(0f);
        Check(p2 != null && p2.PickupKind == Pickup.Kind.OneUp, "a flower roll while holding a flower gives a 1-Up instead");
        if (p2 != null) UnityEngine.Object.DestroyImmediate(p2.gameObject);
        SetField(s_State, "lives", s_State.MaxLives);
        s_State.AddLife();
        Check(s_State.Lives == s_State.MaxLives && s_State.MaxLives == 8, "lives are capped at 8");
        s_State.SetFireFlower(false);
        var p3 = HitFreshBlock(1f);
        Check(p3 != null && p3.PickupKind == Pickup.Kind.FireFlower, "a 1-Up roll at the life cap gives a flower instead");
        if (p3 != null) UnityEngine.Object.DestroyImmediate(p3.gameObject);
        s_State.SetFireFlower(true);
        scoreBefore = s_State.Score;
        var p4 = HitFreshBlock(1f);
        Check(p4 == null && s_State.Score == scoreBefore + 100, "at the life cap with a flower, the ? block gives a coin (+100)");
        SetField(s_State, "lives", 2);

        // -- Bullet Bill: flies straight; hits unless ducked or stepped aside
        var eye = s_Cam.transform.position;
        SetField(s_State, "_duckedUntil", float.NegativeInfinity); // Time.time doesn't advance here: clear the earlier squat
        livesBefore = s_State.Lives;
        var bill = LaunchBill(eye);
        FlyBill(bill, eye);
        Check(bill.State == BulletBill.Phase.Passed && bill.HitPlayer && s_State.Lives == livesBefore - 1, "Bullet Bill hits a player who isn't ducked");
        SetField(s_State, "_invulnerableUntil", float.NegativeInfinity);

        bill = LaunchBill(eye);
        livesBefore = s_State.Lives;
        r = Resolve("squat");
        FlyBill(bill, eye);
        Check(bill.State == BulletBill.Phase.Passed && !bill.HitPlayer && s_State.Lives == livesBefore, "squatting ducks under a Bullet Bill", r);
        SetField(s_State, "_duckedUntil", float.NegativeInfinity);

        bill = LaunchBill(eye);
        var aimed = bill.Direction;
        FlyBill(bill, eye + new Vector3(0.6f, 0f, 0f)); // the player steps aside after it's fired
        Check(bill.State == BulletBill.Phase.Passed && !bill.HitPlayer && s_State.Lives == livesBefore && bill.Direction == aimed,
              "a Bullet Bill flies straight (no homing), so stepping aside dodges it");
        foreach (var b in UnityEngine.Object.FindObjectsByType<BulletBill>()) UnityEngine.Object.DestroyImmediate(b.gameObject);

        // -- cannon: turns to face the player, holds fire when they're close, warns, then fires along its barrel
        var cannon = UnityEngine.Object.Instantiate(
            AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.StandardStagePath).transform.Find("BulletBillCannon").gameObject,
            new Vector3(2f, 0f, 2f), Quaternion.identity, s_Stage).GetComponent<BulletBillCannon>();
        var cannonHead = cannon.transform.Find("Head");
        cannon.Tick(2f, eye); // 2 s at 120°/s: plenty to turn
        var toEye = eye - cannonHead.position; toEye.y = 0f;
        Check(Vector3.Angle(cannonHead.forward, toEye) < 1f, "cannon turns to face the player");
        Check(cannon.Tick(6.1f, eye) == null && cannon.IsWarning, "after its initial delay (8 s) the cannon warns first");
        var fired = cannon.Tick(2.1f, eye); // a 2 s warning
        Check(fired != null && Vector3.Angle(fired.Direction, toEye) < 1f && fired.State == BulletBill.Phase.Flying, "then fires a Bill along its barrel, at the player");
        var nearCannon = cannonHead.position + (eye - cannonHead.position).normalized * 0.8f;
        nearCannon.y = eye.y;
        UnityEngine.Object.DestroyImmediate(fired.gameObject);
        Check(cannon.Tick(11f, nearCannon) == null && !cannon.IsWarning, "cannon holds fire while the player is within 2 m");
        UnityEngine.Object.DestroyImmediate(cannon.gameObject);

        // ==== pass 3 ====

        // -- warp pipe: a squat off the pipe is just a duck; on it, it enters (LevelGenerator swaps in the bonus room)
        var pipe = Spawn<WarpPipe>(GameContentSetup.StandardStagePath, "WarpPipe", new Vector3(0f, 0f, 1f));
        WarpPipe entered = null;
        System.Action<WarpPipe> onEntered = p => entered = p;
        WarpPipe.OnEntered += onEntered;
        r = Resolve("squat");
        Check(r.Resolved && r.StateOnly && s_State.IsDucked && entered == null, "a squat away from a warp pipe just ducks", r);
        pipe.transform.position = new Vector3(0.1f, 0f, 0.15f);
        r = Resolve("squat");
        Check(r.Resolved && r.Target == pipe && entered == pipe && !pipe.IsExit, "a squat on a warp pipe enters it", r);
        entered = null;
        r = Resolve("squat");
        Check(r.Resolved && r.StateOnly && entered == null, "a stage's warp pipe works once (squatting on it again just ducks)", r);
        SetField(s_State, "_duckedUntil", float.NegativeInfinity);
        UnityEngine.Object.DestroyImmediate(pipe.gameObject);

        // -- bonus room: laid out to the stage's footprint, full of coins, with an exit pipe back
        var room = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.BonusRoomPath), s_Stage).GetComponent<BonusRoom>();
        room.Build(2.5f, 4f);
        Check(room.CoinCount >= 20 && room.ExitPipe != null && room.ExitPipe.IsExit, $"the bonus room has coins ({room.CoinCount}) and an exit pipe");
        Register(room.ExitPipe);
        room.ExitPipe.transform.position = new Vector3(0.05f, 0f, 0.1f);
        entered = null;
        r = Resolve("squat");
        Check(r.Resolved && entered == room.ExitPipe, "a squat on the exit pipe leaves the bonus room", r);
        WarpPipe.OnEntered -= onEntered;
        SetField(s_State, "_duckedUntil", float.NegativeInfinity);
        UnityEngine.Object.DestroyImmediate(room.gameObject);

        // -- coins: walk through to collect; 100 make a 1-Up
        var coin = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.CoinPath), new Vector3(0f, 1f, 1f), Quaternion.identity, s_Stage).GetComponent<Coin>();
        int coinsBefore = s_State.Coins;
        scoreBefore = s_State.Score;
        Check(!coin.TryCollect(s_Cam.transform.position), "a coin out of reach isn't collected");
        Check(coin.TryCollect(new Vector3(0f, 1.5f, 0.9f)) && s_State.Coins == coinsBefore + 1 && s_State.Score == scoreBefore + 100, "walking through a coin collects it (+100, counter +1)");
        SetField(s_State, "coins", 99);
        livesBefore = s_State.Lives;
        s_State.AddCoin();
        Check(s_State.Coins == 0 && s_State.Lives == livesBefore + 1, "the 100th coin gives a 1-Up and resets the counter");

        // -- pieces are placed for a fixed 1.70 m eye height, whoever scanned
        var hq = Spawn<QuestionBlock>(GameContentSetup.StandardStagePath, "QuestionBlock", new Vector3(0f, 0f, 3f));
        var hb = Spawn<BrickBlock>(GameContentSetup.StandardStagePath, "BrickBlock", new Vector3(0.5f, 0f, 3f));
        var hc = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.CoinPath), new Vector3(-0.5f, 0f, 3f), Quaternion.identity, s_Stage);
        float[] PlaceAll(float eyes)
        {
            PlayerBody.Calibrate(eyes);
            foreach (var go in new[] { hq.gameObject, hb.gameObject, hc }) go.GetComponent<EyeHeightPlacement>().Place();
            return new[] { hq.transform.position.y, hb.transform.position.y, hc.transform.position.y };
        }
        var shortP = PlaceAll(1.2f);
        var tallP = PlaceAll(1.85f);
        const float fixedEyes = PlayerBody.StageEyeHeight;
        Check(Mathf.Approximately(fixedEyes, 1.70f) && Mathf.Approximately(shortP[0], fixedEyes + 0.35f) && Mathf.Approximately(shortP[1], fixedEyes * 0.78f)
              && Mathf.Approximately(shortP[2], fixedEyes * 0.6f),
              $"pieces are placed for a fixed 1.70 m eye height (? {shortP[0]:F2}, brick {shortP[1]:F2}, coin {shortP[2]:F2})");
        Check(shortP.SequenceEqual(tallP), "a shorter and a taller player get the same stage heights");
        Check(tallP[0] - 0.125f > fixedEyes + 0.12f, "a ? block's bottom clears the top of a 1.70 m-eye player's head");
        PlayerBody.Calibrate(3.5f);
        Check(Mathf.Approximately(PlayerBody.EyeHeight.Value, PlayerBody.MaxEyeHeight), "an implausible eye height is clamped");
        PlayerBody.Reset();
        foreach (var go in new[] { hq.gameObject, hb.gameObject, hc }) UnityEngine.Object.DestroyImmediate(go);

        // -- the flag is tall enough to spot across a room
        var goalRoot = AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.StandardStagePath).transform.Find("Goal");
        var goalPole = goalRoot != null ? goalRoot.Find("Pole") : null;
        Check(goalPole != null && (goalPole.localPosition.y + goalPole.localScale.y) * goalRoot.localScale.y >= 1.5f, "the flagpole is at least 1.5 m tall");

        // -- gameplay objects are built at 87% (comfort); scenery isn't
        var goombaPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.GoombaPath);
        Check(Mathf.Abs(goombaPrefab.transform.localScale.x - 0.87f * 1.18f) < 0.001f && Mathf.Abs(goalRoot.localScale.x - 0.87f) < 0.001f,
              "gameplay objects are built at 87% (the flag), enemies 18% larger than that (Goomba)");

        // -- hard reset: look straight up for 5 s (the phone is in the headset)
        var hardReset = new GameObject("HardReset").AddComponent<HardReset>();
        var upView = Quaternion.Euler(-80f, 0f, 0f) * Vector3.forward;
        var blockView = Quaternion.Euler(-50f, 0f, 0f) * Vector3.forward; // looking up at a ? block
        bool resetFired = false;
        for (int i = 0; i < 100; i++) resetFired |= hardReset.Tick(0.1f, Vector3.forward);
        for (int i = 0; i < 100; i++) resetFired |= hardReset.Tick(0.1f, blockView);
        Check(!resetFired && hardReset.Held == 0f, "looking ahead or up at a ? block never resets");
        for (int i = 0; i < 5; i++) resetFired |= hardReset.Tick(0.1f, upView);
        Check(!resetFired && !hardReset.ShowCountdown, "a glance up (0.5 s) shows no countdown");
        for (int i = 0; i < 10; i++) resetFired |= hardReset.Tick(0.1f, Quaternion.Euler(-60f, 0f, 0f) * Vector3.forward); // jitter just under the 65° threshold
        Check(!resetFired && hardReset.ShowCountdown && hardReset.SecondsLeft == 4, "once looking up, small dips keep counting; countdown shows after 1 s (4 s left)");
        resetFired |= hardReset.Tick(0.1f, Vector3.forward);
        Check(!resetFired && hardReset.Held == 0f, "looking back down cancels the reset");
        int ticks = 0;
        while (!resetFired && ticks < 100) { resetFired = hardReset.Tick(0.1f, upView); ticks++; }
        Check(resetFired && ticks >= 49 && ticks <= 51, $"holding the look up for 5 s triggers the hard reset ({ticks * 0.1f:F1} s)");
        UnityEngine.Object.DestroyImmediate(hardReset.gameObject);

        // -- the iris wipe never holds anything up without a live HUD (here: edit mode)
        bool wiped = false;
        ScreenTransition.Run(() => wiped = true);
        Check(wiped, "a scene change runs at once when there's no live iris wipe");

        // -- campaign: names, looping, and every stage is complete
        Check(GameStateManager.NameOf(1) == "1-1" && GameStateManager.NameOf(4) == "1-4" && GameStateManager.NameOf(5) == "2-1", "stages are named 1-1 ... 1-4, then 2-1");
        var campaignStages = StageDesigns.Campaign.Select(d => AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.CampaignStagePath(d.Name))).ToList();
        Check(campaignStages.All(c => c != null) && LevelGenerator.CampaignStageFor(campaignStages, 5) == campaignStages[4]
              && LevelGenerator.CampaignStageFor(campaignStages, 3) == campaignStages[2], "the campaign plays in order");
        Check(campaignStages.Take(4).All(c => c.GetComponent<StageFootprint>() is { } f && Mathf.Approximately(f.width, 2.5f) && Mathf.Approximately(f.depth, 4f))
              && campaignStages.Skip(4).All(c => c.GetComponent<StageFootprint>() is { } f && Mathf.Approximately(f.width, 3f) && Mathf.Approximately(f.depth, 5f))
              && campaignStages.All(c => c.GetComponentInChildren<StageGoal>() != null && c.GetComponentsInChildren<Coin>().Length > 0),
              "World 1 stages are 2.5 x 4 m, World 2 3 x 5 m; every stage has a goal and coins");
        Check(campaignStages.All(c => c.GetComponentsInChildren<Transform>().Any(t => t.name is "Hill" or "Cloud" or "Pillar" or "Wall")),
              "every campaign stage has scenery");
        Check(campaignStages.All(c => c.GetComponentsInChildren<BulletBillCannon>(true) is var cannons && cannons.Length <= 1
                                      && cannons.All(k => k.transform.localPosition.z > 0f)),
              "every campaign stage has at most one cannon, in its far half (time to react)");
        var templates = new[] { GameContentSetup.StandardStagePath, GameContentSetup.LargeStagePath, GameContentSetup.CompactStagePath }
            .Select(path => AssetDatabase.LoadAssetAtPath<GameObject>(path));
        Check(campaignStages.Concat(templates).All(c => c.GetComponentsInChildren<EnemySpawner>(true).Sum(sp => sp.WaveSize) <= 2),
              "every stage spawns at most 2 enemies");
        Check(campaignStages.Concat(templates).All(c => c.GetComponentsInChildren<EnemySpawner>(true)
                  .All(sp => Enumerable.Range(0, sp.WaveSize).Select(sp.WavePrefab).SkipWhile(e => e is Koopa).All(e => !(e is Koopa)))),
              "every spawner sends its Koopas before its Goombas");
        {
            // 2-1: a Koopa spawner and a Goomba spawner: the Goomba waits for the Koopa.
            s_State.BeginStage();
            var shellHills = UnityEngine.Object.Instantiate(campaignStages.First(c => c.name == "Stage 2-1"), new Vector3(0f, 0f, 20f), Quaternion.identity, s_Stage);
            var spawners = shellHills.GetComponentsInChildren<EnemySpawner>();
            var order = new List<Goomba>();
            for (int t = 0; t < 40 && order.Count < 2; t++)
                foreach (var sp in spawners)
                    if (sp.Tick(0.25f, new Vector3(0f, 1.5f, 0f)) is { } e) order.Add(e);
            Check(spawners.Length == 2 && order.Count == 2 && order[0] is Koopa && !(order[1] is Koopa),
                  "2-1: the Koopa spawns before the other spawner's Goomba (" + string.Join(",", order.Select(e => e.name)) + ")");
            UnityEngine.Object.DestroyImmediate(shellHills);
        }
        var firstStage = UnityEngine.Object.Instantiate(campaignStages[0], s_Stage);
        var castleTop = firstStage.transform.Find("Castle").GetComponentsInChildren<Renderer>().Max(x => x.bounds.max.y);
        var flagTop = firstStage.GetComponentInChildren<StageGoal>().GetComponentsInChildren<Renderer>().Max(x => x.bounds.max.y);
        Check(castleTop > flagTop + 0.3f && castleTop > 2f, $"1-1's backdrop castle ({castleTop:F2} m) stands well above the flag ({flagTop:F2} m)");
        UnityEngine.Object.DestroyImmediate(firstStage);
        var squeezed = UnityEngine.Object.Instantiate(campaignStages[0], s_Stage);
        var goalBefore = squeezed.GetComponentInChildren<StageGoal>().transform.localPosition;
        LevelGenerator.CompressLayout(squeezed, 0.55f);
        var goalAfter = squeezed.GetComponentInChildren<StageGoal>().transform.localPosition;
        var fp = squeezed.GetComponent<StageFootprint>();
        Check(Mathf.Approximately(fp.width, 2.5f * 0.55f) && Mathf.Approximately(fp.depth, 4f * 0.55f)
              && Mathf.Approximately(goalAfter.z, goalBefore.z * 0.55f) && Mathf.Approximately(goalAfter.y, goalBefore.y),
              "a campaign stage compresses to fit a smaller room (positions scale, heights don't)");
        UnityEngine.Object.DestroyImmediate(squeezed);

        // -- difficulty grows with the stage number, up to caps
        var difficulty = manager.AddComponent<Difficulty>();
        SetField(s_State, "stageIndex", 1);
        Check(Difficulty.ExtraShots == 0 && Difficulty.ShotIntervalMultiplier == 1f, "stage 1 is the authored baseline");
        SetField(s_State, "stageIndex", 5);
        Check(Difficulty.ExtraShots == 1 && Mathf.Approximately(Difficulty.ShotIntervalMultiplier, 0.68f),
              "stage 5 adds cannon shots (speeds and enemy counts stay constant)");
        SetField(s_State, "stageIndex", 40);
        Check(Difficulty.ExtraShots == 2 && Mathf.Approximately(Difficulty.ShotIntervalMultiplier, 0.5f), "difficulty is capped");

        SetField(s_State, "stageIndex", 5);
        var dSpawnerGO = new GameObject("DifficultySpawner");
        dSpawnerGO.transform.SetParent(s_Stage, false);
        dSpawnerGO.transform.position = new Vector3(0f, 0f, 2f);
        var dSpawner = dSpawnerGO.AddComponent<EnemySpawner>();
        SetField(dSpawner, "enemyPrefabs", new[] { Load<Goomba>(GameContentSetup.GoombaPath) });
        SetField(dSpawner, "totalCount", 1);
        int spawnedCount = 0;
        float firstSpeed = 0f;
        for (int i = 0; i < 6; i++)
        {
            var e = dSpawner.Tick(i == 0 ? 2.1f : 3.1f, s_Cam.transform.position);
            if (e == null) continue;
            if (spawnedCount++ == 0) firstSpeed = e.SpeedMultiplier;
            UnityEngine.Object.DestroyImmediate(e.gameObject);
        }
        Check(spawnedCount == 1 && Mathf.Approximately(firstSpeed, 1f), "on stage 5 a 1-enemy spawner still spawns 1, at the same walking speed as stage 1");
        UnityEngine.Object.DestroyImmediate(dSpawnerGO);
        UnityEngine.Object.DestroyImmediate(difficulty);
        SetField(s_State, "stageIndex", 1);

        // -- audio: every sound effect is synthesised and audible
        var audioGO = new GameObject("Audio", typeof(AudioManager));
        var audioManager = audioGO.GetComponent<AudioManager>();
        audioManager.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(audioManager, null);
        bool allAudible = true;
        foreach (Sfx sfx in Enum.GetValues(typeof(Sfx)))
        {
            if (!audioManager.Clips.TryGetValue(sfx, out var clip) || clip.samples == 0) { allAudible = false; continue; }
            var data = new float[clip.samples];
            clip.GetData(data, 0);
            if (data.Max(Mathf.Abs) < 0.05f) allAudible = false;
        }
        Check(allAudible, "every sound effect is synthesised and audible");
        AudioManager.Play(Sfx.Coin); // must not throw
        UnityEngine.Object.DestroyImmediate(audioGO);

        // ==== World 2: the campaign ends in Bowser's castle ====

        int lastStage = StageDesigns.Campaign.Length;
        bool onlyLastEnds = true;
        for (int i = 0; i < lastStage; i++)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.CampaignStagePath(StageDesigns.Campaign[i].Name));
            var goal = prefab != null ? prefab.GetComponentInChildren<StageGoal>() : null;
            if (goal == null || goal.EndsGame != (i == lastStage - 1)) onlyLastEnds = false;
        }
        Check(lastStage == 8 && onlyLastEnds, "8 campaign stages (1-1 to 2-4); only the last one's goal ends the game");

        s_State.SetFireFlower(false);
        var castle = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.CampaignStagePath("2-4")), s_Stage);
        castle.transform.position = new Vector3(0f, 0f, 2f);
        var bowser = castle.GetComponentInChildren<Bowser>();
        var axe = castle.GetComponentInChildren<StageGoal>();
        Register(bowser);
        typeof(Bowser).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(bowser, null);
        Check(bowser != null && axe != null && axe.EndsGame && axe.Locked && bowser.Health == 5 && Bowser.Active == bowser, "2-4: Bowser (5 health) guards a locked axe");
        axe.Initialise(s_Cam.transform); // as LevelGenerator does; the player starts away from it
        StandAt(axe.transform.position);
        r = Resolve("jump");
        Check(r.StateOnly && !axe.IsReached, "a jump at the locked axe does nothing", r);
        s_Cam.transform.position = new Vector3(0f, 1.5f, 0f);
        Invoke(axe, "Update"); // away again

        // -- his fire: a roar (warning), then a flame at head height; squatting ducks it
        var farPlayer = new Vector3(0f, 1.5f, 0.5f);
        BulletBill flame = null;
        bool roared = false;
        bool leapt = false;
        for (int i = 0; i < 60 && flame == null; i++) { flame = bowser.Tick(0.1f, farPlayer); roared |= bowser.IsWarning; leapt |= bowser.IsJumpAttack; }
        Check(roared && flame != null && !leapt, $"from afar ({FlatDistance(bowser.transform.position, farPlayer):F1} m) Bowser roars, then breathes fire");
        livesBefore = s_State.Lives;
        s_State.Duck(1f);
        if (flame != null) FlyBill(flame, farPlayer);
        Check(flame != null && !flame.HitPlayer && s_State.Lives == livesBefore, "squatting ducks Bowser's fire");
        if (flame != null) UnityEngine.Object.DestroyImmediate(flame.gameObject);

        // -- up close he leaps instead: a crouch (the red target follows the player), the leap, a shockwave, then stunned
        for (int i = 0; i < 4; i++) s_State.AddLife(); // the leaps below land a hit
        var post = bowser.transform.position;
        var nearPlayer = new Vector3(post.x, 1.5f, post.z - 1f);
        int bTicks = 0;
        bool breathed = false;
        while (bowser.State != Bowser.Phase.JumpWarning && bTicks++ < 40) breathed |= bowser.Tick(0.1f, nearPlayer) != null;
        Check(bowser.State == Bowser.Phase.JumpWarning && !breathed && bTicks <= 13, $"up close (1 m) he crouches to leap within 1.2 s ({bTicks * 0.1f:F1} s), no fire");
        var marker = bowser.transform.Find("LandingMarker");
        Check(marker != null && marker.gameObject.activeSelf && FlatDistance(marker.position, nearPlayer) < 0.05f, $"a red target marks where he'll land: the player's spot (marker {(marker != null ? marker.position.ToString("F2") : "none")} active {marker != null && marker.gameObject.activeSelf}, player {nearPlayer:F2})");
        while (bowser.State == Bowser.Phase.JumpWarning && bTicks++ < 80) bowser.Tick(0.1f, nearPlayer);
        Check(bowser.State == Bowser.Phase.Leaping && !bowser.RespondsTo("punch") && !bowser.RespondsTo("stomp"), "he leaps; nothing reaches him in the air");
        var touchdown = bowser.LandingPoint;
        var sidestep = nearPlayer + Vector3.right;
        livesBefore = s_State.Lives;
        SetField(s_State, "_invulnerableUntil", float.NegativeInfinity);
        ClearAirborne();
        while (bowser.State == Bowser.Phase.Leaping && bTicks++ < 120) bowser.Tick(0.1f, sidestep);
        Check(bowser.State == Bowser.Phase.Stunned && s_State.Lives == livesBefore && FlatDistance(bowser.transform.position, touchdown) < 0.01f && !marker.gameObject.activeSelf,
              "he lands on the target; sidestepping out of it dodges the shockwave");
        s_Cam.transform.position = new Vector3(touchdown.x, 1.5f, touchdown.z - 0.7f);
        FaceForward();
        r = Resolve("stomp");
        Check(r.Resolved && r.Target == bowser && bowser.Health == 4, "stunned, a stomp hurts him (1)", r);
        while (bowser.State != Bowser.Phase.Pacing && bTicks++ < 200) bowser.Tick(0.1f, farPlayer);
        Check(bowser.State == Bowser.Phase.Pacing && FlatDistance(bowser.transform.position, post) < 0.4f, "then he hops back to his post");

        // A player who stays in the target is hit; one who jumps as he lands isn't
        nearPlayer = new Vector3(bowser.transform.position.x, 1.5f, bowser.transform.position.z - 1f);
        SetField(s_State, "_invulnerableUntil", float.NegativeInfinity);
        livesBefore = s_State.Lives;
        while (bowser.State != Bowser.Phase.Stunned && bTicks++ < 300) bowser.Tick(0.1f, nearPlayer);
        Check(bowser.State == Bowser.Phase.Stunned && s_State.Lives == livesBefore - 1, "staying in the red target: the landing costs a life");
        while (bowser.State != Bowser.Phase.Pacing && bTicks++ < 400) bowser.Tick(0.1f, farPlayer);
        nearPlayer = new Vector3(bowser.transform.position.x, 1.5f, bowser.transform.position.z - 1f);
        SetField(s_State, "_invulnerableUntil", float.NegativeInfinity);
        livesBefore = s_State.Lives;
        while (bowser.State != Bowser.Phase.Leaping && bTicks++ < 500) bowser.Tick(0.1f, nearPlayer);
        s_State.Jump(1f);
        while (bowser.State == Bowser.Phase.Leaping && bTicks++ < 600) bowser.Tick(0.1f, nearPlayer);
        Check(bowser.State == Bowser.Phase.Stunned && s_State.Lives == livesBefore, "jumping as he lands dodges the shockwave");
        ClearAirborne();
        while (bowser.State != Bowser.Phase.Pacing && bTicks++ < 700) bowser.Tick(0.1f, farPlayer);

        // -- then a thrown shell (2), a fireball (1) and a punch (1): defeated, the axe unlocks
        var bp = bowser.transform.position;
        s_State.PickUp(HeldItem.Shell);
        s_Cam.transform.position = new Vector3(bp.x, 1.5f, bp.z - 2f);
        Resolve("throw");
        shell = UnityEngine.Object.FindObjectsByType<ThrownShell>().FirstOrDefault();
        for (int i = 0; i < 60 && shell != null; i++) shell.Tick(0.05f);
        Check(bowser.Health == 2, "a thrown shell hits Bowser once, for 2");
        // His hitbox is his whole body art, not just near HitPoint (his head): legs, belly, head hit; beside or above him misses
        var bodyUp = bowser.transform.up;
        var bodySide = bowser.transform.right;
        float bodyScale = bowser.transform.lossyScale.y;
        Check(bowser.IsHitAt(bp + bodyUp * (0.12f * bodyScale), 0.05f) && bowser.IsHitAt(bp + bodyUp * (0.45f * bodyScale), 0.05f)
              && bowser.IsHitAt(bp + bodyUp * (0.9f * bodyScale), 0.05f),
              "a fireball anywhere on Bowser's body hits: legs, belly, head");
        Check(!bowser.IsHitAt(bp + bodySide * 0.9f + bodyUp * 0.45f, 0.3f) && !bowser.IsHitAt(bp + bodyUp * 1.6f, 0.3f),
              "a fireball beside or above him misses");
        s_State.SetFireFlower(true);
        s_Cam.transform.LookAt(bp + Vector3.up * (0.35f * bodyScale)); // at his belly, well below HitPoint (his head)
        Resolve("throw");
        fb = UnityEngine.Object.FindObjectsByType<Fireball>().FirstOrDefault();
        hit = null;
        for (int i = 0; i < 40 && fb != null && hit == null; i++) hit = fb.Tick(0.05f);
        Check(hit == bowser && bowser.Health == 1, "a fireball at his belly hurts Bowser (1)");
        s_Cam.transform.position = new Vector3(bp.x, 1.5f, bp.z - 0.7f);
        FaceForward();
        scoreBefore = s_State.Score;
        int defeatsBefore = s_State.GameStats.enemiesDefeated;
        r = Resolve("punch");
        Check(r.Resolved && !axe.Locked && s_State.Score == scoreBefore + 5000 && s_State.GameStats.enemiesDefeated == defeatsBefore + 1,
            "the last hit defeats Bowser (+5000) and unlocks the axe", r);

        // -- the goal needs a jump: walking up to it isn't enough
        var ap = axe.transform.position;
        s_Cam.transform.position = new Vector3(ap.x, 1.5f, ap.z - axe.ReachRadius - 0.3f);
        Invoke(axe, "Update");
        r = Resolve("jump");
        Check(r.StateOnly && !axe.IsReached, "a jump out of the axe's reach doesn't grab it", r);
        StandAt(ap);
        for (int i = 0; i < 30; i++) Invoke(axe, "Update");
        Check(!axe.IsReached && axe.InReach, "standing at the unlocked axe doesn't clear it (needs a jump)");
        bool reachedEvent = false;
        axe.OnReached += _ => reachedEvent = true;
        r = Resolve("jump");
        Check(r.Resolved && r.Reason == EventResolver.GoalReason && r.Target == axe && axe.IsReached && reachedEvent, "a jump at the axe grabs it", r);

        // -- GAME CLEAR: play stops; a jump (after the grace period) starts a new game
        s_State.ClearGame();
        Check(s_State.IsGameClear && s_State.IsFinished && !s_State.IsSessionReady, "reaching the axe is GAME CLEAR");
        r = Resolve("punch");
        Check(r.Gate == 1 && r.Reason == "game clear", "actions are dropped after GAME CLEAR", r);
        SetField(s_State, "_gameOverAt", float.NegativeInfinity);
        r = Resolve("jump");
        Check(r.Resolved && !s_State.IsGameClear && s_State.StageIndex == 1 && s_State.Score == 0 && s_State.Held == HeldItem.None && s_State.GameStats.enemiesDefeated == 0,
            "a jump after GAME CLEAR starts a new game", r);
        s_State.BeginStage();
        UnityEngine.Object.DestroyImmediate(castle);
        s_Cam.transform.position = new Vector3(0f, 1.5f, 0f);
        FaceForward();

        // -- game over and restart
        SetField(s_State, "lives", 1);
        SetField(s_State, "_invulnerableUntil", float.NegativeInfinity);
        s_State.TakeHit("test");
        Check(s_State.IsGameOver && !s_State.IsSessionReady, "losing the last life is game over");
        r = Resolve("stomp");
        Check(r.Gate == 1, "actions are dropped during game over", r);
        r = Resolve("idle");
        Check(r.Resolved && r.StateOnly && s_State.IsGameOver, "idle still resolves quietly during game over", r);
        r = Resolve("jump");
        Check(r.Gate == 1 && s_State.IsGameOver, "a jump inside the grace period doesn't restart", r);
        SetField(s_State, "_gameOverAt", float.NegativeInfinity);
        r = Resolve("jump");
        Check(r.Resolved && !s_State.IsGameOver && s_State.Lives == 3 && s_State.Score == 0 && s_State.StageIndex == 1, "a jump after the grace period restarts", r);

        // -- spawner: one wave when the stage starts (the first after the initial delay, the rest 1.5 s apart), then nothing
        s_State.BeginStage();
        var spawnerGO = new GameObject("Spawner");
        spawnerGO.transform.SetParent(s_Stage, false);
        spawnerGO.transform.position = new Vector3(0f, 0f, 2f);
        var spawner = spawnerGO.AddComponent<EnemySpawner>();
        SetField(spawner, "enemyPrefabs", new[] { Load<Goomba>(GameContentSetup.GoombaPath), Load<Koopa>(GameContentSetup.KoopaPath) });
        var far = s_Cam.transform.position;
        var near = new Vector3(0f, 1.5f, 1.5f); // 0.5 m from the spawn point
        Check(spawner.Tick(1f, far) == null, "spawner waits for its initial delay");
        Check(spawner.Tick(1.1f, near) == null, "spawner holds off while the player is near the spawn point");
        var spawned = spawner.Tick(0.1f, far);
        Check(spawned != null, "spawner spawns once the player is clear");
        Check(spawner.Tick(1f, far) == null, "the next of the wave waits its spacing (1.5 s)");
        var second = spawner.Tick(0.6f, far);
        Check(spawned is Koopa && second != null && !(second is Koopa), "Koopas first: the Koopa, then the Goomba (while the Koopa is still alive)");
        UnityEngine.Object.DestroyImmediate(spawned.gameObject);
        UnityEngine.Object.DestroyImmediate(second.gameObject);
        bool more = false;
        for (int i = 0; i < 20; i++) more |= spawner.Tick(3f, far) != null;
        Check(!more && spawner.WaveDone, "once the wave (2) is out and cleared, nothing more spawns");

        // -- stage templates are complete
        var large = AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.LargeStagePath);
        var standard = AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.StandardStagePath);
        var compact = AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.CompactStagePath);
        Check(standard.GetComponent<StageFootprint>() != null && standard.GetComponentInChildren<StageGoal>() != null
              && standard.GetComponentInChildren<QuestionBlock>() != null && standard.GetComponentInChildren<BrickBlock>() != null
              && standard.GetComponentInChildren<EnemySpawner>() != null && standard.GetComponentInChildren<BulletBillCannon>() != null,
              "StandardStage has footprint, goal, ? block, brick, spawner, Bullet Bill cannon");
        Check(compact.GetComponent<StageFootprint>() != null && compact.GetComponentInChildren<StageGoal>() != null
              && compact.GetComponentInChildren<QuestionBlock>() != null, "CompactStage has footprint, goal, ? block");
        Check(large.GetComponent<StageFootprint>() is { } lf && Mathf.Approximately(lf.FootprintArea, 6f) && large.GetComponentInChildren<StageGoal>() != null
              && large.GetComponentsInChildren<QuestionBlock>().Length == 2 && large.GetComponentsInChildren<BrickBlock>().Length == 2
              && large.GetComponentInChildren<EnemySpawner>() != null && large.GetComponentInChildren<BulletBillCannon>() != null
              && large.GetComponentInChildren<WarpPipe>(true) != null, "LargeStage (6 m²) has goal, 2 ? blocks, 2 bricks, spawner, cannon, warp pipe");
        Check(standard.GetComponentInChildren<WarpPipe>(true) != null, "StandardStage has a warp pipe");
        Check(large.GetComponent<StageFootprint>().FootprintArea > standard.GetComponent<StageFootprint>().FootprintArea
              && standard.GetComponent<StageFootprint>().FootprintArea > compact.GetComponent<StageFootprint>().FootprintArea, "templates are tried Large, Standard, Compact");

        ArmActionChecks();
        EnemyIndicatorChecks();
        ObjectiveIndicatorStageChecks();
        RangeRingChecks();
        ProximityFadeChecks();
        PhoneMotionChecks();
        TrainingChecks();
        StartMenuChecks();
    }

    /// <summary>The start menu: nod down / tilt up to move (wrapping), clap to pick; god mode means no lives lost in the game.</summary>
    private static void StartMenuChecks()
    {
        var menu = new GameObject("StartMenu").AddComponent<StartMenu>();
        SetField(menu, "eventResolver", s_Resolver);
        typeof(StartMenu).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(menu, null); // edit mode: not called
        Check(menu.IsOpen && StartMenu.Active == menu && menu.Selected == StartMenu.Item.TrainingAndGame && menu.GodMode,
              "start menu: open, TRAINING + GAME selected, god mode on by default");
        menu.Move(-1);
        Resolve("clap");
        Check(!menu.GodMode && menu.IsOpen, "clap on GOD MODE turns it off");
        menu.Move(1);
        Check(menu.Selected == StartMenu.Item.TrainingAndGame, "back to the top");

        static Vector3 Pitch(float degrees) => Quaternion.Euler(-degrees, 0f, 0f) * Vector3.forward;
        int moved = 0;
        for (int i = 0; i <= 60; i++) moved += Mathf.Abs(menu.Tick(0.05f, Pitch(-i * 0.5f))); // a slow look down to -30° over 3 s
        for (int i = 0; i < 20; i++) moved += Mathf.Abs(menu.Tick(0.05f, Pitch(-30f)));
        Check(moved == 0 && menu.Selected == StartMenu.Item.TrainingAndGame, "looking round slowly doesn't move the selection");

        int nod = 0;
        foreach (var p in new[] { -36f, -44f, -52f, -52f, -44f, -36f, -30f, -30f }) nod += menu.Tick(0.05f, Pitch(p)); // a quick nod down and back
        Check(nod == 1 && menu.Selected == StartMenu.Item.GameOnly, "a quick nod down moves the selection down once");
        for (int i = 0; i < 40; i++) moved += Mathf.Abs(menu.Tick(0.05f, Pitch(-50f))); // nod and stay down: becomes the new neutral
        for (int i = 0; i < 20; i++) moved += Mathf.Abs(menu.Tick(0.05f, Pitch(-50f)));
        Check(moved == 1 && menu.Selected == StartMenu.Item.GameLength, "nodding and holding the head down moves it once, not again");
        int tilt = 0;
        foreach (var p in new[] { -40f, -30f, -30f, -40f, -50f, -50f }) tilt += menu.Tick(0.05f, Pitch(p)); // a quick tilt up and back
        Check(tilt == -1, "a quick tilt up moves the selection up");

        // Directly: wrap round, then pick
        while (menu.Selected != StartMenu.Item.TrainingAndGame) menu.Move(1);
        menu.Move(-1);
        Check(menu.Selected == StartMenu.Item.GodMode, "moving up from the top wraps to GOD MODE");
        Resolve("clap", 0.3f);
        Check(!menu.GodMode, "a low-confidence clap picks nothing");
        Resolve("clap");
        Check(menu.GodMode && menu.IsOpen, "clap on GOD MODE turns it on (the menu stays open)");
        menu.Move(-1);
        Check(!menu.ShortGame, "the full game by default");
        Resolve("clap");
        Check(menu.Selected == StartMenu.Item.GameLength && menu.ShortGame && menu.IsOpen, "clap on GAME: FULL switches to the short game (the menu stays open)");
        menu.Move(-1);
        Resolve("clap");
        Check(!menu.IsOpen && StartMenu.Active == null && s_State.GodMode && s_State.ShortGame, "clap on GAME ONLY starts the short game with god mode on");
        menu.Move(1);
        Resolve("clap");
        Check(!menu.IsOpen && s_State.GodMode, "a closed menu ignores nods and claps");

        s_State.RestartGame();
        Check(s_State.GodMode && s_State.ShortGame, "god mode and the short game survive a new game");

        // The short game: 1-1 to 1-4, then straight to Bowser's castle (2-4); the full game plays every stage
        Check(GameStateManager.FinalStage == StageDesigns.Campaign.Length && GameStateManager.NameOf(GameStateManager.FinalStage) == "2-4",
              "the final stage is the campaign's last, 2-4");
        var shortRun = new List<string>();
        for (int i = 0; i < 6 && s_State.StageIndex <= GameStateManager.FinalStage; i++) { shortRun.Add(s_State.StageName); s_State.CompleteStage(); }
        Check(string.Join(",", shortRun) == "1-1,1-2,1-3,1-4,2-4", $"short game: {string.Join(", ", shortRun)}");
        s_State.ShortGame = false;
        s_State.RestartGame();
        var fullRun = new List<string>();
        for (int i = 0; i < 10 && s_State.StageIndex <= GameStateManager.FinalStage; i++) { fullRun.Add(s_State.StageName); s_State.CompleteStage(); }
        Check(fullRun.Count == 8 && fullRun[4] == "2-1" && fullRun[7] == "2-4", $"full game: {string.Join(", ", fullRun)}");
        s_State.RestartGame();
        s_State.BeginStage();
        int livesBefore = s_State.Lives;
        SetField(s_State, "_invulnerableUntil", float.NegativeInfinity);
        s_State.TakeHit("test");
        Check(!s_State.InTraining && s_State.InfiniteLives && s_State.Lives == livesBefore, "god mode: a hit in the game costs no life");
        s_State.GodMode = false;
        SetField(s_State, "_invulnerableUntil", float.NegativeInfinity);
        s_State.TakeHit("test");
        Check(s_State.Lives == livesBefore - 1, "god mode off: a hit costs a life");

        menu.Open();
        Check(menu.IsOpen && StartMenu.Active == menu && menu.GodMode && menu.ShortGame && menu.Selected == StartMenu.Item.GameOnly, "a hard reset reopens the menu with the last choices");
        UnityEngine.Object.DestroyImmediate(menu.gameObject);
        Check(StartMenu.Active == null, "a destroyed menu isn't left active");
    }

    /// <summary>Safety: objects the player's head is in or at turn see-through; enemies never do.</summary>
    private static void ProximityFadeChecks()
    {
        var fade = new GameObject("ProximityFade").AddComponent<ProximityFade>();
        var ghost = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Ghost.mat");
        SetField(fade, "ghostMaterial", ghost);
        var scene = new GameObject("FadeScene");
        var bush = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        bush.transform.SetParent(scene.transform, false);
        bush.transform.position = new Vector3(0f, 1.5f, 2f);
        bush.transform.localScale = Vector3.one * 0.5f;
        var bushRenderer = bush.GetComponent<Renderer>();
        var original = bushRenderer.sharedMaterial;
        var enemy = UnityEngine.Object.Instantiate(Load<Goomba>(GameContentSetup.GoombaPath), new Vector3(0f, 1.3f, 2f), Quaternion.identity, scene.transform);
        fade.Consider(scene);

        fade.Tick(new Vector3(0f, 1.5f, 0f));
        Check(ghost != null && fade.GhostedCount == 0 && bushRenderer.sharedMaterial == original, "see-through: nothing fades when the head is clear of everything");
        fade.Tick(new Vector3(0f, 1.5f, 2f)); // head inside the bush (and right at the Goomba)
        Check(fade.IsGhosted(bushRenderer) && bushRenderer.sharedMaterial == ghost
              && enemy.GetComponentsInChildren<Renderer>().All(r => !fade.IsGhosted(r)), "the head inside a bush turns it see-through; the enemy stays solid");
        fade.Tick(new Vector3(0f, 1.5f, 0f));
        Check(!fade.IsGhosted(bushRenderer) && bushRenderer.sharedMaterial == original, "stepping out restores it");

        // A ? block hit from right underneath goes spent while ghosted: stepping out keeps the spent look
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.StandardStagePath).transform.Find("QuestionBlock").gameObject;
        var qb = UnityEngine.Object.Instantiate(source, new Vector3(3f, 1.8f, 0f), Quaternion.identity, scene.transform).GetComponent<QuestionBlock>();
        SetField(qb, "oneUpChance", 0f);
        var spent = (Material)typeof(QuestionBlock).GetField("spentMaterial", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(qb);
        var qbRenderers = qb.GetComponentsInChildren<Renderer>();
        fade.Consider(qb.gameObject);
        fade.Tick(new Vector3(3f, 1.6f, 0f)); // head just under the block
        bool ghostedBeforeHit = qbRenderers.All(fade.IsGhosted);
        qb.ResolveHit("jump", s_State);
        fade.Tick(new Vector3(3f, 1.6f, 0f));
        bool stillGhosted = qbRenderers.All(r => fade.IsGhosted(r) && r.sharedMaterial == ghost);
        fade.Tick(new Vector3(3f, 1.5f, -2f));
        Check(spent != null && ghostedBeforeHit && stillGhosted && qbRenderers.All(r => !fade.IsGhosted(r) && r.sharedMaterial == spent),
              "a ? block spent while see-through keeps its spent look after stepping out");
        UnityEngine.Object.DestroyImmediate(scene);
        UnityEngine.Object.DestroyImmediate(fade.gameObject);
    }

    /// <summary>The floor ring at stomp/clap reach: green when a stomp would register, gold for a shell to clap.</summary>
    private static void RangeRingChecks()
    {
        s_State.BeginStage();
        s_Cam.transform.position = new Vector3(0f, 1.5f, 0f);
        FaceForward();
        foreach (var leftover in UnityEngine.Object.FindObjectsByType<Goomba>()) UnityEngine.Object.DestroyImmediate(leftover.gameObject);
        var ring = new GameObject("RangeRing").AddComponent<RangeRing>();
        SetField(ring, "eventResolver", s_Resolver);
        SetField(ring, "arCamera", s_Cam);
        SetField(ring, "material", AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/RangeRing.mat"));

        ring.Tick(0.1f);
        Check(ring.Current == RangeRing.State.Idle, "range ring: idle (dim) with nothing in reach");
        var goomba = UnityEngine.Object.Instantiate(Load<Goomba>(GameContentSetup.GoombaPath), new Vector3(0f, 0f, 0.9f), Quaternion.identity, s_Stage);
        Register(goomba);
        ring.Tick(0.1f);
        Check(ring.Current == RangeRing.State.Stomp && s_Resolver.TargetInReach("stomp") == goomba, "a Goomba 0.9 m ahead: green, a stomp would register");
        goomba.transform.position = new Vector3(0f, 0f, 1.3f);
        ring.Tick(0.1f);
        Check(ring.Current == RangeRing.State.Idle, "at 1.3 m it's out of reach: idle again");
        goomba.transform.position = new Vector3(0f, 0f, -0.9f);
        ring.Tick(0.1f);
        Check(ring.Current == RangeRing.State.Idle, "behind the player (outside the stomp arc): idle");
        UnityEngine.Object.DestroyImmediate(goomba.gameObject);

        var koopa = UnityEngine.Object.Instantiate(Load<Koopa>(GameContentSetup.KoopaPath), new Vector3(0.2f, 0f, 0.8f), Quaternion.identity, s_Stage);
        Register(koopa);
        koopa.ResolveHit("stomp", s_State); // into its shell
        ring.Tick(0.1f);
        Check(ring.Current == RangeRing.State.Clap, "a shell in reach: gold, a clap would pick it up");
        s_State.PickUp(HeldItem.Shell);
        ring.Tick(0.1f);
        Check(ring.Current == RangeRing.State.Idle, "hands full: no clap, and a shell can't be stomped: idle");
        s_State.ThrowHeld();
        UnityEngine.Object.DestroyImmediate(koopa.gameObject);

        s_State.CompleteStage();
        ring.Tick(0.1f);
        Check(ring.Current == RangeRing.State.Hidden, "hidden between stages");
        s_State.BeginStage();
        var root = GameObject.Find("RangeRingVisual");
        UnityEngine.Object.DestroyImmediate(ring.gameObject);
        if (root != null) UnityEngine.Object.DestroyImmediate(root);
    }

    /// <summary>Red arrows towards approaching enemies: on the floor, and at the edge of the view when out of sight.</summary>
    /// <summary>Punch, clap and throw are one "arm" motion: whichever alone would do something here is resolved, whatever was read.</summary>
    private static void ArmActionChecks()
    {
        s_State.BeginStage();
        s_Cam.transform.position = new Vector3(0f, 1.5f, 0f);
        FaceForward();
        s_State.SetFireFlower(false);
        foreach (var t in UnityEngine.Object.FindObjectsByType<InteractableBase>()) if (t is Goomba || t is BrickBlock) UnityEngine.Object.DestroyImmediate(t.gameObject);

        // Only a punch fits (a brick in front, nothing to throw or pick up): a throw or a clap is a punch
        var brick = Spawn<BrickBlock>(GameContentSetup.StandardStagePath, "BrickBlock", new Vector3(0.1f, 1.15f, 0.5f));
        var r = Resolve("throw");
        Check(r.Resolved && r.Target == brick && r.ReadAs == "throw", "arm: a throw in front of a brick (nothing to throw) resolves as a punch, read as throw", r);
        brick = Spawn<BrickBlock>(GameContentSetup.StandardStagePath, "BrickBlock", new Vector3(0.1f, 1.15f, 0.5f));
        r = Resolve("clap");
        Check(r.Resolved && r.Target == brick && r.ReadAs == "clap", "arm: a clap in front of a brick resolves as a punch", r);

        // Only a clap fits (a shell on the floor in front): a punch picks it up
        var koopa = SpawnPrefab<Koopa>(GameContentSetup.KoopaPath, new Vector3(0f, 0f, 0.5f));
        Resolve("stomp");
        r = Resolve("punch");
        Check(r.Resolved && r.Target == koopa && r.ReadAs == "punch" && s_State.Held == HeldItem.Shell, "arm: a punch at a shell on the floor picks it up (a clap)", r);

        // Holding the shell, only a throw fits: a clap or a punch throws it
        r = Resolve("clap");
        Check(r.ThrewShell && r.ReadAs == "clap" && s_State.Held == HeldItem.None, "arm: holding a shell, a clap throws it", r);
        foreach (var t in UnityEngine.Object.FindObjectsByType<ThrownShell>()) UnityEngine.Object.DestroyImmediate(t.gameObject);

        // Several fit (a flower and a brick): the classifier's label stands
        s_State.SetFireFlower(true);
        brick = Spawn<BrickBlock>(GameContentSetup.StandardStagePath, "BrickBlock", new Vector3(0.1f, 1.15f, 0.5f));
        r = Resolve("punch");
        Check(r.Resolved && r.Target == brick && r.ReadAs == null, "arm: a punch where a punch and a throw both fit stays a punch", r);
        foreach (var f in UnityEngine.Object.FindObjectsByType<Fireball>()) UnityEngine.Object.DestroyImmediate(f.gameObject);
        brick = Spawn<BrickBlock>(GameContentSetup.StandardStagePath, "BrickBlock", new Vector3(0.1f, 1.15f, 0.5f));
        r = Resolve("clap");
        Check(!r.Resolved && r.ReadAs == null && brick != null && brick.IsActionable, "arm: a clap where a punch and a throw both fit stays a clap (and does nothing)", r);
        UnityEngine.Object.DestroyImmediate(brick.gameObject);

        // Only a throw fits (the flower, nothing in reach): a punch throws a fireball
        r = Resolve("punch");
        Check(r.LaunchedProjectile && r.ReadAs == "punch", "arm: with a flower and nothing in reach, a punch throws a fireball", r);
        foreach (var f in UnityEngine.Object.FindObjectsByType<Fireball>()) UnityEngine.Object.DestroyImmediate(f.gameObject);

        // None fits: the classifier's label stands
        s_State.SetFireFlower(false);
        r = Resolve("punch");
        Check(r.Gate == 4 && r.ReadAs == null, "arm: when nothing fits, the punch stands (Gate 4)", r);

        // One arm motion read as several arm actions is one motion
        brick = Spawn<BrickBlock>(GameContentSetup.StandardStagePath, "BrickBlock", new Vector3(0.1f, 1.15f, 0.5f));
        r = Resolve("throw");
        var again = ResolveAfter(0.2, "punch");
        Check(r.Resolved && again.IsRepeat, "arm: a punch report 0.2 s after a throw is the same arm motion (repeat)", again);
    }

    private static void EnemyIndicatorChecks()
    {
        s_State.BeginStage();
        s_Cam.transform.position = new Vector3(0f, 1.5f, 0f);
        FaceForward();
        var indicator = new GameObject("EnemyIndicator").AddComponent<EnemyIndicator>();
        SetField(indicator, "arrowPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.EnemyArrowPath));
        SetField(indicator, "arCamera", s_Cam);
        foreach (var leftover in UnityEngine.Object.FindObjectsByType<Goomba>()) UnityEngine.Object.DestroyImmediate(leftover.gameObject);

        indicator.Tick(0.1f);
        Check(indicator.ShownArrows == 0 && !indicator.PointerShown, "enemy indicator: nothing shows with no enemies");

        var goomba = UnityEngine.Object.Instantiate(Load<Goomba>(GameContentSetup.GoombaPath), new Vector3(3f, 0f, 0f), Quaternion.identity, s_Stage); // to the right
        Register(goomba);
        indicator.Tick(0.1f);
        var arrow = indicator.Arrows.FirstOrDefault(a => a.activeSelf);
        Check(indicator.ShownArrows == 1 && arrow != null && Vector3.Dot(arrow.transform.forward, Vector3.right) > 0.99f
              && arrow.transform.position.x > 0.5f && arrow.transform.position.x < 1.5f && Mathf.Abs(arrow.transform.position.y) < 0.05f,
              "a Goomba to the right gets a floor arrow ~1.2 m out, pointing at it");
        Check(indicator.PointerShown && indicator.PointerLocalPosition.x > 0.1f, "it's out of view, so the edge pointer shows on the right");

        goomba.transform.position = new Vector3(0f, 0f, -3f); // behind
        indicator.Tick(0.1f);
        Check(indicator.PointerShown && Mathf.Abs(indicator.PointerLocalPosition.x) > 0.1f, "an enemy behind the player gets the edge pointer at a side");

        goomba.transform.position = new Vector3(0.2f, 0f, 3f); // ahead, in view
        indicator.Tick(0.1f);
        Check(indicator.ShownArrows == 1 && !indicator.PointerShown, "an enemy ahead keeps its floor arrow but needs no edge pointer");

        goomba.transform.position = new Vector3(0f, 0f, 0.5f); // at the player's feet
        indicator.Tick(0.1f);
        Check(indicator.ShownArrows == 0 && !indicator.PointerShown, "an enemy at the player's feet (stomp range) needs no arrow");

        var more = Enumerable.Range(0, 4).Select(i =>
        {
            var g = UnityEngine.Object.Instantiate(Load<Goomba>(GameContentSetup.GoombaPath), new Vector3(-2f - i, 0f, 1f), Quaternion.identity, s_Stage);
            Register(g);
            return g;
        }).ToList();
        indicator.Tick(0.1f);
        Check(indicator.ShownArrows == 3, "at most 3 arrows, for the nearest enemies");

        foreach (var g in more) UnityEngine.Object.DestroyImmediate(g.gameObject);
        UnityEngine.Object.DestroyImmediate(goomba.gameObject);
        foreach (var a in indicator.Arrows) UnityEngine.Object.DestroyImmediate(a);
        var pointer = s_Cam.transform.Find("EnemyPointer");
        if (pointer != null) UnityEngine.Object.DestroyImmediate(pointer.gameObject);
        UnityEngine.Object.DestroyImmediate(indicator.gameObject);
    }

    private static ObjectiveIndicator NewObjectiveIndicator(LevelGenerator generator)
    {
        var indicator = new GameObject("ObjectiveIndicator").AddComponent<ObjectiveIndicator>();
        SetField(indicator, "arrowPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.ObjectiveArrowPath));
        SetField(indicator, "arCamera", s_Cam);
        SetField(indicator, "levelGenerator", generator);
        return indicator;
    }

    private static void DestroyObjectiveIndicator(ObjectiveIndicator indicator)
    {
        if (indicator.Marker != null) UnityEngine.Object.DestroyImmediate(indicator.Marker.gameObject);
        if (indicator.FloorArrow != null) UnityEngine.Object.DestroyImmediate(indicator.FloorArrow.gameObject);
        var pointer = s_Cam.transform.Find("ObjectivePointer");
        if (pointer != null) UnityEngine.Object.DestroyImmediate(pointer.gameObject);
        UnityEngine.Object.DestroyImmediate(indicator.gameObject);
    }

    /// <summary>The gold objective pointer on training step 1: marker over the ? block, floor arrow, edge pointer and the look hint.</summary>
    private static void ObjectiveIndicatorTrainingChecks(Tutorial tutorial)
    {
        var indicator = NewObjectiveIndicator(null);
        var block = tutorial.CurrentRound.GetComponentInChildren<QuestionBlock>();
        Check(tutorial.Objective == block.gameObject, "training objective: step 1's ? block");

        var start = s_Cam.transform.position;
        s_Cam.transform.position = new Vector3(block.transform.position.x, 1.5f, block.transform.position.z - 2f);
        s_Cam.transform.rotation = Quaternion.LookRotation(Vector3.back); // facing away from it
        indicator.Tick(0.1f);
        Check(indicator.Target == block.gameObject && indicator.MarkerShown && indicator.Marker.position.y > block.transform.position.y
              && FlatDistance(indicator.Marker.position, block.transform.position) < 0.1f && Vector3.Dot(indicator.Marker.forward, Vector3.down) > 0.99f,
              "a gold marker floats above the ? block, pointing down at it");
        Check(indicator.FloorArrowShown && Vector3.Dot(indicator.FloorArrow.forward, Vector3.forward) > 0.95f,
              "a gold floor arrow points towards the ? block");
        Check(indicator.PointerShown && indicator.LookHint == "TURN AROUND", "the ? block behind the player: edge pointer and 'TURN AROUND'" + $" ({indicator.LookHint})");

        s_Cam.transform.rotation = Quaternion.LookRotation(Vector3.right);
        indicator.Tick(0.1f);
        Check(indicator.PointerShown && indicator.LookHint == "LOOK LEFT" && indicator.PointerLocalPosition.x < -0.1f,
              "the ? block to the left: 'LOOK LEFT', pointer on the left" + $" ({indicator.LookHint})");

        s_Cam.transform.rotation = Quaternion.LookRotation((block.transform.position - s_Cam.transform.position).normalized);
        indicator.Tick(0.1f);
        Check(!indicator.PointerShown && indicator.LookHint == null && indicator.MarkerShown, "looking at it: no pointer or hint, the marker stays");

        s_Cam.transform.position = new Vector3(block.transform.position.x, 1.5f, block.transform.position.z - 0.3f);
        indicator.Tick(0.1f);
        Check(!indicator.FloorArrowShown, "standing at the ? block: no floor arrow");

        s_Cam.transform.position = start;
        FaceForward();
        DestroyObjectiveIndicator(indicator);
    }

    /// <summary>A new campaign stage out of view: the gold edge pointer and banner hint until it's been seen once.</summary>
    private static void ObjectiveIndicatorStageChecks()
    {
        s_State.RestartGame();
        s_State.BeginStage();
        s_Cam.transform.position = new Vector3(0f, 1.5f, 0f);
        var generator = new GameObject("Generator").AddComponent<LevelGenerator>();
        var stage = new GameObject("Stage");
        stage.transform.position = new Vector3(0f, 0f, -3f); // behind the player
        var footprint = stage.AddComponent<StageFootprint>();
        footprint.width = 1.6f;
        footprint.depth = 3.2f;
        typeof(LevelGenerator).GetProperty("CurrentStage").SetValue(generator, stage);
        var indicator = NewObjectiveIndicator(generator);

        FaceForward();
        indicator.Tick(0.1f);
        Check(indicator.StageUnseen && indicator.PointerShown && indicator.LookHint == "TURN AROUND" && !indicator.MarkerShown && !indicator.FloorArrowShown,
              "a new stage behind the player: edge pointer and 'TURN AROUND' (no marker or floor arrow)" + $" ({indicator.LookHint})");

        s_Cam.transform.rotation = Quaternion.LookRotation(Vector3.back);
        indicator.Tick(0.1f);
        Check(!indicator.StageUnseen && !indicator.PointerShown && indicator.LookHint == null, "once seen, the stage needs no pointer");

        FaceForward();
        indicator.Tick(0.1f);
        Check(!indicator.StageUnseen && !indicator.PointerShown, "and it stays seen when the player looks away again");

        var next = new GameObject("NextStage");
        next.transform.position = new Vector3(3f, 0f, 0f); // to the right
        typeof(LevelGenerator).GetProperty("CurrentStage").SetValue(generator, next);
        indicator.Tick(0.1f);
        Check(indicator.StageUnseen && indicator.LookHint == "LOOK RIGHT", "the next stage, to the right and unseen: 'LOOK RIGHT'" + $" ({indicator.LookHint})");

        DestroyObjectiveIndicator(indicator);
        UnityEngine.Object.DestroyImmediate(next);
        UnityEngine.Object.DestroyImmediate(stage);
        UnityEngine.Object.DestroyImmediate(generator.gameObject);
    }

    /// <summary>Jumps and squats from the phone's own head-height tracking (PhoneMotionDetector).</summary>
    private static void PhoneMotionChecks()
    {
        s_State.BeginStage();
        s_Cam.transform.position = new Vector3(0f, 1.5f, 0f);
        FaceForward();
        var motion = new GameObject("PhoneMotion").AddComponent<PhoneMotionDetector>();
        SetField(motion, "eventResolver", s_Resolver);
        const float dt = 1f / 60f;
        System.Collections.Generic.List<string> Play(float seconds, System.Func<float, float> headY)
        {
            var seen = new System.Collections.Generic.List<string>();
            for (float t = 0f; t < seconds; t += dt)
            {
                s_Now += dt;
                if (motion.Tick(dt, headY(t)) is { } a) seen.Add(a);
            }
            return seen;
        }

        var quiet = Play(2f, _ => 1.5f);
        quiet.AddRange(Play(3f, t => 1.5f + 0.03f * Mathf.Sin(t * Mathf.PI * 4f)));              // walking bob, 3 cm
        quiet.AddRange(Play(1f, t => 1.5f - 0.12f * t));                                            // looking down at the feet
        quiet.AddRange(Play(2f, _ => 1.38f));
        quiet.AddRange(Play(1f, t => 1.38f + 0.12f * t));
        quiet.AddRange(Play(1.5f, t => 1.5f + 0.08f * Mathf.Clamp01(t)));                           // slowly onto tiptoe
        quiet.AddRange(Play(1.5f, t => 1.58f - 0.08f * Mathf.Clamp01(t)));
        Check(quiet.Count == 0 && Mathf.Abs(motion.Baseline - 1.5f) < 0.03f, $"phone motion: standing, walking bob, looking down and tiptoe detect nothing (got {string.Join(",", quiet)})");

        var jump = Play(0.25f, t => 1.5f + 0.22f * (t / 0.25f));                                    // up ~0.9 m/s
        jump.AddRange(Play(0.25f, t => 1.72f - 0.22f * (t / 0.25f)));
        Check(jump.Count == 1 && jump[0] == "jump" && s_State.IsAirborne, "phone motion: a 22 cm jump is one jump (the player is airborne)");

        s_Now += 0.6; // the classifier is slower: its report arrives about a second after the jump
        var late = s_Resolver.Resolve(new GameEvent("jump", 0.95f, $"t-{s_NextId++}", "test"));
        Check(late.IsRepeat, "the classifier's report of the same jump, ~1 s later, is a repeat", late);
        jump.AddRange(Play(1f, _ => 1.5f));

        var squat = Play(0.5f, t => 1.5f - 0.5f * (t / 0.5f));
        squat.AddRange(Play(3f, _ => 1.0f));                                                        // and stays down
        Check(squat.Count == 1 && squat[0] == "squat" && motion.IsSquatting && s_State.IsDucked, "phone motion: a squat is one squat, and holding it keeps the player ducked");
        s_Now += 0.8;
        late = s_Resolver.Resolve(new GameEvent("squat", 0.95f, $"t-{s_NextId++}", "test"));
        Check(late.IsRepeat, "the classifier's squat during the phone's held squat is a repeat", late);
        var up = Play(0.5f, t => 1.0f + 0.5f * (t / 0.5f));
        up.AddRange(Play(1f, _ => 1.5f));
        Check(up.Count == 0 && !motion.IsSquatting, "standing back up ends the squat (no jump)");

        var again = Play(0.25f, t => 1.5f + 0.2f * (t / 0.25f));
        again.AddRange(Play(0.25f, t => 1.7f - 0.2f * (t / 0.25f)));
        Check(again.Count == 1 && again[0] == "jump", "a later jump is detected again");
        var teleport = Play(0.5f, _ => 1.5f);
        teleport.AddRange(Play(2f, _ => 3.0f)); // tracking relocalises 1.5 m higher in one frame
        teleport.AddRange(Play(1f, _ => 3.0f - 0.5f)); // and the same again downward, within warm-up
        Check(teleport.Count == 0, "a tracking jump (start-up, relocalisation) is neither a jump nor a squat");
        UnityEngine.Object.DestroyImmediate(motion.gameObject);
    }

    /// <summary>The training round: seven stations shown one at a time, infinite lives, tips, timeouts, the flag.</summary>
    private static void TrainingChecks()
    {
        s_State.RestartGame();
        s_State.BeginStage();
        s_Cam.transform.position = new Vector3(0f, 1.5f, 0f);
        FaceForward();

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.TrainingStagePath);
        var stage = UnityEngine.Object.Instantiate(prefab, new Vector3(0f, 0f, 1.6f), Quaternion.identity, s_Stage); // the player at the lane's start
        var tutorial = stage.GetComponent<Tutorial>();
        Invoke(tutorial, "OnEnable"); // edit mode: Active, and listening to the resolver
        var stations = Enumerable.Range(1, 7).Select(i => stage.transform.Cast<Transform>().First(t => t.name.StartsWith($"{i} "))).ToArray();
        Check(tutorial != null && tutorial.StepCount == 7 && stations.All(t => !t.gameObject.activeSelf) && tutorial.StepIndex == -1,
              "training: 7 steps, every station hidden until its step");
        var trainingCannon = stations[3].GetComponentInChildren<BulletBillCannon>(true);
        var campaignCannon = AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.StandardStagePath).GetComponentInChildren<BulletBillCannon>(true);
        Check(Mathf.Approximately(GetField<float>(trainingCannon, "interval"), 2.5f) && GetField<float>(campaignCannon, "interval") > 2.5f,
              "the training cannon fires twice as often (2.5 s between shots); campaign cannons are unchanged");
        var goal = stage.GetComponentInChildren<StageGoal>(true);
        goal.Initialise(s_Cam.transform);

        string message = null;
        System.Action<string> onMessage = msg => message = msg;
        Tutorial.OnMessage += onMessage;

        // Infinite lives.
        s_State.StartTraining();
        int lives = s_State.Lives;
        SetField(s_State, "_invulnerableUntil", float.NegativeInfinity);
        bool hit = s_State.TakeHit("test");
        Check(hit && s_State.Lives == lives && s_State.InTraining && s_State.StageName == "0-1" && !s_State.IsGameOver,
              "training: a hit lands but costs no life (infinite lives); the stage is 0-1");
        SetField(s_State, "_invulnerableUntil", float.NegativeInfinity);

        // 1: jump at the ? block, 2 times (a fresh copy each round).
        tutorial.Tick(3f);
        Check(tutorial.StepIndex == -1, "training waits for the intro card before step 1");
        tutorial.Tick(1f);
        Check(tutorial.StepIndex == 0 && tutorial.CurrentRound != null && tutorial.CurrentRound.activeSelf && !stations[0].gameObject.activeSelf
              && tutorial.RoundsRequired == 2, "step 1 (JUMP) plays a copy of its station, 2 rounds");
        var blockSpots = new List<Vector3>();
        ObjectiveIndicatorTrainingChecks(tutorial);
        for (int i = 0; i < 2; i++)
        {
            var round = tutorial.CurrentRound.transform;
            RegisterAll(round);
            var block = round.GetComponentInChildren<QuestionBlock>();
            blockSpots.Add(block.transform.position);
            StandAt(block.transform.position);
            if (i == 0)
            {
                var wrong = Resolve("stomp");
                Check(tutorial.WrongMoveTip(new GameEvent("stomp", 0.95f, "x"), wrong) is { } tip1 && tip1.Contains("STOMP") && tip1.Contains("JUMP"),
                      "a wrong move gets a tip: 'That was STOMP — try JUMP'", wrong);
            }
            var jumped = Resolve("jump");
            Check(jumped.Resolved && jumped.Target == block && tutorial.RoundsDone == i + 1, $"jumping at the ? block counts round {i + 1}/2", jumped);
            tutorial.Tick(1.1f); // the beat, then the next round (or step)
        }
        Check(tutorial.StepIndex == 1, "two ? blocks finish step 1");
        Check(FlatDistance(blockSpots[0], blockSpots[1]) > 0.3f,
              "each ? block round stands somewhere new (walk over to it)");

        // 2: punch the brick, 3 times; a brick broken some other way doesn't count.
        var firstBrick = tutorial.CurrentRound.GetComponentInChildren<BrickBlock>();
        firstBrick.ResolveHit("fireball", s_State);
        tutorial.Tick(0.1f);
        Check(tutorial.RoundsDone == 0 && message.Contains("didn't count") && tutorial.CurrentRound.GetComponentInChildren<BrickBlock>() != null,
              "a brick broken by a fireball doesn't count; a fresh brick appears");
        for (int i = 0; i < 2; i++)
        {
            var round = tutorial.CurrentRound.transform;
            RegisterAll(round);
            var brick = round.GetComponentInChildren<BrickBlock>();
            s_Cam.transform.position = new Vector3(brick.transform.position.x, 1.5f, brick.transform.position.z - 0.5f);
            FaceForward();
            var punched = Resolve("punch");
            Check(punched.Resolved && tutorial.RoundsDone == i + 1, $"punching the brick counts round {i + 1}/2 (hit {(punched.Target != null ? punched.Target.name + " under " + punched.Target.transform.parent?.name : "nothing")})", punched);
            tutorial.Tick(1.1f);
        }
        Check(tutorial.StepIndex == 2, "two bricks finish step 2 (PUNCH)");

        // 3: stomp the Goomba; a fireball kill doesn't count; the 90 s fallback moves on.
        var stompRound = tutorial.CurrentRound.transform;
        {
            var goldCheck = NewObjectiveIndicator(null);
            s_Cam.transform.rotation = Quaternion.LookRotation(Vector3.back); // the Goomba behind
            goldCheck.Tick(0.1f);
            Check(tutorial.Objective == stompRound.GetComponentInChildren<Goomba>().gameObject && goldCheck.Target == null
                  && !goldCheck.MarkerShown && !goldCheck.FloorArrowShown && !goldCheck.PointerShown && goldCheck.LookHint == null,
                  "a walking Goomba objective gets no gold arrows (it has the red enemy arrows)");
            FaceForward();
            DestroyObjectiveIndicator(goldCheck);
        }
        stompRound.GetComponentInChildren<Goomba>().ResolveHit("fireball", s_State);
        tutorial.Tick(0.1f);
        Check(tutorial.RoundsDone == 0 && message.Contains("STOMP"), "a Goomba fireballed in the stomp step doesn't count (\"use STOMP\")");
        RegisterAll(tutorial.CurrentRound.transform);
        var stompee = tutorial.CurrentRound.GetComponentInChildren<Goomba>();
        StandAt(stompee.transform.position + Vector3.back * 0.5f);
        var stomped = Resolve("stomp");
        Check(stomped.Resolved && stomped.Target == stompee && tutorial.RoundsDone == 1, "stomping the Goomba counts round 1/2", stomped);
        tutorial.Tick(1.1f);
        var leftover = tutorial.CurrentRound;
        tutorial.Tick(91f);
        Check(tutorial.StepIndex == 3 && message == "Let's move on" && leftover == null, "a step not finished in 90 s moves on, clearing its round");

        // 4: each Bullet Bill ducked under counts; a squat alone doesn't.
        s_Cam.transform.position = new Vector3(0f, 1.5f, 0f);
        FaceForward();
        Resolve("squat");
        tutorial.Tick(0.1f);
        Check(tutorial.StepIndex == 3 && tutorial.RoundsDone == 0, "a squat with no Bullet Bill doesn't count");
        var duckedField = typeof(BulletBill).GetField("<DuckedUnder>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        for (int i = 0; i < 2; i++)
        {
            var bill = new GameObject($"Bill{i}").AddComponent<BulletBill>();
            duckedField.SetValue(bill, true);
            tutorial.Tick(0.1f);
            tutorial.Tick(0.1f); // the same Bill counts once
            Check(tutorial.RoundsDone == i + 1, $"a Bullet Bill ducked under counts {i + 1}/2");
        }
        tutorial.Tick(1.1f);
        Check(tutorial.StepIndex == 4 && !stations[3].GetComponentInChildren<BulletBillCannon>(true).gameObject.activeSelf && !UnityEngine.Object.FindObjectsByType<BulletBill>().Any(),
              "two ducks finish step 4; the cannon stops and its Bills go");

        // 5: fireballs only (the flower is handed out); a stomp doesn't count.
        Check(s_State.HasFireFlower && message == "Here's a fire flower!", "step 5 (THROW) gives the fire flower if the player hasn't got it");
        RegisterAll(tutorial.CurrentRound.transform);
        var notThisWay = tutorial.CurrentRound.GetComponentInChildren<Goomba>();
        StandAt(notThisWay.transform.position + Vector3.back * 0.5f);
        Resolve("stomp");
        tutorial.Tick(0.1f);
        Check(tutorial.RoundsDone == 0 && message.Contains("THROW"), "stomping the fireball Goomba doesn't count (\"use THROW\")");
        for (int i = 0; i < 2; i++)
        {
            RegisterAll(tutorial.CurrentRound.transform);
            var target = tutorial.CurrentRound.GetComponentInChildren<Goomba>();
            s_Cam.transform.position = new Vector3(target.transform.position.x, 1.5f, target.transform.position.z - 1.5f);
            s_Cam.transform.LookAt(target.HitPoint);
            Resolve("throw");
            var fireball = UnityEngine.Object.FindObjectsByType<Fireball>().FirstOrDefault();
            for (int t = 0; t < 60 && fireball != null && tutorial.RoundsDone == i; t++) fireball.Tick(0.05f);
            Check(tutorial.RoundsDone == i + 1, $"a fireball hit counts round {i + 1}/2");
            foreach (var f in UnityEngine.Object.FindObjectsByType<Fireball>()) UnityEngine.Object.DestroyImmediate(f.gameObject);
            tutorial.Tick(1.1f);
        }
        Check(tutorial.StepIndex == 5, "two fireball hits finish step 5");

        // 6: the shell only (the flower is taken away); the flag isn't there yet.
        Check(!s_State.HasFireFlower, "step 6 takes the fire flower away, so throw can only throw the shell");
        StandAt(goal.transform.position + Vector3.back * 1f);
        Invoke(goal, "Update"); // arm it (the player starts away from it)
        StandAt(goal.transform.position);
        var early = Resolve("jump");
        Check(!goal.IsReached && tutorial.StepIndex == 5, "the hidden flag can't be grabbed before its step", early);
        for (int i = 0; i < 2; i++)
        {
            var round = tutorial.CurrentRound.transform;
            RegisterAll(round);
            var koopa = round.GetComponentInChildren<Koopa>();
            StandAt(koopa.transform.position + Vector3.back * 0.5f);
            if (i == 0)
            {
                var noFire = Resolve("throw");
                Check(noFire.Gate == 3, "no fireballs in the shell step: throw with nothing held is refused", noFire);
            }
            if (i == 0)
            {
                tutorial.Tick(0.1f);
                Check(!round.GetComponentsInChildren<Goomba>().Any(g => !(g is Koopa)) && tutorial.CurrentRound != null && tutorial.CurrentRound.transform == round,
                      "the shell step starts with the Koopa alone (no Goombas yet), and waiting doesn't restart it");
            }
            Resolve("stomp");
            if (i == 0)
            {
                tutorial.Tick(0.1f);
                Check(!round.GetComponentsInChildren<Goomba>().Any(g => !(g is Koopa)), "a stomped shell on the floor: still no Goombas");
            }
            var clap = Resolve("clap");
            tutorial.Tick(0.1f);
            RegisterAll(round);
            var goombas = round.GetComponentsInChildren<Goomba>().Where(g => !(g is Koopa)).ToArray();
            if (i == 0) Check(clap.Resolved && goombas.Length == 3, "once the shell is in hand, 3 Goombas appear to throw it at", clap);
            var goomba = goombas.First();
            s_Cam.transform.position = new Vector3(goomba.transform.position.x, 1.5f, goomba.transform.position.z - 1f);
            FaceForward();
            Resolve("throw");
            var shellFlying = UnityEngine.Object.FindObjectsByType<ThrownShell>().FirstOrDefault();
            for (int t = 0; t < 60 && shellFlying != null && tutorial.RoundsDone == i; t++) shellFlying.Tick(0.05f);
            Check(clap.Resolved && tutorial.RoundsDone == i + 1, $"stomp, clap, throw: the shell's hit counts round {i + 1}/2", clap);
            foreach (var t in UnityEngine.Object.FindObjectsByType<ThrownShell>()) UnityEngine.Object.DestroyImmediate(t.gameObject);
            tutorial.Tick(1.1f);
        }
        Check(tutorial.StepIndex == 6 && stations[6].gameObject.activeSelf && tutorial.RoundsRequired == 1, "two shell hits finish step 6; the flag appears (once)");

        // 7: the flag never times out; a jump at it ends the training.
        tutorial.Tick(100f);
        Check(tutorial.StepIndex == 6, "the flag step never times out");
        bool reached = false;
        goal.OnReached += _ => reached = true;
        StandAt(goal.transform.position);
        var r = Resolve("jump");
        Check(r.Resolved && r.Reason == EventResolver.GoalReason && reached, "a jump at the flag ends training", r);

        // After training: a fresh 1-1.
        s_State.AddScore(1234);
        s_State.EndTraining();
        Check(!s_State.InTraining && s_State.Score == 0 && s_State.StageIndex == 1 && s_State.Lives == 3 && !s_State.HasFireFlower && s_State.StageName == "1-1",
              "after training, 1-1 starts fresh (score 0, 3 lives, no flower)");

        Tutorial.OnMessage -= onMessage;
        Invoke(tutorial, "OnDisable");
        UnityEngine.Object.DestroyImmediate(stage);
    }

    private static void RegisterAll(Transform root)
    {
        foreach (var ib in root.GetComponentsInChildren<InteractableBase>()) Register(ib);
    }

    // ---- helpers ----

    /// <summary>The repeat filter's clock: each Resolve is a separate motion (1 s later) unless a check sets it.</summary>
    private static double s_Now;

    private static ResolutionResult Resolve(string action, float confidence = 0.95f)
    {
        s_Now += 1.0;
        return s_Resolver.Resolve(new GameEvent(action, confidence, $"t-{s_NextId++}", "test"));
    }

    /// <summary>A report <paramref name="secondsLater"/> after the previous one (several reports of one motion).</summary>
    private static ResolutionResult ResolveAfter(double secondsLater, string action, float confidence = 0.95f)
    {
        s_Now += secondsLater;
        return s_Resolver.Resolve(new GameEvent(action, confidence, $"t-{s_NextId++}", "test"));
    }

    private static void Check(bool ok, string what, ResolutionResult? r = null)
    {
        s_Checks++;
        if (ok) Debug.Log($"[GameplaySmokeTest] ok — {what}");
        else s_Failures.Add(r.HasValue ? $"{what} (got gate {r.Value.Gate}: {r.Value.Reason})" : what);
    }

    private static void Invoke(object target, string method) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(target, null);

    /// <summary>Puts the player (camera at eye height) right at a floor point, as if they'd walked there.</summary>
    private static void StandAt(Vector3 floorPoint)
    {
        s_Cam.transform.position = new Vector3(floorPoint.x, 1.5f, floorPoint.z + 0.05f);
        FaceForward();
    }

    private static void FaceForward() => s_Cam.transform.rotation = Quaternion.LookRotation(Vector3.forward);


    /// <summary>Spawns a fresh ? block overhead with a fixed roll, jumps under it, and returns what it dropped (null for a coin).</summary>
    private static Pickup HitFreshBlock(float oneUpChance)
    {
        var existing = new HashSet<Pickup>(s_Stage.GetComponentsInChildren<Pickup>());
        var b = Spawn<QuestionBlock>(GameContentSetup.StandardStagePath, "QuestionBlock", new Vector3(0.1f, 1.8f, 0.2f));
        SetField(b, "oneUpChance", oneUpChance);
        Resolve("jump");
        UnityEngine.Object.DestroyImmediate(b.gameObject);
        return s_Stage.GetComponentsInChildren<Pickup>().FirstOrDefault(p => !existing.Contains(p));
    }

    /// <summary>A Bill 5 m in front of the player at head height, fired straight at them.</summary>
    private static BulletBill LaunchBill(Vector3 eye)
    {
        var bill = SpawnPrefab<BulletBill>(GameContentSetup.BulletBillPath, eye + new Vector3(0f, -0.1f, 5f));
        bill.Launch(Vector3.back);
        return bill;
    }

    /// <summary>Ticks a Bullet Bill until it has passed the player (or gives up).</summary>
    private static void FlyBill(BulletBill bill, Vector3 player)
    {
        for (int i = 0; i < 200 && bill != null && bill.State != BulletBill.Phase.Passed; i++) bill.Tick(0.05f, player);
    }

    private static float FlatDistance(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

    private static void ClearAirborne() => SetField(s_State, "_airborneUntil", float.NegativeInfinity);

    private static float Horizontal(Vector3 p) => new Vector2(p.x - s_Cam.transform.position.x, p.z - s_Cam.transform.position.z).magnitude;

    private static T Load<T>(string path) where T : Component => AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<T>();

    /// <summary>Instantiates a prefab under the stage root and registers it (OnEnable doesn't run in edit mode).</summary>
    private static T SpawnPrefab<T>(string path, Vector3 position) where T : Component
    {
        var instance = UnityEngine.Object.Instantiate(Load<T>(path), position, Quaternion.identity, s_Stage);
        Register(instance);
        return instance;
    }

    /// <summary>Clones one named child of a stage prefab (e.g. its QuestionBlock) into the test scene.</summary>
    private static T Spawn<T>(string stagePath, string childName, Vector3 position) where T : Component
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(stagePath).transform.Find(childName).gameObject;
        var instance = UnityEngine.Object.Instantiate(source, position, Quaternion.identity, s_Stage).GetComponent<T>();
        Register(instance);
        return instance;
    }

    private static void Register(Component c)
    {
        if (c is InteractableBase ib)
            typeof(InteractableBase).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(ib, null);
    }

    private static T GetField<T>(object target, string field)
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null) return (T)f.GetValue(target);
        }
        throw new System.MissingFieldException(target.GetType().Name, field);
    }

    private static void SetField(object target, string field, object value)
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null) { f.SetValue(target, value); return; }
        }
        throw new MissingFieldException(target.GetType().Name, field);
    }
}
