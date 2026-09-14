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
    /// prueba no es el de STP; la libreta diegética es la pestaña «Notas» del libro (P0.5). La lógica vive en
    /// <see cref="MapNotebook"/>; esta vista sólo pinta y lee teclas.
    ///
    /// Mientras está abierta se desactiva <see cref="playerControl"/> (en la escena de playtest, el
    /// <c>Wg3TestPlayer</c>: al desactivarse suelta el cursor). Pulsar <c>N</c> o W/A/S/D a mitad de un dibujo
    /// lo CANCELA: lo trazado se queda en la hoja y lo que faltaba sigue en el recuerdo.
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

        private readonly MapSheetRaster _raster = new MapSheetRaster();
        private MapNotebook _notebook;
        private Texture2D _texture;
        private bool _open;
        private int _paintedVersion = -1;
        private int _paintedSheet = -1;

        public bool IsOpen => _open;
        public MapNotebook Notebook => _notebook;
        public IReadOnlyList<MapSheet> Sheets => _notebook.Sheets;
        public MapSheet CurrentSheet => _notebook.CurrentSheet;
        public MapPen Pen => _notebook.Pen;

        /// <summary>P0.4 — la última vez que «Ubicarme» te reconoció: zona, celdas locales y <c>Time.unscaledTime</c>.</summary>
        public bool TryGetFix(out MapZone zone, out float localX, out float localZ, out float time)
        {
            zone = _notebook.FixZone;
            localX = _notebook.FixLocalX;
            localZ = _notebook.FixLocalZ;
            time = _notebook.FixTime;
            return _notebook.HasFix;
        }

        /// <summary>P0.4 — el plano la cierra al abrirse; un dibujo a medias se cancela como con N.</summary>
        public void CloseIfOpen()
        {
            _notebook.Cancel("Cancelado: lo trazado se queda.");
            if (_open) Close();
        }

        private void Awake()
        {
            _notebook = new MapNotebook(new MapPen("Boli azul", ToArgb(penColour), penWidthPx, penCostPerMetre, 1f))
            {
                MaxDrawSeconds = maxDrawSeconds,
                RecognitionSeconds = recognitionSeconds,
                SureThreshold = sureThreshold,
                UnsureThreshold = unsureThreshold,
                Log = message => Debug.Log(message),
            };
        }

        private void OnDestroy()
        {
            if (_texture != null) Destroy(_texture);
        }

        private MapMemory Memory => sampler != null ? sampler.Memory : null;

        private MapHere Here => sampler != null
            ? new MapHere(sampler.HasSample, sampler.LastCellX, sampler.LastCellZ, sampler.LastStorey)
            : default;

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.nKey.wasPressedThisFrame)
            {
                _notebook.Cancel("Cancelado: lo trazado se queda.");
                if (_open) Close();
                else if (atlas == null || !atlas.IsOpen) Open();
            }

            if (_notebook.Drawing)
            {
                if (keyboard.wKey.isPressed || keyboard.aKey.isPressed || keyboard.sKey.isPressed || keyboard.dKey.isPressed)
                    _notebook.Cancel("Te has movido: dibujo cancelado, lo trazado se queda.");
                else
                    _notebook.Step(Memory, Time.deltaTime);
            }

            if (_open && _notebook.CurrentIndex >= 0 &&
                (_notebook.Version != _paintedVersion || _notebook.CurrentIndex != _paintedSheet))
                Repaint();
        }

        private void Open()
        {
            _open = true;
            // El Wg3TestPlayer suelta el cursor al desactivarse.
            if (playerControl != null) playerControl.enabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _paintedVersion = -1;
        }

        private void Close()
        {
            _open = false;
            if (playerControl != null)
            {
                playerControl.enabled = true;
                // Wg3TestPlayer.SetLooking es privado y al desactivarse se quedó en «sin mirar»: SendMessage lo
                // alcanza sin tocar código de WG3. Sólo vale para el jugador de prueba.
                playerControl.SendMessage("SetLooking", true, SendMessageOptions.DontRequireReceiver);
            }
        }

        private void Repaint()
        {
            _paintedVersion = _notebook.Version;
            _paintedSheet = _notebook.CurrentIndex;
            if (_texture == null)
            {
                _texture = new Texture2D(_raster.Size, _raster.Size, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }

            _raster.DrawSheet(_notebook.CurrentSheet, ToArgb(paperColour), Memory.CellsPerChunk);
            _texture.LoadRawTextureData(_raster.Rgba);
            _texture.Apply(false);
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
            bool drawing = _notebook.Drawing;

            GUI.enabled = !drawing;
            for (int i = 0; i < _notebook.Sheets.Count; i++)
            {
                string label = (i == _notebook.CurrentIndex ? "▶ " : "") + "Hoja " + _notebook.Sheets[i].Id;
                if (GUI.Button(new Rect(x + i * 78f, y, 74f, 22f), label)) _notebook.Select(i);
            }

            y += 28f;
            if (GUI.Button(new Rect(x, y, 100f, 24f), "Coger hoja")) _notebook.TakeSheet(Memory, Here);
            GUI.enabled = !drawing && _notebook.CurrentIndex >= 0;
            if (GUI.Button(new Rect(x + 106f, y, 90f, 24f), "Dibujar")) StartDrawing();
            GUI.enabled = !drawing;
            if (GUI.Button(new Rect(x + 202f, y, 90f, 24f), "Ubicarme"))
                _notebook.Locate(Memory, Here, Time.timeAsDouble, Time.unscaledTime);
            GUI.enabled = true;

            float ink = Mathf.Clamp01(_notebook.Pen.Ink);
            GUI.Label(new Rect(x + 300f, y + 2f, 45f, 22f), "Tinta");
            GUI.Box(new Rect(x + 342f, y + 6f, 100f, 12f), GUIContent.none);
            GUI.Box(new Rect(x + 342f, y + 6f, 100f * ink, 12f), GUIContent.none);
            GUI.Label(new Rect(x + 448f, y + 2f, 60f, 22f), $"{ink * 100f:F0} %");

            y += 32f;
            if (_notebook.CurrentIndex >= 0 && _texture != null)
            {
                GUI.DrawTexture(new Rect(x, y, sheetSize, sheetSize), _texture);
                DrawCrosshair(x, y, sheetSize);
            }
            else
            {
                GUI.Label(new Rect(x, y, sheetSize, 22f), "Coge una hoja en blanco para empezar.");
            }

            y += sheetSize + 6f;
            GUI.Label(new Rect(x, y, sheetSize, 22f), _notebook.Status);
            string zone = _notebook.CurrentSheet != null ? _notebook.CurrentSheet.Zone.ToString() : "—";
            GUI.Label(new Rect(x, y + 20f, sheetSize, 22f), $"N cierra · W/A/S/D o N a mitad cancelan · zona interna (depuración): {zone}");
            if (_notebook.OfferMapHere && !drawing && GUI.Button(new Rect(x, y + 44f, 220f, 24f), "Coger hoja y mapear aquí"))
            {
                if (_notebook.TakeSheet(Memory, Here)) StartDrawing();
            }
        }

        private void StartDrawing() =>
            _notebook.StartDrawing(Memory, Time.timeAsDouble, sampler.memorySeconds / 3.0);

        /// <summary>
        /// Cruceta de «Ubicarme» sobre la hoja: parpadea al principio para que el ojo la encuentre y se desvanece
        /// en el último segundo. NO es tinta. La hoja tiene Z hacia arriba y la pantalla Y hacia abajo.
        /// </summary>
        private void DrawCrosshair(float sheetX, float sheetY, float sheetSize)
        {
            if (!_notebook.HasFix || _notebook.FixSheetIndex != _notebook.CurrentIndex) return;
            float left = _notebook.FixTime + crosshairSeconds - Time.unscaledTime;
            if (left <= 0f) return;

            float elapsed = crosshairSeconds - left;
            float alpha = Mathf.Clamp01(left);
            if (elapsed < 1f && Mathf.Repeat(elapsed * 4f, 1f) > 0.6f) alpha *= 0.25f;

            int cellsPerChunk = Memory.CellsPerChunk;
            float cx = sheetX + _notebook.FixLocalX / cellsPerChunk * sheetSize;
            float cy = sheetY + (1f - _notebook.FixLocalZ / cellsPerChunk) * sheetSize;
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
