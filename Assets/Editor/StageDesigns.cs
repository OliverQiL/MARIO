using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The hand-designed campaign (Docs/GAME_DESIGN.md, maps), as data.
/// GameContentSetup builds one stage prefab per design.
///
/// Coordinates are metres on the floor, in the stage's frame: x across,
/// z forward. The player starts beyond the near edge (z = −depth/2), so
/// −z is near and +z is far, where the flag goes. Heights come from the
/// piece type: ? blocks overhead, bricks at chest height, coins at waist
/// height, everything else on the floor. Full-size stages are 2.5 × 4 m
/// (x ±1.25, z ±2.0). Keep pieces about 0.25 m inside the edge.
///
/// To add or change a stage: edit it here, then run Tools ▸ MARIO ▸ Build
/// Game Content. The campaign loops after the last stage, harder each time
/// round (Difficulty).
/// </summary>
public static class StageDesigns
{
    public enum Kind { Question, Brick, Coin, Spawner, Cannon, Pipe, Flag }

    public readonly struct Piece
    {
        public readonly Kind Kind;
        public readonly Vector2 At;        // (x, z)
        public readonly string Enemies;    // Spawner: sequence, G = Goomba, K = Koopa (cycled)
        public readonly int Count;         // Spawner: total enemies
        public readonly float Chance;      // Pipe: chance it appears on a given play

        public Piece(Kind kind, float x, float z, string enemies = null, int count = 0, float chance = 1f)
        {
            Kind = kind; At = new Vector2(x, z); Enemies = enemies; Count = count; Chance = chance;
        }
    }

    public sealed class Design
    {
        public string Name;
        public string Theme;               // one line: what the stage teaches or tests
        public float Width = 2.5f, Depth = 4f;
        public Piece[] Pieces;
    }

    public static readonly Design[] Campaign =
    {
        new Design
        {
            Name = "1-1", Theme = "First steps: coins, a ? block between two bricks, one Goomba, the flag.",
            Pieces = Join(
                Coins(0f, -1.6f, 0f, -0.8f, 5),
                P(Kind.Question, 0f, -0.35f),
                P(Kind.Brick, -0.45f, -0.35f), P(Kind.Brick, 0.45f, -0.35f),
                Coins(-0.6f, 0.35f, 0.6f, 0.35f, 5),
                new[] { new Piece(Kind.Spawner, 0.75f, 1.1f, "G", 1) },
                Coins(0f, 0.9f, 0f, 1.3f, 3),
                P(Kind.Flag, 0f, 1.75f)),
        },
        new Design
        {
            Name = "1-2", Theme = "Koopa road: a Goomba then a Koopa, a zigzag of coins, and a warp pipe to a bonus room.",
            Pieces = Join(
                P(Kind.Question, -0.5f, -1.0f), P(Kind.Question, 0.5f, -1.0f),
                new[] { new Piece(Kind.Pipe, 0.9f, -0.5f, chance: 1f) },
                Coins(-0.6f, -0.4f, -0.3f, -0.1f, 2), Coins(0f, 0.2f, 0.6f, 0.8f, 3),
                P(Kind.Brick, -0.6f, 0.45f),
                new[] { new Piece(Kind.Spawner, -0.75f, 1.2f, "GK", 2) },
                P(Kind.Flag, 0.3f, 1.75f)),
        },
        new Design
        {
            Name = "1-3", Theme = "Bullet alley: two cannons, one firing across the path; squat or sidestep. Coins lead the way.",
            Pieces = Join(
                Coins(0f, -1.5f, 0f, 1.2f, 8),
                P(Kind.Question, 0.45f, -0.6f),
                P(Kind.Brick, -0.45f, 0.6f),
                P(Kind.Cannon, -1.0f, 1.7f), P(Kind.Cannon, 1.05f, 0.0f),
                new[] { new Piece(Kind.Spawner, 0.65f, 1.3f, "G", 1), new Piece(Kind.Pipe, -0.85f, -1.2f, chance: 0.5f) },
                P(Kind.Flag, 0f, 1.75f)),
        },
        new Design
        {
            Name = "1-4", Theme = "Fortress: everything at once — two spawners, a cannon, a ring of coins, and a warp pipe.",
            Pieces = Join(
                P(Kind.Question, -0.5f, -1.2f), P(Kind.Question, 0.5f, 0.0f),
                P(Kind.Brick, 0f, -1.2f), P(Kind.Brick, -0.5f, 0.0f),
                new[]
                {
                    new Piece(Kind.Spawner, -0.85f, 0.9f, "GKG", 3), new Piece(Kind.Spawner, 0.85f, 1.35f, "K", 1),
                    new Piece(Kind.Pipe, -0.9f, -0.45f, chance: 1f),
                },
                P(Kind.Cannon, 1.05f, -0.3f),
                Coins(-0.3f, 0.45f, 0.3f, 0.45f, 3), P(Kind.Coin, -0.3f, 0.75f), P(Kind.Coin, 0.3f, 0.75f), Coins(-0.3f, 1.05f, 0.3f, 1.05f, 3),
                P(Kind.Flag, 0f, 1.75f)),
        },
    };

    // ---- helpers for writing designs ----

    private static Piece[] P(Kind kind, float x, float z) => new[] { new Piece(kind, x, z) };

    /// <summary>A straight line of <paramref name="n"/> coins from (x0, z0) to (x1, z1).</summary>
    private static Piece[] Coins(float x0, float z0, float x1, float z1, int n)
    {
        var pieces = new Piece[n];
        for (int i = 0; i < n; i++)
        {
            float t = n == 1 ? 0f : (float)i / (n - 1);
            pieces[i] = new Piece(Kind.Coin, Mathf.Lerp(x0, x1, t), Mathf.Lerp(z0, z1, t));
        }
        return pieces;
    }

    private static Piece[] Join(params Piece[][] groups)
    {
        var all = new List<Piece>();
        foreach (var g in groups) all.AddRange(g);
        return all.ToArray();
    }
}
