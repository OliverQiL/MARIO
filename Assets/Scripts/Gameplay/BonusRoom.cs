using UnityEngine;

/// <summary>
/// A warp pipe's bonus room (Docs/GAME_DESIGN.md, maps): a temporary
/// "underground" area placed exactly where the stage was, so it always fits
/// the floor that stage was placed on. It lays itself out to the stage's
/// footprint: a blue-brick floor, a low brick wall round the edge, a grid of
/// coins, and an exit pipe at the far end that takes the player back.
/// LevelGenerator creates it and calls Build with the stage's footprint.
/// </summary>
public class BonusRoom : MonoBehaviour
{
    [SerializeField] private Coin coinPrefab;
    [SerializeField] private WarpPipe exitPipePrefab;
    [SerializeField] private GameObject wallBlockPrefab;
    [SerializeField] private Material floorMaterial;
    [Tooltip("Floor tiles per metre.")]
    [SerializeField] private float floorTilesPerMetre = 2f;
    [SerializeField] private float coinSpacing = 0.4f;
    [Tooltip("Coins and the exit stay this far inside the edge (metres).")]
    [SerializeField] private float inset = 0.35f;
    [SerializeField] private float wallBlockSize = 0.25f;

    public int CoinCount { get; private set; }
    public WarpPipe ExitPipe { get; private set; }

    /// <summary>Lays the room out on a width (local X) × depth (local Z) footprint centred on this transform.</summary>
    public void Build(float width, float depth)
    {
        // Floor: one quad, tiled.
        if (floorMaterial != null)
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
            floor.name = "Floor";
            InteractableBase.SafeDestroy(floor.GetComponent<Collider>());
            floor.transform.SetParent(transform, false);
            floor.transform.localPosition = new Vector3(0f, 0.005f, 0f);
            floor.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            floor.transform.localScale = new Vector3(width, depth, 1f);
            var r = floor.GetComponent<MeshRenderer>();
            r.sharedMaterial = floorMaterial;
            var block = new MaterialPropertyBlock();
            block.SetVector("_BaseMap_ST", new Vector4(width * floorTilesPerMetre, depth * floorTilesPerMetre, 0f, 0f));
            r.SetPropertyBlock(block);
        }

        // A low wall of blocks round the edge.
        if (wallBlockPrefab != null)
        {
            float hw = width / 2f - wallBlockSize / 2f, hd = depth / 2f - wallBlockSize / 2f;
            for (float x = -hw; x <= hw + 0.001f; x += wallBlockSize)
            {
                Wall(new Vector3(x, 0f, -hd));
                Wall(new Vector3(x, 0f, hd));
            }
            for (float z = -hd + wallBlockSize; z <= hd - wallBlockSize + 0.001f; z += wallBlockSize)
            {
                Wall(new Vector3(-hw, 0f, z));
                Wall(new Vector3(hw, 0f, z));
            }
        }

        // Exit pipe at the far end.
        var exitAt = new Vector3(0f, 0f, depth / 2f - inset - 0.15f);
        if (exitPipePrefab != null)
        {
            ExitPipe = Instantiate(exitPipePrefab, transform);
            ExitPipe.transform.localPosition = exitAt;
        }

        // Coins on a grid, leaving room round the exit.
        CoinCount = 0;
        if (coinPrefab == null) return;
        float cx = width / 2f - inset - wallBlockSize, cz = depth / 2f - inset - wallBlockSize;
        for (float z = -cz; z <= cz + 0.001f; z += coinSpacing)
            for (float x = -cx; x <= cx + 0.001f; x += coinSpacing)
            {
                var p = new Vector3(x, 0f, z);
                if ((p - exitAt).magnitude < 0.5f) continue;
                var coin = Instantiate(coinPrefab, transform);
                coin.transform.localPosition = p; // EyeHeightPlacement lifts it to waist height
                CoinCount++;
            }
    }

    private void Wall(Vector3 localPos)
    {
        var block = Instantiate(wallBlockPrefab, transform);
        block.transform.localPosition = localPos + Vector3.up * (wallBlockSize / 2f);
    }
}
