using System.Collections.Generic;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>Con qué se dibuja: color, grosor y cuánta tinta queda (0..1).</summary>
    public sealed class MapPen
    {
        public readonly string Name;
        /// <summary>Color como 0xAARRGGBB.</summary>
        public readonly uint Argb;
        /// <summary>Grosor del trazo en píxeles de una hoja de 512.</summary>
        public readonly float WidthPx;
        /// <summary>Tinta que gasta un metro de pared dibujada.</summary>
        public readonly float CostPerMetre;
        public float Ink;

        public MapPen(string name, uint argb, float widthPx, float costPerMetre, float ink)
        {
            Name = name;
            Argb = argb;
            WidthPx = widthPx;
            CostPerMetre = costPerMetre;
            Ink = ink;
        }
    }

    /// <summary>Un trazo ya en el papel.</summary>
    public readonly struct MapStroke
    {
        /// <summary>Pares x,z alternos en celdas LOCALES de la zona (0..celdas por chunk).</summary>
        public readonly float[] Points;
        /// <summary>Recuerdo viejo: se pinta tembloroso y discontinuo.</summary>
        public readonly bool Old;
        /// <summary>P0.4 — pasado a limpio: grosor y tinta constantes, sin borrón.</summary>
        public readonly bool Steady;

        public MapStroke(float[] points, bool old) : this(points, old, false)
        {
        }

        public MapStroke(float[] points, bool old, bool steady)
        {
            Points = points;
            Old = old;
            Steady = steady;
        }
    }

    /// <summary>Por dónde sigue la hoja: un borde del chunk o un cambio de planta.</summary>
    public enum MapLinkSide : byte
    {
        North = 0,
        East = 1,
        South = 2,
        West = 3,
        StoreyUp = 4,
        StoreyDown = 5,
    }

    /// <summary>P0.3 — flecha de borde: el recuerdo demostró que de esta zona se pasa a <see cref="To"/> por aquí.</summary>
    public readonly struct MapLink
    {
        public readonly MapZone To;
        public readonly MapLinkSide Side;
        /// <summary>Posición de la flecha en celdas LOCALES de la zona.</summary>
        public readonly float LocalX;
        public readonly float LocalZ;

        public MapLink(MapZone to, MapLinkSide side, float localX, float localZ)
        {
            To = to;
            Side = side;
            LocalX = localX;
            LocalZ = localZ;
        }
    }

    public enum MapMarkKind : byte
    {
        /// <summary>Te reconociste (≥ 70 %): círculo pequeño.</summary>
        Here = 0,
        /// <summary>Te suena (40–70 %): círculo grande discontinuo.</summary>
        HereUnsure = 1,
    }

    /// <summary>P0.3 — marca de tinta en la hoja, en celdas locales.</summary>
    public readonly struct MapMark
    {
        public readonly MapMarkKind Kind;
        public readonly float LocalX;
        public readonly float LocalZ;
        public readonly uint Argb;

        public MapMark(MapMarkKind kind, float localX, float localZ, uint argb)
        {
            Kind = kind;
            LocalX = localX;
            LocalZ = localZ;
            Argb = argb;
        }
    }

    /// <summary>Todo lo dibujado de una vez con una misma herramienta.</summary>
    public sealed class MapSheetLayer
    {
        public readonly uint Argb;
        public readonly float WidthPx;
        public readonly List<MapStroke> Strokes = new List<MapStroke>();

        public MapSheetLayer(uint argb, float widthPx)
        {
            Argb = argb;
            WidthPx = widthPx;
        }
    }

    /// <summary>
    /// P0.2 de MAPPING-PROTOTYPE — una hoja: una zona (chunk × planta), sus capas de trazos y las aristas de
    /// pared ya dibujadas, para no volver a cobrarlas. C# puro.
    /// </summary>
    /// <remarks>
    /// Las aristas viven en un <see cref="HashSet{T}"/> sólo para CONSULTAR; nada se emite iterándolo (regla 13).
    /// </remarks>
    public sealed class MapSheet
    {
        public readonly int Id;
        public readonly int Seed;
        public readonly List<MapSheetLayer> Layers = new List<MapSheetLayer>();
        /// <summary>Flechas de borde, en orden de alta.</summary>
        public readonly List<MapLink> Links = new List<MapLink>();
        /// <summary>Marcas «estás aquí», en orden de alta.</summary>
        public readonly List<MapMark> Marks = new List<MapMark>();

        private readonly HashSet<long> _edges = new HashSet<long>();

        public bool HasLinkTo(MapZone zone)
        {
            foreach (MapLink link in Links)
                if (link.To.Equals(zone)) return true;
            return false;
        }

        /// <summary>P0.4 — versión pasada a limpio en la base (MAPPING-ROADMAP §7b).</summary>
        public readonly bool Clean;

        public MapSheet(int id, int seed, bool clean = false)
        {
            Id = id;
            Seed = seed;
            Clean = clean;
        }

        public bool HasZone { get; private set; }
        public MapZone Zone { get; private set; }

        /// <summary>Una hoja en blanco se queda con la primera zona donde se usa; después no cambia.</summary>
        public void AssignZone(MapZone zone)
        {
            if (HasZone) return;
            Zone = zone;
            HasZone = true;
        }

        public int EdgeCount => _edges.Count;

        public bool HasEdge(long key) => _edges.Contains(key);

        internal void AddEdge(long key) => _edges.Add(key);

        /// <summary>Las aristas dibujadas, ORDENADAS (regla 13: nunca en el orden del conjunto).</summary>
        public void CopyEdgesTo(List<long> keys)
        {
            keys.Clear();
            keys.AddRange(_edges);
            keys.Sort();
        }

        public MapSheetLayer BeginLayer(MapPen pen)
        {
            var layer = new MapSheetLayer(pen.Argb, pen.WidthPx);
            Layers.Add(layer);
            return layer;
        }
    }
}
