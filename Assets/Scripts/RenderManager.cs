using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Occlusion configuration and camera background (design report Step 4's
/// first half). Tracking-state polling is NOT this module's job — that
/// belongs to ARSessionManager.
///
/// Must live on the AR Camera GameObject (the one with ARCameraManager /
/// ARCameraBackground), not on XR Origin.
///
/// Environment-depth support is polled every frame until the descriptor
/// resolves from Supported.Unknown to a definite answer — this can take a
/// few frames after session start, so a one-shot Awake() check is wrong.
/// On LiDAR devices, requests best-quality smoothed environment depth so
/// real-world geometry occludes virtual content. On non-LiDAR devices,
/// logs a warning and disables the occlusion manager.
/// </summary>
[RequireComponent(typeof(AROcclusionManager))]
public class RenderManager : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Auto-filled from this GameObject if left empty.")]
    [SerializeField] private AROcclusionManager occlusionManager;

    [Header("Occlusion Settings")]
    [SerializeField] private EnvironmentDepthMode depthMode = EnvironmentDepthMode.Best;
    [Tooltip("Temporal smoothing reduces depth flicker at object edges at a small latency cost.")]
    [SerializeField] private bool temporalSmoothing = true;

    [Header("Events")]
    [Tooltip("Fired once, when environment-depth support resolves. Argument: true if occlusion is active.")]
    public UnityEvent<bool> OnOcclusionStateResolved;

    public bool IsResolved { get; private set; }
    public bool OcclusionSupported { get; private set; }
    public bool OcclusionActive => OcclusionSupported && occlusionManager.enabled
        && occlusionManager.currentEnvironmentDepthMode != EnvironmentDepthMode.Disabled;

    private void Awake()
    {
        if (occlusionManager == null)
        {
            occlusionManager = GetComponent<AROcclusionManager>();
        }

        // Human segmentation is not used — the player's own body is never in view
        // in the headset, and it competes with environment depth for GPU time.
        occlusionManager.requestedHumanStencilMode = HumanSegmentationStencilMode.Disabled;
        occlusionManager.requestedHumanDepthMode = HumanSegmentationDepthMode.Disabled;
    }

    private void Update()
    {
        if (IsResolved) return;

        var descriptor = occlusionManager.descriptor;
        if (descriptor == null) return; // subsystem not created yet

        var support = descriptor.environmentDepthImageSupported;
        if (support == Supported.Unknown) return; // keep polling

        IsResolved = true;
        OcclusionSupported = support == Supported.Supported;

        if (OcclusionSupported)
        {
            occlusionManager.requestedEnvironmentDepthMode = depthMode;
            occlusionManager.environmentDepthTemporalSmoothingRequested = temporalSmoothing;
            occlusionManager.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.PreferEnvironmentOcclusion;
            Debug.Log($"[RenderManager] Environment depth supported — occlusion enabled ({depthMode}, smoothing {temporalSmoothing}).");
        }
        else
        {
            occlusionManager.enabled = false;
            Debug.LogWarning("[RenderManager] Environment depth unsupported on this device (no LiDAR) — occlusion disabled.");
        }

        OnOcclusionStateResolved?.Invoke(OcclusionSupported);
    }
}
