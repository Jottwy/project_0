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

        /// <summary>Pasadas de boli por cada pared reciente: a mano se repasa, no se traza una vez.</summary>
        public const int FreshPasses = 2;

        /// <summary>Probabilidad de una tercera pasada en paredes de 4 celdas o más.</summary>
        public const float ThirdPassChance = 0.35f;

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
        private readonly List<MapCrossing> _crossings = new List<MapCrossing>();
        private readonly List<long> _recentKeys = new List<long>();

        /// <summary>Paredes vistas hace poco por debajo de las cuales «Ubicarme» no da veredicto.</summary>
        public const int MinRecognitionEdges = 10;

        /// <summary>Distancia de la flecha al borde de la hoja, en celdas.</summary>
        private const float ArrowInsetCells = 0.6f;

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

            // ADR-154 D2: la clave del aleatorio es la de la CAPA y la posición en la tanda, las dos guardadas con el tramo.
            uint state = Mix((uint)sheet.Seed, (uint)layer.Key, (uint)index);
            if (stroke.Old && Unit(ref state) < OldGapChance) return true;

            pen.Ink -= cost;
            var run = new MapRun(stroke.Vertical, stroke.Line, stroke.From, stroke.To, stroke.Old, index);
            AddPasses(layer, run, sheet.Zone, memory.CellsPerChunk, ref state);
            layer.Runs.Add(run);
            for (int k = 0; k < stroke.KeyCount; k++) sheet.AddEdge(_keys[stroke.KeyStart + k]);
            return true;
        }

        /// <summary>
        /// ADR-154 D2 — rehace los trazos de todas las capas a partir de sus tramos guardados, con la misma clave con la
        /// que se trazaron, y marca sus aristas: la hoja cargada se pinta idéntica a la dibujada. En una hoja pasada a
        /// limpio los tramos salen rectos.
        /// </summary>
        public static void Redraw(MapSheet sheet, int cellsPerChunk)
        {
            foreach (MapSheetLayer layer in sheet.Layers)
            {
                layer.Strokes.Clear();
                foreach (MapRun run in layer.Runs)
                {
                    if (sheet.Clean)
                    {
                        layer.Strokes.Add(CleanStroke(run, sheet.Zone, cellsPerChunk));
                    }
                    else
                    {
                        uint state = Mix((uint)sheet.Seed, (uint)layer.Key, (uint)run.DrawIndex);
                        // Un tramo viejo guardado pasó el sorteo del hueco: se consume igual para seguir la misma serie.
                        if (run.Old) Unit(ref state);
                        AddPasses(layer, run, sheet.Zone, cellsPerChunk, ref state);
                    }

                    for (int pos = run.From; pos < run.To; pos++) sheet.AddEdge(EdgeKey(run.Vertical, run.Line, pos));
                }
            }
        }

        /// <summary>P0.4 — el tramo pasado a limpio: una recta firme en celdas locales.</summary>
        public static MapStroke CleanStroke(MapRun run, MapZone zone, int cellsPerChunk)
        {
            float across = run.Line - (run.Vertical ? zone.ChunkX : zone.ChunkZ) * cellsPerChunk;
            float a = run.From - (run.Vertical ? zone.ChunkZ : zone.ChunkX) * cellsPerChunk;
            float b = run.To - (run.Vertical ? zone.ChunkZ : zone.ChunkX) * cellsPerChunk;
            return new MapStroke(run.Vertical ? new[] { across, a, across, b } : new[] { a, across, b, across }, false,
                steady: true);
        }

        private static void AddPasses(MapSheetLayer layer, MapRun run, MapZone zone, int cellsPerChunk, ref uint state)
        {
            // La tinta se cobra UNA vez por pared; las pasadas son el gesto, no más pared dibujada.
            int passes = run.Old
                ? 1
                : FreshPasses + (run.LengthCells >= 4 && Unit(ref state) < ThirdPassChance ? 1 : 0);
            for (int pass = 0; pass < passes; pass++)
                layer.Strokes.Add(new MapStroke(Sketch(run, zone, cellsPerChunk, pass, ref state), run.Old));
        }

        /// <summary>
        /// P0.3 — añade a la hoja una flecha de borde por cada zona vecina a la que el recuerdo demuestra que se
        /// pasó andando (<see cref="MapMemory.Crossings"/>), en el borde y a la altura por donde se cruzó, o un
        /// enlace de planta. Una por vecina. Devuelve cuántas son nuevas.
        /// </summary>
        public int AddLinks(MapSheet sheet, MapMemory memory)
        {
            if (!sheet.HasZone) return 0;
            memory.Crossings(_crossings);

            MapZone zone = sheet.Zone;
            int cellsPerChunk = memory.CellsPerChunk;
            int added = 0;
            foreach (MapCrossing crossing in _crossings)
            {
                MapZone other;
                if (crossing.From.Equals(zone)) other = crossing.To;
                else if (crossing.To.Equals(zone)) other = crossing.From;
                else continue;
                if (sheet.HasLinkTo(other)) continue;

                float max = cellsPerChunk - 0.5f;
                float localX = Clamp(crossing.FromCellX - zone.ChunkX * cellsPerChunk + 0.5f, 0.5f, max);
                float localZ = Clamp(crossing.FromCellZ - zone.ChunkZ * cellsPerChunk + 0.5f, 0.5f, max);
                MapLinkSide side;

                if (other.Storey != zone.Storey)
                {
                    side = other.Storey > zone.Storey ? MapLinkSide.StoreyUp : MapLinkSide.StoreyDown;
                }
                else
                {
                    int dx = other.ChunkX - zone.ChunkX, dz = other.ChunkZ - zone.ChunkZ;
                    if (Math.Abs(dx) + Math.Abs(dz) != 1) continue; // por una esquina no se demuestra un borde
                    if (dx > 0) { side = MapLinkSide.East; localX = cellsPerChunk - ArrowInsetCells; }
                    else if (dx < 0) { side = MapLinkSide.West; localX = ArrowInsetCells; }
                    else if (dz > 0) { side = MapLinkSide.North; localZ = cellsPerChunk - ArrowInsetCells; }
                    else { side = MapLinkSide.South; localZ = ArrowInsetCells; }
                }

                sheet.Links.Add(new MapLink(other, side, localX, localZ));
                added++;
            }

            return added;
        }

        /// <summary>
        /// P0.3 — las aristas pared↔suelo de lo visto en los últimos <paramref name="maxAgeSeconds"/> en
        /// <paramref name="zone"/>, con la misma regla que el dibujo pero INCLUYENDO lo ya dibujado. Rellena
        /// <paramref name="keys"/> ordenadas y sin repetir.
        /// </summary>
        public int RecentEdges(MapZone zone, MapMemory memory, double now, double maxAgeSeconds, List<long> keys)
        {
            keys.Clear();
            _floorAge.Clear();
            memory.CellsInZone(zone, now, _cells, 1, maxAgeSeconds, includeConsumed: true);

            foreach (RememberedCell cell in _cells)
                if (cell.Kind == MapCellKind.Floor) _floorAge[CellKey(cell.X, cell.Z)] = cell.Age;

            foreach (RememberedCell cell in _cells)
            {
                if (cell.Kind != MapCellKind.Wall) continue;
                if (_floorAge.ContainsKey(CellKey(cell.X - 1, cell.Z))) keys.Add(EdgeKey(true, cell.X, cell.Z));
                if (_floorAge.ContainsKey(CellKey(cell.X + 1, cell.Z))) keys.Add(EdgeKey(true, cell.X + 1, cell.Z));
                if (_floorAge.ContainsKey(CellKey(cell.X, cell.Z - 1))) keys.Add(EdgeKey(false, cell.Z, cell.X));
                if (_floorAge.ContainsKey(CellKey(cell.X, cell.Z + 1))) keys.Add(EdgeKey(false, cell.Z + 1, cell.X));
            }

            keys.Sort();
            int unique = 0;
            for (int i = 0; i < keys.Count; i++)
                if (i == 0 || keys[i] != keys[i - 1]) keys[unique++] = keys[i];
            keys.RemoveRange(unique, keys.Count - unique);
            return keys.Count;
        }

        /// <summary>
        /// P0.3 — «Ubicarme»: qué fracción de las paredes vistas en los últimos <paramref name="recentSeconds"/>
        /// está ya DIBUJADA en la hoja. Devuelve -1 si se ha visto demasiado poco para decidir
        /// (<see cref="MinRecognitionEdges"/>). Un chunk movido dibuja otras paredes y sale bajo sin código aparte.
        /// </summary>
        public float Recognize(MapSheet sheet, MapMemory memory, double now, double recentSeconds)
        {
            if (!sheet.HasZone) return -1f;
            int count = RecentEdges(sheet.Zone, memory, now, recentSeconds, _recentKeys);
            if (count < MinRecognitionEdges) return -1f;

            int hits = 0;
            foreach (long key in _recentKeys)
                if (sheet.HasEdge(key)) hits++;
            return hits / (float)count;
        }

        private static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;

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

        /// <summary>
        /// Una pasada de boli sobre la pared: la posición que se recuerda es la buena, el GESTO no es perfecto.
        /// Temblor punto a punto, ondulación lenta de pulso, algo de torsión, extremos que se pasan o se quedan
        /// cortos (las esquinas no cierran limpias) y, a partir de la segunda pasada, desplazada y a veces más corta.
        /// </summary>
        private static float[] Sketch(MapRun stroke, MapZone zone, int cellsPerChunk, int pass, ref uint state)
        {
            float jitter = stroke.Old ? 0.2f : 0.07f;
            int length = stroke.LengthCells;
            int count = Math.Max(3, (int)Math.Ceiling(length / 0.75f) + 1);

            float startOver = (Unit(ref state) - 0.35f) * 0.55f;
            float endOver = (Unit(ref state) - 0.35f) * 0.55f;
            if (pass > 0 && Unit(ref state) < 0.45f)
            {
                // Repasar no es volver a trazar entero: una de las puntas se queda corta.
                float trim = length * 0.35f * Unit(ref state);
                if (Unit(ref state) < 0.5f) startOver -= trim;
                else endOver -= trim;
            }

            float offset = pass == 0 ? 0f : (Unit(ref state) - 0.5f) * 0.24f;
            float tilt = (Unit(ref state) - 0.5f) * (stroke.Old ? 0.3f : 0.16f);
            float waveAmplitude = stroke.Old ? 0.12f : 0.06f;
            float waveFrequency = 0.35f + Unit(ref state) * 0.5f;
            float wavePhase = Unit(ref state) * 6.2831855f;

            float line = stroke.Line - (stroke.Vertical ? zone.ChunkX : zone.ChunkZ) * cellsPerChunk;
            float from = stroke.From - (stroke.Vertical ? zone.ChunkZ : zone.ChunkX) * cellsPerChunk;
            float span = Math.Max(0.2f, length + startOver + endOver);

            var points = new float[count * 2];
            for (int i = 0; i < count; i++)
            {
                float u = i / (float)(count - 1);
                float along = from - startOver + span * u;
                float across = line + offset + tilt * (u - 0.5f) +
                               waveAmplitude * (float)Math.Sin(wavePhase + along * waveFrequency) +
                               (Unit(ref state) - 0.5f) * 2f * jitter;
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
