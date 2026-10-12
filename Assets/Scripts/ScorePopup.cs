using TMPro;
using UnityEngine;

/// <summary>
/// A "+100" that pops up in the world where the points were scored, floats
/// up, faces the camera and fades. Spawned by the HUD (HUDController) from
/// code; uses the HUD's pixel font.
/// </summary>
public class ScorePopup : MonoBehaviour
{
    private const float Lifetime = 0.9f;
    private const float RiseSpeed = 0.35f;

    private TextMeshPro _text;
    private Color _color;
    private float _age;

    public static ScorePopup Spawn(string message, Vector3 position, Color color, TMP_FontAsset font, Material fontMaterial)
    {
        var go = new GameObject("ScorePopup");
        go.transform.position = position;
        var popup = go.AddComponent<ScorePopup>();
        var text = go.AddComponent<TextMeshPro>();
        if (font != null) text.font = font;
        if (fontMaterial != null) text.fontSharedMaterial = fontMaterial;
        text.text = message;
        text.fontSize = 0.7f; // about 7 cm tall
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.color = color;
        popup._text = text;
        popup._color = color;
        popup.FaceCamera();
        return popup;
    }

    private void Update()
    {
        _age += Time.deltaTime;
        transform.position += Vector3.up * (RiseSpeed * Time.deltaTime);
        float pop = Mathf.Clamp01(_age / 0.12f);
        transform.localScale = Vector3.one * Mathf.LerpUnclamped(1.5f, 1f, pop);
        float alpha = 1f - Mathf.Clamp01((_age - Lifetime * 0.6f) / (Lifetime * 0.4f));
        _text.color = new Color(_color.r, _color.g, _color.b, alpha);
        FaceCamera();
        if (_age >= Lifetime) Destroy(gameObject);
    }

    private void FaceCamera()
    {
        var cam = Camera.main;
        if (cam == null) return;
        var away = transform.position - cam.transform.position;
        if (away.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(away, Vector3.up);
    }
}
