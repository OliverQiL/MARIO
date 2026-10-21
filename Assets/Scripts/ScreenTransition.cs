using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The classic iris wipe between scenes: the view closes to black in a
/// shrinking circle, the scene changes while it's black, and the circle opens
/// again. LevelGenerator uses it between stages and when warping into and out
/// of a pipe's bonus room.
///
/// Lives on the HUD canvas (HUDSetup adds it) and builds its own UI: a
/// black square with a soft round hole (a generated texture) plus four black
/// bars round it, so the black always reaches the screen edges. It's the
/// canvas's first child, so the HUD text (stats, banner) stays readable on
/// top. In headset mode the HUD is part of each eye's image, so both eyes
/// see the same wipe.
///
/// Safety in AR: the player keeps moving through a real room, so the view is
/// never black for long. Opening waits for the new scene to be ready, but
/// never longer than maxHoldSeconds.
/// </summary>
public class ScreenTransition : MonoBehaviour
{
    [SerializeField] private float closeSeconds = 0.45f;
    [SerializeField] private float openSeconds = 0.5f;
    [Tooltip("Shortest time the view stays black, so the change registers.")]
    [SerializeField] private float minHoldSeconds = 0.15f;
    [Tooltip("Longest time the view stays black, even if the next scene isn't ready (the player is moving in a real room).")]
    [SerializeField] private float maxHoldSeconds = 1.2f;

    public static ScreenTransition Instance { get; private set; }
    public bool IsBusy { get; private set; }
    /// <summary>0 = fully open, 1 = fully black.</summary>
    public float Closed { get; private set; }

    private const float BarSize = 20000f;
    private RectTransform _iris;
    private RectTransform _canvasRect;

    /// <summary>
    /// Closes the iris, runs <paramref name="atBlack"/>, waits until
    /// <paramref name="readyToOpen"/> (if given) or maxHoldSeconds, then opens.
    /// Without a live transition (no HUD, edit mode, already running) the
    /// change happens at once, so nothing ever waits on the wipe.
    /// </summary>
    public static void Run(Action atBlack, Func<bool> readyToOpen = null)
    {
        var t = Instance;
        if (t == null || !Application.isPlaying || t.IsBusy || !t.isActiveAndEnabled)
        {
            atBlack?.Invoke();
            return;
        }
        t.StartCoroutine(t.Play(atBlack, readyToOpen));
    }

    private void Awake()
    {
        if (!Application.isPlaying) return;
        Instance = this;
        Build();
        SetClosed(0f);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private IEnumerator Play(Action atBlack, Func<bool> readyToOpen)
    {
        IsBusy = true;
        for (float t = 0f; t < closeSeconds; t += Time.unscaledDeltaTime)
        {
            SetClosed(Ease(t / closeSeconds));
            yield return null;
        }
        SetClosed(1f);

        try { atBlack?.Invoke(); }
        catch (Exception e) { Debug.LogException(e); }

        float held = 0f;
        while (held < maxHoldSeconds && (held < minHoldSeconds || (readyToOpen != null && !readyToOpen())))
        {
            held += Time.unscaledDeltaTime;
            yield return null;
        }

        for (float t = 0f; t < openSeconds; t += Time.unscaledDeltaTime)
        {
            SetClosed(1f - Ease(t / openSeconds));
            yield return null;
        }
        SetClosed(0f);
        IsBusy = false;
    }

    private static float Ease(float t) => Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));

    private void SetClosed(float closed)
    {
        Closed = closed;
        if (_iris == null) return;
        _iris.gameObject.SetActive(closed > 0f);
        var size = _canvasRect != null ? _canvasRect.rect.size : new Vector2(Screen.width, Screen.height);
        // Open: the hole's clear centre (radius 0.45 of the texture) covers the whole screen.
        float open = size.magnitude / 0.9f + 4f;
        float d = Mathf.Lerp(open, 0f, closed);
        _iris.sizeDelta = new Vector2(d, d);
    }

    private void Build()
    {
        _canvasRect = transform as RectTransform;
        var iris = new GameObject("IrisWipe", typeof(RectTransform), typeof(Image));
        _iris = (RectTransform)iris.transform;
        _iris.SetParent(transform, false);
        _iris.SetAsFirstSibling(); // under the HUD text
        _iris.anchorMin = _iris.anchorMax = _iris.pivot = new Vector2(0.5f, 0.5f);
        _iris.anchoredPosition = Vector2.zero;
        var image = iris.GetComponent<Image>();
        image.sprite = HoleSprite();
        image.color = Color.black;
        image.raycastTarget = false;

        // Bars from each edge of the iris out past the screen edges.
        Bar("Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 0f), new Vector2(BarSize, BarSize));
        Bar("Bottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 1f), new Vector2(BarSize, BarSize));
        Bar("Left", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 0.5f), new Vector2(BarSize, 0f));
        Bar("Right", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 0.5f), new Vector2(BarSize, 0f));
    }

    private void Bar(string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 size)
    {
        var bar = new GameObject(name, typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)bar.transform;
        rt.SetParent(_iris, false);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = size;
        var image = bar.GetComponent<Image>();
        image.color = Color.black;
        image.raycastTarget = false;
    }

    /// <summary>White outside a soft-edged circle, clear inside (tinted black by the Image).</summary>
    private static Sprite HoleSprite()
    {
        const int n = 256;
        var tex = new Texture2D(n, n, TextureFormat.Alpha8, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n - 0.5f, dy = (y + 0.5f) / n - 0.5f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                byte a = (byte)(Mathf.Clamp01((r - 0.45f) / 0.05f) * 255f); // clear to 0.45, soft edge to 0.5
                px[y * n + x] = new Color32(255, 255, 255, a);
            }
        tex.SetPixels32(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
    }
}
