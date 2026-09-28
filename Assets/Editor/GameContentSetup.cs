using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds pass-1 game content (Docs/GAME_DESIGN.md) from primitives and wires
/// it into the scene. Idempotent — rebuilds every asset it owns.
/// - Materials (Assets/Materials), element prefabs (Assets/Prefabs):
///   CoinPop, FireFlower, Goomba, Fireball
/// - Stage templates (Assets/Stages): StandardStage 1.2×2.0 m (? block,
///   brick, Goomba spawner, flag) and CompactStage 0.6×1.0 m (? block, flag)
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
    public const string CoinPopPath = PrefabDir + "/CoinPop.prefab";
    public const string StandardStagePath = StageDir + "/StandardStage.prefab";
    public const string CompactStagePath = StageDir + "/CompactStage.prefab";

    [MenuItem("Tools/MARIO/Build Game Content")]
    public static void Run()
    {
        Directory.CreateDirectory(PrefabDir);
        Directory.CreateDirectory(StageDir);

        var m = new Mats();
        var coinPop = BuildCoinPop(m);
        var fireFlower = BuildFireFlower(m);
        var goomba = BuildGoomba(m);
        var fireball = BuildFireball(m);
        var standard = BuildStandardStage(m, coinPop, fireFlower, goomba);
        var compact = BuildCompactStage(m, coinPop, fireFlower);

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var levelGenerator = Object.FindAnyObjectByType<LevelGenerator>();
        var gameState = Object.FindAnyObjectByType<GameStateManager>();
        var resolver = Object.FindAnyObjectByType<EventResolver>();
        var session = Object.FindAnyObjectByType<ARSessionManager>();

        var lg = new SerializedObject(levelGenerator);
        var templates = lg.FindProperty("stageTemplates");
        templates.arraySize = 2;
        templates.GetArrayElementAtIndex(0).objectReferenceValue = standard;
        templates.GetArrayElementAtIndex(1).objectReferenceValue = compact;
        lg.ApplyModifiedPropertiesWithoutUndo();

        var er = new SerializedObject(resolver);
        er.FindProperty("fireballPrefab").objectReferenceValue = fireball.GetComponent<Fireball>();
        er.ApplyModifiedPropertiesWithoutUndo();

        StageLoopSetup.AddListenerOnce(gameState.OnGameOver, levelGenerator.EndGame);
        StageLoopSetup.AddListenerOnce(gameState.OnGameRestarted, levelGenerator.RestartLoop);
        EditorUtility.SetDirty(gameState);

        // Design: scan until floor area exceeds the largest template's footprint (2.4 m²), with margin.
        var ss = new SerializedObject(session);
        ss.FindProperty("requiredFloorArea").floatValue = 4f;
        ss.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[GameContentSetup] Done. Built CoinPop, FireFlower, Goomba, Fireball, StandardStage, CompactStage; scene wired.");
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

    private static GameObject BuildStandardStage(Mats m, GameObject coinPop, GameObject fireFlower, GameObject goomba)
    {
        var root = StageRoot("StandardStage", 1.2f, 2.0f, 2.0f);
        QuestionBlock(root.transform, new Vector3(-0.25f, 1.8f, -0.5f), m, coinPop, fireFlower);
        Brick(root.transform, new Vector3(0.35f, 1.2f, -0.1f), m, coinPop);

        var spawner = new GameObject("GoombaSpawner");
        spawner.transform.SetParent(root.transform, false);
        spawner.transform.localPosition = new Vector3(0f, 0f, 0.55f);
        spawner.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // face the player's side
        var es = spawner.AddComponent<EnemySpawner>();
        Set(es, "enemyPrefab", goomba.GetComponent<Goomba>());

        Goal(root.transform, new Vector3(0f, 0f, 0.85f), m);
        return Save(root, StandardStagePath);
    }

    private static GameObject BuildCompactStage(Mats m, GameObject coinPop, GameObject fireFlower)
    {
        var root = StageRoot("CompactStage", 0.6f, 1.0f, 2.0f);
        QuestionBlock(root.transform, new Vector3(0f, 1.8f, -0.15f), m, coinPop, fireFlower);
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

    private static void QuestionBlock(Transform parent, Vector3 localPos, Mats m, GameObject coinPop, GameObject fireFlower)
    {
        var block = Visual(PrimitiveType.Cube, "QuestionBlock", parent, localPos, Vector3.one * 0.25f, m.Question);
        var qb = block.AddComponent<QuestionBlock>();
        Set(qb, "coinPopPrefab", coinPop);
        Set(qb, "fireFlowerPrefab", fireFlower.GetComponent<Pickup>());
        Set(qb, "spentMaterial", m.Spent);
        var eh = block.AddComponent<EyeHeightPlacement>();
        SetFloat(eh, "offsetFromEyes", 0.3f);
        SetFloat(eh, "minHeightAboveFloor", 1.2f);
    }

    private static void Brick(Transform parent, Vector3 localPos, Mats m, GameObject coinPop)
    {
        var brick = Visual(PrimitiveType.Cube, "BrickBlock", parent, localPos, Vector3.one * 0.25f, m.Brick);
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

        private static Material Mat(string name, Color color)
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
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
