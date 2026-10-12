using System;
using System.Collections.Generic;
using UnityEngine;

public enum Sfx
{
    Jump, Coin, PowerUpAppear, PowerUp, OneUp, Stomp, Kick, BrickBreak, Fireball,
    Damage, Warning, CannonFire, StageClear, Warp, GameOver, Denied, GameClear,
}

/// <summary>
/// Sound effects (design report module table; Docs/GAME_DESIGN.md, pass 3).
/// Every clip is synthesised at startup (square/triangle tones, pitch sweeps
/// and noise, in a chiptune style), so there are no audio assets to license
/// or import. Played non-spatially through one AudioSource.
///
/// Most sounds come from events it listens to (EventResolver, GameStateManager,
/// LevelGenerator); elements with no suitable event call AudioManager.Play
/// directly, which is a no-op when there's no AudioManager in the scene.
///
/// Background music: assign any imported audio file to Music. It loops from
/// the first stage, stops at game over and starts again with the next game.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class AudioManager : MonoBehaviour
{
    [SerializeField] private EventResolver eventResolver;
    [SerializeField] private GameStateManager gameState;
    [SerializeField] private LevelGenerator levelGenerator;
    [Range(0f, 1f)] [SerializeField] private float volume = 0.6f;
    [SerializeField] private bool muted;

    [Header("Background music")]
    [Tooltip("Any imported audio file (e.g. Assets/Audio/theme.ogg). Leave empty for no music.")]
    [SerializeField] private AudioClip music;
    [Range(0f, 1f)] [SerializeField] private float musicVolume = 0.3f;

    private const int SampleRate = 22050;
    private static AudioManager s_Instance;
    private readonly Dictionary<Sfx, AudioClip> _clips = new();
    private AudioSource _source;
    private AudioSource _musicSource;

    public IReadOnlyDictionary<Sfx, AudioClip> Clips => _clips;

    /// <summary>Plays a sound effect if there's an AudioManager in the scene.</summary>
    public static void Play(Sfx sfx)
    {
        if (s_Instance == null) s_Instance = FindAnyObjectByType<AudioManager>();
        if (s_Instance != null) s_Instance.PlayNow(sfx);
    }

    private void Reset()
    {
        eventResolver = FindAnyObjectByType<EventResolver>();
        gameState = FindAnyObjectByType<GameStateManager>();
        levelGenerator = FindAnyObjectByType<LevelGenerator>();
    }

    private void Awake()
    {
        s_Instance = this;
        _source = GetComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.spatialBlend = 0f;
        _musicSource = gameObject.AddComponent<AudioSource>();
        _musicSource.playOnAwake = false;
        _musicSource.loop = true;
        _musicSource.spatialBlend = 0f;
        BuildClips();
    }

    private void OnEnable()
    {
        if (eventResolver != null)
        {
            eventResolver.OnEventProcessed += HandleEventProcessed;
            eventResolver.OnImpact += HandleImpact;
        }
        if (gameState != null)
        {
            gameState.OnLifeLost.AddListener(HandleLifeLost);
            gameState.OnGameOver.AddListener(HandleGameOver);
            gameState.OnGameClear.AddListener(HandleGameClear);
        }
        if (levelGenerator != null)
        {
            levelGenerator.OnStageCompleted.AddListener(HandleStageCompleted);
            levelGenerator.OnStageStarted.AddListener(HandleStageStarted);
        }
    }

    private void OnDisable()
    {
        if (eventResolver != null)
        {
            eventResolver.OnEventProcessed -= HandleEventProcessed;
            eventResolver.OnImpact -= HandleImpact;
        }
        if (gameState != null)
        {
            gameState.OnLifeLost.RemoveListener(HandleLifeLost);
            gameState.OnGameOver.RemoveListener(HandleGameOver);
            gameState.OnGameClear.RemoveListener(HandleGameClear);
        }
        if (levelGenerator != null)
        {
            levelGenerator.OnStageCompleted.RemoveListener(HandleStageCompleted);
            levelGenerator.OnStageStarted.RemoveListener(HandleStageStarted);
        }
    }

    private void PlayNow(Sfx sfx)
    {
        if (muted || _source == null || !_clips.TryGetValue(sfx, out var clip)) return;
        _source.PlayOneShot(clip, volume);
    }

    // ---- event → sound ----

    private void HandleEventProcessed(GameEvent evt, ResolutionResult result)
    {
        if (result.ThrewShell) { PlayNow(Sfx.Kick); return; }
        if (result.LaunchedProjectile) { PlayNow(Sfx.Fireball); return; }

        if (result.Resolved)
        {
            switch (evt.actionLabel)
            {
                case "jump": PlayNow(Sfx.Jump); break; // a ? block adds its own coin/power-up sound
                case "stomp": PlayNow(Sfx.Stomp); break;
                case "punch": PlayNow(Sfx.BrickBreak); break;
                case "clap": PlayNow(Sfx.PowerUp); break; // picked up a shell
                // squat: a warp pipe plays Warp itself; a plain duck is silent
            }
            return;
        }

        // "Not permitted": a soft bonk.
        if (result.NotPermitted) PlayNow(Sfx.Denied);
    }

    private void HandleImpact(GameEvent evt, Component target) =>
        PlayNow(target is BrickBlock ? Sfx.BrickBreak : Sfx.Kick);

    private void HandleLifeLost(int livesLeft)
    {
        if (livesLeft > 0) PlayNow(Sfx.Damage);
    }

    private void HandleGameOver()
    {
        if (_musicSource != null) _musicSource.Stop();
        PlayNow(Sfx.GameOver);
    }

    private void HandleGameClear()
    {
        if (_musicSource != null) _musicSource.Stop();
        PlayNow(Sfx.GameClear);
    }

    /// <summary>Starts the music with the first stage (and again after a game over); it keeps looping between stages.</summary>
    private void HandleStageStarted()
    {
        if (music == null || muted || _musicSource == null || _musicSource.isPlaying) return;
        _musicSource.clip = music;
        _musicSource.volume = musicVolume;
        _musicSource.Play();
    }
    private void HandleStageCompleted() => PlayNow(Sfx.StageClear);

    // ---- synthesis ----

    private enum Wave { Square, Triangle, Noise }

    private void BuildClips()
    {
        _clips.Clear();
        Add(Sfx.Jump, Sweep(Wave.Square, 330f, 880f, 0.16f, 0.35f));
        Add(Sfx.Coin, Notes(Wave.Square, 0.35f, (988f, 0.07f), (1319f, 0.28f)));
        Add(Sfx.PowerUpAppear, Notes(Wave.Square, 0.3f, (392f, 0.06f), (523f, 0.06f), (659f, 0.06f), (784f, 0.06f), (1047f, 0.12f)));
        Add(Sfx.PowerUp, Notes(Wave.Square, 0.3f, (523f, 0.05f), (659f, 0.05f), (784f, 0.05f), (1047f, 0.05f), (1319f, 0.05f), (1568f, 0.15f)));
        Add(Sfx.OneUp, Notes(Wave.Square, 0.3f, (1319f, 0.09f), (1568f, 0.09f), (2637f, 0.09f), (2093f, 0.09f), (2349f, 0.09f), (3136f, 0.18f)));
        Add(Sfx.Stomp, Sweep(Wave.Square, 520f, 130f, 0.12f, 0.4f));
        Add(Sfx.Kick, Mix(Sweep(Wave.Square, 700f, 200f, 0.1f, 0.3f), Sweep(Wave.Noise, 0f, 0f, 0.08f, 0.2f)));
        Add(Sfx.BrickBreak, Mix(Sweep(Wave.Noise, 0f, 0f, 0.22f, 0.45f), Sweep(Wave.Square, 220f, 90f, 0.12f, 0.2f)));
        Add(Sfx.Fireball, Mix(Sweep(Wave.Square, 900f, 300f, 0.12f, 0.25f), Sweep(Wave.Noise, 0f, 0f, 0.1f, 0.15f)));
        Add(Sfx.Damage, Sweep(Wave.Square, 700f, 110f, 0.45f, 0.4f));
        Add(Sfx.Warning, Notes(Wave.Triangle, 0.5f, (880f, 0.12f), (0f, 0.06f), (880f, 0.12f), (0f, 0.06f), (1175f, 0.2f)));
        Add(Sfx.CannonFire, Mix(Sweep(Wave.Noise, 0f, 0f, 0.35f, 0.5f), Sweep(Wave.Square, 160f, 50f, 0.3f, 0.35f)));
        Add(Sfx.StageClear, Notes(Wave.Square, 0.3f, (523f, 0.1f), (659f, 0.1f), (784f, 0.1f), (1047f, 0.14f), (784f, 0.08f), (1047f, 0.35f)));
        Add(Sfx.Warp, Concat(Sweep(Wave.Square, 600f, 200f, 0.14f, 0.35f), Sweep(Wave.Square, 600f, 200f, 0.14f, 0.35f), Sweep(Wave.Square, 600f, 150f, 0.2f, 0.35f)));
        Add(Sfx.GameOver, Notes(Wave.Triangle, 0.5f, (523f, 0.18f), (392f, 0.18f), (330f, 0.18f), (262f, 0.5f)));
        Add(Sfx.Denied, Sweep(Wave.Triangle, 160f, 120f, 0.1f, 0.35f));
        // A longer victory fanfare: rising arpeggios, then a held high chord tone.
        Add(Sfx.GameClear, Notes(Wave.Square, 0.3f,
            (392f, 0.1f), (523f, 0.1f), (659f, 0.1f), (784f, 0.2f), (659f, 0.1f), (784f, 0.3f),
            (415f, 0.1f), (523f, 0.1f), (622f, 0.1f), (831f, 0.2f), (622f, 0.1f), (831f, 0.3f),
            (466f, 0.1f), (587f, 0.1f), (698f, 0.1f), (932f, 0.2f), (932f, 0.1f), (932f, 0.1f), (1047f, 0.6f)));
    }

    private void Add(Sfx sfx, float[] samples)
    {
        var clip = AudioClip.Create(sfx.ToString(), samples.Length, 1, SampleRate, false);
        clip.SetData(samples, 0);
        _clips[sfx] = clip;
    }

    /// <summary>One tone gliding from f0 to f1 (Hz) with a quick attack and decay envelope.</summary>
    private static float[] Sweep(Wave wave, float f0, float f1, float seconds, float amp)
    {
        int n = Mathf.Max(1, Mathf.RoundToInt(seconds * SampleRate));
        var s = new float[n];
        var rng = new System.Random(n); // deterministic noise
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / n;
            float f = Mathf.Lerp(f0, f1, t);
            phase += f / SampleRate;
            float p = (float)(phase - Math.Floor(phase));
            float v = wave switch
            {
                Wave.Square => p < 0.5f ? 1f : -1f,
                Wave.Triangle => 4f * Mathf.Abs(p - 0.5f) - 1f,
                _ => (float)(rng.NextDouble() * 2 - 1),
            };
            float attack = Mathf.Clamp01(i / (0.005f * SampleRate));
            float decay = 1f - t * t;
            s[i] = v * amp * attack * decay;
        }
        return s;
    }

    /// <summary>A sequence of (frequency, seconds) notes; frequency 0 is a rest.</summary>
    private static float[] Notes(Wave wave, float amp, params (float hz, float seconds)[] notes)
    {
        var parts = new float[notes.Length][];
        for (int i = 0; i < notes.Length; i++)
            parts[i] = notes[i].hz > 0f ? Sweep(wave, notes[i].hz, notes[i].hz, notes[i].seconds, amp)
                                        : new float[Mathf.RoundToInt(notes[i].seconds * SampleRate)];
        return Concat(parts);
    }

    private static float[] Concat(params float[][] parts)
    {
        int n = 0;
        foreach (var p in parts) n += p.Length;
        var s = new float[n];
        int o = 0;
        foreach (var p in parts) { Array.Copy(p, 0, s, o, p.Length); o += p.Length; }
        return s;
    }

    private static float[] Mix(float[] a, float[] b)
    {
        var s = new float[Mathf.Max(a.Length, b.Length)];
        for (int i = 0; i < s.Length; i++)
            s[i] = Mathf.Clamp((i < a.Length ? a[i] : 0f) + (i < b.Length ? b[i] : 0f), -1f, 1f);
        return s;
    }
}
