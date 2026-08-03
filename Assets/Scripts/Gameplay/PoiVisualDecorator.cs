using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// NEVER CALLED. Phase 3.2D was left half-built: 3.2C shipped (its <c>PoiDebugHud</c> is alive,
    /// started from <c>GameBootstrap</c>) and this one never got wired. The intended hook was
    /// <c>ChunkRenderer.BuildChunk</c>, which is itself inert today.
    ///
    /// Unlike a MonoBehaviour, the "no references" claim is airtight here: this is a
    /// <c>public static class</c>, so it cannot hang off a GameObject or a UnityEvent. There is no
    /// path to it other than a direct call, and no direct call exists in the repository.
    ///
    /// Retiring it belongs with the ChunkRenderer decision (see docs/AUDIT-2026-08-03.md), not
    /// before: both are staging for the same unfinished phase.
    ///
    /// Phase 3.2D — POI Visual Identity V1.
    ///
    /// Adds lightweight, deterministic visual markers to POI chunks based only
    /// on the backend-provided template_id. Never invents POIs client-side:
    /// if the backend did not tag the chunk as a POI template, nothing renders.
    ///
    /// Visuals are simple primitive geometry under a "__POI_VISUALS" container
    /// child of the chunk root. Visual-only — no colliders, no gameplay.
    /// </summary>
    public static class PoiVisualDecorator
    {
        public const string ContainerName = "__POI_VISUALS";

        private const int TemplateLandmark = 18;
        private const int TemplateAnomaly = 19;
        private const int TemplateDangerPocket = 20;
        private const int TemplateSafePocket = 21;

        private static Material _landmarkMat;
        private static Material _landmarkGlowMat;
        private static Material _anomalyMat;
        private static Material _anomalyDarkMat;
        private static Material _dangerMat;
        private static Material _dangerDarkMat;
        private static Material _safeMat;
        private static Material _safeTrimMat;

        /// <summary>
        /// Decorate a chunk root with POI visuals when its backend template_id
        /// marks it as a POI chunk. Idempotent: skips if the container already
        /// exists. Deterministic: variation derives only from template_id,
        /// chunk coordinates, layer, and world seed.
        /// </summary>
        public static void Decorate(Transform chunkRoot, ChunkViewMsg cv, long worldSeed, float chunkSize, float ceilingHeight, bool debugLog)
        {
            if (cv == null || chunkRoot == null)
                return;
            if (cv.templateId < TemplateLandmark || cv.templateId > TemplateSafePocket)
                return;
            if (chunkRoot.Find(ContainerName) != null)
                return;

            EnsureMaterials();

            var container = new GameObject(ContainerName);
            container.transform.SetParent(chunkRoot, false);
            container.transform.localPosition = Vector3.zero;

            uint h = Hash(cv.templateId, cv.pos[0], cv.pos[1], cv.layer, worldSeed);
            float c = chunkSize * 0.5f;

            switch (cv.templateId)
            {
                case TemplateLandmark:
                    BuildLandmark(container.transform, c, ceilingHeight, h);
                    break;
                case TemplateAnomaly:
                    BuildAnomaly(container.transform, c, ceilingHeight, h);
                    break;
                case TemplateDangerPocket:
                    BuildDangerPocket(container.transform, c, h);
                    break;
                case TemplateSafePocket:
                    BuildSafePocket(container.transform, c, h);
                    break;
            }

            if (debugLog)
                Debug.Log($"MPTRACE step=POI2 event=unity_poi_visuals_created chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) template_id={cv.templateId}");
        }

        // ── Template 18: Landmark — central tower anchor + glowing bars ──
        private static void BuildLandmark(Transform parent, float c, float ceilingHeight, uint h)
        {
            float towerH = ceilingHeight - 0.05f;
            // Central square tower: a clearly abnormal, navigable anchor.
            Cube(parent, "LandmarkTower", new Vector3(c, towerH * 0.5f, c), new Vector3(2.2f, towerH, 2.2f), 0f, _landmarkMat);
            // Wide base plinth so the tower reads as built, not dropped in.
            Cube(parent, "LandmarkPlinth", new Vector3(c, 0.25f, c), new Vector3(4.6f, 0.5f, 4.6f), 0f, _landmarkMat);

            // Four vertical fluorescent bars around the tower — visible from afar.
            float r = 3.4f;
            for (int i = 0; i < 4; i++)
            {
                float ang = i * 90f + (h % 2 == 0 ? 0f : 45f);
                float rad = ang * Mathf.Deg2Rad;
                var p = new Vector3(c + Mathf.Cos(rad) * r, towerH * 0.5f, c + Mathf.Sin(rad) * r);
                Cube(parent, "LandmarkBar", p, new Vector3(0.18f, towerH - 0.4f, 0.18f), 0f, _landmarkGlowMat);
            }

            // Two broken wall frames flanking the approach axis.
            float frameAng = (h >> 3) % 2 == 0 ? 0f : 90f;
            for (int i = 0; i < 2; i++)
            {
                float side = i == 0 ? -8f : 8f;
                var p = new Vector3(c + (frameAng == 0f ? side : 0f), 1.1f, c + (frameAng == 0f ? 0f : side));
                Cube(parent, "LandmarkFrame", p, new Vector3(frameAng == 0f ? 0.3f : 3.2f, 2.2f, frameAng == 0f ? 3.2f : 0.3f), 0f, _landmarkMat);
            }
        }

        // ── Template 19: Anomaly — tilted panels + distorted pillar cluster ──
        private static void BuildAnomaly(Transform parent, float c, float ceilingHeight, uint h)
        {
            // Three tilted wall panels at deterministic offsets/tilts.
            for (int i = 0; i < 3; i++)
            {
                uint hi = Step(h, i);
                float ox = Spread(hi, 14f);
                float oz = Spread(hi >> 8, 14f);
                float tilt = 8f + (hi >> 16) % 14;            // 8–21°
                float yaw = (hi >> 4) % 360;
                var go = Cube(parent, "AnomalyPanel",
                    new Vector3(c + ox, 1.3f, c + oz),
                    new Vector3(2.4f, 2.6f, 0.16f), yaw, _anomalyMat);
                go.transform.localRotation = Quaternion.Euler(tilt, yaw, 0f);
            }

            // Clustered distorted pillars: varying widths, slightly leaning.
            for (int i = 0; i < 4; i++)
            {
                uint hi = Step(h, 16 + i);
                float ox = 4f + (hi % 5);
                float oz = -4f - (float)((hi >> 8) % 5);
                float w = 0.5f + ((hi >> 16) % 10) * 0.08f;
                float lean = 2f + (hi >> 20) % 6;
                float ph = ceilingHeight - 0.3f;
                var go = Cube(parent, "AnomalyPillar",
                    new Vector3(c + ox, ph * 0.5f, c + oz),
                    new Vector3(w, ph, w), 0f, _anomalyDarkMat);
                go.transform.localRotation = Quaternion.Euler(lean, (hi >> 5) % 360, 0f);
            }

            // Irregular floor markers: thin offset dark plates.
            for (int i = 0; i < 3; i++)
            {
                uint hi = Step(h, 32 + i);
                Cube(parent, "AnomalyFloorMark",
                    new Vector3(c + Spread(hi, 16f), 0.03f, c + Spread(hi >> 10, 16f)),
                    new Vector3(1.8f, 0.05f, 1.4f), (hi >> 3) % 360, _anomalyDarkMat);
            }
        }

        // ── Template 20: Danger Pocket — barricades + broken panels ──
        private static void BuildDangerPocket(Transform parent, float c, uint h)
        {
            // Barricade cluster: stacked irregular blocks across the approach.
            for (int i = 0; i < 5; i++)
            {
                uint hi = Step(h, i);
                float ox = -6f + (hi % 12);
                float oz = 5f + (float)((hi >> 8) % 4);
                float bw = 1.2f + ((hi >> 16) % 8) * 0.15f;
                float bh = 0.7f + ((hi >> 20) % 6) * 0.12f;
                Cube(parent, "DangerBarricade",
                    new Vector3(c + ox, bh * 0.5f, c + oz),
                    new Vector3(bw, bh, 1.0f), (hi >> 4) % 30, _dangerMat);
            }

            // Broken leaning panels in two corners.
            for (int i = 0; i < 2; i++)
            {
                uint hi = Step(h, 8 + i);
                float side = i == 0 ? -1f : 1f;
                var go = Cube(parent, "DangerBrokenPanel",
                    new Vector3(c + side * 9f, 1.0f, c - 8f),
                    new Vector3(2.6f, 2.0f, 0.14f), 0f, _dangerDarkMat);
                go.transform.localRotation = Quaternion.Euler(14f + (hi % 10), side * 25f, 0f);
            }

            // Blocked-corner stack: harsh clustered cubes.
            for (int i = 0; i < 3; i++)
            {
                uint hi = Step(h, 12 + i);
                Cube(parent, "DangerCornerBlock",
                    new Vector3(c - 9f + i * 1.1f, 0.45f + (i % 2) * 0.5f, c + 9f - (float)(hi % 2)),
                    new Vector3(1.0f, 0.9f, 1.0f), (hi >> 6) % 45, _dangerDarkMat);
            }
        }

        // ── Template 21: Safe Pocket — organized shelter geometry ──
        private static void BuildSafePocket(Transform parent, float c, uint h)
        {
            // Shelter frame: two posts + lintel, an intentional built structure.
            float yaw = (h % 4) * 90f;
            float rad = yaw * Mathf.Deg2Rad;
            var dir = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
            var perp = new Vector3(-dir.z, 0f, dir.x);
            Vector3 anchor = new Vector3(c, 0f, c) + dir * 6f;
            Cube(parent, "SafePostA", anchor + perp * 1.6f + Vector3.up * 1.1f, new Vector3(0.3f, 2.2f, 0.3f), 0f, _safeMat);
            Cube(parent, "SafePostB", anchor - perp * 1.6f + Vector3.up * 1.1f, new Vector3(0.3f, 2.2f, 0.3f), 0f, _safeMat);
            var lintel = Cube(parent, "SafeLintel", anchor + Vector3.up * 2.25f, new Vector3(3.6f, 0.25f, 0.4f), 0f, _safeMat);
            lintel.transform.localRotation = Quaternion.Euler(0f, yaw + 90f, 0f);

            // Organized prop row: neat aligned crates along one side.
            for (int i = 0; i < 3; i++)
            {
                Vector3 p = new Vector3(c, 0.35f, c) - dir * 5f + perp * (i - 1) * 1.4f;
                Cube(parent, "SafeCrate", p, new Vector3(0.9f, 0.7f, 0.9f), yaw, _safeTrimMat);
            }

            // Clean center marker: a calm low platform, center deliberately open.
            Cube(parent, "SafePlatform", new Vector3(c, 0.06f, c), new Vector3(5.5f, 0.12f, 5.5f), 0f, _safeTrimMat);
        }

        // ── Helpers ──

        private static GameObject Cube(Transform parent, string name, Vector3 localPos, Vector3 scale, float yaw, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            // Visual-only: never a gameplay collider.
            var col = go.GetComponent<Collider>();
            if (col != null)
                Object.Destroy(col);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            if (yaw != 0f)
                go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            var r = go.GetComponent<Renderer>();
            if (r != null && mat != null)
                r.sharedMaterial = mat;
            return go;
        }

        // Deterministic hash over POI identity inputs; no UnityEngine.Random.
        private static uint Hash(int templateId, int cx, int cz, int layer, long seed)
        {
            unchecked
            {
                uint hh = (uint)seed ^ 0x9E3779B9u;
                hh = (hh ^ (uint)templateId) * 0x85EBCA77u;
                hh ^= hh >> 13;
                hh = (hh ^ (uint)cx) * 0xC2B2AE3Du;
                hh ^= hh >> 16;
                hh = (hh ^ (uint)cz) * 0x27D4EB2Fu;
                hh ^= hh >> 15;
                hh = (hh ^ (uint)layer) * 0x165667B1u;
                hh ^= hh >> 16;
                return hh;
            }
        }

        private static uint Step(uint h, int i)
        {
            unchecked
            {
                uint x = h + (uint)i * 0x9E3779B9u;
                x = (x ^ (x >> 16)) * 0x85EBCA6Bu;
                x = (x ^ (x >> 13)) * 0xC2B2AE35u;
                return x ^ (x >> 16);
            }
        }

        // Maps hash bits to a symmetric offset in [-range/2, +range/2].
        private static float Spread(uint h, float range)
        {
            return ((h & 0xFFFF) / 65535f - 0.5f) * range;
        }

        private static void EnsureMaterials()
        {
            if (_landmarkMat != null)
                return;
            _landmarkMat = Matte(new Color(0.62f, 0.58f, 0.44f));
            _landmarkGlowMat = MaterialHelper.MakeEmissive(new Color(0.95f, 0.92f, 0.72f), 0.9f);
            _anomalyMat = Matte(new Color(0.46f, 0.44f, 0.36f));
            _anomalyDarkMat = Matte(new Color(0.24f, 0.23f, 0.20f));
            _dangerMat = Matte(new Color(0.34f, 0.22f, 0.16f));
            _dangerDarkMat = Matte(new Color(0.18f, 0.13f, 0.10f));
            _safeMat = Matte(new Color(0.70f, 0.62f, 0.42f));
            _safeTrimMat = Matte(new Color(0.55f, 0.50f, 0.36f));
        }

        // Flat matte finish matching the ChunkRenderer surface look.
        private static Material Matte(Color color)
        {
            Material m = MaterialHelper.MakeLit(color);
            if (m == null)
                return m;
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.04f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            return m;
        }
    }
}
