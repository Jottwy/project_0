using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Wall meshing for Wall cells (§6 of BACKROOMS_GRID_SYSTEM.md).
    ///
    /// Pairs of x-adjacent Wall cells with matching north/south insets fuse
    /// into one 5 m box (TileSize); leftover cells emit one 2.5 m box each.
    /// Every box is 4 side quads + top cap, all accumulated into ONE mesh
    /// per chunk (1 MeshRenderer, 2 submeshes).
    /// Exposed sides retract WallInset metres inward (thin partition); sides
    /// toward other Wall cells or the chunk border extend to the cell edge so
    /// seams between adjacent walls and with neighbouring chunks stay closed.
    ///
    /// Pure and allocation-light: no scene access, fully testable in EditMode.
    /// </summary>
    public static class WallGreedyMesher
    {
        // WallThickness and the wall inset now live in GridVisualConstants
        // (ADR-001). NOT part of the Rust contract: the cell grid and backend
        // collision still treat the whole Wall cell as solid; Fase 4 must model
        // wall collision as this thin slab (pending ADR).

        // Per-side inset flags for ComputeWallInsets results.
        public const byte InsetWest  = 1;  // -x
        public const byte InsetEast  = 2;  // +x
        public const byte InsetSouth = 4;  // -z
        public const byte InsetNorth = 8;  // +z

        /// <summary>
        /// Render height (in ceiling units) for every Wall cell of a chunk:
        /// a fixed 2 units (4 m with CellHeight), ignoring Rust ceiling data.
        /// Non-wall cells get 0.
        /// </summary>
        public static int[] ComputeWallHeights(GridCell[] cells, int size)
        {
            var heights = new int[cells.Length];
            for (int i = 0; i < cells.Length; i++)
                heights[i] = cells[i].Kind == GridCellType.Wall ? 2 : 0;
            return heights;
        }

        /// <summary>
        /// Per-side inset flags for every Wall cell: a side is flagged when its
        /// in-chunk neighbour is anything but Wall, so the face there retracts
        /// into a thin partition. Sides toward other Wall cells AND toward the
        /// chunk border stay unflagged (the box reaches the cell border to join
        /// the neighbouring wall flush — including the one in the adjacent
        /// chunk, which this builder cannot see). Non-wall cells get 0.
        /// </summary>
        public static byte[] ComputeWallInsets(GridCell[] cells, int size)
        {
            var insets = new byte[cells.Length];
            for (int z = 0; z < size; z++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = z * size + x;
                    if (cells[i].Kind != GridCellType.Wall)
                        continue;

                    byte f = 0;
                    if (x > 0          && cells[i - 1].Kind    != GridCellType.Wall) f |= InsetWest;
                    if (x < size - 1   && cells[i + 1].Kind    != GridCellType.Wall) f |= InsetEast;
                    if (z > 0          && cells[i - size].Kind  != GridCellType.Wall) f |= InsetSouth;
                    if (z < size - 1   && cells[i + size].Kind  != GridCellType.Wall) f |= InsetNorth;
                    insets[i] = f;
                }
            }
            return insets;
        }

        /// <summary>
        /// Build one mesh for all Wall cells of a chunk: pairs of x-adjacent
        /// Wall cells with matching north/south insets fuse into one box
        /// (5 m = TileSize); leftovers of odd runs and isolated cells emit
        /// per-cell boxes. Each box is 4 side quads + 1 top cap, all in a
        /// single vertex buffer.
        /// Submesh 0 = side faces (wall material).
        /// Submesh 1 = top caps (ceiling material, coplanar with Ceiling
        /// panels so the seam shades identically and never flickers).
        /// UVs are in metres so materials tile at constant world density.
        /// </summary>
        public static Mesh BuildChunkMesh(GridCell[] cells, int size,
                                          int[] heights, byte[] insets)
        {
            float cellSize = GridConstants.CellSize;
            var vertices = new List<Vector3>();
            var normals  = new List<Vector3>();
            var uvs      = new List<Vector2>();
            var sideTris = new List<int>();
            var topTris  = new List<int>();

            for (int z = 0; z < size; z++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = z * size + x;
                    if (cells[i].Kind != GridCellType.Wall || heights[i] == 0)
                        continue;

                    byte ins = insets[i];
                    int span = 1;

                    // Fuse with the +x neighbour when it is a Wall of the same
                    // height whose north/south insets match: the pair renders
                    // as one 5 m tile. The shared boundary carries no east/west
                    // inset by construction (its neighbour is a Wall).
                    if (x + 1 < size
                        && cells[i + 1].Kind == GridCellType.Wall
                        && heights[i + 1] == heights[i]
                        && (insets[i + 1] & (InsetSouth | InsetNorth))
                           == (ins & (InsetSouth | InsetNorth)))
                        span = 2;

                    byte insEnd = insets[i + span - 1];
                    float x0 = x * cellSize + ((ins & InsetWest)  != 0 ? GridVisualConstants.WallInset : 0f);
                    float z0 = z * cellSize + ((ins & InsetSouth) != 0 ? GridVisualConstants.WallInset : 0f);
                    float x1 = (x + span) * cellSize - ((insEnd & InsetEast)  != 0 ? GridVisualConstants.WallInset : 0f);
                    float z1 = (z + 1) * cellSize - ((ins & InsetNorth) != 0 ? GridVisualConstants.WallInset : 0f);
                    float y1 = heights[i] * GridVisualConstants.CellHeight;

                    // South (-z), north (+z), west (-x), east (+x).
                    AddQuad(vertices, normals, uvs, sideTris,
                        new Vector3(x0, 0, z0), new Vector3(x1, 0, z0),
                        new Vector3(x1, y1, z0), new Vector3(x0, y1, z0),
                        Vector3.back, x0, x1, y1);
                    AddQuad(vertices, normals, uvs, sideTris,
                        new Vector3(x1, 0, z1), new Vector3(x0, 0, z1),
                        new Vector3(x0, y1, z1), new Vector3(x1, y1, z1),
                        Vector3.forward, x0, x1, y1);
                    AddQuad(vertices, normals, uvs, sideTris,
                        new Vector3(x0, 0, z1), new Vector3(x0, 0, z0),
                        new Vector3(x0, y1, z0), new Vector3(x0, y1, z1),
                        Vector3.left, z0, z1, y1);
                    AddQuad(vertices, normals, uvs, sideTris,
                        new Vector3(x1, 0, z0), new Vector3(x1, 0, z1),
                        new Vector3(x1, y1, z1), new Vector3(x1, y1, z0),
                        Vector3.right, z0, z1, y1);
                    AddTopQuad(vertices, normals, uvs, topTris, x0, z0, x1, z1, y1);

                    x += span - 1; // skip the cell consumed by the fusion
                }
            }

            var mesh = new Mesh
            {
                name = "WallChunkMesh",
                indexFormat = vertices.Count > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(sideTris, 0);
            mesh.SetTriangles(topTris, 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddQuad(List<Vector3> v, List<Vector3> n, List<Vector2> uv,
            List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            Vector3 normal, float u0, float u1, float height)
        {
            int baseIndex = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            for (int i = 0; i < 4; i++)
                n.Add(normal);
            uv.Add(new Vector2(u0, 0));
            uv.Add(new Vector2(u1, 0));
            uv.Add(new Vector2(u1, height));
            uv.Add(new Vector2(u0, height));
            t.Add(baseIndex); t.Add(baseIndex + 2); t.Add(baseIndex + 1);
            t.Add(baseIndex); t.Add(baseIndex + 3); t.Add(baseIndex + 2);
        }

        private static void AddTopQuad(List<Vector3> v, List<Vector3> n, List<Vector2> uv,
            List<int> t, float x0, float z0, float x1, float z1, float y)
        {
            int baseIndex = v.Count;
            v.Add(new Vector3(x0, y, z0));
            v.Add(new Vector3(x1, y, z0));
            v.Add(new Vector3(x1, y, z1));
            v.Add(new Vector3(x0, y, z1));
            for (int i = 0; i < 4; i++)
                n.Add(Vector3.up);
            uv.Add(new Vector2(x0, z0));
            uv.Add(new Vector2(x1, z0));
            uv.Add(new Vector2(x1, z1));
            uv.Add(new Vector2(x0, z1));
            t.Add(baseIndex); t.Add(baseIndex + 2); t.Add(baseIndex + 1);
            t.Add(baseIndex); t.Add(baseIndex + 3); t.Add(baseIndex + 2);
        }
    }
}
