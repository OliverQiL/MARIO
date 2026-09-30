using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

/// <summary>
/// Builds the HUDController layout on the scene's Canvas. Idempotent.
/// Styled after the classic Mario HUD: the Press Start 2P pixel font (SIL OFL,
/// Assets/Fonts) with a drop shadow and thin outline so it reads over any
/// camera background, a labelled top bar with pixel-art icons, a centre title,
/// and a game-over screen. The LiDAR debug readout stays bottom-left in the
/// default font.
///
/// Layout is for landscape with the Canvas at constant pixel size; the left
/// margin clears the Dynamic Island.
///
/// Menu:     Tools ▸ MARIO ▸ Configure HUD
/// Headless: Unity -batchmode -quit -projectPath . -executeMethod HUDSetup.Run
/// </summary>
public static class HUDSetup
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string FontFile = "Assets/Fonts/PressStart2P-Regular.ttf";
    private const string FontAssetPath = "Assets/Fonts/PressStart2P SDF.asset";
    private const string ShadowMaterialPath = "Assets/Fonts/PressStart2P Shadow.mat";
    public const float SideMargin = 180f; // clears the Dynamic Island in landscape
    public const string LidarHudName = "LidarDebug (TMP)";

    [MenuItem("Tools/MARIO/Configure HUD")]
    public static void Run()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var canvas = Object.FindAnyObjectByType<Canvas>();
        if (canvas == null) throw new System.Exception("[HUDSetup] No Canvas in scene.");
        var root = canvas.transform;

        // Retire the pre-styling objects.
        foreach (var old in new[] { "Stats (TMP)", "ScanHUD" })
        {
            var t = root.Find(old);
            if (t != null) Object.DestroyImmediate(t.gameObject);
        }

        var font = EnsureFont();
        var shadow = EnsureShadowMaterial(font);
        var heart = PixelArt.HeartIcon();
        var flower = PixelArt.FlowerIcon();

        // ---- top bar: labelled columns ----
        const float top = -36f;
        const float valueTop = top - 42f;
        float x = SideMargin;
        Label(root, "ScoreLabel", "SCORE", x, top, font, shadow);
        var score = Styled(root, "Score (TMP)", new Vector2(0f, 1f), new Vector2(x, valueTop), new Vector2(360f, 60f), 44f, TextAlignmentOptions.TopLeft, font, shadow);
        score.text = "000000";

        x += 380f;
        Label(root, "StageLabel", "STAGE", x, top, font, shadow);
        var stage = Styled(root, "Stage (TMP)", new Vector2(0f, 1f), new Vector2(x, valueTop), new Vector2(200f, 60f), 44f, TextAlignmentOptions.TopLeft, font, shadow);
        stage.text = "1";

        x += 260f;
        Label(root, "LivesLabel", "LIVES", x, top, font, shadow);
        var livesIcon = Icon(root, "LivesIcon", heart, x, valueTop + 2f, 48f);
        var lives = Styled(root, "Lives (TMP)", new Vector2(0f, 1f), new Vector2(x + 56f, valueTop), new Vector2(200f, 60f), 44f, TextAlignmentOptions.TopLeft, font, shadow);
        lives.text = "x3";

        x += 260f;
        Label(root, "PowerLabel", "POWER", x, top, font, shadow);
        var power = Icon(root, "PowerIcon", flower, x + 36f, valueTop + 2f, 48f);
        var powerEmpty = Styled(root, "PowerEmpty (TMP)", new Vector2(0f, 1f), new Vector2(x + 36f, valueTop), new Vector2(120f, 60f), 44f, TextAlignmentOptions.TopLeft, font, shadow);
        powerEmpty.text = "-";

        var connection = Styled(root, "Connection (TMP)", new Vector2(1f, 1f), new Vector2(-80f, top), new Vector2(600f, 40f), 22f, TextAlignmentOptions.TopRight, font, shadow);

        // ---- centre ----
        var banner = Styled(root, "Banner (TMP)", new Vector2(0.5f, 1f), new Vector2(0f, -150f), new Vector2(2000f, 60f), 30f, TextAlignmentOptions.Top, font, shadow);
        var title = Styled(root, "Title (TMP)", new Vector2(0.5f, 0.5f), new Vector2(0f, 40f), new Vector2(2000f, 140f), 96f, TextAlignmentOptions.Center, font, shadow);
        title.text = "";
        var feedback = Styled(root, "Feedback (TMP)", new Vector2(0.5f, 0.5f), new Vector2(0f, -110f), new Vector2(2000f, 80f), 40f, TextAlignmentOptions.Center, font, shadow);
        feedback.text = "";

        // ---- damage flash: full screen, behind the text ----
        var flashGO = EnsureChild(root, "DamageFlash");
        var flashRect = (RectTransform)flashGO.transform;
        flashRect.anchorMin = Vector2.zero;
        flashRect.anchorMax = Vector2.one;
        flashRect.offsetMin = flashRect.offsetMax = Vector2.zero;
        var flash = flashGO.GetComponent<Image>();
        if (flash == null) flash = flashGO.AddComponent<Image>();
        flash.color = new Color(1f, 0.1f, 0.05f, 0f);
        flash.raycastTarget = false;
        flash.enabled = false;
        flashGO.transform.SetAsFirstSibling();

        // ---- game over ----
        var panel = EnsureChild(root, "GameOver");
        var panelRect = (RectTransform)panel.transform;
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = panelRect.offsetMax = Vector2.zero;
        var dim = panel.GetComponent<Image>();
        if (dim == null) dim = panel.AddComponent<Image>();
        dim.color = new Color(0f, 0f, 0f, 0.7f);
        dim.raycastTarget = false;
        var overTitle = Styled(panel.transform, "GameOverTitle (TMP)", new Vector2(0.5f, 0.5f), new Vector2(0f, 130f), new Vector2(2000f, 140f), 110f, TextAlignmentOptions.Center, font, shadow);
        overTitle.text = "GAME OVER";
        overTitle.color = new Color(1f, 0.3f, 0.25f);
        var overStats = Styled(panel.transform, "GameOverStats (TMP)", new Vector2(0.5f, 0.5f), new Vector2(0f, -20f), new Vector2(2000f, 60f), 40f, TextAlignmentOptions.Center, font, shadow);
        var overPrompt = Styled(panel.transform, "GameOverPrompt (TMP)", new Vector2(0.5f, 0.5f), new Vector2(0f, -150f), new Vector2(2000f, 60f), 34f, TextAlignmentOptions.Center, font, shadow);
        overPrompt.text = "JUMP TO PLAY AGAIN";
        overPrompt.color = new Color(1f, 0.85f, 0.2f);
        panel.transform.SetAsLastSibling();
        panel.SetActive(false);

        // LiDAR debug readout (if LidarSetup created it): bottom-left, small, default font.
        var lidar = root.Find(LidarHudName);
        if (lidar != null)
            EnsureText(root, LidarHudName, new Vector2(0f, 0f), new Vector2(SideMargin, 40f), new Vector2(1200f, 160f), 28f, TextAlignmentOptions.BottomLeft);

        var hud = canvas.GetComponent<HUDController>();
        if (hud == null) hud = canvas.gameObject.AddComponent<HUDController>();
        var so = new SerializedObject(hud);
        so.FindProperty("sessionManager").objectReferenceValue = Object.FindAnyObjectByType<ARSessionManager>();
        so.FindProperty("levelGenerator").objectReferenceValue = Object.FindAnyObjectByType<LevelGenerator>();
        so.FindProperty("gameState").objectReferenceValue = Object.FindAnyObjectByType<GameStateManager>();
        so.FindProperty("eventResolver").objectReferenceValue = Object.FindAnyObjectByType<EventResolver>();
        so.FindProperty("networkClient").objectReferenceValue = Object.FindAnyObjectByType<NetworkClient>();
        so.FindProperty("scoreText").objectReferenceValue = score;
        so.FindProperty("stageText").objectReferenceValue = stage;
        so.FindProperty("livesText").objectReferenceValue = lives;
        so.FindProperty("livesIcon").objectReferenceValue = livesIcon;
        so.FindProperty("damageFlash").objectReferenceValue = flash;
        so.FindProperty("powerIcon").objectReferenceValue = power;
        so.FindProperty("powerEmptyText").objectReferenceValue = powerEmpty;
        so.FindProperty("connectionText").objectReferenceValue = connection;
        so.FindProperty("bannerText").objectReferenceValue = banner;
        so.FindProperty("feedbackText").objectReferenceValue = feedback;
        so.FindProperty("titleText").objectReferenceValue = title;
        so.FindProperty("gameOverPanel").objectReferenceValue = panel;
        so.FindProperty("gameOverStatsText").objectReferenceValue = overStats;
        so.FindProperty("gameOverPromptText").objectReferenceValue = overPrompt;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[HUDSetup] Done. Styled HUD (Press Start 2P) with top bar, banner, title, feedback and game-over screen.");
    }

    // ---- font ----

    /// <summary>The pixel font as a TMP font asset (atlas and material saved inside it) with ASCII baked in and the atlas frozen; the default font is the fallback for symbols it lacks (●).</summary>
    private static TMP_FontAsset EnsureFont()
    {
        var fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (fontAsset == null)
        {
            var ttf = AssetDatabase.LoadAssetAtPath<Font>(FontFile);
            if (ttf == null) throw new System.Exception($"[HUDSetup] Missing {FontFile}.");
            fontAsset = TMP_FontAsset.CreateFontAsset(ttf, 48, 6, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic);
            fontAsset.name = "PressStart2P SDF";
            AssetDatabase.CreateAsset(fontAsset, FontAssetPath);
            fontAsset.atlasTextures[0].name = "PressStart2P Atlas";
            AssetDatabase.AddObjectToAsset(fontAsset.atlasTextures[0], fontAsset);
            fontAsset.material.name = "PressStart2P Material";
            AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
            AssetDatabase.SaveAssets(); // persist the atlas and material sub-assets before anything else can fail
        }
        // Bake every character the HUD uses, then freeze the atlas: a dynamic atlas being
        // rewritten at runtime tripped Unity's GPU-allocation asserts in Play Mode.
        fontAsset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
        fontAsset.TryAddCharacters(" !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~²");
        var fallback = TMP_Settings.defaultFontAsset;
        fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;
        fontAsset.fallbackFontAssetTable ??= new System.Collections.Generic.List<TMP_FontAsset>();
        if (fallback != null && !fontAsset.fallbackFontAssetTable.Contains(fallback))
            fontAsset.fallbackFontAssetTable.Add(fallback);
        EditorUtility.SetDirty(fontAsset);
        return fontAsset;
    }

    /// <summary>A material preset for the pixel font: hard drop shadow down-right plus a thin dark outline.</summary>
    private static Material EnsureShadowMaterial(TMP_FontAsset font)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(ShadowMaterialPath);
        if (mat == null)
        {
            mat = new Material(font.material) { name = "PressStart2P Shadow" };
            AssetDatabase.CreateAsset(mat, ShadowMaterialPath);
        }
        mat.shader = font.material.shader;
        mat.SetTexture("_MainTex", font.atlasTexture);
        mat.EnableKeyword("UNDERLAY_ON");
        mat.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.9f));
        mat.SetFloat("_UnderlayOffsetX", 0.9f);
        mat.SetFloat("_UnderlayOffsetY", -0.9f);
        mat.SetFloat("_UnderlaySoftness", 0f);
        mat.EnableKeyword("OUTLINE_ON");
        mat.SetColor("_OutlineColor", new Color(0.05f, 0.03f, 0.03f, 1f));
        mat.SetFloat("_OutlineWidth", 0.12f);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // ---- elements ----

    private static void Label(Transform parent, string name, string text, float x, float y, TMP_FontAsset font, Material mat)
    {
        var label = Styled(parent, name, new Vector2(0f, 1f), new Vector2(x, y), new Vector2(360f, 40f), 26f, TextAlignmentOptions.TopLeft, font, mat);
        label.text = text;
        label.color = new Color(1f, 0.92f, 0.75f);
    }

    private static Image Icon(Transform parent, string name, Sprite sprite, float x, float y, float size)
    {
        var go = EnsureChild(parent, name);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(size, size);
        var image = go.GetComponent<Image>();
        if (image == null) image = go.AddComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        image.raycastTarget = false;
        return image;
    }

    private static TextMeshProUGUI Styled(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size, float fontSize, TextAlignmentOptions alignment, TMP_FontAsset font, Material mat)
    {
        var text = EnsureText(parent, name, anchor, position, size, fontSize, alignment);
        text.font = font;
        text.fontSharedMaterial = mat;
        text.fontStyle = FontStyles.Normal;
        return text;
    }

    private static GameObject EnsureChild(Transform parent, string name)
    {
        var existing = parent.Find(name);
        var go = existing != null ? existing.gameObject : new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        go.layer = parent.gameObject.layer;
        return go;
    }

    /// <summary>Finds or creates a TMP text child of <paramref name="parent"/> and lays it out. Anchor = pivot.</summary>
    public static TextMeshProUGUI EnsureText(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size, float fontSize, TextAlignmentOptions alignment)
    {
        var go = EnsureChild(parent, name);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;

        var text = go.GetComponent<TextMeshProUGUI>();
        if (text == null) text = go.AddComponent<TextMeshProUGUI>();
        if (text.font == null) text.font = TMP_Settings.defaultFontAsset;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.richText = true;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.raycastTarget = false;
        return text;
    }
}
