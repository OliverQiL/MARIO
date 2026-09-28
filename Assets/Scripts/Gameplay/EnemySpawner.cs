using UnityEngine;

/// <summary>
/// Spawns enemies from a point on the stage, one at a time: the next appears
/// a short while after the previous one is defeated or gone, up to a total
/// count. Spawned enemies are children of the stage, so they're cleaned up
/// with it. Paused while the session isn't ready, and holds off while the
/// player is standing near the spawn point, so an enemy never appears on top
/// of them (seen on-device: a Goomba hit the player ~1 s after spawning).
/// </summary>
public class EnemySpawner : MonoBehaviour
{
    [SerializeField] private Goomba enemyPrefab;
    [SerializeField] private int totalCount = 2;
    [SerializeField] private float initialDelay = 2f;
    [SerializeField] private float respawnDelay = 3f;
    [Tooltip("Don't spawn while the player is closer than this (horizontal metres) to the spawn point.")]
    [SerializeField] private float minPlayerDistance = 1.2f;

    private Goomba _current;
    private int _spawned;
    private float _timer;

    private void Update()
    {
        var cam = Camera.main;
        if (cam != null) Tick(Time.deltaTime, cam.transform.position);
    }

    public Goomba Tick(float dt, Vector3 playerPosition)
    {
        var gs = GameStateManager.Instance;
        if (gs == null || !gs.IsSessionReady || enemyPrefab == null) return null;
        if (_current != null || _spawned >= totalCount) return null;

        _timer += dt;
        if (_timer < (_spawned == 0 ? initialDelay : respawnDelay)) return null;

        var toPlayer = playerPosition - transform.position;
        toPlayer.y = 0f;
        if (toPlayer.magnitude < minPlayerDistance) return null; // wait for the player to move off

        _timer = 0f;
        _spawned++;
        _current = Instantiate(enemyPrefab, transform.position, transform.rotation, transform.parent);
        Debug.Log($"[EnemySpawner] Spawned {enemyPrefab.name} ({_spawned}/{totalCount}).");
        return _current;
    }
}
