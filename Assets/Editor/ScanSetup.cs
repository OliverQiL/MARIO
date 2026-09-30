using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Sets up the room-scan look. Idempotent.
/// - Builds "AR Scan Plane" (Assets/Prefabs): ARPlane + ARPlaneMeshVisualizer
///   with a transparent gold pixel-grid material and a thin edge line, driven
///   by ScanPlaneVisual (floor only, fades out when the scan completes), and
///   makes it ARPlaneManager's plane prefab.
/// - Turns the development visuals off: the cyan LiDAR debug mesh (back to
///   the plain "AR Mesh" prefab) and the LiDAR readout text (LidarDebugHUD.show). Tools ▸ MARIO ▸ Mesh Debug View ▸ On,
///   and the Inspector, switch them back on for debugging.
///
/// Menu:     Tools ▸ MARIO ▸ Configure Scan
/// Headless: Unity -batchmode -quit -projectPath . -executeMethod ScanSetup.Run
/// </summary>
public static class ScanSetup
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string PlanePrefabPath = "Assets/Prefabs/AR Scan Plane.prefab";
    private const string GridMaterialPath = "Assets/Materials/ScanGrid.mat";
    private const string EdgeMaterialPath = "Assets/Materials/ScanEdge.mat";
    private const string PlainMeshPrefabPath = "Assets/AR Mesh.prefab";

    [MenuItem("Tools/MARIO/Configure Scan")]
    public static void Run()
    {
        var grid = TransparentMaterial(GridMaterialPath, "Universal Render Pipeline/Unlit");
        grid.SetTexture("_BaseMap", PixelArt.ScanGrid());
        grid.SetTextureScale("_BaseMap", new Vector2(4f, 4f)); // 25 cm squares
        grid.SetColor("_BaseColor", Color.white);
        EditorUtility.SetDirty(grid);
        var edge = TransparentMaterial(EdgeMaterialPath, "Universal Render Pipeline/Particles/Unlit"); // uses the line's vertex colour
        edge.SetColor("_BaseColor", Color.white);
        EditorUtility.SetDirty(edge);

        var planePrefab = BuildPlanePrefab(grid, edge);

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var planeManager = Object.FindAnyObjectByType<ARPlaneManager>();
        var pm = new SerializedObject(planeManager);
        pm.FindProperty("m_PlanePrefab").objectReferenceValue = planePrefab;
        pm.ApplyModifiedPropertiesWithoutUndo();

        var meshManager = Object.FindAnyObjectByType<ARMeshManager>();
        var plainMesh = AssetDatabase.LoadAssetAtPath<GameObject>(PlainMeshPrefabPath);
        if (meshManager != null && plainMesh != null)
        {
            var mm = new SerializedObject(meshManager);
            mm.FindProperty("m_MeshPrefab").objectReferenceValue = plainMesh.GetComponent<MeshFilter>();
            mm.ApplyModifiedPropertiesWithoutUndo();
        }

        var lidarHud = Object.FindAnyObjectByType<LidarDebugHUD>();
        if (lidarHud != null)
        {
            var lh = new SerializedObject(lidarHud);
            lh.FindProperty("show").boolValue = false;
            lh.ApplyModifiedPropertiesWithoutUndo();
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[ScanSetup] Done. Scan plane prefab set; debug mesh and LiDAR readout off.");
    }

    private static GameObject BuildPlanePrefab(Material grid, Material edge)
    {
        var root = new GameObject("AR Scan Plane");
        root.AddComponent<ARPlane>();
        root.AddComponent<MeshFilter>();
        var renderer = root.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = grid;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        root.AddComponent<MeshCollider>();
        var line = root.AddComponent<LineRenderer>();
        line.sharedMaterial = edge;
        line.widthMultiplier = 0.008f;
        line.useWorldSpace = false;
        line.loop = true;
        line.numCornerVertices = 2;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        root.AddComponent<ARPlaneMeshVisualizer>();
        root.AddComponent<ScanPlaneVisual>();
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, PlanePrefabPath);
        Object.DestroyImmediate(root);
        return prefab;
    }

    /// <summary>A URP material set up for alpha blending (transparent queue, no depth write).</summary>
    private static Material TransparentMaterial(string path, string shaderName)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find(shaderName));
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = Shader.Find(shaderName);
        mat.SetFloat("_Surface", 1f);
        mat.SetFloat("_Blend", 0f);
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_ZWrite", 0f);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)RenderQueue.Transparent;
        return mat;
    }
}
