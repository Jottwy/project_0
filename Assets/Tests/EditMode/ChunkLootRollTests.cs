using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Headless tests for the PURE loot logic behind ChunkLootManager (no Play, no Physics):
    /// per-chunk determinism, the ring→column projection that decides load/unload, the
    /// collected-slot filter that stops a picked-up item reappearing on reload, and (Pieza 3) the
    /// zone-loot-profile plumbing: ZoneLootTable's bounds-safe lookup and the hard constraint that
    /// a profile may vary chance/pools/rarity but never entry COUNT. The GameObject placement
    /// (walkable raycast) AND the live zone gate (ZoneRegistry.TryGetZone + ChunkLootManager's
    /// CollectLoads) are Play-only and stay out of scope here — same declared boundary as before
    /// Pieza 3, just now covering one more Play-only dependency (ZoneRegistry is populated from
    /// live IPC state, not something to fake in a headless fixture).
    /// </summary>
    [TestFixture]
    public class ChunkLootRollTests
    {
        private const long Seed = 7778;
        /// <summary>Espejo de `ZONE_OFFICE`, el último zone_kind
        /// (backend/src/world/chunk/surface_profiles.rs).</summary>
        private const int ZoneOffice = 12;
        // Every pre-Pieza-3 test below exercises the PURE roll mechanics independent of zone —
        // using the NORMAL/default profile everywhere keeps their asserted behaviour numerically
        // identical to the flat pre-Pieza-3 constants (see ZoneLootProfile.Default's doc-comment).
        private static readonly ZoneLootProfile DefaultProfile = ZoneLootProfile.Default;

        private static bool SameEntries(List<ChunkLootRoll.Entry> a, List<ChunkLootRoll.Entry> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Slot != b[i].Slot || a[i].Name != b[i].Name || a[i].Count != b[i].Count) return false;
                if (a[i].U != b[i].U || a[i].V != b[i].V || a[i].Rotation != b[i].Rotation) return false;
            }
            return true;
        }

        [Test]
        public void RollItems_SameSeedAndChunk_IsDeterministic()
        {
            var a = ChunkLootRoll.RollItems(Seed, 3, -4, DefaultProfile);
            var b = ChunkLootRoll.RollItems(Seed, 3, -4, DefaultProfile);
            Assert.IsTrue(SameEntries(a, b), "same (seed, chunk) must yield identical loot");
        }

        [Test]
        public void RollCarryables_SameSeedAndChunk_IsDeterministic()
        {
            var a = ChunkLootRoll.RollCarryables(Seed, -10, 22, DefaultProfile);
            var b = ChunkLootRoll.RollCarryables(Seed, -10, 22, DefaultProfile);
            Assert.IsTrue(SameEntries(a, b));
        }

        [Test]
        public void RollCarryables_MixesMaterialsWithinAZone()
        {
            // 2026-07-07: a zone now MIXES Log/Stone/Metal per slot (was one type per whole zone).
            // Find the first chunk that rolls a zone in a band and assert its materials are not all
            // identical. (16 slots weighted 40/35/25 → a single-material zone is astronomically rare.)
            List<ChunkLootRoll.Entry> zone = null;
            for (int cx = 0; cx < 200; cx++)
            {
                var roll = ChunkLootRoll.RollCarryables(Seed, cx, 7, DefaultProfile);
                if (roll.Count > 0) { zone = roll; break; }
            }
            Assert.IsNotNull(zone, "expected at least one carryable zone in the scanned band");

            var distinct = new HashSet<string>();
            foreach (var e in zone) distinct.Add(e.Name);
            Assert.Greater(distinct.Count, 1, "a carryable zone must mix more than one material type");
            foreach (var e in zone)
                Assert.Contains(e.Name, new[] { "Log", "Stone", "Metal" }, "material must be one of the 3 construction types");
        }

        [Test]
        public void Roll_DiffersAcrossChunksAndSeeds()
        {
            // Not every chunk is identical (would mean the coord isn't mixed in). Scan a band and
            // assert we see more than one distinct outcome across chunks, and that changing the seed
            // changes a fixed chunk's roll.
            var seen = new HashSet<string>();
            for (int cx = 0; cx < 24; cx++)
                seen.Add(Signature(ChunkLootRoll.RollItems(Seed, cx, 0, DefaultProfile)));
            Assert.Greater(seen.Count, 1, "coord must influence the roll");

            string s1 = Signature(ChunkLootRoll.RollItems(Seed, 5, 5, DefaultProfile));
            string s2 = Signature(ChunkLootRoll.RollItems(Seed + 1, 5, 5, DefaultProfile));
            Assert.AreNotEqual(s1, s2, "seed must influence the roll");
        }

        [Test]
        public void Roll_SlotsAreContiguousFromZero()
        {
            // The collected-set keys on (chunk, slot); slots must be a stable 0..N-1 range so a
            // collected slot addresses the same entry on every reload.
            var items = ChunkLootRoll.RollItems(Seed, 100, 100, DefaultProfile);
            for (int i = 0; i < items.Count; i++)
                Assert.AreEqual(i, items[i].Slot);
        }

        [Test]
        public void RemoveCollected_DropsOnlyMarkedSlots_AndSurvivesReload()
        {
            // Find a chunk that actually rolls a cache (chance-gated), so there is something to drop.
            int cx = 0, cz = 0;
            List<ChunkLootRoll.Entry> entries = null;
            for (int i = 0; i < 200 && (entries == null || entries.Count < 2); i++)
            {
                var roll = ChunkLootRoll.RollItems(Seed, i, 0, DefaultProfile);
                if (roll.Count >= 2) { cx = i; entries = roll; break; }
            }
            Assert.IsNotNull(entries, "expected at least one item chunk in the scanned band");

            int keptSlot = entries[0].Slot;
            int takenSlot = entries[1].Slot;
            var collected = new HashSet<(int, int, int)> { (cx, cz, takenSlot) };

            // Simulate reload: fresh deterministic roll, then filter the collected slot.
            var reloaded = ChunkLootRoll.RollItems(Seed, cx, cz, DefaultProfile);
            ChunkLootRoll.RemoveCollected(reloaded, cx, cz, collected);

            Assert.IsFalse(reloaded.Exists(e => e.Slot == takenSlot), "collected slot must not reappear");
            Assert.IsTrue(reloaded.Exists(e => e.Slot == keptSlot), "un-collected slots must survive reload");
        }

        [Test]
        public void RemoveCollected_AllSlots_YieldsEmpty()
        {
            var entries = ChunkLootRoll.RollCarryables(Seed, 40, -40, DefaultProfile);
            if (entries.Count == 0) entries = ChunkLootRoll.RollCarryables(Seed, 41, -40, DefaultProfile);
            Assume.That(entries.Count, Is.GreaterThan(0));

            var collected = new HashSet<(int, int, int)>();
            foreach (var e in entries) collected.Add((40, -40, e.Slot));
            // Use the same chunk coords the collected set was built for.
            var fresh = new List<ChunkLootRoll.Entry>(entries);
            ChunkLootRoll.RemoveCollected(fresh, 40, -40, collected);
            Assert.AreEqual(0, fresh.Count);
        }

        [Test]
        public void DesiredColumns_ExcludeChunksOutsideRing()
        {
            // The ring the manager uses: BuildDesiredSet with layerCount=1 gives the (cx,cz) columns.
            var desired = new HashSet<(int, int, int)>();
            ChunkStreamer.BuildDesiredSet(cx: 0, cz: 0, viewRadius: 1, layerCount: 1, desired);

            var columns = new HashSet<(int, int)>();
            foreach (var k in desired) columns.Add((k.Item1, k.Item2));

            Assert.AreEqual(9, columns.Count, "3×3 ring = 9 columns");
            Assert.IsTrue(columns.Contains((0, 0)));
            Assert.IsTrue(columns.Contains((1, -1)));
            Assert.IsFalse(columns.Contains((2, 0)), "a chunk outside the ring is not desired → unloaded");
        }

        [Test]
        public void SelectExpired_PicksOnlyEntriesPastTheTtl()
        {
            // Material respawn timer (2026-07-07): a collected carryable slot expires once its stamp
            // is >= ttl old, freeing it to respawn. Fresh picks stay collected.
            const float ttl = 1800f; // 30 min
            float now = 5000f;
            var collected = new Dictionary<(int, int, int), float>
            {
                { (0, 0, 1), now - 1801f },  // just past ttl → expired
                { (0, 0, 2), now - 1800f },  // exactly ttl → expired (>=)
                { (3, 4, 0), now - 100f },   // fresh → stays
            };
            var into = new List<(int, int, int)>();
            ChunkLootManager.SelectExpired(collected, now, ttl, into);

            Assert.AreEqual(2, into.Count);
            Assert.Contains((0, 0, 1), into);
            Assert.Contains((0, 0, 2), into);
            Assert.IsFalse(into.Contains((3, 4, 0)), "a freshly-collected slot must not expire yet");
        }

        // ── Pieza 3: zone_kind → loot ──────────────────────────────────────────────────────

        [Test]
        public void RollItems_DifferentProfiles_AlwaysProduceTheSameNonEmptySlotCount()
        {
            // HARD CONSTRAINT (ZoneLootProfile's doc-comment): a zone's profile may vary
            // chance/pools/rarity, but must NEVER change entry COUNT — ChunkLootManager keys its
            // pickup memory on (cx,cz,slot), a bare ordinal from THIS roll. ItemsPerCache is a
            // fixed const, not a profile field, specifically so two different profiles rolling the
            // same (seed,cx,cz) either both roll empty or both roll the exact same non-empty count.
            var normal = DefaultProfile;
            var storage = ChunkLootRoll.DefaultZoneLootProfiles()[1]; // ZONE_STORAGE — deliberately different chance/pools/weapon rarity

            int? expectedCount = null;
            int nonEmptySamples = 0;
            for (int cx = 0; cx < 100 && nonEmptySamples < 10; cx++)
            {
                foreach (var profile in new[] { normal, storage })
                {
                    var entries = ChunkLootRoll.RollItems(Seed, cx, 0, profile);
                    if (entries.Count == 0) continue;
                    nonEmptySamples++;
                    if (expectedCount == null)
                        expectedCount = entries.Count;
                    else
                        Assert.AreEqual(expectedCount.Value, entries.Count,
                            "ItemsPerCache must be profile-independent — pickup-memory slot keys rely on a stable count");
                }
            }
            Assert.Greater(nonEmptySamples, 0, "expected at least one non-empty roll across the scanned band");
        }

        [Test]
        public void RollCarryables_DifferentProfiles_AlwaysProduceTheSameNonEmptySlotCount()
        {
            // Mirror of the RollItems test above, for the carryables channel (CarryablesPerZone).
            var normal = DefaultProfile;
            var humid = ChunkLootRoll.DefaultZoneLootProfiles()[6]; // ZONE_HUMID — deliberately different chance/material weights

            int? expectedCount = null;
            int nonEmptySamples = 0;
            for (int cx = 0; cx < 100 && nonEmptySamples < 10; cx++)
            {
                foreach (var profile in new[] { normal, humid })
                {
                    var entries = ChunkLootRoll.RollCarryables(Seed, cx, 0, profile);
                    if (entries.Count == 0) continue;
                    nonEmptySamples++;
                    if (expectedCount == null)
                        expectedCount = entries.Count;
                    else
                        Assert.AreEqual(expectedCount.Value, entries.Count,
                            "CarryablesPerZone must be profile-independent — pickup-memory slot keys rely on a stable count");
                }
            }
            Assert.Greater(nonEmptySamples, 0, "expected at least one non-empty roll across the scanned band");
        }

        [Test]
        public void DefaultZoneLootProfiles_CoverEveryZoneKind_AndNormalMatchesTheSharedDefault()
        {
            // 13 ZONE_* constants in backend/src/world/chunk/surface_profiles.rs
            // (0=Normal..12=Office). El conteo se afirma contra ZONE_OFFICE + 1 y no contra un
            // literal a propósito: este assert ya se quedó obsoleto UNA vez —al añadir
            // ZONE_OFFICE— y el nombre del test llevaba el número dentro, así que el fallo
            // pedía renombrar además de corregir. Anclado al último zone_kind, un futuro
            // ZONE_13 rompe el test por el motivo correcto en vez de por aritmética.
            var profiles = ChunkLootRoll.DefaultZoneLootProfiles();
            Assert.AreEqual(ZoneOffice + 1, profiles.Length);

            // Entry 0 (NORMAL) must keep the exact pre-Pieza-3 numbers via ZoneLootProfile.Default
            // (the ~83%-of-the-world default zone keeps today's already-playtested feel).
            var normal = profiles[0];
            var expected = ZoneLootProfile.Default;
            Assert.AreEqual(expected.itemCacheChance, normal.itemCacheChance);
            Assert.AreEqual(expected.carryableZoneChance, normal.carryableZoneChance);
            Assert.AreEqual(expected.weaponRollChance, normal.weaponRollChance);
        }

        [Test]
        public void ZoneLootTable_Profile_IsBoundsSafe()
        {
            // Mirrors LayerVisualConfig.ZoneTint's bounds-safety contract (Pieza 2): an
            // out-of-range index clamps, and a null/empty table never throws — it falls back to
            // ZoneLootProfile.Default rather than leaving a column permanently unrolled.
            var table = ScriptableObject.CreateInstance<ZoneLootTable>();
            try
            {
                var storage = table.Profile(1); // ZONE_STORAGE, in range
                Assert.AreEqual(ChunkLootRoll.DefaultZoneLootProfiles()[1].metalWeight, storage.metalWeight);

                Assert.DoesNotThrow(() => table.Profile(-1));
                Assert.DoesNotThrow(() => table.Profile(999));

                table.profiles = System.Array.Empty<ZoneLootProfile>();
                var fallback = table.Profile(5);
                Assert.AreEqual(ZoneLootProfile.Default.itemCacheChance, fallback.itemCacheChance);

                table.profiles = null;
                Assert.DoesNotThrow(() => table.Profile(0));
            }
            finally
            {
                Object.DestroyImmediate(table);
            }
        }

        private static string Signature(List<ChunkLootRoll.Entry> entries)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var e in entries) sb.Append(e.Slot).Append(':').Append(e.Name).Append('|');
            return sb.ToString();
        }

        // ── Fix priorizado worldgen (Alpha 1): loot dentro de muros ───────────────────────────
        // Covers the reachability invariant the audit flagged (ChunkLootManager.TryPlace only
        // raycast to the floor, which every tile has including walled ones, so it could never
        // reject a position). IsWalkable is pure (no Unity, no Physics) so this is a genuine
        // headless test, unlike the walkable RAYCAST itself (ChunkLootManager.TryPlace), which
        // stays Play-only per this file's declared boundary above.

        // 10x10 backend-convention bitmask (GridChunkDataMsg.Tiles), same layout ChunkLootRoll's
        // internal IsWalkable expects: low nibble N/S/E/W wall bits, high nibble pillar sub-cell.
        private static byte[,] Walls() => new byte[10, 10];

        // Chunk-local normalized (u,v) for a point at tile (tx,tz), offset (localX,localZ) in
        // metres within that 5 m tile. Mirrors ChunkLootManager's own wx/wz = cx*Side + u*Side.
        private static (float u, float v) UvAt(int tx, float localX, int tz, float localZ) =>
            ((tx * 5f + localX) / 50f, (tz * 5f + localZ) / 50f);

        [Test]
        public void IsWalkable_EmptyBitmask_EverySampledPointIsWalkable()
        {
            var walls = Walls(); // all zero — no walls, no pillars anywhere
            var (cu, cv) = UvAt(4, 2.5f, 4, 2.5f); // tile centre
            Assert.IsTrue(ChunkLootRoll.IsWalkable(walls, cu, cv));

            var (eu, ev) = UvAt(9, 4.99f, 9, 4.99f); // far corner, near the u/v==1.0 edge
            Assert.IsTrue(ChunkLootRoll.IsWalkable(walls, eu, ev));
        }

        [Test]
        public void IsWalkable_PillarSubCell_RejectsOnlyThatSubCell()
        {
            var walls = Walls();
            walls[3, 4] = 0x10; // PillarNW: the (x0,z0) sub-cell of tile (3,4)

            var (nwU, nwV) = UvAt(3, 1.0f, 4, 1.0f); // west half, north half → NW sub-cell
            Assert.IsFalse(ChunkLootRoll.IsWalkable(walls, nwU, nwV), "point inside the pillar sub-cell must reject");

            var (seU, seV) = UvAt(3, 4.0f, 4, 4.0f); // east half, south half → SE sub-cell, no pillar there
            Assert.IsTrue(ChunkLootRoll.IsWalkable(walls, seU, seV), "a different sub-cell of the same tile must stay walkable");
        }

        [Test]
        public void IsWalkable_WalledEdge_RejectsNearThatEdgeOnly()
        {
            var walls = Walls();
            walls[5, 5] = 1; // WallN (−Z edge) of tile (5,5)

            var (edgeU, edgeV) = UvAt(5, 2.5f, 5, 0.05f); // 5 cm from the north edge — inside WallThickness/2
            Assert.IsFalse(ChunkLootRoll.IsWalkable(walls, edgeU, edgeV), "point within wall thickness of a walled edge must reject");

            var (centreU, centreV) = UvAt(5, 2.5f, 5, 2.5f); // tile centre, well clear of the edge
            Assert.IsTrue(ChunkLootRoll.IsWalkable(walls, centreU, centreV), "tile centre away from the walled edge must stay walkable");
        }

        [Test]
        public void IsWalkable_UvExactlyOne_ClampsInsteadOfThrowing()
        {
            var walls = Walls();
            Assert.DoesNotThrow(() => ChunkLootRoll.IsWalkable(walls, 1f, 1f));
            Assert.IsTrue(ChunkLootRoll.IsWalkable(walls, 1f, 1f), "u/v==1.0 must clamp to the last tile, not index out of range");
        }

        [Test]
        public void IsWalkable_NullBitmask_ReturnsFalseInsteadOfThrowing()
        {
            Assert.DoesNotThrow(() => ChunkLootRoll.IsWalkable(null, 0.5f, 0.5f));
            Assert.IsFalse(ChunkLootRoll.IsWalkable(null, 0.5f, 0.5f));
        }
    }
}
