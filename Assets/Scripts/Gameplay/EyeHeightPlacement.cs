using UnityEngine;

/// <summary>
/// Sets this object's height from the player's body when the stage appears,
/// so pieces suit a short or a tall player: the eye height calibrated during
/// the scan (PlayerBody), or, before calibration, the camera's height now.
/// - fractionOfEyes > 0: a height proportional to the body, e.g. coins at
///   waist height (0.6) and bricks at chest height (0.78).
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

    private void Start() => Place();

    /// <summary>Public so headless tests can place with a chosen eye height.</summary>
    public void Place()
    {
        var cam = Camera.main;
        float floorY = transform.parent != null ? transform.parent.position.y : 0f;
        float live = cam != null ? cam.transform.position.y - floorY : 1.55f;
        float eyes = PlayerBody.EyeHeightOr(live);
        float above = fractionOfEyes > 0f ? eyes * fractionOfEyes : eyes + offsetFromEyes;
        var p = transform.position;
        transform.position = new Vector3(p.x, floorY + Mathf.Max(above, minHeightAboveFloor), p.z);
    }
}
