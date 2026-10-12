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

        x += 340f;
        Label(root, "CoinsLabel", "COINS", x, top, font, shadow);
        Icon(root, "CoinsIcon", PixelArt.CoinIcon(), x, valueTop + 2f, 48f);
        var coins = Styled(root, "Coins (TMP)", new Vector2(0f, 1f), new Vector2(x + 56f, valueTop), new Vector2(200f, 60f), 44f, TextAlignmentOptions.TopLeft, font, shadow);
        coins.text = "x00";

        x += 260f;
        Label(root, "StageLabel", "STAGE", x, top, font, shadow);
        var stage = Styled(root, "Stage (TMP)", new Vector2(0f, 1f), new Vector2(x, valueTop), new Vector2(240f, 60f), 44f, TextAlignmentOptions.TopLeft, font, shadow);
        stage.text = "1-1";

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

        x += 220f;
        Label(root, "HeldLabel", "HELD", x, top, font, shadow);
        var held = Icon(root, "HeldIcon", PixelArt.ShellIcon(), x + 20f, valueTop + 2f, 48f);
        var heldEmpty = Styled(root, "HeldEmpty (TMP)", new Vector2(0f, 1f), new Vector2(x + 20f, valueTop), new Vector2(120f, 60f), 44f, TextAlignmentOptions.TopLeft, font, shadow);
        heldEmpty.text = "-";

        var connection = Styled(root, "Connection (TMP)", new Vector2(1f, 1f), new Vector2(-80f, top), new Vector2(600f, 40f), 22f, TextAlignmentOptions.TopRight, font, shadow);

        // ---- centre ----
        var banner = Styled(root, "Banner (TMP)", new Vector2(0.5f, 1f), new Vector2(0f, -150f), new Vector2(2000f, 60f), 30f, TextAlignmentOptions.Top, font, shadow);
        var title = Styled(root, "Title (TMP)", new Vector2(0.5f, 0.5f), new Vector2(0f, 40f), new Vector2(2000f, 140f), 96f, TextAlignmentOptions.Center, font, shadow);
        title.text = "";
        var subtitle = Styled(root, "Subtitle (TMP)", new Vector2(0.5f, 0.5f), new Vector2(0f, -95f), new Vector2(2000f, 110f), 44f, TextAlignmentOptions.Top, font, shadow);
        subtitle.text = "";
        subtitle.richText = true;
        subtitle.lineSpacing = 40f;

        // ---- boss bar: top centre, under the banner ----
        var whiteBar = PixelArt.WhitePixel();
        var bossPanel = EnsureChild(root, "BossBar");
        var bossRect = (RectTransform)bossPanel.transform;
        bossRect.anchorMin = bossRect.anchorMax = bossRect.pivot = new Vector2(0.5f, 1f);
        bossRect.anchoredPosition = new Vector2(0f, -215f);
        bossRect.sizeDelta = new Vector2(900f, 90f);
        var bossLabel = Styled(bossPanel.transform, "BossLabel (TMP)", new Vector2(0.5f, 0.5f), new Vector2(0f, 24f), new Vector2(900f, 40f), 28f, TextAlignmentOptions.Center, font, shadow);
        bossLabel.text = "BOWSER";
        bossLabel.color = new Color(1f, 0.45f, 0.2f);
        Bar(bossPanel.transform, "BossBorder", whiteBar, new Vector2(0f, -18f), new Vector2(612f, 36f), new Color(1f, 0.95f, 0.8f));
        Bar(bossPanel.transform, "BossBack", whiteBar, new Vector2(0f, -18f), new Vector2(600f, 24f), new Color(0.08f, 0.06f, 0.05f, 0.85f));
        var bossFill = Bar(bossPanel.transform, "BossFill", whiteBar, new Vector2(0f, -18f), new Vector2(600f, 24f), new Color(0.95f, 0.25f, 0.15f));
        bossFill.type = Image.Type.Filled;
        bossFill.fillMethod = Image.FillMethod.Horizontal;
        bossFill.fillOrigin = (int)Image.OriginHorizontal.Left;
        bossFill.fillAmount = 1f;
        bossPanel.SetActive(false);
        var feedback = Styled(root, "Feedback (TMP)", new Vector2(0.5f, 0.5f), new Vector2(0f, -200f), new Vector2(2000f, 80f), 40f, TextAlignmentOptions.Center, font, shadow);
        feedback.text = "";

        // ---- scan panel: title, progress bar, rotating hint ----
        var white = PixelArt.WhitePixel();
        var scan = EnsureChild(root, "ScanPanel");
        var scanRect = (RectTransform)scan.transform;
        scanRect.anchorMin = scanRect.anchorMax = scanRect.pivot = new Vector2(0.5f, 0.5f);
        scanRect.anchoredPosition = new Vector2(0f, -140f);
        scanRect.sizeDelta = new Vector2(1600f, 320f);
        var scanTitle = Styled(scan.transform, "ScanTitle (TMP)", new Vector2(0.5f, 0.5f), new Vector2(0f, 100f), new Vector2(1600f, 70f), 48f, TextAlignmentOptions.Center, font, shadow);
        scanTitle.text = "SCAN THE FLOOR";
        // The bar's parts share a parent, hidden until the scan starts.
        var barGroup = EnsureChild(scan.transform, "ScanBar");
        var barRect = (RectTransform)barGroup.transform;
        barRect.anchorMin = barRect.anchorMax = barRect.pivot = new Vector2(0.5f, 0.5f);
        barRect.anchoredPosition = Vector2.zero;
        barRect.sizeDelta = new Vector2(1600f, 320f);
        foreach (var old in new[] { "BarBorder", "BarBack", "BarFill", "ScanPercent (TMP)" })
        {
            var t = scan.transform.Find(old);
            if (t != null) Object.DestroyImmediate(t.gameObject); // from before the group existed
        }
        Bar(barGroup.transform, "BarBorder", white, new Vector2(0f, 10f), new Vector2(912f, 56f), new Color(1f, 0.95f, 0.8f));
        Bar(barGroup.transform, "BarBack", white, new Vector2(0f, 10f), new Vector2(900f, 44f), new Color(0.08f, 0.06f, 0.05f, 0.85f));
        var fill = Bar(barGroup.transform, "BarFill", white, new Vector2(0f, 10f), new Vector2(900f, 44f), new Color(1f, 0.78f, 0.12f));
        fill.type = Image.Type.Filled;
        fill.fillMethod = Image.FillMethod.Horizontal;
        fill.fillOrigin = (int)Image.OriginHorizontal.Left;
        fill.fillAmount = 0.35f;
        var percent = Styled(barGroup.transform, "ScanPercent (TMP)", new Vector2(0.5f, 0.5f), new Vector2(0f, 10f), new Vector2(900f, 44f), 26f, TextAlignmentOptions.Center, font, shadow);
        percent.text = "35%";
        var hint = Styled(scan.transform, "ScanHint (TMP)", new Vector2(0.5f, 0.5f), new Vector2(0f, -70f), new Vector2(1600f, 50f), 24f, TextAlignmentOptions.Center, font, shadow);
        hint.text = "SWEEP THE CAMERA SLOWLY OVER THE FLOOR";
        hint.color = new Color(1f, 0.92f, 0.75f);

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
        var overStats = Styled(panel.transform, "GameOverStats (TMP)", new Vector2(0.5f, 0.5f), new Vector2(0f, -25f), new Vector2(2000f, 130f), 40f, TextAlignmentOptions.Center, font, shadow);
        overStats.richText = true;
        overStats.lineSpacing = 50f;
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
        so.FindProperty("coinsText").objectReferenceValue = coins;
        so.FindProperty("livesText").objectReferenceValue = lives;
        so.FindProperty("livesIcon").objectReferenceValue = livesIcon;
        so.FindProperty("damageFlash").objectReferenceValue = flash;
        so.FindProperty("scanPanel").objectReferenceValue = scan;
        so.FindProperty("scanFill").objectReferenceValue = fill;
        so.FindProperty("scanTitleText").objectReferenceValue = scanTitle;
        so.FindProperty("scanBar").objectReferenceValue = barGroup;
        so.FindProperty("scanPercentText").objectReferenceValue = percent;
        so.FindProperty("scanHintText").objectReferenceValue = hint;
        so.FindProperty("powerIcon").objectReferenceValue = power;
        so.FindProperty("powerEmptyText").objectReferenceValue = powerEmpty;
        so.FindProperty("heldIcon").objectReferenceValue = held;
        so.FindProperty("heldEmptyText").objectReferenceValue = heldEmpty;
        so.FindProperty("subtitleText").objectReferenceValue = subtitle;
        so.FindProperty("bossPanel").objectReferenceValue = bossPanel;
        so.FindProperty("bossFill").objectReferenceValue = bossFill;
        so.FindProperty("gameOverTitleText").objectReferenceValue = overTitle;
        so.FindProperty("connectionText").objectReferenceValue = connection;
        so.FindProperty("bannerText").objectReferenceValue = banner;
        so.FindProperty("feedbackText").objectReferenceValue = feedback;
        so.FindProperty("titleText").objectReferenceValue = title;
        so.FindProperty("gameOverPanel").objectReferenceValue = panel;
        so.FindProperty("gameOverStatsText").objectReferenceValue = overStats;
        so.FindProperty("gameOverPromptText").objectReferenceValue = overPrompt;
        so.ApplyModifiedPropertiesWithoutUndo();

        // Headset (phone VR viewer) mode: side-by-side eyes with lens pre-distortion; on by default on a device.
        const string lensMaterialPath = "Assets/Materials/HeadsetLens.mat";
        var lens = AssetDatabase.LoadAssetAtPath<Material>(lensMaterialPath);
        if (lens == null)
        {
            lens = new Material(Shader.Find("MARIO/HeadsetLens"));
            AssetDatabase.CreateAsset(lens, lensMaterialPath);
        }
        var headset = canvas.GetComponent<HeadsetView>();
        if (headset == null) headset = canvas.gameObject.AddComponent<HeadsetView>();
        var hs = new SerializedObject(headset);
        hs.FindProperty("arCamera").objectReferenceValue = Camera.main;
        hs.FindProperty("hudCanvas").objectReferenceValue = canvas;
        hs.FindProperty("lensMaterial").objectReferenceValue = lens;
        // Whole view at near-true scale in each eye (filling the lens magnified the world ~3x and disoriented).
        hs.FindProperty("cropWidth").floatValue = 1f;
        hs.FindProperty("eyeImageScale").floatValue = 0.85f; // 0.75 looked too far away through the VRG Pro (Oliver); tune live with = / -
        hs.FindProperty("k1").floatValue = 0.15f;
        hs.FindProperty("k2").floatValue = 0.05f;
        hs.FindProperty("hudScale").floatValue = 0.9f;
        hs.FindProperty("safeFrame").vector2Value = new Vector2(0.92f, 0.9f);
        hs.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[HUDSetup] Done. Styled HUD (Press Start 2P) with top bar (incl. HELD), banner, boss bar, title + intro card, feedback and end screen.");
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

    private static Image Bar(Transform parent, string name, Sprite sprite, Vector2 position, Vector2 size, Color color)
    {
        var go = EnsureChild(parent, name);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        var image = go.GetComponent<Image>();
        if (image == null) image = go.AddComponent<Image>();
        image.sprite = sprite;
        image.color = color;
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
