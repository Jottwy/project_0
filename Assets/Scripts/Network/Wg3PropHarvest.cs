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
            ulong h = (ulong)worldSeed * 0x9E37_79B9_7F4A_7C15UL;
            h ^= PropHarvestSalt;
            h ^= (ulong)(uint)xCm * 0xFF51_AFD7_ED55_8CCDUL;
            h ^= (ulong)(uint)yCm * 0xC2B2_AE3D_27D4_EB4FUL;
            h ^= ((ulong)(uint)zCm + 0xC4CE_B9FE_1A85_EC53UL);
            h ^= (ulong)kind;
            h ^= h >> 29;
            h *= 0xBF58_476D_1CE4_E5B9UL;
            h ^= h >> 32;
            uint id = (uint)(h & 0xFFFF_FFFFUL);
            return id == 0 ? 1u : id;
        }
    }
}
