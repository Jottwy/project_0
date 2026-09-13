using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>
    /// P0.1 de MAPPING-PROTOTYPE — minimapa de DEPURACIÓN del recuerdo, en la esquina superior
    /// derecha. No es el mapa del juego (ese es papel): sirve para ver en Play qué recuerda el
    /// jugador, que no atraviesa paredes y que se apaga con la edad.
    /// </summary>
    public sealed class MapMemoryDebugView : MonoBehaviour
    {
        public MapMemorySampler sampler;
        public bool show = true;

        [Tooltip("Lado de la ventana, en celdas de 0,5 m (80 = 40 m).")]
        public int viewCells = 80;
        [Tooltip("Tamaño en pantalla, en píxeles.")]
        public int screenSize = 320;
        public float refreshSeconds = 0.25f;

        private static readonly Color32 Background = new Color32(16, 17, 9, 200);
        private static readonly Color32 ChunkBorder = new Color32(120, 60, 50, 220);
        private static readonly Color32 PlayerColour = new Color32(250, 248, 220, 255);

        private Texture2D _texture;
        private Color32[] _pixels;
        private readonly List<RememberedCell> _cells = new List<RememberedCell>();
        private float _nextRefresh;
        private GUIStyle _label;

        private void OnDestroy()
        {
            if (_texture != null) Destroy(_texture);
        }

        private void Update()
        {
            if (!show || sampler == null || sampler.Memory == null || !sampler.HasSample) return;
            if (Time.time < _nextRefresh) return;
            _nextRefresh = Time.time + refreshSeconds;

            if (_texture == null)
            {
                _texture = new Texture2D(viewCells, viewCells, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
                _pixels = new Color32[viewCells * viewCells];
            }

            Redraw();
        }

        private void Redraw()
        {
            MapMemory memory = sampler.Memory;
            int half = viewCells / 2;
            int minX = sampler.LastCellX - half;
            int minZ = sampler.LastCellZ - half;
            double now = Time.timeAsDouble;

            for (int i = 0; i < _pixels.Length; i++) _pixels[i] = Background;

            int cellsPerChunk = Mathf.RoundToInt((float)(memory.ChunkSizeM / memory.CellSizeM));
            for (int p = 0; p < viewCells; p++)
            {
                if (Mod(minX + p, cellsPerChunk) == 0)
                    for (int q = 0; q < viewCells; q++) _pixels[q * viewCells + p] = ChunkBorder;
                if (Mod(minZ + p, cellsPerChunk) == 0)
                    for (int q = 0; q < viewCells; q++) _pixels[p * viewCells + q] = ChunkBorder;
            }

            MapZone first = memory.ZoneOfCell(minX, minZ, sampler.LastStorey);
            MapZone last = memory.ZoneOfCell(minX + viewCells - 1, minZ + viewCells - 1, sampler.LastStorey);
            for (int chunkZ = first.ChunkZ; chunkZ <= last.ChunkZ; chunkZ++)
            {
                for (int chunkX = first.ChunkX; chunkX <= last.ChunkX; chunkX++)
                {
                    memory.CellsInZone(new MapZone(chunkX, chunkZ, sampler.LastStorey), now, _cells);
                    foreach (RememberedCell cell in _cells)
                    {
                        int px = cell.X - minX;
                        int pz = cell.Z - minZ;
                        if (px < 0 || pz < 0 || px >= viewCells || pz >= viewCells) continue;

                        float fresh = Mathf.Clamp01(1f - (float)(cell.Age / memory.MemorySeconds));
                        _pixels[pz * viewCells + px] = cell.Kind == MapCellKind.Wall
                            ? new Color32(236, 216, 108, (byte)(90 + 165 * fresh))
                            : new Color32(150, 138, 64, (byte)(40 + 120 * fresh));
                    }
                }
            }

            _pixels[half * viewCells + half] = PlayerColour;
            _texture.SetPixels32(_pixels);
            _texture.Apply(false);
        }

        private void OnGUI()
        {
            if (!show || _texture == null || sampler == null) return;

            var rect = new Rect(Screen.width - screenSize - 16, 16, screenSize, screenSize);
            GUI.DrawTexture(rect, _texture);

            if (_label == null)
                _label = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(0.91f, 0.9f, 0.78f) } };

            MapZone zone = sampler.Memory.ZoneOfCell(sampler.LastCellX, sampler.LastCellZ, sampler.LastStorey);
            GUI.Label(new Rect(rect.x, rect.yMax + 4, screenSize, 40),
                $"Recuerdo · {sampler.Memory.SampleCount} muestras · visibles {sampler.LastVisibleCells} · " +
                $"paredes {sampler.LastWallCells}\nzona {zone} · {sampler.LastSampleMs:F2} ms/muestra", _label);
        }

        private static int Mod(int a, int b) => ((a % b) + b) % b;
    }
}
