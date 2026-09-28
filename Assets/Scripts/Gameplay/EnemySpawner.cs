using UnityEngine;

/// <summary>
/// Spawns enemies from a point on the stage, one at a time: the next appears
/// a short while after the previous one is defeated or gone, up to a total
/// count. Spawned enemies are children of the stage, so they're cleaned up
/// with it. Paused while the session isn't ready.
/// </summary>
public class EnemySpawner : MonoBehaviour
{
    [SerializeField] private Goomba enemyPrefab;
    [SerializeField] private int totalCount = 2;
    [SerializeField] private float initialDelay = 2f;
    [SerializeField] private float respawnDelay = 3f;

    private Goomba _current;
    private int _spawned;
    private float _timer;

    private void Update() => Tick(Time.deltaTime);

    public Goomba Tick(float dt)
    {
        var gs = GameStateManager.Instance;
        if (gs == null || !gs.IsSessionReady || enemyPrefab == null) return null;
        if (_current != null || _spawned >= totalCount) return null;

        _timer += dt;
        if (_timer < (_spawned == 0 ? initialDelay : respawnDelay)) return null;

        _timer = 0f;
        _spawned++;
        _current = Instantiate(enemyPrefab, transform.position, transform.rotation, transform.parent);
        Debug.Log($"[EnemySpawner] Spawned {enemyPrefab.name} ({_spawned}/{totalCount}).");
        return _current;
    }
}
