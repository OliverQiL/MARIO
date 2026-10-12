using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Pixel-art textures for game content, drawn as data: each texture is rows
/// of palette characters (top row first). Saved as point-filtered PNGs so the
/// pixels stay crisp in AR. Original designs in the style of the classic
/// blocks; used by GameContentSetup.
/// </summary>
public static class PixelArt
{
    private const string TextureDir = "Assets/Textures";

    // L light bevel, S shadow bevel, Y gold, R rivet, Q "?" glyph, G glyph shadow
    private static readonly string[] QuestionRows =
    {
        "LLLLLLLLLLLLLLLS",
        "LYYYYYYYYYYYYYYS",
        "LYRYYYYYYYYYYRYS",
        "LYYYYYQQQQYYYYYS",
        "LYYYYQQGGQQYYYYS",
        "LYYYYQQGYQQGYYYS",
        "LYYYYYGGYQQGYYYS",
        "LYYYYYYYQQGGYYYS",
        "LYYYYYYQQGGYYYYS",
        "LYYYYYYQQGYYYYYS",
        "LYYYYYYYGGYYYYYS",
        "LYYYYYYQQYYYYYYS",
        "LYYYYYYQQGYYYYYS",
        "LYRYYYYYGGYYYRYS",
        "LYYYYYYYYYYYYYYS",
        "SSSSSSSSSSSSSSSS",
    };

    // Same frame as the ? block, empty face: L light, S shadow, Y fill, R rivet
    private static readonly string[] SpentRows =
    {
        "LLLLLLLLLLLLLLLS",
        "LYYYYYYYYYYYYYYS",
        "LYRYYYYYYYYYYRYS",
        "LYYYYYYYYYYYYYYS",
        "LYYYYYYYYYYYYYYS",
        "LYYYYYYYYYYYYYYS",
        "LYYYYYYYYYYYYYYS",
        "LYYYYYYYYYYYYYYS",
        "LYYYYYYYYYYYYYYS",
        "LYYYYYYYYYYYYYYS",
        "LYYYYYYYYYYYYYYS",
        "LYYYYYYYYYYYYYYS",
        "LYYYYYYYYYYYYYYS",
        "LYRYYYYYYYYYYRYS",
        "LYYYYYYYYYYYYYYS",
        "SSSSSSSSSSSSSSSS",
    };

    // H brick top highlight, B brick, M mortar (running bond)
    private static readonly string[] BrickRows =
    {
        "HHHHHHHMHHHHHHHM",
        "BBBBBBBMBBBBBBBM",
        "BBBBBBBMBBBBBBBM",
        "MMMMMMMMMMMMMMMM",
        "HHHMHHHHHHHMHHHH",
        "BBBMBBBBBBBMBBBB",
        "BBBMBBBBBBBMBBBB",
        "MMMMMMMMMMMMMMMM",
        "HHHHHHHMHHHHHHHM",
        "BBBBBBBMBBBBBBBM",
        "BBBBBBBMBBBBBBBM",
        "MMMMMMMMMMMMMMMM",
        "HHHMHHHHHHHMHHHH",
        "BBBMBBBBBBBMBBBB",
        "BBBMBBBBBBBMBBBB",
        "MMMMMMMMMMMMMMMM",
    };

    public static Texture2D QuestionBlock() => Save("QuestionBlock", QuestionRows, new Dictionary<char, Color>
    {
        ['L'] = Hex("FFD98C"), ['S'] = Hex("8C4A0A"), ['Y'] = Hex("F7A81A"),
        ['R'] = Hex("5A2E05"), ['Q'] = Hex("FFF7DC"), ['G'] = Hex("9C5410"),
    });

    public static Texture2D SpentBlock() => Save("SpentBlock", SpentRows, new Dictionary<char, Color>
    {
        ['L'] = Hex("B8794A"), ['S'] = Hex("4A2810"), ['Y'] = Hex("8C5428"), ['R'] = Hex("3A1E08"),
    });

    /// <summary>Bonus-room wall: the brick pattern in underground blue.</summary>
    public static Texture2D UndergroundBrick() => Save("UndergroundBrick", BrickRows, new Dictionary<char, Color>
    {
        ['H'] = Hex("5A8CD8"), ['B'] = Hex("2E5AA8"), ['M'] = Hex("0E1E40"),
    });

    /// <summary>Bonus-room floor tile: the block frame in dark underground blue.</summary>
    public static Texture2D UndergroundFloor() => Save("UndergroundFloor", SpentRows, new Dictionary<char, Color>
    {
        ['L'] = Hex("3A64B0"), ['S'] = Hex("0A1430"), ['Y'] = Hex("1E3C78"), ['R'] = Hex("0A1430"),
    });

