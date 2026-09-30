using UnityEngine;
using TMPro;

/// <summary>
/// Development readout for validating the LiDAR pipeline: occlusion state
/// (RenderManager), room-mesh chunk count and placement obstructions
/// (LevelGenerator). Hidden unless Show is ticked (ScanSetup turns it off for
/// play). Polls every Update().
/// </summary>
public class LidarDebugHUD : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private RenderManager renderManager;
    [SerializeField] private LevelGenerator levelGenerator;
    [SerializeField] private TMP_Text statusText;
    [Tooltip("Show the readout (development only).")]
    [SerializeField] private bool show;

    private void Reset()
    {
        // Convenience: auto-find references the first time this component is added.
        if (renderManager == null) renderManager = FindAnyObjectByType<RenderManager>();
        if (levelGenerator == null) levelGenerator = FindAnyObjectByType<LevelGenerator>();
        if (statusText == null) statusText = GetComponentInChildren<TMP_Text>();
    }

    private void Update()
    {
        if (statusText == null) return;
        statusText.enabled = show;
        if (!show) return;

        string occlusion = renderManager == null ? "n/a"
            : !renderManager.IsResolved ? "resolving…"
            : renderManager.OcclusionActive ? "<color=green>active</color>"
            : renderManager.OcclusionSupported ? "<color=yellow>starting…</color>"
            : "<color=orange>unsupported</color>";

        string mesh = "n/a", placement = "";
        if (levelGenerator != null)
        {
            mesh = levelGenerator.IsMeshingActive ? $"{levelGenerator.MeshChunkCount} chunks" : "<color=orange>off</color>";
            placement = $"\nObstructed candidates: {levelGenerator.TotalObstructedCandidates}";
            if (levelGenerator.TotalObstructedCandidates > 0)
                placement += $" (last: {Shorten(levelGenerator.LastObstructionName)})";
            if (levelGenerator.IsWaitingForMesh)
                placement += "\nWaiting for mesh to settle…";
        }

        statusText.text = $"Occlusion: {occlusion}\nMesh: {mesh}{placement}";
    }

    // Mesh chunks are named "Mesh <32-hex trackable id>" — too long for one HUD line.
    private static string Shorten(string name) => name.Length > 16 ? name.Substring(0, 16) + "…" : name;
}
