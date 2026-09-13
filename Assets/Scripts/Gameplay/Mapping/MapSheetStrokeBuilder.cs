using System;
using System.Collections.Generic;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>Un tramo de pared recordado y aún no dibujado en la hoja.</summary>
    public readonly struct PendingStroke
    {
        /// <summary>true: la pared corre a lo largo de Z en X = <see cref="Line"/>; false: a lo largo de X en Z = <see cref="Line"/>.</summary>
        public readonly bool Vertical;
        /// <summary>Frontera entre celdas, en celdas de MUNDO.</summary>
        public readonly int Line;
        /// <summary>Primera celda que cubre, en celdas de mundo.</summary>
        public readonly int From;
        /// <summary>Celda siguiente a la última (exclusivo).</summary>
        public readonly int To;
        public readonly bool Old;
        internal readonly int KeyStart;
        internal readonly int KeyCount;

        internal PendingStroke(bool vertical, int line, int from, int to, bool old, int keyStart, int keyCount)
        {
            Vertical = vertical;
            Line = line;
            From = from;
            To = to;
            Old = old;
            KeyStart = keyStart;
            KeyCount = keyCount;
        }

        public int LengthCells => To - From;
    }

    /// <summary>
    /// P0.2 de MAPPING-PROTOTYPE — convierte lo que se recuerda de la zona de una hoja en trazos. Portado de la
    /// maqueta «Libreta del cartógrafo» (<c>buildStrokes</c>). C# puro, sin asignar en régimen salvo los puntos
    /// de cada trazo que se queda en el papel.
    /// </summary>
    /// <remarks>
    /// Una ARISTA es una pared recordada junto a un suelo recordado de la zona. Las paredes se piden con una
    /// celda de margen fuera del chunk porque el muro de borde es del vecino. Las aristas contiguas de la misma
    /// fila y la misma frescura se funden en un trazo; la salida sale ordenada por (horizontal antes que
    /// vertical, línea, posición) para que el mismo recuerdo dé siempre el mismo dibujo.
    ///
    /// Dos pasos a propósito: <see cref="Build"/> calcula lo pendiente y <see cref="Commit"/> pasa UN trazo al
    /// papel, para que la vista lo anime y pueda cancelarse a mitad sin perder lo ya trazado.
    /// </remarks>
    public sealed class MapSheetStrokeBuilder
    {
        /// <summary>Probabilidad de que un tramo de recuerdo viejo no llegue al papel.</summary>
        public const float OldGapChance = 0.28f;

        private struct EdgeEntry
        {
            public bool Vertical;
            public int Line;
            public int Pos;
            public double Age;
            public long Key;
        }

        private readonly List<RememberedCell> _cells = new List<RememberedCell>();
        private readonly Dictionary<long, double> _floorAge = new Dictionary<long, double>();
        private readonly Dictionary<long, int> _edgeIndex = new Dictionary<long, int>();
        private readonly List<EdgeEntry> _entries = new List<EdgeEntry>();
        private readonly List<long> _keys = new List<long>();
        private readonly List<PendingStroke> _pending = new List<PendingStroke>();

        private static readonly Comparison<EdgeEntry> ByLine = (a, b) =>
        {
            if (a.Vertical != b.Vertical) return a.Vertical ? 1 : -1;
            if (a.Line != b.Line) return a.Line.CompareTo(b.Line);
            return a.Pos.CompareTo(b.Pos);
        };

        public IReadOnlyList<PendingStroke> Pending => _pending;

        /// <summary>
        /// Calcula lo que falta por dibujar en <paramref name="sheet"/>. Un recuerdo de más de
        /// <paramref name="oldAfterSeconds"/> sale como trazo viejo. Devuelve cuántos trazos quedan pendientes.
        /// </summary>
        public int Build(MapSheet sheet, MapMemory memory, double now, double oldAfterSeconds)
        {
            _pending.Clear();
            _keys.Clear();
            _entries.Clear();
            _floorAge.Clear();
            _edgeIndex.Clear();
            if (!sheet.HasZone) return 0;

            memory.CellsInZone(sheet.Zone, now, _cells, 1);
            foreach (RememberedCell cell in _cells)
                if (cell.Kind == MapCellKind.Floor) _floorAge[CellKey(cell.X, cell.Z)] = cell.Age;

            foreach (RememberedCell cell in _cells)
            {
                if (cell.Kind != MapCellKind.Wall) continue;
                Consider(sheet, cell.X - 1, cell.Z, true, cell.X, cell.Z);
                Consider(sheet, cell.X + 1, cell.Z, true, cell.X + 1, cell.Z);
                Consider(sheet, cell.X, cell.Z - 1, false, cell.Z, cell.X);
                Consider(sheet, cell.X, cell.Z + 1, false, cell.Z + 1, cell.X);
            }

            _entries.Sort(ByLine);
            int start = 0;
            for (int i = 1; i <= _entries.Count; i++)
            {
                if (i < _entries.Count)
                {
                    EdgeEntry previous = _entries[i - 1];
                    EdgeEntry current = _entries[i];
                    bool sameRun = current.Vertical == previous.Vertical && current.Line == previous.Line &&
                                   current.Pos == previous.Pos + 1 &&
                                   (current.Age > oldAfterSeconds) == (_entries[start].Age > oldAfterSeconds);
                    if (sameRun) continue;
                }

                EdgeEntry first = _entries[start];
                int keyStart = _keys.Count;
                for (int k = start; k < i; k++) _keys.Add(_entries[k].Key);
                _pending.Add(new PendingStroke(first.Vertical, first.Line, first.Pos, _entries[i - 1].Pos + 1,
                    first.Age > oldAfterSeconds, keyStart, i - start));
                start = i;
            }

            return _pending.Count;
        }

        /// <summary>
        /// Pasa al papel el trazo pendiente <paramref name="index"/> en <paramref name="layer"/>. Devuelve false si
        /// no llega la tinta: entonces no se pinta nada y la arista sigue pendiente. Un tramo viejo puede no
        /// llegar al papel (<see cref="OldGapChance"/>): devuelve true sin gastar tinta ni marcar la arista.
        /// </summary>
        public bool Commit(MapSheet sheet, MapSheetLayer layer, int index, MapPen pen, MapMemory memory)
        {
            PendingStroke stroke = _pending[index];
            float cost = (float)(stroke.LengthCells * memory.CellSizeM) * pen.CostPerMetre;
            if (pen.Ink < cost) return false;

            uint state = Mix((uint)sheet.Seed, (uint)sheet.Layers.IndexOf(layer), (uint)index);
            if (stroke.Old && Unit(ref state) < OldGapChance) return true;

            pen.Ink -= cost;
            layer.Strokes.Add(new MapStroke(Jitter(stroke, sheet.Zone, memory.CellsPerChunk, ref state), stroke.Old));
            for (int k = 0; k < stroke.KeyCount; k++) sheet.AddEdge(_keys[stroke.KeyStart + k]);
            return true;
        }

        /// <summary>Clave estable de una arista entre celdas.</summary>
        public static long EdgeKey(bool vertical, int line, int pos) =>
            ((vertical ? 1L : 0L) << 62) | (((long)line + (1L << 30)) << 31) | ((long)pos + (1L << 30));

        private void Consider(MapSheet sheet, int floorX, int floorZ, bool vertical, int line, int pos)
        {
            if (!_floorAge.TryGetValue(CellKey(floorX, floorZ), out double age)) return;
            long key = EdgeKey(vertical, line, pos);
            if (sheet.HasEdge(key)) return;

            if (_edgeIndex.TryGetValue(key, out int existing))
            {
                if (age < _entries[existing].Age)
                {
                    EdgeEntry entry = _entries[existing];
                    entry.Age = age;
                    _entries[existing] = entry;
                }

                return;
            }

            _edgeIndex[key] = _entries.Count;
            _entries.Add(new EdgeEntry { Vertical = vertical, Line = line, Pos = pos, Age = age, Key = key });
        }

        private static float[] Jitter(PendingStroke stroke, MapZone zone, int cellsPerChunk, ref uint state)
        {
            float amplitude = stroke.Old ? 0.2f : 0.08f;
            int length = stroke.LengthCells;
            int count = Math.Max(2, (int)Math.Ceiling(length / 1.5f) + 1);
            float overshoot = (Unit(ref state) - 0.3f) * 0.3f;

            float line = stroke.Line - (stroke.Vertical ? zone.ChunkX : zone.ChunkZ) * cellsPerChunk;
            float from = stroke.From - (stroke.Vertical ? zone.ChunkZ : zone.ChunkX) * cellsPerChunk;

            var points = new float[count * 2];
            for (int i = 0; i < count; i++)
            {
                float along = from - overshoot + (length + 2f * overshoot) * i / (count - 1);
                float across = line + (Unit(ref state) - 0.5f) * 2f * amplitude;
                points[2 * i] = stroke.Vertical ? across : along;
                points[2 * i + 1] = stroke.Vertical ? along : across;
            }

            return points;
        }

        private static long CellKey(int x, int z) => ((long)x << 32) ^ (uint)z;

        private static uint Mix(uint a, uint b, uint c)
        {
            uint h = (a * 0x9E3779B1u) ^ ((b + 0x7F4A7C15u) * 0x85EBCA77u) ^ ((c + 0x165667B1u) * 0xC2B2AE3Du);
            return Scramble(h == 0 ? 1u : h);
        }

        private static uint Scramble(uint x)
        {
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }

        private static float Unit(ref uint state)
        {
            state = Scramble(state + 0x9E3779B9u);
            return (state >> 8) / 16777216f;
        }
    }
}
