using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// One greedy-merged wall block: a run of contiguous Wall cells with the
    /// same height and inset profile, rendered as a single box instead of
    /// w×d cubes. Coordinates and sizes are in cells; height in 2.5 m units.
    /// <see cref="insets"/> holds WallGreedyMesher.Inset* flags: a flagged
    /// side faces walkable space and its face retracts WallGreedyMesher.Inset
    /// metres into the cell (thin partition); unflagged sides reach the cell
    /// border to join the neighbouring wall run flush.
    /// </summary>
    public struct WallRect
    {
        public int x;
        public int z;
        public int w;
        public int d;
        public int heightUnits;
        public byte insets;
    }

    /// <summary>
    /// Greedy meshing for Wall cells (§6 of BACKROOMS_GRID_SYSTEM.md).
    ///
    /// Instead of one GameObject per wall cell, contiguous equal-height wall
    /// runs collapse into rectangles and the whole chunk's walls become ONE
    /// mesh. A corridor wall of 8 cells = 1 box, not 8 cubes.
    ///
    /// Pure and allocation-light: no scene access, fully testable in EditMode.
    /// </summary>
    public static class WallGreedyMesher
    {
        // Render-only thin-partition constants (mini-fase Paredes Finas).
        // NOT part of the Rust contract (GridConstants mirrors that): the cell
        // grid and backend collision still treat the whole Wall cell as solid;
        // Fase 4 must model wall collision as this thin slab (pending ADR).
        public const float WallThickness = 0.2f;

        /// <summary>How far an exposed wall face retracts from the cell border.</summary>
        public const float Inset = (GridConstants.CellSize - WallThickness) / 2f;

        // Per-side inset flags for WallRect.insets / ComputeWallInsets.
        public const byte InsetWest = 1;   // -x
        public const byte InsetEast = 2;   // +x
        public const byte InsetSouth = 4;  // -z
        public const byte InsetNorth = 8;  // +z

        /// <summary>
        /// Render height (in 2.5 m units) for every Wall cell of a chunk:
        /// the max ceiling of adjacent walkable cells, so walls always reach
        /// the ceiling of the tallest space they bound. Interior solid walls
        /// (no walkable neighbour) inherit the chunk's max ceiling so no gap
        /// is visible over a wall from a tall room. Non-wall cells get 0.
        /// Index layout matches Rust: cells[z * size + x].
        /// </summary>
        public static int[] ComputeWallHeights(GridCell[] cells, int size)
        {
            var heights = new int[cells.Length];
            int chunkMax = 2; // minimum wall height: 2 units = 5 m

            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i].IsWalkable && cells[i].ceilingHeight > chunkMax)
                    chunkMax = cells[i].ceilingHeight;
            }

            for (int z = 0; z < size; z++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = z * size + x;
                    if (cells[i].Kind != GridCellType.Wall)
                        continue;

                    int h = 0;
                    h = MaxWalkableCeiling(cells, size, x - 1, z, h);
                    h = MaxWalkableCeiling(cells, size, x + 1, z, h);
                    h = MaxWalkableCeiling(cells, size, x, z - 1, h);
                    h = MaxWalkableCeiling(cells, size, x, z + 1, h);

                    heights[i] = h > 0 ? Mathf.Max(h, 2) : chunkMax;
                }
            }

            return heights;
        }

        private static int MaxWalkableCeiling(GridCell[] cells, int size, int x, int z, int current)
        {
            if (x < 0 || z < 0 || x >= size || z >= size)
                return current;
            var c = cells[z * size + x];
            if (c.IsWalkable && c.ceilingHeight > current)
                return c.ceilingHeight;
            return current;
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
                    if (x > 0 && cells[i - 1].Kind != GridCellType.Wall)
                        f |= InsetWest;
                    if (x < size - 1 && cells[i + 1].Kind != GridCellType.Wall)
                        f |= InsetEast;
                    if (z > 0 && cells[i - size].Kind != GridCellType.Wall)
                        f |= InsetSouth;
                    if (z < size - 1 && cells[i + size].Kind != GridCellType.Wall)
                        f |= InsetNorth;
                    insets[i] = f;
                }
            }
            return insets;
        }

        /// <summary>
        /// Greedy meshing over the height grid, inset-profile aware: cells of
        /// equal height and equal north/south inset merge into runs along +x;
        /// identical consecutive runs merge along +z when the seam between
        /// them is closed (no inset on either side of it). Every nonzero cell
        /// is covered by exactly one rect (no gaps, no overlaps), and inset
        /// sides stay per-cell-correct so thin partitions join flush at
        /// corners and T-junctions.
        /// </summary>
        public static List<WallRect> GreedyRects(int[] heights, byte[] insets, int size)
        {
            var rects = new List<WallRect>();
            // Rect index per run-origin x in the previous row, -1 = none.
            var openAbove = new int[size];
            var openHere = new int[size];
            for (int x = 0; x < size; x++)
                openAbove[x] = -1;

            for (int z = 0; z < size; z++)
            {
                for (int x = 0; x < size; x++)
                    openHere[x] = -1;

                for (int x = 0; x < size; )
                {
                    int i = z * size + x;
                    if (heights[i] == 0)
                    {
                        x++;
                        continue;
                    }

                    int h = heights[i];
                    byte ns = (byte)(insets[i] & (InsetSouth | InsetNorth));
                    int w = 1;
                    while (x + w < size && heights[i + w] == h
                           && (insets[i + w] & (InsetSouth | InsetNorth)) == ns)
                        w++;
                    // Interior seams of the run are Wall↔Wall (no inset by
                    // construction); only the end cells contribute west/east.
                    byte runInsets = (byte)(ns
                        | (insets[i] & InsetWest)
                        | (insets[i + w - 1] & InsetEast));

                    int above = openAbove[x];
                    if (above >= 0 && MergesWithRunAbove(rects[above], w, h, runInsets))
                    {
                        var grown = rects[above];
                        grown.d++;
                        grown.insets |= (byte)(runInsets & InsetNorth);
                        rects[above] = grown;
                        openHere[x] = above;
                    }
                    else
                    {
                        openHere[x] = rects.Count;
                        rects.Add(new WallRect
                        {
                            x = x,
                            z = z,
                            w = w,
                            d = 1,
                            heightUnits = h,
                            insets = runInsets,
                        });
                    }

                    x += w;
                }

                (openAbove, openHere) = (openHere, openAbove);
            }

            return rects;
        }

        private static bool MergesWithRunAbove(WallRect r, int w, int h, byte runInsets)
        {
            const byte westEast = InsetWest | InsetEast;
            return r.w == w && r.heightUnits == h
                && (r.insets & InsetNorth) == 0
                && (runInsets & InsetSouth) == 0
                && (r.insets & westEast) == (runInsets & westEast);
        }

        /// <summary>
        /// Build one mesh for all wall rects of a chunk: 4 side quads + top
        /// quad per rect, local origin at the chunk's min corner. UVs are in
        /// metres so wall materials tile at constant world density.
        ///
        /// Two submeshes share one vertex buffer: submesh 0 = side faces (wall
        /// material), submesh 1 = top caps. The caps go in their own submesh so
        /// the builder can paint them with the CEILING material — the caps are
        /// coplanar with the FloorCeiling panels along every wall↔room edge, and
        /// matching their shading makes that seam invisible instead of a flicker.
        /// </summary>
        public static Mesh BuildMesh(List<WallRect> rects, float cellSize)
        {
            var vertices = new List<Vector3>(rects.Count * 20);
            var normals = new List<Vector3>(rects.Count * 20);
            var uvs = new List<Vector2>(rects.Count * 20);
            var sideTris = new List<int>(rects.Count * 24);
            var topTris = new List<int>(rects.Count * 6);

            foreach (var r in rects)
            {
                // Exposed sides retract Inset metres into the cell (thin
                // partition); closed sides reach the cell border to join the
                // neighbouring wall run flush.
                float x0 = r.x * cellSize + ((r.insets & InsetWest) != 0 ? Inset : 0f);
                float z0 = r.z * cellSize + ((r.insets & InsetSouth) != 0 ? Inset : 0f);
                float x1 = (r.x + r.w) * cellSize - ((r.insets & InsetEast) != 0 ? Inset : 0f);
                float z1 = (r.z + r.d) * cellSize - ((r.insets & InsetNorth) != 0 ? Inset : 0f);
                float y1 = r.heightUnits * cellSize;

                // South (-z), north (+z), west (-x), east (+x), top (+y).
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
            }

            var mesh = new Mesh
            {
                name = "WallGreedyMesh",
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
