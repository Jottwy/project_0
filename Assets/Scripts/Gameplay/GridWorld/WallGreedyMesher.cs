using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// One greedy-merged wall block: a run of contiguous Wall cells with the
    /// same height, rendered as a single box instead of w×d cubes.
    /// Coordinates and sizes are in cells; height in 2.5 m units.
    /// </summary>
    public struct WallRect
    {
        public int x;
        public int z;
        public int w;
        public int d;
        public int heightUnits;
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
        /// Classic 2D greedy meshing over the height grid: maximal rectangles
        /// of equal nonzero height, row-major. Every nonzero cell is covered
        /// by exactly one rect (no gaps, no overlaps).
        /// </summary>
        public static List<WallRect> GreedyRects(int[] heights, int size)
        {
            var rects = new List<WallRect>();
            var used = new bool[heights.Length];

            for (int z = 0; z < size; z++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = z * size + x;
                    if (used[i] || heights[i] == 0)
                        continue;

                    int h = heights[i];

                    // Grow width along +x while same height and unused.
                    int w = 1;
                    while (x + w < size && !used[i + w] && heights[i + w] == h)
                        w++;

                    // Grow depth along +z while the whole row matches.
                    int d = 1;
                    while (z + d < size && RowMatches(heights, used, size, x, z + d, w, h))
                        d++;

                    for (int dz = 0; dz < d; dz++)
                        for (int dx = 0; dx < w; dx++)
                            used[(z + dz) * size + x + dx] = true;

                    rects.Add(new WallRect { x = x, z = z, w = w, d = d, heightUnits = h });
                }
            }

            return rects;
        }

        private static bool RowMatches(int[] heights, bool[] used, int size, int x, int z, int w, int h)
        {
            for (int dx = 0; dx < w; dx++)
            {
                int i = z * size + x + dx;
                if (used[i] || heights[i] != h)
                    return false;
            }
            return true;
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
                float x0 = r.x * cellSize;
                float z0 = r.z * cellSize;
                float x1 = (r.x + r.w) * cellSize;
                float z1 = (r.z + r.d) * cellSize;
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
