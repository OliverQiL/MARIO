using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Renders the game's prefabs side by side to PNGs, for checking art without
/// entering Play Mode or a device: blocks, enemies, items, and a close-up.
///
/// Headless (Unity closed, after GameContentSetup):
///   Unity -batchmode -quit -projectPath . -executeMethod ArtPreview.Run -out &lt;dir&gt;
/// Menu: Tools ▸ MARIO ▸ Render Art Preview (writes to Logs/ArtPreview).
/// </summary>
public static class ArtPreview
{
    [MenuItem("Tools/MARIO/Render Art Preview")]
    public static void RunFromMenu() => Render("Logs/ArtPreview");

    public static void Run()
    {
        var args = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(args, "-out");
        Render(i >= 0 && i + 1 < args.Length ? args[i + 1] : "Logs/ArtPreview");
    }

    private static void Render(string outDir)
    {
        // Batch mode + the SRP batcher drew every textured block with the first block's texture.
        var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
        bool batcher = urp != null && urp.useSRPBatcher;
        if (urp != null) urp.useSRPBatcher = false;
        try { RenderAll(outDir); }
        finally { if (urp != null) urp.useSRPBatcher = batcher; }
    }

    private static void RenderAll(string outDir)
    {
        Directory.CreateDirectory(outDir);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var light = new GameObject("Sun", typeof(Light)).GetComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.3f;
        light.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f);

        var stage = AssetDatabase.LoadAssetAtPath<GameObject>(GameContentSetup.StandardStagePath);
        var spentMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/SpentBlock.mat");

        // Blocks: ? block, spent block, brick.
        var blocks = new GameObject("Blocks").transform;
        var q = Place(stage.transform.Find("QuestionBlock").gameObject, blocks, new Vector3(-0.4f, 0.125f, 0f), 25f);
        var spent = Place(stage.transform.Find("QuestionBlock").gameObject, blocks, new Vector3(0f, 0.125f, 0f), 25f);
        foreach (var r in spent.GetComponentsInChildren<Renderer>()) r.sharedMaterial = spentMat;
        Place(stage.transform.Find("BrickBlock").gameObject, blocks, new Vector3(0.4f, 0.125f, 0f), 25f);
        Shoot(Path.Combine(outDir, "blocks.png"), new Vector3(0f, 0.45f, -1.25f), new Vector3(0f, 0.1f, 0f), 1600, 640);
        Object.DestroyImmediate(blocks.gameObject);

        // Enemies: Goomba, Koopa, Koopa shell, Bullet Bill — facing the camera, turned a little.
        var enemies = new GameObject("Enemies").transform;
        Place(Load(GameContentSetup.GoombaPath), enemies, new Vector3(-0.54f, 0f, 0f), 200f);
        Place(Load(GameContentSetup.KoopaPath), enemies, new Vector3(-0.18f, 0f, 0f), 210f);
        var shell = Place(Load(GameContentSetup.KoopaPath), enemies, new Vector3(0.18f, 0f, 0f), 210f);
        shell.transform.Find("Model/Head").gameObject.SetActive(false);
        shell.transform.Find("Model/Feet").gameObject.SetActive(false);
        Place(Load(GameContentSetup.BulletBillPath), enemies, new Vector3(0.56f, 0.13f, 0f), 215f);
        Shoot(Path.Combine(outDir, "enemies.png"), new Vector3(0f, 0.32f, -1.05f), new Vector3(0f, 0.1f, 0f), 1800, 700);
        Object.DestroyImmediate(enemies.gameObject);

        // Items: fire flower, 1-Up, coin, fireball, warp pipe, goal.
        var items = new GameObject("Items").transform;
        Place(Load(GameContentSetup.FireFlowerPath), items, new Vector3(-0.75f, 0f, 0f), 180f);
        Place(Load(GameContentSetup.OneUpPath), items, new Vector3(-0.45f, 0f, 0f), 180f);
        Place(Load(GameContentSetup.CoinPopPath), items, new Vector3(-0.18f, 0.15f, 0f), 160f).GetComponent<CoinPop>().enabled = false;
        Place(Load(GameContentSetup.FireballPath), items, new Vector3(0.05f, 0.12f, 0f), 0f);
        Place(stage.transform.Find("WarpPipe").gameObject, items, new Vector3(0.35f, 0f, 0f), 0f);
        Place(stage.transform.Find("Goal").gameObject, items, new Vector3(0.72f, 0f, 0.1f), 160f);
        Shoot(Path.Combine(outDir, "items.png"), new Vector3(0f, 0.75f, -2.5f), new Vector3(0f, 0.5f, 0f), 1800, 900);
        Object.DestroyImmediate(items.gameObject);

