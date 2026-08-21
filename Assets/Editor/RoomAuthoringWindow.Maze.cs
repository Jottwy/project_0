#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using BackroomsSurvival.Gameplay.GridWorld;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Laberinto DENTRO de una sala: rellena el piso con tabiques (bloques) siguiendo la misma
    /// rejilla de 2,5 m en la que piensa el pasillo, para poder autorar un tramo de laberinto sin
    /// salir de la sala.
    ///
    /// Ojo con lo que NO es: el trazado del mundo lo sigue decidiendo el backend (las gramáticas de
    /// <c>layout_grammars.rs</c>). Esto es mobiliario — tabiques dentro de una sala ya colocada —,
    /// no una forma de trazar el mapa desde el editor. Los tabiques salen como
    /// <see cref="RoomDefinition.Block"/> normales: se editan, se borran y se les abre puerta uno a
    /// uno después, igual que si se hubieran puesto a mano.
    /// </summary>
    public sealed partial class RoomAuthoringWindow
    {
        private int _mazeSeed = 1;

        /// <summary>Ancho de pasillo. Por defecto la celda de 2,5 m del mundo: un laberinto con
        /// pasillos de 5 m no se lee como laberinto, se lee como sala con tabiques sueltos.</summary>
        private float _mazeCell = GridConstants.CellSize;

        /// <summary>Qué fracción de los tabiques interiores se tira DESPUÉS de trazar. 0 = árbol
        /// perfecto, un solo camino entre dos puntos cualesquiera y ni un bucle. Subirlo abre
        /// atajos: un laberinto perfecto se recorre a ciegas pegado a una pared, y eso se nota.</summary>
        private float _mazeBraid = 0.15f;

        /// <summary>Tabiques hasta el techo, o de media altura para poder ver por encima.</summary>
        private bool _mazeFullHeight = true;

        private const float MazeShortWallHeight = 1.4f;

        /// <summary>Cuántas celdas por eje admite el generador. 40×40 celdas de 2,5 m son 100 m,
        /// más que la sala más grande que ADR-084 permite (16 tiles = 80 m); el tope está para que
        /// bajar el ancho de pasillo a mano no dispare el coste a millones de celdas.</summary>
        private const int MazeMaxCells = 40;

        private void DrawStoreyMazeRow(int storey)
        {
            if (!Section($"maze{storey}", "Maze generator", openByDefault: false)) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.HelpBox(
                    "Rellena ESTE piso de tabiques en laberinto, sobre la rejilla del mundo. Salen "
                    + "como bloques normales: luego se mueven, se borran y se les abre puerta uno a "
                    + "uno. Generar REEMPLAZA los bloques de este piso.", MessageType.None);

                using (new EditorGUILayout.HorizontalScope())
                {
                    _mazeSeed = EditorGUILayout.IntField(new GUIContent("Seed",
                        "Misma semilla, mismo laberinto."), _mazeSeed);
                    if (GUILayout.Button("Roll", GUILayout.Width(50)))
                        _mazeSeed = UnityEngine.Random.Range(1, 999999);
                }

                _mazeCell = EditorGUILayout.Slider(new GUIContent("Corridor (m)",
                    "Ancho de pasillo. 2,5 m es la celda del mundo."), _mazeCell, 1.5f, 6f);
                _mazeBraid = EditorGUILayout.Slider(new GUIContent("Shortcuts",
                    "0 = un solo camino entre dos puntos. Subirlo abre bucles."), _mazeBraid, 0f, 0.6f);
                _mazeFullHeight = EditorGUILayout.Toggle(new GUIContent("Full height",
                    "Apagado: tabiques de 1,4 m, se ve por encima."), _mazeFullHeight);

                var (cellsX, cellsZ) = MazeCellCount();
                EditorGUILayout.LabelField("Grid", cellsX > 0 ? $"{cellsX} × {cellsZ} cells" : "—",
                    EditorStyles.miniLabel);

                using (new EditorGUI.DisabledScope(cellsX < 2 || cellsZ < 2))
                    if (GUILayout.Button("Generate Maze"))
                        Defer(() => GenerateMaze(storey));
            }
        }

        private (int, int) MazeCellCount()
        {
            var inner = _def.InnerContour();
            if (inner == null || inner.Length < 3) return (0, 0);
            Vector2 min = inner[0], max = inner[0];
            foreach (var p in inner) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            return (Mathf.Clamp(Mathf.FloorToInt((max.x - min.x) / _mazeCell), 0, MazeMaxCells),
                    Mathf.Clamp(Mathf.FloorToInt((max.y - min.y) / _mazeCell), 0, MazeMaxCells));
        }

        /// <summary>
        /// De la planta de la sala a tabiques: decide qué celdas de la rejilla caen dentro, deja
        /// que <see cref="MazeCarver"/> trace, y convierte los pasos que NO existen en bloques.
        /// </summary>
        private void GenerateMaze(int storey)
        {
            var inner = _def.InnerContour();
            if (inner == null || inner.Length < 3) return;

            Vector2 min = inner[0], max = inner[0];
            foreach (var p in inner) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }

            var (nx, nz) = MazeCellCount();
            if (nx < 2 || nz < 2) return;

            // Centrado en la planta: el resto de dividir el ancho entre celdas enteras se reparte
            // a los dos lados, o el laberinto queda pegado a una pared y con un pasillo doble en la
            // de enfrente.
            float ox = min.x + ((max.x - min.x) - nx * _mazeCell) * 0.5f;
            float oz = min.y + ((max.y - min.y) - nz * _mazeCell) * 0.5f;
            Vector2 Center(int cx, int cz) =>
                new Vector2(ox + (cx + 0.5f) * _mazeCell, oz + (cz + 0.5f) * _mazeCell);

            // Media celda de holgura: un tabique que arranca pegado a la pared de la sala deja un
            // resquicio impasable en el que uno se queda encajado.
            var open = new bool[nx, nz];
            int usable = 0;
            for (int cz = 0; cz < nz; cz++)
                for (int cx = 0; cx < nx; cx++)
                    if (InsideWithClearance(inner, Center(cx, cz), _mazeCell * 0.5f))
                    {
                        open[cx, cz] = true;
                        usable++;
                    }
            if (usable < 4) return;

            // linkX[cx, cz] = paso abierto entre (cx,cz) y (cx+1,cz); linkZ, entre (cx,cz) y
            // (cx,cz+1). Guardar los PASOS y no las paredes es lo que hace trivial el paso final:
            // donde no hay paso, hay tabique.
            var linkX = new bool[nx, nz];
            var linkZ = new bool[nx, nz];
            // Semilla desplazada por piso: la misma semilla en dos pisos daría el MISMO laberinto
            // uno encima de otro, que es lo único que no se quiere de un edificio de varios pisos.
            MazeCarver.Carve(open, linkX, linkZ,
                new System.Random(_mazeSeed + storey * 7919), _mazeBraid);

            float minCeil = _def.MinCeilingOver(inner);
            float floorY = _def.StoreyBaseY(storey, minCeil);
            float ceilY = storey == _def.StoreyCount
                ? MinCeilingOverMaze(inner, ox, oz, nx, nz)
                : _def.StoreyCeilingY(storey, minCeil);
            float height = _mazeFullHeight
                ? Mathf.Max(0.5f, ceilY - floorY)
                : MazeShortWallHeight;

            var blocks = new List<RoomDefinition.Block>();
            // Los bloques de OTROS pisos sobreviven: generar el laberinto de la planta baja no
            // puede llevarse por delante los tabiques puestos a mano arriba.
            foreach (var b in _def.blocks)
                if (b != null && StoreyOf(b.level) != storey) blocks.Add(b);

            float t = Mathf.Max(0.05f, _def.wallThickness);
            var runs = new List<MazeCarver.WallRun>();
            MazeCarver.WallRuns(open, linkX, linkZ, runs);
            foreach (var w in runs)
                blocks.Add(new RoomDefinition.Block
                {
                    position = new Vector2(ox + w.center.x * _mazeCell, oz + w.center.y * _mazeCell),
                    sizeX = w.alongZ ? t : w.length * _mazeCell,
                    sizeZ = w.alongZ ? w.length * _mazeCell : t,
                    baseY = 0f,
                    height = height,
                    level = storey,
                });

            if (blocks.Count > MaxPerGroup)
            {
                EditorUtility.DisplayDialog("Maze too dense",
                    $"Saldrían {blocks.Count} bloques y el tope por grupo es {MaxPerGroup}. "
                    + "Sube el ancho de pasillo o los atajos.", "OK");
                return;
            }

            _def.blocks = blocks.ToArray();
            _strips.Clear();
            _foldouts.Clear();
            RebuildIfLive();
            Debug.Log($"[RoomAuthoringWindow] Maze on storey {storey}: {nx}×{nz} cells, "
                      + $"{blocks.Count} block(s) (seed {_mazeSeed}).");
        }

        /// <summary>El punto más bajo del techo SOBRE EL LABERINTO, no sobre la sala entera: con el
        /// techo inclinado, medir en la esquina más baja de la sala achata todos los tabiques hasta
        /// la altura del rincón peor y deja un palmo de aire bajo el resto del techo.</summary>
        private float MinCeilingOverMaze(Vector2[] inner, float ox, float oz, int nx, int nz)
        {
            float lo = float.MaxValue;
            for (int cz = 0; cz <= nz; cz++)
                for (int cx = 0; cx <= nx; cx++)
                    lo = Mathf.Min(lo, _def.CeilingYAt(
                        new Vector2(ox + cx * _mazeCell, oz + cz * _mazeCell)));
            return lo == float.MaxValue ? _def.MinCeilingOver(inner) : lo;
        }
    }
}
#endif
