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
        s_State.BeginStage();
        s_Stage = new GameObject("StageRoot").transform; // floor at y = 0

        // -- state-only actions
        var r = Resolve("walk");
        Check(r.Resolved && r.StateOnly, "walk resolves as state-only", r);
        Check(s_State.MovementState == "walk", "walk sets movement state");
        r = Resolve("squat");
        Check(r.Resolved && r.StateOnly && s_State.IsDucked, "squat ducks the player", r);
        r = Resolve("jump");
        Check(r.Resolved && r.StateOnly && s_State.IsAirborne, "jump with no block is airborne, state-only", r);

        // -- "?" block: jump underneath drops a fire flower (no flower yet), then it's spent
        var block = Spawn<QuestionBlock>(GameContentSetup.StandardStagePath, "QuestionBlock", new Vector3(0.1f, 1.8f, 0.2f));
        r = Resolve("jump");
        Check(r.Resolved && r.Target == block, "jump under ? block hits it", r);
        var flower = s_Stage.GetComponentInChildren<Pickup>();
        Check(flower != null && flower.PickupKind == Pickup.Kind.FireFlower, "? block drops a fire flower when the player has none");
        r = Resolve("jump");
        Check(r.Gate == 5, "second jump on the spent block is Gate 5", r);

        // -- fire flower pickup
        Check(flower != null && flower.TryCollect(s_Cam.transform.position) && s_State.HasFireFlower, "walking to the flower collects it");

        // -- fireball projectile hits a Goomba the player is looking at
        var g1 = SpawnPrefab<Goomba>(GameContentSetup.GoombaPath, new Vector3(0f, 0f, 2f));
        s_Cam.transform.LookAt(g1.HitPoint);
        int scoreBefore = s_State.Score;
        Component impacted = null;
        s_Resolver.OnImpact += (_, t) => impacted = t;
        r = Resolve("fireball");
        Check(r.LaunchedProjectile && Fireball.InFlight == 1, "fireball with flower launches a projectile", r);
        var fb = UnityEngine.Object.FindObjectsByType<Fireball>(FindObjectsSortMode.None).FirstOrDefault();
        InteractableBase hit = null;
        for (int i = 0; i < 40 && fb != null && hit == null; i++) hit = fb.Tick(0.05f);
        Check(hit == g1 && !g1.IsActionable && s_State.Score == scoreBefore + 100 && impacted == g1, "fireball hits the Goomba (+100, OnImpact)");
        Check(Fireball.InFlight == 0, "fireball is gone after the hit");
        FaceForward();

        // -- Gate 3: at most 2 fireballs in flight
        Resolve("fireball");
        Resolve("fireball");
        r = Resolve("fireball");
        Check(r.Gate == 3, "third fireball in flight is Gate 3", r);
        foreach (var f in UnityEngine.Object.FindObjectsByType<Fireball>(FindObjectsSortMode.None)) UnityEngine.Object.DestroyImmediate(f.gameObject);

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

        // -- bare-handed strike: brick at chest height in reach; nothing in reach → Gate 4
        var brick = Spawn<BrickBlock>(GameContentSetup.StandardStagePath, "BrickBlock", new Vector3(0.1f, 1.15f, 0.5f));
        scoreBefore = s_State.Score;
        r = Resolve("fireball");
        Check(r.Resolved && r.Target == brick && s_State.Score == scoreBefore + 50, "fireball without flower strikes a brick (+50)", r);
        r = Resolve("fireball");
        Check(r.Gate == 4, "strike with no brick in reach is Gate 4", r);
        r = Resolve("stomp");
        Check(r.Gate == 4, "stomp with no enemy in range is Gate 4", r);

        // -- game over and restart
        SetField(s_State, "lives", 1);
        SetField(s_State, "_invulnerableUntil", float.NegativeInfinity);
        s_State.TakeHit("test");
        Check(s_State.IsGameOver && !s_State.IsSessionReady, "losing the last life is game over");
        r = Resolve("stomp");
        Check(r.Gate == 1, "actions are dropped during game over", r);
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
        SetField(spawner, "enemyPrefab", Load<Goomba>(GameContentSetup.GoombaPath));
        var far = s_Cam.transform.position;
        var near = new Vector3(0f, 1.5f, 1.5f); // 0.5 m from the spawn point
        Check(spawner.Tick(1f, far) == null, "spawner waits for its initial delay");
        Check(spawner.Tick(1.1f, near) == null, "spawner holds off while the player is near the spawn point");
        var spawned = spawner.Tick(0.1f, far);
        Check(spawned != null, "spawner spawns once the player is clear");
        Check(spawner.Tick(5f, far) == null, "spawner waits while its enemy is alive");

        // -- stage templates are complete
        var standard = AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.StandardStagePath);
        var compact = AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.CompactStagePath);
        Check(standard.GetComponent<StageFootprint>() != null && standard.GetComponentInChildren<StageGoal>() != null
              && standard.GetComponentInChildren<QuestionBlock>() != null && standard.GetComponentInChildren<BrickBlock>() != null
              && standard.GetComponentInChildren<EnemySpawner>() != null, "StandardStage has footprint, goal, ? block, brick, spawner");
        Check(compact.GetComponent<StageFootprint>() != null && compact.GetComponentInChildren<StageGoal>() != null
              && compact.GetComponentInChildren<QuestionBlock>() != null, "CompactStage has footprint, goal, ? block");
        Check(standard.GetComponent<StageFootprint>().FootprintArea > compact.GetComponent<StageFootprint>().FootprintArea, "Standard is tried before Compact");
    }

    // ---- helpers ----

    private static ResolutionResult Resolve(string action, float confidence = 0.95f) =>
        s_Resolver.Resolve(new GameEvent(action, confidence, $"t-{s_NextId++}", "test"));

    private static void Check(bool ok, string what, ResolutionResult? r = null)
    {
        s_Checks++;
        if (ok) Debug.Log($"[GameplaySmokeTest] ok — {what}");
        else s_Failures.Add(r.HasValue ? $"{what} (got gate {r.Value.Gate}: {r.Value.Reason})" : what);
    }

    private static void FaceForward() => s_Cam.transform.rotation = Quaternion.LookRotation(Vector3.forward);

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
