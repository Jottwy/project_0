using BackroomsSurvival.Gameplay.GridWorld;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Building
{
    /// <summary>Result of snapping an aim point to a grid-wall slot.</summary>
    public readonly struct GridWallPose
    {
        /// <summary>World pivot of the wall: on the tile edge line, at the layer's floor Y.</summary>
        public readonly Vector3 Position;

        /// <summary>0 for a panel running along X (a tile's ±Z edge), 90 for one running along Z.</summary>
        public readonly float Yaw;

        /// <summary>Macro layer the wall belongs to. Floor Y is <c>Layer * LayerHeight</c>.</summary>
        public readonly int Layer;

        public GridWallPose(Vector3 position, float yaw, int layer)
        {
            Position = position;
            Yaw = yaw;
            Layer = layer;
        }
    }

    /// <summary>
    /// Snaps an arbitrary aim point onto the world tile grid so a player-built wall lands on the
    /// SAME slots the procedural walls use. No <c>MonoBehaviour</c>, no scene, no physics — pure
    /// function, headless-testable (same discipline as <c>ChunkLootRoll</c>).
    ///
    /// The contract it mirrors is <c>GridChunkBuilder.WallEdgeTable</c>: a panel sits centred on a
    /// tile edge, pivot on the floor, yaw 0 when it runs along X and 90 when it runs along Z. Tile
    /// centres are at <c>(t + 0.5) * TileSize</c> in GLOBAL coordinates (a chunk origin is
    /// <c>chunk * 50</c>, an exact multiple of the 5 m tile), so this needs no chunk lookup: the
    /// grid is world-aligned everywhere.
    /// </summary>
    public static class GridWallSnap
    {
        /// <summary>Side of one render tile (5 m) — the length of a wall panel.</summary>
        public const float TileSize = GridVisualConstants.TileSize;

        /// <summary>Vertical separation between macro layers (4 m) — also the wall height.</summary>
        public const float LayerHeight = GridConstants.LayerHeight;

        /// <summary>
        /// Macro layer of a surface hit at world height <paramref name="y"/>.
        ///
        /// The two cases genuinely differ and collapsing them is a bug: a FLOOR sits within a few
        /// centimetres of <c>layer * LayerHeight</c> (the slab is 0.08 m thick, centred), so
        /// rounding lands on it exactly. A VERTICAL surface is hit anywhere across the 4 m of its
        /// own layer, so only flooring the quotient attributes it to the layer it belongs to —
        /// rounding would push any hit above the halfway mark up to the layer above.
        /// </summary>
        public static int LayerAt(float y, bool upwardSurface) => upwardSurface
            ? Mathf.RoundToInt(y / LayerHeight)
            : Mathf.FloorToInt(y / LayerHeight);

        /// <summary>
        /// Snaps <paramref name="hit"/> to the nearest edge of the tile containing it.
        ///
        /// Ties (the exact tile centre, where all four edges are 2.5 m away) resolve in the fixed
        /// order W → E → S → N. Arbitrary but deterministic on purpose: every client must derive
        /// the same pose from the same aim point, or the host's pose-cell dedup would see two
        /// different cells for what the players meant as one wall.
        /// </summary>
        public static GridWallPose Snap(Vector3 hit, bool upwardSurface)
        {
            int layer = LayerAt(hit.y, upwardSurface);
            float floorY = layer * LayerHeight;

            // FloorToInt, not a cast: a cast truncates toward zero and would fold tile -1 into
            // tile 0 for every negative coordinate, mirroring the whole grid across the origin.
            int tx = Mathf.FloorToInt(hit.x / TileSize);
            int tz = Mathf.FloorToInt(hit.z / TileSize);

            // Position within the tile, in [0, TileSize).
            float u = hit.x - tx * TileSize;
            float v = hit.z - tz * TileSize;

            float centreX = (tx + 0.5f) * TileSize;
            float centreZ = (tz + 0.5f) * TileSize;

            // West edge: constant X, panel runs along Z.
            float best = u;
            var position = new Vector3(tx * TileSize, floorY, centreZ);
            float yaw = 90f;

            float east = TileSize - u;
            if (east < best)
            {
                best = east;
                position = new Vector3((tx + 1) * TileSize, floorY, centreZ);
                yaw = 90f;
            }

            // South edge: constant Z, panel runs along X.
            if (v < best)
            {
                best = v;
                position = new Vector3(centreX, floorY, tz * TileSize);
                yaw = 0f;
            }

            float north = TileSize - v;
            if (north < best)
            {
                position = new Vector3(centreX, floorY, (tz + 1) * TileSize);
                yaw = 0f;
            }

            return new GridWallPose(position, yaw, layer);
        }
    }
}
