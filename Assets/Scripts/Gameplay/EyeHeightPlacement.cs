using UnityEngine;

/// <summary>
/// Sets this object's height relative to the player's eye height (the AR
/// camera) when the stage appears, so "overhead" and "chest height" mean the
/// same thing for every player. The stage root sits on the floor.
/// </summary>
public class EyeHeightPlacement : MonoBehaviour
{
    [Tooltip("Metres relative to eye height: +0.3 = overhead block, -0.35 = chest height.")]
    [SerializeField] private float offsetFromEyes = 0.3f;
    [Tooltip("Never lower than this above the floor (metres), e.g. for a seated or short player.")]
    [SerializeField] private float minHeightAboveFloor = 0.6f;

    private void Start()
    {
        var cam = Camera.main;
        if (cam == null) return;

        float floorY = transform.parent != null ? transform.parent.position.y : 0f;
        float y = Mathf.Max(cam.transform.position.y + offsetFromEyes, floorY + minHeightAboveFloor);
        var p = transform.position;
        transform.position = new Vector3(p.x, y, p.z);
    }
}
