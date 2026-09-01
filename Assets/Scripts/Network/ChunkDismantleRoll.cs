using System;
using System.Collections.Generic;

namespace BackroomsSurvival.Net
{
    /// <summary>Qué MUEBLE desmontable cae en un chunk. Sale del PAPEL del espacio, así que no
    /// viaja por el cable — el cliente lo deriva de la posición igual que ya deriva el contenedor
    /// (ADR-114 D3). `None` significa que ese papel no admite desmontables.</summary>
    public enum DismantleProp : byte
    {
        None = 0,
        /// <summary>Oficina.</summary>
        Desk = 1,
        /// <summary>Nave y servicio/almacén — estantería.</summary>
        Shelf = 2,
        /// <summary>Callejón sin salida — la silla que alguien dejó ahí.</summary>
        Chair = 3,
    }

    /// <summary>Un material y cuántas unidades da. `Name` es el nombre de `ItemDefinition` SIN
    /// prefijo, igual que las pools del loot suelto ("Cloth", "Leather"): lo resuelve a id quien
    /// siembra, con la misma tolerancia a un catálogo a medio autorar.</summary>
    public readonly struct MaterialYield
    {
        public readonly string Name;
        public readonly int Count;

        public MaterialYield(string name, int count)
        {
            Name = name;
            Count = count;
        }
    }

    /// <summary>
    /// ADR-114 D7/D8/D9 — la tabla objeto → materiales, y el sorteo de dónde cae cada mueble.
    ///
    /// PURO y determinista, mismo contrato que <see cref="ChunkLootRoll"/> y
    /// <see cref="ChunkContainerRoll"/>: sin acceso a la escena de Unity, así que se prueba
    /// headless. Dado (worldSeed, cx, cz) siempre contesta el MISMO mueble.
    ///
    /// **No es un motor data-driven ni un asset nuevo: son tres filas** (ADR-114 D7). La puerta de
    /// herramienta NO está aquí y no puede estarlo — la mira el cliente con el `HarvestPower` del
    /// vendor (D6), y el servidor nunca aprende qué herramienta se usó, que es exactamente lo que
    /// mantiene a ADR-023 intacto.
    /// </summary>
    public static class ChunkDismantleRoll
    {
        /// <summary>Probabilidad de que un chunk lleve un mueble desmontable, ANTES de mirar el
        /// papel. TODO(balance): placeholder, declarado como tal en el riesgo 4 de ADR-114. Arranca
        /// en el mismo valor que el contenedor porque no hay con qué medir todavía: el crafteo que
        /// consumiría estos materiales es ADR-064 y sigue en PROPUESTA.</summary>
        public const float DismantleChance = 0.10f;

        /// <summary>Uno por chunk, misma razón que el contenedor: dos piden decidir si se agrupan o
        /// se reparten, y ésa es una decisión de contenido que la cadena no necesita.</summary>
        public const int MaxPropsPerChunk = 1;

        /// <summary>Sal PROPIA. ADR-114 D9 lo exige por escrito: compartirla con
        /// <see cref="ChunkContainerRoll"/> movería `ContainerChance`, que es un número de balance
        /// ya en juego, y además pondría el mueble y el contenedor siempre en la misma esquina.
        /// </summary>
        private const ulong DismantleSalt = 0xD1_5A_11_7A_B1_E0UL;

        /// <summary>El desmontable que le toca a cada papel. Mismos tres papeles de PASO fuera que
        /// en el contenedor (espina, pasillo, escalera): un mueble ahí estorba la circulación.
        /// </summary>
        public static DismantleProp PropForStyle(byte style)
        {
            switch (style)
            {
                case 0: return DismantleProp.Desk;  // oficina
                case 3: return DismantleProp.Shelf; // nave
                case 4: return DismantleProp.Shelf; // servicio / almacén
                case 5: return DismantleProp.Chair; // callejón sin salida
                default: return DismantleProp.None; // 1 espina, 2 pasillo, 6 escalera
            }
        }

        /// <summary>
        /// ADR-114 D8 — la tabla, literal. Cinco materiales autorizados, cuatro repartidos aquí.
        ///
        /// **La cinta adhesiva NO sale de desmontar**, a propósito: si los cinco materiales salen
        /// del mueble más cercano, el desmontaje deja de rozarse con la exploración. Se queda como
        /// material sólo de loot, vía contenedores.
        ///
        /// Las cantidades cuentan con que ningún prop cae en menos de CUATRO golpes: eso no es una
        /// perilla de este lado, es `MAX_HARVEST_FRACTION_PER_HIT = 0.25` en el backend.
        /// </summary>
        public static IReadOnlyList<MaterialYield> MaterialsFor(DismantleProp prop)
        {
            switch (prop)
            {
                case DismantleProp.Desk:
                    return new[]
                    {
                        new MaterialYield(WoodenPlank, 2),
                        new MaterialYield(MetalBeam, 1),
                    };
                case DismantleProp.Shelf:
                    return new[]
                    {
                        new MaterialYield(MetalBeam, 2),
                        new MaterialYield(WoodenPlank, 1),
                    };
                case DismantleProp.Chair:
                    return new[]
                    {
                        new MaterialYield(WoodenPlank, 1),
                        new MaterialYield(Cloth, 1),
                        new MaterialYield(Leather, 1),
                    };
                default:
                    return Array.Empty<MaterialYield>();
            }
        }

