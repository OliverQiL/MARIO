using UnityEngine;

/// <summary>
/// Spawns enemies from a point on the stage, one at a time, cycling through
/// enemyPrefabs (Goomba, Koopa, ...): the next appears
/// a short while after the previous one is defeated or gone, up to a total
/// count. Spawned enemies are children of the stage, so they're cleaned up
/// with it. Paused while the session isn't ready, and holds off while the
/// player is standing near the spawn point, so an enemy never appears on top
/// of them (seen on-device: a Goomba hit the player ~1 s after spawning).
/// Difficulty adds enemies and walking speed on later stages.
/// </summary>
public class EnemySpawner : MonoBehaviour
{
    [Tooltip("Spawned in this order, cycling. Koopa derives from Goomba.")]
    [SerializeField] private Goomba[] enemyPrefabs;
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
        if (gs == null || !gs.IsSessionReady || enemyPrefabs == null || enemyPrefabs.Length == 0) return null;
        if (_current != null || _spawned >= totalCount + Difficulty.ExtraEnemies) return null;

        _timer += dt;
        if (_timer < (_spawned == 0 ? initialDelay : respawnDelay)) return null;

        var toPlayer = playerPosition - transform.position;
        toPlayer.y = 0f;
        if (toPlayer.magnitude < minPlayerDistance) return null; // wait for the player to move off

        var prefab = enemyPrefabs[_spawned % enemyPrefabs.Length];
        if (prefab == null) return null;
        _timer = 0f;
        _spawned++;
        _current = Instantiate(prefab, transform.position, transform.rotation, transform.parent);
        _current.SpeedMultiplier = Difficulty.EnemySpeedMultiplier;
        Debug.Log($"[EnemySpawner] Spawned {prefab.name} ({_spawned}/{totalCount + Difficulty.ExtraEnemies}, speed ×{_current.SpeedMultiplier:F2}).");
        return _current;
    }
}
