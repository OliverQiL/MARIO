using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// One-click (or headless) scene/project wiring for the LiDAR pipeline.
/// Safe to run repeatedly — every step checks before applying.
///
/// Menu:     Tools ▸ MARIO ▸ Configure LiDAR
/// Headless: Unity -batchmode -quit -projectPath . -executeMethod LidarSetup.Run
/// </summary>
public static class LidarSetup
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string MeshPrefabPath = "Assets/AR Mesh.prefab";
    private const string DebugMeshPrefabPath = "Assets/AR Mesh (Debug).prefab";
    private const string DebugMaterialPath = "Assets/Materials/ARMeshDebug.mat";
    private const string MeshLayerName = "ARMesh";
    private const string AppleTeamId = "U5T7NBV8MB";

    [MenuItem("Tools/MARIO/Configure LiDAR")]
    public static void Run()
    {
        int meshLayer = EnsureLayer(MeshLayerName);
        ConfigureMeshPrefab(meshLayer);
        var debugPrefab = EnsureDebugMeshPrefab();

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var cameraManager = Object.FindAnyObjectByType<ARCameraManager>();
        if (cameraManager == null) throw new System.Exception("[LidarSetup] No ARCameraManager in scene.");
        var cameraGO = cameraManager.gameObject;
        var occlusion = GetOrAdd<AROcclusionManager>(cameraGO);
        var renderManager = GetOrAdd<RenderManager>(cameraGO);

        var meshManager = Object.FindAnyObjectByType<ARMeshManager>();
        if (meshManager == null) throw new System.Exception("[LidarSetup] No ARMeshManager in scene.");
        SetMeshPrefab(meshManager, debugPrefab);

        var levelGenerator = Object.FindAnyObjectByType<LevelGenerator>();
        if (levelGenerator == null) throw new System.Exception("[LidarSetup] No LevelGenerator in scene.");
        var so = new SerializedObject(levelGenerator);
        so.FindProperty("obstructionLayerMask").intValue = 1 << meshLayer;
        so.FindProperty("meshManager").objectReferenceValue = meshManager;
        so.ApplyModifiedPropertiesWithoutUndo();

        EnsureLidarHud(renderManager, levelGenerator);

        PlayerSettings.iOS.appleDeveloperTeamID = AppleTeamId;
        PlayerSettings.iOS.appleEnableAutomaticSigning = true;

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log($"[LidarSetup] Done. Occlusion on '{cameraGO.name}' ({occlusion.GetType().Name}), mesh layer {meshLayer}, debug mesh view ON.");
    }

    [MenuItem("Tools/MARIO/Mesh Debug View/On")]
    public static void EnableMeshDebug() => SetMeshDebug(true);

    [MenuItem("Tools/MARIO/Mesh Debug View/Off")]
    public static void DisableMeshDebug() => SetMeshDebug(false);

    private static void SetMeshDebug(bool on)
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var meshManager = Object.FindAnyObjectByType<ARMeshManager>();
        var prefab = on ? EnsureDebugMeshPrefab() : AssetDatabase.LoadAssetAtPath<GameObject>(MeshPrefabPath);
        SetMeshPrefab(meshManager, prefab);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[LidarSetup] Mesh debug view {(on ? "ON" : "OFF")}.");
    }

    // ---- helpers ----

    private static T GetOrAdd<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    private static void SetMeshPrefab(ARMeshManager meshManager, GameObject prefab)
    {
        var so = new SerializedObject(meshManager);
        so.FindProperty("m_MeshPrefab").objectReferenceValue = prefab.GetComponent<MeshFilter>();
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static int EnsureLayer(string name)
    {
        int existing = LayerMask.NameToLayer(name);
        if (existing >= 0) return existing;

        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tagManager.FindProperty("layers");
        for (int i = 6; i < layers.arraySize; i++)
        {
            var element = layers.GetArrayElementAtIndex(i);
            if (string.IsNullOrEmpty(element.stringValue))
            {
                element.stringValue = name;
                tagManager.ApplyModifiedPropertiesWithoutUndo();
                return i;
            }
        }
        throw new System.Exception("[LidarSetup] No free user layer for " + name);
    }

    private static void ConfigureMeshPrefab(int layer)
    {
        var root = PrefabUtility.LoadPrefabContents(MeshPrefabPath);
        root.layer = layer;
        PrefabUtility.SaveAsPrefabAsset(root, MeshPrefabPath);
        PrefabUtility.UnloadPrefabContents(root);
    }

    private static GameObject EnsureDebugMeshPrefab()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(DebugMaterialPath);
        if (material == null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DebugMaterialPath));
            material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            // Transparent, alpha-blended, no depth write.
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            material.SetColor("_BaseColor", new Color(0f, 0.8f, 1f, 0.25f));
            AssetDatabase.CreateAsset(material, DebugMaterialPath);
        }

        // Always rebuilt from the base prefab so it tracks its layer/collider settings.
        var root = PrefabUtility.LoadPrefabContents(MeshPrefabPath);
        root.name = "AR Mesh (Debug)";
        GetOrAdd<MeshRenderer>(root).sharedMaterial = material;
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, DebugMeshPrefabPath);
        PrefabUtility.UnloadPrefabContents(root);
        return prefab;
    }

    private static void EnsureLidarHud(RenderManager renderManager, LevelGenerator levelGenerator)
    {
        var canvas = Object.FindAnyObjectByType<Canvas>();
        if (canvas == null) throw new System.Exception("[LidarSetup] No Canvas in scene.");

        // Bottom-left, small, out of the HUD's way (same layout HUDSetup applies).
        var text = HUDSetup.EnsureText(canvas.transform, HUDSetup.LidarHudName, new Vector2(0f, 0f),
            new Vector2(HUDSetup.SideMargin, 40f), new Vector2(1200f, 160f), 28f, TextAlignmentOptions.BottomLeft);
        text.text = "Occlusion: …\nMesh: …";
        var go = text.gameObject;

        var hud = GetOrAdd<LidarDebugHUD>(go);
        var so = new SerializedObject(hud);
        so.FindProperty("renderManager").objectReferenceValue = renderManager;
        so.FindProperty("levelGenerator").objectReferenceValue = levelGenerator;
        so.FindProperty("statusText").objectReferenceValue = text;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
