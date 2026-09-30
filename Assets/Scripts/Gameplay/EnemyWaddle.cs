using UnityEngine;

/// <summary>
/// Visual only: rocks an enemy's model side to side with a little bounce while
/// it walks, like the classic two-frame walk. Goes on the "Model" child so the
/// enemy's own transform (used for movement and hits) is untouched. Holds
/// still when the enemy isn't moving (shell, defeated, session paused).
/// </summary>
public class EnemyWaddle : MonoBehaviour
{
    [SerializeField] private float stepsPerSecond = 4f;
    [SerializeField] private float rockDegrees = 10f;
    [SerializeField] private float bounce = 0.012f;

    private Goomba _enemy;
    private Vector3 _restPosition;
    private Quaternion _restRotation;
    private float _phase;

    private void Awake()
    {
        _enemy = GetComponentInParent<Goomba>();
        _restPosition = transform.localPosition;
        _restRotation = transform.localRotation;
    }

    private void Update()
    {
        var gs = GameStateManager.Instance;
        bool moving = _enemy != null && _enemy.IsMoving && gs != null && gs.IsSessionReady;
        if (!moving)
        {
            transform.localPosition = _restPosition;
            transform.localRotation = _restRotation;
            return;
        }

        _phase += Time.deltaTime * stepsPerSecond * Mathf.PI * _enemy.SpeedMultiplier;
        float s = Mathf.Sin(_phase);
        transform.localRotation = _restRotation * Quaternion.Euler(0f, 0f, s * rockDegrees);
        transform.localPosition = _restPosition + Vector3.up * (Mathf.Abs(s) * bounce);
    }
}
