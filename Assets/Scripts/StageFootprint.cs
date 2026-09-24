using UnityEngine;

/// <summary>
/// Attach to the root of any stage prefab to declare the floor footprint and
/// vertical clearance it needs. LevelGenerator's placement search reads this
/// to decide whether a candidate position can fit the prefab.
/// </summary>
public class StageFootprint : MonoBehaviour
{
    [Header("Footprint (metres)")]
    [Tooltip("Width along local X.")]
    public float width = 1f;
    [Tooltip("Depth along local Z.")]
    public float depth = 1f;
    [Tooltip("Vertical clearance required above the floor, along local Y.")]
    public float clearance = 1f;

    /// <summary>Half-extents for a Physics.CheckBox test matching this footprint.</summary>
    public Vector3 HalfExtents => new Vector3(width * 0.5f, clearance * 0.5f, depth * 0.5f);

    /// <summary>Floor area this footprint occupies — used to rank templates when falling back to a compact variant.</summary>
    public float FootprintArea => width * depth;

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        // Visualise the footprint box in the Scene view while authoring prefabs.
        Gizmos.color = new Color(0f, 1f, 0f, 0.3f);
        var center = transform.position + transform.up * (clearance * 0.5f);
        Gizmos.matrix = Matrix4x4.TRS(center, transform.rotation, Vector3.one);
        Gizmos.DrawCube(Vector3.zero, new Vector3(width, clearance, depth));
    }
#endif
}
