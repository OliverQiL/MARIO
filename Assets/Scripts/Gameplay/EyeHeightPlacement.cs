using UnityEngine;

/// <summary>
/// Sets this object's height when the stage appears, from a fixed eye height
/// (PlayerBody.StageEyeHeight, 1.70 m; Oliver 2026-11-12: until then it was the
/// eye height calibrated during the scan, so stages changed with whoever scanned):
/// - fractionOfEyes > 0: a height proportional to it, e.g. coins at waist
///   height (0.6) and bricks at chest height (0.78).
/// - otherwise offsetFromEyes above/below eye level, e.g. ? blocks at +0.35,
///   so their bottom clears the player's head and they're reached by jumping.
/// The stage root sits on the floor.
/// </summary>
public class EyeHeightPlacement : MonoBehaviour
{
    [Tooltip("Metres relative to eye height, used when Fraction Of Eyes is 0: +0.35 = overhead block.")]
    [SerializeField] private float offsetFromEyes = 0.3f;
    [Tooltip("If > 0: height = this fraction of eye height (0.6 = waist, 0.78 = chest), so it scales with the player.")]
    [SerializeField] private float fractionOfEyes;
    [Tooltip("Never lower than this above the floor (metres).")]
    [SerializeField] private float minHeightAboveFloor = 0.3f;
    [Tooltip("Extra metres on top of the body-relative height (coin arcs rise in the middle).")]
    [SerializeField] private float extraHeight;

    private void Start() => Place();

    /// <summary>Public so headless tests can place it.</summary>
    public void Place()
    {
        float floorY = transform.parent != null ? transform.parent.position.y : 0f;
        const float eyes = PlayerBody.StageEyeHeight;
        float above = (fractionOfEyes > 0f ? eyes * fractionOfEyes : eyes + offsetFromEyes) + extraHeight;
        var p = transform.position;
        transform.position = new Vector3(p.x, floorY + Mathf.Max(above, minHeightAboveFloor), p.z);
    }
}
