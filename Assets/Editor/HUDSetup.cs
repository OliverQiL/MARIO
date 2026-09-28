using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds the HUDController layout on the scene's Canvas. Idempotent.
/// Replaces the old ScanHUD (its text object becomes the centre banner) and
/// moves the LiDAR debug readout to the bottom-left, out of the way.
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
    public const float SideMargin = 180f; // clears the Dynamic Island in landscape
    public const string LidarHudName = "LidarDebug (TMP)";

    [MenuItem("Tools/MARIO/Configure HUD")]
    public static void Run()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var canvas = Object.FindAnyObjectByType<Canvas>();
        if (canvas == null) throw new System.Exception("[HUDSetup] No Canvas in scene.");
        var root = canvas.transform;

        // Retire ScanHUD: its text object becomes the banner.
        foreach (Transform child in root)
        {
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(child.gameObject);
            var scanHud = child.GetComponent("ScanHUD");
            if (scanHud == null) continue;
            Object.DestroyImmediate(scanHud, true);
            child.name = "Banner (TMP)";
        }

        var stats = EnsureText(root, "Stats (TMP)", new Vector2(0f, 1f), new Vector2(SideMargin, -40f), new Vector2(1400f, 80f), 42f, TextAlignmentOptions.TopLeft);
        var connection = EnsureText(root, "Connection (TMP)", new Vector2(1f, 1f), new Vector2(-80f, -40f), new Vector2(700f, 80f), 36f, TextAlignmentOptions.TopRight);
        var banner = EnsureText(root, "Banner (TMP)", new Vector2(0.5f, 1f), new Vector2(0f, -150f), new Vector2(1800f, 80f), 44f, TextAlignmentOptions.Top);
        var feedback = EnsureText(root, "Feedback (TMP)", new Vector2(0.5f, 0.5f), new Vector2(0f, 120f), new Vector2(1800f, 100f), 60f, TextAlignmentOptions.Center);
        feedback.fontStyle = FontStyles.Bold;
        feedback.text = "";

        // LiDAR debug readout (if LidarSetup created it): bottom-left, small.
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
        so.FindProperty("statsText").objectReferenceValue = stats;
        so.FindProperty("connectionText").objectReferenceValue = connection;
        so.FindProperty("bannerText").objectReferenceValue = banner;
        so.FindProperty("feedbackText").objectReferenceValue = feedback;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[HUDSetup] Done. HUDController on Canvas with stats, connection, banner and feedback text.");
    }

    /// <summary>Finds or creates a TMP text child of <paramref name="parent"/> and lays it out. Anchor = pivot.</summary>
    public static TextMeshProUGUI EnsureText(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size, float fontSize, TextAlignmentOptions alignment)
    {
        var existing = parent.Find(name);
        var go = existing != null ? existing.gameObject : new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        go.layer = parent.gameObject.layer;

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
