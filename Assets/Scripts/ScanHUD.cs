using UnityEngine;
using TMPro;

/// <summary>
/// Simple screen-space HUD that displays live scan progress from ARSessionManager,
/// then (once scanning is done) the stage-loop state from LevelGenerator and
/// GameStateManager. Attach to the same GameObject as the TextMeshPro text it
/// should update. Stop-gap until the real HUDController exists.
/// </summary>
public class ScanHUD : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ARSessionManager sessionManager;
    [SerializeField] private TMP_Text statusText;
    [Tooltip("Optional: shows stage-loop state after scanning.")]
    [SerializeField] private LevelGenerator levelGenerator;
    [Tooltip("Optional: shows stage number and score after scanning.")]
    [SerializeField] private GameStateManager gameState;

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
        if (levelGenerator == null) levelGenerator = FindAnyObjectByType<LevelGenerator>();
        if (gameState == null) gameState = FindAnyObjectByType<GameStateManager>();
    }

    private void Update()
    {
        if (sessionManager == null || statusText == null) return;

        if (sessionManager.IsScanComplete && levelGenerator != null && gameState != null)
        {
            statusText.text = StageLoopText();
            statusText.color = completeColor;
        }
        else if (sessionManager.IsScanComplete)
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

    private string StageLoopText()
    {
        string header = $"Stage {gameState.StageIndex} · Score {gameState.Score}";
        if (levelGenerator.IsTransitioning) return $"{header} — Stage clear!";
        if (levelGenerator.CurrentStage == null) return $"{header} — placing stage…";

        var goal = levelGenerator.CurrentGoal;
        return goal == null || float.IsInfinity(goal.PlayerDistance)
            ? header
            : $"{header} — walk to the flag ({goal.PlayerDistance:F1} m)";
    }
}
