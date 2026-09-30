/// <summary>
/// The player's measured eye height (metres above the floor), calibrated once
/// during the room scan: ARSessionManager samples the camera's height while
/// the player walks round, and the median becomes their eye height when they
/// confirm. Stage pieces are placed from it (EyeHeightPlacement), so they suit
/// a short or a tall player and don't depend on how the phone happens to be
/// held when a stage appears. Clamped to a plausible range.
/// </summary>
public static class PlayerBody
{
    /// <summary>Plausible eye heights (metres above the floor): a child to a very tall adult.</summary>
    public const float MinEyeHeight = 0.9f, MaxEyeHeight = 2.1f;

    /// <summary>Calibrated eye height, or null before the scan is confirmed.</summary>
    public static float? EyeHeight { get; private set; }

    public static void Calibrate(float eyeHeight) =>
        EyeHeight = UnityEngine.Mathf.Clamp(eyeHeight, MinEyeHeight, MaxEyeHeight);

    public static void Reset() => EyeHeight = null;

    /// <summary>The calibrated eye height, or else <paramref name="live"/> (the camera now), clamped.</summary>
    public static float EyeHeightOr(float live) =>
        UnityEngine.Mathf.Clamp(EyeHeight ?? live, MinEyeHeight, MaxEyeHeight);
}
