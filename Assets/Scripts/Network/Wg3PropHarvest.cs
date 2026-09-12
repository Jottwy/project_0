using System.Collections.Generic;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-145 D1/D2/D5 — qué atrezo de <see cref="Wg3PropMsg"/> se puede desmontar, a qué clase de
    /// material pertenece, y su id de red determinista.
    ///
    /// PURO, sin <c>UnityEngine</c>, mismo contrato que <see cref="ChunkDismantleRoll"/>: dado
    /// (worldSeed, kind, x_cm, y_cm, z_cm) siempre contesta lo mismo, así que el host y el joiner
    /// —que ven el mismo <see cref="Wg3PropMsg"/> en la misma posición, `fill.rs` es determinista
    /// sobre `world_seed`— calculan el MISMO id sin emparejamiento por proximidad.
    /// </summary>
    public static class Wg3PropHarvest
    {
        /// <summary>Sal PROPIA (ADR-145 D2): ni la de <see cref="ChunkDismantleRoll.NetIdFor"/>
        /// (0xD1_5A_11_7A_B1_E0) ni la de <c>StpWorldContainerSpawner.RequestIdFor</c>
        /// ("CONTAI" &lt;&lt; 8) — compartir sal pondría el atrezo y el mueble suelto siempre en la
        /// misma esquina del espacio de ids.</summary>
        private const ulong PropHarvestSalt = 0x0FF1CE00_C0FFEE45UL;

        /// <summary>Sal PROPIA (ADR-145 D6): otro id, otro mapa (`world.corpses`, no
        /// `net.stp_harvestables`). Nunca la de <see cref="PropHarvestSalt"/> ni las de
        /// `ChunkDismantleRoll`/`StpWorldContainerSpawner` — el mismo criterio del proyecto de
        /// "ni la del contenedor ni la del desmontable suelto".</summary>
        private const ulong PropChestSalt = 0x0FF1CE00_DECAF000UL;

        /// <summary>ADR-145 D6 — bit reservado del id de cofre-atrezo. `World::next_corpse_id`
        /// (backend) es un contador puro que arranca en 1 y sólo sube: jamás emite un valor con
        /// este bit puesto, así que un <see cref="ChestIdFor"/> calculado aquí no puede chocar
        /// con un cadáver de jugador ni con un cofre de `StpChestSpawner`/
        /// `StpWorldContainerSpawner` — los tres viven en el MISMO `HashMap&lt;u32,CorpseData&gt;`
        /// y un choque ahí sobreescribiría en silencio lo que ya hubiera.</summary>
        private const uint ChestIdReservedBit = 0x8000_0000u;

        /// <summary>Las cuatro clases de material de D5. <c>MetalContainer</c> es la única que
        /// dropea por <c>logDefId</c>/<c>logCount</c> (el carryable Metal); las otras tres dropean
        /// por <c>itemDrops</c>.</summary>
        public enum MaterialClass
        {
            WoodSmall,
            MetalSmall,
            BigFurniture,
            MetalContainer,
        }

        /// <summary>D1 — los 18 kinds físicos con prefab real. Ausentes a propósito: Sign(15),
        /// CeilingTileHung(21), LightHung(22) — sin malla ni collider que golpear.</summary>
        public static bool IsPhysical(byte kind) => ClassFor(kind).HasValue;

        /// <summary>D5 — la tabla, literal. <c>null</c> para lo que D1 deja fuera.</summary>
        public static MaterialClass? ClassFor(byte kind)
        {
            switch (kind)
            {
                case Wg3PropMsg.Chair:
                case Wg3PropMsg.ChairFallen:
                case Wg3PropMsg.Trash:
                case Wg3PropMsg.Box:
                case Wg3PropMsg.Whiteboard:
                case Wg3PropMsg.Paper:
                case Wg3PropMsg.Tray:
                    return MaterialClass.WoodSmall;
                case Wg3PropMsg.Monitor:
                case Wg3PropMsg.Clock:
                case Wg3PropMsg.Phone:
                case Wg3PropMsg.Keyboard:
                case Wg3PropMsg.Microwave:
                    return MaterialClass.MetalSmall;
                case Wg3PropMsg.Desk:
                case Wg3PropMsg.TableLong:
                case Wg3PropMsg.Counter:
                    return MaterialClass.BigFurniture;
                case Wg3PropMsg.Cabinet:
                case Wg3PropMsg.Shelf:
                case Wg3PropMsg.Fridge:
                case Wg3PropMsg.Rack:
                    return MaterialClass.MetalContainer;
                default:
                    return null;
            }
        }

        /// <summary>D5 — la tabla de <c>itemDrops</c> por clase, en nombres de `ItemDefinition`
        /// (los mismos dos materiales de ADR-114: `ChunkDismantleRoll.WoodenPlank`/`MetalBeam`,
        /// sin material nuevo). Vacía para <see cref="MaterialClass.MetalContainer"/> a propósito:
        /// esa clase dropea por <c>logDefId</c>/<c>logCount</c> (el carryable Metal), nunca las
        /// dos vías a la vez (`StpHarvestableSyncManager.MaybeSpawnOnDeplete`, sin tocar).</summary>
        public static IReadOnlyList<MaterialYield> ItemDropsFor(MaterialClass cls)
        {
            switch (cls)
            {
                case MaterialClass.WoodSmall:
                    return new[] { new MaterialYield(ChunkDismantleRoll.WoodenPlank, 1) };
                case MaterialClass.MetalSmall:
                    return new[] { new MaterialYield(ChunkDismantleRoll.MetalBeam, 1) };
                case MaterialClass.BigFurniture:
                    return new[]
                    {
                        new MaterialYield(ChunkDismantleRoll.WoodenPlank, 2),
                        new MaterialYield(ChunkDismantleRoll.MetalBeam, 1),
                    };
                default:
                    return System.Array.Empty<MaterialYield>();
            }
        }

        /// <summary>
        /// ADR-145 D2 — el id de red del atrezo, DETERMINISTA a partir de (worldSeed, kind, x_cm,
        /// y_cm, z_cm). Nunca devuelve 0: el backend usa el 0 como "sin objetivo" y descarta el
        /// golpe (mismo contrato que <see cref="ChunkDismantleRoll.NetIdFor"/>).
        /// </summary>
        public static uint NetIdFor(long worldSeed, byte kind, int xCm, int yCm, int zCm)
        {
            ulong h = MixPosition(worldSeed, kind, xCm, yCm, zCm, PropHarvestSalt);
            uint id = (uint)(h & 0xFFFF_FFFFUL);
            return id == 0 ? 1u : id;
        }

        /// <summary>
        /// ADR-145 D6 — el id del cofre que acompaña a Cabinet/Shelf/Fridge/Rack. Misma mezcla que
        /// <see cref="NetIdFor"/> pero con <see cref="PropChestSalt"/> (nunca comparte sal con el
        /// harvestable de la misma pieza) y el bit 31 SIEMPRE puesto — el espacio de ids que
        /// <c>World::next_corpse_id</c> jamás alcanza.
        /// </summary>
        public static uint ChestIdFor(long worldSeed, byte kind, int xCm, int yCm, int zCm)
        {
            ulong h = MixPosition(worldSeed, kind, xCm, yCm, zCm, PropChestSalt);
            uint id = (uint)(h & 0x7FFF_FFFFUL); // deja el bit 31 libre para la reserva
            if (id == 0) id = 1;
            return id | ChestIdReservedBit;
        }

        /// <summary>
        /// ADR-145 D6 — el loot del cofre, sorteado con el MISMO par de primitivas que
        /// <see cref="ChunkContainerRoll"/> usa para su propio contenedor por chunk
        /// (<c>ChunkLootRoll.RollItemCount</c>/<c>RollItemName</c> contra el perfil del papel de
        /// la sala, más <see cref="ChunkContainerRoll.ContainerBonusItems"/>) — "reutiliza el pool
        /// tal cual quedó", no una tabla nueva. Semilla del RNG: la MISMA mezcla que
        /// <see cref="ChestIdFor"/> antes de recortar el bit, así que el contenido de un cofre no
        /// se puede predecir sin conocer también su posición exacta (igual que hoy).
        /// </summary>
        public static List<string> ChestContentsFor(long worldSeed, byte kind, int xCm, int yCm,
            int zCm, ZoneLootProfile profile)
        {
            ulong h = MixPosition(worldSeed, kind, xCm, yCm, zCm, PropChestSalt);
            var rng = new DeterministicRng(h);
            int count = ChunkLootRoll.RollItemCount(ref rng) + ChunkContainerRoll.ContainerBonusItems;
            var contents = new List<string>(count);
            for (int i = 0; i < count; i++)
                contents.Add(ChunkLootRoll.RollItemName(ref rng, profile));
            return contents;
        }

        private static ulong MixPosition(long worldSeed, byte kind, int xCm, int yCm, int zCm, ulong salt)
        {
            ulong h = (ulong)worldSeed * 0x9E37_79B9_7F4A_7C15UL;
            h ^= salt;
            h ^= (ulong)(uint)xCm * 0xFF51_AFD7_ED55_8CCDUL;
            h ^= (ulong)(uint)yCm * 0xC2B2_AE3D_27D4_EB4FUL;
            h ^= ((ulong)(uint)zCm + 0xC4CE_B9FE_1A85_EC53UL);
            h ^= (ulong)kind;
            h ^= h >> 29;
            h *= 0xBF58_476D_1CE4_E5B9UL;
            h ^= h >> 32;
            return h;
        }
    }
}
