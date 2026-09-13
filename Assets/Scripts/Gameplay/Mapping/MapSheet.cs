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

        public MapStroke(float[] points, bool old)
        {
            Points = points;
            Old = old;
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

        private readonly HashSet<long> _edges = new HashSet<long>();

        public MapSheet(int id, int seed)
        {
            Id = id;
            Seed = seed;
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

        public MapSheetLayer BeginLayer(MapPen pen)
        {
            var layer = new MapSheetLayer(pen.Argb, pen.WidthPx);
            Layers.Add(layer);
            return layer;
        }
    }
}
