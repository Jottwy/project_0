// Partición de GridChunkBuilder: tintado y hashes deterministas —
// Paint/JitterValue/Damp y TileSeed/CeilingHash/MoistureAt/Hash01. El resto
// vive en GridChunkBuilder.cs (raíz), .Placement.cs, .WallVariants.cs y .Props.cs.
// TODOS los campos estáticos (incl. _mpb y _rendererScratch) viven en el raíz:
// el orden de inicialización de estáticos entre partials es indefinido.
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    public static partial class GridChunkBuilder
    {
        /// <summary>Assign the shared material to every MeshRenderer and override
        /// <c>_BaseColor</c> per-renderer with <paramref name="tint"/> via a property
        /// block — no material instance is created.</summary>
        private static void Paint(GameObject go, Material mat, Color tint)
        {
            _mpb.Clear();
            _mpb.SetColor(LayerVisualMaterials.BaseColorId, tint);
            // Same non-allocating overload as AddColliderIfMissing; the two never nest, so they
            // can share one scratch buffer.
            go.GetComponentsInChildren(_rendererScratch);
            for (int i = 0; i < _rendererScratch.Count; i++)
            {
                var r = _rendererScratch[i];
                if (mat != null) r.sharedMaterial = mat;
                r.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>Jitter the HSV Value of <paramref name="baseColor"/> by ±8%
        /// (deterministic via <paramref name="rng"/>) to break tile uniformity.</summary>
        private static Color JitterValue(Color baseColor, System.Random rng)
        {
            if (rng == null) return baseColor;
            Color.RGBToHSV(baseColor, out float h, out float s, out float v);
            float k = 1f + (float)(rng.NextDouble() * 2.0 - 1.0) * 0.08f;
            var c = Color.HSVToRGB(h, Mathf.Clamp01(s), Mathf.Clamp01(v * k));
            c.a = baseColor.a;
            return c;
        }

        /// <summary>Pieza F — <paramref name="tint"/> darkened by <paramref name="stain"/>
        /// when <paramref name="damp"/>. Kept as a one-liner so the wall and pillar call
        /// sites can stay single expressions and not disturb the rng draw order.</summary>
        private static Color Damp(Color tint, bool damp, Color stain) => damp ? tint * stain : tint;

        /// <summary>Deterministic per-tile seed (no UnityEngine.Random).</summary>
        private static int TileSeed(int cx, int cz, int tx, int tz)
        {
            unchecked { return cx * 73856093 ^ cz * 19349663 ^ tx * 83492791 ^ tz; }
        }

        /// <summary>Deterministic per-tile hash → four floats in [0,1), independent of the
        /// jitter rng (so it never perturbs floor/wall tints).</summary>
        private static void CeilingHash(int cx, int cz, int tx, int tz,
            out float a, out float b, out float c, out float d)
        {
            unchecked
            {
                ulong h = 0x9E3779B97F4A7C15UL;
                h ^= (ulong)(uint)cx * 0xFF51AFD7ED558CCDUL; h ^= h >> 33;
                h ^= (ulong)(uint)cz * 0xC4CEB9FE1A85EC53UL; h ^= h >> 29;
                h ^= (ulong)(uint)tx * 0x165667B19E3779F9UL; h ^= h >> 32;
                h ^= (ulong)(uint)tz * 0x27D4EB2F165667C5UL; h ^= h >> 30;
                h *= 0x9E3779B185EBCA87UL; h ^= h >> 32;
                a = ((h      ) & 0xFFFF) / 65535f;
                b = ((h >> 16) & 0xFFFF) / 65535f;
                c = ((h >> 32) & 0xFFFF) / 65535f;
                d = ((h >> 48) & 0xFFFF) / 65535f;
            }
        }

        /// <summary>Moisture cluster value in [0,1): coarse 2-tile cells (in GLOBAL tile
        /// coords) make adjacent tiles share a base so stains cluster and tile across chunk
        /// seams; a per-tile jitter softens the block edges. Below the surface's threshold
        /// ⇒ stained. Pieza F: the salts are parameters so each surface gets its OWN damp
        /// field — sharing one would stack a floor stain under every ceiling stain, which
        /// reads as a lighting bug rather than water damage.</summary>
        private static float MoistureAt(int cx, int cz, int tx, int tz, uint saltCell, uint saltJit)
        {
            int gx = cx * Tiles + tx, gz = cz * Tiles + tz;
            float cell = Hash01(gx >> 1, gz >> 1, saltCell); // shared by a 2×2 block
            float jit  = Hash01(gx, gz, saltJit);            // per-tile
            return cell * 0.8f + jit * 0.2f;
        }

        /// <summary>Deterministic [0,1) hash of two ints under <paramref name="salt"/>.
        /// Shared with <see cref="World.BackroomsLighting"/> (Pieza D) so lamp placement
        /// and surface variety draw from the same generator instead of a near-copy that
        /// can drift.</summary>
        internal static float Hash01(int x, int y, uint salt)
        {
            unchecked
            {
                uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ salt;
                h ^= h >> 15; h *= 0x2C1B3C6DU; h ^= h >> 12; h *= 0x297A2D39U; h ^= h >> 15;
                return (h & 0xFFFFFFU) / 16777215f;
            }
        }
    }
}