        // Close-ups.
        var close = new GameObject("Close").transform;
        Place(Load(GameContentSetup.GoombaPath), close, new Vector3(-0.17f, 0f, 0f), 195f);
        Place(Load(GameContentSetup.KoopaPath), close, new Vector3(0.17f, 0f, 0f), 200f);
        Shoot(Path.Combine(outDir, "closeup.png"), new Vector3(0f, 0.2f, -0.6f), new Vector3(0f, 0.12f, 0f), 1400, 700);
        Object.DestroyImmediate(close.gameObject);

        RenderHud(outDir);
        Debug.Log($"[ArtPreview] Wrote blocks.png, enemies.png, items.png, closeup.png, hud.png, hud_gameover.png to {Path.GetFullPath(outDir)}");
    }

    /// <summary>
    /// The HUD at iPhone 17 landscape resolution, filled with sample values: in
    /// play, and on the game-over screen. Opens the scene without saving it.
    /// </summary>
    private static void RenderHud(string outDir)
    {
        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
        var canvas = Object.FindAnyObjectByType<Canvas>();
        var hud = canvas.GetComponent<HUDController>();
        var so = new SerializedObject(hud);
        TMP_Text T(string field) => (TMP_Text)so.FindProperty(field).objectReferenceValue;

        var camGO = new GameObject("HudCamera", typeof(Camera));
        var cam = camGO.GetComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.55f, 0.5f, 0.45f); // a stand-in for the camera feed
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = cam;
        canvas.planeDistance = 1f;

        T("scoreText").text = "002150";
        T("stageText").text = "4";
        T("livesText").text = "x3";
        ((Image)so.FindProperty("powerIcon").objectReferenceValue).enabled = true;
        T("powerEmptyText").enabled = false;
        T("connectionText").text = "<color=#FF5A4D>●</color> OFFLINE";
        T("bannerText").text = "FLAG 2.6 M";
        T("bannerText").color = new Color(0.8f, 0.8f, 0.8f);
        T("titleText").text = "STAGE 4";
        T("titleText").color = Color.white;
        T("feedbackText").text = "STOMP!  +100";
        T("feedbackText").color = new Color(0.3f, 1f, 0.4f);
        ShootCanvas(cam, Path.Combine(outDir, "hud.png"));

        var panel = (GameObject)so.FindProperty("gameOverPanel").objectReferenceValue;
        panel.SetActive(true);
        T("gameOverStatsText").text = "SCORE 002150   STAGE 4";
        T("gameOverPromptText").enabled = true;
        T("titleText").text = "";
        T("feedbackText").text = "";
        T("bannerText").text = "";
        ShootCanvas(cam, Path.Combine(outDir, "hud_gameover.png"));
    }

    private static void ShootCanvas(Camera cam, string path)
    {
        const int width = 2622, height = 1206; // iPhone 17, landscape
        var rt = new RenderTexture(width, height, 24);
        cam.targetTexture = rt;
        Canvas.ForceUpdateCanvases();
        foreach (var t in Object.FindObjectsByType<TMP_Text>(FindObjectsSortMode.None)) t.ForceMeshUpdate();
        Canvas.ForceUpdateCanvases();
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        cam.targetTexture = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(tex);
    }

    private static GameObject Load(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(path);

    private static GameObject Place(GameObject source, Transform parent, Vector3 position, float yaw)
    {
        var go = Object.Instantiate(source, parent);
        go.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
        return go;
    }

    private static void Shoot(string path, Vector3 from, Vector3 lookAt, int width, int height)
    {
        var camGO = new GameObject("PreviewCamera", typeof(Camera));
        var cam = camGO.GetComponent<Camera>();
        cam.transform.position = from;
        cam.transform.LookAt(lookAt);
        cam.fieldOfView = 40f;
        cam.nearClipPlane = 0.05f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.62f, 0.8f, 0.95f);
        var rt = new RenderTexture(width, height, 24) { antiAliasing = 4 };
        cam.targetTexture = rt;
        cam.Render();

        RenderTexture.active = rt;
        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        File.WriteAllBytes(path, tex.EncodeToPNG());

        cam.targetTexture = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(tex);
        Object.DestroyImmediate(camGO);
    }
}
