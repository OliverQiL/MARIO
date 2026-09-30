using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Fireball projectile, thrown along the camera's facing direction (the player
/// aims by looking). Hits the first actionable target that responds to
/// "fireball" (enemies, bricks) within hitRadius of its path, or expires.
/// InFlight counts live fireballs for EventResolver's Gate 3 limit.
/// </summary>
public class Fireball : MonoBehaviour
{
    [SerializeField] private float speed = 4f;
    [SerializeField] private float lifetime = 1.5f;
    [SerializeField] private float hitRadius = 0.3f;

    // Live fireballs; destroyed ones compare equal to null and are pruned on read,
    // so the count stays right even where OnDestroy doesn't run (edit-mode tests).
    private static readonly List<Fireball> s_Live = new();

    public static int InFlight
    {
        get
        {
            s_Live.RemoveAll(f => f == null);
            return s_Live.Count;
        }
    }

    private Vector3 _direction;
    private float _age;
    private Action<InteractableBase> _onHit;

    public void Launch(Vector3 direction, Action<InteractableBase> onHit)
    {
        _direction = direction.normalized;
        _onHit = onHit;
        if (!s_Live.Contains(this)) s_Live.Add(this);
    }

    private void Update() => Tick(Time.deltaTime);

    /// <summary>Advances the projectile; returns the target hit, if any. Public for headless tests.</summary>
    public InteractableBase Tick(float dt)
    {
        _age += dt;
        // Sub-step so a fast fireball can't skip over a small target.
        int steps = Mathf.Max(1, Mathf.CeilToInt(speed * dt / (hitRadius * 0.5f)));
        for (int i = 0; i < steps; i++)
        {
            transform.position += _direction * (speed * dt / steps);
            var target = FindHit();
            if (target != null)
            {
                var gs = GameStateManager.Instance;
                if (gs != null) target.ResolveHit("fireball", gs);
                _onHit?.Invoke(target);
                InteractableBase.SafeDestroy(gameObject);
                return target;
            }
        }

        if (_age >= lifetime) InteractableBase.SafeDestroy(gameObject);
        return null;
    }

    private InteractableBase FindHit()
    {
        foreach (var t in InteractableBase.All)
        {
            if (t == null || !t.RespondsTo("fireball") || !t.IsActionableFor("fireball")) continue;
            if ((t.HitPoint - transform.position).sqrMagnitude <= hitRadius * hitRadius) return t;
        }
        return null;
    }
}
