using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

/// <summary>
/// Side-by-side view for a phone VR viewer (the phone worn in a head mount
/// with two magnifying lenses). The phone has one rear camera, so both eyes
/// get the same view (no true stereo depth): the AR camera renders the normal
/// full-screen view (camera image, stage, HUD) into a texture of the screen's
/// size, so AR Foundation's projection and camera-image mapping stay exactly
/// as usual, and each half of the screen shows it through the
/// MARIO/HeadsetLens shader, pre-distorted so the lenses straighten it.
///
/// Scale matters most for comfort: the lenses spread each half-screen over
/// roughly 90° of vision, while the camera sees roughly 60°. Filling the lens
/// magnifies the world (the first version showed the middle half of the view:
/// about 3×, which was disorienting). So each eye shows the whole view, in
/// proportion, at eyeImageScale of its half: a window near 1:1 scale.
///
/// The HUD sits in a slightly inset safe frame. The view rebuilds when the
/// screen size changes (rotation).
///
/// Mode: Auto = on on a device, off in the Editor. A two-finger tap toggles
/// it before the phone goes in the headset (setup only; gameplay stays touch-free).
///
/// Live tuning while wearing it: the mock gateway's --keys mode sends DEV_TUNE
/// (= / - window size, ] / [ image separation, 0 reset); Tune applies it,
/// rebuilds the eyes, remembers it on the phone (PlayerPrefs) and reports it
/// on the HUD (OnTuned).
/// </summary>
public class HeadsetView : MonoBehaviour
{
    public enum Mode { Auto, On, Off }

    [SerializeField] private Mode mode = Mode.Auto;
    [SerializeField] private Camera arCamera;
    [Tooltip("The HUD canvas; moved into the safe frame in headset mode.")]
    [SerializeField] private Canvas hudCanvas;
    [SerializeField] private Material lensMaterial;

    [Header("Lenses (tune to the viewer)")]
    [Tooltip("Barrel pre-distortion coefficients; 0 = none.")]
    [SerializeField] private float k1 = 0.15f;
    [SerializeField] private float k2 = 0.05f;
    [Tooltip(">1 shows less of the view, larger.")]
    [SerializeField] private float zoom = 1.0f;
    [Tooltip("Distance between the two eye images' centres as a fraction of screen width (0.5 = the centres of each half).")]
    [SerializeField] private float lensSeparation = 0.5f;
    [Tooltip("How much of the full-screen view's width each eye shows (centred). 1 = all of it, at true proportions.")]
    [Range(0.3f, 1f)] [SerializeField] private float cropWidth = 1f;
    [Tooltip("Each eye image's width as a fraction of its half of the screen. Smaller = closer to real-world scale (less magnified, more comfortable); larger = more immersive.")]
    [Range(0.3f, 1f)] [SerializeField] private float eyeImageScale = 0.85f;

    [Header("HUD")]
    [Tooltip("HUD scale inside the safe frame, on the screen the HUD was laid out for (iPhone 17 landscape, 2622 px wide).")]
    [SerializeField] private float hudScale = 0.9f;
    [SerializeField] private float hudDesignWidth = 2622f;
    [Tooltip("The safe frame, as a fraction of the full-screen view (centred).")]
    [SerializeField] private Vector2 safeFrame = new(0.92f, 0.9f);

    public bool IsOn { get; private set; }

    /// <summary>Raised after a live tune, with a short description for the HUD.</summary>
    public static event System.Action<string> OnTuned;

    private const string ScalePref = "HeadsetView.eyeImageScale", SeparationPref = "HeadsetView.lensSeparation";
    private float _defaultScale, _defaultSeparation;

    private RenderTexture _view;
    private GameObject _eyesRoot;
    private Camera _screenCamera;
    private RectTransform _hudFrame;
    private RenderMode _hudRenderMode;
    private float _hudPlaneDistance;
    private Vector2Int _builtFor;

    private void Start()
    {
        if (arCamera == null) arCamera = Camera.main;
        _defaultScale = eyeImageScale;
        _defaultSeparation = lensSeparation;
        eyeImageScale = PlayerPrefs.GetFloat(ScalePref, eyeImageScale);
        lensSeparation = PlayerPrefs.GetFloat(SeparationPref, lensSeparation);
        bool on = mode == Mode.On || (mode == Mode.Auto && !Application.isEditor);
        if (on) TurnOn();
    }

    private void Update()
    {
        // Rotation (or any resize): rebuild for the new screen.
        if (IsOn && (Screen.width != _builtFor.x || Screen.height != _builtFor.y))
        {
            TurnOff();
            TurnOn();
        }

        // Setup toggle: a two-finger tap (before the phone goes in the headset).
        var touch = Touchscreen.current;
        if (touch == null) return;
        int began = 0, down = 0;
        foreach (var t in touch.touches)
        {
            if (!t.press.isPressed) continue;
            down++;
            if (t.press.wasPressedThisFrame) began++;
        }
        if (down == 2 && began > 0)
        {
            if (IsOn) TurnOff(); else TurnOn();
        }
    }

