using System.Collections.Generic;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// PURE, deterministic per-chunk loot roll — no Unity scene access (no Physics, no
    /// GameObjects), so it is unit-testable headlessly (EditMode, like ChunkStreamSchedulerTests).
    /// Given (worldSeed, chunkX, chunkZ) it always returns the SAME loot: which items/carryables a
    /// chunk contains, at which chunk-local normalized position, in which slot. The Unity side
    /// (<see cref="ChunkLootManager"/>) resolves item NAMES → STP definition ids and normalized
    /// (u,v) → world position via the walkable raycast; that part needs Play.
    ///
    /// The hash mirrors <c>ChunkRenderer.Level0Profile.FromSeedAndPos</c> (the project's existing
    /// seed+coord mixing pattern) so per-chunk determinism is consistent with the renderer.
    ///
    /// POOL TABLES are copied VERBATIM from StpItemSpawner / StpCarryableSpawner. They are NOT
    /// refactored out of those two files: the old scatter spawners stay byte-identical as the A/B
    /// comparison baseline (they are only DISABLED behind a flag, not edited). When the old
    /// spawners are deleted in a later prompt, their copies go and this becomes the single source.
    /// </summary>
    public static class ChunkLootRoll
    {
        // ── Pool tables (verbatim from the two spawners; see class doc) ──────────────────────
        private static readonly string[] ConsumablePool =
        {
            "Apple", "Cooked Meat", "Raw Meat", "Energy Bar", "Small Food Can", "Large Food Can", "Water Bottle"
        };
        private static readonly string[] MedicalPool = { "Antibiotics", "Medicinal Corn" };
        private static readonly string[] AmmoPool = { "30-30 Bullet", "Wooden Arrow", "Stone Arrow", "Metal Arrow" };
        private static readonly string[] MaterialPool =
        {
            "Stick", "Rope", "Cloth", "Leather", "Metal Shard", "Stone Shard", "Feather", "Duct Tape", "Wooden Torch"
        };
        private static readonly string[] WeaponPool =
        {
            "Marlin 336", "Wooden Bow", "Bone Club", "Hunting Axe", "Hunting Knife", "Steel Pickaxe", "Stone Spear", "Wooden Spear"
        };
        private const float WeaponRollChance = 0.15f;
        private static readonly string[] CarryableTypes = { "Log", "Stone", "Metal" };

        // ── Density (TODO(balance): first-playtest placeholders, mirror the old per-cache/zone
        // counts but now rolled PER CHUNK COLUMN so the live total is bounded by the ~3×3 ring
        // instead of a one-shot world-wide scatter). ──────────────────────────────────────────
        private const float ItemCacheChancePerChunk = 0.40f;
        private const int ItemsPerCache = 6;
        // TODO(balance): construction-material abundance test (2026-07-07) — carryable zones are
        // 100% construction materials (Log/Stone/Metal). Chance 0.30→0.60 and per-zone 8→16
        // (~x4 more materials). Not tuned final values.
        private const float CarryableZoneChancePerChunk = 0.60f;
        private const int CarryablesPerZone = 16;
        // Cluster tightness in chunk-normalized units (chunk side = 1.0). Item caches stay a tight
        // 1.5 m pile. Carryable zones: SPREAD widened 6 m → 12 m (2026-07-07) so the same 16
        // materials cover ~4× the area (≈1/4 the per-m² density) instead of a heavy concentrated
        // pile. 12 m / 50 m = 0.24 normalized; points past the chunk edge are clamped (mild).
        private const float CacheClusterRadius = 1.5f / 50f;
        private const float ZoneSpreadRadius = 12.0f / 50f;
        // Keep cluster centres off the chunk seams so a cache/zone never straddles two chunks.
        private const float CentreMargin = 0.18f;

        // Per-channel salts so items and carryables draw INDEPENDENT deterministic streams from the
        // same chunk (otherwise a chunk with a cache would bias its carryable roll).
        private const ulong ItemSalt = 0xA1_10_07_17UL;
        private const ulong CarrySalt = 0xC0_00_10_07UL;

        /// <summary>One rolled loot entry (pure). Name → STP def id and (u,v) → world pos are
        /// resolved by the Unity layer.</summary>
        public readonly struct Entry
        {
            public readonly int Slot;     // stable per-chunk index; identifies the entry for pickup memory
            public readonly string Name;  // STP ItemDefinition / CarryableDefinition name
            public readonly int Count;
            public readonly float U;      // chunk-local X in [0,1)
            public readonly float V;      // chunk-local Z in [0,1)
            public readonly float Rotation; // degrees

            public Entry(int slot, string name, int count, float u, float v, float rotation)
            {
                Slot = slot; Name = name; Count = count; U = u; V = v; Rotation = rotation;
            }
        }

        /// <summary>
        /// Seed+coord mix (mirror of ChunkRenderer.Level0Profile.FromSeedAndPos), XORed with a
        /// per-channel salt so different loot channels of the same chunk are independent.
        /// </summary>
        public static ulong Hash(long worldSeed, int cx, int cz, ulong salt)
        {
            ulong h = (ulong)worldSeed ^ 0x9E3779B97F4A7C15UL ^ salt;
            h += ((ulong)(uint)cx) * 0xFF51AFD7ED558CCDUL;
            h ^= h >> 33;
            h += ((ulong)(uint)cz) * 0xC4CEB9FE1A85EC53UL;
            h ^= h >> 29;
            h *= 0x9E3779B185EBCA87UL;
            h ^= h >> 32;
            return h;
        }

        /// <summary>Deterministic per-chunk item cache. Empty list = this chunk has no cache.</summary>
        public static List<Entry> RollItems(long worldSeed, int cx, int cz)
        {
            var rng = new DeterministicRng(Hash(worldSeed, cx, cz, ItemSalt));
            var result = new List<Entry>();
            if (rng.NextFloat() >= ItemCacheChancePerChunk)
                return result; // no cache in this chunk

            RollCentre(ref rng, out float cu, out float cv);
            for (int slot = 0; slot < ItemsPerCache; slot++)
            {
                string name = RollItemName(ref rng);
                ClusterAround(ref rng, cu, cv, CacheClusterRadius, out float u, out float v);
                result.Add(new Entry(slot, name, 1, u, v, rng.NextFloat() * 360f));
            }
            return result;
        }

        /// <summary>
        /// Deterministic per-chunk carryable resource zone. Empty = no zone. Each SLOT rolls its own
        /// material (2026-07-07: was one type per whole zone — felt too concentrated), so a zone mixes
        /// Log/Stone/Metal, spread over ZoneSpreadRadius.
        /// </summary>
        public static List<Entry> RollCarryables(long worldSeed, int cx, int cz)
        {
            var rng = new DeterministicRng(Hash(worldSeed, cx, cz, CarrySalt));
            var result = new List<Entry>();
            if (rng.NextFloat() >= CarryableZoneChancePerChunk)
                return result; // no zone in this chunk

            RollCentre(ref rng, out float cu, out float cv);
            for (int slot = 0; slot < CarryablesPerZone; slot++)
            {
                string name = RollMaterialName(ref rng); // per-slot MIX (was a single type per zone)
                ClusterAround(ref rng, cu, cv, ZoneSpreadRadius, out float u, out float v);
                result.Add(new Entry(slot, name, 1, u, v, rng.NextFloat() * 360f));
            }
            return result;
        }

        // Per-slot construction-material pick, weighted so basics dominate: Log 40% / Stone 35% /
        // Metal 25%. Same deterministic RNG stream, so same seed+chunk → same mix. TODO(balance).
        private static string RollMaterialName(ref DeterministicRng rng)
        {
            float r = rng.NextFloat();
            int idx = r < 0.40f ? 0 : (r < 0.75f ? 1 : 2);
            return CarryableTypes[idx]; // { "Log", "Stone", "Metal" }
        }

        /// <summary>
        /// Drop entries whose (chunk, slot) is in the session "already picked up" set — so a
        /// reloaded chunk regenerates everything EXCEPT what was taken. Pure (no Unity), shared by
        /// <see cref="ChunkLootManager"/> and its tests.
        /// </summary>
        public static void RemoveCollected(List<Entry> entries, int cx, int cz,
            ICollection<(int cx, int cz, int slot)> collected)
        {
            if (collected == null || collected.Count == 0)
                return;
            entries.RemoveAll(e => collected.Contains((cx, cz, e.Slot)));
        }

        private static void RollCentre(ref DeterministicRng rng, out float cu, out float cv)
        {
            cu = CentreMargin + rng.NextFloat() * (1f - 2f * CentreMargin);
            cv = CentreMargin + rng.NextFloat() * (1f - 2f * CentreMargin);
        }

        private static void ClusterAround(ref DeterministicRng rng, float cu, float cv, float radius,
            out float u, out float v)
        {
            // Uniform-ish point in a disc, then clamped to the chunk so an edge cluster never spills.
            float ang = rng.NextFloat() * 6.28318530718f;
            float r = radius * rng.NextFloat();
            u = Clamp01(cu + r * Cos(ang));
            v = Clamp01(cv + r * Sin(ang));
        }

        // Mirror of StpItemSpawner.RollItemName (weapon roll, else even split across the 3 common
        // pools with a 50/50 ammo/material tiebreak).
        private static string RollItemName(ref DeterministicRng rng)
        {
            if (rng.NextFloat() < WeaponRollChance)
                return WeaponPool[rng.NextInt(WeaponPool.Length)];

            int poolPick = rng.NextInt(3);
            string[] pool = poolPick switch
            {
                0 => ConsumablePool,
                1 => MedicalPool,
                _ => rng.NextFloat() < 0.5f ? AmmoPool : MaterialPool,
            };
            return pool[rng.NextInt(pool.Length)];
        }

        private static float Clamp01(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);
        // Tiny local trig (keeps this file free of a UnityEngine dependency for pure headless use).
        private static float Cos(float a) => (float)System.Math.Cos(a);
        private static float Sin(float a) => (float)System.Math.Sin(a);
    }

    /// <summary>
    /// SplitMix64 — a tiny deterministic RNG (byval struct). Same seed → same stream on every
    /// platform (unlike UnityEngine.Random, which is a global and not seedable inline). Used so a
    /// chunk's loot is reproducible across reloads without persisting anything.
    /// </summary>
    public struct DeterministicRng
    {
        private ulong _s;

        public DeterministicRng(ulong seed)
        {
            _s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        }

        public ulong NextU64()
        {
            _s += 0x9E3779B97F4A7C15UL;
            ulong z = _s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Uniform float in [0,1).</summary>
        public float NextFloat() => (NextU64() >> 40) * (1.0f / 16777216.0f);

        /// <summary>Uniform int in [0, maxExclusive).</summary>
        public int NextInt(int maxExclusive) => maxExclusive <= 0 ? 0 : (int)(NextU64() % (ulong)maxExclusive);
    }
}