        /// <summary>Nombres de `ItemDefinition` sin prefijo. `Cloth` y `Leather` YA existen en el
        /// catálogo y ya los sirve el loot suelto; los dos primeros hay que autorarlos
        /// (`BR_Wooden Plank`, `BR_Metal Beam`) y hasta entonces quien siembre los salta con aviso,
        /// igual que hace el resto de sembradores con un catálogo a medio autorar.</summary>
        public const string WoodenPlank = "Wooden Plank";
        public const string MetalBeam = "Metal Beam";
        public const string Cloth = "Cloth";
        public const string Leather = "Leather";

        /// <summary>Un mueble desmontable, en coordenadas normalizadas del chunk igual que
        /// <see cref="ChunkContainerRoll.Entry"/>.</summary>
        public readonly struct Entry
        {
            public readonly DismantleProp Prop;
            public readonly float U;
            public readonly float V;
            public readonly float Rotation;

            public Entry(DismantleProp prop, float u, float v, float rotation)
            {
                Prop = prop; U = u; V = v; Rotation = rotation;
            }
        }

        /// <summary>
        /// El mueble desmontable de este chunk, si lo hay.
        ///
        /// El centro se sortea PRIMERO, por el mismo motivo que en el contenedor: hay que saber
        /// dónde cae para poder preguntar qué sitio es antes de decidir si lo hay.
        ///
        /// <paramref name="styleAt"/> devuelve nulo cuando ahí todavía no hay geometría montada, y
        /// eso NO es "aquí no hay mueble": <paramref name="spaceKnown"/> sale en falso y la columna
        /// se queda sin sellar para reintentarla. Distinguir "todavía no" de "aquí no" es lo que
        /// evita que una columna se re-sortee para siempre (ADR-108 enm. 4).
        /// </summary>
        public static bool RollPropByStyle(
            long worldSeed, int cx, int cz,
            Func<float, float, byte?> styleAt,
            out Entry entry, out bool spaceKnown)
        {
            entry = default;
            var rng = new DeterministicRng(ChunkLootRoll.Hash(worldSeed, cx, cz, DismantleSalt));

            ChunkLootRoll.RollCentre(ref rng, out float cu, out float cv);
            byte? found = styleAt(cu, cv);
            spaceKnown = found.HasValue;
            if (!spaceKnown)
                return false;

            DismantleProp prop = PropForStyle(found.Value);
            if (prop == DismantleProp.None)
                return false; // este papel es sitio de paso

            if (rng.NextFloat() >= DismantleChance)
                return false;

            entry = new Entry(prop, cu, cv, rng.NextFloat() * 360f);
            return true;
        }

        /// <summary>
        /// ADR-114 D2 — el id de red del prop, DETERMINISTA a partir de (worldSeed, cx, cz).
        ///
        /// Es el cambio que hace que la persistencia funcione. El registro de hoy reparte ids con
        /// un contador sobre el barrido de escena; con props procedurales eso significa que al
        /// recargar el orden cambia y cada escritorio desmontado vuelve entero con el `remaining`
        /// de otro. Misma mezcla de 64 bits que `StpWorldContainerSpawner.RequestIdFor`, y por el
        /// mismo motivo: recortar sin mezclar tira los bits altos de `cx` y dos chunks lejanos
        /// acuñan el mismo id.
        ///
        /// Nunca devuelve 0: el backend usa el 0 como "sin objetivo" y descarta el golpe.
        /// </summary>
        public static uint NetIdFor(long worldSeed, int cx, int cz)
        {
            ulong h = (ulong)worldSeed * 0x9E37_79B9_7F4A_7C15UL;
            h ^= (ulong)(uint)cx * 0xFF51_AFD7_ED55_8CCDUL;
            h ^= ((ulong)(uint)cz + 0xC4CE_B9FE_1A85_EC53UL);
            h ^= h >> 29;
            h *= 0xBF58_476D_1CE4_E5B9UL;
            h ^= h >> 32;
            uint id = (uint)(h & 0xFFFF_FFFFUL);
            return id == 0 ? 1u : id;
        }
    }
}
