using System;
using UnityEngine;

/// <summary>
/// Marks a stage's goal (design report Step 6). Put it on a child of a stage
/// prefab; LevelGenerator finds it when the stage is placed and hands it the
/// player's transform (the AR camera).
///
/// Markerless and touch-free: the goal is "reached" when the player
/// physically stands within reachRadius of it (horizontal distance only —
/// phone height doesn't matter) continuously for dwellSeconds, so briefly
/// brushing past doesn't count. Fires OnReached once.
///
/// Safety net: the goal only arms once the player has been outside its
/// reach, so a stage that happens to appear with its goal on the player can't
/// complete without them moving (LevelGenerator also avoids placing it so).
/// </summary>
public class StageGoal : MonoBehaviour
{
    [Header("Reach")]
    [Tooltip("Horizontal distance (metres) from this transform within which the player counts as at the goal.")]
    [SerializeField] private float reachRadius = 0.6f;
    [Tooltip("Seconds the player must stay within reach before the goal triggers.")]
    [SerializeField] private float dwellSeconds = 0.5f;

    [Header("Celebration (visual)")]
    [Tooltip("The flag, which slides down the pole when the goal is reached.")]
    [SerializeField] private Transform pennant;
    [Tooltip("Local height the flag slides down to.")]
    [SerializeField] private float pennantBottom = 0.25f;
    [SerializeField] private float slideSeconds = 1f;
    [SerializeField] private GameObject celebrationEffect;

    [Header("Final stage")]
    [Tooltip("Reaching this goal ends the game (GAME CLEAR) instead of moving on to the next stage.")]
    [SerializeField] private bool endsGame;

    public event Action<StageGoal> OnReached;

    public bool EndsGame => endsGame;
    /// <summary>Locked goals can't be reached (a boss guards it). Bowser sets and clears this.</summary>
    public bool Locked { get; set; }

    public float ReachRadius => reachRadius;
    public bool IsReached { get; private set; }
    /// <summary>Current horizontal distance to the player, for HUD/debugging. Infinity until initialised.</summary>
    public float PlayerDistance { get; private set; } = float.PositiveInfinity;

    private Transform _player;
    private float _dwellTimer;
    private bool _armed;
    private float _slideAge = float.PositiveInfinity;
    private float _pennantTop;

    public void Initialise(Transform player)
    {
        _player = player;
        IsReached = false;
        _dwellTimer = 0f;
        _armed = false;
    }

    private void Update()
    {
        if (pennant != null && _slideAge < slideSeconds)
        {
            _slideAge += Time.deltaTime;
            float t = Mathf.Clamp01(_slideAge / slideSeconds);
            var p = pennant.localPosition;
            pennant.localPosition = new Vector3(p.x, Mathf.Lerp(_pennantTop, pennantBottom, t * t * (3f - 2f * t)), p.z);
        }
        if (IsReached || _player == null) return;

        var offset = _player.position - transform.position;
        offset.y = 0f;
        PlayerDistance = offset.magnitude;

        if (!_armed)
        {
            _armed = PlayerDistance > reachRadius;
            return;
        }

        _dwellTimer = PlayerDistance <= reachRadius && !Locked ? _dwellTimer + Time.deltaTime : 0f;
        if (_dwellTimer < dwellSeconds) return;

        Reach();
    }

    /// <summary>Completes the stage now (the flag, or a warp pipe). Fires OnReached once.</summary>
    public void Reach()
    {
        if (IsReached) return;
        IsReached = true;
        if (pennant != null)
        {
            _pennantTop = pennant.localPosition.y;
            _slideAge = 0f;
            if (celebrationEffect != null) Instantiate(celebrationEffect, pennant.position, Quaternion.identity);
        }
        Debug.Log($"[StageGoal] '{name}' reached.");
        OnReached?.Invoke(this);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.85f, 0f, 0.5f);
        UnityEditor.Handles.color = Gizmos.color;
        UnityEditor.Handles.DrawWireDisc(transform.position, Vector3.up, reachRadius);
    }
#endif
}
