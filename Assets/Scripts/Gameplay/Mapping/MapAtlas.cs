using System;
using System.Collections.Generic;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>
    /// P0.4 de MAPPING-PROTOTYPE — el plano maestro de la base (MAPPING-ROADMAP §7b). C# puro.
    /// </summary>
    /// <remarks>
    /// **Colocación solo por lo demostrado (D13).** La zona de la base se coloca siempre; otra zona se coloca si
    /// una flecha de borde la une con una zona YA colocada, en cualquiera de las dos hojas. El enlace decide SI sale
    /// en el tablero, no dónde: la posición es la de la zona.
    ///
    /// **Limpias.** Una por zona; una nueva tapa a la anterior, que pasa a <see cref="Archived"/>.
    ///
    /// Los conjuntos sólo se CONSULTAN; toda salida sale ordenada por planta, Z y X (regla 13).
    /// </remarks>
    public sealed class MapAtlas
    {
        /// <summary>Tinta negra fina de lo pasado a limpio.</summary>
        public const uint CleanArgb = 0xFF1C1C22u;
        public const float CleanWidthPx = 1.8f;

        private readonly List<MapSheet> _cleans = new List<MapSheet>();
        private readonly List<MapSheet> _archived = new List<MapSheet>();
        private readonly List<MapZone> _known = new List<MapZone>();
        private readonly List<MapZone> _edgeA = new List<MapZone>();
        private readonly List<MapZone> _edgeB = new List<MapZone>();
        private readonly HashSet<MapZone> _placed = new HashSet<MapZone>();
        private readonly Queue<MapZone> _queue = new Queue<MapZone>();

        public bool HasBase { get; private set; }
        public MapZone Base { get; private set; }

        /// <summary>Limpias vigentes, ordenadas por zona.</summary>
        public IReadOnlyList<MapSheet> Cleans => _cleans;

        /// <summary>Limpias tapadas por otra más nueva, en orden de archivo.</summary>
        public IReadOnlyList<MapSheet> Archived => _archived;

        public static readonly Comparison<MapZone> ByZone = (a, b) =>
        {
            if (a.Storey != b.Storey) return a.Storey.CompareTo(b.Storey);
            if (a.ChunkZ != b.ChunkZ) return a.ChunkZ.CompareTo(b.ChunkZ);
            return a.ChunkX.CompareTo(b.ChunkX);
        };

        /// <summary>La base se fija una vez.</summary>
        public void SetBase(MapZone zone)
        {
            if (HasBase) return;
            Base = zone;
            HasBase = true;
        }

        public MapSheet CleanOf(MapZone zone)
        {
            foreach (MapSheet clean in _cleans)
                if (clean.Zone.Equals(zone)) return clean;
            return null;
        }

        /// <summary>Cuelga una limpia en el plano. Si ya había una de su zona, la tapa y la archiva.</summary>
        public void AddClean(MapSheet clean)
        {
            if (clean == null || !clean.HasZone) throw new ArgumentException("la limpia necesita zona", nameof(clean));

            for (int i = 0; i < _cleans.Count; i++)
            {
                int order = ByZone(_cleans[i].Zone, clean.Zone);
                if (order == 0)
                {
                    _archived.Add(_cleans[i]);
                    _cleans[i] = clean;
                    return;
                }

                if (order > 0)
                {
                    _cleans.Insert(i, clean);
                    return;
                }
            }

            _cleans.Add(clean);
        }

        /// <summary>
        /// Reparte las zonas con hoja (borradores de <paramref name="drafts"/> y limpias) y la base entre
        /// <paramref name="placed"/> y <paramref name="unplaced"/>, las dos ordenadas por zona.
        /// </summary>
        public void Place(IReadOnlyList<MapSheet> drafts, List<MapZone> placed, List<MapZone> unplaced)
        {
            placed.Clear();
            unplaced.Clear();
            _known.Clear();
            _edgeA.Clear();
            _edgeB.Clear();
            _placed.Clear();
            _queue.Clear();

            if (HasBase) _known.Add(Base);
            foreach (MapSheet clean in _cleans) Collect(clean);
            if (drafts != null)
                foreach (MapSheet draft in drafts)
                    Collect(draft);

            if (HasBase)
            {
                _placed.Add(Base);
                _queue.Enqueue(Base);
            }

            while (_queue.Count > 0)
            {
                MapZone zone = _queue.Dequeue();
                for (int i = 0; i < _edgeA.Count; i++)
                {
                    MapZone other;
                    if (_edgeA[i].Equals(zone)) other = _edgeB[i];
                    else if (_edgeB[i].Equals(zone)) other = _edgeA[i];
                    else continue;
                    if (_placed.Add(other)) _queue.Enqueue(other);
                }
            }

            _known.Sort(ByZone);
            for (int i = 0; i < _known.Count; i++)
            {
                if (i > 0 && _known[i].Equals(_known[i - 1])) continue;
                (_placed.Contains(_known[i]) ? placed : unplaced).Add(_known[i]);
            }
        }

        private void Collect(MapSheet sheet)
        {
            if (!sheet.HasZone) return;
            _known.Add(sheet.Zone);
            foreach (MapLink link in sheet.Links)
            {
                _edgeA.Add(sheet.Zone);
                _edgeB.Add(link.To);
            }
        }

        /// <summary>
        /// Pasa a limpio <paramref name="source"/>: una línea recta por cada tramo de pared dibujada (aristas contiguas
        /// fundidas), tinta negra fina y firme, y las mismas flechas. Sin marcas: «estás aquí» no se pasa a limpio.
        /// Conserva las aristas, así que «Ubicarme» reconoce la limpia igual que el borrador.
        /// </summary>
        public static MapSheet CleanCopy(MapSheet source, int id, int cellsPerChunk, List<long> scratch)
        {
            if (source == null || !source.HasZone) throw new ArgumentException("la hoja necesita zona", nameof(source));

            var clean = new MapSheet(id, source.Seed, clean: true);
            clean.AssignZone(source.Zone);
            var layer = new MapSheetLayer(CleanArgb, CleanWidthPx);
            clean.Layers.Add(layer);

            source.CopyEdgesTo(scratch);
            MapZone zone = source.Zone;
            int runStart = 0;
            for (int i = 1; i <= scratch.Count; i++)
            {
                if (i < scratch.Count)
                {
                    Decode(scratch[i - 1], out bool pv, out int pl, out int pp);
                    Decode(scratch[i], out bool cv, out int cl, out int cp);
                    if (cv == pv && cl == pl && cp == pp + 1) continue;
                }

                Decode(scratch[runStart], out bool vertical, out int line, out int from);
                Decode(scratch[i - 1], out _, out _, out int last);
                float across = line - (vertical ? zone.ChunkX : zone.ChunkZ) * cellsPerChunk;
                float a = from - (vertical ? zone.ChunkZ : zone.ChunkX) * cellsPerChunk;
                float b = last + 1 - (vertical ? zone.ChunkZ : zone.ChunkX) * cellsPerChunk;
                layer.Strokes.Add(new MapStroke(
                    vertical ? new[] { across, a, across, b } : new[] { a, across, b, across }, false, steady: true));

                for (int k = runStart; k < i; k++) clean.AddEdge(scratch[k]);
                runStart = i;
            }

            clean.Links.AddRange(source.Links);
            return clean;
        }

        /// <summary>Inversa de <see cref="MapSheetStrokeBuilder.EdgeKey"/>.</summary>
        public static void Decode(long key, out bool vertical, out int line, out int pos)
        {
            vertical = ((key >> 62) & 1L) == 1L;
            line = (int)(((key >> 31) & 0x7FFFFFFFL) - (1L << 30));
            pos = (int)((key & 0x7FFFFFFFL) - (1L << 30));
        }
    }
}
