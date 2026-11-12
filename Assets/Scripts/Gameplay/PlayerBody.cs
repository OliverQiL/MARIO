/// <summary>
/// The player's measured eye height (metres above the floor), calibrated once
/// during the room scan: ARSessionManager samples the camera's height while
/// the player walks round, and the median becomes their eye height when they
/// confirm. It's the standing baseline for phone-side jumps and squats and
/// the floor estimate for targeting. Clamped to a plausible range.
///
/// Stage pieces don't use it: they're placed for a fixed
/// <see cref="StageEyeHeight"/> (Oliver 2026-11-12), so every stage looks the
/// same whoever scanned and however the phone was held.
/// </summary>
public static class PlayerBody
{
    /// <summary>Plausible eye heights (metres above the floor): a child to a very tall adult.</summary>
    public const float MinEyeHeight = 0.9f, MaxEyeHeight = 2.1f;

    /// <summary>The eye height stages are built for (metres above the floor): ? blocks, bricks and coins (EyeHeightPlacement).</summary>
    public const float StageEyeHeight = 1.70f;

    /// <summary>Calibrated eye height, or null before the scan is confirmed.</summary>
    public static float? EyeHeight { get; private set; }

    public static void Calibrate(float eyeHeight) =>
        EyeHeight = UnityEngine.Mathf.Clamp(eyeHeight, MinEyeHeight, MaxEyeHeight);

    public static void Reset() => EyeHeight = null;

    /// <summary>The calibrated eye height, or else <paramref name="live"/> (the camera now), clamped.</summary>
    public static float EyeHeightOr(float live) =>
        UnityEngine.Mathf.Clamp(EyeHeight ?? live, MinEyeHeight, MaxEyeHeight);
}
