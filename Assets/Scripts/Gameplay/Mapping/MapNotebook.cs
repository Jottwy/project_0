using System;
using System.Collections.Generic;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>Dónde está el jugador para la libreta: celda y planta de la última muestra del recuerdo.</summary>
    public readonly struct MapHere
    {
        public readonly bool Known;
        public readonly int CellX;
        public readonly int CellZ;
        public readonly int Storey;

        public MapHere(bool known, int cellX, int cellZ, int storey)
        {
            Known = known;
            CellX = cellX;
            CellZ = cellZ;
            Storey = storey;
        }
    }

    public enum MapLocateResult : byte
    {
        /// <summary>Todavía no hay recuerdo.</summary>
        NoMemory = 0,
        /// <summary>Menos de <see cref="MapSheetStrokeBuilder.MinRecognitionEdges"/> paredes vistas: sin veredicto.</summary>
        TooLittleSeen = 1,
        /// <summary>No te reconoces: en tu mapa está en blanco.</summary>
        Blank = 2,
        Unsure = 3,
        Sure = 4,
    }

    /// <summary>
    /// P0.5 de MAPPING-PROTOTYPE — la libreta sin pantalla: hojas, boli, «Coger hoja», dibujo paso a paso,
    /// «Ubicarme» y la última ubicación. C# puro; la usan la libreta de prueba (IMGUI, <c>N</c>), el plano
    /// (<c>M</c>) y la pestaña «Notas» del libro de supervivencia. El tiempo llega por parámetro.
    /// </summary>
    /// <remarks>
    /// El recuerdo sólo se consume cuando un dibujo termina ENTERO: cancelar (moverse, cerrar, enfundar) deja lo
    /// trazado en la hoja y lo que faltaba en el recuerdo. <see cref="Version"/> cambia con todo lo que altera lo que
    /// se ve, para que cada vista repinte sólo cuando hace falta.
    /// </remarks>
    public sealed class MapNotebook
    {
        public float MaxDrawSeconds = 3f;
        public double RecognitionSeconds = 8.0;
        public float SureThreshold = 0.7f;
        public float UnsureThreshold = 0.4f;

        /// <summary>Cómo se escriben las líneas MAPSHEET y MAPFIX. Null = en ningún sitio.</summary>
        public Action<string> Log;

        public readonly MapPen Pen;

        private readonly List<MapSheet> _sheets = new List<MapSheet>();
        private readonly MapSheetStrokeBuilder _builder = new MapSheetStrokeBuilder();
        private readonly List<long> _recentKeys = new List<long>();
        private readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();

        private MapSheetLayer _layer;
        private int _drawIndex;
        private float _drawRate;
        private float _drawAccumulator;
        private double _buildMs;

        public MapNotebook(MapPen pen)
        {
            Pen = pen ?? throw new ArgumentNullException(nameof(pen));
        }

        public IReadOnlyList<MapSheet> Sheets => _sheets;
        public int CurrentIndex { get; private set; } = -1;
        public MapSheet CurrentSheet => CurrentIndex >= 0 ? _sheets[CurrentIndex] : null;
        public string Status { get; private set; } = "";
        public bool OfferMapHere { get; private set; }
        public bool Drawing { get; private set; }
        public int DrawCount { get; private set; }
        public int DrawCommitted { get; private set; }
        public int Version { get; private set; }

        public bool HasFix { get; private set; }
        public int FixSheetIndex { get; private set; } = -1;
        public MapZone FixZone { get; private set; }
        /// <summary>Posición de la última ubicación en celdas LOCALES de su hoja.</summary>
        public float FixLocalX { get; private set; }
        public float FixLocalZ { get; private set; }
        /// <summary>Reloj que pasó quien llamó a <see cref="Locate"/>.</summary>
        public float FixTime { get; private set; }

        public void Select(int index)
        {
            if (Drawing || index < 0 || index >= _sheets.Count || index == CurrentIndex) return;
            CurrentIndex = index;
            Version++;
        }

        public bool TakeSheet(MapMemory memory, MapHere here)
        {
            if (Drawing) return false;
            if (memory == null || !here.Known)
            {
                Status = "Todavía no recuerdas nada.";
                return false;
            }

            int id = _sheets.Count + 1;
            var sheet = new MapSheet(id, id * 7919 + 13);
            sheet.AssignZone(memory.ZoneOfCell(here.CellX, here.CellZ, here.Storey));
            _sheets.Add(sheet);
            CurrentIndex = _sheets.Count - 1;
            Status = $"Hoja {id} en blanco.";
            OfferMapHere = false;
            Version++;
            return true;
        }

        /// <summary>Empieza a pasar al papel lo que se recuerda de la zona de la hoja abierta.</summary>
        public void StartDrawing(MapMemory memory, double now, double oldAfterSeconds)
        {
            if (Drawing || CurrentIndex < 0 || memory == null) return;
            MapSheet sheet = _sheets[CurrentIndex];
            OfferMapHere = false;

            _watch.Restart();
            // P0.3 — las flechas de borde salen del mismo recuerdo que los trazos.
            int newLinks = _builder.AddLinks(sheet, memory);
            DrawCount = _builder.Build(sheet, memory, now, oldAfterSeconds);
            _watch.Stop();
            _buildMs = _watch.Elapsed.TotalMilliseconds;
            if (newLinks > 0) Version++;

            if (DrawCount == 0)
            {
                Status = newLinks > 0
                    ? $"Nada nuevo que dibujar, pero apuntas {newLinks} salida(s) de la zona."
                    : "No recuerdas nada nuevo de la zona de esta hoja.";
                return;
            }

            _layer = sheet.BeginLayer(Pen);
            _drawIndex = 0;
            DrawCommitted = 0;
            _drawAccumulator = 0f;
            _drawRate = Math.Max(12f, DrawCount / Math.Max(0.1f, MaxDrawSeconds));
            Drawing = true;
            Status = "Dibujando…";
        }

        /// <summary>Avanza el dibujo <paramref name="deltaSeconds"/>. Termina solo al acabar o si se acaba la tinta.</summary>
        public void Step(MapMemory memory, float deltaSeconds)
        {
            if (!Drawing) return;
            MapSheet sheet = _sheets[CurrentIndex];
            _drawAccumulator += deltaSeconds * _drawRate;
            while (_drawAccumulator >= 1f && _drawIndex < DrawCount)
            {
                _drawAccumulator -= 1f;
                if (!_builder.Commit(sheet, _layer, _drawIndex, Pen, memory))
                {
                    Finish(memory, false, "Se ha acabado la tinta.");
                    return;
                }

                _drawIndex++;
                DrawCommitted++;
                Version++;
            }

            if (_drawIndex >= DrawCount) Finish(memory, true, "Dibujado.");
        }

        /// <summary>Corta un dibujo a medias: lo trazado se queda y lo que faltaba sigue en el recuerdo.</summary>
        public void Cancel(string status)
        {
            if (Drawing) Finish(null, false, status);
        }

        private void Finish(MapMemory memory, bool complete, string status)
        {
            if (!Drawing) return;
            Drawing = false;
            Status = status;
            // Sólo un dibujo ENTERO saca la zona del recuerdo: lo ya trazado no se vuelve a cobrar (las aristas quedan
            // marcadas en la hoja).
            if (complete && memory != null) memory.Consume(_sheets[CurrentIndex].Zone);

            Log?.Invoke($"MAPSHEET sheet={_sheets[CurrentIndex].Id} strokes={DrawCommitted}/{DrawCount} " +
                        $"ms_build={_buildMs:F3} ink={Pen.Ink:F3} complete={complete}");
        }

        /// <summary>
        /// P0.3 — «Ubicarme»: compara lo visto en los últimos <see cref="RecognitionSeconds"/> con las paredes dibujadas
        /// de cada hoja de tu planta y marca «estás aquí» en la que más coincida. <paramref name="clock"/> se guarda
        /// como <see cref="FixTime"/>.
        /// </summary>
        public MapLocateResult Locate(MapMemory memory, MapHere here, double now, float clock)
        {
            if (memory == null || !here.Known)
            {
                Status = "Todavía no recuerdas nada.";
                return MapLocateResult.NoMemory;
            }

            OfferMapHere = false;
            _watch.Restart();
            int best = -1;
            float bestScore = -1f;
            for (int i = 0; i < _sheets.Count; i++)
            {
                MapSheet sheet = _sheets[i];
                if (!sheet.HasZone || sheet.Zone.Storey != here.Storey) continue;
                float score = _builder.Recognize(sheet, memory, now, RecognitionSeconds);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            _watch.Stop();

            MapLocateResult result;
            if (bestScore < 0f)
            {
                MapZone zone = memory.ZoneOfCell(here.CellX, here.CellZ, here.Storey);
                int seen = _builder.RecentEdges(zone, memory, now, RecognitionSeconds, _recentKeys);
                if (seen < MapSheetStrokeBuilder.MinRecognitionEdges)
                {
                    result = MapLocateResult.TooLittleSeen;
                    Status = "Has visto muy poco en los últimos segundos: mira alrededor y vuelve a intentarlo.";
                }
                else
                {
                    result = MapLocateResult.Blank;
                    Status = "No reconoces este sitio: en tu mapa está en blanco.";
                    OfferMapHere = true;
                }
            }
            else if (bestScore >= UnsureThreshold)
            {
                MapSheet sheet = _sheets[best];
                bool sure = bestScore >= SureThreshold;
                int cellsPerChunk = memory.CellsPerChunk;
                float localX = here.CellX - sheet.Zone.ChunkX * cellsPerChunk + 0.5f;
                float localZ = here.CellZ - sheet.Zone.ChunkZ * cellsPerChunk + 0.5f;
                sheet.Marks.Add(new MapMark(sure ? MapMarkKind.Here : MapMarkKind.HereUnsure, localX, localZ, Pen.Argb));

                HasFix = true;
                FixSheetIndex = best;
                FixZone = sheet.Zone;
                FixLocalX = localX;
                FixLocalZ = localZ;
                FixTime = clock;
                CurrentIndex = best;
                Version++;
                result = sure ? MapLocateResult.Sure : MapLocateResult.Unsure;
                Status = sure
                    ? $"Te reconoces: coincide un {bestScore * 100f:F0} % con la hoja {sheet.Id}."
                    : $"Te suena ({bestScore * 100f:F0} %): crees que estás por aquí, en la hoja {sheet.Id}.";
            }
            else
            {
                result = MapLocateResult.Blank;
                Status = $"No reconoces este sitio ({bestScore * 100f:F0} %): en tu mapa está en blanco.";
                OfferMapHere = true;
            }

            Log?.Invoke($"MAPFIX result={result} score={bestScore:F3} sheet={(best >= 0 ? _sheets[best].Id : 0)} " +
                        $"sheets={_sheets.Count} ms={_watch.Elapsed.TotalMilliseconds:F3}");
            return result;
        }
    }
}
