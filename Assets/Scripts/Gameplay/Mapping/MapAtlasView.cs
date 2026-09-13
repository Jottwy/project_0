using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>
    /// P0.4 de MAPPING-PROTOTYPE — el plano maestro del PROTOTIPO: <c>M</c> lo abre en la base (la zona donde
    /// apareces) y en él se pasa a limpio la hoja abierta de la libreta.
    /// </summary>
    /// <remarks>
    /// IMGUI por lo mismo que <see cref="MapNotebookView"/>. Fuera de la base <c>M</c> no abre nada (D15).
    /// Pasar a limpio se hace quieto, como dibujar: W/A/S/D o <c>M</c> lo cancelan y no se gasta nada.
    /// </remarks>
    public sealed class MapAtlasView : MonoBehaviour
    {
        public MapMemorySampler sampler;
        public MapNotebookView notebook;
        [Tooltip("Lo que se desactiva con el plano abierto (movimiento y mirada).")]
        public Behaviour playerControl;

        [Header("Plano")]
        [Tooltip("Píxeles por zona en el tablero de 512.")]
        public int tilePx = 128;
        [Tooltip("Cuánto sale la cruceta de «Ubicarme» en el plano (MAPPING-ROADMAP §7b: 30 s).")]
        public float fixSeconds = 30f;

        [Header("Pasar a limpio")]
        public float cleanSeconds = 5f;
        [Tooltip("Folios en blanco para pasar a limpio.")]
        public int sheetsLeft = 20;
        [Tooltip("Tinta del boli por metro de pared pasada a limpio.")]
        public float cleanCostPerMetre = 0.002f;

        private readonly MapAtlas _atlas = new MapAtlas();
        private readonly MapSheetRaster _raster = new MapSheetRaster();
        private readonly List<MapZone> _placed = new List<MapZone>();
        private readonly List<MapZone> _unplaced = new List<MapZone>();
        private readonly List<int> _storeys = new List<int>();
        private readonly List<long> _scratch = new List<long>();
        private readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();

        private Texture2D _texture;
        private bool _open;
        private bool _dirty;
        private int _storey;
        private float _centreX;
        private float _centreZ;
        private string _status = "";
        private string _toast = "";
        private float _toastUntil;
        private Rect _boardRect;

        private MapSheet _cleaning;
        private float _cleanProgress;
        private int _nextCleanId = 1000;

        public bool IsOpen => _open;
        public MapAtlas Atlas => _atlas;

        private void OnDestroy()
        {
            if (_texture != null) Destroy(_texture);
        }

        private void Update()
        {
            if (sampler != null && sampler.HasSample && !_atlas.HasBase)
            {
                _atlas.SetBase(sampler.Memory.ZoneOfCell(sampler.LastCellX, sampler.LastCellZ, sampler.LastStorey));
                Debug.Log($"MAPATLAS base={_atlas.Base}");
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.mKey.wasPressedThisFrame)
            {
                if (_cleaning != null) CancelClean("Cancelado: no se ha gastado nada.");
                if (_open) Close();
                else TryOpen();
            }

            if (_cleaning != null)
            {
                if (keyboard.wKey.isPressed || keyboard.aKey.isPressed || keyboard.sKey.isPressed || keyboard.dKey.isPressed)
                    CancelClean("Te has movido: pasar a limpio cancelado, no se ha gastado nada.");
                else
                    StepClean(Time.deltaTime);
            }

            if (_open && _dirty) Repaint();
        }

        private bool InBase =>
            sampler != null && sampler.HasSample && _atlas.HasBase &&
            _atlas.Base.Equals(sampler.Memory.ZoneOfCell(sampler.LastCellX, sampler.LastCellZ, sampler.LastStorey));

        private void TryOpen()
        {
            if (!InBase)
            {
                _toast = "Sin plano encima: fuera de la base M no abre nada.";
                _toastUntil = Time.unscaledTime + 3f;
                return;
            }

            if (notebook != null) notebook.CloseIfOpen();
            _open = true;
            if (playerControl != null) playerControl.enabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            CentreOnPlayer();
        }

        private void Close()
        {
            _open = false;
            if (playerControl != null)
            {
                playerControl.enabled = true;
                playerControl.SendMessage("SetLooking", true, SendMessageOptions.DontRequireReceiver);
            }
        }

        private void CentreOnPlayer()
        {
            MapZone here = sampler.Memory.ZoneOfCell(sampler.LastCellX, sampler.LastCellZ, sampler.LastStorey);
            _storey = here.Storey;
            _centreX = here.ChunkX + 0.5f;
            _centreZ = here.ChunkZ + 0.5f;
            _dirty = true;
        }

        private void StartClean()
        {
            MapSheet sheet = notebook != null ? notebook.CurrentSheet : null;
            if (sheet == null || !sheet.HasZone || sheet.EdgeCount == 0)
            {
                _status = "Abre en la libreta (N) una hoja con algo dibujado.";
                return;
            }

            if (sheetsLeft <= 0)
            {
                _status = "No te quedan folios.";
                return;
            }

            if (notebook.Pen.Ink < CleanCost(sheet))
            {
                _status = "No queda tinta para pasarla a limpio.";
                return;
            }

            _cleaning = sheet;
            _cleanProgress = 0f;
            _status = $"Pasando a limpio la hoja {sheet.Id}…";
        }

        private float CleanCost(MapSheet sheet) =>
            (float)(sheet.EdgeCount * sampler.Memory.CellSizeM) * cleanCostPerMetre;

        private void StepClean(float deltaTime)
        {
            _cleanProgress += deltaTime / Mathf.Max(0.1f, cleanSeconds);
            if (_cleanProgress < 1f) return;

            MapSheet source = _cleaning;
            _cleaning = null;
            _watch.Restart();
            MapSheet clean = MapAtlas.CleanCopy(source, _nextCleanId++, sampler.Memory.CellsPerChunk, _scratch);
            bool covers = _atlas.CleanOf(source.Zone) != null;
            _atlas.AddClean(clean);
            _watch.Stop();

            float cost = CleanCost(source);
            notebook.Pen.Ink -= cost;
            sheetsLeft--;
            _dirty = true;
            _status = covers
                ? $"Hoja {source.Id} pasada a limpio: tapa a la anterior, que va al archivo."
                : $"Hoja {source.Id} pasada a limpio.";
            Debug.Log($"MAPATLAS clean sheet={source.Id} zone={source.Zone} edges={clean.EdgeCount} " +
                      $"strokes={clean.Layers[0].Strokes.Count} covers={covers} ink={notebook.Pen.Ink:F3} " +
                      $"sheets_left={sheetsLeft} ms={_watch.Elapsed.TotalMilliseconds:F3}");
        }

        private void CancelClean(string status)
        {
            _cleaning = null;
            _status = status;
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
            _atlas.Place(notebook != null ? notebook.Sheets : null, _placed, _unplaced);
            _raster.DrawAtlas(_atlas, _placed, notebook != null ? notebook.Sheets : null, _storey, _centreX, _centreZ,
                tilePx, ToArgb(notebook != null ? notebook.paperColour : Color.white), sampler.Memory.CellsPerChunk);
            _texture.LoadRawTextureData(_raster.Rgba);
            _texture.Apply(false);
            _watch.Stop();

            _storeys.Clear();
            foreach (MapZone zone in _placed)
                if (!_storeys.Contains(zone.Storey)) _storeys.Add(zone.Storey);
            if (!_storeys.Contains(_storey)) _storeys.Add(_storey);
            _storeys.Sort();

            Debug.Log($"MAPATLAS paint placed={_placed.Count} unplaced={_unplaced.Count} cleans={_atlas.Cleans.Count} " +
                      $"archived={_atlas.Archived.Count} storey={_storey} ms={_watch.Elapsed.TotalMilliseconds:F3}");
        }

        private void OnGUI()
        {
            if (!_open)
            {
                if (Time.unscaledTime < _toastUntil)
                    GUI.Box(new Rect((Screen.width - 380f) * 0.5f, Screen.height - 90f, 380f, 26f), _toast);
                return;
            }

            float boardSize = Mathf.Min(_raster.Size, Screen.height - 190f, Screen.width - 60f);
            float width = boardSize + 28f;
            var panel = new Rect((Screen.width - width) * 0.5f, 20f, width, boardSize + 200f);
            GUI.Box(panel, "Plano de la base");

            float x = panel.x + 14f;
            float y = panel.y + 26f;

            for (int i = 0; i < _storeys.Count; i++)
            {
                string label = (_storeys[i] == _storey ? "▶ " : "") + "Planta " + _storeys[i];
                if (GUI.Button(new Rect(x + i * 86f, y, 82f, 22f), label))
                {
                    _storey = _storeys[i];
                    _dirty = true;
                }
            }

            y += 28f;
            GUI.enabled = _cleaning == null;
            MapSheet current = notebook != null ? notebook.CurrentSheet : null;
            string cleanLabel = current != null ? $"Pasar a limpio hoja {current.Id}" : "Pasar a limpio";
            if (GUI.Button(new Rect(x, y, 190f, 24f), cleanLabel)) StartClean();
            GUI.enabled = true;
            if (GUI.Button(new Rect(x + 196f, y, 110f, 24f), "Centrar en mí")) CentreOnPlayer();
            GUI.Label(new Rect(x + 314f, y + 2f, 200f, 22f), $"Folios {sheetsLeft} · tinta {(notebook != null ? notebook.Pen.Ink * 100f : 0f):F0} %");

            y += 32f;
            _boardRect = new Rect(x, y, boardSize, boardSize);
            if (_texture != null) GUI.DrawTexture(_boardRect, _texture);
            DrawFix(boardSize);
            HandleDrag(boardSize);

            y += boardSize + 6f;
            if (_cleaning != null)
            {
                GUI.Box(new Rect(x, y, boardSize, 14f), GUIContent.none);
                GUI.Box(new Rect(x, y, boardSize * Mathf.Clamp01(_cleanProgress), 14f), GUIContent.none);
                y += 18f;
            }

            GUI.Label(new Rect(x, y, boardSize, 22f), _status);
            GUI.Label(new Rect(x, y + 20f, boardSize, 40f), UnplacedText());
            GUI.Label(new Rect(x, y + 58f, boardSize, 22f),
                "M cierra · arrastra para mover · borde rojo = base · lavado = borrador · negro = limpia");
        }

        private string UnplacedText()
        {
            if (_unplaced.Count == 0) return "Por colocar: nada.";
            var text = new System.Text.StringBuilder("Por colocar (sin flecha que las una): ");
            for (int i = 0; i < _unplaced.Count; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(_unplaced[i]);
            }

            return text.ToString();
        }

        private void HandleDrag(float boardSize)
        {
            Event e = Event.current;
            if (e.type != EventType.MouseDrag || !_boardRect.Contains(e.mousePosition)) return;

            float pixelsPerChunk = tilePx * boardSize / _raster.Size;
            _centreX -= e.delta.x / pixelsPerChunk;
            // La pantalla crece hacia abajo y el tablero hacia arriba (Z): arrastrar hacia abajo baja el contenido.
            _centreZ += e.delta.y / pixelsPerChunk;
            _dirty = true;
            e.Use();
        }

        private void DrawFix(float boardSize)
        {
            if (notebook == null || !notebook.TryGetFix(out MapZone zone, out float localX, out float localZ, out float at))
                return;
            float left = at + fixSeconds - Time.unscaledTime;
            if (left <= 0f || zone.Storey != _storey) return;

            int cellsPerChunk = sampler.Memory.CellsPerChunk;
            float pixelsPerChunk = tilePx * boardSize / _raster.Size;
            float cx = _boardRect.x + boardSize * 0.5f + (zone.ChunkX + localX / cellsPerChunk - _centreX) * pixelsPerChunk;
            float cy = _boardRect.y + boardSize * 0.5f - (zone.ChunkZ + localZ / cellsPerChunk - _centreZ) * pixelsPerChunk;
            if (!_boardRect.Contains(new Vector2(cx, cy))) return;

            const float arm = 14f, gap = 3f, thick = 2f;
            Color previous = GUI.color;
            GUI.color = new Color(0.85f, 0.1f, 0.1f, Mathf.Clamp01(left));
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
