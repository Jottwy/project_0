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
    /// POOL TABLES nacieron copiadas VERBATIM de StpItemSpawner / StpCarryableSpawner y YA NO LO
    /// SON: el recorte de catálogo vendor (2026-08-10) borró AmmoPool y adelgazó WeaponPool AQUÍ y
    /// en StpChestSpawner, dejando a los dos spawners viejos con el catálogo completo. Eso es
    /// deliberado — siguen byte-idénticos como baseline A/B (están DESACTIVADOS tras un flag, no
    /// editados), así que a partir de ahora el A/B compara además catálogos distintos: si se
    /// reactivan para comparar, el rifle y el arco reaparecen por esa vía. Cuando esos spawners se
    /// borren, sus copias se van y este fichero queda como fuente única.
    ///
    /// PIEZA 3 (zone_kind → loot): RollItems/RollCarryables now take a <see cref="ZoneLootProfile"/>
    /// (resolved by the Unity side from ZoneRegistry + ZoneLootTable) that varies cache/zone chance,
    /// pool weights and material weights per zone_kind. See ZoneLootProfile's doc-comment for the
    /// hard constraint this must preserve (never change entry COUNT).
    /// </summary>
    public static class ChunkLootRoll
    {
        // ── Pool tables (verbatim from the two spawners; see class doc) ──────────────────────
        // "Almond Water" is deliberately NOT here (ADR-030 amendment) — chest-only by design, see
        // the exception note on StpChestSpawner.ConsumablePool. Not an oversight, and also can't
        // add it here without a weight bump anyway: the hard constraint below is never change
        // entry COUNT without a matching ZoneLootProfile/ZoneLootTable.asset change.
        private static readonly string[] ConsumablePool =
        {
            "Apple", "Cooked Meat", "Raw Meat", "Energy Bar", "Small Food Can", "Large Food Can", "Water Bottle"
        };
        private static readonly string[] MedicalPool = { "Antibiotics", "Medicinal Corn" };
        // RECORTE DE CATÁLOGO VENDOR (2026-08-10): AmmoPool ELIMINADA junto con el rifle y el arco.
        // Munición sin arma de fuego es basura de inventario. `profile.ammoWeight` sobrevive en
        // ZoneLootProfile / ZoneLootTable.asset pero su masa de probabilidad ahora cae en
        // MaterialPool (ver RollItemName) — campo VIVO pero redirigido, no muerto: sigue moviendo
        // el reparto material/consumible por zona. No borrar creyendo que no lo lee nadie.
        // ADR-064 (DIFERIDO, no olvidado): los materiales de crafteo Metal/Circuit/Battery/Cable van
        // AQUÍ cuando el flujo de crafteo se implemente — no en una pool propia, por la restricción
        // dura de esta clase: un perfil de zona puede variar pools y rareza, nunca el COUNT de
        // entradas, y una pool nueva exigiría un peso nuevo en ZoneLootProfile y en el
        // ZoneLootTable.asset ya serializado.
        // NO se añaden todavía A PROPÓSITO: sus ItemDefinition existen solo como generador de menú
        // ("Backrooms ▸ Create Craft Materials") que nadie ha ejecutado. Con los nombres aquí y los
        // assets sin generar, `GetWithName` devuelve null y ChunkLootManager DESCARTA el slot con un
        // warning — o sea ~4/13 de los slots de material caídos y un warning por cada uno. Añadirlos
        // y generar los assets es un solo paso, no dos.
        // TODO(balance) para cuando se añadan: a 13 entradas equiprobables un material sale ~4/13 de
        // los slots de esta pool, contra un T3 que cuesta 130 unidades.
        // ADR-068 S4: "Spray Can" entra AQUÍ y no en una pool propia, por la restricción dura de
        // arriba — una pool nueva exigiría un peso nuevo en ZoneLootProfile y en el
        // ZoneLootTable.asset ya serializado. Va en la de materiales y no en la de armas porque
        // se lee como el objeto encontrado en un edificio que es, y porque "Wooden Torch" ya
        // sienta el precedente de un wieldable en esta pool.
        //
        // Su ItemDefinition SÍ existe y resuelve (`BR_Spray Can` → Name "Spray Can"), a
        // diferencia de los materiales de crafteo de ADR-064 que siguen fuera a propósito: con
        // el nombre aquí y el asset sin generar, GetWithName devolvería null y el slot se
        // descartaría con un warning.
        //
        // TODO(balance): a 10 entradas equiprobables, un bote sale en ~1/10 de los slots de esta
        // pool. Es deliberadamente encontrable — marcar el camino es la mecánica, no el premio —
        // pero nadie ha medido todavía cuántos botes acumula una sesión larga.
        private static readonly string[] MaterialPool =
        {
            "Stick", "Rope", "Cloth", "Leather", "Metal Shard", "Stone Shard", "Feather", "Duct Tape",
            "Wooden Torch", "Spray Can"
        };
        // Recorte de catálogo vendor: fuera el rifle y el arco (armas de fuego/caza ajenas al tono)
        // y el kit de caza (Hunting Axe/Knife, Stone/Wooden Spear). Quedan los dos que se leen como
        // objeto encontrado en un edificio, no como equipo de cazador.
        private static readonly string[] WeaponPool =
        {
            "Bone Club", "Steel Pickaxe"
        };
        private static readonly string[] CarryableTypes = { "Log", "Stone", "Metal" };

        // ── RECORTE TOTAL DE CATÁLOGO (2026-08-17), prueba de escasez ────────────────────────
        // El mundo suelto solo suelta "Spray Can". El agua de almendras NO entra aquí: sigue
        // siendo chest-only por la enmienda de ADR-030, así que las dos únicas fuentes de loot
        // del juego pasan a ser esta pool y StpChestSpawner (que en la misma tanda queda
        // reducido a esos mismos dos objetos).
        //
        // Las cuatro pools de arriba quedan VIVAS y sin editar A PROPÓSITO: la única puerta es
        // RestrictCacheCatalog, así que levantar el recorte es poner un bool a false, no
        // reconstruir listas. Criterio deliberadamente distinto al del recorte de catálogo
        // vendor de 2026-08-10, que borró entradas y por eso no se puede revertir de un tirón —
        // aquel era permanente (arte fuera de tono), este es una prueba de balance.
        private static readonly string[] RestrictedCachePool = { "Spray Can" };
        // `static readonly` y no `const` a propósito: con un `const bool` el compilador pliega el
        // `if` de RollItemName y marca el resto del método como código muerto (CS0162), que en un
        // build con warnings-as-errors sería un error por una decisión de balance.
        private static readonly bool RestrictCacheCatalog = true;

        // ── Density (TODO(balance): first-playtest placeholders, mirror the old per-cache/zone
        // counts but now rolled PER CHUNK COLUMN so the live total is bounded by the ~3×3 ring
        // instead of a one-shot world-wide scatter). ──────────────────────────────────────────
        // Cache/zone existence chance and pool/material weights are now PER-ZONE (see
        // ZoneLootProfile below, resolved by ChunkLootManager via ZoneRegistry). These two counts
        // stay FIXED consts on purpose — Pieza 3 hard constraint: a zone's profile may vary
        // chance/pools/rarity but never the slot COUNT (see ZoneLootProfile's doc-comment).
        // RECORTE 2026-08-17: 6 → 2. Con el catálogo reducido a un solo objeto, una caché de 6
        // slots era una pila de 6 botes idénticos. Mover este const es seguro porque la memoria
        // de recogida (`ChunkLootManager._collectedItems`) es un HashSet EN MEMORIA que no
        // sobrevive a la sesión: ninguna clave (cx,cz,slot) vieja puede quedar fuera de rango
        // tras el cambio, porque cambiar este número exige recompilar y por tanto reiniciar.
        private const int ItemsPerCache = 2;
        // TODO(balance): construction-material abundance test (2026-07-07) — carryable zones are
        // 100% construction materials (Log/Stone/Metal). Chance 0.30→0.60 and per-zone 8→16
        // (~x4 more materials). Not tuned final values.
        private const int CarryablesPerZone = 16;
        // Cluster tightness in chunk-normalized units (chunk side = 1.0). Item caches stay a tight
        // 1.5 m pile. Carryable zones: SPREAD widened 6 m → 12 m (2026-07-07) so the same 16
        // materials cover ~4× the area (≈1/4 the per-m² density) instead of a heavy concentrated
        // pile. 12 m / 50 m = 0.24 normalized; points past the chunk edge are clamped (mild).
        private const float CacheClusterRadius = 1.5f / 50f;
        // internal, not private: ChunkLootManager's walkability-retry jitter (Fix priorizado
        // worldgen Alpha 1) reuses this instead of a duplicated magic number — a retry should
        // never wander further than the zone's own spread.
        internal const float ZoneSpreadRadius = 12.0f / 50f;
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

        /// <summary>
        /// 13 first-pass profiles, one per ZONE_* (0=Normal..12=Office, mirrors
        /// backend/src/world/chunk/surface_profiles.rs) — TODO(balance): distinguishable so the
        /// wiring is OBSERVABLE in playtest, deliberately NOT a tuned final design (approved plan,
        /// see docs/STATE.md Pieza 3). Per the zone_kind distribution audited for that plan, the
        /// expansion generator (the vast majority of the explorable world) only reaches NORMAL/
        /// OPEN_HALL/PILLAR_HALL/STORAGE/HUMID/OFFICE in real volume; SAFE/DANGER/CLEANING only
        /// occur in the starter structures near spawn, and RED/PIT need depth >= 12 chunks at ~1%
        /// each. All 13 are still wired for completeness. Entry 0 (NORMAL) is byte-for-value
        /// identical to <see cref="ZoneLootProfile.Default"/> — the largest case keeps today's feel.
        ///
        /// THE ARRAY LENGTH IS LOAD-BEARING, not documentation: <see cref="ZoneLootTable.Profile"/>
        /// resolves out-of-range with <c>Mathf.Clamp</c>, so a short array does not throw — it
        /// silently serves the LAST entry's profile. Before ZONE_OFFICE existed, an OFFICE chunk
        /// would have been looted as ZONE_PIT (richest caches, weapon-heavy) with nothing to
        /// notice it by.
        /// </summary>
        ///
        /// RECORTE 2026-08-17 (prueba de escasez): `itemCacheChance` de cada zona bajó ×10 y
        /// `carryableZoneChance` está a CERO en las 13. El reparto relativo entre zonas se
        /// conserva (PIT/RED siguen siendo las más ricas, PILLAR/HUMID las más pobres) para que
        /// la prueba mida la RAREZA y no un rediseño de zonas encima. Los pesos de pool y
        /// `weaponRollChance` NO se tocaron: quedan sin leer mientras `RestrictCacheCatalog` esté
        /// activo, y así el día que baje no hay que reconstruirlos de memoria.
        public static ZoneLootProfile[] DefaultZoneLootProfiles() => new[]
        {
            ZoneLootProfile.Default, // 0  ZONE_NORMAL      — baseline, unchanged from pre-Pieza-3
            new ZoneLootProfile // 1  ZONE_STORAGE — abundant, material/metal-heavy
            {
                itemCacheChance = 0.05f, carryableZoneChance = 0f, weaponRollChance = 0.10f,
                consumableWeight = 1f, medicalWeight = 1f, ammoWeight = 1f, materialWeight = 3f,
                logWeight = 25f, stoneWeight = 25f, metalWeight = 50f,
            },
            new ZoneLootProfile // 2  ZONE_SAFE — utility-heavy, low weapon rarity
            {
                itemCacheChance = 0.06f, carryableZoneChance = 0f, weaponRollChance = 0.05f,
                consumableWeight = 3f, medicalWeight = 3f, ammoWeight = 1f, materialWeight = 1f,
                logWeight = 40f, stoneWeight = 35f, metalWeight = 25f,
            },
            new ZoneLootProfile // 3  ZONE_DANGER — weapon/ammo/medical-heavy
            {
                itemCacheChance = 0.04f, carryableZoneChance = 0f, weaponRollChance = 0.35f,
                consumableWeight = 1f, medicalWeight = 2f, ammoWeight = 3f, materialWeight = 1f,
                logWeight = 30f, stoneWeight = 30f, metalWeight = 40f,
            },
            new ZoneLootProfile // 4  ZONE_OPEN_HALL — near-baseline, slightly sparser
            {
                itemCacheChance = 0.03f, carryableZoneChance = 0f, weaponRollChance = 0.15f,
                consumableWeight = 2f, medicalWeight = 2f, ammoWeight = 1f, materialWeight = 1f,
                logWeight = 45f, stoneWeight = 35f, metalWeight = 20f,
            },
            new ZoneLootProfile // 5  ZONE_PILLAR_HALL — stone-leaning, cache-sparse
            {
                itemCacheChance = 0.03f, carryableZoneChance = 0f, weaponRollChance = 0.12f,
                consumableWeight = 2f, medicalWeight = 1f, ammoWeight = 1f, materialWeight = 2f,
                logWeight = 30f, stoneWeight = 45f, metalWeight = 25f,
            },
            new ZoneLootProfile // 6  ZONE_HUMID — medical-leaning (mould/rot), stone-heavy
            {
                itemCacheChance = 0.03f, carryableZoneChance = 0f, weaponRollChance = 0.10f,
                consumableWeight = 1f, medicalWeight = 3f, ammoWeight = 1f, materialWeight = 2f,
                logWeight = 20f, stoneWeight = 50f, metalWeight = 30f,
            },
            new ZoneLootProfile // 7  ZONE_BLACKOUT — TODO(balance): no battery/flashlight item yet; material-heavy stand-in
            {
                itemCacheChance = 0.05f, carryableZoneChance = 0f, weaponRollChance = 0.10f,
                consumableWeight = 1f, medicalWeight = 2f, ammoWeight = 1f, materialWeight = 3f,
                logWeight = 20f, stoneWeight = 20f, metalWeight = 60f,
            },
            new ZoneLootProfile // 8  ZONE_MANILA — office/safe-pocket, consumable-leaning
            {
                itemCacheChance = 0.04f, carryableZoneChance = 0f, weaponRollChance = 0.08f,
                consumableWeight = 3f, medicalWeight = 2f, ammoWeight = 1f, materialWeight = 1f,
                logWeight = 35f, stoneWeight = 35f, metalWeight = 30f,
            },
            new ZoneLootProfile // 9  ZONE_CLEANING — chemicals/tools stand-in, medical+metal-heavy
            {
                itemCacheChance = 0.04f, carryableZoneChance = 0f, weaponRollChance = 0.05f,
                consumableWeight = 1f, medicalWeight = 3f, ammoWeight = 1f, materialWeight = 2f,
                logWeight = 15f, stoneWeight = 25f, metalWeight = 60f,
            },
            new ZoneLootProfile // 10 ZONE_RED — signalled danger, high weapon/ammo rarity
            {
                itemCacheChance = 0.06f, carryableZoneChance = 0f, weaponRollChance = 0.45f,
                consumableWeight = 1f, medicalWeight = 2f, ammoWeight = 3f, materialWeight = 1f,
                logWeight = 25f, stoneWeight = 25f, metalWeight = 50f,
            },
            new ZoneLootProfile // 11 ZONE_PIT — rare/deep, richest cache odds, medical-heavy (fall risk)
            {
                itemCacheChance = 0.07f, carryableZoneChance = 0f, weaponRollChance = 0.30f,
                consumableWeight = 1f, medicalWeight = 3f, ammoWeight = 2f, materialWeight = 1f,
                logWeight = 20f, stoneWeight = 40f, metalWeight = 40f,
            },
            new ZoneLootProfile // 12 ZONE_OFFICE — compartmented floor: many small caches, few materials
            {
                itemCacheChance = 0.06f, carryableZoneChance = 0f, weaponRollChance = 0.08f,
                consumableWeight = 3f, medicalWeight = 2f, ammoWeight = 1f, materialWeight = 1f,
                logWeight = 15f, stoneWeight = 20f, metalWeight = 65f,
            },
        };

        /// <summary>Deterministic per-chunk item cache under a zone's loot profile. Empty list =
        /// this chunk has no cache.</summary>
        public static List<Entry> RollItems(long worldSeed, int cx, int cz, ZoneLootProfile profile)
        {
            var rng = new DeterministicRng(Hash(worldSeed, cx, cz, ItemSalt));
            var result = new List<Entry>();
            if (rng.NextFloat() >= profile.itemCacheChance)
                return result; // no cache in this chunk

            RollCentre(ref rng, out float cu, out float cv);
            for (int slot = 0; slot < ItemsPerCache; slot++)
            {
                string name = RollItemName(ref rng, profile);
                ClusterAround(ref rng, cu, cv, CacheClusterRadius, out float u, out float v);
                result.Add(new Entry(slot, name, 1, u, v, rng.NextFloat() * 360f));
            }
            return result;
        }

        /// <summary>
        /// Deterministic per-chunk carryable resource zone under a zone's loot profile. Empty = no
        /// zone. Each SLOT rolls its own material (2026-07-07: was one type per whole zone — felt
        /// too concentrated), so a zone mixes Log/Stone/Metal, spread over ZoneSpreadRadius.
        /// </summary>
        public static List<Entry> RollCarryables(long worldSeed, int cx, int cz, ZoneLootProfile profile)
        {
            var rng = new DeterministicRng(Hash(worldSeed, cx, cz, CarrySalt));
            var result = new List<Entry>();
            if (rng.NextFloat() >= profile.carryableZoneChance)
                return result; // no zone in this chunk

            RollCentre(ref rng, out float cu, out float cv);
            for (int slot = 0; slot < CarryablesPerZone; slot++)
            {
                string name = RollMaterialName(ref rng, profile); // per-slot MIX (was a single type per zone)
                ClusterAround(ref rng, cu, cv, ZoneSpreadRadius, out float u, out float v);
                result.Add(new Entry(slot, name, 1, u, v, rng.NextFloat() * 360f));
            }
            return result;
        }

        // Per-slot construction-material pick, weighted by the zone's profile (relative weights,
        // normalized here). Same deterministic RNG stream, so same seed+chunk+profile → same mix.
        private static string RollMaterialName(ref DeterministicRng rng, ZoneLootProfile profile)
        {
            float total = profile.logWeight + profile.stoneWeight + profile.metalWeight;
            if (total <= 0f)
                return CarryableTypes[0]; // degenerate profile guard — never leave a slot unnamed

            float r = rng.NextFloat() * total;
            if ((r -= profile.logWeight) < 0f) return CarryableTypes[0];
            if ((r -= profile.stoneWeight) < 0f) return CarryableTypes[1];
            return CarryableTypes[2]; // { "Log", "Stone", "Metal" }
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

        // Weighted pick across the 4 common pools (mirror of StpItemSpawner.RollItemName's shape —
        // weapon roll first, then a pool pick — but the pool pick is now weighted by the zone's
        // profile instead of a flat 1/3-1/3-1/6-1/6 split).
        private static string RollItemName(ref DeterministicRng rng, ZoneLootProfile profile)
        {
            // RECORTE TOTAL DE CATÁLOGO (2026-08-17) — ver RestrictedCachePool. Consume UN draw,
            // igual que el que gasta la tirada de arma en la ruta completa, para que levantar el
            // recorte no reordene el stream de posiciones de un chunk ya explorado. Mientras esté
            // activo, `weaponRollChance` y los cuatro pesos de pool del perfil quedan SIN LEER:
            // siguen vivos en el asset y vuelven a mandar en cuanto el bool baje a false.
            if (RestrictCacheCatalog)
                return RestrictedCachePool[rng.NextInt(RestrictedCachePool.Length)];

            if (rng.NextFloat() < profile.weaponRollChance)
                return WeaponPool[rng.NextInt(WeaponPool.Length)];

            float total = profile.consumableWeight + profile.medicalWeight + profile.ammoWeight + profile.materialWeight;
            string[] pool;
            if (total <= 0f)
            {
                pool = ConsumablePool; // degenerate profile guard — never leave a slot unnamed
            }
            else
            {
                float r = rng.NextFloat() * total;
                if ((r -= profile.consumableWeight) < 0f) pool = ConsumablePool;
                else if ((r -= profile.medicalWeight) < 0f) pool = MedicalPool;
                // La rama de ammo se plegó en material (recorte de catálogo vendor, 2026-08-10):
                // `total` SIGUE sumando ammoWeight a propósito, así que ni la escala de `r` ni el
                // número de draws cambian — la masa que antes era munición ahora es material. Eso
                // preserva la restricción dura de la clase (nunca cambiar el COUNT de entradas) y
                // deja el recorte reversible restaurando esta sola línea.
                else pool = MaterialPool;
            }
            return pool[rng.NextInt(pool.Length)];
        }

        private static float Clamp01(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);
        // Tiny local trig (keeps this file free of a UnityEngine dependency for pure headless use).
        private static float Cos(float a) => (float)System.Math.Cos(a);
        private static float Sin(float a) => (float)System.Math.Sin(a);

        // ── Fix priorizado worldgen (Alpha 1): loot dentro de muros ───────────────────────────
        // ChunkLootManager.TryPlace solo hacia raycast al suelo; toda tile tiene losa de suelo
        // (incluidas las macizas), asi que el raycast nunca podia fallar y el loot terminaba
        // dentro de paneles de pared/pilares. Este es el chequeo que faltaba, sobre el MISMO
        // bitmask que GridChunkBuilder.BuildFromWalls ya usa para renderizar (sin segunda fuente
        // de verdad, sin riesgo de divergir de is_walkable_grid_gen — que es host-only en Rust y
        // nunca llega a Unity). Pura, sin UnityEngine, misma razon que el resto del fichero.
        private const byte WallN = 1, WallS = 2, WallE = 4, WallW = 8; // mirror GridChunkDataMsg
        private const byte PillarNW = 0x10, PillarNE = 0x20, PillarSW = 0x40, PillarSE = 0x80; // mirror GridChunkBuilder
        private const float TileSize = 5.0f;      // mirror GridVisualConstants.TileSize
        private const float WallThickness = 0.2f; // mirror GridVisualConstants.WallThickness

        /// <summary>
        /// Given the tile-wall bitmask of a chunk column (10x10, backend/GridChunkDataMsg
        /// convention) and a chunk-local normalized (u,v), is that point walkable? Rejects a
        /// point that falls in a pillar sub-cell (high nibble, ADR-033/Pillar) or within
        /// WallThickness of a tile edge the low nibble marks as walled. Deliberately coarser
        /// than the backend's raw 2.5 m collision (grid_gen/collision.rs, host-only, never
        /// serialized to Unity) — this matches what the PLAYER sees and collides with
        /// client-side, which is the correct target for where loot renders, not the backend's
        /// finer-grained authoritative maze.
        /// </summary>
        public static bool IsWalkable(byte[,] walls, float u, float v)
        {
            if (walls == null) return false;
            int gridX = walls.GetLength(0);
            int gridZ = walls.GetLength(1);
            if (gridX <= 0 || gridZ <= 0) return false;

            // Clamp01 alone lets u/v == 1.0 map to tile index == gridX/gridZ (out of range) —
            // clamp the derived tile index too, not just the normalized input.
            float lx = Clamp01(u) * (gridX * TileSize);
            float lz = Clamp01(v) * (gridZ * TileSize);
            int tx = ClampInt((int)(lx / TileSize), 0, gridX - 1);
            int tz = ClampInt((int)(lz / TileSize), 0, gridZ - 1);
            byte b = walls[tx, tz];

            float localX = lx - tx * TileSize; // [0, TileSize)
            float localZ = lz - tz * TileSize;

            // Which of the tile's 4 sub-cells (2.5 m halves) does the point fall in?
            bool west = localX < TileSize * 0.5f;
            bool north = localZ < TileSize * 0.5f; // low-Z half — backend's "north (−Z) row"
            byte pillarBit = north ? (west ? PillarNW : PillarNE) : (west ? PillarSW : PillarSE);
            if ((b & pillarBit) != 0) return false;

            float half = WallThickness * 0.5f;
            if ((b & WallN) != 0 && localZ < half) return false;
            if ((b & WallS) != 0 && localZ > TileSize - half) return false;
            if ((b & WallW) != 0 && localX < half) return false;
            if ((b & WallE) != 0 && localX > TileSize - half) return false;
            return true;
        }

        private static int ClampInt(int x, int lo, int hi) => x < lo ? lo : (x > hi ? hi : x);
    }

    /// <summary>
    /// Per-zone loot tuning (Pieza 3, zone_kind → loot). Plain data, no UnityEngine dependency
    /// (keeps this file pure/headless-testable, same reason <see cref="DeterministicRng"/> below
    /// avoids it) — <c>[System.Serializable]</c> alone (mscorlib, not a UnityEngine attribute) is
    /// enough for the Unity-side <c>BackroomsSurvival.Gameplay.GridWorld.ZoneLootTable</c> to
    /// serialize an array of these in the Inspector.
    ///
    /// HARD CONSTRAINT — never add a field that changes entry COUNT (e.g. a per-zone item/carryable
    /// count override). <see cref="ChunkLootManager"/> keys its pickup memory on (cx,cz,slot),
    /// where slot is a bare 0..N-1 ordinal from THIS roll (ItemsPerCache / CarryablesPerZone stay
    /// fixed consts above, deliberately NOT profile fields). A chunk re-rolled under a different
    /// profile — e.g. zone_kind resolved after this column's zone was unknown at an earlier roll —
    /// must always yield the same slot COUNT as before, or old collected-slot keys go out of range
    /// or orphan still-live loot. See docs/STATE.md Pieza 3 for the full reasoning.
    /// </summary>
    [System.Serializable]
    public struct ZoneLootProfile
    {
        /// <summary>Chance [0,1) a chunk in this zone rolls an item cache (ItemsPerCache slots) at all.</summary>
        public float itemCacheChance;
        /// <summary>Chance [0,1) a chunk in this zone rolls a carryable resource zone (CarryablesPerZone slots).</summary>
        public float carryableZoneChance;
        /// <summary>Within a rolled item slot, chance it comes from WeaponPool instead of the 4 pools below.</summary>
        public float weaponRollChance;

        // Item pool weights (relative; normalized at roll time — need not sum to any particular total).
        public float consumableWeight;
        public float medicalWeight;
        public float ammoWeight;
        public float materialWeight;

        // Carryable material weights (relative; normalized at roll time).
        public float logWeight;
        public float stoneWeight;
        public float metalWeight;

        /// <summary>ZONE_NORMAL's profile, y también el fallback de una tabla nula/vacía.
        /// RECORTE 2026-08-17: la caché baja 0.40 → 0.04 y las zonas de carryable se APAGAN
        /// (0.60 → 0). Ya no son "los números pre-Pieza-3": la zona que cubre ~83% del mundo es
        /// justo donde más se nota la prueba de escasez, así que dejarla intacta la habría
        /// anulado. Los pesos de pool y `weaponRollChance` sí siguen en su valor playtesteado —
        /// están sin leer bajo `RestrictCacheCatalog`, no borrados.</summary>
        public static ZoneLootProfile Default => new ZoneLootProfile
        {
            itemCacheChance = 0.04f,
            carryableZoneChance = 0f,
            weaponRollChance = 0.15f,
            consumableWeight = 2f,
            medicalWeight = 2f,
            ammoWeight = 1f,
            materialWeight = 1f,
            logWeight = 40f,
            stoneWeight = 35f,
            metalWeight = 25f,
        };
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
