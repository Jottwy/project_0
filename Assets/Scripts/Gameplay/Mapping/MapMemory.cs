using System;
using System.Collections.Generic;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>Qué era una celda cuando el jugador la vio.</summary>
    public enum MapCellKind : byte
    {
        Floor = 0,
        Wall = 1,
    }

    /// <summary>
    /// Un chunk en una planta. Es la unidad de una hoja de papel (MAPPING-ROADMAP §4): lo que se
    /// recuerda de la zona es lo único que se puede pasar a su hoja.
    /// </summary>
    public readonly struct MapZone : IEquatable<MapZone>
    {
        public readonly int ChunkX;
        public readonly int ChunkZ;
        public readonly int Storey;

        public MapZone(int chunkX, int chunkZ, int storey)
        {
            ChunkX = chunkX;
            ChunkZ = chunkZ;
            Storey = storey;
        }

        public bool Equals(MapZone other) =>
            ChunkX == other.ChunkX && ChunkZ == other.ChunkZ && Storey == other.Storey;

        public override bool Equals(object obj) => obj is MapZone other && Equals(other);

        public override int GetHashCode() => (ChunkX * 73856093) ^ (ChunkZ * 19349663) ^ (Storey * 83492791);

        public override string ToString() => $"({ChunkX},{ChunkZ}) planta {Storey}";
    }

    /// <summary>Una celda tal como la recuerda el jugador: dónde, qué era y hace cuánto la vio por última vez.</summary>
    public readonly struct RememberedCell
    {
        public readonly int X;
        public readonly int Z;
        public readonly MapCellKind Kind;
        public readonly double Age;

        public RememberedCell(int x, int z, MapCellKind kind, double age)
        {
            X = x;
            Z = z;
            Kind = kind;
            Age = age;
        }
    }

    /// <summary>
    /// P0.1 de MAPPING-PROTOTYPE — el recuerdo del jugador: qué celdas vio en los últimos segundos y
    /// cuáles eran pared, repartidas por <see cref="MapZone"/>.
    /// </summary>
    /// <remarks>
    /// **C# puro, sin UnityEngine, a propósito.** Así corre también en `tools/dev/headless-tests`
    /// cuando el editor está ocupado. Por eso el tamaño de chunk y la altura de planta entran por el
    /// constructor en vez de leer <c>Wg3ChunkStreamer.ChunkSize</c> y <c>Wg3StoreyLayers.StoreyM</c>:
    /// quien lo monta en escena le pasa ESOS valores, nunca números propios.
    ///
    /// Búfer circular con arrays preasignados: en régimen no asigna nada por muestra. Cuando se
    /// llena antes de caducar (tiempo parado), la muestra nueva pisa la más vieja.
    ///
    /// La salida de <see cref="CellsInZone"/> sale ORDENADA (Z, luego X): el conjunto interno de
    /// celdas vistas no se itera para emitir nada (regla dura 13).
    /// </remarks>
    public sealed class MapMemory
    {
        /// <summary>Marca de celda ya pasada al papel (<see cref="Consume"/>).</summary>
        private const byte ConsumedKind = byte.MaxValue;

        public readonly double CellSizeM;
        public readonly double ChunkSizeM;
        public readonly double StoreyM;
        public readonly double MemorySeconds;
        public readonly int Capacity;
        public readonly int MaxCellsPerSample;

        private readonly int _cellsPerChunk;

        // Muestra i: celdas en [i * MaxCellsPerSample, i * MaxCellsPerSample + _count[i]).
        private readonly double[] _time;
        private readonly int[] _storey;
        private readonly int[] _count;
        private readonly int[] _cellX;
        private readonly int[] _cellZ;
        private readonly byte[] _kind;

        private int _head;
        private int _size;

        private readonly HashSet<long> _seen = new HashSet<long>();
        private static readonly Comparison<RememberedCell> ByZThenX = (a, b) =>
            a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X);

        public MapMemory(float cellSizeM, float chunkSizeM, float storeyM, float memorySeconds,
            float sampleSeconds, int maxCellsPerSample)
        {
            if (cellSizeM <= 0f || chunkSizeM <= 0f || storeyM <= 0f || memorySeconds <= 0f || sampleSeconds <= 0f)
                throw new ArgumentOutOfRangeException(nameof(cellSizeM), "todas las medidas tienen que ser positivas");
            if (maxCellsPerSample <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxCellsPerSample));

            CellSizeM = cellSizeM;
            ChunkSizeM = chunkSizeM;
            StoreyM = storeyM;
            MemorySeconds = memorySeconds;
            MaxCellsPerSample = maxCellsPerSample;

            _cellsPerChunk = (int)Math.Round(ChunkSizeM / CellSizeM);
            if (_cellsPerChunk <= 0 || Math.Abs(_cellsPerChunk * CellSizeM - ChunkSizeM) > 1e-4)
                throw new ArgumentException("el chunk tiene que medir un número entero de celdas", nameof(chunkSizeM));

            // +1: con muestras cada 0,5 s y 60 s de memoria, la de t=0 y la de t=60 conviven.
            Capacity = (int)Math.Ceiling(memorySeconds / sampleSeconds) + 1;

            _time = new double[Capacity];
            _storey = new int[Capacity];
            _count = new int[Capacity];
            _cellX = new int[Capacity * maxCellsPerSample];
            _cellZ = new int[Capacity * maxCellsPerSample];
            _kind = new byte[Capacity * maxCellsPerSample];
        }

        /// <summary>Muestras vivas en el búfer.</summary>
        public int SampleCount => _size;

        public int CellOf(double metres) => (int)Math.Floor(metres / CellSizeM);

        public int StoreyOf(double floorY) => (int)Math.Floor(floorY / StoreyM);

        public MapZone ZoneOfCell(int cellX, int cellZ, int storey) =>
            new MapZone(FloorDiv(cellX, _cellsPerChunk), FloorDiv(cellZ, _cellsPerChunk), storey);

        public MapZone ZoneOf(double x, double floorY, double z) =>
            ZoneOfCell(CellOf(x), CellOf(z), StoreyOf(floorY));

        /// <summary>
        /// Guarda lo visto en <paramref name="time"/>. Olvida antes lo que ya pasó de
        /// <see cref="MemorySeconds"/>.
        /// </summary>
        public void AddSample(double time, int storey, int[] cellX, int[] cellZ, MapCellKind[] kinds, int count)
        {
            if (count < 0 || count > MaxCellsPerSample)
                throw new ArgumentOutOfRangeException(nameof(count), $"{count} celdas; el máximo por muestra es {MaxCellsPerSample}");

            Prune(time);

            int slot = _head;
            _time[slot] = time;
            _storey[slot] = storey;
            _count[slot] = count;
            int offset = slot * MaxCellsPerSample;
            for (int i = 0; i < count; i++)
            {
                _cellX[offset + i] = cellX[i];
                _cellZ[offset + i] = cellZ[i];
                _kind[offset + i] = (byte)kinds[i];
            }

            _head = (_head + 1) % Capacity;
            if (_size < Capacity) _size++;
        }

        /// <summary>Olvida las muestras de hace más de <see cref="MemorySeconds"/>.</summary>
        public void Prune(double now)
        {
            while (_size > 0 && now - _time[Tail] > MemorySeconds)
                _size--;
        }

        /// <summary>
        /// Lo que se recuerda de <paramref name="zone"/> en <paramref name="now"/>: una entrada por
        /// celda, con la edad y el tipo de la vez MÁS RECIENTE que se vio. Rellena
        /// <paramref name="results"/> (lo vacía antes), ordenado por Z y luego X.
        /// </summary>
        public void CellsInZone(MapZone zone, double now, List<RememberedCell> results)
        {
            results.Clear();
            _seen.Clear();

            // De la más nueva a la más vieja: la primera vez que aparece una celda es la más fresca.
            for (int n = 0; n < _size; n++)
            {
                int slot = (_head - 1 - n + Capacity) % Capacity;
                double age = now - _time[slot];
                if (age > MemorySeconds) break;
                if (_storey[slot] != zone.Storey) continue;

                int offset = slot * MaxCellsPerSample;
                for (int i = 0; i < _count[slot]; i++)
                {
                    byte kind = _kind[offset + i];
                    if (kind == ConsumedKind) continue;
                    int x = _cellX[offset + i];
                    int z = _cellZ[offset + i];
                    if (!InZone(x, z, zone)) continue;
                    if (!_seen.Add(Key(x, z))) continue;
                    results.Add(new RememberedCell(x, z, (MapCellKind)kind, age));
                }
            }

            results.Sort(ByZThenX);
        }

        /// <summary>Saca del recuerdo todo lo de <paramref name="zone"/>: lo que ya se pasó al papel no se cobra dos veces.</summary>
        public void Consume(MapZone zone)
        {
            for (int n = 0; n < _size; n++)
            {
                int slot = (_head - 1 - n + Capacity) % Capacity;
                if (_storey[slot] != zone.Storey) continue;

                int offset = slot * MaxCellsPerSample;
                for (int i = 0; i < _count[slot]; i++)
                {
                    if (InZone(_cellX[offset + i], _cellZ[offset + i], zone))
                        _kind[offset + i] = ConsumedKind;
                }
            }
        }

        /// <summary>
        /// Qué celdas ve el jugador desde el centro de una rejilla ya sondeada.
        /// <paramref name="occupied"/> es cuadrada, de lado <c>2·radiusCells+1</c>, centrada en el
        /// jugador; <c>true</c> = pared. Una pared se ve; lo que tiene detrás, no. Escribe en los
        /// arrays de salida (celdas de mundo) y devuelve cuántas.
        /// </summary>
        public static int CollectVisible(bool[] occupied, int radiusCells, int originCellX, int originCellZ,
            int[] outX, int[] outZ, MapCellKind[] outKind)
        {
            int side = 2 * radiusCells + 1;
            if (occupied.Length < side * side)
                throw new ArgumentException($"la rejilla necesita {side * side} celdas", nameof(occupied));

            int written = 0;
            int radiusSq = radiusCells * radiusCells;
            for (int dz = -radiusCells; dz <= radiusCells; dz++)
            {
                for (int dx = -radiusCells; dx <= radiusCells; dx++)
                {
                    if (dx * dx + dz * dz > radiusSq) continue;
                    if (!HasLineOfSight(occupied, side, radiusCells, dx, dz)) continue;

                    outX[written] = originCellX + dx;
                    outZ[written] = originCellZ + dz;
                    outKind[written] = occupied[(dz + radiusCells) * side + dx + radiusCells]
                        ? MapCellKind.Wall
                        : MapCellKind.Floor;
                    written++;
                }
            }

            return written;
        }

        private int Tail => (_head - _size + Capacity) % Capacity;

        private bool InZone(int x, int z, MapZone zone) =>
            FloorDiv(x, _cellsPerChunk) == zone.ChunkX && FloorDiv(z, _cellsPerChunk) == zone.ChunkZ;

        /// <summary>Recorre las celdas intermedias del segmento; la de destino no cuenta, así una pared es visible.</summary>
        private static bool HasLineOfSight(bool[] occupied, int side, int radiusCells, int dx, int dz)
        {
            int steps = Math.Max(Math.Abs(dx), Math.Abs(dz));
            for (int i = 1; i < steps; i++)
            {
                int x = (int)Math.Round((double)dx * i / steps, MidpointRounding.AwayFromZero);
                int z = (int)Math.Round((double)dz * i / steps, MidpointRounding.AwayFromZero);
                if (occupied[(z + radiusCells) * side + x + radiusCells]) return false;
            }

            return true;
        }

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        /// <summary>División entera hacia −∞: la celda −1 cae en el chunk −1, no en el 0.</summary>
        private static int FloorDiv(int a, int b)
        {
            int q = a / b;
            if (a % b != 0 && (a < 0) != (b < 0)) q--;
            return q;
        }
    }
}
