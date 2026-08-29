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

        /// <summary>
        /// Perfiles de PRUEBA con las probabilidades forzadas, introducidos por el recorte de
        /// escasez de 2026-08-17. Antes de ese recorte los tests de MECÁNICA (determinismo,
        /// influencia de coord/semilla, filtro de recogidos, mezcla de materiales, invariante de
        /// COUNT) se apoyaban en que el perfil enviado fuera abundante: escaneaban una banda de
        /// chunks confiando en encontrar loot. Con la caché al 4% y las zonas de carryable a CERO,
        /// esos escaneos salen vacíos y el test falla por RAREZA, no por la mecánica que mide —
        /// uno de ellos (Roll_DiffersAcrossChunksAndSeeds) habría fallado ~1 de cada 3
        /// ejecuciones, que es la peor clase de rojo.
        ///
        /// Separar las dos cosas es deliberado: la mecánica se prueba con estos perfiles y el
        /// balance enviado se prueba aparte, en la región "Recorte de escasez" del final. Así
        /// afinar rareza nunca vuelve a tocar los tests de mecánica.
        /// </summary>
        private static ZoneLootProfile RichProfile(float itemChance, float carryChance)
        {
            var p = ZoneLootProfile.Default;
            p.itemCacheChance = itemChance;
            p.carryableZoneChance = carryChance;
            return p;
        }

        private static readonly ZoneLootProfile RichItemProfile = RichProfile(0.40f, 0f);
        private static readonly ZoneLootProfile RichCarryProfile = RichProfile(0f, 0.60f);

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
            var a = ChunkLootRoll.RollCarryables(Seed, -10, 22, RichCarryProfile);
            var b = ChunkLootRoll.RollCarryables(Seed, -10, 22, RichCarryProfile);
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
                var roll = ChunkLootRoll.RollCarryables(Seed, cx, 7, RichCarryProfile);
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
                seen.Add(Signature(ChunkLootRoll.RollItems(Seed, cx, 0, RichItemProfile)));
            Assert.Greater(seen.Count, 1, "coord must influence the roll");

            // Con el catálogo recortado a un solo nombre, la FIRMA de dos cachés no vacías es
            // idéntica ("0:Spray Can|1:Spray Can"), así que lo que distingue una tirada de otra es
            // que la caché exista o no. Basta para probar que la semilla entra en el mezclador, y
            // es lo mismo que medía antes con nombres distintos. Se busca un par de chunks donde
            // difieran en vez de fijar uno a mano: fijarlo lo ataría al valor concreto de Seed.
            bool seedChangedSomething = false;
            for (int cx = 0; cx < 64 && !seedChangedSomething; cx++)
            {
                string s1 = Signature(ChunkLootRoll.RollItems(Seed, cx, 5, RichItemProfile));
                string s2 = Signature(ChunkLootRoll.RollItems(Seed + 1, cx, 5, RichItemProfile));
                seedChangedSomething = s1 != s2;
            }
            Assert.IsTrue(seedChangedSomething, "seed must influence the roll");
        }

        [Test]
        public void Roll_SlotsAreContiguousFromZero()
        {
            // The collected-set keys on (chunk, slot); slots must be a stable 0..N-1 range so a
            // collected slot addresses the same entry on every reload.
            var items = ChunkLootRoll.RollItems(Seed, 100, 100, RichItemProfile);
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
                var roll = ChunkLootRoll.RollItems(Seed, i, 0, RichItemProfile);
                if (roll.Count >= 2) { cx = i; entries = roll; break; }
            }
            Assert.IsNotNull(entries, "expected at least one item chunk in the scanned band");

            int keptSlot = entries[0].Slot;
            int takenSlot = entries[1].Slot;
            var collected = new HashSet<(int, int, int)> { (cx, cz, takenSlot) };

            // Simulate reload: fresh deterministic roll, then filter the collected slot.
            var reloaded = ChunkLootRoll.RollItems(Seed, cx, cz, RichItemProfile);
            ChunkLootRoll.RemoveCollected(reloaded, cx, cz, collected);

            Assert.IsFalse(reloaded.Exists(e => e.Slot == takenSlot), "collected slot must not reappear");
            Assert.IsTrue(reloaded.Exists(e => e.Slot == keptSlot), "un-collected slots must survive reload");
        }

        [Test]
        public void RemoveCollected_AllSlots_YieldsEmpty()
        {
            var entries = ChunkLootRoll.RollCarryables(Seed, 40, -40, RichCarryProfile);
            if (entries.Count == 0) entries = ChunkLootRoll.RollCarryables(Seed, 41, -40, RichCarryProfile);
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
            // Las dos probabilidades se fuerzan al mismo valor alto: lo que este test mide es que
            // dos perfiles con POOLS y RAREZA distintas den el mismo COUNT, no cuál de los dos
            // saca caché más a menudo. Sin forzarlas, tras el recorte de escasez el escaneo sale
            // casi vacío y el test mediría rareza (ver RichProfile).
            var normal = RichItemProfile;
            var storage = ChunkLootRoll.DefaultZoneLootProfiles()[1]; // ZONE_STORAGE — deliberately different pools/weapon rarity
            storage.itemCacheChance = RichItemProfile.itemCacheChance;

            // La comparación es POR CHUNK, no global. Desde el reparto 80/15/5 (2026-08-17) el
            // count varía ENTRE chunks a propósito; lo que la restricción dura exige es que no
            // varíe entre PERFILES sobre el MISMO chunk. La versión anterior de este test
            // comparaba todas las muestras contra la primera, así que a partir del reparto habría
            // fallado por el motivo equivocado.
            int nonEmptySamples = 0;
            for (int cx = 0; cx < 100 && nonEmptySamples < 10; cx++)
            {
                var a = ChunkLootRoll.RollItems(Seed, cx, 0, normal);
                var b = ChunkLootRoll.RollItems(Seed, cx, 0, storage);
                Assert.AreEqual(a.Count, b.Count,
                    $"chunk {cx}: el COUNT debe ser independiente del perfil — las claves de la memoria de recogida dependen de ello");
                if (a.Count > 0) nonEmptySamples++;
            }
            Assert.Greater(nonEmptySamples, 0, "expected at least one non-empty roll across the scanned band");
        }

        [Test]
        public void RollCarryables_DifferentProfiles_AlwaysProduceTheSameNonEmptySlotCount()
        {
            // Mirror of the RollItems test above, for the carryables channel (CarryablesPerZone).
            // Mismo forzado de probabilidad que el test de RollItems, y por la misma razón — aquí
            // además es obligatorio: el recorte dejó carryableZoneChance a CERO en las 13 zonas,
            // así que sin forzarla NINGUNA tirada saldría no vacía.
            var normal = RichCarryProfile;
            var humid = ChunkLootRoll.DefaultZoneLootProfiles()[6]; // ZONE_HUMID — deliberately different material weights
            humid.carryableZoneChance = RichCarryProfile.carryableZoneChance;

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

        // ── Recorte de escasez (2026-08-17) ────────────────────────────────────────────────
        // Fijan el BALANCE ENVIADO, no la mecánica (que se prueba arriba con RichProfile). Un
        // rojo aquí significa "alguien deshizo el recorte", no "la lógica está mal" — si el
        // recorte se levanta a propósito, estos tests se borran o se invierten en el mismo
        // commit que lo levante.
        //
        // El asset serializado se comprueba APARTE de los defaults de C#: es el que manda en
        // juego (ChunkLootManager lo carga con Resources.Load) y los defaults solo aplican a un
        // asset que nunca se ha serializado, así que tocar únicamente el C# no cambiaría nada de
        // lo que ve el jugador. Además se editó por script, y el YAML de Unity se corrompe en
        // silencio.

        /// <summary>Techo de rareza del recorte: la zona más rica (ZONE_PIT) se quedó en 0.07.</summary>
        private const float MaxCacheChanceAfterCut = 0.07f;

        [Test]
        public void Recorte_CachesDelMundo_SoloSueltanSprayCan()
        {
            // El agua de almendras NO puede aparecer aquí: es chest-only por la enmienda de
            // ADR-030, y ese es justo el reparto que el recorte conserva.
            int sampled = 0;
            for (int cx = 0; cx < 200; cx++)
            {
                foreach (var e in ChunkLootRoll.RollItems(Seed, cx, 3, RichItemProfile))
                {
                    sampled++;
                    Assert.AreEqual("Spray Can", e.Name,
                        "el recorte de 2026-08-17 deja el mundo suelto con un solo objeto");
                }
            }
            Assert.Greater(sampled, 0, "expected at least one item across the scanned band");
        }

        [Test]
        public void Recorte_TamanoDeCache_SigueElReparto80_15_5()
        {
            // Reparto pedido por Joel (2026-08-17): 80% un objeto, 15% dos, 5% tres. Se mide sobre
            // RollItems (el camino real) y no sobre RollItemCount, que es internal y además dejaría
            // sin cubrir que el count se tira ANTES de leer el perfil.
            //
            // Muestra grande y márgenes anchos (±3 puntos) a propósito: esto fija la FORMA del
            // reparto —la mayoría de cachés son un objeto suelto y tres es raro— no los decimales
            // exactos de un generador concreto. Un margen estrecho lo convertiría en un test que
            // se rompe al tocar la semilla.
            var histogram = new Dictionary<int, int>();
            int caches = 0;
            for (int cx = 0; cx < 300; cx++)
                for (int cz = 0; cz < 40; cz++)
                {
                    int n = ChunkLootRoll.RollItems(Seed, cx, cz, RichItemProfile).Count;
                    if (n == 0) continue;
                    caches++;
                    histogram.TryGetValue(n, out int prev);
                    histogram[n] = prev + 1;
                }

            Assert.Greater(caches, 2000, "muestra insuficiente para medir un reparto");
            foreach (var size in histogram.Keys)
                Assert.That(size, Is.InRange(1, 3), "una caché nunca trae menos de 1 ni más de 3 objetos");

            double one = 100.0 * histogram[1] / caches;
            double two = 100.0 * histogram[2] / caches;
            double three = 100.0 * histogram[3] / caches;
            Assert.That(one, Is.EqualTo(80.0).Within(3.0), $"un objeto debería ser ~80% (medido {one:F1}%)");
            Assert.That(two, Is.EqualTo(15.0).Within(3.0), $"dos objetos debería ser ~15% (medido {two:F1}%)");
            Assert.That(three, Is.EqualTo(5.0).Within(3.0), $"tres objetos debería ser ~5% (medido {three:F1}%)");
        }

        [Test]
        public void Recorte_TodasLasZonas_TienenCarryablesApagadosYCachesRaras()
        {
            var profiles = ChunkLootRoll.DefaultZoneLootProfiles();
            for (int zone = 0; zone < profiles.Length; zone++)
            {
                Assert.AreEqual(0f, profiles[zone].carryableZoneChance,
                    $"zone_kind {zone}: las zonas de carryable están apagadas por el recorte");
                Assert.LessOrEqual(profiles[zone].itemCacheChance, MaxCacheChanceAfterCut,
                    $"zone_kind {zone}: la rareza de caché no debe volver a subir sin deshacer el recorte");
            }
        }

        [Test]
        public void Recorte_AssetEnviado_CoincideConLosDefaultsDeCodigo()
        {
            var table = Resources.Load<ZoneLootTable>("Loot/ZoneLootTable");
            Assert.IsNotNull(table, "Assets/Resources/Loot/ZoneLootTable.asset debe existir — es lo que ChunkLootManager carga en juego");

            var defaults = ChunkLootRoll.DefaultZoneLootProfiles();
            Assert.AreEqual(defaults.Length, table.profiles.Length,
                "un asset más corto NO lanza: ZoneLootTable.Profile hace Clamp y sirve la última entrada a toda zona superior");

            for (int zone = 0; zone < table.profiles.Length; zone++)
            {
                Assert.AreEqual(0f, table.profiles[zone].carryableZoneChance,
                    $"zone_kind {zone}: el asset serializado también debe traer los carryables apagados");
                Assert.AreEqual(defaults[zone].itemCacheChance, table.profiles[zone].itemCacheChance, 0.0001f,
                    $"zone_kind {zone}: asset y defaults de código se desincronizaron — manda el asset");
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

        // ── ADR-108 D4 — el reparto por PAPEL ───────────────────────────────────────────────

        /// <summary>Un espacio que todavía no está montado NO es una caché vacía. Distinguirlo es
        /// lo único que impide sellar la columna para siempre con el resultado de no haber podido
        /// preguntar — el mismo fallo que la puerta de zona de WG2 evita esperando.</summary>
        [Test]
        public void RollByStyle_UnknownSpace_ReportsNotKnownInsteadOfEmpty()
        {
            var got = ChunkLootRoll.RollItemsByStyle(Seed, 3, 4, (u, v) => null, out bool known);
            Assert.IsFalse(known, "sin geometría montada la respuesta es «no sé», no «no hay»");
            Assert.AreEqual(0, got.Count);
        }

        /// <summary>Un papel con la caché a cero no deja nada, y sí se sabe: la columna se sella.
        /// Es el caso de la escalera.</summary>
        [Test]
        public void RollByStyle_ProfileWithNoCache_IsKnownAndEmpty()
        {
            var barren = ZoneLootProfile.Default;
            barren.itemCacheChance = 0f;
            var got = ChunkLootRoll.RollItemsByStyle(Seed, 3, 4, (u, v) => barren, out bool known);
            Assert.IsTrue(known);
            Assert.AreEqual(0, got.Count);
        }

        /// <summary>El perfil se pide por el CENTRO de la caché, una sola vez, y ese centro cae
        /// dentro del chunk. Sin esto el sorteo dependería del orden en que se coloquen los huecos.
        /// </summary>
        [Test]
        public void RollByStyle_AsksForTheProfileOnce_AtACentreInsideTheChunk()
        {
            int calls = 0;
            var seen = new List<(float u, float v)>();
            ChunkLootRoll.RollItemsByStyle(Seed, 0, 0, (u, v) =>
            {
                calls++;
                seen.Add((u, v));
                return ZoneLootProfile.Default;
            }, out _);
            Assert.AreEqual(1, calls, "una pregunta por columna, no una por hueco");
            Assert.That(seen[0].u, Is.InRange(0f, 1f));
            Assert.That(seen[0].v, Is.InRange(0f, 1f));
        }

        /// <summary>Mismo chunk, mismo papel, mismo resultado — el sorteo sigue siendo determinista
        /// aunque el centro se haya adelantado en el flujo de números.</summary>
        [Test]
        public void RollByStyle_IsDeterministic()
        {
            var rich = RichItemProfile;
            var a = ChunkLootRoll.RollItemsByStyle(Seed, 11, -6, (u, v) => rich, out _);
            var b = ChunkLootRoll.RollItemsByStyle(Seed, 11, -6, (u, v) => rich, out _);
            Assert.AreEqual(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].Name, b[i].Name);
                Assert.AreEqual(a[i].U, b[i].U);
                Assert.AreEqual(a[i].V, b[i].V);
            }
        }

        /// <summary>Los siete papeles existen y la densidad NO se ha tocado: todos llevan el
        /// `itemCacheChance` de la prueba de escasez, salvo la escalera, que no deja nada.
        /// Si alguien sube uno de estos números, que sea a sabiendas y no de rebote.</summary>
        [Test]
        public void DefaultStyleProfiles_KeepTodaysScarcity()
        {
            var t = ChunkLootRoll.DefaultStyleLootProfiles();
            Assert.AreEqual(7, t.Length, "un perfil por style de fill::style_of (0-6)");
            for (int i = 0; i < t.Length; i++)
            {
                Assert.AreEqual(0f, t[i].carryableZoneChance, $"papel {i}: los transportables siguen apagados");
                float expected = i == 6 ? 0f : ZoneLootProfile.Default.itemCacheChance;
                Assert.AreEqual(expected, t[i].itemCacheChance, 1e-6f, $"papel {i}: la densidad no cambia con esta tarea");
            }
        }

        /// <summary>El campo nuevo llega VACÍO a un asset que ya estaba serializado, y eso es lo
        /// normal, no un fallo: tiene que caer a los valores del código y no servir el perfil 0 a
        /// los siete papeles, que anularía la decisión entera en silencio.</summary>
        [Test]
        public void ZoneLootTable_EmptyStyleProfiles_FallsBackToCodeDefaults()
        {
            var table = ScriptableObject.CreateInstance<ZoneLootTable>();
            table.styleProfiles = System.Array.Empty<ZoneLootProfile>();
            var code = ChunkLootRoll.DefaultStyleLootProfiles();
            for (int i = 0; i < code.Length; i++)
                Assert.AreEqual(code[i].materialWeight, table.ProfileForStyle(i).materialWeight,
                                $"papel {i} tiene que salir del código mientras el asset no lo autore");
            Assert.AreEqual(code[code.Length - 1].materialWeight,
                            table.ProfileForStyle(99).materialWeight, "fuera de rango se clampa");
            Object.DestroyImmediate(table);
        }
    }
}
