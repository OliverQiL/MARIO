using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Base for everything EventResolver (and fireball projectiles) can target:
/// blocks, enemies, test targets. Keeps a registry of live instances so
/// targeting is a cheap geometric query over known objects instead of a
/// physics overlap — stage elements need no colliders, which also keeps them
/// out of LevelGenerator's placement checks.
/// </summary>
public abstract class InteractableBase : MonoBehaviour, IInteractable
{
    private static readonly List<InteractableBase> s_All = new();
    public static IReadOnlyList<InteractableBase> All => s_All;

    public abstract bool IsActionable { get; }
    public abstract bool RespondsTo(string actionLabel);
    /// <summary>Gate 5 for one action. Defaults to IsActionable; a Koopa shell differs per action.</summary>
    public virtual bool IsActionableFor(string actionLabel) => IsActionable;
    public abstract void ResolveHit(string actionLabel, GameStateManager gameState);

    /// <summary>The point used for range checks and projectile hits.</summary>
    public virtual Vector3 HitPoint => transform.position;

    protected virtual void OnEnable()
    {
        s_All.RemoveAll(x => x == null); // destroyed without OnDisable (edit-mode tests)
        if (!s_All.Contains(this)) s_All.Add(this);
    }
    protected virtual void OnDisable() => s_All.Remove(this);

    /// <summary>Destroy that also works in edit mode (headless tests).</summary>
    public static void SafeDestroy(Object obj, float delay = 0f)
    {
        if (obj == null) return;
        if (Application.isPlaying) Destroy(obj, delay);
        else DestroyImmediate(obj);
    }
}
