using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BackroomsSurvival.Gameplay.Shaft
{
    // ─────────────────────────────────────────────────────────────────────────
    // Standalone procedural "vertical shaft" prototype. NOT part of the validated
    // edge-based chunk pipeline (WorldGenerator / GridChunkBuilder / ChunkStreamer)
    // nor the Rust 2.5 m cell contract — it is an additive, self-contained demo.
    //
    // Geometry (origin = top-centre of the room; the shaft descends along −Y):
    //   Layer 0  (Y =   0): a see-through grate (5×5 m) + 4 walls, 2 of which have
    //                       a random doorway opening.
    //   Layers 1-3 (Y = −4, −8, −12): solid 5×5 m floor slabs, walls closed.
    //   Walls run continuously the full 12 m; void between every floor.
    //
    // Boxes use scaled cube primitives (one shared cube mesh + shared materials →
    // SRP-batcher friendly, and guaranteed-correct meshes + BoxColliders so there
    // is no fall-through). The grate's holes come from an alpha-cutout texture
    // (the spec's "UV mapping" option) rather than 10 000 real hole quads.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Metadata describing a generated shaft (openings, layer depths…).</summary>
    public struct ShaftMetadata
    {
        public Vector3 Origin;
        public int     Seed;
        public float   RoomSize;       // 5 m
        public float   WallThickness;  // 0.2 m
        public float   LayerHeight;    // 4 m
        public int     LayerCount;     // 4 (layers 0..3)
        public float   TotalDepth;     // 12 m
        public bool    OpeningNorth, OpeningSouth, OpeningEast, OpeningWest;
        public float[] LayerY;         // local Y of each layer surface: 0, −4, −8, −12
    }

    /// <summary>Metadata for a generated 5×5 grid of shafts.</summary>
    public struct ShaftGridMetadata
    {
        public Vector3   Origin;
        public int       Seed;
        public int       Columns;       // shafts per row (X)
        public int       Rows;          // shafts per column (Z)
        public float     ShaftSize;     // 5 m
        public float     WallThickness; // 0.2 m
        public float     LayerHeight;   // 4 m
        public int       LayerCount;    // 4
        public float     TotalDepth;    // 12 m
        public float     GridSpan;      // Columns × ShaftSize (square): 25 m
        public Vector2[] ShaftCenters;  // local XZ centre of every shaft, row-major [row*Columns + col]
    }

    /// <summary>
    /// Pure generation logic (no MonoBehaviour state) — builds shaft hierarchies
    /// (single shaft or a 5×5 grid) under a fresh root at <paramref name="origin"/>.
    /// Deterministic by seed.
    /// </summary>
    public static class VerticalShaftGenerator
    {
        public const float RoomSize      = 5f;
        public const float WallThickness = 0.2f;
        public const float LayerHeight   = 4f;
        public const int   LayerCount    = 4;
        public const float TotalDepth    = LayerHeight * (LayerCount - 1); // 12 m

        // Grate (layer 0 only): 100×100 cells of 5 cm, 4 cm hole, ~1 cm bar.
        public const int   GridCells = 100;

        // Doorway opening cut into layer 0 of two random walls.
        private const float OpeningWidth  = 1.5f;
        private const float OpeningHeight = 2.2f;

        public static GameObject GenerateVerticalShaftChunk(Vector3 origin, int seed)
            => GenerateVerticalShaftChunk(origin, seed, out _);

        public static GameObject GenerateVerticalShaftChunk(Vector3 origin, int seed, out ShaftMetadata meta)
        {
            var rng  = new System.Random(seed);
            var root = new GameObject($"VerticalShaft_{seed}");
            root.transform.position = origin;

            var mats = BuildMaterials();

            // Pick exactly two of the four walls (N,S,E,W = 0,1,2,3) to open.
            var order = new List<int> { 0, 1, 2, 3 };
            Shuffle(order, rng);
            bool oN = order[0] == 0 || order[1] == 0;
            bool oS = order[0] == 1 || order[1] == 1;
            bool oE = order[0] == 2 || order[1] == 2;
            bool oW = order[0] == 3 || order[1] == 3;

            // Walls — perpendicular offset puts the inner face flush at ±RoomSize/2.
            float perp = RoomSize * 0.5f + WallThickness * 0.5f; // 2.6
            BuildWall(root.transform, mats.wall, isNS: true,  perp: +perp, opening: oN); // North (+Z)
            BuildWall(root.transform, mats.wall, isNS: true,  perp: -perp, opening: oS); // South (−Z)
            BuildWall(root.transform, mats.wall, isNS: false, perp: +perp, opening: oE); // East  (+X)
            BuildWall(root.transform, mats.wall, isNS: false, perp: -perp, opening: oW); // West  (−X)

            // Layer 0 grate at the top, layers 1..3 solid floors below.
            BuildGrate(root.transform, mats.grate);
            var layerY = new float[LayerCount];
            layerY[0] = 0f;
            for (int l = 1; l < LayerCount; l++)
            {
                layerY[l] = -LayerHeight * l;
                BuildFloor(root.transform, mats.floor, layerY[l]);
            }

            meta = new ShaftMetadata
            {
                Origin = origin, Seed = seed,
                RoomSize = RoomSize, WallThickness = WallThickness,
                LayerHeight = LayerHeight, LayerCount = LayerCount, TotalDepth = TotalDepth,
                OpeningNorth = oN, OpeningSouth = oS, OpeningEast = oE, OpeningWest = oW,
                LayerY = layerY,
            };
            return root;
        }

        // ── 5×5 grid of shafts ────────────────────────────────────────────────
        // 25 shafts in a regular grid: per-shaft grate + 3 floors, NO internal
        // walls (open between shafts), a single shared material set, and one
        // perimeter wall ring built once. The layout is fully regular, so the seed
        // currently changes nothing (reserved for future per-shaft variation).
        public static GameObject GenerateVerticalShaftGrid(Vector3 origin, int seed)
            => GenerateVerticalShaftGrid(origin, seed, out _);

        public static GameObject GenerateVerticalShaftGrid(Vector3 origin, int seed, out ShaftGridMetadata meta)
        {
            const int cols = 5, rows = 5;
            var root = new GameObject($"VerticalShaftGrid_{seed}");
            root.transform.position = origin;

            var mats = BuildMaterials(); // one shared set for all 25 shafts → batchable

            float gridSpan = cols * RoomSize;  // 25 m (square grid)
            float half     = gridSpan * 0.5f;  // 12.5
            var centers = new Vector2[rows * cols];

            for (int i = 0; i < rows; i++)      // i → Z
            {
                for (int j = 0; j < cols; j++)  // j → X
                {
                    float cx = -half + RoomSize * 0.5f + j * RoomSize; // −10 + j·5
                    float cz = -half + RoomSize * 0.5f + i * RoomSize;
                    centers[i * cols + j] = new Vector2(cx, cz);

                    // Layer 0 grate, then layers 1..3 floors — no per-shaft walls.
                    AddBox(root.transform, $"Grate_{i}_{j}",
                           new Vector3(cx, 0f, cz), new Vector3(RoomSize, 0.05f, RoomSize), mats.grate);
                    for (int l = 1; l < LayerCount; l++)
                        AddBox(root.transform, $"Floor_{i}_{j}_L{l}",
                               new Vector3(cx, -LayerHeight * l - 0.1f, cz),
                               new Vector3(RoomSize, 0.2f, RoomSize), mats.floor);
                }
            }

            BuildPerimeterWalls(root.transform, mats.wall, gridSpan);

            meta = new ShaftGridMetadata
            {
                Origin = origin, Seed = seed,
                Columns = cols, Rows = rows,
                ShaftSize = RoomSize, WallThickness = WallThickness,
                LayerHeight = LayerHeight, LayerCount = LayerCount, TotalDepth = TotalDepth,
                GridSpan = gridSpan, ShaftCenters = centers,
            };
            return root;
        }

        // Four solid exterior walls around the whole grid (no openings, no internal
        // walls). N/S run the full span plus the corners; E/W fit between them.
        private static void BuildPerimeterWalls(Transform root, Material mat, float span)
        {
            float t = WallThickness, h = TotalDepth, midY = -h * 0.5f;
            float perp = span * 0.5f + t * 0.5f; // inner face flush at ±span/2
            float lenNS = span + 2f * t;         // cover the corners
            AddBox(root, "Wall_North", new Vector3(0f, midY,  perp), new Vector3(lenNS, h, t), mat);
            AddBox(root, "Wall_South", new Vector3(0f, midY, -perp), new Vector3(lenNS, h, t), mat);
            AddBox(root, "Wall_East",  new Vector3( perp, midY, 0f), new Vector3(t, h, span), mat);
            AddBox(root, "Wall_West",  new Vector3(-perp, midY, 0f), new Vector3(t, h, span), mat);
        }

        // ── Walls ────────────────────────────────────────────────────────────
        // A wall runs along X (isNS) or Z. With an opening it is split into two
        // full-height side segments plus a short segment below the doorway,
        // leaving the top OpeningHeight of the middle open.
        private static void BuildWall(Transform root, Material mat, bool isNS, float perp, bool opening)
        {
            float t = WallThickness, h = TotalDepth, midY = -h * 0.5f;
            float runLen = isNS ? RoomSize + 2f * t : RoomSize; // N/S cover the corners

            if (!opening)
            {
                Vector3 size   = isNS ? new Vector3(runLen, h, t) : new Vector3(t, h, runLen);
                Vector3 center = isNS ? new Vector3(0f, midY, perp) : new Vector3(perp, midY, 0f);
                AddBox(root, "Wall", center, size, mat);
                return;
            }

            float segLen     = runLen * 0.5f - OpeningWidth * 0.5f;     // each side segment
            float segOffset  = OpeningWidth * 0.5f + segLen * 0.5f;     // its centre along the run
            float belowH     = h - OpeningHeight;                       // wall under the doorway
            float belowY     = -OpeningHeight - belowH * 0.5f;

            if (isNS)
            {
                AddBox(root, "Wall_L", new Vector3(-segOffset, midY,  perp), new Vector3(segLen, h, t), mat);
                AddBox(root, "Wall_R", new Vector3( segOffset, midY,  perp), new Vector3(segLen, h, t), mat);
                AddBox(root, "Wall_Below", new Vector3(0f, belowY, perp), new Vector3(OpeningWidth, belowH, t), mat);
            }
            else
            {
                AddBox(root, "Wall_L", new Vector3(perp, midY, -segOffset), new Vector3(t, h, segLen), mat);
                AddBox(root, "Wall_R", new Vector3(perp, midY,  segOffset), new Vector3(t, h, segLen), mat);
                AddBox(root, "Wall_Below", new Vector3(perp, belowY, 0f), new Vector3(t, belowH, OpeningWidth), mat);
            }
        }

        private static void BuildGrate(Transform root, Material mat)
            => AddBox(root, "Grate_L0", new Vector3(0f, 0f, 0f),
                      new Vector3(RoomSize, 0.05f, RoomSize), mat);

        private static void BuildFloor(Transform root, Material mat, float layerY)
            => AddBox(root, "Floor", new Vector3(0f, layerY - 0.1f, 0f),
                      new Vector3(RoomSize, 0.2f, RoomSize), mat);

        // Scaled cube primitive: shared 1×1×1 mesh + auto BoxCollider (solid → no
        // fall-through). Shared material keeps everything batchable.
        private static GameObject AddBox(Transform parent, string name, Vector3 localCenter, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localCenter;
            go.transform.localScale    = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        // ── Materials & grate texture ─────────────────────────────────────────
        private struct Mats { public Material wall, floor, grate; }

        private static Mats BuildMaterials()
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) lit = Shader.Find("Standard");

            var wall  = NewLit(lit, "ShaftWall",  new Color(0.78f, 0.70f, 0.38f)); // Backrooms yellow
            var floor = NewLit(lit, "ShaftFloor", new Color(0.30f, 0.28f, 0.22f)); // dark concrete
            var grate = NewLit(lit, "ShaftGrate", new Color(0.40f, 0.40f, 0.44f)); // metal
            SetupCutout(grate, BuildGrateTexture());
            return new Mats { wall = wall, floor = floor, grate = grate };
        }

        private static Material NewLit(Shader shader, string name, Color color)
        {
            var m = new Material(shader) { name = name, hideFlags = HideFlags.DontSave };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color"))     m.SetColor("_Color", color);
            m.color = color;
            return m;
        }

        // Alpha-clip the grate so the holes are genuinely see-through, double-sided
        // so the void reads from below, and tile one cell across the whole 5 m.
        private static void SetupCutout(Material m, Texture2D tex)
        {
            var tiling = new Vector2(GridCells, GridCells);
            if (m.HasProperty("_BaseMap")) { m.SetTexture("_BaseMap", tex); m.SetTextureScale("_BaseMap", tiling); }
            if (m.HasProperty("_MainTex")) { m.SetTexture("_MainTex", tex); m.SetTextureScale("_MainTex", tiling); }
            if (m.HasProperty("_Surface"))   m.SetFloat("_Surface", 0f);   // opaque surface, alpha-clipped
            if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 1f);
            if (m.HasProperty("_Cutoff"))    m.SetFloat("_Cutoff", 0.5f);
            if (m.HasProperty("_Cull"))      m.SetFloat("_Cull", (float)CullMode.Off); // double-sided
            m.EnableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)RenderQueue.AlphaTest;
        }

        // One 5 cm cell as a tiny tile: 4 cm hole (alpha 0) framed by a ~1 cm bar
        // (alpha 1). Repeated 100× over the grate quad → a 100×100 grid.
        private static Texture2D BuildGrateTexture()
        {
            const int t = 20, border = 2; // 20 px = 5 cm → border 2 px ≈ 0.5 cm each side, hole 16 px = 4 cm
            var tex = new Texture2D(t, t, TextureFormat.RGBA32, false)
            {
                name = "GrateTile", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat,
            };
            var bar  = new Color32(150, 150, 160, 255);
            var hole = new Color32(0, 0, 0, 0);
            var px = new Color32[t * t];
            for (int y = 0; y < t; y++)
                for (int x = 0; x < t; x++)
                    px[y * t + x] = (x < border || x >= t - border || y < border || y >= t - border) ? bar : hole;
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        private static void Shuffle<T>(List<T> list, System.Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }

    /// <summary>
    /// Drop this on an empty GameObject. Generates a shaft at the object's
    /// position on Start (runtime) or via the context menu (edit mode). The
    /// generated geometry is parented under this object.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VerticalShaftChunk : MonoBehaviour
    {
        [Tooltip("Deterministic seed: same seed ⇒ same opening layout.")]
        public int seed = 12345;
        [Tooltip("Build automatically when entering Play mode.")]
        public bool generateOnStart = true;

        public ShaftMetadata Metadata { get; private set; }
        private GameObject _instance;

        private void Start()
        {
            if (generateOnStart && _instance == null) Generate();
        }

        [ContextMenu("Generate / Regenerate")]
        public void Generate()
        {
            Clear();
            _instance = VerticalShaftGenerator.GenerateVerticalShaftChunk(
                transform.position, seed, out var meta);
            _instance.transform.SetParent(transform, worldPositionStays: true);
            Metadata = meta;
        }

        [ContextMenu("Clear")]
        public void Clear()
        {
            // Drop the tracked instance and any leftover shaft roots (e.g. after a
            // domain reload that cleared _instance but not the children).
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (child.name.StartsWith("VerticalShaft_")) DestroySafe(child.gameObject);
            }
            _instance = null;
        }

        private static void DestroySafe(Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
