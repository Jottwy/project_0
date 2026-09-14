using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>
    /// P0.2 de MAPPING-PROTOTYPE — la libreta del PROTOTIPO: <c>N</c> la abre, «Coger hoja» asigna una hoja a la
    /// zona donde estás y «Dibujar» pasa al papel lo que recuerdas de ella, trazo a trazo, en hasta
    /// <see cref="maxDrawSeconds"/> y sin moverte.
    /// </summary>
    /// <remarks>
    /// **Panel IMGUI a propósito, no uGUI.** La escena de playtest no tiene <c>EventSystem</c> y el jugador de
    /// prueba no es el de STP; la libreta de verdad (P0.5) será UI en World Space sobre un wieldable, calcada
    /// del libro de supervivencia. Aquí sólo se juega la LÓGICA: recuerdo → trazos → hoja.
    ///
    /// Mientras está abierta se desactiva <see cref="playerControl"/> (en la escena de playtest, el
    /// <c>Wg3TestPlayer</c>: al desactivarse suelta el cursor). Pulsar <c>N</c> o W/A/S/D a mitad de un dibujo
    /// lo CANCELA: lo trazado se queda en la hoja y lo que faltaba sigue en el recuerdo, porque el recuerdo
    /// sólo se consume cuando el dibujo termina entero.
    /// </remarks>
    public sealed class MapNotebookView : MonoBehaviour
    {
        public MapMemorySampler sampler;
        [Tooltip("Lo que se desactiva con la libreta abierta (movimiento y mirada).")]
        public Behaviour playerControl;
        [Tooltip("P0.4: el plano de la base. Con él abierto, N no abre la libreta.")]
        public MapAtlasView atlas;

        [Header("Dibujo")]
        public float maxDrawSeconds = 3f;
        [Tooltip("Tinta que gasta un metro de pared. Con 0,002 un boli lleno da para 500 m (Joel, playtest 2026-09-13: con 0,02 se acababa a los ~42 m).")]
        public float penCostPerMetre = 0.002f;
        public float penWidthPx = 3f;
        public Color penColour = new Color(0.165f, 0.278f, 0.659f, 0.95f);
        public Color paperColour = new Color(0.945f, 0.937f, 0.898f, 1f);

        [Header("Ubicarme (P0.3, MAPPING-ROADMAP D14)")]
        [Tooltip("Cuánto de lo visto hace poco se compara con las hojas, en segundos.")]
        public float recognitionSeconds = 8f;
        [Tooltip("Coincidencia a partir de la cual te reconoces.")]
        [Range(0f, 1f)] public float sureThreshold = 0.7f;
        [Tooltip("Coincidencia a partir de la cual el sitio te suena.")]
        [Range(0f, 1f)] public float unsureThreshold = 0.4f;
        [Tooltip("Segundos que dura la cruceta sobre la hoja tras ubicarte (Joel, playtest 2026-09-13).")]
        public float crosshairSeconds = 4f;
        public Color crosshairColour = new Color(0.85f, 0.1f, 0.1f, 1f);

        private readonly List<MapSheet> _sheets = new List<MapSheet>();
        private readonly MapSheetStrokeBuilder _builder = new MapSheetStrokeBuilder();
        private readonly MapSheetRaster _raster = new MapSheetRaster();
        private readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();

        private MapPen _pen;
        private Texture2D _texture;
        private int _current = -1;
        private bool _open;
        private bool _dirty;
        private string _status = "";
        private bool _offerMapHere;
        private readonly List<long> _recentKeys = new List<long>();

        // Cruceta de «Ubicarme»: NO es tinta, se pinta encima de la hoja y se desvanece.
        private int _crosshairSheet = -1;
        private float _crosshairX;
        private float _crosshairZ;
        private float _crosshairUntil;

        private bool _hasFix;
        private MapZone _fixZone;
        private float _fixLocalX;
        private float _fixLocalZ;
        private float _fixTime;

        public bool IsOpen => _open;
        public IReadOnlyList<MapSheet> Sheets => _sheets;
        public MapSheet CurrentSheet => _current >= 0 ? _sheets[_current] : null;
        public MapPen Pen => _pen;

        /// <summary>P0.4 — la última vez que «Ubicarme» te reconoció: zona, celdas locales y <c>Time.unscaledTime</c>.</summary>
        public bool TryGetFix(out MapZone zone, out float localX, out float localZ, out float time)
        {
            zone = _fixZone;
            localX = _fixLocalX;
            localZ = _fixLocalZ;
            time = _fixTime;
            return _hasFix;
        }

        /// <summary>P0.4 — el plano la cierra al abrirse; un dibujo a medias se cancela como con N.</summary>
        public void CloseIfOpen()
        {
            if (_drawing) Finish(false, "Cancelado: lo trazado se queda.");
            if (_open) Close();
        }

        private bool _drawing;
        private MapSheetLayer _layer;
        private int _drawIndex;
        private int _drawCount;
        private int _drawCommitted;
        private float _drawRate;
        private float _drawAccumulator;
        private double _buildMs;
        private double _rasterMaxMs;

        private void Awake()
        {
            _pen = new MapPen("Boli azul", ToArgb(penColour), penWidthPx, penCostPerMetre, 1f);
        }

        private void OnDestroy()
        {
            if (_texture != null) Destroy(_texture);
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.nKey.wasPressedThisFrame)
            {
                if (_drawing) Finish(false, "Cancelado: lo trazado se queda.");
                if (_open) Close();
                else if (atlas == null || !atlas.IsOpen) Open();
            }

            if (_drawing)
            {
                if (keyboard.wKey.isPressed || keyboard.aKey.isPressed || keyboard.sKey.isPressed || keyboard.dKey.isPressed)
                    Finish(false, "Te has movido: dibujo cancelado, lo trazado se queda.");
                else
                    StepDrawing(Time.deltaTime);
            }

            if (_dirty && _current >= 0) Repaint();
        }

        private void Open()
        {
            _open = true;
            // El Wg3TestPlayer suelta el cursor al desactivarse.
            if (playerControl != null) playerControl.enabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _dirty = true;
        }

        private void Close()
        {
            _open = false;
            if (playerControl != null)
            {
                playerControl.enabled = true;
                // Wg3TestPlayer.SetLooking es privado y al desactivarse se quedó en «sin mirar»: SendMessage lo
                // alcanza sin tocar código de WG3. Sólo vale para el jugador de prueba; P0.5 usa el de STP.
                playerControl.SendMessage("SetLooking", true, SendMessageOptions.DontRequireReceiver);
            }
        }

        private void TakeSheet()
        {
            if (sampler == null || sampler.Memory == null || !sampler.HasSample)
            {
                _status = "Todavía no recuerdas nada.";
                return;
            }

            int id = _sheets.Count + 1;
            var sheet = new MapSheet(id, id * 7919 + 13);
            sheet.AssignZone(sampler.Memory.ZoneOfCell(sampler.LastCellX, sampler.LastCellZ, sampler.LastStorey));
            _sheets.Add(sheet);
            _current = _sheets.Count - 1;
            _status = $"Hoja {id} en blanco.";
            _offerMapHere = false;
            _dirty = true;
        }

        /// <summary>
        /// P0.3 — «Ubicarme»: compara lo visto en los últimos <see cref="recognitionSeconds"/> con las paredes
        /// dibujadas de cada hoja de tu planta y marca «estás aquí» en la que más coincida.
        /// </summary>
        private void Locate()
        {
            if (sampler == null || sampler.Memory == null || !sampler.HasSample)
            {
                _status = "Todavía no recuerdas nada.";
                return;
            }

            MapMemory memory = sampler.Memory;
            double now = Time.timeAsDouble;
            _offerMapHere = false;

            _watch.Restart();
            int best = -1;
            float bestScore = -1f;
            for (int i = 0; i < _sheets.Count; i++)
            {
                MapSheet sheet = _sheets[i];
                if (!sheet.HasZone || sheet.Zone.Storey != sampler.LastStorey) continue;
                float score = _builder.Recognize(sheet, memory, now, recognitionSeconds);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            _watch.Stop();

            string result;
            if (bestScore < 0f)
            {
                MapZone here = memory.ZoneOfCell(sampler.LastCellX, sampler.LastCellZ, sampler.LastStorey);
                int seen = _builder.RecentEdges(here, memory, now, recognitionSeconds, _recentKeys);
                if (seen < MapSheetStrokeBuilder.MinRecognitionEdges)
                {
                    result = "poco";
                    _status = "Has visto muy poco en los últimos segundos: mira alrededor y vuelve a intentarlo.";
                }
                else
                {
                    result = "blanco";
                    _status = "No reconoces este sitio: en tu mapa está en blanco.";
                    _offerMapHere = true;
                }
            }
            else if (bestScore >= unsureThreshold)
            {
                MapSheet sheet = _sheets[best];
                bool sure = bestScore >= sureThreshold;
                int cellsPerChunk = memory.CellsPerChunk;
                float localX = sampler.LastCellX - sheet.Zone.ChunkX * cellsPerChunk + 0.5f;
                float localZ = sampler.LastCellZ - sheet.Zone.ChunkZ * cellsPerChunk + 0.5f;
                sheet.Marks.Add(new MapMark(sure ? MapMarkKind.Here : MapMarkKind.HereUnsure, localX, localZ, _pen.Argb));
                _crosshairSheet = best;
                _crosshairX = localX / cellsPerChunk;
                _crosshairZ = localZ / cellsPerChunk;
                _crosshairUntil = Time.unscaledTime + crosshairSeconds;
                _hasFix = true;
                _fixZone = sheet.Zone;
                _fixLocalX = localX;
                _fixLocalZ = localZ;
                _fixTime = Time.unscaledTime;                _current = best;
                _dirty = true;
                result = sure ? "seguro" : "dudoso";
                _status = sure
                    ? $"Te reconoces: coincide un {bestScore * 100f:F0} % con la hoja {sheet.Id}."
                    : $"Te suena ({bestScore * 100f:F0} %): crees que estás por aquí, en la hoja {sheet.Id}.";
            }
            else
            {
                result = "blanco";
                _status = $"No reconoces este sitio ({bestScore * 100f:F0} %): en tu mapa está en blanco.";
                _offerMapHere = true;
            }

            Debug.Log($"MAPFIX result={result} score={bestScore:F3} sheet={(best >= 0 ? _sheets[best].Id : 0)} " +
                      $"sheets={_sheets.Count} ms={_watch.Elapsed.TotalMilliseconds:F3}");
        }

        private void StartDrawing()
        {
            if (_current < 0 || sampler == null || sampler.Memory == null) return;
            MapSheet sheet = _sheets[_current];
            _offerMapHere = false;

            _watch.Restart();
            // P0.3 — las flechas de borde salen del mismo recuerdo que los trazos.
            int newLinks = _builder.AddLinks(sheet, sampler.Memory);
            _drawCount = _builder.Build(sheet, sampler.Memory, Time.timeAsDouble, sampler.memorySeconds / 3.0);
            _watch.Stop();
            _buildMs = _watch.Elapsed.TotalMilliseconds;
            if (newLinks > 0) _dirty = true;

            if (_drawCount == 0)
            {
                _status = newLinks > 0
                    ? $"Nada nuevo que dibujar, pero apuntas {newLinks} salida(s) de la zona."
                    : "No recuerdas nada nuevo de la zona de esta hoja.";
                return;
            }

            _layer = sheet.BeginLayer(_pen);
            _drawIndex = 0;
            _drawCommitted = 0;
            _drawAccumulator = 0f;
            _drawRate = Mathf.Max(12f, _drawCount / Mathf.Max(0.1f, maxDrawSeconds));
            _rasterMaxMs = 0;
            _drawing = true;
            _status = "Dibujando…";
        }

        private void StepDrawing(float deltaTime)
        {
            MapSheet sheet = _sheets[_current];
            _drawAccumulator += deltaTime * _drawRate;
            while (_drawAccumulator >= 1f && _drawIndex < _drawCount)
            {
                _drawAccumulator -= 1f;
                if (!_builder.Commit(sheet, _layer, _drawIndex, _pen, sampler.Memory))
                {
                    Finish(false, "Se ha acabado la tinta.");
                    return;
                }

                _drawIndex++;
                _drawCommitted++;
                _dirty = true;
            }

            if (_drawIndex >= _drawCount) Finish(true, "Dibujado.");
        }

        private void Finish(bool complete, string status)
        {
            if (!_drawing) return;
            _drawing = false;
            _status = status;
            // Sólo un dibujo ENTERO saca la zona del recuerdo: si se corta, lo que faltaba sigue ahí y lo ya
            // trazado no se vuelve a cobrar (las aristas quedan marcadas en la hoja).
            if (complete) sampler.Memory.Consume(_sheets[_current].Zone);

            Debug.Log($"MAPSHEET sheet={_sheets[_current].Id} strokes={_drawCommitted}/{_drawCount} " +
                      $"ms_build={_buildMs:F3} ms_raster_max={_rasterMaxMs:F3} ink={_pen.Ink:F3} complete={complete}");
        }

        private void Repaint()
        {
            _dirty = false;
            if (_texture == null)
            {
                _texture = new Texture2D(_raster.Size, _raster.Size, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }

            _watch.Restart();
            _raster.DrawSheet(_sheets[_current], ToArgb(paperColour), sampler.Memory.CellsPerChunk);
            _texture.LoadRawTextureData(_raster.Rgba);
            _texture.Apply(false);
            _watch.Stop();
            if (_watch.Elapsed.TotalMilliseconds > _rasterMaxMs) _rasterMaxMs = _watch.Elapsed.TotalMilliseconds;
        }

        private void OnGUI()
        {
            if (!_open) return;

            float sheetSize = Mathf.Min(_raster.Size, Screen.height - 150f, Screen.width - 60f);
            float width = sheetSize + 28f;
            var panel = new Rect((Screen.width - width) * 0.5f, 20f, width, sheetSize + 160f);
            GUI.Box(panel, "Libreta");

            float x = panel.x + 14f;
            float y = panel.y + 26f;

            GUI.enabled = !_drawing;
            for (int i = 0; i < _sheets.Count; i++)
            {
                string label = (i == _current ? "▶ " : "") + "Hoja " + _sheets[i].Id;
                if (GUI.Button(new Rect(x + i * 78f, y, 74f, 22f), label))
                {
                    _current = i;
                    _dirty = true;
                }
            }

            y += 28f;
            if (GUI.Button(new Rect(x, y, 100f, 24f), "Coger hoja")) TakeSheet();
            GUI.enabled = !_drawing && _current >= 0;
            if (GUI.Button(new Rect(x + 106f, y, 90f, 24f), "Dibujar")) StartDrawing();
            GUI.enabled = !_drawing;
            if (GUI.Button(new Rect(x + 202f, y, 90f, 24f), "Ubicarme")) Locate();
            GUI.enabled = true;

            GUI.Label(new Rect(x + 300f, y + 2f, 45f, 22f), "Tinta");
            GUI.Box(new Rect(x + 342f, y + 6f, 100f, 12f), GUIContent.none);
            GUI.Box(new Rect(x + 342f, y + 6f, 100f * Mathf.Clamp01(_pen.Ink), 12f), GUIContent.none);
            GUI.Label(new Rect(x + 448f, y + 2f, 60f, 22f), $"{Mathf.Clamp01(_pen.Ink) * 100f:F0} %");

            y += 32f;
            if (_current >= 0 && _texture != null)
            {
                GUI.DrawTexture(new Rect(x, y, sheetSize, sheetSize), _texture);
                DrawCrosshair(x, y, sheetSize);
            }
            else
                GUI.Label(new Rect(x, y, sheetSize, 22f), "Coge una hoja en blanco para empezar.");

            y += sheetSize + 6f;
            GUI.Label(new Rect(x, y, sheetSize, 22f), _status);
            string zone = _current >= 0 ? _sheets[_current].Zone.ToString() : "—";
            GUI.Label(new Rect(x, y + 20f, sheetSize, 22f), $"N cierra · W/A/S/D o N a mitad cancelan · zona interna (depuración): {zone}");
            if (_offerMapHere && !_drawing && GUI.Button(new Rect(x, y + 44f, 220f, 24f), "Coger hoja y mapear aquí"))
            {
                TakeSheet();
                StartDrawing();
            }
        }

        /// <summary>
        /// Cruceta de «Ubicarme» sobre la hoja: parpadea al principio para que el ojo la encuentre y se desvanece
        /// en el último segundo. La hoja tiene Z hacia arriba y la pantalla Y hacia abajo.
        /// </summary>
        private void DrawCrosshair(float sheetX, float sheetY, float sheetSize)
        {
            float left = _crosshairUntil - Time.unscaledTime;
            if (_crosshairSheet != _current || left <= 0f) return;

            float elapsed = crosshairSeconds - left;
            float alpha = Mathf.Clamp01(left);
            if (elapsed < 1f && Mathf.Repeat(elapsed * 4f, 1f) > 0.6f) alpha *= 0.25f;

            float cx = sheetX + _crosshairX * sheetSize;
            float cy = sheetY + (1f - _crosshairZ) * sheetSize;
            const float arm = 22f, gap = 5f, thick = 2f;

            Color previous = GUI.color;
            GUI.color = new Color(crosshairColour.r, crosshairColour.g, crosshairColour.b, crosshairColour.a * alpha);
            Texture2D white = Texture2D.whiteTexture;
            GUI.DrawTexture(new Rect(cx - arm, cy - thick * 0.5f, arm - gap, thick), white);
            GUI.DrawTexture(new Rect(cx + gap, cy - thick * 0.5f, arm - gap, thick), white);
            GUI.DrawTexture(new Rect(cx - thick * 0.5f, cy - arm, thick, arm - gap), white);
            GUI.DrawTexture(new Rect(cx - thick * 0.5f, cy + gap, thick, arm - gap), white);
            GUI.color = previous;
        }

        private static uint ToArgb(Color colour)
        {
            Color32 c = colour;
            return ((uint)c.a << 24) | ((uint)c.r << 16) | ((uint)c.g << 8) | c.b;
        }
    }
}
