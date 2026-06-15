#if UNITY_EDITOR
using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Chunks;
using UnityEngine;
using UnityEngine.Rendering;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Builds an in-scene preview GameObject for a <see cref="ChunkTemplate"/>:
    /// grate layers (alpha-cutout holes), solid floor layers, and perimeter walls
    /// with carved openings. Convention: corner origin — the chunk occupies
    /// X∈[0,width], Z∈[0,length], and a layer block spans Y∈[zPosition, zPosition+height].
    /// </summary>
    public static class ChunkPreviewRenderer
    {
        /// <summary>Per-build preview toggles. wireframe/showGrid are applied at the
        /// SceneView level by the editor window; this renderer honours showColliders.</summary>
        public struct RenderOptions
        {
            public bool wireframe;
            public bool showColliders;
            public bool showGrid;

            public static RenderOptions Default => new RenderOptions { showColliders = true };
        }

        public static GameObject RenderPreview(ChunkTemplate template, Vector3 origin)
            => RenderPreview(template, origin, RenderOptions.Default);

        public static GameObject RenderPreview(ChunkTemplate template, Vector3 origin, RenderOptions options)
        {
            var root = new GameObject($"Preview_{template.name}");
            root.transform.position = origin;

            var layers = template.layers ?? System.Array.Empty<LayerConfig>();
            for (int i = 0; i < layers.Length; i++)
                RenderLayer(root, template, layers[i], i, options.showColliders);

            RenderWall(root, template, "North", template.north, options.showColliders);
            RenderWall(root, template, "South", template.south, options.showColliders);
            RenderWall(root, template, "East",  template.east,  options.showColliders);
            RenderWall(root, template, "West",  template.west,  options.showColliders);

            return root;
        }

        private static void RenderLayer(GameObject parent, ChunkTemplate t, LayerConfig layer, int index, bool colliders)
        {
            switch (layer.type)
            {
                case LayerType.Grate: RenderGrate(parent, t, layer, index, colliders); break;
                case LayerType.Floor: RenderFloor(parent, t, layer, index, colliders); break;
                case LayerType.Void:  break; // empty
            }
        }

        private static void RenderGrate(GameObject parent, ChunkTemplate t, LayerConfig layer, int index, bool colliders)
        {
            var obj = new GameObject($"Grate_L{index}");
            obj.transform.SetParent(parent.transform, false);
            obj.transform.localPosition = new Vector3(t.width * 0.5f, layer.zPosition + layer.height * 0.5f, t.length * 0.5f);

            obj.AddComponent<MeshFilter>().sharedMesh = CreateGrateMesh(t.width, t.length, layer.cellSize);

            float ratio = layer.cellSize > 0f ? Mathf.Clamp01(layer.holeSize / layer.cellSize) : 0.8f;
            obj.AddComponent<MeshRenderer>().sharedMaterial = GrateMaterial(t.grateColor, ratio);

            if (colliders)
            {
                var col = obj.AddComponent<BoxCollider>();
                col.center = Vector3.zero;
                col.size   = new Vector3(t.width, 0.05f, t.length); // thin but solid → stand on it
            }
        }

        private static void RenderFloor(GameObject parent, ChunkTemplate t, LayerConfig layer, int index, bool colliders)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = $"Floor_L{index}";
            cube.transform.SetParent(parent.transform, false);
            cube.transform.localPosition = new Vector3(t.width * 0.5f, layer.zPosition + layer.height * 0.5f, t.length * 0.5f);
            cube.transform.localScale    = new Vector3(t.width, Mathf.Max(0.01f, layer.height), t.length);
            cube.GetComponent<MeshRenderer>().sharedMaterial = OpaqueMaterial(t.floorColor);
            StripCollider(cube, colliders);
        }

        // ── Walls ─────────────────────────────────────────────────────────────
        private static void RenderWall(GameObject parent, ChunkTemplate t, string side, WallConfig wall, bool colliders)
        {
            if (!wall.enabled) return;

            bool  runX   = side == "North" || side == "South"; // runs along X (width)
            float runLen = runX ? t.width : t.length;
            float perp   = side == "North" ? 0f
                         : side == "South" ? t.length
                         : side == "East"  ? t.width
                         : 0f;                                  // West
            float depth  = t.depth, th = wall.thickness;
            var   mat    = OpaqueMaterial(t.wallColor);
            var   ops    = wall.openings ?? System.Array.Empty<WallOpening>();

            if (ops.Length == 0)
            {
                AddWallBox(parent, side, runX, perp, 0f, runLen, -depth, 0f, th, mat, colliders);
                return;
            }

            var sorted = new List<WallOpening>(ops);
            sorted.Sort((a, b) => (a.position - a.width * 0.5f).CompareTo(b.position - b.width * 0.5f));

            float cursor = 0f;
            int seg = 0;
            foreach (var o in sorted)
            {
                float oL = Mathf.Clamp(o.position - o.width * 0.5f, 0f, runLen);
                float oR = Mathf.Clamp(o.position + o.width * 0.5f, 0f, runLen);
                if (oR <= oL) continue;

                if (oL > cursor)
                    AddWallBox(parent, $"{side}_Seg{seg++}", runX, perp, cursor, oL, -depth, 0f, th, mat, colliders);

                float oh = Mathf.Min(o.height, depth);
                if (oh < depth) // wall below the doorway
                    AddWallBox(parent, $"{side}_Below{seg}", runX, perp, oL, oR, -depth, -oh, th, mat, colliders);

                cursor = Mathf.Max(cursor, oR);
            }
            if (cursor < runLen)
                AddWallBox(parent, $"{side}_Seg{seg}", runX, perp, cursor, runLen, -depth, 0f, th, mat, colliders);
        }

        private static void AddWallBox(GameObject parent, string name, bool runX, float perp,
            float runStart, float runEnd, float yBottom, float yTop, float thickness, Material mat, bool colliders)
        {
            float runLen = runEnd - runStart;
            float yH     = yTop - yBottom;
            if (runLen <= 0.0001f || yH <= 0.0001f) return;

            float runC = (runStart + runEnd) * 0.5f;
            float yC   = (yBottom + yTop) * 0.5f;

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(parent.transform, false);
            cube.transform.localPosition = runX ? new Vector3(runC, yC, perp) : new Vector3(perp, yC, runC);
            cube.transform.localScale    = runX ? new Vector3(runLen, yH, thickness) : new Vector3(thickness, yH, runLen);
            cube.GetComponent<MeshRenderer>().sharedMaterial = mat;
            StripCollider(cube, colliders);
        }

        private static void StripCollider(GameObject go, bool keep)
        {
            if (keep) return;
            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
        }

        // ── Grate mesh + materials ────────────────────────────────────────────
        private static Mesh CreateGrateMesh(float width, float length, float cellSize)
        {
            float uW = cellSize > 0f ? width / cellSize : width;
            float uL = cellSize > 0f ? length / cellSize : length;
            float hw = width * 0.5f, hl = length * 0.5f;

            var mesh = new Mesh { name = "GratePreview" };
            mesh.vertices  = new[] { new Vector3(-hw, 0, -hl), new Vector3(hw, 0, -hl), new Vector3(hw, 0, hl), new Vector3(-hw, 0, hl) };
            mesh.normals   = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.uv        = new[] { new Vector2(0, 0), new Vector2(uW, 0), new Vector2(uW, uL), new Vector2(0, uL) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Shader LitShader()
        {
            var s = Shader.Find("Universal Render Pipeline/Lit");
            return s != null ? s : Shader.Find("Standard");
        }

        private static Material OpaqueMaterial(Color color)
        {
            var m = new Material(LitShader()) { hideFlags = HideFlags.DontSave };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color"))     m.SetColor("_Color", color);
            m.color = color;
            return m;
        }

        private static Material GrateMaterial(Color color, float holeRatio)
        {
            var m = OpaqueMaterial(color);
            var tex = BuildGrateTile(holeRatio);
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
            if (m.HasProperty("_Surface"))   m.SetFloat("_Surface", 0f);
            if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 1f);
            if (m.HasProperty("_Cutoff"))    m.SetFloat("_Cutoff", 0.5f);
            if (m.HasProperty("_Cull"))      m.SetFloat("_Cull", (float)CullMode.Off);
            m.EnableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)RenderQueue.AlphaTest;
            return m;
        }

        private static Texture2D BuildGrateTile(float holeRatio)
        {
            const int t = 20;
            int border = Mathf.Clamp(Mathf.RoundToInt((1f - holeRatio) * 0.5f * t), 1, t / 2 - 1);
            var tex = new Texture2D(t, t, TextureFormat.RGBA32, false)
            {
                name = "GrateTile", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat, hideFlags = HideFlags.DontSave,
            };
            var bar  = new Color32(255, 255, 255, 255);
            var hole = new Color32(0, 0, 0, 0);
            var px = new Color32[t * t];
            for (int y = 0; y < t; y++)
                for (int x = 0; x < t; x++)
                    px[y * t + x] = (x < border || x >= t - border || y < border || y >= t - border) ? bar : hole;
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
#endif
