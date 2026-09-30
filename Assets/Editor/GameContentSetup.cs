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
/// - Scene: LevelGenerator templates, EventResolver fireball prefab,
///   GameStateManager.OnGameOver/OnGameRestarted → LevelGenerator.EndGame/RestartLoop,
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
    public const string GoombaPath = PrefabDir + "/Goomba.prefab";
    public const string FireFlowerPath = PrefabDir + "/FireFlower.prefab";
    public const string OneUpPath = PrefabDir + "/OneUp.prefab";
    public const string KoopaPath = PrefabDir + "/Koopa.prefab";
    public const string BulletBillPath = PrefabDir + "/BulletBill.prefab";
    public const string CoinPopPath = PrefabDir + "/CoinPop.prefab";
    public const string StandardStagePath = StageDir + "/StandardStage.prefab";
    public const string CompactStagePath = StageDir + "/CompactStage.prefab";
    public const string LargeStagePath = StageDir + "/LargeStage.prefab";

    [MenuItem("Tools/MARIO/Build Game Content")]
    public static void Run()
    {
        Directory.CreateDirectory(PrefabDir);
        Directory.CreateDirectory(StageDir);

        var m = new Mats();
        var coinPop = BuildCoinPop(m);
        var pickups = new Pickups(BuildFireFlower(m), BuildOneUp(m));
        var goomba = BuildGoomba(m);
        var koopa = BuildKoopa(m);
        var bulletBill = BuildBulletBill(m);
        var fireball = BuildFireball(m);
        var standard = BuildStandardStage(m, coinPop, pickups, goomba, koopa, bulletBill);
        var compact = BuildCompactStage(m, coinPop, pickups);
        var large = BuildLargeStage(m, coinPop, pickups, goomba, koopa, bulletBill);

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
        lg.ApplyModifiedPropertiesWithoutUndo();

        var er = new SerializedObject(resolver);
        er.FindProperty("fireballPrefab").objectReferenceValue = fireball.GetComponent<Fireball>();
        er.ApplyModifiedPropertiesWithoutUndo();

        StageLoopSetup.AddListenerOnce(gameState.OnGameOver, levelGenerator.EndGame);
        StageLoopSetup.AddListenerOnce(gameState.OnGameRestarted, levelGenerator.RestartLoop);
        EditorUtility.SetDirty(gameState);

        // Design: scan until floor area exceeds the largest template's footprint (6 m²), with margin;
        // a smaller room is accepted once its floor stops growing (at least 3 m², enough for Standard).
        var ss = new SerializedObject(session);
        ss.FindProperty("requiredFloorArea").floatValue = 7f;
        ss.FindProperty("minFloorArea").floatValue = 3f;
        ss.ApplyModifiedPropertiesWithoutUndo();

        var manager = gameState.gameObject;
        if (manager.GetComponent<Difficulty>() == null) manager.AddComponent<Difficulty>();
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
        Debug.Log("[GameContentSetup] Done. Built CoinPop, FireFlower, OneUp, Goomba, Koopa, BulletBill, Fireball, LargeStage, StandardStage, CompactStage; scene wired.");
    }

    // ---- element prefabs ----

    private static GameObject BuildCoinPop(Mats m)
    {
        var root = new GameObject("CoinPop");
        root.AddComponent<CoinPop>();
        Visual(PrimitiveType.Cylinder, "Coin", root.transform, Vector3.zero, new Vector3(0.12f, 0.008f, 0.12f), m.Coin, new Vector3(90f, 0f, 0f));
        return Save(root, CoinPopPath);
    }

    private static GameObject BuildFireFlower(Mats m)
    {
        var root = new GameObject("FireFlower");
        root.AddComponent<Pickup>(); // Kind defaults to FireFlower
        Visual(PrimitiveType.Cylinder, "Stem", root.transform, new Vector3(0f, 0.08f, 0f), new Vector3(0.025f, 0.08f, 0.025f), m.Stem);
        Visual(PrimitiveType.Sphere, "Petals", root.transform, new Vector3(0f, 0.2f, 0f), new Vector3(0.14f, 0.14f, 0.05f), m.Flower);
        Visual(PrimitiveType.Sphere, "Centre", root.transform, new Vector3(0f, 0.2f, 0.025f), new Vector3(0.06f, 0.06f, 0.03f), m.Coin);
        return Save(root, FireFlowerPath);
    }

    private static GameObject BuildOneUp(Mats m)
    {
        var root = new GameObject("OneUp");
        var p = root.AddComponent<Pickup>();
        var so = new SerializedObject(p);
        so.FindProperty("kind").enumValueIndex = (int)Pickup.Kind.OneUp;
        so.ApplyModifiedPropertiesWithoutUndo();
        Visual(PrimitiveType.Cylinder, "Stalk", root.transform, new Vector3(0f, 0.05f, 0f), new Vector3(0.09f, 0.05f, 0.09f), m.White);
        Visual(PrimitiveType.Sphere, "Cap", root.transform, new Vector3(0f, 0.12f, 0f), new Vector3(0.2f, 0.12f, 0.2f), m.OneUp);
        Visual(PrimitiveType.Sphere, "SpotF", root.transform, new Vector3(0f, 0.14f, 0.085f), new Vector3(0.06f, 0.06f, 0.02f), m.White);
        Visual(PrimitiveType.Sphere, "SpotB", root.transform, new Vector3(0f, 0.14f, -0.085f), new Vector3(0.06f, 0.06f, 0.02f), m.White);
        return Save(root, OneUpPath);
    }

    private static GameObject BuildKoopa(Mats m)
    {
        var root = new GameObject("Koopa");
        var k = root.AddComponent<Koopa>();
        Visual(PrimitiveType.Sphere, "Shell", root.transform, new Vector3(0f, 0.09f, 0f), new Vector3(0.26f, 0.16f, 0.3f), m.KoopaShell);
        var head = Visual(PrimitiveType.Sphere, "Head", root.transform, new Vector3(0f, 0.2f, 0.15f), Vector3.one * 0.11f, m.KoopaSkin);
        var footL = Visual(PrimitiveType.Cube, "FootL", root.transform, new Vector3(-0.07f, 0.015f, 0.05f), new Vector3(0.07f, 0.03f, 0.1f), m.KoopaSkin);
        var footR = Visual(PrimitiveType.Cube, "FootR", root.transform, new Vector3(0.07f, 0.015f, 0.05f), new Vector3(0.07f, 0.03f, 0.1f), m.KoopaSkin);
        var so = new SerializedObject(k);
        so.FindProperty("scoreValue").intValue = 200;
        so.FindProperty("bodyHeight").floatValue = 0.15f;
        var hide = so.FindProperty("hideInShell");
        hide.arraySize = 3;
        hide.GetArrayElementAtIndex(0).objectReferenceValue = head;
        hide.GetArrayElementAtIndex(1).objectReferenceValue = footL;
        hide.GetArrayElementAtIndex(2).objectReferenceValue = footR;
        so.ApplyModifiedPropertiesWithoutUndo();
        return Save(root, KoopaPath);
    }

    private static GameObject BuildBulletBill(Mats m)
    {
        var root = new GameObject("BulletBill");
        root.AddComponent<BulletBill>();
        // Nose along +Z (the flight direction).
        Visual(PrimitiveType.Capsule, "Hull", root.transform, Vector3.zero, new Vector3(0.22f, 0.2f, 0.22f), m.Dark, new Vector3(90f, 0f, 0f));
        Visual(PrimitiveType.Cylinder, "Tail", root.transform, new Vector3(0f, 0f, -0.2f), new Vector3(0.24f, 0.03f, 0.24f), m.Dark, new Vector3(90f, 0f, 0f));
        Visual(PrimitiveType.Sphere, "EyeL", root.transform, new Vector3(-0.06f, 0.04f, 0.12f), Vector3.one * 0.05f, m.White);
        Visual(PrimitiveType.Sphere, "EyeR", root.transform, new Vector3(0.06f, 0.04f, 0.12f), Vector3.one * 0.05f, m.White);
        return Save(root, BulletBillPath);
    }

    /// <summary>A Bill Blaster tower: column from the floor to a turning head at head height (both resized at runtime).</summary>
    private static void Cannon(Transform parent, Vector3 localPos, Mats m, GameObject bulletBill)
    {
        var root = new GameObject("BulletBillCannon");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = localPos;
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
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static GameObject BuildGoomba(Mats m)
    {
        var root = new GameObject("Goomba");
        root.AddComponent<Goomba>();
        Visual(PrimitiveType.Sphere, "Body", root.transform, new Vector3(0f, 0.11f, 0f), new Vector3(0.26f, 0.22f, 0.26f), m.Goomba);
        Visual(PrimitiveType.Sphere, "EyeL", root.transform, new Vector3(-0.05f, 0.15f, 0.11f), Vector3.one * 0.05f, m.White);
        Visual(PrimitiveType.Sphere, "EyeR", root.transform, new Vector3(0.05f, 0.15f, 0.11f), Vector3.one * 0.05f, m.White);
        Visual(PrimitiveType.Cube, "FootL", root.transform, new Vector3(-0.06f, 0.015f, 0.02f), new Vector3(0.08f, 0.03f, 0.1f), m.Dark);
        Visual(PrimitiveType.Cube, "FootR", root.transform, new Vector3(0.06f, 0.015f, 0.02f), new Vector3(0.08f, 0.03f, 0.1f), m.Dark);
        return Save(root, GoombaPath);
    }

    private static GameObject BuildFireball(Mats m)
    {
        var root = new GameObject("Fireball");
        root.AddComponent<Fireball>();
        Visual(PrimitiveType.Sphere, "Ball", root.transform, Vector3.zero, Vector3.one * 0.08f, m.Fire);
        return Save(root, FireballPath);
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
        WarpPipe(root.transform, new Vector3(-0.6f, 0f, -0.2f), m); // near half: a shortcut past the rest
        Goal(root.transform, new Vector3(0f, 0f, 1.3f), m);
        return Save(root, LargeStagePath);
    }

    /// <summary>A short green pipe on the floor; squat on it to warp (appears on some stages only).</summary>
    private static void WarpPipe(Transform parent, Vector3 localPos, Mats m)
    {
        var root = new GameObject("WarpPipe");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = localPos;
        root.AddComponent<WarpPipe>();
        Visual(PrimitiveType.Cylinder, "Body", root.transform, new Vector3(0f, 0.125f, 0f), new Vector3(0.36f, 0.125f, 0.36f), m.Pipe);
        Visual(PrimitiveType.Cylinder, "Rim", root.transform, new Vector3(0f, 0.27f, 0f), new Vector3(0.44f, 0.03f, 0.44f), m.PipeRim);
        Visual(PrimitiveType.Cylinder, "Opening", root.transform, new Vector3(0f, 0.301f, 0f), new Vector3(0.32f, 0.002f, 0.32f), m.CannonBore);
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
        var block = Visual(PrimitiveType.Cube, name, parent, localPos, Vector3.one * 0.25f, m.Question);
        var qb = block.AddComponent<QuestionBlock>();
        Set(qb, "coinPopPrefab", coinPop);
        Set(qb, "fireFlowerPrefab", pickups.FireFlower.GetComponent<Pickup>());
        Set(qb, "oneUpPrefab", pickups.OneUp.GetComponent<Pickup>());
        Set(qb, "spentMaterial", m.Spent);
        var eh = block.AddComponent<EyeHeightPlacement>();
        SetFloat(eh, "offsetFromEyes", 0.3f);
        SetFloat(eh, "minHeightAboveFloor", 1.2f);
    }

    private static void Brick(Transform parent, Vector3 localPos, Mats m, GameObject coinPop, string name = "BrickBlock")
    {
        var brick = Visual(PrimitiveType.Cube, name, parent, localPos, Vector3.one * 0.25f, m.Brick);
        var bb = brick.AddComponent<BrickBlock>();
        Set(bb, "coinPopPrefab", coinPop);
        var eh = brick.AddComponent<EyeHeightPlacement>();
        SetFloat(eh, "offsetFromEyes", -0.35f);
        SetFloat(eh, "minHeightAboveFloor", 0.6f);
    }

    private static void Goal(Transform parent, Vector3 localPos, Mats m)
    {
        var goal = new GameObject("Goal");
        goal.transform.SetParent(parent, false);
        goal.transform.localPosition = localPos;
        var sg = goal.AddComponent<StageGoal>();
        SetFloat(sg, "reachRadius", 0.35f);
        Visual(PrimitiveType.Cylinder, "Pole", goal.transform, new Vector3(0f, 0.25f, 0f), new Vector3(0.02f, 0.25f, 0.02f), m.Pole);
        Visual(PrimitiveType.Cube, "Flag", goal.transform, new Vector3(0.075f, 0.44f, 0f), new Vector3(0.15f, 0.1f, 0.01f), m.Flag);
    }

    // ---- helpers ----

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
        public readonly Material Question = Mat("Question", new Color(1f, 0.78f, 0.1f));
        public readonly Material Spent = Mat("SpentBlock", new Color(0.45f, 0.3f, 0.15f));
        public readonly Material Brick = Mat("Brick", new Color(0.72f, 0.3f, 0.18f));
        public readonly Material Coin = Mat("Coin", new Color(1f, 0.85f, 0.2f));
        public readonly Material Stem = Mat("FlowerStem", new Color(0.2f, 0.65f, 0.25f));
        public readonly Material Flower = Mat("FlowerPetals", new Color(1f, 0.45f, 0.1f));
        public readonly Material Goomba = Mat("Goomba", new Color(0.55f, 0.33f, 0.18f));
        public readonly Material White = Mat("White", Color.white);
        public readonly Material Dark = Mat("Dark", new Color(0.15f, 0.12f, 0.1f));
        public readonly Material Fire = Mat("Fire", new Color(1f, 0.35f, 0.05f));
        public readonly Material Pole = Mat("GoalPole", new Color(0.85f, 0.85f, 0.85f));
        public readonly Material Flag = Mat("GoalFlag", new Color(1f, 0.8f, 0.1f));
        public readonly Material OneUp = Mat("OneUp", new Color(0.2f, 0.75f, 0.3f));
        public readonly Material KoopaShell = Mat("KoopaShell", new Color(0.15f, 0.6f, 0.2f));
        public readonly Material KoopaSkin = Mat("KoopaSkin", new Color(1f, 0.85f, 0.45f));
        public readonly Material Pipe = Mat("Pipe", new Color(0.15f, 0.65f, 0.2f));
        public readonly Material PipeRim = Mat("PipeRim", new Color(0.1f, 0.5f, 0.15f));
        public readonly Material CannonSteel = Mat("CannonSteel", new Color(0.32f, 0.34f, 0.38f));
        public readonly Material CannonBarrel = Mat("CannonBarrel", new Color(0.5f, 0.52f, 0.56f));
        public readonly Material CannonRim = Mat("CannonRim", new Color(0.82f, 0.84f, 0.88f));
        public readonly Material CannonBore = Mat("CannonBore", new Color(0.05f, 0.05f, 0.05f));
        public readonly Material CannonGlow = Mat("CannonGlow", new Color(1f, 0.6f, 0.2f), emission: new Color(0.9f, 0.45f, 0.1f));

        private static Material Mat(string name, Color color, Color? emission = null)
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
