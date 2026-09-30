using UnityEngine;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Look of a detected plane during the room scan (on the plane prefab, next to
/// ARPlaneMeshVisualizer). Only floor, as ARSessionManager.IsFloorPlane
/// decides, is shown: a gold pixel grid with a soft pulse, so the player sees
/// which floor has counted. Tables, beds and walls stay invisible. Once the
/// scan completes, everything fades out so stages stand on the real floor.
///
/// Visibility is faded through the colour's alpha (a MaterialPropertyBlock):
/// ARPlaneMeshVisualizer switches the renderers on and off itself for tracking
/// state, so this doesn't touch renderer.enabled.
/// </summary>
[RequireComponent(typeof(ARPlane))]
public class ScanPlaneVisual : MonoBehaviour
{
    [SerializeField] private Color gridColor = new(1f, 0.85f, 0.35f, 1f);
    [SerializeField] private Color edgeColor = new(1f, 0.95f, 0.7f, 0.9f);
    [SerializeField] private float fadeSeconds = 0.6f;
    [SerializeField] private float pulseSpeed = 2.5f;
    [Range(0f, 1f)] [SerializeField] private float pulseDepth = 0.25f;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static ARSessionManager s_Session;

    private ARPlane _plane;
    private MeshRenderer _renderer;
    private LineRenderer _edge;
    private MaterialPropertyBlock _block;
    private float _alpha;

    private void Awake()
    {
        _plane = GetComponent<ARPlane>();
        _renderer = GetComponent<MeshRenderer>();
        _edge = GetComponent<LineRenderer>();
        _block = new MaterialPropertyBlock();
        Apply(0f);
    }

    private void Update()
    {
        if (s_Session == null) s_Session = FindAnyObjectByType<ARSessionManager>();
        bool show = s_Session != null && !s_Session.IsScanComplete && s_Session.IsFloorPlane(_plane);
        _alpha = Mathf.MoveTowards(_alpha, show ? 1f : 0f, Time.deltaTime / Mathf.Max(0.01f, fadeSeconds));
        Apply(_alpha);
    }

    private void Apply(float alpha)
    {
        float pulse = 1f - pulseDepth * (0.5f + 0.5f * Mathf.Sin(Time.time * pulseSpeed));
        if (_renderer != null)
        {
            _renderer.GetPropertyBlock(_block);
            _block.SetColor(BaseColorId, new Color(gridColor.r, gridColor.g, gridColor.b, gridColor.a * alpha * pulse));
            _renderer.SetPropertyBlock(_block);
        }
        if (_edge != null)
        {
            var c = new Color(edgeColor.r, edgeColor.g, edgeColor.b, edgeColor.a * alpha);
            _edge.startColor = _edge.endColor = c;
        }
    }
}
