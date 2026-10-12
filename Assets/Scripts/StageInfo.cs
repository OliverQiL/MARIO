using UnityEngine;

/// <summary>
/// A campaign stage's name and one-line tip, shown on the HUD's intro card
/// when the stage starts ("WORLD 2-1 / SHELL HILLS"). GameContentSetup puts
/// it on each campaign stage root from StageDesigns; fallback stages have none.
/// </summary>
public class StageInfo : MonoBehaviour
{
    [SerializeField] private string title;
    [SerializeField] private string tip;

    public string Title => title;
    public string Tip => tip;
}
