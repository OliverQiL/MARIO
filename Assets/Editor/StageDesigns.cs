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
/// height, everything else on the floor. World 1 stages are 2.5 × 4 m
/// (x ±1.25, z ±2.0); World 2 stages are 3 × 5 m (x ±1.5, z ±2.5), and
/// shrink to fit a smaller room (LevelGenerator's campaign scales). Keep
/// pieces about 0.25 m inside the edge.
///
/// Each stage has a scenery theme: decoration (hills, bushes, clouds, a
/// castle; underground pillars; castle walls and lava) built around and
/// above the play area, never in the way. Stairs and tall pipes add height
/// to walk round. The last stage (2-4) is Bowser's castle: beat the boss to
/// unlock the axe, and reaching the axe ends the game (GAME CLEAR).
///
/// To add or change a stage: edit it here, then run Tools ▸ MARIO ▸ Build
/// Game Content. Each stage is a little harder than the last (Difficulty).
/// </summary>
public static class StageDesigns
{
    public enum Kind { Question, Brick, Coin, Spawner, Cannon, Pipe, Flag, Stairs, TallPipe, Boss, Axe }

    public enum Scenery { Overworld, Underground, Sky, Castle }

    public readonly struct Piece
    {
        public readonly Kind Kind;
        public readonly Vector2 At;        // (x, z)
        public readonly string Enemies;    // Spawner: sequence, G = Goomba, K = Koopa (cycled)
        public readonly int Count;         // Spawner: total enemies. Stairs / TallPipe: height in blocks (0.25 m)
        public readonly float Chance;      // Pipe: chance it appears on a given play

        public Piece(Kind kind, float x, float z, string enemies = null, int count = 0, float chance = 1f)
        {
            Kind = kind; At = new Vector2(x, z); Enemies = enemies; Count = count; Chance = chance;
        }
    }

    public sealed class Design
    {
        public string Name;
        public string Title;               // shown on the stage's intro card, e.g. "SHELL HILLS"
        public string Tip;                 // one short hint under the title
        public string Theme;               // one line: what the stage teaches or tests
        public float Width = 2.5f, Depth = 4f;
        public Scenery Scenery = Scenery.Overworld;
        public Piece[] Pieces;
    }

