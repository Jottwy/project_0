using System;
using System.Collections.Generic;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// SÓLO PLAYTEST (2026-09-02, Joel): el «almacén de muebles» — una sala existente de WG3 llena
    /// de desmontables, garantizada una por celda de <see cref="CellMeters"/>, para poder probar
    /// el desmontaje sin cruzar medio mundo buscando el 10 % de chunks que llevan un mueble.
    ///
    /// PURO y determinista, mismo contrato que <see cref="ChunkDismantleRoll"/>: sin UnityEngine,
    /// así que se prueba headless. La sala NO se inventa: se elige entre las que el plan ya
    /// produce. Dado (worldSeed, celda) se recorre una lista FIJA de puntos candidatos —uno por
    /// chunk de la celda, en orden barajado— y se acepta el PRIMERO cuyo espacio sea de un papel
    /// elegible y lo bastante grande. «Primero en la lista», y no «el más grande de la celda», es
    /// lo que hace que dos clientes decidan lo mismo sin haber montado los 16 chunks: cada
    /// candidato es una función pura de la geometría, y el orden es el mismo para todos.
    ///
    /// Se apaga con <see cref="DepotEnabled"/>. No cambia ADR-114 D9 («un prop por chunk»): es
    /// una capa aparte, con sal e ids propios, que convive con el sorteo normal.
    /// </summary>
    public static class ChunkDepotRoll
    {
        /// <summary>El interruptor del playtest. En `false` no se siembra ningún almacén.</summary>
        public const bool DepotEnabled = true;

        /// <summary>Lado de la celda: un almacén por celda, o sea uno cada 200 m.</summary>
        public const float CellMeters = 200f;

        /// <summary>Chunks de 50 m por lado de celda: 16 candidatos por celda.</summary>
        public const int ChunksPerCellSide = 4;
        public const int CandidateCount = ChunksPerCellSide * ChunksPerCellSide;

        /// <summary>Lo pedido: 12 muebles por almacén, a partes iguales entre los tres.</summary>
        public const int PropsPerDepot = 12;

        /// <summary>Por debajo de esto el espacio no vale como almacén y se pasa al candidato
        /// siguiente: un almacén de tres sillas no es un almacén.</summary>
        public const int MinPropsToPlace = 6;

        /// <summary>Distancia a las paredes y entre muebles, en metros. 2,5 m deja pasar entre
        /// escritorios de 2,3 m de largo girados en cualquiera de las cuatro direcciones.</summary>
        public const float WallMargin = 1.25f;
        public const float Spacing = 2.5f;

        /// <summary>Sal propia: ni la del contenedor ni la del desmontable suelto.</summary>
        private const ulong DepotSalt = 0xDE_90_7C_E1_1A_00_00UL;

        private const float ChunkMeters = 50f;

        public static int CellOf(float worldCoord) => (int)Math.Floor(worldCoord / CellMeters);

        /// <summary>Los papeles que admiten almacén: nave (3), servicio/almacén (4) y oficina (0).
        /// Los mismos que admiten mueble suelto; espina, pasillo y escalera son sitio de paso.</summary>
        public static bool StyleIsEligible(byte style) => style == 0 || style == 3 || style == 4;

        public readonly struct Candidate
        {
            /// <summary>Punto del mundo (XZ) donde se pregunta por el espacio.</summary>
            public readonly float X;
            public readonly float Z;

            public Candidate(float x, float z)
            {
                X = x;
                Z = z;
            }
        }

        /// <summary>
        /// El candidato <paramref name="k"/> (0..15) de la celda: un punto dentro de uno de sus 16
        /// chunks, con los chunks barajados por la semilla y el punto sorteado dentro del chunk con
        /// el mismo margen que el loot suelto. El orden es fijo por celda.
        /// </summary>
        public static Candidate CandidateAt(long worldSeed, int cellX, int cellZ, int k)
        {
            if (k < 0 || k >= CandidateCount)
                throw new ArgumentOutOfRangeException(nameof(k));

            var rng = new DeterministicRng(ChunkLootRoll.Hash(worldSeed, cellX, cellZ, DepotSalt));
            var order = new int[CandidateCount];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            // Fisher–Yates con el rng de la celda: baraja igual en todos los clientes.
            for (int i = order.Length - 1; i > 0; i--)
            {
                int j = rng.NextInt(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }

            // El punto dentro de cada chunk se sortea DESPUÉS de barajar y en orden, así que el
            // candidato k consume siempre los mismos números.
            float u = 0.5f, v = 0.5f;
            for (int i = 0; i <= k; i++)
                ChunkLootRoll.RollCentre(ref rng, out u, out v);

            int chunk = order[k];
            int cx = cellX * ChunksPerCellSide + chunk % ChunksPerCellSide;
            int cz = cellZ * ChunksPerCellSide + chunk / ChunksPerCellSide;
            return new Candidate((cx + u) * ChunkMeters, (cz + v) * ChunkMeters);
        }

        public readonly struct Slot
        {
            public readonly DismantleProp Prop;
            public readonly float X;
            public readonly float Z;
            /// <summary>Grados sobre Y, múltiplos de 90: los muebles se alinean a la sala.</summary>
            public readonly float Rotation;

            public Slot(DismantleProp prop, float x, float z, float rotation)
            {
                Prop = prop;
                X = x;
                Z = z;
                Rotation = rotation;
            }
        }

        /// <summary>
        /// Reparte hasta <see cref="PropsPerDepot"/> muebles en cuadrícula dentro de la caja del
        /// espacio (XZ del mundo), con <see cref="WallMargin"/> a las paredes y <see cref="Spacing"/>
        /// entre ellos. Devuelve MENOS si no caben, y una lista vacía si caben menos de
        /// <see cref="MinPropsToPlace"/>: eso es «este espacio no vale», no «pon los que quepan».
        ///
        /// Los tres muebles van a partes iguales (4/4/4) y barajados por la semilla, para que un
        /// almacén no sea doce sillas.
        /// </summary>
        public static List<Slot> Layout(long worldSeed, int cellX, int cellZ,
            float minX, float minZ, float maxX, float maxZ)
        {
            var slots = new List<Slot>();
            float w = maxX - minX - 2f * WallMargin;
            float d = maxZ - minZ - 2f * WallMargin;
            if (w <= 0f || d <= 0f)
                return slots;

            int cols = (int)Math.Floor(w / Spacing) + 1;
            int rows = (int)Math.Floor(d / Spacing) + 1;
            int capacity = cols * rows;
            int n = Math.Min(PropsPerDepot, capacity);
            if (n < MinPropsToPlace)
                return slots;

            // La cuadrícula se centra en la sala: el sobrante se reparte a los dos lados.
            float x0 = minX + WallMargin + (w - (cols - 1) * Spacing) * 0.5f;
            float z0 = minZ + WallMargin + (d - (rows - 1) * Spacing) * 0.5f;

            // Reparto 4/4/4 barajado; con menos de 12, se recorta la secuencia barajada.
            var kinds = new DismantleProp[PropsPerDepot];
            for (int i = 0; i < PropsPerDepot; i++)
                kinds[i] = (DismantleProp)(1 + i % 3); // Desk, Shelf, Chair
            var rng = new DeterministicRng(ChunkLootRoll.Hash(worldSeed, cellX, cellZ, DepotSalt ^ 0x51))
                ;
            for (int i = kinds.Length - 1; i > 0; i--)
            {
                int j = rng.NextInt(i + 1);
                (kinds[i], kinds[j]) = (kinds[j], kinds[i]);
            }

            // De la cuadrícula se toman n celdas repartidas de forma uniforme, no las n primeras:
            // si no, un espacio grande llenaría una esquina y dejaría el resto vacío.
            for (int i = 0; i < n; i++)
            {
                int cell = (int)((long)i * capacity / n);
                int col = cell % cols;
                int row = cell / cols;
                float rot = 90f * rng.NextInt(4);
                slots.Add(new Slot(kinds[i], x0 + col * Spacing, z0 + row * Spacing, rot));
            }
            return slots;
        }

        /// <summary>
        /// Id de red del mueble <paramref name="slot"/> del almacén de la celda. Determinista y con
        /// sal propia, por lo mismo que <see cref="ChunkDismantleRoll.NetIdFor"/>: el `remaining`
        /// guardado tiene que seguir apuntando al mismo mueble tras recargar. Nunca 0.
        /// </summary>
        public static uint NetIdFor(long worldSeed, int cellX, int cellZ, int slot)
        {
            ulong h = ChunkLootRoll.Hash(worldSeed, cellX, cellZ, DepotSalt);
            h ^= ((ulong)(uint)slot + 1UL) * 0xBF58_476D_1CE4_E5B9UL;
            h ^= h >> 31;
            h *= 0x94D0_49BB_1331_11EBUL;
            h ^= h >> 29;
            uint id = (uint)(h & 0xFFFF_FFFFUL);
            return id == 0 ? 1u : id;
        }
    }
}
