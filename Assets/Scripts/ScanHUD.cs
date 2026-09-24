using UnityEngine;
using TMPro;

/// <summary>
/// Simple screen-space HUD that displays live scan progress from ARSessionManager.
/// Attach to the same GameObject as the TextMeshPro text it should update.
/// </summary>
public class ScanHUD : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ARSessionManager sessionManager;
    [SerializeField] private TMP_Text statusText;

    [Header("Display")]
    [SerializeField] private string scanningFormat = "Scanning floor: {0:F1} / {1:F1} m\u00b2";
    [SerializeField] private string completeFormat = "Scan complete! ({0:F1} m\u00b2 scanned)";
    [SerializeField] private Color scanningColor = Color.white;
    [SerializeField] private Color completeColor = Color.green;

    private void Reset()
    {
        // Convenience: auto-find references the first time this component is added.
        if (sessionManager == null) sessionManager = FindAnyObjectByType<ARSessionManager>();
        if (statusText == null) statusText = GetComponentInChildren<TMP_Text>();
    }

    private void Update()
    {
        if (sessionManager == null || statusText == null) return;

        if (sessionManager.IsScanComplete)
        {
            statusText.text = string.Format(completeFormat, sessionManager.TotalScannedArea);
            statusText.color = completeColor;
        }
        else
        {
            statusText.text = string.Format(scanningFormat, sessionManager.TotalScannedArea, sessionManager.RequiredFloorArea);
            statusText.color = scanningColor;
        }
    }
}