    /// <summary>Grey castle stone: the brick pattern in greys.</summary>
    public static Texture2D CastleStone() => Save("CastleStone", BrickRows, new Dictionary<char, Color>
    {
        ['B'] = Hex("8C8C96"), ['M'] = Hex("3C3C46"), ['H'] = Hex("B4B4BE"),
    });

    public static Texture2D Brick() => Save("Brick", BrickRows, new Dictionary<char, Color>
    {
        ['H'] = Hex("E0784A"), ['B'] = Hex("B8481E"), ['M'] = Hex("401C0C"),
    });

    /// <summary>
    /// Warp pipe side: wraps once around a cylinder (u = around), so each
    /// column is a vertical band: dark edges, mid green, a bright highlight stripe.
    /// </summary>
    public static Texture2D PipeSide()
    {
        const string band = "DDMMLLWWLMMMMMDD";
        return Save("PipeSide", new[] { band, band, band, band }, new Dictionary<char, Color>
        {
            ['D'] = Hex("0A5A14"), ['M'] = Hex("1E9E2E"), ['L'] = Hex("5AD05A"), ['W'] = Hex("B4F5A0"),
        });
    }

    // HUD icons. K outline, '.' transparent.
    private static readonly string[] HeartRows =
    {
        "................",
        "..KKKK....KKKK..",
        ".KRRRRK..KRRRRK.",
        "KRRWWRRKKRRRRRRK",
        "KRWWRRRRRRRRRRRK",
        "KRWRRRRRRRRRRRRK",
        "KRRRRRRRRRRRRRRK",
        ".KRRRRRRRRRRRRK.",
        "..KRRRRRRRRRRK..",
        "...KRRRRRRRRK...",
        "....KRRRRRRK....",
        ".....KRRRRK.....",
        "......KRRK......",
        ".......KK.......",
        "................",
        "................",
    };

    private static readonly string[] FlowerIconRows =
    {
        "....KKKKKKKK....",
        "..KKRRRRRRRRKK..",
        ".KRRRYYYYYYRRRK.",
        "KRRYYWWWWWWYYRRK",
        "KRYYWWKWWKWWYYRK",
        "KRYYWWKWWKWWYYRK",
        "KRRYYWWWWWWYYRRK",
        ".KRRRYYYYYYRRRK.",
        "..KKRRRRRRRRKK..",
        "....KKKGGKKK....",
        ".KKK...KGGK..KKK",
        "KGGGK..KGGK.KGGG",
        "KGGGGK.KGGKKGGGG",
        ".KGGGGKKGGKGGGGK",
        "..KKGGGGGGGGGKK.",
        "....KKKKKKKKKK..",
    };

    /// <summary>A plain white sprite, for UI bars (a Filled Image needs a sprite).</summary>
    public static Sprite WhitePixel() => SaveSprite("WhitePixel", new[] { "WWWW", "WWWW", "WWWW", "WWWW" }, new Dictionary<char, Color> { ['W'] = Color.white });

    private static readonly string[] CoinIconRows =
    {
        "................",
        ".....KKKKKK.....",
        "....KYYYYYYK....",
        "...KYYWWYYYOK...",
        "...KYWYYYYYOK...",
        "..KYYWYKKYYYOK..",
        "..KYYWYKKYYYOK..",
        "..KYYYYKKYYYOK..",
        "..KYYYYKKYYYOK..",
        "..KYYYYKKYYYOK..",
        "..KYYYYKKYYYOK..",
        "...KYYYYYYYOK...",
        "...KYYYYYYOOK...",
        "....KOOOOOOK....",
        ".....KKKKKK.....",
        "................",
    };

    public static Sprite CoinIcon() => SaveSprite("CoinIcon", CoinIconRows, new Dictionary<char, Color>
    {
        ['.'] = Color.clear, ['K'] = Hex("1A0A0A"), ['Y'] = Hex("FFC81E"), ['W'] = Hex("FFF5C0"), ['O'] = Hex("C8780A"),
    });

    public static Sprite HeartIcon() => SaveSprite("HeartIcon", HeartRows, new Dictionary<char, Color>
    {
        ['.'] = Color.clear, ['K'] = Hex("1A0A0A"), ['R'] = Hex("F03C3C"), ['W'] = Hex("FFD0D0"),
    });

    public static Sprite FlowerIcon() => SaveSprite("FlowerIcon", FlowerIconRows, new Dictionary<char, Color>
    {
        ['.'] = Color.clear, ['K'] = Hex("1A0A0A"), ['R'] = Hex("F0461E"), ['Y'] = Hex("FFC81E"),
        ['W'] = Hex("FFF7DC"), ['G'] = Hex("28A03C"),
    });

