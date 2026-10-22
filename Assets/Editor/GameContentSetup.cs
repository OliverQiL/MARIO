using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds pass-1 to pass-3 game content (Docs/GAME_DESIGN.md) from primitives
/// and wires it into the scene. Idempotent — rebuilds every asset it owns.
/// - Materials (Assets/Materials), element prefabs (Assets/Prefabs):
///   CoinPop, FireFlower, OneUp, Goomba, Koopa, BulletBill, Fireball
/// - Stage templates (Assets/Stages), tried largest first:
///   LargeStage 2.0×3.0 m (2 ? blocks, 2 bricks, spawner, cannon, warp pipe, flag),
///   StandardStage 1.2×2.0 m (? block, brick, Goomba-then-Koopa spawner,
///   Bullet Bill cannon, warp pipe, flag) and CompactStage 0.6×1.0 m (? block, flag)
/// - GameManager: Difficulty and AudioManager components
/// - Scene: LevelGenerator templates, EventResolver fireball prefab, the stage
///   loop's events (LevelGenerator.OnStageStarted/OnStageCompleted →
///   GameStateManager.BeginStage/CompleteStage; OnGameOver/OnGameRestarted →
///   LevelGenerator.EndGame/RestartLoop),
///   scan target above the largest footprint.
/// Each stage's +Z points away from the player (LevelGenerator orients it so).
///
/// Menu:     Tools ▸ MARIO ▸ Build Game Content
/// Headless: Unity -batchmode -quit -projectPath . -executeMethod GameContentSetup.Run
/// </summary>
public static class GameContentSetup
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string MaterialDir = "Assets/Materials";
    private const string PrefabDir = "Assets/Prefabs";
    private const string StageDir = "Assets/Stages";

    public const string FireballPath = PrefabDir + "/Fireball.prefab";
    public const string ThrownShellPath = PrefabDir + "/ThrownShell.prefab";
    public const string BowserPath = PrefabDir + "/Bowser.prefab";
    public const string BossFlamePath = PrefabDir + "/BossFlame.prefab";
    public const string GoombaPath = PrefabDir + "/Goomba.prefab";
    public const string FireFlowerPath = PrefabDir + "/FireFlower.prefab";
    public const string OneUpPath = PrefabDir + "/OneUp.prefab";
    public const string BrickDebrisPath = PrefabDir + "/BrickDebris.prefab";
    public const string SparklePath = PrefabDir + "/Effects/Sparkle.prefab";
    public const string PoofPath = PrefabDir + "/Effects/Poof.prefab";
    public const string FireBurstPath = PrefabDir + "/Effects/FireBurst.prefab";
    public const string SmokePath = PrefabDir + "/Effects/Smoke.prefab";
    public const string ConfettiPath = PrefabDir + "/Effects/Confetti.prefab";
    private const string BlockMeshPath = PrefabDir + "/BlockCube.asset";
    private const string PennantMeshPath = PrefabDir + "/Pennant.asset";
    public const string KoopaPath = PrefabDir + "/Koopa.prefab";
    public const string BulletBillPath = PrefabDir + "/BulletBill.prefab";
    public const string CoinPopPath = PrefabDir + "/CoinPop.prefab";
    public const string StandardStagePath = StageDir + "/StandardStage.prefab";
    public const string TrainingStagePath = StageDir + "/TrainingStage.prefab";
    public const string CompactStagePath = StageDir + "/CompactStage.prefab";
    public const string LargeStagePath = StageDir + "/LargeStage.prefab";
    public const string CampaignDir = StageDir + "/Campaign";
    public const string CoinPath = PrefabDir + "/Coin.prefab";
    public const string ExitPipePath = PrefabDir + "/ExitPipe.prefab";
    public const string WallBlockPath = PrefabDir + "/UndergroundBlock.prefab";
    public const string BonusRoomPath = PrefabDir + "/BonusRoom.prefab";
    public static string CampaignStagePath(string name) => $"{CampaignDir}/Stage {name}.prefab";

    [MenuItem("Tools/MARIO/Build Game Content")]
    public static void Run()
    {
        Directory.CreateDirectory(PrefabDir);
        Directory.CreateDirectory(StageDir);

        var m = new Mats();
        BuildEffects(m); // loaded by path by the elements that use them
        var coinPop = BuildCoinPop(m);
        BuildBrickDebris(m); // loaded by path when bricks are built
        var pickups = new Pickups(BuildFireFlower(m), BuildOneUp(m));
        var goomba = BuildGoomba(m);
        var koopa = BuildKoopa(m);
        var bulletBill = BuildBulletBill(m);
        var fireball = BuildFireball(m);
        var thrownShell = BuildThrownShell(m);
        var bowser = BuildBowser(m, BuildBossFlame(m));
        var standard = BuildStandardStage(m, coinPop, pickups, goomba, koopa, bulletBill);
        var compact = BuildCompactStage(m, coinPop, pickups);
        var large = BuildLargeStage(m, coinPop, pickups, goomba, koopa, bulletBill);
        var bonusRoom = BuildBonusRoom(m);
        var training = BuildTrainingStage(m, coinPop, pickups, goomba, koopa, bulletBill);
        var campaign = new System.Collections.Generic.List<GameObject>();
        foreach (var design in StageDesigns.Campaign)
            campaign.Add(BuildCampaignStage(design, m, coinPop, pickups, goomba, koopa, bulletBill, bowser));

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var levelGenerator = Object.FindAnyObjectByType<LevelGenerator>();
        var gameState = Object.FindAnyObjectByType<GameStateManager>();
        var resolver = Object.FindAnyObjectByType<EventResolver>();
        var session = Object.FindAnyObjectByType<ARSessionManager>();

        var lg = new SerializedObject(levelGenerator);
        var templates = lg.FindProperty("stageTemplates");
        templates.arraySize = 3;
        templates.GetArrayElementAtIndex(0).objectReferenceValue = large;
        templates.GetArrayElementAtIndex(1).objectReferenceValue = standard;
        templates.GetArrayElementAtIndex(2).objectReferenceValue = compact;
        var campaignProp = lg.FindProperty("campaignStages");
        campaignProp.arraySize = campaign.Count;
        for (int i = 0; i < campaign.Count; i++) campaignProp.GetArrayElementAtIndex(i).objectReferenceValue = campaign[i];
        lg.FindProperty("bonusRoomPrefab").objectReferenceValue = bonusRoom;
        lg.FindProperty("trainingStage").objectReferenceValue = training;
        // Search the whole scanned room (out to 6 m), not just in front of the player:
        // full-size (4 m deep) stages need their centre 2.3 m+ away.
        var distances = lg.FindProperty("searchDistances");
        var wanted = new[] { 0.5f, 1f, 1.5f, 2f, 2.5f, 3f, 3.5f, 4f, 4.5f, 5f, 5.5f, 6f };
        distances.arraySize = wanted.Length;
        for (int i = 0; i < wanted.Length; i++) distances.GetArrayElementAtIndex(i).floatValue = wanted[i];
        lg.ApplyModifiedPropertiesWithoutUndo();

        var er = new SerializedObject(resolver);
        er.FindProperty("fireballPrefab").objectReferenceValue = fireball.GetComponent<Fireball>();
        er.FindProperty("thrownShellPrefab").objectReferenceValue = thrownShell.GetComponent<ThrownShell>();
        er.ApplyModifiedPropertiesWithoutUndo();

        // The stage loop's events: LevelGenerator ⇄ GameStateManager (persistent listeners).
        AddListenerOnce(levelGenerator.OnStageStarted ??= new UnityEngine.Events.UnityEvent(), gameState.BeginStage);
        AddListenerOnce(levelGenerator.OnStageCompleted ??= new UnityEngine.Events.UnityEvent(), gameState.CompleteStage);
        AddListenerOnce(gameState.OnGameOver, levelGenerator.EndGame);
        AddListenerOnce(gameState.OnGameRestarted, levelGenerator.RestartLoop);
        EditorUtility.SetDirty(levelGenerator);
        EditorUtility.SetDirty(gameState);

        // The player scans the room and confirms with a jump (Oliver 2026-09-30); 12 m² is the
        // HUD's guidance for a full-size stage, and at least 3 m² (Standard's worth) is needed to confirm.
        var ss = new SerializedObject(session);
        ss.FindProperty("fullSizeFloorArea").floatValue = 12f;
        ss.FindProperty("minFloorArea").floatValue = 3f;
        ss.ApplyModifiedPropertiesWithoutUndo();

        BuildHeldItemView(m);

        var manager = gameState.gameObject;
        if (manager.GetComponent<Difficulty>() == null) manager.AddComponent<Difficulty>();
        // Hard reset from inside the headset: look straight up for 5 s.
        var hardReset = manager.GetComponent<HardReset>();
        if (hardReset == null) hardReset = manager.AddComponent<HardReset>();
        Set(hardReset, "arCamera", Camera.main);
        var audio = manager.GetComponent<AudioManager>();
        if (audio == null) audio = manager.AddComponent<AudioManager>(); // adds its AudioSource
        var am = new SerializedObject(audio);
        am.FindProperty("eventResolver").objectReferenceValue = resolver;
        am.FindProperty("gameState").objectReferenceValue = gameState;
        am.FindProperty("levelGenerator").objectReferenceValue = levelGenerator;
        am.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log($"[GameContentSetup] Done. Built CoinPop, FireFlower, OneUp, Goomba, Koopa, BulletBill, Fireball, ThrownShell, Bowser, LargeStage, StandardStage, CompactStage, BonusRoom, {StageDesigns.Campaign.Length} campaign stages; scene wired.");
    }

    // ---- element prefabs ----

    /// <summary>Coin: gold disc with a darker rim and a raised bar on each face.</summary>
    private static GameObject BuildCoinPop(Mats m)
    {
        var root = new GameObject("CoinPop");
        root.AddComponent<CoinPop>();
        Visual(PrimitiveType.Cylinder, "Rim", root.transform, Vector3.zero, new Vector3(0.13f, 0.007f, 0.13f), m.CoinRim, new Vector3(90f, 0f, 0f));
        Visual(PrimitiveType.Cylinder, "Face", root.transform, Vector3.zero, new Vector3(0.11f, 0.009f, 0.11f), m.Coin, new Vector3(90f, 0f, 0f));
        Visual(PrimitiveType.Cube, "Bar", root.transform, Vector3.zero, new Vector3(0.022f, 0.065f, 0.026f), m.CoinShine);
        return Save(Shrink(root), CoinPopPath);
    }

    /// <summary>Fire flower: red outer petals, a yellow ring and a cream centre with eyes, on a stem with two leaves.</summary>
    private static GameObject BuildFireFlower(Mats m)
    {
        var root = new GameObject("FireFlower");
        Set(root.AddComponent<Pickup>(), "collectEffect", Effect(SparklePath)); // Kind defaults to FireFlower
        Visual(PrimitiveType.Cylinder, "Stem", root.transform, new Vector3(0f, 0.07f, 0f), new Vector3(0.022f, 0.07f, 0.022f), m.Stem);
        Visual(PrimitiveType.Sphere, "LeafL", root.transform, new Vector3(-0.045f, 0.045f, 0f), new Vector3(0.08f, 0.025f, 0.045f), m.Stem, new Vector3(0f, 0f, 25f));
        Visual(PrimitiveType.Sphere, "LeafR", root.transform, new Vector3(0.045f, 0.045f, 0f), new Vector3(0.08f, 0.025f, 0.045f), m.Stem, new Vector3(0f, 0f, -25f));
        var head = Group("Head", root.transform, new Vector3(0f, 0.19f, 0f));
        Visual(PrimitiveType.Sphere, "Petals", head, Vector3.zero, new Vector3(0.17f, 0.13f, 0.05f), m.Flower);
        Visual(PrimitiveType.Sphere, "Ring", head, Vector3.zero, new Vector3(0.12f, 0.09f, 0.06f), m.FlowerRing);
        Visual(PrimitiveType.Sphere, "Centre", head, Vector3.zero, new Vector3(0.07f, 0.05f, 0.07f), m.FlowerCentre);
        foreach (int side in new[] { -1, 1 })
            foreach (int face in new[] { -1, 1 }) // eyes on both faces: it spins
                Visual(PrimitiveType.Sphere, "Eye", head, new Vector3(0.012f * side, 0.004f, 0.034f * face), new Vector3(0.01f, 0.022f, 0.008f), m.Black);
        return Save(Shrink(root), FireFlowerPath);
    }

    /// <summary>1-Up mushroom: green cap dotted with white spots all round, cream stalk with eyes.</summary>
    private static GameObject BuildOneUp(Mats m)
    {
        var root = new GameObject("OneUp");
        var p = root.AddComponent<Pickup>();
        var so = new SerializedObject(p);
        so.FindProperty("kind").enumValueIndex = (int)Pickup.Kind.OneUp;
        so.FindProperty("collectEffect").objectReferenceValue = Effect(SparklePath);
        so.ApplyModifiedPropertiesWithoutUndo();
        Visual(PrimitiveType.Sphere, "Stalk", root.transform, new Vector3(0f, 0.07f, 0f), new Vector3(0.14f, 0.14f, 0.14f), m.GoombaFace);
        foreach (int side in new[] { -1, 1 })
            foreach (int face in new[] { -1, 1 })
                Visual(PrimitiveType.Sphere, "Eye", root.transform, new Vector3(0.024f * side, 0.075f, 0.066f * face), new Vector3(0.018f, 0.04f, 0.012f), m.Black);

        var capCentre = new Vector3(0f, 0.16f, 0f);
        var capRadii = new Vector3(0.12f, 0.085f, 0.12f);
        Visual(PrimitiveType.Sphere, "Cap", root.transform, capCentre, capRadii * 2f, m.OneUp);
        // Spots sit on the cap's surface, flat against it: one on top, four around the sides.
        var spots = new (float yaw, float elevation, float size)[] { (0f, 90f, 0.07f), (0f, 25f, 0.06f), (90f, 25f, 0.06f), (180f, 25f, 0.06f), (270f, 25f, 0.06f) };
        foreach (var (yaw, elevation, size) in spots)
        {
            float y = elevation * Mathf.Deg2Rad, a = yaw * Mathf.Deg2Rad;
            var unit = new Vector3(Mathf.Cos(y) * Mathf.Sin(a), Mathf.Sin(y), Mathf.Cos(y) * Mathf.Cos(a));
            var pos = capCentre + Vector3.Scale(unit, capRadii);
            var normal = new Vector3(unit.x / capRadii.x, unit.y / capRadii.y, unit.z / capRadii.z).normalized;
            var spot = Visual(PrimitiveType.Sphere, "Spot", root.transform, pos, new Vector3(size, size, 0.02f), m.White);
            spot.transform.localRotation = Quaternion.LookRotation(normal, Mathf.Abs(normal.y) > 0.99f ? Vector3.forward : Vector3.up);
        }
        return Save(Shrink(root), OneUpPath);
    }

    /// <summary>Koopa: patterned green shell with a white rim, yellow head with a snout, big eyes, yellow feet.</summary>
    private static GameObject BuildKoopa(Mats m)
    {
        var root = new GameObject("Koopa");
        var k = root.AddComponent<Koopa>();
        Set(k, "stompEffect", Effect(PoofPath));
        Shadow(root.transform, 0.36f, m);
        var model = Group("Model", root.transform, Vector3.zero);
        model.gameObject.AddComponent<EnemyWaddle>();

        Visual(PrimitiveType.Sphere, "Shell", model, new Vector3(0f, 0.1f, -0.02f), new Vector3(0.26f, 0.18f, 0.3f), m.KoopaShell);
        Visual(PrimitiveType.Cylinder, "Rim", model, new Vector3(0f, 0.06f, -0.02f), new Vector3(0.28f, 0.016f, 0.32f), m.White);
        Visual(PrimitiveType.Cylinder, "Belly", model, new Vector3(0f, 0.045f, -0.02f), new Vector3(0.24f, 0.012f, 0.28f), m.KoopaBelly);

        var head = Group("Head", model, new Vector3(0f, 0.2f, 0.15f));
        Visual(PrimitiveType.Capsule, "Neck", head, new Vector3(0f, -0.055f, -0.045f), new Vector3(0.06f, 0.05f, 0.06f), m.KoopaSkin, new Vector3(-25f, 0f, 0f));
        Visual(PrimitiveType.Sphere, "Skull", head, Vector3.zero, new Vector3(0.11f, 0.12f, 0.11f), m.KoopaSkin);
        Visual(PrimitiveType.Sphere, "Snout", head, new Vector3(0f, -0.02f, 0.055f), new Vector3(0.08f, 0.06f, 0.07f), m.KoopaSkin);
        foreach (int side in new[] { -1, 1 })
        {
            Visual(PrimitiveType.Sphere, side < 0 ? "EyeL" : "EyeR", head, new Vector3(0.022f * side, 0.025f, 0.045f), new Vector3(0.035f, 0.06f, 0.025f), m.White);
            Visual(PrimitiveType.Sphere, side < 0 ? "PupilL" : "PupilR", head, new Vector3(0.02f * side, 0.022f, 0.056f), new Vector3(0.018f, 0.032f, 0.012f), m.Black);
        }

        var feet = Group("Feet", model, Vector3.zero);
        foreach (var (x, z) in new[] { (-1, 1), (1, 1), (-1, -1), (1, -1) })
            Visual(PrimitiveType.Sphere, "Foot", feet, new Vector3(0.09f * x, 0.025f, 0.08f * z), new Vector3(0.07f, 0.05f, 0.09f), m.KoopaSkin);

        var so = new SerializedObject(k);
        so.FindProperty("scoreValue").intValue = 200;
        so.FindProperty("bodyHeight").floatValue = 0.15f;
        var hide = so.FindProperty("hideInShell");
        hide.arraySize = 2;
        hide.GetArrayElementAtIndex(0).objectReferenceValue = head.gameObject;
        hide.GetArrayElementAtIndex(1).objectReferenceValue = feet.gameObject;
        so.ApplyModifiedPropertiesWithoutUndo();
        return Save(Shrink(root), KoopaPath);
    }

    /// <summary>Bullet Bill: glossy black bullet, round nose, flared tail, big white eyes and white gloves.</summary>
    private static GameObject BuildBulletBill(Mats m)
    {
        var root = new GameObject("BulletBill");
        root.AddComponent<BulletBill>();
        // Nose along +Z (the flight direction).
        Visual(PrimitiveType.Cylinder, "Body", root.transform, new Vector3(0f, 0f, -0.03f), new Vector3(0.22f, 0.13f, 0.22f), m.BillBody, new Vector3(90f, 0f, 0f));
        Visual(PrimitiveType.Sphere, "Nose", root.transform, new Vector3(0f, 0f, 0.1f), new Vector3(0.22f, 0.22f, 0.2f), m.BillBody);
        Visual(PrimitiveType.Cylinder, "TailRing", root.transform, new Vector3(0f, 0f, -0.165f), new Vector3(0.25f, 0.018f, 0.25f), m.CannonSteel, new Vector3(90f, 0f, 0f));
        Visual(PrimitiveType.Cylinder, "TailFin", root.transform, new Vector3(0f, 0f, -0.2f), new Vector3(0.17f, 0.025f, 0.17f), m.BillBody, new Vector3(90f, 0f, 0f));
        foreach (int side in new[] { -1, 1 })
        {
            Visual(PrimitiveType.Sphere, side < 0 ? "EyeL" : "EyeR", root.transform, new Vector3(0.05f * side, 0.04f, 0.175f), new Vector3(0.07f, 0.1f, 0.05f), m.White);
            Visual(PrimitiveType.Sphere, side < 0 ? "PupilL" : "PupilR", root.transform, new Vector3(0.042f * side, 0.036f, 0.198f), new Vector3(0.032f, 0.05f, 0.015f), m.Black);
            // Angry brow: a slanted cut across the top of each eye, in the body colour.
            Visual(PrimitiveType.Cube, side < 0 ? "LidL" : "LidR", root.transform, new Vector3(0.05f * side, 0.088f, 0.18f), new Vector3(0.085f, 0.03f, 0.06f), m.BillBody, new Vector3(0f, 0f, 22f * side));
            Visual(PrimitiveType.Sphere, side < 0 ? "GloveL" : "GloveR", root.transform, new Vector3(0.125f * side, -0.03f, 0.0f), new Vector3(0.06f, 0.055f, 0.07f), m.White);
        }
        return Save(Shrink(root), BulletBillPath);
    }

    /// <summary>A Bill Blaster tower: column from the floor to a turning head at head height (both resized at runtime).</summary>
    private static void Cannon(Transform parent, Vector3 localPos, Mats m, GameObject bulletBill)
    {
        var root = new GameObject("BulletBillCannon");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = localPos;
        Shrink(root);
        var cannon = root.AddComponent<BulletBillCannon>();

        // Steel column on a wider base (the column is stretched to the head's height at runtime).
        Visual(PrimitiveType.Cylinder, "Base", root.transform, new Vector3(0f, 0.04f, 0f), new Vector3(0.34f, 0.04f, 0.34f), m.CannonBarrel);
        var column = Visual(PrimitiveType.Cylinder, "Column", root.transform, new Vector3(0f, 0.7f, 0f), new Vector3(0.14f, 0.7f, 0.14f), m.CannonSteel);
        var head = new GameObject("Head");
        head.transform.SetParent(root.transform, false);
        head.transform.localPosition = new Vector3(0f, 1.4f, 0f);
        Visual(PrimitiveType.Cylinder, "Collar", head.transform, new Vector3(0f, -0.19f, 0f), new Vector3(0.24f, 0.03f, 0.24f), m.CannonRim);
        Visual(PrimitiveType.Cube, "Housing", head.transform, Vector3.zero, new Vector3(0.32f, 0.32f, 0.32f), m.Dark);
        Visual(PrimitiveType.Cube, "SkullL", head.transform, new Vector3(-0.161f, 0.02f, 0f), new Vector3(0.005f, 0.11f, 0.11f), m.White);
        Visual(PrimitiveType.Cube, "SkullR", head.transform, new Vector3(0.161f, 0.02f, 0f), new Vector3(0.005f, 0.11f, 0.11f), m.White);
        // Barrel along +Z: grey tube, silver rim, dark bore, so it reads as a cannon even head-on.
        Visual(PrimitiveType.Cylinder, "Barrel", head.transform, new Vector3(0f, 0f, 0.24f), new Vector3(0.22f, 0.09f, 0.22f), m.CannonBarrel, new Vector3(90f, 0f, 0f));
        Visual(PrimitiveType.Cylinder, "Rim", head.transform, new Vector3(0f, 0f, 0.34f), new Vector3(0.27f, 0.02f, 0.27f), m.CannonRim, new Vector3(90f, 0f, 0f));
        Visual(PrimitiveType.Cylinder, "Bore", head.transform, new Vector3(0f, 0f, 0.355f), new Vector3(0.17f, 0.005f, 0.17f), m.CannonBore, new Vector3(90f, 0f, 0f));
        var muzzle = new GameObject("Muzzle");
        muzzle.transform.SetParent(head.transform, false);
        muzzle.transform.localPosition = new Vector3(0f, 0f, 0.38f);
        // Warning: a warm glow filling the bore, not a ball in front of it.
        var glow = Visual(PrimitiveType.Cylinder, "WarningGlow", head.transform, new Vector3(0f, 0f, 0.36f), new Vector3(0.15f, 0.004f, 0.15f), m.CannonGlow, new Vector3(90f, 0f, 0f));

        var so = new SerializedObject(cannon);
        so.FindProperty("bulletBillPrefab").objectReferenceValue = bulletBill.GetComponent<BulletBill>();
        so.FindProperty("head").objectReferenceValue = head.transform;
        so.FindProperty("muzzle").objectReferenceValue = muzzle.transform;
        so.FindProperty("column").objectReferenceValue = column.transform;
        so.FindProperty("warningGlow").objectReferenceValue = glow;
        so.FindProperty("fireEffect").objectReferenceValue = Effect(SmokePath);
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    /// <summary>Goomba: a big brown mushroom-cap head over a tan face, angry brows, fangs and dark feet.</summary>
    private static GameObject BuildGoomba(Mats m)
    {
        var root = new GameObject("Goomba");
        Set(root.AddComponent<Goomba>(), "stompEffect", Effect(PoofPath));
        Shadow(root.transform, 0.3f, m);
        var model = Group("Model", root.transform, Vector3.zero);
        model.gameObject.AddComponent<EnemyWaddle>();

        // Tan lower body under a big brown head; the face (eyes, brows, fangs) is on the head's front.
        Visual(PrimitiveType.Sphere, "Body", model, new Vector3(0f, 0.075f, 0f), new Vector3(0.17f, 0.13f, 0.16f), m.GoombaFace);
        Visual(PrimitiveType.Sphere, "Head", model, new Vector3(0f, 0.175f, 0f), new Vector3(0.3f, 0.21f, 0.26f), m.Goomba);
        foreach (int side in new[] { -1, 1 })
        {
            Visual(PrimitiveType.Sphere, side < 0 ? "EyeL" : "EyeR", model, new Vector3(0.042f * side, 0.19f, 0.118f), new Vector3(0.052f, 0.085f, 0.035f), m.White);
            Visual(PrimitiveType.Sphere, side < 0 ? "PupilL" : "PupilR", model, new Vector3(0.036f * side, 0.183f, 0.133f), new Vector3(0.026f, 0.045f, 0.015f), m.Black);
            // Angry brows: thick, inner ends low.
            Visual(PrimitiveType.Cube, side < 0 ? "BrowL" : "BrowR", model, new Vector3(0.046f * side, 0.243f, 0.11f), new Vector3(0.07f, 0.02f, 0.02f), m.Black, new Vector3(0f, 0f, 24f * side));
            Visual(PrimitiveType.Cube, side < 0 ? "FangL" : "FangR", model, new Vector3(0.032f * side, 0.115f, 0.108f), new Vector3(0.02f, 0.024f, 0.012f), m.White);
            Visual(PrimitiveType.Sphere, side < 0 ? "FootL" : "FootR", model, new Vector3(0.06f * side, 0.022f, 0.02f), new Vector3(0.09f, 0.045f, 0.13f), m.GoombaFeet);
        }
        return Save(Shrink(root), GoombaPath);
    }

    /// <summary>Fireball: a glowing orange ball with flame licks, spinning as it flies.</summary>
    private static GameObject BuildFireball(Mats m)
    {
        var root = new GameObject("Fireball");
        Set(root.AddComponent<Fireball>(), "impactEffect", Effect(FireBurstPath));
        var model = Group("Model", root.transform, Vector3.zero);
        model.gameObject.AddComponent<Spinner>();
        Visual(PrimitiveType.Sphere, "Ball", model, Vector3.zero, Vector3.one * 0.075f, m.Fire);
        foreach (var d in new[] { Vector3.up, Vector3.down, Vector3.left, Vector3.right })
            Visual(PrimitiveType.Sphere, "Lick", model, d * 0.035f, Vector3.one * 0.035f, m.FireCore);
        return Save(Shrink(root), FireballPath);
    }

    /// <summary>Thrown Koopa shell: the green shell with its white rim, spinning about the vertical as it slides.</summary>
    private static GameObject BuildThrownShell(Mats m)
    {
        var root = new GameObject("ThrownShell");
        Set(root.AddComponent<ThrownShell>(), "impactEffect", Effect(PoofPath));
        Shadow(root.transform, 0.32f, m);
        var model = Group("Model", root.transform, Vector3.zero);
        var spin = model.gameObject.AddComponent<Spinner>();
        var so = new SerializedObject(spin);
        so.FindProperty("axis").vector3Value = Vector3.up;
        so.FindProperty("degreesPerSecond").floatValue = 900f;
        so.ApplyModifiedPropertiesWithoutUndo();
        Visual(PrimitiveType.Sphere, "Shell", model, new Vector3(0f, 0.08f, 0f), new Vector3(0.26f, 0.16f, 0.28f), m.KoopaShell);
        Visual(PrimitiveType.Cylinder, "Rim", model, new Vector3(0f, 0.04f, 0f), new Vector3(0.28f, 0.016f, 0.3f), m.White);
        Visual(PrimitiveType.Cylinder, "Belly", model, new Vector3(0f, 0.025f, 0f), new Vector3(0.24f, 0.012f, 0.26f), m.KoopaBelly);
        return Save(Shrink(root), ThrownShellPath);
    }

    /// <summary>A small Koopa shell in the bottom-right of the player's view, shown while they hold one (HeldItemView on the AR camera).</summary>
    private static void BuildHeldItemView(Mats m)
    {
        var cam = Camera.main;
        if (cam == null) { Debug.LogWarning("[GameContentSetup] No MainCamera: held-item view not built."); return; }
        var old = cam.transform.Find("HeldItemView");
        if (old != null) Object.DestroyImmediate(old.gameObject);
        var view = new GameObject("HeldItemView");
        view.transform.SetParent(cam.transform, false);
        var shell = Group("Shell", view.transform, new Vector3(0.11f, -0.13f, 0.38f));
        shell.localRotation = Quaternion.Euler(-20f, 0f, 0f);
        shell.localScale = Vector3.one * 0.45f;
        Visual(PrimitiveType.Sphere, "Shell", shell, new Vector3(0f, 0.08f, 0f), new Vector3(0.26f, 0.16f, 0.28f), m.KoopaShell);
        Visual(PrimitiveType.Cylinder, "Rim", shell, new Vector3(0f, 0.04f, 0f), new Vector3(0.28f, 0.016f, 0.3f), m.White);
        Visual(PrimitiveType.Cylinder, "Belly", shell, new Vector3(0f, 0.025f, 0f), new Vector3(0.24f, 0.012f, 0.26f), m.KoopaBelly);
        Set(view.AddComponent<HeldItemView>(), "shellModel", shell.gameObject);
    }

    /// <summary>A soft blob shadow on the floor under a character: grounds it on the real floor in AR.</summary>
    private static void Shadow(Transform parent, float size, Mats m)
    {
        Visual(PrimitiveType.Quad, "Shadow", parent, new Vector3(0f, 0.004f, 0f), new Vector3(size, size, 1f), m.BlobShadow, new Vector3(90f, 0f, 0f));
    }

    // ---- stage templates ----

    private static GameObject BuildStandardStage(Mats m, GameObject coinPop, Pickups pickups, GameObject goomba, GameObject koopa, GameObject bulletBill)
    {
        var root = StageRoot("StandardStage", 1.2f, 2.0f, 2.0f);
        QuestionBlock(root.transform, new Vector3(-0.25f, 1.8f, -0.5f), m, coinPop, pickups);
        Brick(root.transform, new Vector3(0.35f, 1.2f, -0.1f), m, coinPop);

        var spawner = new GameObject("GoombaSpawner");
        spawner.transform.SetParent(root.transform, false);
        spawner.transform.localPosition = new Vector3(0f, 0f, 0.55f);
        spawner.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // face the player's side
        var es = spawner.AddComponent<EnemySpawner>();
        var so = new SerializedObject(es);
        var seq = so.FindProperty("enemyPrefabs");
        seq.arraySize = 2;
        seq.GetArrayElementAtIndex(0).objectReferenceValue = goomba.GetComponent<Goomba>();
        seq.GetArrayElementAtIndex(1).objectReferenceValue = koopa.GetComponent<Koopa>();
        so.ApplyModifiedPropertiesWithoutUndo();

        // Far corner, opposite the brick's side of the flag: the player starts ~2-3 m away.
        Cannon(root.transform, new Vector3(-0.45f, 0f, 0.9f), m, bulletBill);
        WarpPipe(root.transform, new Vector3(0.4f, 0f, 0.35f), m);

        Goal(root.transform, new Vector3(0f, 0f, 0.85f), m);
        return Save(root, StandardStagePath);
    }

    private static GameObject BuildLargeStage(Mats m, GameObject coinPop, Pickups pickups, GameObject goomba, GameObject koopa, GameObject bulletBill)
    {
        var root = StageRoot("LargeStage", 2.0f, 3.0f, 2.0f);
        QuestionBlock(root.transform, new Vector3(-0.4f, 1.8f, -0.9f), m, coinPop, pickups, "QuestionBlock");
        QuestionBlock(root.transform, new Vector3(0.45f, 1.8f, 0.25f), m, coinPop, pickups, "QuestionBlock2");
        Brick(root.transform, new Vector3(0.55f, 1.2f, -0.6f), m, coinPop, "BrickBlock");
        Brick(root.transform, new Vector3(-0.55f, 1.2f, 0.35f), m, coinPop, "BrickBlock2");

        var spawner = new GameObject("EnemySpawner");
        spawner.transform.SetParent(root.transform, false);
        spawner.transform.localPosition = new Vector3(0f, 0f, 1.0f);
        spawner.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        var es = spawner.AddComponent<EnemySpawner>();
        var so = new SerializedObject(es);
        so.FindProperty("totalCount").intValue = 3;
        var seq = so.FindProperty("enemyPrefabs");
        seq.arraySize = 2;
        seq.GetArrayElementAtIndex(0).objectReferenceValue = goomba.GetComponent<Goomba>();
        seq.GetArrayElementAtIndex(1).objectReferenceValue = koopa.GetComponent<Koopa>();
        so.ApplyModifiedPropertiesWithoutUndo();

        Cannon(root.transform, new Vector3(0.8f, 0f, 1.35f), m, bulletBill);
        WarpPipe(root.transform, new Vector3(-0.6f, 0f, -0.2f), m); // near half: a detour to the bonus room early on
        Goal(root.transform, new Vector3(0f, 0f, 1.3f), m);
        return Save(root, LargeStagePath);
    }

    /// <summary>A short green pipe on the floor; squat on it to warp (appears on some stages only).</summary>
    /// <summary>A short green pipe with a highlight stripe and a wider lip; squat on it to warp (appearChance: how often it's there).</summary>
    private static GameObject WarpPipe(Transform parent, Vector3 localPos, Mats m, float appearChance = 0.5f, bool isExit = false)
    {
        var root = new GameObject(isExit ? "ExitPipe" : "WarpPipe");
        if (parent != null) root.transform.SetParent(parent, false);
        root.transform.localPosition = localPos;
        Shrink(root);
        var pipe = root.AddComponent<WarpPipe>();
        var so = new SerializedObject(pipe);
        so.FindProperty("appearChance").floatValue = appearChance;
        so.FindProperty("isExit").boolValue = isExit;
        so.ApplyModifiedPropertiesWithoutUndo();
        Visual(PrimitiveType.Cylinder, "Body", root.transform, new Vector3(0f, 0.115f, 0f), new Vector3(0.36f, 0.115f, 0.36f), m.Pipe);
        Visual(PrimitiveType.Cylinder, "Lip", root.transform, new Vector3(0f, 0.26f, 0f), new Vector3(0.46f, 0.045f, 0.46f), m.Pipe);
        Visual(PrimitiveType.Cylinder, "Opening", root.transform, new Vector3(0f, 0.306f, 0f), new Vector3(0.36f, 0.002f, 0.36f), m.CannonBore);
        return root;
    }

    // ---- coins, bonus room, campaign ----

    /// <summary>A floating coin to walk through: spinning gold disc at waist height.</summary>
    private static GameObject CoinObject(Transform parent, Vector3 localPos, Mats m)
    {
        var root = new GameObject("Coin");
        if (parent != null) root.transform.SetParent(parent, false);
        root.transform.localPosition = localPos;
        Shrink(root);
        Set(root.AddComponent<Coin>(), "collectEffect", Effect(SparklePath));
        var eh = root.AddComponent<EyeHeightPlacement>();
        SetFloat(eh, "fractionOfEyes", 0.6f); // waist height, scaled to the player
        SetFloat(eh, "minHeightAboveFloor", 0.4f);
        Visual(PrimitiveType.Cylinder, "Rim", root.transform, Vector3.zero, new Vector3(0.13f, 0.007f, 0.13f), m.CoinRim, new Vector3(90f, 0f, 0f));
        Visual(PrimitiveType.Cylinder, "Face", root.transform, Vector3.zero, new Vector3(0.11f, 0.009f, 0.11f), m.Coin, new Vector3(90f, 0f, 0f));
        Visual(PrimitiveType.Cube, "Bar", root.transform, Vector3.zero, new Vector3(0.022f, 0.065f, 0.026f), m.CoinShine);
        return root;
    }

    private static void Spawner(Transform parent, Vector3 localPos, string sequence, int count, GameObject goomba, GameObject koopa)
    {
        var spawner = new GameObject("EnemySpawner");
        spawner.transform.SetParent(parent, false);
        spawner.transform.localPosition = localPos;
        spawner.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // face the player's side
        var so = new SerializedObject(spawner.AddComponent<EnemySpawner>());
        so.FindProperty("totalCount").intValue = count;
        var seq = so.FindProperty("enemyPrefabs");
        seq.arraySize = sequence.Length;
        for (int i = 0; i < sequence.Length; i++)
            seq.GetArrayElementAtIndex(i).objectReferenceValue = sequence[i] == 'K' ? koopa.GetComponent<Koopa>() : goomba.GetComponent<Goomba>();
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    /// <summary>A campaign stage from its StageDesigns entry.</summary>
    private static GameObject BuildCampaignStage(StageDesigns.Design design, Mats m, GameObject coinPop, Pickups pickups, GameObject goomba, GameObject koopa, GameObject bulletBill, GameObject bowser)
    {
        Directory.CreateDirectory(CampaignDir);
        var root = StageRoot($"Stage {design.Name}", design.Width, design.Depth, 2.0f);
        int n = 0;
        foreach (var piece in design.Pieces)
        {
            var at = new Vector3(piece.At.x, 0f, piece.At.y);
            switch (piece.Kind)
            {
                case StageDesigns.Kind.Question: QuestionBlock(root.transform, at + Vector3.up * 1.8f, m, coinPop, pickups, $"QuestionBlock{n++}"); break;
                case StageDesigns.Kind.Brick: Brick(root.transform, at + Vector3.up * 1.2f, m, coinPop, $"BrickBlock{n++}"); break;
                case StageDesigns.Kind.Coin:
                    var coin = CoinObject(root.transform, at, m);
                    if (piece.Count > 0) SetFloat(coin.GetComponent<EyeHeightPlacement>(), "extraHeight", piece.Count / 100f); // arcs
                    break;
                case StageDesigns.Kind.Spawner: Spawner(root.transform, at, piece.Enemies, piece.Count, goomba, koopa); break;
                case StageDesigns.Kind.Cannon: Cannon(root.transform, at, m, bulletBill); break;
                case StageDesigns.Kind.Pipe: WarpPipe(root.transform, at, m, piece.Chance); break;
                case StageDesigns.Kind.Flag: Goal(root.transform, at, m); break;
                case StageDesigns.Kind.Stairs: StairColumn(root.transform, at, piece.Count, m); break;
                case StageDesigns.Kind.TallPipe: TallPipe(root.transform, at, piece.Count, m); break;
                case StageDesigns.Kind.Boss:
                    var boss = (GameObject)PrefabUtility.InstantiatePrefab(bowser, root.transform);
                    boss.transform.localPosition = at;
                    boss.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // facing the player's side
                    break;
                case StageDesigns.Kind.Axe: AxeGoal(root.transform, at, m); break;
            }
        }
        Scenery(root.transform, design, m);
        var info = new SerializedObject(root.AddComponent<StageInfo>());
        info.FindProperty("title").stringValue = design.Title;
        info.FindProperty("tip").stringValue = design.Tip;
        info.ApplyModifiedPropertiesWithoutUndo();
        return Save(root, CampaignStagePath(design.Name));
    }

    // ---- the training round (Tutorial): one station per step, shown one at a time ----

    /// <summary>
    /// WORLD 0-1, TRAINING: a 1.6 × 3.2 m lane. Each step's station is a child at its own spot
    /// (so CompressLayout moves stations, not the pieces within one), hidden until its step.
    /// From the player's end (−z) to the flag (+z): ? block, brick, a slow Goomba, a cannon,
    /// a distant Goomba for a fireball, a Koopa with two Goombas for the shell, the flag.
    /// </summary>
    private static GameObject BuildTrainingStage(Mats m, GameObject coinPop, Pickups pickups, GameObject goomba, GameObject koopa, GameObject bulletBill)
    {
        var root = StageRoot("TrainingStage", 1.6f, 3.2f, 2.0f);
        Transform Station(string name, float x, float z)
        {
            var t = Group(name, root.transform, new Vector3(x, 0f, z));
            t.gameObject.SetActive(false);
            return t;
        }
        void Enemy(GameObject prefab, Transform station, Vector3 at, float speed)
        {
            var e = (GameObject)PrefabUtility.InstantiatePrefab(prefab, station);
            e.transform.localPosition = at;
            e.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // facing the player's end
            SetFloat(e.GetComponent<Goomba>(), "speed", speed); // Koopa is a Goomba too
        }

        var jump = Station("1 Jump", 0f, -1.0f);
        QuestionBlock(jump, Vector3.up * 1.8f, m, coinPop, pickups);
        SetFloat(jump.GetComponentInChildren<QuestionBlock>(true), "oneUpChance", 0f); // always the fire flower

        var punch = Station("2 Punch", 0.3f, -0.5f);
        Brick(punch, Vector3.up * 1.2f, m, coinPop);

        var stomp = Station("3 Stomp", -0.15f, 0.7f);
        Enemy(goomba, stomp, Vector3.zero, 0.12f); // half speed

        var squat = Station("4 Squat", 0.7f, 0.6f);
        Cannon(squat, Vector3.zero, m, bulletBill);
        var cannon = new SerializedObject(squat.GetComponentInChildren<BulletBillCannon>(true));
        cannon.FindProperty("maxShots").intValue = 20; // keeps firing until the duck lands
        cannon.FindProperty("initialDelay").floatValue = 2.5f;
        cannon.FindProperty("interval").floatValue = 5f;
        cannon.ApplyModifiedPropertiesWithoutUndo();

        var fire = Station("5 Throw", 0f, 1.3f);
        Enemy(goomba, fire, Vector3.zero, 0.06f); // slow, far: a target for a fireball

        var shell = Station("6 Shell", 0f, 1.0f);
        Enemy(koopa, shell, Vector3.zero, 0.06f);
        Enemy(goomba, shell, new Vector3(-0.45f, 0f, 0.35f), 0.05f);
        Enemy(goomba, shell, new Vector3(0.45f, 0f, 0.35f), 0.05f);

        var flag = Station("7 Flag", 0f, 1.45f);
        Goal(flag, Vector3.zero, m);

        // A little scenery so it feels like a level.
        foreach (int side in new[] { -1, 1 })
        {
            Bush(root.transform, new Vector3(side * 1.05f, 0f, -0.8f), 0.9f, m);
            Bush(root.transform, new Vector3(side * 1.05f, 0f, 0.9f), 0.8f, m);
        }
        Cloud(root.transform, new Vector3(-0.4f, 2.4f, 0.4f), 1.6f, m);

        var steps = new[]
        {
            new Tutorial.Step { title = "JUMP!", hint = "Jump under the ? block to hit it", actions = new[] { "jump" }, station = jump.gameObject, done = Tutorial.Done.BlocksUsed },
            new Tutorial.Step { title = "PUNCH!", hint = "Punch the brick in front of you", actions = new[] { "punch" }, station = punch.gameObject, done = Tutorial.Done.BlocksUsed },
            new Tutorial.Step { title = "STOMP!", hint = "Wait for the Goomba, stomp it at your feet", actions = new[] { "stomp" }, station = stomp.gameObject, done = Tutorial.Done.EnemiesCleared },
            new Tutorial.Step { title = "SQUAT!", hint = "Squat when the cannon glows to duck the Bullet Bill", actions = new[] { "squat" }, station = squat.gameObject, done = Tutorial.Done.DuckUnderBill },
            new Tutorial.Step { title = "THROW!", hint = "Throw a fireball at the Goomba", actions = new[] { "throw" }, station = fire.gameObject, done = Tutorial.Done.EnemiesCleared, needsFlower = true },
            new Tutorial.Step { title = "STOMP, CLAP, THROW!", hint = "Stomp the Koopa, clap to grab its shell, throw it", actions = new[] { "stomp", "clap", "throw" }, station = shell.gameObject, done = Tutorial.Done.EnemiesCleared },
            new Tutorial.Step { title = "JUMP AT THE FLAG!", hint = "Walk to the flag and jump to grab it", actions = new[] { "jump" }, station = flag.gameObject, done = Tutorial.Done.Goal },
        };
        var tutorial = root.AddComponent<Tutorial>();
        typeof(Tutorial).GetField("steps", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(tutorial, steps);

        var info = new SerializedObject(root.AddComponent<StageInfo>());
        info.FindProperty("title").stringValue = "TRAINING";
        info.FindProperty("tip").stringValue = "Learn the moves - you can't lose lives here";
        info.ApplyModifiedPropertiesWithoutUndo();
        return Save(root, TrainingStagePath);
    }

    // ---- height: stairs and tall pipes (walk round them) ----

    /// <summary>A column of <paramref name="blocks"/> stair blocks (hard, bevelled, 0.25 m each).</summary>
    private static void StairColumn(Transform parent, Vector3 at, int blocks, Mats m)
    {
        var column = Shrink(Group("Stairs", parent, at));
        for (int i = 0; i < Mathf.Max(1, blocks); i++)
            Visual(PrimitiveType.Cube, "StairBlock", column, new Vector3(0f, 0.125f + 0.25f * i, 0f), Vector3.one * 0.25f, m.Stair)
                .GetComponent<MeshFilter>().sharedMesh = BlockMesh();
    }

    /// <summary>A tall decorative green pipe (no warp), <paramref name="blocks"/> × 0.25 m high plus its lip.</summary>
    private static void TallPipe(Transform parent, Vector3 at, int blocks, Mats m)
    {
        var pipe = Shrink(Group("TallPipe", parent, at));
        float h = 0.25f * Mathf.Max(1, blocks);
        Visual(PrimitiveType.Cylinder, "Body", pipe, new Vector3(0f, h / 2f, 0f), new Vector3(0.36f, h / 2f, 0.36f), m.Pipe);
        Visual(PrimitiveType.Cylinder, "Lip", pipe, new Vector3(0f, h + 0.045f, 0f), new Vector3(0.46f, 0.045f, 0.46f), m.Pipe);
        Visual(PrimitiveType.Cylinder, "Opening", pipe, new Vector3(0f, h + 0.091f, 0f), new Vector3(0.36f, 0.002f, 0.36f), m.CannonBore);
    }

    // ---- scenery: decoration around and above the play area (never targets, no colliders) ----

    /// <summary>
    /// Background art for a campaign stage, by theme, sized to fill the space
    /// round the play area as a backdrop (Oliver 2026-10-21: the castle was
    /// smaller than the flagpole). Every piece is a direct child of the stage
    /// root so CompressLayout moves it in with the rest. Big pieces stand
    /// beyond the stage's sides and far end; clouds and ceilings are overhead,
    /// so nothing gets in the player's way.
    /// </summary>
    private static void Scenery(Transform root, StageDesigns.Design design, Mats m)
    {
        float hw = design.Width / 2f, hd = design.Depth / 2f;
        switch (design.Scenery)
        {
            case StageDesigns.Scenery.Overworld:
                // Rolling hills either side, a big castle beyond the flag (taller than the pole), bushes along the edges.
                Hill(root, new Vector3(-hw - 0.65f, 0f, hd - 0.5f), 0.6f, 0.75f, m); // each hill stays just outside the edge
                Hill(root, new Vector3(-hw - 0.45f, 0f, -hd + 1.2f), 0.4f, 0.45f, m);
                Hill(root, new Vector3(hw + 0.5f, 0f, hd - 1.6f), 0.45f, 0.55f, m);
                Hill(root, new Vector3(hw + 0.6f, 0f, -hd + 0.6f), 0.55f, 0.65f, m);
                for (float z = -hd + 0.7f; z < hd - 0.3f; z += 1.1f)
                {
                    Bush(root, new Vector3(-hw - 0.25f, 0f, z), 1.0f, m);
                    Bush(root, new Vector3(hw + 0.25f, 0f, z + 0.55f), 0.9f, m);
                }
                Cloud(root, new Vector3(-hw * 0.7f, 2.4f, -hd * 0.3f), 2f, m);
                Cloud(root, new Vector3(hw * 0.6f, 2.55f, hd * 0.2f), 1.7f, m);
                Cloud(root, new Vector3(-hw * 0.2f, 2.5f, hd * 0.9f), 2.3f, m);
                Cloud(root, new Vector3(hw + 0.6f, 2.3f, -hd * 0.7f), 1.8f, m);
                CastleBackdrop(root, new Vector3(hw * 0.35f, 0f, hd + 1.0f), 2.6f, m); // ~2.3 m tall
                break;

            case StageDesigns.Scenery.Underground:
                // A cave: brick ceiling beams along both sides, thick pillars, and a brick wall at the far end.
                for (float z = -hd + 0.125f; z <= hd; z += 0.25f)
                    foreach (int side in new[] { -1, 1 })
                        Visual(PrimitiveType.Cube, "Ceiling", root, new Vector3(side * (hw - 0.125f), 2.35f, z), Vector3.one * 0.25f, m.UndergroundBrick)
                            .GetComponent<MeshFilter>().sharedMesh = BlockMesh();
                foreach (float z in new[] { -hd + 0.4f, -hd * 0.35f, hd * 0.35f, hd - 0.4f })
                    foreach (int side in new[] { -1, 1 })
                    {
                        var pillar = Group("Pillar", root, new Vector3(side * (hw + 0.3f), 0f, z));
                        for (int i = 0; i < 5; i++)
                            Visual(PrimitiveType.Cube, "Block", pillar, new Vector3(0f, 0.25f + 0.5f * i, 0f), Vector3.one * 0.5f, m.UndergroundBrick)
                                .GetComponent<MeshFilter>().sharedMesh = BlockMesh();
                    }
                BlockWall(root, "BackWall", new Vector3(0f, 0f, hd + 0.5f), design.Width + 1.2f, 5, 0.5f, m.UndergroundBrick, crenellated: false);
                break;

            case StageDesigns.Scenery.Sky:
                // High above the clouds: big clouds overhead, a cloud floor at the edges, giant mushrooms.
                Cloud(root, new Vector3(-hw * 0.7f, 2.35f, -hd * 0.6f), 2f, m);
                Cloud(root, new Vector3(hw * 0.6f, 2.6f, -hd * 0.1f), 1.7f, m);
                Cloud(root, new Vector3(-hw * 0.3f, 2.5f, hd * 0.4f), 2.4f, m);
                Cloud(root, new Vector3(hw * 0.4f, 2.4f, hd * 0.9f), 1.9f, m);
                Cloud(root, new Vector3(0f, 1.2f, hd + 1.4f), 3.2f, m); // a big one on the horizon
                foreach (int side in new[] { -1, 1 })
                    for (float z = -hd + 0.4f; z < hd; z += 0.9f)
                        Cloud(root, new Vector3(side * (hw + 0.45f), 0.15f, z + (side > 0 ? 0.45f : 0f)), 1.6f, m); // the cloud floor at the edges
                Mushroom(root, new Vector3(-hw - 0.6f, 0f, hd * 0.25f), 1.6f, 1.6f, m);
                Mushroom(root, new Vector3(hw + 0.55f, 0f, -hd * 0.4f), 1.1f, 1.3f, m);
                Mushroom(root, new Vector3(hw + 0.7f, 0f, hd * 0.7f), 1.9f, 1.8f, m);
                break;

            case StageDesigns.Scenery.Castle:
                // Inside the castle: crenellated stone walls with lava moats either side, a high wall behind Bowser, torches.
                foreach (int side in new[] { -1, 1 })
                {
                    // Lava moat just outside the edge, then the wall beyond it.
                    Visual(PrimitiveType.Cube, "Lava", root, new Vector3(side * (hw + 0.35f), 0.01f, 0f), new Vector3(0.6f, 0.02f, design.Depth + 0.5f), m.Lava);
                    var wall = BlockWall(root, "Wall", new Vector3(side * (hw + 0.9f), 0f, 0f), design.Depth + 1f, 3, 0.5f, m.CastleStone, crenellated: true);
                    wall.localRotation = Quaternion.Euler(0f, 90f, 0f);
                    foreach (float z in new[] { -hd * 0.6f, 0f, hd * 0.6f })
                        Torch(root, new Vector3(side * (hw - 0.05f), 0f, z), m);
                }
                BlockWall(root, "BackWall", new Vector3(0f, 0f, hd + 0.5f), design.Width + 2.3f, 4, 0.5f, m.CastleStone, crenellated: true);
                break;
        }
    }

    /// <summary>
    /// A wall of <paramref name="rows"/> × block-sized stone blocks, <paramref name="length"/> long along
    /// its local X, standing on the floor (crenellated: every other block on top).
    /// </summary>
    private static Transform BlockWall(Transform parent, string name, Vector3 at, float length, int rows, float block, Material mat, bool crenellated)
    {
        var wall = Group(name, parent, at);
        int columns = Mathf.Max(1, Mathf.RoundToInt(length / block));
        float x0 = -(columns - 1) * block / 2f;
        for (int c = 0; c < columns; c++)
        {
            int height = rows + (crenellated && c % 2 == 0 ? 1 : 0);
            for (int r = 0; r < height; r++)
                Visual(PrimitiveType.Cube, "Block", wall, new Vector3(x0 + c * block, block / 2f + r * block, 0f), Vector3.one * block, mat)
                    .GetComponent<MeshFilter>().sharedMesh = BlockMesh();
        }
        return wall;
    }

    /// <summary>A rounded green hill (a half-buried sphere) with a few dark spots, as in the 1985 backdrop.</summary>
    private static void Hill(Transform parent, Vector3 at, float radius, float height, Mats m)
    {
        var hill = Group("Hill", parent, at);
        Visual(PrimitiveType.Sphere, "Mound", hill, Vector3.zero, new Vector3(radius * 2f, height * 2f, radius * 2f), m.Hill);
        foreach (var (x, y) in new[] { (-0.35f, 0.55f), (0.25f, 0.7f), (0f, 0.35f) })
        {
            var p = new Vector3(x * radius, y * height, 0f);
            float zOnSurface = -Mathf.Sqrt(Mathf.Max(0.01f, 1f - (x * x) - (y * y))) * radius; // on the player's (-z) side
            Visual(PrimitiveType.Sphere, "Spot", hill, new Vector3(p.x, p.y, zOnSurface), new Vector3(0.1f, 0.18f, 0.06f) * radius, m.HillDark);
        }
    }

    /// <summary>A bush: three overlapping bright-green puffs on the floor.</summary>
    private static void Bush(Transform parent, Vector3 at, float size, Mats m)
    {
        var bush = Group("Bush", parent, at);
        bush.localScale = Vector3.one * size;
        Visual(PrimitiveType.Sphere, "Puff", bush, new Vector3(-0.13f, 0.06f, 0f), new Vector3(0.2f, 0.17f, 0.18f), m.Bush);
        Visual(PrimitiveType.Sphere, "Puff", bush, new Vector3(0f, 0.09f, 0f), new Vector3(0.26f, 0.22f, 0.22f), m.Bush);
        Visual(PrimitiveType.Sphere, "Puff", bush, new Vector3(0.13f, 0.06f, 0f), new Vector3(0.2f, 0.17f, 0.18f), m.Bush);
    }

    /// <summary>A puffy white cloud with two eyes, drifting gently.</summary>
    private static void Cloud(Transform parent, Vector3 at, float size, Mats m)
    {
        var cloud = Group("Cloud", parent, at);
        cloud.localScale = Vector3.one * size;
        cloud.gameObject.AddComponent<Drift>();
        Visual(PrimitiveType.Sphere, "Puff", cloud, new Vector3(-0.16f, 0f, 0f), new Vector3(0.24f, 0.18f, 0.18f), m.Cloud);
        Visual(PrimitiveType.Sphere, "Puff", cloud, new Vector3(0f, 0.05f, 0f), new Vector3(0.3f, 0.24f, 0.22f), m.Cloud);
        Visual(PrimitiveType.Sphere, "Puff", cloud, new Vector3(0.16f, 0f, 0f), new Vector3(0.24f, 0.18f, 0.18f), m.Cloud);
        foreach (int side in new[] { -1, 1 })
            Visual(PrimitiveType.Sphere, "Eye", cloud, new Vector3(0.035f * side, 0.06f, -0.105f), new Vector3(0.022f, 0.045f, 0.01f), m.Black);
    }

    /// <summary>A castle in the distance beyond the flag: brick keep, crenellations, a dark doorway and a tower (≈0.9 m × size tall).</summary>
    private static void CastleBackdrop(Transform parent, Vector3 at, float size, Mats m)
    {
        var castle = Group("Castle", parent, at);
        castle.localScale = Vector3.one * size;
        Visual(PrimitiveType.Cube, "Keep", castle, new Vector3(0f, 0.25f, 0f), new Vector3(0.75f, 0.5f, 0.4f), m.CastleBrick);
        Visual(PrimitiveType.Cube, "Tower", castle, new Vector3(0f, 0.65f, 0f), new Vector3(0.35f, 0.3f, 0.3f), m.CastleBrick);
        Visual(PrimitiveType.Cube, "Door", castle, new Vector3(0f, 0.12f, -0.201f), new Vector3(0.14f, 0.24f, 0.01f), m.Black);
        Visual(PrimitiveType.Cube, "Window", castle, new Vector3(0f, 0.68f, -0.151f), new Vector3(0.07f, 0.12f, 0.01f), m.Black);
        for (int i = -3; i <= 3; i += 2)
            Visual(PrimitiveType.Cube, "Crenel", castle, new Vector3(i * 0.1f, 0.55f, 0f), new Vector3(0.09f, 0.1f, 0.4f), m.CastleBrick);
        for (int i = -1; i <= 1; i += 2)
            Visual(PrimitiveType.Cube, "Crenel", castle, new Vector3(i * 0.12f, 0.85f, 0f), new Vector3(0.08f, 0.1f, 0.3f), m.CastleBrick);
    }

    /// <summary>A giant mushroom (stalk and red, white-spotted cap), as in the athletic levels; <paramref name="capSize"/> scales the cap.</summary>
    private static void Mushroom(Transform parent, Vector3 at, float height, float capSize, Mats m)
    {
        var mush = Group("Mushroom", parent, at);
        Visual(PrimitiveType.Cylinder, "Stalk", mush, new Vector3(0f, height / 2f, 0f), new Vector3(0.12f, height / 2f, 0.12f) * Mathf.Max(1f, capSize * 0.8f), m.GoombaFace);
        var cap = Group("Cap", mush, new Vector3(0f, height, 0f));
        cap.localScale = Vector3.one * capSize;
        Visual(PrimitiveType.Sphere, "Cap", cap, Vector3.zero, new Vector3(0.5f, 0.22f, 0.5f), m.MushroomCap);
        foreach (var (x, z) in new[] { (-0.14f, -0.12f), (0.15f, -0.1f), (0f, -0.2f) })
            Visual(PrimitiveType.Sphere, "Spot", cap, new Vector3(x, 0.05f, z), new Vector3(0.08f, 0.04f, 0.08f), m.White);
    }

    /// <summary>A wall torch: a dark post with a flickering flame on top.</summary>
    private static void Torch(Transform parent, Vector3 at, Mats m)
    {
        var torch = Group("Torch", parent, at);
        Visual(PrimitiveType.Cylinder, "Post", torch, new Vector3(0f, 0.55f, 0f), new Vector3(0.05f, 0.55f, 0.05f), m.Dark);
        Visual(PrimitiveType.Cylinder, "Bowl", torch, new Vector3(0f, 1.12f, 0f), new Vector3(0.12f, 0.03f, 0.12f), m.CannonSteel);
        var flame = Visual(PrimitiveType.Sphere, "Flame", torch, new Vector3(0f, 1.2f, 0f), new Vector3(0.09f, 0.14f, 0.09f), m.Fire);
        flame.AddComponent<Flicker>();
        Visual(PrimitiveType.Sphere, "Core", flame.transform, new Vector3(0f, -0.1f, 0f), Vector3.one * 0.6f, m.FireCore);
    }

    // ---- the final boss and the axe ----

    /// <summary>Bowser's fire breath: a stretched fireball that flies like a Bullet Bill (squat or sidestep).</summary>
    private static GameObject BuildBossFlame(Mats m)
    {
        var root = new GameObject("BossFlame");
        var bill = root.AddComponent<BulletBill>();
        SetFloat(bill, "speed", 1.6f);
        SetFloat(bill, "hitRadius", 0.3f);
        var model = Group("Model", root.transform, Vector3.zero);
        var spin = model.gameObject.AddComponent<Spinner>();
        var so = new SerializedObject(spin);
        so.FindProperty("axis").vector3Value = Vector3.forward;
        so.ApplyModifiedPropertiesWithoutUndo();
        Visual(PrimitiveType.Sphere, "Flame", model, Vector3.zero, new Vector3(0.16f, 0.16f, 0.3f), m.Fire);
        Visual(PrimitiveType.Sphere, "Core", model, new Vector3(0f, 0f, 0.05f), new Vector3(0.1f, 0.1f, 0.18f), m.FireCore);
        Visual(PrimitiveType.Sphere, "Tail", model, new Vector3(0f, 0f, -0.18f), new Vector3(0.09f, 0.09f, 0.2f), m.Fire);
        return Save(Shrink(root), BossFlamePath);
    }

    /// <summary>Bowser: a spiked green shell on a big yellow body, horned head with a red mane, white-ringed shell; about 1 m tall.</summary>
    private static GameObject BuildBowser(Mats m, GameObject flame)
    {
        var root = new GameObject("Bowser");
        var boss = root.AddComponent<Bowser>();
        Shadow(root.transform, 0.9f, m);
        var model = Group("Model", root.transform, Vector3.zero);

        Visual(PrimitiveType.Sphere, "Shell", model, new Vector3(0f, 0.52f, -0.14f), new Vector3(0.62f, 0.6f, 0.5f), m.KoopaShell);
        Visual(PrimitiveType.Cylinder, "Rim", model, new Vector3(0f, 0.3f, -0.1f), new Vector3(0.64f, 0.025f, 0.56f), m.White);
        foreach (var (x, y) in new[] { (-0.16f, 0.66f), (0.16f, 0.66f), (0f, 0.78f), (-0.2f, 0.45f), (0.2f, 0.45f), (0f, 0.52f) })
            Visual(PrimitiveType.Capsule, "Spike", model, new Vector3(x, y, -0.4f), new Vector3(0.06f, 0.06f, 0.06f), m.White, new Vector3(90f, 0f, 0f));
        Visual(PrimitiveType.Sphere, "Belly", model, new Vector3(0f, 0.45f, 0.06f), new Vector3(0.48f, 0.6f, 0.36f), m.KoopaBelly);
        foreach (int side in new[] { -1, 1 })
        {
            Visual(PrimitiveType.Sphere, side < 0 ? "LegL" : "LegR", model, new Vector3(0.17f * side, 0.1f, 0.04f), new Vector3(0.2f, 0.22f, 0.24f), m.BowserSkin);
            Visual(PrimitiveType.Sphere, side < 0 ? "ArmL" : "ArmR", model, new Vector3(0.28f * side, 0.55f, 0.08f), new Vector3(0.14f, 0.22f, 0.14f), m.BowserSkin, new Vector3(0f, 0f, -25f * side));
            Visual(PrimitiveType.Cylinder, side < 0 ? "CuffL" : "CuffR", model, new Vector3(0.32f * side, 0.46f, 0.1f), new Vector3(0.13f, 0.02f, 0.13f), m.Black);
        }

        var head = Group("Head", model, new Vector3(0f, 0.88f, 0.12f));
        Visual(PrimitiveType.Sphere, "Skull", head, Vector3.zero, new Vector3(0.32f, 0.28f, 0.3f), m.BowserSkin);
        Visual(PrimitiveType.Sphere, "Snout", head, new Vector3(0f, -0.06f, 0.14f), new Vector3(0.26f, 0.15f, 0.2f), m.KoopaBelly);
        foreach (int side in new[] { -1, 1 })
        {
            Visual(PrimitiveType.Sphere, side < 0 ? "EyeL" : "EyeR", head, new Vector3(0.07f * side, 0.05f, 0.13f), new Vector3(0.06f, 0.08f, 0.04f), m.White);
            Visual(PrimitiveType.Sphere, side < 0 ? "PupilL" : "PupilR", head, new Vector3(0.065f * side, 0.045f, 0.15f), new Vector3(0.03f, 0.045f, 0.015f), m.Black);
            Visual(PrimitiveType.Cube, side < 0 ? "BrowL" : "BrowR", head, new Vector3(0.07f * side, 0.1f, 0.13f), new Vector3(0.09f, 0.025f, 0.03f), m.BowserHair, new Vector3(0f, 0f, 25f * side));
            Visual(PrimitiveType.Capsule, side < 0 ? "HornL" : "HornR", head, new Vector3(0.12f * side, 0.15f, -0.02f), new Vector3(0.05f, 0.08f, 0.05f), m.White, new Vector3(0f, 0f, -30f * side));
            Visual(PrimitiveType.Cube, side < 0 ? "FangL" : "FangR", head, new Vector3(0.06f * side, -0.13f, 0.2f), new Vector3(0.025f, 0.035f, 0.015f), m.White);
        }
        Visual(PrimitiveType.Sphere, "Mane", head, new Vector3(0f, 0.12f, -0.12f), new Vector3(0.22f, 0.18f, 0.2f), m.BowserHair);
        Visual(PrimitiveType.Sphere, "Mane", head, new Vector3(0f, 0.02f, -0.18f), new Vector3(0.2f, 0.18f, 0.16f), m.BowserHair);
        var glow = Visual(PrimitiveType.Sphere, "MouthGlow", head, new Vector3(0f, -0.08f, 0.24f), new Vector3(0.12f, 0.08f, 0.06f), m.CannonGlow);

        var so = new SerializedObject(boss);
        so.FindProperty("flamePrefab").objectReferenceValue = flame.GetComponent<BulletBill>();
        so.FindProperty("warningGlow").objectReferenceValue = glow;
        so.FindProperty("model").objectReferenceValue = model;
        so.FindProperty("hitEffect").objectReferenceValue = Effect(FireBurstPath);
        so.FindProperty("defeatEffect").objectReferenceValue = Effect(ConfettiPath);
        so.ApplyModifiedPropertiesWithoutUndo();
        return Save(Shrink(root), BowserPath);
    }

    /// <summary>The final goal: an axe on a stone pedestal. Locked while Bowser stands; reaching it ends the game (the axe drops).</summary>
    private static void AxeGoal(Transform parent, Vector3 localPos, Mats m)
    {
        var goal = new GameObject("Goal");
        goal.transform.SetParent(parent, false);
        goal.transform.localPosition = localPos;
        Shrink(goal);
        var sg = goal.AddComponent<StageGoal>();
        SetFloat(sg, "reachRadius", 0.5f); // jump within reach to grab it
        Visual(PrimitiveType.Cube, "Pedestal", goal.transform, new Vector3(0f, 0.25f, 0f), new Vector3(0.25f, 0.5f, 0.25f), m.CastleStone)
            .GetComponent<MeshFilter>().sharedMesh = BlockMesh();
        // A big double-bit axe standing on its handle, blades facing the player, as in the castle levels.
        var axe = Group("Axe", goal.transform, new Vector3(0f, 0.85f, 0f));
        Visual(PrimitiveType.Cylinder, "Handle", axe, Vector3.zero, new Vector3(0.04f, 0.32f, 0.04f), m.AxeHandle);
        Visual(PrimitiveType.Sphere, "Knob", axe, new Vector3(0f, 0.33f, 0f), Vector3.one * 0.07f, m.CoinRim);
        foreach (int side in new[] { -1, 1 })
        {
            // Each blade: a wide wedge (a cube turned 45°, squashed) with a bright edge.
            Visual(PrimitiveType.Cube, side < 0 ? "BladeL" : "BladeR", axe, new Vector3(0.11f * side, 0.2f, 0f), new Vector3(0.17f, 0.17f, 0.035f), m.CannonRim, new Vector3(0f, 0f, 45f));
            Visual(PrimitiveType.Cube, side < 0 ? "EdgeL" : "EdgeR", axe, new Vector3(0.21f * side, 0.2f, 0f), new Vector3(0.025f, 0.24f, 0.04f), m.White);
        }
        var gso = new SerializedObject(sg);
        gso.FindProperty("endsGame").boolValue = true;
        gso.FindProperty("pennant").objectReferenceValue = axe;
        gso.FindProperty("pennantBottom").floatValue = 0.55f;
        gso.FindProperty("celebrationEffect").objectReferenceValue = Effect(ConfettiPath);
        gso.ApplyModifiedPropertiesWithoutUndo();
    }

    /// <summary>The bonus room and the pieces it lays out at runtime (coin, exit pipe, wall block).</summary>
    private static GameObject BuildBonusRoom(Mats m)
    {
        var coin = Save(CoinObject(null, Vector3.zero, m), CoinPath);
        var exit = Save(WarpPipe(null, Vector3.zero, m, 1f, isExit: true), ExitPipePath);
        var wallRoot = new GameObject("UndergroundBlock");
        Visual(PrimitiveType.Cube, "Block", wallRoot.transform, Vector3.zero, Vector3.one * 0.25f, m.UndergroundBrick)
            .GetComponent<MeshFilter>().sharedMesh = BlockMesh();
        var wall = Save(wallRoot, WallBlockPath);

        var root = new GameObject("BonusRoom");
        var so = new SerializedObject(root.AddComponent<BonusRoom>());
        so.FindProperty("coinPrefab").objectReferenceValue = coin.GetComponent<Coin>();
        so.FindProperty("exitPipePrefab").objectReferenceValue = exit.GetComponent<WarpPipe>();
        so.FindProperty("wallBlockPrefab").objectReferenceValue = wall;
        so.FindProperty("floorMaterial").objectReferenceValue = m.UndergroundFloor;
        so.ApplyModifiedPropertiesWithoutUndo();
        return Save(root, BonusRoomPath);
    }

    private static GameObject BuildCompactStage(Mats m, GameObject coinPop, Pickups pickups)
    {
        var root = StageRoot("CompactStage", 0.6f, 1.0f, 2.0f);
        QuestionBlock(root.transform, new Vector3(0f, 1.8f, -0.15f), m, coinPop, pickups);
        Goal(root.transform, new Vector3(0f, 0f, 0.35f), m);
        return Save(root, CompactStagePath);
    }

    private static GameObject StageRoot(string name, float width, float depth, float clearance)
    {
        var root = new GameObject(name);
        var fp = root.AddComponent<StageFootprint>();
        fp.width = width;
        fp.depth = depth;
        fp.clearance = clearance;
        return root;
    }

    private static void QuestionBlock(Transform parent, Vector3 localPos, Mats m, GameObject coinPop, Pickups pickups, string name = "QuestionBlock")
    {
        var block = Visual(PrimitiveType.Cube, name, parent, localPos, Vector3.one * 0.25f * ObjectScale, m.Question);
        block.GetComponent<MeshFilter>().sharedMesh = BlockMesh();
        var qb = block.AddComponent<QuestionBlock>();
        Set(qb, "coinPopPrefab", coinPop);
        Set(qb, "fireFlowerPrefab", pickups.FireFlower.GetComponent<Pickup>());
        Set(qb, "oneUpPrefab", pickups.OneUp.GetComponent<Pickup>());
        Set(qb, "spentMaterial", m.Spent);
        var eh = block.AddComponent<EyeHeightPlacement>();
        SetFloat(eh, "offsetFromEyes", 0.35f); // bottom clears the head; reached by jumping
        SetFloat(eh, "minHeightAboveFloor", 1.1f);
    }

    // ---- effects (Burst variants) ----

    private static GameObject Effect(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(path);

    private static void BuildEffects(Mats m)
    {
        Directory.CreateDirectory(PrefabDir + "/Effects");

        // Power-up collected: golden sparkles.
        BuildBurst(SparklePath, 12, PrimitiveType.Cube, 0.028f, i => i % 3 == 0 ? m.CoinShine : m.Coin,
                   lifetime: 0.6f, speed: 1.1f, upBias: 0.6f, gravity: 0.6f, drag: 1f, spin: 540f);
        // Stomped: a small white puff that hangs and fades.
        BuildBurst(PoofPath, 8, PrimitiveType.Sphere, 0.055f, _ => m.Dust,
                   lifetime: 0.45f, speed: 0.7f, upBias: 0.2f, gravity: -0.3f, drag: 4f, spin: 0f);
        // Fireball hit: orange and yellow flame bits.
        BuildBurst(FireBurstPath, 9, PrimitiveType.Sphere, 0.045f, i => i % 2 == 0 ? m.Fire : m.FireCore,
                   lifetime: 0.35f, speed: 1.6f, upBias: 0.2f, gravity: 1f, drag: 2f, spin: 0f);
        // Cannon fired: grey smoke pushed out of the barrel.
        BuildBurst(SmokePath, 7, PrimitiveType.Sphere, 0.075f, _ => m.Smoke,
                   lifetime: 0.7f, speed: 0.7f, upBias: 0.3f, gravity: -0.3f, drag: 2.5f, spin: 0f, forwardBias: 1.5f);
        // Stage clear: confetti in the classic colours, fluttering down.
        var confetti = new[] { m.ConfettiRed, m.Coin, m.ConfettiGreen, m.ConfettiBlue, m.White };
        BuildBurst(ConfettiPath, 28, PrimitiveType.Cube, 0.03f, i => confetti[i % confetti.Length],
                   lifetime: 1.6f, speed: 1.7f, upBias: 1.3f, gravity: 2.2f, drag: 1.2f, spin: 540f, flat: true);
    }

    private static void BuildBurst(string path, int count, PrimitiveType shape, float size, System.Func<int, Material> material,
                                   float lifetime, float speed, float upBias, float gravity, float drag, float spin,
                                   float forwardBias = 0f, bool flat = false)
    {
        var root = new GameObject(Path.GetFileNameWithoutExtension(path));
        var burst = root.AddComponent<Burst>();
        for (int i = 0; i < count; i++)
        {
            var scale = flat ? new Vector3(size, size, size * 0.2f) : Vector3.one * size * (0.8f + 0.4f * (i % 3) / 2f);
            Visual(shape, "Piece", root.transform, Vector3.zero, scale, material(i));
        }
        var so = new SerializedObject(burst);
        so.FindProperty("lifetime").floatValue = lifetime;
        so.FindProperty("speed").floatValue = speed;
        so.FindProperty("upBias").floatValue = upBias;
        so.FindProperty("forwardBias").floatValue = forwardBias;
        so.FindProperty("gravity").floatValue = gravity;
        so.FindProperty("drag").floatValue = drag;
        so.FindProperty("spinDegreesPerSecond").floatValue = spin;
        so.ApplyModifiedPropertiesWithoutUndo();
        Save(root, path);
    }

    private static GameObject BuildBrickDebris(Mats m)
    {
        var root = new GameObject("BrickDebris");
        root.AddComponent<BrickDebris>();
        foreach (var (x, y) in new[] { (-1, 1), (1, 1), (-1, -1), (1, -1) })
            Visual(PrimitiveType.Cube, "Piece", root.transform, new Vector3(0.06f * x, 0.06f * y, 0f), Vector3.one * 0.11f, m.Brick)
                .GetComponent<MeshFilter>().sharedMesh = BlockMesh();
        return Save(root, BrickDebrisPath);
    }

    private static void Brick(Transform parent, Vector3 localPos, Mats m, GameObject coinPop, string name = "BrickBlock")
    {
        var brick = Visual(PrimitiveType.Cube, name, parent, localPos, Vector3.one * 0.25f * ObjectScale, m.Brick);
        brick.GetComponent<MeshFilter>().sharedMesh = BlockMesh();
        var bb = brick.AddComponent<BrickBlock>();
        Set(bb, "coinPopPrefab", coinPop);
        Set(bb, "debrisPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(BrickDebrisPath));
        var eh = brick.AddComponent<EyeHeightPlacement>();
        SetFloat(eh, "fractionOfEyes", 0.78f); // chest height, scaled to the player
        SetFloat(eh, "minHeightAboveFloor", 0.5f);
    }

    /// <summary>Goal flagpole: block base, a tall (1.8 m) green pole topped with a ball, a large white pennant with a green emblem — prominent across a room.</summary>
    private static void Goal(Transform parent, Vector3 localPos, Mats m)
    {
        var goal = new GameObject("Goal");
        goal.transform.SetParent(parent, false);
        goal.transform.localPosition = localPos;
        Shrink(goal);
        var sg = goal.AddComponent<StageGoal>();
        SetFloat(sg, "reachRadius", 0.5f); // jump within reach to grab the flag
        const float poleHeight = 1.8f; // prominent across a room
        const float flagScale = 1.6f;
        Visual(PrimitiveType.Cube, "Base", goal.transform, new Vector3(0f, 0.06f, 0f), Vector3.one * 0.12f, m.Spent)
            .GetComponent<MeshFilter>().sharedMesh = BlockMesh();
        Visual(PrimitiveType.Cylinder, "Pole", goal.transform, new Vector3(0f, 0.12f + poleHeight / 2f, 0f), new Vector3(0.03f, poleHeight / 2f, 0.03f), m.Pole);
        Visual(PrimitiveType.Sphere, "Ball", goal.transform, new Vector3(0f, 0.12f + poleHeight + 0.03f, 0f), Vector3.one * 0.08f, m.PoleBall);
        // The flag and its emblems move together: it slides down the pole when the goal is reached.
        var pennant = Group("Pennant", goal.transform, new Vector3(0f, 0.12f + poleHeight - 0.15f, 0f));
        pennant.localScale = Vector3.one * flagScale;
        var flag = Visual(PrimitiveType.Quad, "Flag", pennant, Vector3.zero, Vector3.one, m.Flag);
        flag.GetComponent<MeshFilter>().sharedMesh = PennantMesh();
        foreach (int face in new[] { -1, 1 })
            Visual(PrimitiveType.Cylinder, "Emblem", pennant, new Vector3(0.075f, 0f, 0.004f * face), new Vector3(0.05f, 0.002f, 0.05f), m.PoleBall, new Vector3(90f, 0f, 0f));
        var gso = new SerializedObject(sg);
        gso.FindProperty("pennant").objectReferenceValue = pennant;
        gso.FindProperty("pennantBottom").floatValue = 0.35f;
        gso.FindProperty("celebrationEffect").objectReferenceValue = Effect(ConfettiPath);
        gso.ApplyModifiedPropertiesWithoutUndo();
    }

    /// <summary>A flat, double-sided triangular pennant: 0.2 m along +X from the pole, 0.16 m tall, centred on its origin's height.</summary>
    private static Mesh PennantMesh()
    {
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(PennantMeshPath);
        if (mesh == null)
        {
            mesh = new Mesh { name = "Pennant" };
            AssetDatabase.CreateAsset(mesh, PennantMeshPath);
        }
        var top = new Vector3(0f, 0.08f, 0f);
        var bottom = new Vector3(0f, -0.08f, 0f);
        var tip = new Vector3(0.2f, 0f, 0f);
        mesh.Clear();
        mesh.SetVertices(new[] { top, tip, bottom, top, tip, bottom });
        mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.forward, Vector3.forward, Vector3.forward });
        mesh.SetUVs(0, new[] { new Vector2(0, 1), new Vector2(1, 0.5f), new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 0.5f), new Vector2(0, 0) });
        mesh.SetTriangles(new[] { 0, 1, 2, 3, 5, 4 }, 0); // front face (seen from -Z) clockwise; back face reversed
        mesh.RecalculateBounds();
        EditorUtility.SetDirty(mesh);
        return mesh;
    }

    // ---- helpers ----

    /// <summary>
    /// A unit cube whose side faces all show their texture upright (Unity's
    /// built-in cube draws some faces upside down, which turns "?" into "¿").
    /// Top and bottom are oriented to read from the -Z side.
    /// </summary>
    private static Mesh BlockMesh()
    {
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(BlockMeshPath);
        if (mesh == null)
        {
            mesh = new Mesh { name = "BlockCube" };
            AssetDatabase.CreateAsset(mesh, BlockMeshPath);
        }
        var verts = new System.Collections.Generic.List<Vector3>();
        var normals = new System.Collections.Generic.List<Vector3>();
        var uvs = new System.Collections.Generic.List<Vector2>();
        var tris = new System.Collections.Generic.List<int>();
        var faces = new (Vector3 n, Vector3 up)[]
        {
            (Vector3.back, Vector3.up), (Vector3.forward, Vector3.up), (Vector3.left, Vector3.up), (Vector3.right, Vector3.up),
            (Vector3.up, Vector3.forward), (Vector3.down, Vector3.back),
        };
        foreach (var (n, up) in faces)
        {
            var right = Vector3.Cross(up, -n); // the viewer's right, looking at the face from outside
            var c = n * 0.5f;
            int i = verts.Count;
            verts.Add(c - right * 0.5f - up * 0.5f); uvs.Add(new Vector2(0f, 0f));
            verts.Add(c - right * 0.5f + up * 0.5f); uvs.Add(new Vector2(0f, 1f));
            verts.Add(c + right * 0.5f + up * 0.5f); uvs.Add(new Vector2(1f, 1f));
            verts.Add(c + right * 0.5f - up * 0.5f); uvs.Add(new Vector2(1f, 0f));
            for (int k = 0; k < 4; k++) normals.Add(n);
            tris.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 }); // clockwise seen from outside
        }
        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        EditorUtility.SetDirty(mesh);
        return mesh;
    }

    /// <summary>
    /// Gameplay objects (blocks, pipes, enemies, pickups, the goal) are built at this size:
    /// Oliver found them a little large for comfort (2026-10-21). Scenery is sized separately, to fill the space.
    /// Only the visuals shrink; reach and hit distances are in world metres and stay the same.
    /// </summary>
    private const float ObjectScale = 0.87f;

    private static T Shrink<T>(T obj) where T : Object
    {
        var t = obj is GameObject go ? go.transform : obj as Transform;
        if (t != null) t.localScale *= ObjectScale;
        return obj;
    }

    private static Transform Group(string name, Transform parent, Vector3 localPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        return go.transform;
    }

    /// <summary>A primitive with its collider removed: targeting uses InteractableBase's registry, and colliders would interfere with placement.</summary>
    private static GameObject Visual(PrimitiveType type, string name, Transform parent, Vector3 localPos, Vector3 localScale, Material mat, Vector3 euler = default)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.Euler(euler);
        go.transform.localScale = localScale;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        return go;
    }

    private static GameObject Save(GameObject root, string path)
    {
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        return prefab;
    }

    /// <summary>Adds a persistent (scene-saved) listener unless it's already there.</summary>
    public static void AddListenerOnce(UnityEngine.Events.UnityEvent evt, UnityEngine.Events.UnityAction action)
    {
        for (int i = 0; i < evt.GetPersistentEventCount(); i++)
            if (evt.GetPersistentTarget(i) == (Object)action.Target && evt.GetPersistentMethodName(i) == action.Method.Name)
                return;
        UnityEditor.Events.UnityEventTools.AddVoidPersistentListener(evt, action);
    }

    private static void Set(Object target, string field, Object value)
    {
        var so = new SerializedObject(target);
        so.FindProperty(field).objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void SetFloat(Object target, string field, float value)
    {
        var so = new SerializedObject(target);
        so.FindProperty(field).floatValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private sealed class Pickups
    {
        public readonly GameObject FireFlower, OneUp;
        public Pickups(GameObject fireFlower, GameObject oneUp) { FireFlower = fireFlower; OneUp = oneUp; }
    }

    private sealed class Mats
    {
        public readonly Material Question = Mat("Question", Color.white, texture: PixelArt.QuestionBlock(), smoothness: 0.15f);
        public readonly Material Spent = Mat("SpentBlock", Color.white, texture: PixelArt.SpentBlock(), smoothness: 0.15f);
        public readonly Material Brick = Mat("Brick", Color.white, texture: PixelArt.Brick(), smoothness: 0.1f);
        public readonly Material Coin = Mat("Coin", new Color(1f, 0.8f, 0.15f), emission: new Color(0.25f, 0.18f, 0f), smoothness: 0.8f);
        public readonly Material CoinRim = Mat("CoinRim", new Color(0.85f, 0.55f, 0.05f), smoothness: 0.8f);
        public readonly Material CoinShine = Mat("CoinShine", new Color(1f, 0.96f, 0.7f), emission: new Color(0.3f, 0.28f, 0.15f));
        public readonly Material Stem = Mat("FlowerStem", new Color(0.2f, 0.65f, 0.25f));
        public readonly Material Flower = Mat("FlowerPetals", new Color(0.95f, 0.25f, 0.1f));
        public readonly Material FlowerRing = Mat("FlowerRing", new Color(1f, 0.75f, 0.1f));
        public readonly Material FlowerCentre = Mat("FlowerCentre", new Color(1f, 0.97f, 0.85f));
        public readonly Material Goomba = Mat("Goomba", new Color(0.6f, 0.3f, 0.12f));
        public readonly Material GoombaFace = Mat("GoombaFace", new Color(0.96f, 0.8f, 0.58f));
        public readonly Material GoombaFeet = Mat("GoombaFeet", new Color(0.28f, 0.14f, 0.06f));
        public readonly Material Black = Mat("Black", new Color(0.04f, 0.04f, 0.05f));
        public readonly Material BillBody = Mat("BillBody", new Color(0.09f, 0.09f, 0.11f), smoothness: 0.75f);
        public readonly Material White = Mat("White", Color.white);
        public readonly Material Dark = Mat("Dark", new Color(0.15f, 0.12f, 0.1f));
        public readonly Material Fire = Mat("Fire", new Color(1f, 0.45f, 0.05f), emission: new Color(1f, 0.35f, 0.02f));
        public readonly Material FireCore = Mat("FireCore", new Color(1f, 0.85f, 0.2f), emission: new Color(1f, 0.7f, 0.1f));
        public readonly Material Pole = Mat("GoalPole", new Color(0.3f, 0.75f, 0.3f), smoothness: 0.7f);
        public readonly Material PoleBall = Mat("GoalBall", new Color(0.1f, 0.5f, 0.15f), smoothness: 0.7f);
        public readonly Material Flag = Mat("GoalFlag", new Color(0.97f, 0.97f, 0.95f));
        public readonly Material OneUp = Mat("OneUp", new Color(0.2f, 0.75f, 0.3f));
        public readonly Material KoopaShell = Mat("KoopaShell", Color.white, texture: PixelArt.KoopaShell(), smoothness: 0.6f, tiling: new Vector2(2f, 1f));
        public readonly Material KoopaSkin = Mat("KoopaSkin", new Color(1f, 0.84f, 0.35f));
        public readonly Material KoopaBelly = Mat("KoopaBelly", new Color(1f, 0.93f, 0.62f));
        public readonly Material Pipe = Mat("Pipe", Color.white, texture: PixelArt.PipeSide(), smoothness: 0.55f);
        public readonly Material Dust = Mat("Dust", new Color(0.95f, 0.93f, 0.9f), smoothness: 0.1f);
        public readonly Material Smoke = Mat("Smoke", new Color(0.55f, 0.55f, 0.58f), smoothness: 0.1f);
        public readonly Material ConfettiRed = Mat("ConfettiRed", new Color(0.95f, 0.2f, 0.2f), emission: new Color(0.2f, 0.02f, 0.02f));
        public readonly Material ConfettiGreen = Mat("ConfettiGreen", new Color(0.2f, 0.8f, 0.3f), emission: new Color(0.02f, 0.15f, 0.04f));
        public readonly Material ConfettiBlue = Mat("ConfettiBlue", new Color(0.25f, 0.5f, 1f), emission: new Color(0.03f, 0.08f, 0.2f));
        public readonly Material UndergroundBrick = Mat("UndergroundBrick", Color.white, texture: PixelArt.UndergroundBrick(), smoothness: 0.1f);
        public readonly Material UndergroundFloor = Mat("UndergroundFloor", Color.white, texture: PixelArt.UndergroundFloor(), smoothness: 0.1f);
        public readonly Material CannonSteel = Mat("CannonSteel", new Color(0.32f, 0.34f, 0.38f));
        public readonly Material CannonBarrel = Mat("CannonBarrel", new Color(0.5f, 0.52f, 0.56f));
        public readonly Material CannonRim = Mat("CannonRim", new Color(0.82f, 0.84f, 0.88f));
        public readonly Material CannonBore = Mat("CannonBore", new Color(0.05f, 0.05f, 0.05f));
        public readonly Material CannonGlow = Mat("CannonGlow", new Color(1f, 0.6f, 0.2f), emission: new Color(0.9f, 0.45f, 0.1f));
        // World 2 and scenery
        public readonly Material Stair = Mat("StairBlock", Color.white, texture: PixelArt.SpentBlock(), smoothness: 0.15f);
        public readonly Material Hill = Mat("Hill", new Color(0.25f, 0.7f, 0.3f), smoothness: 0.2f);
        public readonly Material HillDark = Mat("HillDark", new Color(0.08f, 0.35f, 0.12f));
        public readonly Material Bush = Mat("Bush", new Color(0.45f, 0.85f, 0.3f), smoothness: 0.2f);
        public readonly Material Cloud = Mat("Cloud", new Color(0.97f, 0.98f, 1f), emission: new Color(0.12f, 0.12f, 0.14f), smoothness: 0.1f);
        public readonly Material CastleBrick = Mat("CastleBrick", Color.white, texture: PixelArt.Brick(), smoothness: 0.1f, tiling: new Vector2(2f, 2f));
        public readonly Material CastleStone = Mat("CastleStone", Color.white, texture: PixelArt.CastleStone(), smoothness: 0.1f);
        public readonly Material Lava = Mat("Lava", new Color(1f, 0.35f, 0.05f), emission: new Color(1f, 0.3f, 0.02f), smoothness: 0.8f);
        public readonly Material MushroomCap = Mat("MushroomCap", new Color(0.9f, 0.2f, 0.15f), smoothness: 0.4f);
        public readonly Material BowserSkin = Mat("BowserSkin", new Color(0.95f, 0.78f, 0.25f));
        public readonly Material BowserHair = Mat("BowserHair", new Color(0.95f, 0.35f, 0.1f));
        public readonly Material AxeHandle = Mat("AxeHandle", new Color(0.75f, 0.15f, 0.1f));
        public readonly Material BlobShadow = TransparentMat("BlobShadow", PixelArt.BlobShadow());

        /// <summary>Unlit, alpha-blended, no depth write: for soft shadows on the floor.</summary>
        private static Material TransparentMat(string name, Texture2D texture)
        {
            var path = $"{MaterialDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                Directory.CreateDirectory(MaterialDir);
                mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetTexture("_BaseMap", texture);
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material Mat(string name, Color color, Color? emission = null, Texture2D texture = null, float smoothness = 0.5f, Vector2? tiling = null)
        {
            var path = $"{MaterialDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                Directory.CreateDirectory(MaterialDir);
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            mat.SetTexture("_BaseMap", texture);
            mat.SetTextureScale("_BaseMap", tiling ?? Vector2.one);
            mat.SetFloat("_Smoothness", smoothness);
            if (emission.HasValue)
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", emission.Value);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
