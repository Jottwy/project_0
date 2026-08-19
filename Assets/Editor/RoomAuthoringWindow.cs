#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using BackroomsSurvival.Gameplay.GridWorld;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Room Authoring Tool — genera y edita una sala procedural, y la hornea al pool.
    ///
    /// Fase A: el CASCARÓN. Los parámetros de <see cref="RoomDefinition"/> se editan aquí y la
    /// malla se reconstruye en vivo a cada cambio, en UNA sola malla. Los features editables
    /// (boquetes, columnas, escaleras, rejillas) son fases posteriores y cuelgan del mismo
    /// modelo, así que la ventana crecerá por aquí sin rehacerse.
    ///
    /// El tamaño va en TILES de 5 m (<see cref="GridVisualConstants.TileSize"/>), la misma
    /// unidad en la que piensa el mundo — no en metros sueltos ni en celdas de 2.5 m.
    /// </summary>
    public sealed class RoomAuthoringWindow : EditorWindow
    {
        private const string RoomFolder = "Assets/Resources/Rooms";
        private const string PoolPath = RoomFolder + "/RoomPool.asset";

        /// <summary>Marca el objeto de previsualización para poder reencontrarlo y no confundirlo
        /// con geometría que Joel haya puesto a mano.</summary>
        private const string PreviewName = "Room_Preview";

        // No es readonly: cargar una sala ya guardada REEMPLAZA el modelo entero, no lo parchea
        // campo a campo -- son decenas de campos y arrays, y parchear a mano es justo donde se
        // olvida uno.
        private RoomDefinition _def = new RoomDefinition();
        private GameObject _preview;
        private Mesh _previewMesh;

        private GameObject _sceneRoot;
        private Transform _doorAnchor;

        [MenuItem("Backrooms/Room Authoring Tool")]
        private static void Open() => GetWindow<RoomAuthoringWindow>("Room Authoring");

        private static readonly string[] TabNames = { "Shape", "Features", "Save" };
        private int _tab;
        private Vector2 _scroll;

        private void OnGUI()
        {
            // La barra de acción va ARRIBA y fuera de las pestañas: crear, encuadrar y ver el
            // recuento son cosas que quieres a mano estés en la pestaña que estés.
            DrawToolbar();
            _tab = GUILayout.Toolbar(_tab, TabNames);
            EditorGUILayout.Space();

            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;

                EditorGUI.BeginChangeCheck();
                switch (_tab)
                {
                    case 0: DrawShapeTab(); break;
                    case 1: DrawFeaturesTab(); break;
                    default: DrawSaveTab(); break;
                }
                // Reconstruir DENTRO del change-check: sin esto habría que pulsar un botón tras
                // cada ajuste, y lo que se pidió es ver la geometría moverse mientras se arrastra.
                if (EditorGUI.EndChangeCheck() && _preview != null)
                    Rebuild();
            }
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button(_preview == null ? "Create Room" : "Rebuild",
                        EditorStyles.toolbarButton, GUILayout.Width(90)))
                    CreateOrRebuild();

                using (new EditorGUI.DisabledScope(_preview == null))
                {
                    if (GUILayout.Button("Frame", EditorStyles.toolbarButton, GUILayout.Width(60)))
                    {
                        // Encuadrar desde el botón: crear la sala y no verla es lo que hace
                        // pensar que no ha pasado nada.
                        Selection.activeGameObject = _preview;
                        SceneView.lastActiveSceneView?.FrameSelected();
                    }
                }

                DrawLoadButton();

                GUILayout.FlexibleSpace();
                if (_previewMesh != null)
                    GUILayout.Label($"{_previewMesh.vertexCount} verts · " +
                                    $"{_previewMesh.triangles.Length / 3} tris", EditorStyles.miniLabel);
            }
        }

        /// <summary>
        /// Cargar una sala YA GUARDADA de vuelta al editor. Sin esto, hornear era un callejón
        /// sin salida: la sala quedaba como malla + prefab, pero mover una puerta o ensanchar un
        /// pasillo significaba rehacerla entera desde cero en vez de retocar la que ya existe.
        ///
        /// Solo aparecen las entradas con <see cref="RoomPool.RoomEntry.definition"/> — las
        /// horneadas a mano desde una escena (<c>Bake</c>, no <c>Save Room To Pool</c>) no salen
        /// de un modelo y no hay parámetros que traer de vuelta.
        /// </summary>
        private void DrawLoadButton()
        {
            if (!GUILayout.Button("Load ▾", EditorStyles.toolbarDropDown, GUILayout.Width(60)))
                return;

            var pool = AssetDatabase.LoadAssetAtPath<RoomPool>(PoolPath);
            var menu = new GenericMenu();
            if (pool == null || pool.rooms == null || pool.rooms.Length == 0)
            {
                menu.AddDisabledItem(new GUIContent("(no saved rooms)"));
            }
            else
            {
                foreach (var entry in pool.rooms)
                {
                    if (entry == null) continue;
                    if (entry.definition == null)
                    {
                        menu.AddDisabledItem(new GUIContent($"{entry.id} (hand-built, no parameters)"));
                        continue;
                    }
                    // Captura local: `entry` es la variable de bucle, y el lambda se dispara
                    // mucho después de que el bucle haya terminado.
                    var captured = entry;
                    menu.AddItem(new GUIContent(captured.id), false, () => LoadRoom(captured));
                }
            }
            menu.ShowAsContext();
        }

        /// <summary>
        /// Clona el modelo guardado y lo pone en el editor. CLONA y no asigna la referencia
        /// directa: <c>entry.definition</c> es la MISMA instancia que vive dentro del asset del
        /// pool, y tocar sus arrays desde aquí (añadir una puerta, mover un pilar) ensuciaría el
        /// asset ya guardado sin pasar por un guardado explícito.
        ///
        /// El clon es un viaje de ida y vuelta por JSON: es el modo estándar de Unity para
        /// clonar un árbol de clases `[Serializable]` con arrays anidados (huecos, pilares,
        /// bloques, escaleras, niveles...) sin tener que mantener a mano una copia campo a campo
        /// que se desincroniza en cuanto se añade un feature nuevo.
        /// </summary>
        private void LoadRoom(RoomPool.RoomEntry entry)
        {
            _def = JsonUtility.FromJson<RoomDefinition>(JsonUtility.ToJson(entry.definition));
            CreateOrRebuild();
            Debug.Log($"[RoomAuthoringWindow] Loaded '{entry.id}' for editing.");
        }

        private int _seed = 1;

        private void DrawShapeTab()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Random room", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    _seed = EditorGUILayout.IntField(new GUIContent("Seed"), _seed);
                    if (GUILayout.Button("Roll", GUILayout.Width(50)))
                    {
                        // Semilla nueva, no aleatoriedad suelta: apuntando el número puedes
                        // volver a la sala que te gustó. Sin ella, "una buena" se pierde.
                        _seed = UnityEngine.Random.Range(1, 999999);
                        GenerateRandom();
                    }
                    if (GUILayout.Button("Generate", GUILayout.Width(75)))
                        GenerateRandom();
                }
                EditorGUILayout.LabelField(" ",
                    "Same seed always gives the same room.", EditorStyles.miniLabel);
            }
            EditorGUILayout.Space();

            _def.tilesX = Mathf.Max(1, EditorGUILayout.IntField(
                new GUIContent("Tiles X", "Footprint width in 5 m tiles."), _def.tilesX));
            _def.tilesZ = Mathf.Max(1, EditorGUILayout.IntField(
                new GUIContent("Tiles Z", "Footprint depth in 5 m tiles."), _def.tilesZ));
            EditorGUILayout.LabelField(" ", $"{_def.WidthMeters:0.#} m × {_def.DepthMeters:0.#} m");

            EditorGUILayout.Space();
            _def.heightMeters = EditorGUILayout.FloatField(
                new GUIContent("Height (m)"), _def.heightMeters);
            _def.wallThickness = EditorGUILayout.FloatField(
                new GUIContent("Wall Thickness (m)"), _def.wallThickness);

            _def.ceilingTilt = EditorGUILayout.Slider(
                new GUIContent("Ceiling tilt", "0 = flat. The tilt pivots on the centre, so Height stays the AVERAGE."),
                _def.ceilingTilt, 0f, 40f);
            using (new EditorGUI.DisabledScope(_def.ceilingTilt <= 0.001f))
                _def.ceilingTiltYaw = EditorGUILayout.Slider(
                    new GUIContent("Tilt facing", "Which way the ceiling drops."),
                    _def.ceilingTiltYaw, -180f, 180f);

            EditorGUILayout.Space();
            _def.irregularity = EditorGUILayout.Slider(
                new GUIContent("Irregularity", "0 = perfect geometry. Raise it and the walls go slightly out of square."),
                _def.irregularity, 0f, 1f);
            using (new EditorGUI.DisabledScope(_def.irregularity <= 0.001f))
                using (new EditorGUILayout.HorizontalScope())
                {
                    _def.irregularitySeed = EditorGUILayout.IntField(
                        new GUIContent("  Seed"), _def.irregularitySeed);
                    if (GUILayout.Button("Roll", GUILayout.Width(50)))
                        _def.irregularitySeed = UnityEngine.Random.Range(1, 999999);
                }

            EditorGUILayout.Space();
            _def.planMode = (RoomDefinition.PlanMode)EditorGUILayout.EnumPopup(
                new GUIContent("Plan", "Polygon = round/boxy convex plans. Blocks = bite tiles out for L / T / U."),
                _def.planMode);

            if (_def.planMode == RoomDefinition.PlanMode.Polygon)
            {
                _def.sides = EditorGUILayout.IntSlider(
                    new GUIContent("Sides", "4 = boxy. Raise it to round the plan off."),
                    _def.sides, RoomDefinition.MinSides, RoomDefinition.MaxSides);
                _def.squareness = EditorGUILayout.Slider(
                    new GUIContent("Squareness", "0 = round, 1 = the footprint rectangle."),
                    _def.squareness, 0f, 1f);
            }
            else
            {
                DrawNotches();
            }
        }

        /// <summary>
        /// Muescas: bloques de tiles que se le quitan al footprint. Se miden en TILES y no en
        /// metros porque el modo bloques trabaja sobre la rejilla — pedir 3,7 m de mordisco no
        /// significaría nada.
        /// </summary>
        private void DrawNotches()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Notches ({_def.notches.Length})", EditorStyles.boldLabel);
                if (GUILayout.Button("+ Corner", GUILayout.Width(80)))
                {
                    // Nace mordiendo una esquina: es la muesca que da una L, la forma que
                    // seguramente quieres la primera vez.
                    ArrayUtility.Add(ref _def.notches, new RoomDefinition.Notch
                    {
                        tileX = Mathf.Max(0, _def.tilesX - Mathf.Max(1, _def.tilesX / 2)),
                        tileZ = Mathf.Max(0, _def.tilesZ - Mathf.Max(1, _def.tilesZ / 2)),
                        tilesX = Mathf.Max(1, _def.tilesX / 2),
                        tilesZ = Mathf.Max(1, _def.tilesZ / 2),
                    });
                    RebuildIfLive();
                }
            }

            EditorGUILayout.HelpBox(
                "A notch that would empty the room or split it in two is ignored: the plan falls " +
                "back to the polygon one rather than hand you an unreachable half.",
                MessageType.None);

            for (int i = 0; i < _def.notches.Length; i++)
            {
                var t = _def.notches[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (!ItemHeader(ref _def.notches, i, "n",
                            $"#{i}  {t.tilesX}×{t.tilesZ} tiles at ({t.tileX}, {t.tileZ})")) continue;
                    t.tileX = EditorGUILayout.IntSlider("Tile X", t.tileX, 0, Mathf.Max(0, _def.tilesX - 1));
                    t.tileZ = EditorGUILayout.IntSlider("Tile Z", t.tileZ, 0, Mathf.Max(0, _def.tilesZ - 1));
                    t.tilesX = EditorGUILayout.IntSlider("Width (tiles)", t.tilesX, 1, _def.tilesX);
                    t.tilesZ = EditorGUILayout.IntSlider("Depth (tiles)", t.tilesZ, 1, _def.tilesZ);
                }
            }
        }

        private void GenerateRandom()
        {
            _def.Randomize(_seed);
            _foldouts.Clear();
            CreateOrRebuild();
        }

        private void DrawFeaturesTab()
        {
            DrawHoles();
            EditorGUILayout.Space();
            DrawFloorHoles();
            EditorGUILayout.Space();
            DrawPillarGrids();
            EditorGUILayout.Space();
            DrawPillars();
            EditorGUILayout.Space();
            DrawBlocks();
            EditorGUILayout.Space();
            DrawStairs();
            EditorGUILayout.Space();
            DrawLevels();
        }

        private void DrawFloorHoles()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Floor pits ({_def.floorHoles.Length})", EditorStyles.boldLabel);
                if (GUILayout.Button("+ Pit", GUILayout.Width(70)))
                {
                    ArrayUtility.Add(ref _def.floorHoles, new RoomDefinition.FloorHole());
                    RebuildIfLive();
                }
            }

            DrawGroupPanel(_def.floorHoles, "floor pits",
                new GroupField<RoomDefinition.FloorHole>("Position X", f => f.position.x, (f, v) => f.position.x = v),
                new GroupField<RoomDefinition.FloorHole>("Position Z", f => f.position.y, (f, v) => f.position.y = v),
                new GroupField<RoomDefinition.FloorHole>("Size X (m)", f => f.sizeX, (f, v) => f.sizeX = v),
                new GroupField<RoomDefinition.FloorHole>("Size Z (m)", f => f.sizeZ, (f, v) => f.sizeZ = v),
                new GroupField<RoomDefinition.FloorHole>("Depth (m)", f => f.depth, (f, v) => f.depth = v),
                new GroupField<RoomDefinition.FloorHole>("Yaw", f => f.yawDegrees, (f, v) => f.yawDegrees = v));

            for (int i = 0; i < _def.floorHoles.Length; i++)
            {
                var f = _def.floorHoles[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    string kind = f.bottomless ? "bottomless" : $"{f.depth:0.#} m deep";
                    if (!ItemHeader(ref _def.floorHoles, i, "f",
                            $"#{i}  {f.sizeX:0.#} × {f.sizeZ:0.#} m, {kind}")) continue;
                    f.position = EditorGUILayout.Vector2Field("Position (XZ)", f.position);
                    f.sizeX = EditorGUILayout.FloatField("Size X (m)", f.sizeX);
                    f.sizeZ = EditorGUILayout.FloatField("Size Z (m)", f.sizeZ);
                    f.depth = EditorGUILayout.FloatField(
                        new GUIContent("Depth (m)",
                            "How far down it goes from the room floor. Con Bottomless activo "
                            + "sigue mandando: es hasta donde llegan paredes de verdad, con "
                            + "colision. Mas alla no hay nada, ni suelo ni pared."),
                        f.depth);
                    f.bottomless = EditorGUILayout.Toggle(
                        new GUIContent("Bottomless",
                            "Sin fondo: mas alla de Depth no hay losa ni colision con la que "
                            + "aterrizar. Se sigue cayendo -- a otra sala, a otro nivel, o a nada."),
                        f.bottomless);
                    f.yawDegrees = EditorGUILayout.Slider("Yaw", f.yawDegrees, -180f, 180f);
                }
            }
        }

        private void DrawPillarGrids()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Pillar grids ({_def.pillarGrids.Length})", EditorStyles.boldLabel);
                if (GUILayout.Button("+ Grid", GUILayout.Width(70)))
                {
                    ArrayUtility.Add(ref _def.pillarGrids, new RoomDefinition.PillarGrid());
                    RebuildIfLive();
                }
            }

            for (int i = 0; i < _def.pillarGrids.Length; i++)
            {
                var g = _def.pillarGrids[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (!ItemHeader(ref _def.pillarGrids, i, "g",
                            $"#{i}  {g.countX}×{g.countZ} = {g.countX * g.countZ} pillars")) continue;

                    EditorGUILayout.HelpBox(
                        "Move the centre and all of them move. Change the spacing and they " +
                        "space themselves. Yaw turns the whole grid, not each pillar.",
                        MessageType.None);
                    g.center = EditorGUILayout.Vector2Field("Centre (XZ)", g.center);
                    g.countX = EditorGUILayout.IntSlider("Count X", g.countX, 1, 12);
                    g.countZ = EditorGUILayout.IntSlider("Count Z", g.countZ, 1, 12);
                    g.spacingX = EditorGUILayout.FloatField("Spacing X (m)", g.spacingX);
                    g.spacingZ = EditorGUILayout.FloatField("Spacing Z (m)", g.spacingZ);
                    g.size = EditorGUILayout.FloatField(
                        new GUIContent("Size (m)", "Width across the flats."), g.size);
                    g.sides = EditorGUILayout.IntSlider("Sides", g.sides, 3, 32);
                    g.yawDegrees = EditorGUILayout.Slider("Yaw", g.yawDegrees, -180f, 180f);
                }
            }
        }

        // Estado de plegado por (tipo, índice). Fuera del modelo a propósito: es cómo está mirando
        // Joel la lista, no parte de la sala, y no debe acabar guardado en el prefab.
        private readonly Dictionary<string, bool> _foldouts = new Dictionary<string, bool>();

        private bool Foldout(string key, string label)
        {
            _foldouts.TryGetValue(key, out bool open);
            bool now = EditorGUILayout.Foldout(open, label, true);
            _foldouts[key] = now;
            return now;
        }

        /// <summary>
        /// Cabecera común de un elemento de lista: plegable, flechas para reordenar y ×.
        /// Devuelve true si hay que pintar el cuerpo. Sale a un helper porque las cuatro listas
        /// necesitan exactamente lo mismo y repetirlo cuatro veces es donde una se queda atrás.
        /// </summary>
        private bool ItemHeader<T>(ref T[] array, int i, string kind, string label)
        {
            bool open;
            using (new EditorGUILayout.HorizontalScope())
            {
                open = Foldout($"{kind}{i}", label);
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(i == 0))
                    if (GUILayout.Button("▲", EditorStyles.miniButtonLeft, GUILayout.Width(24)))
                    { Swap(array, i, i - 1); GUIUtility.ExitGUI(); }

                using (new EditorGUI.DisabledScope(i == array.Length - 1))
                    if (GUILayout.Button("▼", EditorStyles.miniButtonMid, GUILayout.Width(24)))
                    { Swap(array, i, i + 1); GUIUtility.ExitGUI(); }

                if (GUILayout.Button("×", EditorStyles.miniButtonRight, GUILayout.Width(24)))
                {
                    ArrayUtility.RemoveAt(ref array, i);
                    _foldouts.Clear(); // los índices se han corrido: el plegado ya no corresponde
                    RebuildIfLive();
                    GUIUtility.ExitGUI();
                }
            }
            return open;
        }

        private static void Swap<T>(T[] a, int i, int j) { (a[i], a[j]) = (a[j], a[i]); }

        /// <summary>Un campo que un panel de grupo sabe leer y escribir de cada elemento.</summary>
        private readonly struct GroupField<T>
        {
            public readonly string label, tooltip;
            public readonly System.Func<T, float> get;
            public readonly System.Action<T, float> set;

            public GroupField(string label, System.Func<T, float> get, System.Action<T, float> set,
                string tooltip = "")
            {
                this.label = label; this.tooltip = tooltip; this.get = get; this.set = set;
            }
        }

        /// <summary>
        /// Con 2 o más elementos del mismo tipo, un panel "All N" antes de la lista. Tocar un
        /// campo ahí no fija el mismo valor en todos: aplica el MISMO DESPLAZAMIENTO a cada
        /// elemento, igual que mover un Transform con varios objetos seleccionados en Unity — el
        /// número que se ve aquí es solo el mando de partida (el valor del primer elemento), lo
        /// que persiste es la diferencia relativa entre ellos. Un elemento que ya se había
        /// tocado a mano se mueve CON el grupo sin perder lo que lo hacía distinto.
        ///
        /// Con 0 o 1 elemento no hay grupo que editar y no se dibuja nada.
        /// </summary>
        private static void DrawGroupPanel<T>(T[] items, string title, params GroupField<T>[] fields)
        {
            if (items == null || items.Length < 2) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField($"All {items.Length} {title}", EditorStyles.boldLabel);
                foreach (var f in fields)
                {
                    float shown = f.get(items[0]);
                    EditorGUI.BeginChangeCheck();
                    float next = EditorGUILayout.FloatField(new GUIContent(f.label, f.tooltip), shown);
                    if (EditorGUI.EndChangeCheck())
                    {
                        float delta = next - shown;
                        foreach (var item in items) f.set(item, f.get(item) + delta);
                    }
                }
            }
        }

        private void DrawBlocks()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Blocks ({_def.blocks.Length})", EditorStyles.boldLabel);
                if (GUILayout.Button("+ Block", GUILayout.Width(70)))
                {
                    ArrayUtility.Add(ref _def.blocks, new RoomDefinition.Block());
                    RebuildIfLive();
                }
            }

            DrawGroupPanel(_def.blocks, "blocks",
                new GroupField<RoomDefinition.Block>("Position X", b => b.position.x, (b, v) => b.position.x = v),
                new GroupField<RoomDefinition.Block>("Position Z", b => b.position.y, (b, v) => b.position.y = v),
                new GroupField<RoomDefinition.Block>("Size X (m)", b => b.sizeX, (b, v) => b.sizeX = v),
                new GroupField<RoomDefinition.Block>("Size Z (m)", b => b.sizeZ, (b, v) => b.sizeZ = v),
                new GroupField<RoomDefinition.Block>("Base Y (m)", b => b.baseY, (b, v) => b.baseY = v),
                new GroupField<RoomDefinition.Block>("Height (m)", b => b.height, (b, v) => b.height = v),
                new GroupField<RoomDefinition.Block>("Yaw", b => b.yawDegrees, (b, v) => b.yawDegrees = v));

            for (int i = 0; i < _def.blocks.Length; i++)
            {
                var b = _def.blocks[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (!ItemHeader(ref _def.blocks, i, "b",
                            $"#{i}  {b.sizeX:0.#} × {b.sizeZ:0.#} × {b.height:0.#} m")) continue;
                    b.position = EditorGUILayout.Vector2Field("Position (XZ)", b.position);
                    b.sizeX = EditorGUILayout.FloatField("Size X (m)", b.sizeX);
                    b.sizeZ = EditorGUILayout.FloatField("Size Z (m)", b.sizeZ);
                    b.baseY = EditorGUILayout.FloatField("Base Y (m)", b.baseY);
                    b.height = EditorGUILayout.FloatField("Height (m)", b.height);
                    b.yawDegrees = EditorGUILayout.Slider("Yaw", b.yawDegrees, -180f, 180f);
                }
            }
        }

        private void DrawStairs()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Stairs ({_def.stairs.Length})", EditorStyles.boldLabel);
                if (GUILayout.Button("+ Stairs", GUILayout.Width(70)))
                {
                    ArrayUtility.Add(ref _def.stairs, new RoomDefinition.Stairs());
                    RebuildIfLive();
                }
            }

            DrawGroupPanel(_def.stairs, "stairs",
                new GroupField<RoomDefinition.Stairs>("Position X", s => s.position.x, (s, v) => s.position.x = v),
                new GroupField<RoomDefinition.Stairs>("Position Z", s => s.position.y, (s, v) => s.position.y = v),
                new GroupField<RoomDefinition.Stairs>("Facing", s => s.yawDegrees, (s, v) => s.yawDegrees = v),
                new GroupField<RoomDefinition.Stairs>("Width (m)", s => s.width, (s, v) => s.width = v),
                new GroupField<RoomDefinition.Stairs>("Steps", s => s.steps, (s, v) => s.steps = Mathf.Clamp(Mathf.RoundToInt(v), 1, 40)),
                new GroupField<RoomDefinition.Stairs>("Rise per step (m)", s => s.rise, (s, v) => s.rise = v),
                new GroupField<RoomDefinition.Stairs>("Run per step (m)", s => s.run, (s, v) => s.run = v));

            for (int i = 0; i < _def.stairs.Length; i++)
            {
                var s = _def.stairs[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (!ItemHeader(ref _def.stairs, i, "s",
                            $"#{i}  {s.steps} steps, rises {s.steps * s.rise:0.#} m")) continue;
                    s.position = EditorGUILayout.Vector2Field("Bottom step (XZ)", s.position);
                    s.yawDegrees = EditorGUILayout.Slider(
                        new GUIContent("Facing", "Direction it climbs towards."),
                        s.yawDegrees, -180f, 180f);
                    s.width = EditorGUILayout.FloatField("Width (m)", s.width);
                    s.steps = EditorGUILayout.IntSlider("Steps", s.steps, 1, 40);
                    s.rise = EditorGUILayout.FloatField(
                        new GUIContent("Rise per step (m)", "0.18 is comfortable to climb."), s.rise);
                    s.run = EditorGUILayout.FloatField("Run per step (m)", s.run);
                    EditorGUILayout.LabelField(" ",
                        $"Total: {s.steps * s.rise:0.##} m up, {s.steps * s.run:0.##} m long",
                        EditorStyles.miniLabel);
                }
            }
        }

        private void DrawLevels()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Levels ({_def.levels.Length})", EditorStyles.boldLabel);
                if (GUILayout.Button("+ Level", GUILayout.Width(70)))
                {
                    ArrayUtility.Add(ref _def.levels, new RoomDefinition.Level());
                    RebuildIfLive();
                }
            }
            EditorGUILayout.HelpBox(
                "Una entreplanta a media altura, del ancho de TODA la sala. Cualquier tramo de "
                + "escalera que llegue a su altura le abre hueco solo -- no hace falta abrirlo "
                + "a mano.", MessageType.None);

            DrawGroupPanel(_def.levels, "levels",
                new GroupField<RoomDefinition.Level>("Height (m)", l => l.height, (l, v) => l.height = v));

            for (int i = 0; i < _def.levels.Length; i++)
            {
                var lvl = _def.levels[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (!ItemHeader(ref _def.levels, i, "lvl", $"#{i}  {lvl.height:0.#} m up")) continue;
                    lvl.height = EditorGUILayout.FloatField(
                        new GUIContent("Height (m)",
                            "Altura del canto superior sobre el suelo de la sala. Se recorta "
                            + "solo para dejar hueco de sobra por debajo y por encima."),
                        lvl.height);
                }
            }
        }

        private void DrawSaveTab()
        {
            using (new EditorGUI.DisabledScope(_preview == null))
                if (GUILayout.Button("Save Room To Pool", GUILayout.Height(28)))
                    SaveGenerated();

            if (_preview == null)
                EditorGUILayout.HelpBox("Create a room first (toolbar, top-left).", MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Saving writes room_N.prefab plus its mesh asset under Resources/Rooms, adds one " +
                "BoxCollider per wall / floor / ceiling / pillar derived from the parameters (NOT " +
                "from the triangles), and registers it in RoomPool.asset.\n\n" +
                "The parameters travel with it, so a saved room can be reopened and kept editing.\n\n" +
                "The pivot is the CENTRE of the footprint: the placer rotates the room " +
                "0/90/180/270°, and a corner pivot would shift it on every turn.",
                MessageType.Info);

            _handBuiltFoldout = EditorGUILayout.Foldout(_handBuiltFoldout, "Hand-built room (advanced)", true);
            if (!_handBuiltFoldout) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.HelpBox(
                    "For rooms assembled by hand out of primitives instead of generated. " +
                    "Collision comes from the BoxColliders you placed yourself.", MessageType.None);
                _sceneRoot = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent("Room Root"), _sceneRoot, typeof(GameObject), true);
                _doorAnchor = (Transform)EditorGUILayout.ObjectField(
                    new GUIContent("Door Anchor", "Forward must point OUT of the room."),
                    _doorAnchor, typeof(Transform), true);
                if (GUILayout.Button("Bake Hand-Built Room"))
                    Bake();
            }
        }

        private bool _handBuiltFoldout;

        private void SaveGenerated()
        {
            if (SaveGeneratedRoom(_def, out string message))
                Debug.Log($"[RoomAuthoringWindow] {message}");
            else
                Debug.LogError($"[RoomAuthoringWindow] {message}");
        }

        /// <summary>
        /// Hornea una sala GENERADA: malla como asset, prefab con sus colliders y entrada en el
        /// pool. Estático y con el modelo por parámetro para poder ejercitarlo sin la ventana.
        ///
        /// La malla se guarda como `.asset` propio y no se deja viviendo dentro del prefab: una
        /// malla generada en memoria no sobrevive a la recarga de dominio, y el prefab se quedaría
        /// apuntando a nada — el objeto invisible de siempre.
        /// </summary>
        internal static bool SaveGeneratedRoom(RoomDefinition def, out string message)
        {
            if (def == null || def.tilesX < 1 || def.tilesZ < 1 || def.heightMeters <= 0f)
            {
                message = "Room parameters are not valid (size and height must be positive).";
                return false;
            }

            BackroomsEditorFolders.EnsureFolder("Assets/Resources");
            BackroomsEditorFolders.EnsureFolder(RoomFolder);

            int index = NextFreeIndex();
            string id = $"room_{index}";
            string prefabPath = $"{RoomFolder}/{id}.prefab";
            string meshPath = $"{RoomFolder}/{id}_mesh.asset";

            // Malla NUEVA, no la de la vista previa: esa se reutiliza y se vacía en cada
            // reconstrucción, así que guardarla dejaría el asset atado a un objeto vivo.
            var mesh = RoomMeshBuilder.Build(def);
            if (RoomMeshBuilder.TriangulationFailed)
            {
                // Guardar aquí produciría un prefab en el que se ve suelo donde la colisión tiene
                // hueco. Mejor no dejar guardar que dejar guardar algo roto.
                Object.DestroyImmediate(mesh);
                message = "Floor triangulation failed for this room, so the mesh and the colliders " +
                          "would disagree (solid-looking floor you fall through). Not saved. " +
                          "Move or shrink the floor pits and try again.";
                return false;
            }
            mesh.name = id;
            AssetDatabase.CreateAsset(mesh, meshPath);

            var root = new GameObject(id);
            try
            {
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                root.AddComponent<MeshRenderer>().sharedMaterials = new[]
                {
                    LoadGridMaterial("GridFloor"),
                    LoadGridMaterial("GridWall"),
                    LoadGridMaterial("GridCeiling"),
                };

                var boxes = RoomColliderBuilder.Build(def);
                var colliders = new GameObject("Colliders");
                colliders.transform.SetParent(root.transform, false);
                for (int i = 0; i < boxes.Count; i++)
                {
                    // Un GameObject por caja: el giro vive en el transform, así que una pared en
                    // diagonal se representa exacta. Con un solo objeto y varios BoxCollider
                    // todos compartirían una única rotación.
                    var go = new GameObject($"Box_{i}");
                    go.transform.SetParent(colliders.transform, false);
                    go.transform.localPosition = boxes[i].center;
                    go.transform.localRotation = Quaternion.Euler(0f, boxes[i].yawDegrees, 0f);
                    go.AddComponent<BoxCollider>().size = boxes[i].size;
                }

                var (doorPos, doorFwd) = DoorAnchorFor(def);
                var anchor = new GameObject("DoorAnchor");
                anchor.transform.SetParent(root.transform, false);
                anchor.transform.localPosition = doorPos;
                anchor.transform.localRotation = Quaternion.LookRotation(doorFwd);

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (prefab == null)
                {
                    message = $"Failed to save prefab at {prefabPath}.";
                    return false;
                }

                var pool = LoadOrCreatePool();
                ArrayUtility.Add(ref pool.rooms, new RoomPool.RoomEntry
                {
                    id = id,
                    prefab = prefab,
                    tilesX = def.tilesX,
                    tilesZ = def.tilesZ,
                    heightMeters = def.heightMeters,
                    doorLocalPosition = doorPos,
                    doorLocalForward = doorFwd,
                    collisionBoxes = boxes.ToArray(),
                    definition = def,
                });
                EditorUtility.SetDirty(pool);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                message = $"Saved {id} ({def.tilesX}×{def.tilesZ} tiles, {def.sides} sides) → " +
                          $"{prefabPath}. {mesh.vertexCount} verts, {boxes.Count} collider box(es), " +
                          $"{pool.rooms.Length} room(s) in the pool.";
                return true;
            }
            finally
            {
                DestroyImmediate(root);
            }
        }

        /// <summary>
        /// De dónde sale la puerta de una sala generada. Se DEDUCE del primer boquete que llegue
        /// al suelo en vez de pedirla aparte: si has puesto una puerta, ya has dicho por dónde se
        /// entra, y volver a pedirlo es una forma de que no cuadren. Sin boquetes a nivel de
        /// suelo cae al centro de la pared 0, que al menos es una posición válida.
        /// </summary>
        private static (Vector3 pos, Vector3 forward) DoorAnchorFor(RoomDefinition def)
        {
            Vector2[] inner = def.InnerContour();
            int n = inner.Length;

            int side = 0;
            float along = 0.5f;
            if (def.holes != null)
                foreach (var hole in def.holes)
                {
                    if (hole == null || hole.baseY > 0.01f) continue;
                    side = ((hole.side % n) + n) % n;
                    along = hole.along;
                    break;
                }

            Vector2 p0 = inner[side], p1 = inner[(side + 1) % n];
            Vector2 p = Vector2.Lerp(p0, p1, along);
            var nrm = new Vector2(p1.y - p0.y, -(p1.x - p0.x)).normalized;
            if (Vector2.Dot(nrm, (p0 + p1) * 0.5f) < 0f) nrm = -nrm;

            return (new Vector3(p.x, 0f, p.y), new Vector3(nrm.x, 0f, nrm.y));
        }

        /// <summary>
        /// Lista de boquetes. Cada uno se describe como se piensa —"en la pared 2, a media
        /// altura, 1,6 × 2,2 m"— y no con x/y/z sueltos, que es lo que deja poner un hueco
        /// flotando fuera del muro sin entender por qué está mal.
        /// </summary>
        private void DrawHoles()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Holes ({_def.holes.Length})", EditorStyles.boldLabel);
                if (GUILayout.Button("+ Door", GUILayout.Width(70)))
                    AddHole(baseY: 0f, w: 1.6f, h: 2.2f);
                if (GUILayout.Button("+ Window", GUILayout.Width(80)))
                    AddHole(baseY: 1.1f, w: 2.0f, h: 1.2f);
            }

            DrawGroupPanel(_def.holes, "holes",
                new GroupField<RoomDefinition.WallHole>("Wall", h => h.side, (h, v) => h.side = Mathf.RoundToInt(v),
                    "Desplaza a que pared apunta cada uno, todos a la vez."),
                new GroupField<RoomDefinition.WallHole>("Along wall", h => h.along, (h, v) => h.along = v),
                new GroupField<RoomDefinition.WallHole>("Height off floor (m)", h => h.baseY, (h, v) => h.baseY = v),
                new GroupField<RoomDefinition.WallHole>("Width (m)", h => h.width, (h, v) => h.width = v),
                new GroupField<RoomDefinition.WallHole>("Height (m)", h => h.height, (h, v) => h.height = v),
                new GroupField<RoomDefinition.WallHole>("Grate bars", h => h.grateBars, (h, v) => h.grateBars = Mathf.Max(0, Mathf.RoundToInt(v))));

            for (int i = 0; i < _def.holes.Length; i++)
            {
                var hole = _def.holes[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    string kind = hole.grateBars > 0 ? "grate" : hole.baseY <= 0.01f ? "door" : "window";
                    string where = hole.spanCorners ? $"from wall {hole.side}" : $"on wall {hole.side}";
                    if (!ItemHeader(ref _def.holes, i, "h", $"#{i}  {kind} {where}")) continue;

                    hole.side = EditorGUILayout.IntSlider(
                        new GUIContent("Wall"), hole.side, 0, Mathf.Max(0, _def.sides - 1));
                    hole.along = EditorGUILayout.Slider(
                        new GUIContent("Along wall", "0 y 1 son las dos esquinas de esa pared."),
                        hole.along, 0f, 1f);
                    hole.baseY = EditorGUILayout.FloatField(
                        new GUIContent("Height off floor (m)", "0 = puerta. Súbelo y es ventana."),
                        hole.baseY);
                    hole.width = EditorGUILayout.FloatField(new GUIContent("Width (m)"), hole.width);
                    hole.height = EditorGUILayout.FloatField(new GUIContent("Height (m)"), hole.height);
                    hole.spanCorners = EditorGUILayout.Toggle(
                        new GUIContent("Turn corners",
                            "Deja que la abertura doble la esquina y salga por la pared de al "
                            + "lado. Con esto el ancho se mide sobre el contorno, no sobre una "
                            + "pared: es la unica forma de pedir una puerta ancha en una planta "
                            + "redonda, donde cada faceta mide poco mas de un metro."),
                        hole.spanCorners);
                    hole.grateBars = EditorGUILayout.IntSlider(
                        new GUIContent("Grate bars", "0 = open hole. Above that it fills with bars."),
                        hole.grateBars, 0, 20);
                }
            }
        }

        private void DrawPillars()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Pillars ({_def.pillars.Length})", EditorStyles.boldLabel);
                if (GUILayout.Button("+ Pillar", GUILayout.Width(70)))
                {
                    ArrayUtility.Add(ref _def.pillars, new RoomDefinition.Pillar());
                    RebuildIfLive();
                }
            }

            DrawGroupPanel(_def.pillars, "pillars",
                new GroupField<RoomDefinition.Pillar>("Position X", p => p.position.x, (p, v) => p.position.x = v),
                new GroupField<RoomDefinition.Pillar>("Position Z", p => p.position.y, (p, v) => p.position.y = v),
                new GroupField<RoomDefinition.Pillar>("Size (m)", p => p.size, (p, v) => p.size = v),
                new GroupField<RoomDefinition.Pillar>("Sides", p => p.sides, (p, v) => p.sides = Mathf.Clamp(Mathf.RoundToInt(v), 3, 32)),
                new GroupField<RoomDefinition.Pillar>("Yaw", p => p.yawDegrees, (p, v) => p.yawDegrees = v));

            for (int i = 0; i < _def.pillars.Length; i++)
            {
                var p = _def.pillars[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (!ItemHeader(ref _def.pillars, i, "p",
                            $"#{i}  {p.size:0.##} m, {p.sides} sides")) continue;

                    p.position = EditorGUILayout.Vector2Field("Position (XZ)", p.position);
                    p.size = EditorGUILayout.FloatField(
                        new GUIContent("Size (m)", "Width across the flats."), p.size);
                    p.sides = EditorGUILayout.IntSlider(
                        new GUIContent("Sides", "4 = square. Raise it to round it off."), p.sides, 3, 32);
                    p.yawDegrees = EditorGUILayout.Slider("Yaw", p.yawDegrees, -180f, 180f);
                }
            }
        }

        /// <summary>Nace en la pared 0 y en el centro: aparece donde se ve, no escondido en una
        /// esquina, que es lo que hace pensar que el botón no ha hecho nada.</summary>
        private void AddHole(float baseY, float w, float h)
        {
            ArrayUtility.Add(ref _def.holes, new RoomDefinition.WallHole
            {
                side = 0, along = 0.5f, baseY = baseY, width = w, height = h,
            });
            RebuildIfLive();
        }

        private void RebuildIfLive()
        {
            if (_preview != null) Rebuild();
        }

        /// <summary>Crea el objeto de previsualización si no existe y (re)genera su malla.</summary>
        private void CreateOrRebuild()
        {
            if (_preview == null)
            {
                _preview = new GameObject(PreviewName);
                Undo.RegisterCreatedObjectUndo(_preview, "Create Room");
                _preview.AddComponent<MeshFilter>();
                var mr = _preview.AddComponent<MeshRenderer>();

                // Los mismos materiales de rejilla que usa el mundo, para que la sala se lea en
                // contexto y no como una caja blanca: la sala tiene que juzgarse contra el
                // aspecto real del nivel.
                mr.sharedMaterials = new[]
                {
                    LoadGridMaterial("GridFloor"),
                    LoadGridMaterial("GridWall"),
                    LoadGridMaterial("GridCeiling"),
                };

                _sceneRoot = _preview;
                Selection.activeGameObject = _preview;
            }
            Rebuild();
        }

        private void Rebuild()
        {
            // El Mesh se REUTILIZA: OnGUI dispara esto en cada pulsación mientras se arrastra un
            // slider, y crear uno nuevo cada vez fuga memoria hasta el próximo GC.
            _previewMesh = RoomMeshBuilder.Build(_def, _previewMesh);
            _preview.GetComponent<MeshFilter>().sharedMesh = _previewMesh;
            SceneView.RepaintAll();
        }

        private static Material LoadGridMaterial(string name) =>
            AssetDatabase.LoadAssetAtPath<Material>($"Assets/Resources/GridMaterials/{name}.mat");


        private void Bake()
        {
            if (BakeRoom(_sceneRoot, _doorAnchor, _def.tilesX, _def.tilesZ, _def.heightMeters, out string message))
                Debug.Log($"[RoomAuthoringWindow] {message}");
            else
                Debug.LogError($"[RoomAuthoringWindow] {message}");
        }

        /// <summary>
        /// The bake itself, independent of the window's fields so it can be driven headlessly
        /// (a menu item, a test) and not only by a human clicking the button. Returns false with
        /// the reason in <paramref name="message"/>; on success the message is the summary line.
        /// </summary>
        internal static bool BakeRoom(GameObject sceneRoot, Transform doorAnchor,
            int tilesX, int tilesZ, float heightMeters, out string message)
        {
            if (!Validate(sceneRoot, doorAnchor, tilesX, tilesZ, heightMeters, out message))
                return false;

            BackroomsEditorFolders.EnsureFolder("Assets/Resources");
            BackroomsEditorFolders.EnsureFolder(RoomFolder);

            int index = NextFreeIndex();
            string id = $"room_{index}";
            string prefabPath = $"{RoomFolder}/{id}.prefab";

            // Capture the anchor BEFORE saving — SaveAsPrefabAssetAndConnect keeps the scene
            // instance's transforms untouched, so values read now stay valid afterwards.
            Vector3 doorLocalPos = sceneRoot.transform.InverseTransformPoint(doorAnchor.position);
            Vector3 doorLocalFwd = sceneRoot.transform.InverseTransformDirection(doorAnchor.forward);

            // Collected BEFORE the prefab save: SaveAsPrefabAssetAndConnect turns the scene
            // objects into a prefab instance, and reading colliders off the instance afterwards
            // would depend on that reconnection having settled.
            var boxes = CollectCollisionBoxes(sceneRoot, out int skipped);

            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(
                sceneRoot, prefabPath, InteractionMode.UserAction, out bool success);
            if (!success || prefab == null)
            {
                message = $"Failed to save prefab at {prefabPath}.";
                return false;
            }

            var pool = LoadOrCreatePool();
            var entry = new RoomPool.RoomEntry
            {
                id = id,
                prefab = prefab,
                tilesX = tilesX,
                tilesZ = tilesZ,
                heightMeters = heightMeters,
                doorLocalPosition = doorLocalPos,
                doorLocalForward = doorLocalFwd,
                collisionBoxes = boxes,
            };
            ArrayUtility.Add(ref pool.rooms, entry);
            EditorUtility.SetDirty(pool);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (boxes.Length == 0)
                Debug.LogWarning($"[RoomAuthoringWindow] {id} has NO collision boxes — it will be " +
                                 "walk-through. Build the room from primitives (each carries its own " +
                                 "BoxCollider) or add BoxColliders by hand, then bake again.");

            message = $"Baked {id} ({tilesX}×{tilesZ} tiles) → {prefabPath}, registered in " +
                      $"{PoolPath} ({pool.rooms.Length} room(s) total). " +
                      $"Collision proxy: {boxes.Length} box(es)" +
                      (skipped > 0 ? $", {skipped} collider(s) skipped — see warnings above." : ".");
            return true;
        }

        /// <summary>
        /// Collision proxy for the room: one axis-aligned box per BoxCollider found under the
        /// root, expressed in root-local metres. Building the room out of primitives/ProBuilder
        /// pieces gives a good decomposition for free — every piece already carries a
        /// BoxCollider — so no decomposition algorithm is needed here.
        ///
        /// DELIBERATELY never touches <c>Collider.bounds</c>. PhysX only refreshes bounds when
        /// something calls Physics.SyncTransforms(), which nothing does in EditMode, so every
        /// freshly-transformed collider reports the unit cube at the origin — a trap that has
        /// already broken tests in this project (docs/DEV-ENVIRONMENT.md, "Trampas de test que
        /// no dan error"). The 8 corners are transformed by hand instead, which reads the
        /// transform and not the physics scene.
        ///
        /// A collider rotated off-axis still yields an axis-aligned box: its true corners are
        /// enclosed, so the proxy is conservative (blocks slightly more than the art). Only
        /// BoxColliders are read — a Mesh/Capsule/Sphere collider has no honest box without
        /// bounds, so it is reported and skipped rather than silently approximated.
        /// </summary>
        private static RoomPool.CollisionBox[] CollectCollisionBoxes(GameObject root, out int skipped)
        {
            skipped = 0;
            var result = new List<RoomPool.CollisionBox>();
            Transform rootT = root.transform;

            foreach (var col in root.GetComponentsInChildren<Collider>(includeInactive: false))
            {
                if (col.isTrigger)
                    continue; // triggers are gameplay volumes, not geometry

                if (!(col is BoxCollider box))
                {
                    skipped++;
                    Debug.LogWarning($"[RoomAuthoringWindow] Skipped {col.GetType().Name} on " +
                                     $"'{col.name}' — only BoxCollider can be baked into the " +
                                     "collision proxy. Replace it with one or more BoxColliders.");
                    continue;
                }

                Vector3 c = box.center, e = box.size * 0.5f;
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        c.x + ((i & 1) == 0 ? -e.x : e.x),
                        c.y + ((i & 2) == 0 ? -e.y : e.y),
                        c.z + ((i & 4) == 0 ? -e.z : e.z));
                    Vector3 local = rootT.InverseTransformPoint(box.transform.TransformPoint(corner));
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                }

                result.Add(new RoomPool.CollisionBox
                {
                    center = (min + max) * 0.5f,
                    size = max - min,
                });
            }

            return result.ToArray();
        }

        private static bool Validate(GameObject sceneRoot, Transform doorAnchor,
            int tilesX, int tilesZ, float heightMeters, out string error)
        {
            if (sceneRoot == null)
            {
                error = "Room Root is required.";
                return false;
            }
            if (!sceneRoot.scene.IsValid())
            {
                error = "Room Root must be a scene instance, not a prefab asset.";
                return false;
            }
            if (doorAnchor == null)
            {
                error = "Door Anchor is required.";
                return false;
            }
            if (doorAnchor != sceneRoot.transform && !doorAnchor.IsChildOf(sceneRoot.transform))
            {
                error = "Door Anchor must be Room Root itself or one of its children.";
                return false;
            }
            if (tilesX < 1 || tilesZ < 1)
            {
                error = "Tiles X/Z must be at least 1.";
                return false;
            }
            if (heightMeters <= 0f)
            {
                error = "Height must be positive.";
                return false;
            }

            // A door whose forward has no dominant horizontal axis cannot be matched against a
            // sealed room's opening — GridChunkBuilder.SideOf returns 0 and the room would never
            // be placed anywhere. Caught here rather than shipping a silently unplaceable entry.
            Vector3 fwd = sceneRoot.transform.InverseTransformDirection(doorAnchor.forward);
            if (Mathf.Abs(fwd.x) < 1e-3f && Mathf.Abs(fwd.z) < 1e-3f)
            {
                error = "Door Anchor forward must point along X or Z (it is vertical or zero), " +
                        "otherwise the room can never be matched to a room opening.";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>Lowest room_N whose prefab does not already exist — re-running after a
        /// manual deletion reuses the gap instead of skipping it.</summary>
        private static int NextFreeIndex()
        {
            int n = 0;
            while (File.Exists($"{RoomFolder}/room_{n}.prefab"))
                n++;
            return n;
        }

        private static RoomPool LoadOrCreatePool()
        {
            var pool = AssetDatabase.LoadAssetAtPath<RoomPool>(PoolPath);
            if (pool != null)
                return pool;

            pool = CreateInstance<RoomPool>();
            AssetDatabase.CreateAsset(pool, PoolPath);
            return pool;
        }
    }
}
#endif
