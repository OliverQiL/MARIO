using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Spawns one wave of enemies from a point on the stage when the stage
/// starts, cycling through enemyPrefabs (Goomba, Koopa, ...): the first after
/// initialDelay, the rest of the wave following waveSpacing apart (so they
/// walk out in a line rather than on top of each other), whether or not the
/// earlier ones are still alive. Once the wave is out, nothing more spawns:
/// clear it and the stage is clear (Oliver 2026-11-05; it used to trickle in
/// the next enemy after each defeat). Spawned enemies are children of the
/// stage, so they're cleaned up with it. Paused while the session isn't
/// ready, and holds off while the player is standing near the spawn point, so
/// an enemy never appears on top of them (seen on-device: a Goomba hit the
/// player ~1 s after spawning). The wave is the designed size on every stage.
/// </summary>
public class EnemySpawner : MonoBehaviour
{
    [Tooltip("Spawned in this order, cycling. Koopa derives from Goomba.")]
    [SerializeField] private Goomba[] enemyPrefabs;
    [Tooltip("The wave's size.")]
    [SerializeField] private int totalCount = 2;
    [SerializeField] private float initialDelay = 2f;
    [Tooltip("Seconds between the enemies of the wave, so they don't spawn on top of each other.")]
    [FormerlySerializedAs("respawnDelay")]
    [SerializeField] private float waveSpacing = 1.5f;
    [Tooltip("Don't spawn while the player is closer than this (horizontal metres) to the spawn point.")]
    [SerializeField] private float minPlayerDistance = 1.2f;

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
        if (_spawned >= WaveSize) return null; // the wave is out: no more

        _timer += dt;
        if (_timer < (_spawned == 0 ? initialDelay : waveSpacing)) return null;

        var toPlayer = playerPosition - transform.position;
        toPlayer.y = 0f;
        if (toPlayer.magnitude < minPlayerDistance) return null; // wait for the player to move off

        var prefab = enemyPrefabs[_spawned % enemyPrefabs.Length];
        if (prefab == null) return null;
        _timer = 0f;
        _spawned++;
        var enemy = Instantiate(prefab, transform.position, transform.rotation, transform.parent);
        Debug.Log($"[EnemySpawner] Spawned {prefab.name} ({_spawned}/{WaveSize} of the wave).");
        return enemy;
    }

    /// <summary>How many enemies the wave has.</summary>
    public int WaveSize => totalCount;
    /// <summary>The whole wave has been spawned; nothing more will come from here.</summary>
    public bool WaveDone => _spawned >= WaveSize;
}
