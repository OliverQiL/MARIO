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

        // -- Koopa: stomp → shell (harmless); a second stomp clears it (+200)
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
        Check(r.Resolved && r.Target == k1 && !k1.IsActionable && s_State.Score == scoreBefore + 200, "a second stomp clears the shell (+200)", r);

        // -- clap picks up a shell; throw sends it sliding through a line of Goombas
        s_State.SetFireFlower(false);
        var k3 = SpawnPrefab<Koopa>(GameContentSetup.KoopaPath, new Vector3(0f, 0f, 0.5f));
        r = Resolve("clap");
        Check(r.Gate == 4, "clap at a walking Koopa is Gate 4 (only shells can be picked up)", r);
        Resolve("stomp");
        r = Resolve("clap");
        Check(r.Resolved && r.Target == k3 && s_State.Held == HeldItem.Shell, "clap picks up a Koopa shell", r);
        r = Resolve("clap");
        Check(r.Gate == 3 && r.Reason == "hands full", "clap while holding a shell is Gate 3", r);
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
        Check(cannon.Tick(4.1f, eye) == null && cannon.IsWarning, "after its initial delay the cannon warns first");
        var fired = cannon.Tick(1.6f, eye);
        Check(fired != null && Vector3.Angle(fired.Direction, toEye) < 1f && fired.State == BulletBill.Phase.Flying, "then fires a Bill along its barrel, at the player");
        var nearCannon = cannonHead.position + (eye - cannonHead.position).normalized * 0.8f;
        nearCannon.y = eye.y;
        UnityEngine.Object.DestroyImmediate(fired.gameObject);
        Check(cannon.Tick(11f, nearCannon) == null && !cannon.IsWarning, "cannon holds fire while the player is within 1.2 m");
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

        // -- pieces are placed for the player's body: shorter player, lower pieces
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
        Check(Mathf.Approximately(shortP[0], 1.55f) && Mathf.Approximately(shortP[1], 1.2f * 0.78f) && Mathf.Approximately(shortP[2], 1.2f * 0.6f),
              $"a shorter player (eyes 1.2 m) gets lower pieces (? {shortP[0]:F2}, brick {shortP[1]:F2}, coin {shortP[2]:F2})");
        Check(Mathf.Approximately(tallP[0], 2.2f) && Mathf.Approximately(tallP[1], 1.85f * 0.78f) && Mathf.Approximately(tallP[2], 1.85f * 0.6f),
              $"a taller player (eyes 1.85 m) gets higher pieces (? {tallP[0]:F2}, brick {tallP[1]:F2}, coin {tallP[2]:F2})");
        Check(tallP[0] - 0.125f > 1.85f + 0.12f, "a ? block's bottom clears the top of the player's head");
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
        Check(Mathf.Abs(goombaPrefab.transform.localScale.x - 0.87f) < 0.001f && Mathf.Abs(goalRoot.localScale.x - 0.87f) < 0.001f,
              "gameplay objects (Goomba, flag) are built at 87%");

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
        Check(Difficulty.ExtraEnemies == 0 && Difficulty.EnemySpeedMultiplier == 1f && Difficulty.ExtraShots == 0
              && Difficulty.ShotIntervalMultiplier == 1f && Difficulty.BillSpeedMultiplier == 1f, "stage 1 is the authored baseline");
        SetField(s_State, "stageIndex", 5);
        Check(Difficulty.ExtraEnemies == 2 && Mathf.Approximately(Difficulty.EnemySpeedMultiplier, 1.4f) && Difficulty.ExtraShots == 1
              && Mathf.Approximately(Difficulty.ShotIntervalMultiplier, 0.68f) && Mathf.Approximately(Difficulty.BillSpeedMultiplier, 1.2f), "stage 5 adds enemies, speed and shots");
        SetField(s_State, "stageIndex", 40);
        Check(Difficulty.ExtraEnemies == 3 && Mathf.Approximately(Difficulty.EnemySpeedMultiplier, 1.8f) && Difficulty.ExtraShots == 2
              && Mathf.Approximately(Difficulty.ShotIntervalMultiplier, 0.5f) && Mathf.Approximately(Difficulty.BillSpeedMultiplier, 1.5f), "difficulty is capped");

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
        Check(spawnedCount == 3 && Mathf.Approximately(firstSpeed, 1.4f), "on stage 5 a 1-enemy spawner spawns 3, walking 1.4× faster");
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
        for (int i = 0; i < 60 && flame == null; i++) { flame = bowser.Tick(0.1f, farPlayer); roared |= bowser.IsWarning; }
        Check(roared && flame != null, "Bowser roars, then breathes fire");
        livesBefore = s_State.Lives;
        s_State.Duck(1f);
        if (flame != null) FlyBill(flame, farPlayer);
        Check(flame != null && !flame.HitPlayer && s_State.Lives == livesBefore, "squatting ducks Bowser's fire");
        if (flame != null) UnityEngine.Object.DestroyImmediate(flame.gameObject);

        // -- punch (1), thrown shell (2), fireball (1), punch (1): defeated, the axe unlocks
        var bp = bowser.transform.position;
        s_Cam.transform.position = new Vector3(bp.x, 1.5f, bp.z - 0.7f);
        FaceForward();
        r = Resolve("stomp");
        Check(r.Gate == 4, "Bowser can't be stomped", r);
        r = Resolve("punch");
        Check(r.Resolved && r.Target == bowser && bowser.Health == 4, "a punch hurts Bowser (1)", r);
        s_State.PickUp(HeldItem.Shell);
        s_Cam.transform.position = new Vector3(bp.x, 1.5f, bp.z - 2f);
        Resolve("throw");
        shell = UnityEngine.Object.FindObjectsByType<ThrownShell>().FirstOrDefault();
        for (int i = 0; i < 60 && shell != null; i++) shell.Tick(0.05f);
        Check(bowser.Health == 2, "a thrown shell hits Bowser once, for 2");
        s_State.SetFireFlower(true);
        s_Cam.transform.LookAt(bowser.HitPoint);
        Resolve("throw");
        fb = UnityEngine.Object.FindObjectsByType<Fireball>().FirstOrDefault();
        hit = null;
        for (int i = 0; i < 40 && fb != null && hit == null; i++) hit = fb.Tick(0.05f);
        Check(hit == bowser && bowser.Health == 1, "a fireball hurts Bowser (1)");
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

        // -- spawner: first enemy after the initial delay, one at a time
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
        Check(spawner.Tick(5f, far) == null, "spawner waits while its enemy is alive");
        UnityEngine.Object.DestroyImmediate(spawned.gameObject);
        Check(spawner.Tick(3.1f, far) is Koopa, "spawner's second enemy is a Koopa");

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

        TrainingChecks();
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

        // 1: jump at the ? block.
        tutorial.Tick(3f);
        Check(tutorial.StepIndex == -1, "training waits for the intro card before step 1");
        tutorial.Tick(1f);
        Check(tutorial.StepIndex == 0 && stations[0].gameObject.activeSelf && !stations[1].gameObject.activeSelf, "step 1 (JUMP) shows only its station");
        RegisterAll(stations[0]);
        var block = stations[0].GetComponentInChildren<QuestionBlock>();
        StandAt(block.transform.position);
        var r = Resolve("stomp");
        Check(tutorial.WrongMoveTip(new GameEvent("stomp", 0.95f, "x"), r) is { } tip1 && tip1.Contains("STOMP") && tip1.Contains("JUMP"),
              "a wrong move gets a tip: 'That was STOMP — try JUMP'", r);
        r = Resolve("jump");
        tutorial.Tick(0.1f);
        Check(r.Resolved && r.Target == block && tutorial.StepIndex == 1, "hitting the ? block finishes step 1", r);

        // 2: punch the brick.
        RegisterAll(stations[1]);
        var brick = stations[1].GetComponentInChildren<BrickBlock>();
        s_Cam.transform.position = new Vector3(brick.transform.position.x, 1.5f, brick.transform.position.z - 0.5f);
        FaceForward();
        r = Resolve("punch");
        tutorial.Tick(0.1f);
        Check(r.Resolved && tutorial.StepIndex == 2, "breaking the brick finishes step 2 (PUNCH)", r);

        // 3: a step not done in 40 s moves on, and its enemy is cleared.
        var stompGoomba = stations[2].GetComponentInChildren<Goomba>();
        tutorial.Tick(41f);
        Check(tutorial.StepIndex == 3 && message == "Let's move on" && stompGoomba == null, "a step not done after 40 s moves on, clearing its enemy");

        // 4: squat while a Bullet Bill is coming.
        Resolve("squat");
        tutorial.Tick(0.1f);
        Check(tutorial.StepIndex == 3, "a squat with no Bullet Bill coming doesn't finish step 4");
        var bill = new GameObject("Bill").AddComponent<BulletBill>();
        Resolve("squat");
        tutorial.Tick(0.1f);
        Check(tutorial.StepIndex == 4 && bill == null && !stations[3].GetComponentInChildren<BulletBillCannon>(true).gameObject.activeSelf,
              "ducking a Bullet Bill finishes step 4; its cannon stops");

        // 5: the fireball step hands out the flower if needed.
        Check(s_State.HasFireFlower && message == "Here's a fire flower!", "step 5 (THROW) gives the fire flower if the player hasn't got it");
        foreach (var g in stations[4].GetComponentsInChildren<Goomba>()) g.ResolveHit("fireball", s_State);
        tutorial.Tick(0.1f);
        Check(tutorial.StepIndex == 5, "defeating the Goomba finishes step 5");

        // 6: the flag isn't there yet.
        StandAt(goal.transform.position + Vector3.back * 1f);
        Invoke(goal, "Update"); // arm it (the player starts away from it)
        StandAt(goal.transform.position);
        r = Resolve("jump");
        Check(!goal.IsReached && tutorial.StepIndex == 5, "the hidden flag can't be grabbed before its step", r);
        foreach (var e in stations[5].GetComponentsInChildren<Goomba>().Where(g => !(g is Koopa))) e.ResolveHit("stomp", s_State);
        foreach (var k in stations[5].GetComponentsInChildren<Koopa>()) k.ResolveHit("stomp", s_State); // into the shell...
        tutorial.Tick(0.1f);
        Check(tutorial.StepIndex == 5, "a Koopa still in its shell keeps step 6 going");
        foreach (var k in stations[5].GetComponentsInChildren<Koopa>()) k.ResolveHit("stomp", s_State); // ...and cleared
        tutorial.Tick(0.1f);
        Check(tutorial.StepIndex == 6 && stations[6].gameObject.activeSelf, "clearing the Koopa and Goombas finishes step 6; the flag appears");

        // 7: the flag never times out; a jump at it ends the round.
        tutorial.Tick(100f);
        Check(tutorial.StepIndex == 6, "the flag step never times out");
        bool reached = false;
        goal.OnReached += _ => reached = true;
        r = Resolve("jump");
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