    private static readonly string[] ShellIconRows =
    {
        "................",
        "................",
        "......KKKK......",
        "....KKGGGGKK....",
        "...KGGWGGGGGK...",
        "..KGGWGGLLGGGK..",
        "..KGWGGLLLLGGK..",
        ".KGGGGGLLLLGGGK.",
        ".KGGLLGGLLGGLGK.",
        ".KGLLLLGGGGLLGK.",
        ".KWWWWWWWWWWWWK.",
        ".KYYYYYYYYYYYYK.",
        "..KYYYYYYYYYYK..",
        "...KKKKKKKKKK...",
        "................",
        "................",
    };

    /// <summary>HUD icon for a held Koopa shell: green plates, white rim, yellow belly.</summary>
    public static Sprite ShellIcon() => SaveSprite("ShellIcon", ShellIconRows, new Dictionary<char, Color>
    {
        ['.'] = Color.clear, ['K'] = Hex("1A0A0A"), ['G'] = Hex("1E8C32"), ['L'] = Hex("3CB44A"),
        ['W'] = Hex("FFFFFF"), ['Y'] = Hex("FFE6A0"),
    });

    /// <summary>
    /// Soft round shadow (black, alpha falling off from the centre) for blob
    /// shadows under characters, which ground them on the real floor in AR.
    /// </summary>
    public static Texture2D BlobShadow()
    {
        const int size = 32;
        var rows = new string[size];
        var palette = new Dictionary<char, Color>();
        for (int i = 0; i <= 9; i++) palette[(char)('0' + i)] = new Color(0f, 0f, 0f, i / 9f * 0.55f);
        for (int y = 0; y < size; y++)
        {
            var chars = new char[size];
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                chars[x] = (char)('0' + Mathf.RoundToInt(Mathf.SmoothStep(0f, 1f, a) * 9f));
            }
            rows[y] = new string(chars);
        }
        var tex = Save("BlobShadow", rows, palette);
        // Smooth and clamped, unlike the pixel art.
        var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex));
        importer.filterMode = FilterMode.Bilinear;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.alphaIsTransparency = true;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GetAssetPath(tex));
    }

    /// <summary>
    /// Scan grid tile (white, tinted by the material): a one-pixel line along two
    /// edges, so tiled it draws a grid, over a faint fill. Plane UVs are in metres.
    /// </summary>
    public static Texture2D ScanGrid()
    {
        var rows = new string[16];
        for (int r = 0; r < 16; r++)
        {
            var chars = new char[16];
            for (int c = 0; c < 16; c++)
                chars[c] = r == 0 && c == 0 ? 'C' : r == 0 || c == 0 ? 'E' : 'F';
            rows[r] = new string(chars);
        }
        return Save("ScanGrid", rows, new Dictionary<char, Color>
        {
            ['C'] = new Color(1f, 1f, 1f, 1f), ['E'] = new Color(1f, 1f, 1f, 0.8f), ['F'] = new Color(1f, 1f, 1f, 0.12f),
        });
    }

    /// <summary>Koopa shell scutes: rounded plates on an offset grid, with dark seams.</summary>
    public static Texture2D KoopaShell()
    {
        const int size = 32;
        var baseCol = Hex("1E8C32");
        var plate = Hex("3CB44A");
        var seam = Hex("0E5A1C");
        var rows = new string[size];
        var palette = new Dictionary<char, Color> { ['b'] = baseCol, ['p'] = plate, ['s'] = seam };
        for (int y = 0; y < size; y++)
        {
            var chars = new char[size];
            for (int x = 0; x < size; x++)
            {
                int row = y / 8;
                float cx = (x + (row % 2 == 1 ? 4 : 0)) % 8 - 3.5f;
                float cy = y % 8 - 3.5f;
                float d = Mathf.Sqrt(cx * cx + cy * cy);
                chars[x] = d < 2.6f ? 'p' : d < 3.4f ? 'b' : 's';
            }
            rows[y] = new string(chars);
        }
        return Save("KoopaShell", rows, palette);
    }

    private static Sprite SaveSprite(string name, string[] rows, Dictionary<char, Color> palette)
    {
        var path = AssetDatabase.GetAssetPath(Save(name, rows, palette));
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Point;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    private static Texture2D Save(string name, string[] rows, Dictionary<char, Color> palette)
    {
        int h = rows.Length, w = rows[0].Length;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
                tex.SetPixel(c, h - 1 - r, palette[rows[r][c]]); // rows are top-first; texture y is bottom-up

        Directory.CreateDirectory(TextureDir);
        var path = $"{TextureDir}/{name}.png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.filterMode = FilterMode.Point;
        importer.mipmapEnabled = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    private static Color Hex(string rgb) => ColorUtility.TryParseHtmlString("#" + rgb, out var c) ? c : Color.magenta;
}