    public void TurnOn()
    {
        if (IsOn || arCamera == null) return;
        IsOn = true;
        _builtFor = new Vector2Int(Screen.width, Screen.height);

        // 1. The AR view renders off-screen at the screen's own size and aspect.
        _view = new RenderTexture(Screen.width, Screen.height, 24) { name = "HeadsetView" };
        arCamera.targetTexture = _view;

        // 2. The HUD renders into that view, inside a centred safe frame.
        if (hudCanvas != null)
        {
            _hudRenderMode = hudCanvas.renderMode;
            _hudPlaneDistance = hudCanvas.planeDistance;
            hudCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            hudCanvas.worldCamera = arCamera;
            hudCanvas.planeDistance = Mathf.Max(arCamera.nearClipPlane * 1.5f, 0.15f);
            WrapHudInFrame();
        }

        // 3. Something must draw to the screen itself: a black camera that renders nothing.
        var camGO = new GameObject("HeadsetScreenCamera");
        _screenCamera = camGO.AddComponent<Camera>();
        _screenCamera.clearFlags = CameraClearFlags.SolidColor;
        _screenCamera.backgroundColor = Color.black;
        _screenCamera.cullingMask = 0;
        _screenCamera.depth = arCamera.depth + 10;

        // 4. Two eye images, one per half of the screen.
        _eyesRoot = new GameObject("HeadsetEyes", typeof(Canvas));
        var canvas = _eyesRoot.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;
        // Each eye: the (cropped) view in its true proportions, eyeImageScale of the half's width.
        float viewAspect = (float)Screen.width / Screen.height;
        float imageAspect = viewAspect * cropWidth;
        float imageW = Screen.width * 0.5f * eyeImageScale;
        float imageH = Mathf.Min(Screen.height, imageW / imageAspect);
        imageW = imageH * imageAspect;
        foreach (int side in new[] { -1, 1 })
        {
            var eye = new GameObject(side < 0 ? "LeftEye" : "RightEye", typeof(RectTransform), typeof(RawImage));
            eye.transform.SetParent(_eyesRoot.transform, false);
            var rect = (RectTransform)eye.transform;
            float centre = 0.5f + side * lensSeparation * 0.5f;
            rect.anchorMin = rect.anchorMax = new Vector2(centre, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(imageW, imageH);
            var image = eye.GetComponent<RawImage>();
            image.texture = _view;
            image.raycastTarget = false;
            var mat = lensMaterial != null ? new Material(lensMaterial) : new Material(Shader.Find("MARIO/HeadsetLens"));
            mat.SetFloat("_CropX", 0.5f - cropWidth * 0.5f);
            mat.SetFloat("_CropW", cropWidth);
            mat.SetFloat("_K1", k1);
            mat.SetFloat("_K2", k2);
            mat.SetFloat("_Zoom", zoom);
            mat.SetFloat("_EyeAspect", imageAspect);
            image.material = mat;
        }
        Debug.Log($"[HeadsetView] On: {Screen.width}x{Screen.height}, each eye {imageW:F0}x{imageH:F0} px (crop {cropWidth:F2}), k1 {k1}, k2 {k2}, lens separation {lensSeparation:F2}.");
    }

    /// <summary>Live adjustment (DEV_TUNE): "eyeImageScale" or "lensSeparation" by delta, or "reset" both. Remembered on the phone.</summary>
    public void Tune(string param, float delta)
    {
        switch (param)
        {
            case "eyeImageScale": eyeImageScale = Mathf.Clamp(eyeImageScale + delta, 0.4f, 1f); break;
            case "lensSeparation": lensSeparation = Mathf.Clamp(lensSeparation + delta, 0.35f, 0.65f); break;
            case "reset": eyeImageScale = _defaultScale; lensSeparation = _defaultSeparation; break;
            default: Debug.LogWarning($"[HeadsetView] Unknown tune '{param}'."); return;
        }
        PlayerPrefs.SetFloat(ScalePref, eyeImageScale);
        PlayerPrefs.SetFloat(SeparationPref, lensSeparation);
        PlayerPrefs.Save();
        if (IsOn) { TurnOff(); TurnOn(); }
        string text = $"Window {eyeImageScale:F2}  Gap {lensSeparation:F2}";
        Debug.Log($"[HeadsetView] Tuned: {text}");
        OnTuned?.Invoke(text);
    }

    public void TurnOff()
    {
        if (!IsOn) return;
        IsOn = false;
        arCamera.targetTexture = null;
        if (hudCanvas != null)
        {
            UnwrapHudFrame();
            hudCanvas.renderMode = _hudRenderMode;
            hudCanvas.planeDistance = _hudPlaneDistance;
        }
        if (_screenCamera != null) Destroy(_screenCamera.gameObject);
        if (_eyesRoot != null) Destroy(_eyesRoot);
        if (_view != null) { _view.Release(); Destroy(_view); }
        Debug.Log("[HeadsetView] Off.");
    }

    /// <summary>Moves the HUD's elements into a centred frame, drawn at hudScale, covering safeFrame of the view.</summary>
    private void WrapHudInFrame()
    {
        var root = (RectTransform)hudCanvas.transform;
        var frameGO = new GameObject("HeadsetSafeFrame", typeof(RectTransform));
        _hudFrame = (RectTransform)frameGO.transform;
        _hudFrame.SetParent(root, false);
        _hudFrame.anchorMin = _hudFrame.anchorMax = _hudFrame.pivot = new Vector2(0.5f, 0.5f);
        float scale = hudScale * Screen.width / hudDesignWidth; // the HUD is laid out in iPhone 17 pixels
        _hudFrame.sizeDelta = new Vector2(Screen.width * safeFrame.x, Screen.height * safeFrame.y) / Mathf.Max(0.01f, scale);
        _hudFrame.localScale = Vector3.one * scale;
        // Keep the children's order: it's their draw order (damage flash first, game-over screen last).
        var children = new System.Collections.Generic.List<Transform>();
        foreach (Transform child in root) if (child != _hudFrame) children.Add(child);
        foreach (var child in children) child.SetParent(_hudFrame, false);
    }

    private void UnwrapHudFrame()
    {
        if (_hudFrame == null) return;
        var root = hudCanvas.transform;
        var children = new System.Collections.Generic.List<Transform>();
        foreach (Transform child in _hudFrame) children.Add(child);
        foreach (var child in children) child.SetParent(root, false);
        Destroy(_hudFrame.gameObject);
        _hudFrame = null;
    }
}
