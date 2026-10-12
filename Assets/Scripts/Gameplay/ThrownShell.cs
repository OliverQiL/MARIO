using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A Koopa shell the player has thrown: it slides along the floor in the
/// direction they're facing and knocks out every enemy it touches (label
/// "shell"), then stops and vanishes. Unlike a fireball it doesn't stop at
/// the first hit, so a good throw down a line of Goombas clears them all.
///
/// Movement is in Tick(dt) so headless tests can drive it. InFlight counts
/// live shells (for tests and the HUD).
/// </summary>
public class ThrownShell : MonoBehaviour
{
    /// <summary>The action label enemies see when a shell hits them.</summary>
    public const string HitLabel = "shell";

    [SerializeField] private float speed = 3f;
    [SerializeField] private float lifetime = 2.5f;
    [SerializeField] private float hitRadius = 0.25f;
    [Tooltip("Score multiplier steps for a combo of hits from one throw (classic 'shell chain').")]
    [SerializeField] private int comboBonus = 100;
    [SerializeField] private GameObject impactEffect;

    private static readonly List<ThrownShell> s_Live = new();

    public static int InFlight
    {
        get
        {
            s_Live.RemoveAll(s => s == null);
            return s_Live.Count;
        }
    }

    private Vector3 _direction;
    private float _age;
    private int _hits;
    private Action<InteractableBase> _onHit;
    private readonly HashSet<InteractableBase> _alreadyHit = new(); // a boss survives a hit: once per throw

    public int Hits => _hits;

    public void Launch(Vector3 direction, Action<InteractableBase> onHit)
    {
        direction.y = 0f;
        _direction = direction.sqrMagnitude > 1e-4f ? direction.normalized : Vector3.forward;
        _onHit = onHit;
        transform.rotation = Quaternion.LookRotation(_direction, Vector3.up);
        if (!s_Live.Contains(this)) s_Live.Add(this);
    }

    private void Update() => Tick(Time.deltaTime);

    /// <summary>Advances the shell; returns how many targets it hit this step. Public for headless tests.</summary>
    public int Tick(float dt)
    {
        _age += dt;
        int hitsThisStep = 0;
        int steps = Mathf.Max(1, Mathf.CeilToInt(speed * dt / (hitRadius * 0.5f)));
        for (int i = 0; i < steps; i++)
        {
            transform.position += _direction * (speed * dt / steps);
            var target = FindHit();
            if (target == null) continue;

            _alreadyHit.Add(target);
            var gs = GameStateManager.Instance;
            if (gs != null)
            {
                target.ResolveHit(HitLabel, gs);
                if (_hits > 0) gs.AddScore(comboBonus * _hits); // chain bonus
            }
            _hits++;
            hitsThisStep++;
            _onHit?.Invoke(target);
            if (impactEffect != null) Instantiate(impactEffect, transform.position, Quaternion.identity);
        }

        if (_age >= lifetime) InteractableBase.SafeDestroy(gameObject);
        return hitsThisStep;
    }

    private InteractableBase FindHit()
    {
        foreach (var t in InteractableBase.All)
        {
            if (t == null || _alreadyHit.Contains(t) || !t.RespondsTo(HitLabel) || !t.IsActionableFor(HitLabel)) continue;
            if (t.TouchesFloorAt(transform.position, hitRadius)) return t;
        }
        return null;
    }
}