    public static readonly Design[] Campaign =
    {
        new Design
        {
            Name = "1-1", Title = "FIRST STEPS", Tip = "Jump under ? blocks. Stomp Goombas.",
            Theme = "First steps: coins, a ? block between two bricks, one Goomba, the flag.",
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
            Name = "1-2", Title = "KOOPA ROAD", Tip = "Stomp a Koopa twice - or clap to grab its shell.",
            Theme = "Koopa road: a Goomba then a Koopa, a zigzag of coins, and a warp pipe to a bonus room.",
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
            Name = "1-3", Title = "BULLET ALLEY", Tip = "Squat or sidestep the Bullet Bills.",
            Theme = "Bullet alley: two cannons, one firing across the path; squat or sidestep. Coins lead the way.",
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
            Name = "1-4", Title = "FORTRESS", Tip = "Punch bricks. Throw fireballs with a flower.",
            Theme = "Fortress: everything at once — two spawners, a cannon, a ring of coins, and a warp pipe.",
            Scenery = Scenery.Castle,
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

        // ---- World 2: bigger (3 × 5 m), taller, and it ends in Bowser's castle ----

        new Design
        {
            Name = "2-1", Title = "SHELL HILLS", Tip = "Clap a shell, throw it down the Goomba line.",
            Theme = "Shell hills: Koopas to stomp, pick up (clap) and throw down a line of Goombas; a staircase to the flag.",
            Width = 3f, Depth = 5f,
            Pieces = Join(
                Coins(0f, -2.1f, 0f, -1.5f, 4),
                P(Kind.Question, -0.4f, -1.2f), P(Kind.Brick, 0f, -1.2f), P(Kind.Question, 0.4f, -1.2f),
                new[]
                {
                    new Piece(Kind.Spawner, -1.0f, -0.2f, "K", 1),
                    new Piece(Kind.Spawner, 0.3f, 1.0f, "G", 3),
                    new Piece(Kind.TallPipe, 1.15f, -0.6f, count: 3), new Piece(Kind.TallPipe, -1.2f, 0.7f, count: 2),
                },
                Arc(-0.7f, 0.1f, 0.7f, 0.1f, 6),
                P(Kind.Brick, -0.35f, 0.6f), P(Kind.Brick, 0.35f, 0.6f),
                Stairs(0.45f, 1.55f, 0.25f, 0f, new[] { 1, 2, 3 }),
                Stairs(-0.45f, 1.55f, -0.25f, 0f, new[] { 1, 2, 3 }),
                P(Kind.Flag, 0f, 2.2f)),
        },
        new Design
        {
            Name = "2-2", Title = "UNDERGROUND", Tip = "Weave between the pillars. Punch the bricks.",
            Theme = "Underground: pillars to weave between, Goomba waves, bricks to punch and a warp pipe to coins.",
            Width = 3f, Depth = 5f, Scenery = Scenery.Underground,
            Pieces = Join(
                Coins(-0.9f, -2.0f, 0.9f, -2.0f, 6),
                P(Kind.Brick, -0.5f, -1.3f), P(Kind.Brick, -0.25f, -1.3f), P(Kind.Question, 0.15f, -1.3f), P(Kind.Brick, 0.5f, -1.3f),
                new[]
                {
                    new Piece(Kind.Stairs, -0.8f, -0.5f, count: 2), new Piece(Kind.Stairs, 0.8f, -0.5f, count: 2),
                    new Piece(Kind.Spawner, 0f, 0.4f, "GGK", 3),
                    new Piece(Kind.Pipe, 1.1f, 0.6f, chance: 1f),
                    new Piece(Kind.Stairs, -0.8f, 1.2f, count: 3), new Piece(Kind.Stairs, 0.8f, 1.2f, count: 3),
                },
                Coins(0f, -0.4f, 0f, 1.6f, 6),
                P(Kind.Question, -0.4f, 1.0f),
                P(Kind.Flag, 0f, 2.2f)),
        },
        new Design
        {
            Name = "2-3", Title = "SKY ROAD", Tip = "Cannons on both sides - keep moving.",
            Theme = "Sky road: cannons on both sides, a Koopa for a shell, coin arcs and tall pipes.",
            Width = 3f, Depth = 5f, Scenery = Scenery.Sky,
            Pieces = Join(
                Arc(-0.6f, -1.9f, 0.6f, -1.9f, 5),
                P(Kind.Question, 0f, -1.1f),
                P(Kind.Cannon, -1.25f, -0.5f), P(Kind.Cannon, 1.25f, 0.9f),
                new[]
                {
                    new Piece(Kind.TallPipe, 0.6f, -0.4f, count: 4), new Piece(Kind.TallPipe, -0.6f, 0.5f, count: 3),
                    new Piece(Kind.Spawner, 0.2f, 1.3f, "KG", 2),
                    new Piece(Kind.Pipe, -1.1f, 1.6f, chance: 0.5f),
                },
                P(Kind.Brick, 0.15f, 0.2f), P(Kind.Brick, -0.15f, 0.2f),
                Arc(-0.5f, 0.9f, 0.5f, 0.9f, 5),
                P(Kind.Flag, 0.4f, 2.2f)),
        },
        new Design
        {
            Name = "2-4", Title = "BOWSER'S CASTLE", Tip = "Punch, fireball or shell him (a shell counts 2). Squat his fire!",
            Theme = "Bowser's castle (final): power up, get a shell, beat Bowser (5 hits; dodge his fire), then reach the axe.",
            Width = 3f, Depth = 5f, Scenery = Scenery.Castle,
            Pieces = Join(
                P(Kind.Question, -0.45f, -1.6f), P(Kind.Question, 0.45f, -1.6f),
                Coins(-0.3f, -2.1f, 0.3f, -2.1f, 3),
                new[]
                {
                    new Piece(Kind.Spawner, -1.0f, -0.3f, "K", 2),
                    new Piece(Kind.Stairs, 1.1f, -0.7f, count: 2), new Piece(Kind.Stairs, -1.15f, 0.6f, count: 2),
                },
                P(Kind.Brick, 0.6f, -0.2f),
                P(Kind.Boss, 0f, 1.3f),
                P(Kind.Axe, 0f, 2.2f)),
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

    /// <summary>
    /// A line of coins that rises and falls (an arc you walk under and
    /// through): the ends at waist height, the middle higher. Encoded as coins
    /// whose Count is the extra height in centimetres.
    /// </summary>
    private static Piece[] Arc(float x0, float z0, float x1, float z1, int n)
    {
        var pieces = new Piece[n];
        for (int i = 0; i < n; i++)
        {
            float t = n == 1 ? 0.5f : (float)i / (n - 1);
            int lift = Mathf.RoundToInt(Mathf.Sin(t * Mathf.PI) * 35f);
            pieces[i] = new Piece(Kind.Coin, Mathf.Lerp(x0, x1, t), Mathf.Lerp(z0, z1, t), count: lift);
        }
        return pieces;
    }

    /// <summary>A staircase: columns of stair blocks, each <paramref name="dx"/>, <paramref name="dz"/> from the last, with the given heights.</summary>
    private static Piece[] Stairs(float x, float z, float dx, float dz, int[] heights)
    {
        var pieces = new Piece[heights.Length];
        for (int i = 0; i < heights.Length; i++)
            pieces[i] = new Piece(Kind.Stairs, x + dx * i, z + dz * i, count: heights[i]);
        return pieces;
    }

    private static Piece[] Join(params Piece[][] groups)
    {
        var all = new List<Piece>();
        foreach (var g in groups) all.AddRange(g);
        return all.ToArray();
    }
}
