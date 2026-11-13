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
/// player ~1 s after spawning). The wave is the designed size on every stage
/// (at most 2 enemies a stage, Oliver 2026-11-12).
///
/// Koopas come first (Oliver 2026-11-12: the shell is the thing to learn):
/// the wave's Koopas spawn before its Goombas, and a spawner about to send a
/// Goomba waits (its delay starting again) while another spawner on the same
/// stage still has a Koopa to send.
///
/// One Koopa at a time, and always one to come (Oliver 2026-11-13): a Koopa
/// never spawns while another is in play on the stage (walking, a shell on
/// the floor, held or sliding). Once the wave is out, a spawner whose wave
/// had a Koopa sends a new one koopaRespawnSeconds after the last one's
/// shell is used up (or it's defeated), for as long as the stage lasts.
/// Goombas never come back.
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
    [Tooltip("After the wave: seconds after the last Koopa's shell is used up before a new Koopa comes.")]
    [SerializeField] private float koopaRespawnSeconds = 3f;

    private int _spawned;
    private float _timer, _koopaTimer;

    private void Update()
    {
        var cam = Camera.main;
        if (cam != null) Tick(Time.deltaTime, cam.transform.position);
    }

    public Goomba Tick(float dt, Vector3 playerPosition)
    {
        var gs = GameStateManager.Instance;
        if (gs == null || !gs.IsSessionReady || enemyPrefabs == null || enemyPrefabs.Length == 0) return null;
        if (_spawned >= WaveSize) return KoopaAgain(dt, playerPosition, gs); // the wave is out: only Koopas come back

        var prefab = WavePrefab(_spawned);
        if (prefab == null) return null;
        bool koopaInPlay = KoopaInPlay(gs);
        if (prefab is Koopa ? koopaInPlay : !koopaInPlay && SiblingHasKoopaToSend())
        {
            _timer = 0f; // one Koopa at a time / Goombas after the Koopa: wait, then the usual delay
            return null;
        }

        _timer += dt;
        if (_timer < (_spawned == 0 ? initialDelay : waveSpacing)) return null;

        var toPlayer = playerPosition - transform.position;
        toPlayer.y = 0f;
        if (toPlayer.magnitude < minPlayerDistance) return null; // wait for the player to move off

        _timer = 0f;
        _spawned++;
        var enemy = Instantiate(prefab, transform.position, transform.rotation, transform.parent);
        Debug.Log($"[EnemySpawner] Spawned {prefab.name} ({_spawned}/{WaveSize} of the wave).");
        return enemy;
    }

    /// <summary>After the wave: a new Koopa koopaRespawnSeconds after none is in play (its shell used up).</summary>
    private Goomba KoopaAgain(float dt, Vector3 playerPosition, GameStateManager gs)
    {
        if (!HasKoopa || KoopaInPlay(gs)) { _koopaTimer = 0f; return null; }
        _koopaTimer += dt;
        if (_koopaTimer < koopaRespawnSeconds) return null;
        var toPlayer = playerPosition - transform.position;
        toPlayer.y = 0f;
        if (toPlayer.magnitude < minPlayerDistance) return null;
        _koopaTimer = 0f;
        Goomba prefab = null;
        foreach (var p in enemyPrefabs) if (p is Koopa) { prefab = p; break; }
        var koopa = Instantiate(prefab, transform.position, transform.rotation, transform.parent);
        Debug.Log("[EnemySpawner] A new Koopa (the last shell is used up).");
        return koopa;
    }

    /// <summary>Its wave has a Koopa.</summary>
    public bool HasKoopa
    {
        get
        {
            for (int i = 0; i < WaveSize; i++) if (WavePrefab(i) is Koopa) return true;
            return false;
        }
    }

    /// <summary>A Koopa is in play on this stage: walking, its shell on the floor, held, or sliding.</summary>
    public bool KoopaInPlay(GameStateManager gs)
    {
        if (gs != null && gs.Held == HeldItem.Shell) return true;
        if (ThrownShell.InFlight > 0) return true;
        var stage = transform.parent;
        if (stage == null) return false;
        foreach (var k in stage.GetComponentsInChildren<Koopa>())
            if (k.IsActionable) return true;
        return false;
    }

    /// <summary>The <paramref name="index"/>th enemy of the wave: the cycled sequence, Koopas moved to the front.</summary>
    public Goomba WavePrefab(int index)
    {
        if (enemyPrefabs == null || enemyPrefabs.Length == 0 || index < 0 || index >= WaveSize) return null;
        int koopas = 0;
        for (int i = 0; i < WaveSize; i++) if (enemyPrefabs[i % enemyPrefabs.Length] is Koopa) koopas++;
        bool wantKoopa = index < koopas;
        int rank = wantKoopa ? index : index - koopas;
        for (int i = 0; i < WaveSize; i++)
        {
            var p = enemyPrefabs[i % enemyPrefabs.Length];
            if ((p is Koopa) != wantKoopa) continue;
            if (rank-- == 0) return p;
        }
        return null;
    }

    /// <summary>A Koopa still to come from this wave.</summary>
    public bool HasKoopaToSend => WavePrefab(_spawned) is Koopa;

    private bool SiblingHasKoopaToSend()
    {
        if (transform.parent == null) return false;
        foreach (var other in transform.parent.GetComponentsInChildren<EnemySpawner>())
            if (other != this && other.isActiveAndEnabled && other.HasKoopaToSend) return true;
        return false;
    }

    /// <summary>How many enemies the wave has.</summary>
    public int WaveSize => totalCount;
    /// <summary>The whole wave has been spawned; nothing more will come from here.</summary>
    public bool WaveDone => _spawned >= WaveSize;
}
