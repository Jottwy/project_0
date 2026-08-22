#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.Gameplay.World;

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
    public sealed partial class RoomAuthoringWindow : EditorWindow
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

        /// <summary>De qué sala salió <see cref="_def"/> vía <see cref="LoadRoom"/>. Null = sala sin
        /// guardar todavía. Guardar con esto puesto ACTUALIZA esa entrada del pool en vez de crear
        /// una nueva — sin esto, cargar una sala, tocar una puerta y pulsar Save dejaba la vieja
        /// intacta y creaba una copia.</summary>
        private string _loadedRoomId;

        private GameObject _sceneRoot;
        private Transform _doorAnchor;

        [MenuItem("Backrooms/Room Authoring Tool")]
        private static void Open() => GetWindow<RoomAuthoringWindow>("Room Authoring");

        private static readonly string[] TabNames = { "Shape", "Features", "Save" };
        private int _tab;
        private Vector2 _scroll;

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += TickFlickerPreviews;
        }
        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= TickFlickerPreviews;
            // Los props/luces de la vista previa mueren con la ventana: son HideFlags.DontSave,
            // así que no los recogería nadie y quedarían en la escena sin dueño hasta recargar.
            ClearPropPreviews();
            ClearLightPreviews();
        }

        private void OnGUI()
        {
            // Antes de pintar nada: el atajo se atiende aquí, y la foto del historial se toma
            // cuando empieza la interacción, no cuando ya ha cambiado algo.
            HandleUndoShortcuts();
            TrackUndoInteraction();

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

            ApplyDeferred();
        }

        // Cambios que AÑADEN O QUITAN elementos, aplazados al final del frame.
        //
        // IMGUI recorre la ventana dos veces por frame —una para medir (Layout) y otra para
        // pintar (Repaint)— y exige EL MISMO número de controles en las dos. Añadir, borrar,
        // duplicar o cambiar el recuento a mitad del recorrido deja la segunda pasada con una
        // cuenta distinta de la primera, y Unity lanza ImmediateModeException desde el primer
        // control que ya no cuadra. Aplazando, las dos pasadas del frame ven la misma lista y el
        // cambio entra limpio en el siguiente.
        //
        // El primer intento fue no hacer nada y dejar que el frame terminase con la lista vieja.
        // No vale: la excepción salta en ESE frame, no en el siguiente.
        private readonly List<System.Action> _deferred = new List<System.Action>();

        private void Defer(System.Action change) => _deferred.Add(change);

        private void ApplyDeferred()
        {
            if (_deferred.Count == 0) return;

            // Añadir o quitar un elemento es un paso de historial por sí solo: no viene de un
            // arrastre, así que no hay MouseUp que lo cierre. La foto se toma aquí, JUSTO antes de
            // aplicar, que es el último momento en que la sala aún está como estaba.
            CaptureUndoBaseline();
            foreach (var change in _deferred) change();
            CommitUndo();
            _deferred.Clear();
            _foldouts.Clear(); // los índices se han corrido: el plegado ya no corresponde
            RebuildIfLive();
            Repaint();
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
                DrawEditSelectedButton();
                DrawResetButton();
                DrawPropsToolbar();
                DrawLightsToolbar();

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
        /// Reanuda la edición de la sala seleccionada (su instancia en escena, o su asset de
        /// prefab en el Project) sin tener que buscarla a mano en <see cref="DrawLoadButton"/>.
        /// Clic en la sala, clic aquí -- reusa <see cref="LoadRoom"/> tal cual, así que hereda
        /// su misma limitación con las salas horneadas a mano.
        /// </summary>
        private void DrawEditSelectedButton()
        {
            if (!GUILayout.Button("Edit Selected", EditorStyles.toolbarButton, GUILayout.Width(90)))
                return;

            var source = ResolvePrefabSource(Selection.activeGameObject);
            var pool = AssetDatabase.LoadAssetAtPath<RoomPool>(PoolPath);
            var entry = pool != null && pool.rooms != null
                ? System.Array.Find(pool.rooms, e => e != null && e.prefab == source)
                : null;

            if (entry == null)
            {
                EditorUtility.DisplayDialog("Room Authoring Tool",
                    "La selección no es una sala de este pool (ni su instancia en escena ni su prefab).",
                    "OK");
                return;
            }
            if (entry.definition == null)
            {
                EditorUtility.DisplayDialog("Room Authoring Tool",
                    $"'{entry.id}' se horneó a mano (Bake, no Save Room To Pool) -- no tiene " +
                    "parámetros que traer de vuelta.",
                    "OK");
                return;
            }
            LoadRoom(entry);
        }

        /// <summary>
        /// Vuelve a una sala vacía SIN cerrar y reabrir la ventana. Antes, para empezar de cero
        /// tras cargar una sala había que reabrir: quedaba puesto <see cref="_loadedRoomId"/> y
        /// Save actualizaba la sala anterior en vez de crear una nueva.
        /// </summary>
        private void DrawResetButton()
        {
            if (!GUILayout.Button("Reset", EditorStyles.toolbarButton, GUILayout.Width(55)))
                return;
            if (!ConfirmReplaceRoom("Reset")) return;
            Defer(ResetAll);
        }

        private void ResetAll()
        {
            _def = new RoomDefinition();
            _loadedRoomId = null;
            _seed = 1;
            _lightGenSeed = 1;
            _search = "";
            _validateIssues = null;
            _handBuiltFoldout = false;
            _doorAnchor = null;
            _foldouts.Clear();
            _groupSpacing.Clear();
            _groupSetMode.Clear();
            // Las previews son hijas del modelo viejo: sin limpiarlas quedan props y luces de la
            // sala anterior flotando sobre una sala que ya no los tiene.
            ClearPropPreviews();
            ClearLightPreviews();
            RebuildIfLive();
        }

        /// <summary>
        /// De cualquier cosa seleccionada (un hijo dentro de la sala, la raíz de la instancia, o
        /// el propio asset de prefab en el Project) llega al asset de prefab ORIGEN -- el mismo
        /// objeto que <see cref="RoomPool.RoomEntry.prefab"/> referencia, para poder compararlos
        /// por igualdad de referencia.
        /// </summary>
        private static GameObject ResolvePrefabSource(GameObject go)
        {
            if (go == null) return null;
            if (PrefabUtility.IsPartOfPrefabAsset(go))
                return go.transform.root.gameObject;

            var outermost = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (outermost == null) return null; // ni instancia ni asset de prefab
            return PrefabUtility.GetCorrespondingObjectFromOriginalSource(outermost);
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
            _def.MigrateLegacyPillarGrids();
            _def.MigrateLegacyLightMarkers();
            _loadedRoomId = entry.id;
            CreateOrRebuild();
            Debug.Log($"[RoomAuthoringWindow] Loaded '{entry.id}' for editing.");
        }

        private int _seed = 1;

        /// <summary>
        /// La pestaña de la FORMA, en secciones y ordenada por lo que decide cada cosa: primero
        /// el tamaño, luego la planta —que es lo que decide qué mandos existen debajo—, y al final
        /// los acabados. Antes era una lista plana de doce controles con "Plan" el ÚLTIMO, cuando
        /// es el que manda sobre Sides y Squareness.
        ///
        /// El generador aleatorio baja del todo y nace plegado: reemplaza la sala ENTERA, así que
        /// tenerlo de primero era poner el botón más destructivo donde cae la vista al entrar.
        /// </summary>
        private void DrawShapeTab()
        {
            if (Section("secSize", "Size"))
                using (new EditorGUI.IndentLevelScope())
                    DrawSizeSection();
            EditorGUILayout.Space();

            if (Section("secPlan", $"Plan — {_def.planMode}"))
                using (new EditorGUI.IndentLevelScope())
                    DrawPlanSection();
            EditorGUILayout.Space();

            if (Section("secFinish", "Ceiling & irregularity"))
                using (new EditorGUI.IndentLevelScope())
                    DrawFinishSection();
            EditorGUILayout.Space();

            if (Section("secRandom", "Random room", openByDefault: false))
                using (new EditorGUI.IndentLevelScope())
                    DrawRandomSection();
        }

        private void DrawSizeSection()
        {
            // Ancho y fondo en la MISMA fila, con los metros al lado. Eran dos filas más una
            // tercera que solo decía "25 m × 25 m": tres renglones para un dato que cabe en uno.
            // En planta MANUAL el footprint no se teclea: sigue a la planta. Tecleado, se quedaba
            // en lo que pusiera el campo (10 × 10 con una planta de 20 × 24 m) y el backend
            // reservaba —y el cliente vaciaba— un boquete de 50 × 50 alrededor de la sala.
            bool manual = _def.planMode == RoomDefinition.PlanMode.Manual;
            if (manual) FitFootprintToPlan(_def);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("Footprint",
                    "En TILES de 5 m, la unidad en la que piensa el mundo — no en metros sueltos."
                    + (manual ? "\n\nEn planta Manual se ajusta solo a los puntos dibujados "
                                + "(simétrico respecto al centro, que es el pivote)." : "")));
                using (new EditorGUI.DisabledScope(manual))
                {
                    _def.tilesX = Mathf.Max(1, EditorGUILayout.IntField(_def.tilesX, GUILayout.Width(38)));
                    EditorGUILayout.LabelField("×", GUILayout.Width(12));
                    _def.tilesZ = Mathf.Max(1, EditorGUILayout.IntField(_def.tilesZ, GUILayout.Width(38)));
                }
                EditorGUILayout.LabelField(
                    $"tiles   ·   {_def.WidthMeters:0.#} × {_def.DepthMeters:0.#} m"
                    + (manual ? "   ·   ajustado a la planta" : ""),
                    EditorStyles.miniLabel);
            }

            // Deslizador CON campo, y el tope se estira hasta el valor que ya haya: un slider con
            // tope fijo recortaría en silencio una sala más alta que el rango la primera vez que
            // se repinta, y eso es perder un valor que alguien puso a propósito.
            //
            // El rango de AUTORADO va muy por encima del cap del mundo (12 m) y baja hasta la altura
            // mínima que el modelo respeta de todos modos: probar una nave de 30 m o un tramo de
            // gatera no puede exigir editar el asset a mano. Lo que el cap prohíbe se dice en
            // Validate y al exportar, no recortando el mando — un mando que no llega es
            // indistinguible de un mando roto.
            _def.heightMeters = EditorGUILayout.Slider(
                new GUIContent("Height (m)",
                    "Del suelo al techo. Con el techo inclinado es la MEDIA.\n\n"
                    + $"Por encima de {RoomManifestExporter.BackendHeightCapMeters:0.#} m la sala se "
                    + "sigue autorando y viendo aquí, pero en el mundo la losa de la primera capa "
                    + "que no puede invadir la atraviesa. Válida antes de hornear."),
                _def.heightMeters, RoomDefinition.MinCeilingHeight,
                Mathf.Max(40f, _def.heightMeters));
            _def.wallThickness = EditorGUILayout.Slider(
                new GUIContent("Wall thickness (m)",
                    "Grosor del muro. Es lo que se ve en el corte de una puerta."),
                _def.wallThickness, 0.05f, Mathf.Max(0.6f, _def.wallThickness));
        }

        private void DrawPlanSection()
        {
            _def.planMode = (RoomDefinition.PlanMode)EditorGUILayout.EnumPopup(
                new GUIContent("Plan",
                    "Polygon = plantas convexas, de redondas a rectangulares. "
                    + "Blocks = muerde tiles para hacer una L / T / U. "
                    + "Manual = dibujada a mano con tiradores en la vista de escena."),
                _def.planMode);

            switch (_def.planMode)
            {
                case RoomDefinition.PlanMode.Polygon:
                    _def.sides = EditorGUILayout.IntSlider(
                        new GUIContent("Sides",
                            "4 = rectángulo exacto. Subiéndolo la planta se redondea, y cada lado "
                            + "es una pared más donde poder abrir una puerta."),
                        _def.sides, RoomDefinition.MinSides, RoomDefinition.MaxSides);
                    _def.squareness = EditorGUILayout.Slider(
                        new GUIContent("Squareness", "0 = redonda, 1 = el rectángulo del footprint."),
                        _def.squareness, 0f, 1f);
                    break;
                case RoomDefinition.PlanMode.Blocks:
                    DrawNotches();
                    break;
                default:
                    DrawManualContour();
                    break;
            }

            // Cuántas paredes ha salido de verdad: es el número que conecta esta pestaña con la
            // otra, porque el campo "Wall" de una abertura se mueve entre 0 y este menos uno.
            int walls = _def.InnerContour()?.Length ?? 0;
            EditorGUILayout.LabelField(" ",
                $"{walls} wall(s) — es el rango del campo Wall de una abertura",
                EditorStyles.miniLabel);
        }

        private void DrawFinishSection()
        {
            _def.ceilingTilt = EditorGUILayout.Slider(
                new GUIContent("Ceiling tilt",
                    "0 = plano. La inclinación pivota en el centro, así que Height sigue siendo la MEDIA."),
                _def.ceilingTilt, 0f, 40f);
            // Sangrado y apagado, no escondido: si desapareciera, la fila de debajo subiría justo
            // al bajar el tilt a 0 y lo que estabas mirando saltaría de sitio.
            using (new EditorGUI.IndentLevelScope())
            using (new EditorGUI.DisabledScope(_def.ceilingTilt <= 0.001f))
                _def.ceilingTiltYaw = EditorGUILayout.Slider(
                    new GUIContent("Facing", "Hacia dónde cae el techo."),
                    _def.ceilingTiltYaw, -180f, 180f);

            EditorGUILayout.Space();

            _def.irregularity = EditorGUILayout.Slider(
                new GUIContent("Irregularity",
                    "0 = geometría perfecta. Subiéndolo las paredes se salen un poco de escuadra."),
                _def.irregularity, 0f, 1f);
            using (new EditorGUI.IndentLevelScope())
            using (new EditorGUI.DisabledScope(_def.irregularity <= 0.001f))
            using (new EditorGUILayout.HorizontalScope())
            {
                _def.irregularitySeed = EditorGUILayout.IntField(
                    new GUIContent("Seed", "Misma semilla, mismo desajuste."), _def.irregularitySeed);
                if (GUILayout.Button("Roll", GUILayout.Width(50)))
                    _def.irregularitySeed = UnityEngine.Random.Range(1, 999999);
            }
        }

        private void DrawRandomSection()
        {
            EditorGUILayout.HelpBox(
                "Generar REEMPLAZA la sala entera: planta, aberturas, pisos y todo lo demás. "
                + "Es para empezar, no para retocar.", MessageType.Warning);

            using (new EditorGUILayout.HorizontalScope())
            {
                _seed = EditorGUILayout.IntField(new GUIContent("Seed",
                    "La misma semilla da siempre la misma sala. Apunta el número de una que te "
                    + "guste y podrás volver a ella."), _seed);
                if (GUILayout.Button("Roll", GUILayout.Width(50)))
                {
                    // Semilla nueva, no aleatoriedad suelta: apuntando el número puedes volver a
                    // la sala que te gustó. Sin ella, "una buena" se pierde.
                    _seed = UnityEngine.Random.Range(1, 999999);
                    if (ConfirmReplaceRoom()) GenerateRandom();
                }
                if (GUILayout.Button("Generate", GUILayout.Width(75)) && ConfirmReplaceRoom())
                    GenerateRandom();
            }
        }

        /// <summary>
        /// Pregunta antes de tirar una sala en la que ya hay trabajo. Solo pregunta si de verdad
        /// hay algo que perder — con una sala recién creada el diálogo sería un clic de peaje por
        /// nada, y un aviso que salta siempre se acaba pulsando sin leer.
        /// </summary>
        private bool ConfirmReplaceRoom(string verb = "Generate")
        {
            int features = _def.holes.Length + _def.floorHoles.Length + _def.pillars.Length
                + _def.blocks.Length + _def.stairs.Length
                + _def.markers.Length + _def.lights.Length + _def.levels.Length + _def.notches.Length
                + _def.manualContour.Length;
            if (features == 0) return true;

            return EditorUtility.DisplayDialog(
                "Replace the room?",
                $"Esta sala tiene {features} cosa(s) puestas a mano. {verb} las borra todas.",
                verb, "Cancel");
        }

        /// <summary>
        /// Planta dibujada a mano: un tirador esférico por vértice en la vista de escena
        /// (arrastrable), un cubito rojo encima para borrarlo, y puntos amarillos a media
        /// arista para insertar uno nuevo — todo dibujado en <see cref="OnSceneGUI"/>. Aquí solo
        /// vive lo que no cabe en la vista de escena: el arranque y el conteo.
        /// </summary>
        private void DrawManualContour()
        {
            EditorGUILayout.HelpBox(
                "Arrastra los puntos en la vista de escena. Cubito rojo encima de un punto = "
                + "borrarlo. Punto amarillo a media arista = insertar uno ahí.", MessageType.None);

            EditorGUILayout.LabelField("Points", _def.manualContour.Length.ToString());

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Start from Polygon shape"))
                {
                    _def.planMode = RoomDefinition.PlanMode.Polygon;
                    _def.manualContour = (Vector2[])_def.ProceduralContour().Clone();
                    _def.planMode = RoomDefinition.PlanMode.Manual;
                    RebuildIfLive();
                }
                if (GUILayout.Button("Start from Blocks shape"))
                {
                    _def.planMode = RoomDefinition.PlanMode.Blocks;
                    _def.manualContour = (Vector2[])_def.ProceduralContour().Clone();
                    _def.planMode = RoomDefinition.PlanMode.Manual;
                    RebuildIfLive();
                }
            }
            if (_def.manualContour.Length > 0 && GUILayout.Button("Clear points"))
            {
                _def.manualContour = System.Array.Empty<Vector2>();
                RebuildIfLive();
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

        /// <summary>
        /// La pestaña entera, colgando de los PISOS. La planta baja siempre está y no se borra;
        /// cada entreplanta trae debajo los mismos siete grupos, con lo que de verdad vive en
        /// ella. Subir la altura de un piso se lleva todo lo suyo con él — eso ya lo hacía el
        /// modelo (<see cref="RoomDefinition.StoreyBaseY"/>), lo que faltaba era VERLO.
        /// </summary>
        private void DrawFeaturesTab()
        {
            DrawSearchField();
            DrawGenerateLightsSection();

            int storeys = _def.StoreyCount;
            for (int s = 0; s <= storeys; s++)
            {
                DrawStorey(s);
                EditorGUILayout.Space();
            }

            DrawAddLevel();
        }

        /// <summary>Los siete grupos, en el orden en que se leen: primero por dónde se entra y
        /// por dónde se cae, luego lo que hay puesto, y al final lo que no es geometría.</summary>
        private FeatureGroupBase[] AllGroups() => new FeatureGroupBase[]
        {
            HolesGroup(), PitsGroup(), PillarsGroup(), PlatformsGroup(),
            BlocksGroup(), StairsGroup(), LightsGroup(), MarkersGroup(),
        };

        private void DrawStorey(int storey)
        {
            var groups = AllGroups();

            int shown = 0;
            foreach (var g in groups) shown += g.MatchingOn(this, storey);

            // Buscando, un piso sin ninguna coincidencia se va entero: dejar su cabecera puesta
            // con todo vacío debajo obliga a leer siete "(0)" para descubrir que ahí no hay nada.
            // Sin buscar NO se esconde, ni siquiera vacío — la planta baja tiene que estar
            // siempre, aunque solo sea para tener dónde añadir el primer feature.
            if (Searching && shown == 0) return;

            int onIt = FeaturesOnStorey(storey);
            string count = Searching ? $"{shown} of {onIt} match" : $"{onIt} feature(s)";
            string title = storey == 0
                ? $"Base floor — {count}"
                : $"Floor {storey} — slab {SlabTopOf(storey):0.#} m up, {count}";

            if (!Section($"storey{storey}", title)) return;

            // Mismo truco que en DrawFeatureGroup para el raíl: capturar el bloque DESPUÉS de
            // dibujarlo entero y pintar el borde con su Rect real, porque en IMGUI no se sabe la
            // altura de antemano. Un poco más ancho (5 px) que el de cada grupo (3 px) — es el
            // contenedor de fuera, y así los dos se ven a la vez sin confundirse: el del piso a
            // ras del margen, el de cada grupo un pelín más adentro por la indentación.
            Color storeyAccent = StoreyAccentColor(storey);
            EditorGUILayout.BeginVertical();
            using (new EditorGUI.IndentLevelScope())
            {
                if (storey > 0) DrawStoreySlab(storey);
                DrawStoreyLightsRow(storey);
                DrawStoreyMazeRow(storey);
                foreach (var g in groups) g.Draw(this, storey);
            }
            EditorGUILayout.EndVertical();
            var storeyRect = GUILayoutUtility.GetLastRect();
            if (storeyRect.height > 0f)
                EditorGUI.DrawRect(new Rect(storeyRect.x, storeyRect.y, 5f, storeyRect.height), storeyAccent);
        }

        // El texto del buscador. Fuera del modelo por lo mismo que _foldouts: es cómo está
        // mirando Joel la sala, no parte de la sala.
        private string _search = "";

        private bool Searching => !string.IsNullOrWhiteSpace(_search);

        /// <summary>
        /// Buscador sobre lo que YA se ve: casa contra el nombre del grupo y contra la línea de la
        /// tira, que es donde están el índice, el tipo y las medidas ("pillar", "#2", "door",
        /// "grate", "bottomless", "4 sides"). Sin campo `name` en el modelo — buscar por un nombre
        /// que hay que escribir antes no sirve de nada en una sala recién generada, y las salas ya
        /// horneadas lo tendrían vacío.
        ///
        /// Todos los términos tienen que aparecer, en cualquier orden: "door 2" encuentra la
        /// puerta de la pared 2 sin depender de cómo esté redactada la línea.
        /// </summary>
        private bool MatchesSearch(string groupTitle, string row)
        {
            if (!Searching) return true;
            string hay = ($"{groupTitle} {row}").ToLowerInvariant();
            foreach (string term in _search.ToLowerInvariant().Split(' '))
            {
                if (term.Length == 0) continue;
                if (!hay.Contains(term)) return false;
            }
            return true;
        }

        private void DrawSearchField()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _search = EditorGUILayout.TextField(new GUIContent("Search",
                    "Filtra por tipo, número o medida: \"door\", \"#2\", \"grate\", \"bottomless\".\n"
                    + "Varios términos: tienen que aparecer todos."), _search);
                // Al cambiar el filtro, las vistas se recortan y el elemento seleccionado de cada
                // tira puede quedar apuntando fuera. Se sueltan todas.
                if (EditorGUI.EndChangeCheck()) _strips.Clear();

                using (new EditorGUI.DisabledScope(!Searching))
                    if (GUILayout.Button(new GUIContent("×", "Clear"),
                            EditorStyles.miniButton, GUILayout.Width(26)))
                    {
                        _search = "";
                        _strips.Clear();
                        GUIUtility.keyboardControl = 0; // si no, el campo se queda con el texto viejo
                    }
            }
            EditorGUILayout.Space();
        }

        /// <summary>El canto superior de la losa que hace de suelo de este piso.</summary>
        private float SlabTopOf(int storey) =>
            _def.StoreyBaseY(storey, _def.MinCeilingOver(_def.InnerContour()));

        /// <summary>
        /// La losa del piso: su altura, editable AQUÍ y no en una lista aparte. Es el cambio que
        /// hace legible la jerarquía — se toca el número en la cabecera del piso y se ve moverse
        /// todo lo que cuelga de él.
        /// </summary>
        private void DrawStoreySlab(int storey)
        {
            int idx = LevelIndexOfStorey(storey);
            if (idx < 0) return;
            var lvl = _def.levels[idx];

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                float h = EditorGUILayout.FloatField(
                    new GUIContent("Slab height (m)",
                        "Altura del canto superior sobre el suelo de la sala. Se recorta sola "
                        + "para dejar hueco de sobra por debajo y por encima.\n\n"
                        + "Todo lo que vive en este piso se mueve CON ella."),
                    lvl.height);
                if (EditorGUI.EndChangeCheck())
                {
                    lvl.height = h;
                    // El orden de los pisos es por ALTURA: cruzar otra losa renumera los pisos y
                    // las tiras cacheadas apuntarían a la selección de otro piso.
                    _strips.Clear();
                    RebuildIfLive();
                }

                if (GUILayout.Button(new GUIContent("×", "Remove this level"),
                        EditorStyles.miniButton, GUILayout.Width(26)))
                {
                    var next = _def.levels;
                    ArrayUtility.RemoveAt(ref next, idx);
                    _def.levels = next;
                    _strips.Clear();
                    _foldouts.Clear();
                    RebuildIfLive();
                    GUIUtility.ExitGUI();
                }
            }
        }

        /// <summary>Qué losa del array es la que hace de suelo del piso <paramref name="storey"/>
        /// — el array no está ordenado por altura y el piso sí, así que hay que buscarla.</summary>
        private int LevelIndexOfStorey(int storey)
        {
            if (_def.levels == null) return -1;
            for (int i = 0; i < _def.levels.Length; i++)
                if (_def.levels[i] != null && StoreyRankOf(i) == storey) return i;
            return -1;
        }

        private void DrawAddLevel()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("+ Level",
                        "Una entreplanta del ancho de TODA la sala. Cualquier tramo de escalera "
                        + "que llegue a su altura le abre hueco solo."), GUILayout.Width(80)))
                {
                    var next = _def.levels;
                    ArrayUtility.Add(ref next, new RoomDefinition.Level());
                    _def.levels = next;
                    _strips.Clear();
                    RebuildIfLive();
                    GUIUtility.ExitGUI();
                }

                // Repartir: N losas a alturas iguales entre suelo y techo. Es LA colocación que
                // se acaba haciendo a mano casi siempre, así que un botón.
                using (new EditorGUI.DisabledScope((_def.levels?.Length ?? 0) < 2))
                    if (GUILayout.Button(new GUIContent("Distribute",
                            "Reparte las losas a alturas iguales entre suelo y techo."),
                            GUILayout.Width(80)))
                    {
                        int n = _def.levels.Length;
                        for (int k = 0; k < n; k++)
                            if (_def.levels[k] != null)
                                _def.levels[k].height = _def.heightMeters * (k + 1) / (n + 1);
                        _strips.Clear();
                        RebuildIfLive();
                    }
            }
        }

        // ── Árbol de features por piso ────────────────────────────────────────
        //
        // La pestaña entera cuelga de los PISOS: un desplegable por piso y, dentro, los grupos
        // que viven en él. Antes eran cuatro secciones por TIPO, y para saber qué había en la
        // entreplanta había que abrir las cuatro y leer el campo Floor de cada elemento.
        //
        // Cada grupo es una tira reordenable de Unity (asa de arrastre, +/− nativos) con UNA
        // línea por elemento, y los ajustes del seleccionado debajo. Los foldouts por elemento
        // no caben dentro de un ReorderableList sin dibujar a mano con rects y calcular alturas
        // en los siete tipos, que es donde se rompe en cuanto uno crece un campo.

        /// <summary>
        /// Todo lo que la tira reordenable necesita saber de un tipo de feature para dibujarlo
        /// sin saber qué es un pilar. El array se pasa por get/set y no por `ref` a propósito:
        /// los callbacks del ReorderableList son lambdas, y una lambda no puede capturar un `ref`
        /// — pero sí un par de delegados que apuntan al campo de verdad.
        /// </summary>
        /// <summary>
        /// La cara NO genérica de un grupo. Existe para poder tener los siete en un array y
        /// preguntarles "¿cuántos tuyos hay en este piso?" antes de dibujar la cabecera del piso
        /// — sin esto, con el buscador activo habría que dibujar el piso para descubrir que está
        /// vacío, y en IMGUI eso ya no se puede deshacer.
        /// </summary>
        private abstract class FeatureGroupBase
        {
            public abstract int MatchingOn(RoomAuthoringWindow w, int storey);
            public abstract void Draw(RoomAuthoringWindow w, int storey);
        }

        private sealed class FeatureGroup<T> : FeatureGroupBase where T : class
        {
            public override int MatchingOn(RoomAuthoringWindow w, int storey)
            {
                var arr = get();
                if (arr == null) return 0;
                int c = 0;
                for (int i = 0; i < arr.Length; i++)
                    if (arr[i] != null && w.StoreyOf(level(arr[i])) == storey
                        && w.MatchesSearch(title, row(arr[i], i))) c++;
                return c;
            }

            public override void Draw(RoomAuthoringWindow w, int storey) =>
                w.DrawFeatureGroup(this, storey);

            public string key, title;
            /// <summary>Color de la muesca junto al título — mismo tipo, mismo color, en la tira
            /// Y en la cabecera vacía. Solo organización visual: escanear "dónde están las Lights"
            /// entre siete grupos por piso sin leer cada palabra.</summary>
            public Color accentColor = Color.gray;
            public System.Func<T[]> get;
            public System.Action<T[]> set;
            public System.Func<T, int> level;
            public System.Action<T, int> setLevel;
            /// <summary>La línea de la tira: (elemento, índice REAL en el array).</summary>
            public System.Func<T, int, string> row;
            public System.Func<T> make;
            /// <summary>Los campos que se ven al desplegar un elemento, en orden.</summary>
            public Row<T>[] rows;
            public GroupField<T>[] fields;

            /// <summary>
            /// Reparte estos elementos por la planta actual, tamaño incluido. Es lo que convierte
            /// "quiero 8 pilares" en 8 pilares puestos y no en 8 pilares apilados en el origen,
            /// que es lo que salía pulsando + ocho veces.
            ///
            /// Null = este grupo no tiene recuento. Las rejillas ya traen el suyo (countX×countZ)
            /// y una escalera colocada sola no significa nada: hacia dónde sube es la decisión.
            /// </summary>
            /// <remarks>El segundo parámetro es el PASO pedido a mano, en metros, o 0 para que lo
            /// elija el reparto. Sin él, "quiero los pilares cada 4 m" no se podía decir: había
            /// que subir y bajar el recuento a ver qué separación salía.</remarks>
            public System.Action<T[], float> arrange;
        }

        // Una tira por (piso, grupo). Se cachean porque el ReorderableList guarda AHÍ el elemento
        // seleccionado y el estado del arrastre: recrearlo cada frame lo deselecciona al soltar.
        private readonly Dictionary<string, ReorderableList> _strips
            = new Dictionary<string, ReorderableList>();

        /// <summary>El piso REAL de un `level`, con el mismo recorte que aplica el modelo: más
        /// allá de la última losa, todo cae en la última. Sin esto un elemento con el piso de una
        /// losa ya borrada desaparecería del árbol mientras se sigue construyendo.</summary>
        private int StoreyOf(int level) => Mathf.Clamp(level, 0, _def.StoreyCount);

        private void DrawFeatureGroup<T>(FeatureGroup<T> g, int storey) where T : class
        {
            var arr = g.get();
            if (arr == null) return;

            // La VISTA: índices reales de los elementos de este piso que pasan el buscador. Es lo
            // que la tira ordena y dibuja, así que arrastrar dentro de un piso no puede tocar a
            // los de otro — ver ReorderViewInPlace.
            var view = new List<int>();
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] != null && StoreyOf(g.level(arr[i])) == storey
                    && MatchesSearch(g.title, g.row(arr[i], i)))
                    view.Add(i);

            string stripKey = $"{storey}:{g.key}";

            // Bloque entero capturado en un Rect (sin estilo propio, para no anidar caja dentro
            // de caja con la de la ReorderableList) SOLO para poder pintar el raíl de color a la
            // izquierda de TODO el grupo al final, no solo de la cabecera — y para separar un
            // grupo del siguiente con un hueco de verdad, que antes no había ninguno: siete
            // grupos seguidos sin espacio se leían como un bloque único.
            //
            // Antes esto TAMBIÉN teñía el fondo entero (GUI.backgroundColor) y el título
            // (GUI.contentColor) además de la cebra por fila — demasiado a la vez, se leía como
            // "todo de colores" en vez de organizado. Se queda solo con la muesca junto al título
            // y el raíl: el mínimo que separa un tipo de otro sin saturar.
            EditorGUILayout.BeginVertical();

            // Un grupo vacío se anuncia con su cabecera y nada más: la tira entera con su marco y
            // su "List is Empty" son cuatro líneas para decir que no hay nada, y con siete grupos
            // por piso eso es la mitad de la pantalla en blanco.
            if (view.Count == 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawAccentSwatch(g.accentColor);
                    EditorGUILayout.LabelField($"{g.title} (0)", EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    if (g.arrange != null)
                    {
                        EditorGUILayout.LabelField(CountLabel(g), GUILayout.Width(54));
                        EditorGUI.BeginChangeCheck();
                        int n = EditorGUILayout.IntField(0, GUILayout.Width(40));
                        if (EditorGUI.EndChangeCheck() && n > 0) SetGroupCount(g, storey, view, n);
                    }
                    if (GUILayout.Button(new GUIContent("+", $"Add a {g.title.ToLowerInvariant()}"),
                            EditorStyles.miniButton, GUILayout.Width(24)))
                        AddToStorey(g, storey);
                }
            }
            else
            {
                // El panel de grupo va ANTES de la lista: con los elementos desplegables la lista
                // crece mucho, y debajo el panel quedaba a un scroll de distancia de la cabecera a
                // la que pertenece.
                DrawGroupGlobals(g, storey, arr, view);

                if (!_strips.TryGetValue(stripKey, out var rl))
                {
                    rl = new ReorderableList(view, typeof(T), true, true, true, true);
                    _strips[stripKey] = rl;
                }
                rl.list = view; // la vista se rehace cada frame; la tira tiene que mirar a la nueva

                // Los callbacks se reasignan CADA frame y no una vez al crear la tira: capturan
                // `arr` y `view`, que cambian de instancia en cuanto se añade o se borra algo. Una
                // lambda del frame anterior escribiría en el array de entonces y el cambio se
                // perdería sin dar error.
                rl.drawHeaderCallback = r =>
                {
                    var swatch = new Rect(r.x, r.y + 2f, 10f, r.height - 4f);
                    EditorGUI.DrawRect(swatch, g.accentColor);
                    var label = new Rect(r.x + 14f, r.y, r.width - 14f, r.height);
                    EditorGUI.LabelField(label, $"{g.title} ({view.Count})", EditorStyles.boldLabel);
                };

                // La altura de cada elemento, SUMADA de sus filas. Ver Row<T>: es lo que permite
                // que los ajustes vivan dentro del desplegable en vez de en un panel aparte.
                rl.elementHeightCallback = k =>
                {
                    if (k < 0 || k >= view.Count) return LineStep;
                    float h = LineStep + 2f; // la fila-cabecera con el plegable
                    if (!IsItemOpen(stripKey, view[k])) return h;

                    var it = arr[view[k]];
                    h += LineStep; // el selector de piso, común a todos los tipos
                    foreach (var row in g.rows) h += row.height(it);
                    return h + 4f;
                };

                rl.drawElementCallback = (r, k, active, focus) =>
                {
                    if (k < 0 || k >= view.Count) return;
                    int real = view[k];
                    var item = arr[real];

                    // El sangrado de la sección del piso ya viene aplicado en el rect que llega;
                    // dejarlo puesto lo aplicaría OTRA VEZ a cada control de dentro.
                    int indent = EditorGUI.indentLevel;
                    EditorGUI.indentLevel = 0;

                    const float dupW = 72f;
                    var head = new Rect(r.x, r.y + 1f, r.width, LineH);
                    bool open = IsItemOpen(stripKey, real);
                    bool now = EditorGUI.Foldout(
                        new Rect(head.x, head.y, head.width - dupW - 4f, head.height),
                        open, g.row(item, real), true);
                    if (now != open) _foldouts[ItemKey(stripKey, real)] = now;

                    // Clon por JSON — el mismo viaje que LoadRoom — para no compartir arrays
                    // anidados (los boquetes de un bloque) entre el original y la copia.
                    if (GUI.Button(new Rect(head.xMax - dupW, head.y, dupW, head.height),
                            new GUIContent("Duplicate", "Otro igual, justo debajo"),
                            EditorStyles.miniButton))
                        Defer(() =>
                        {
                            var copy = JsonUtility.FromJson<T>(JsonUtility.ToJson(item));
                            var next = g.get();
                            ArrayUtility.Insert(ref next, real + 1, copy);
                            g.set(next);
                        });

                    if (now)
                    {
                        float y = head.yMax + EditorGUIUtility.standardVerticalSpacing;
                        float x = r.x + 12f, w = r.width - 12f;

                        // Mover de piso es un campo y no un arrastre entre desplegables: el
                        // arrastre de la tira ya significa "ordenar dentro de este piso", y que el
                        // mismo gesto hiciera dos cosas según dónde sueltes es justo lo que no se
                        // adivina.
                        int lvl = FloorField(new Rect(x, y, w, LineH), g.level(item));
                        if (lvl != g.level(item))
                            Defer(() =>
                            {
                                g.setLevel(item, lvl);
                                _strips.Clear(); // se va a otro piso: esta tira ya no lo tiene
                            });
                        y += LineStep;

                        foreach (var row in g.rows)
                        {
                            float hh = row.height(item);
                            row.draw(new Rect(x, y, w, hh), item);
                            y += hh;
                        }
                    }

                    EditorGUI.indentLevel = indent;
                };

                rl.onAddCallback = _ => AddToStorey(g, storey);
                rl.onRemoveCallback = list =>
                {
                    if (list.index < 0 || list.index >= view.Count) return;
                    int victim = view[list.index];
                    list.index = -1;
                    Defer(() =>
                    {
                        var next = g.get();
                        if (victim < next.Length) ArrayUtility.RemoveAt(ref next, victim);
                        g.set(next);
                    });
                };
                rl.onReorderCallbackWithDetails = (list, from, to) =>
                    ReorderViewInPlace(g, (List<int>)list.list);

                rl.DoLayoutList();
            }

            EditorGUILayout.EndVertical();
            var blockRect = GUILayoutUtility.GetLastRect();
            if (blockRect.height > 0f)
                EditorGUI.DrawRect(new Rect(blockRect.x, blockRect.y, 3f, blockRect.height), g.accentColor);
            EditorGUILayout.Space(4f);
        }

        /// <summary>Clave de plegado de UN elemento. Por índice real y no por posición en la
        /// vista: filtrar con el buscador no debe cerrar lo que tenías abierto.</summary>
        private static string ItemKey(string stripKey, int realIndex) => $"{stripKey}#{realIndex}";

        private bool IsItemOpen(string stripKey, int realIndex) =>
            _foldouts.TryGetValue(ItemKey(stripKey, realIndex), out bool open) && open;

        private static GUIContent CountLabel<T>(FeatureGroup<T> g) where T : class =>
            new GUIContent("Count",
                $"Cuántos {g.title.ToLowerInvariant()} quieres en este piso. Al cambiarlo se "
                + "reparten por la planta actual con el tamaño que les cabe.");

        /// <summary>
        /// Los ajustes que valen para TODO el grupo de este piso, en un solo sitio: cuántos hay,
        /// cada cuánto se separan, un botón para volver a repartirlos, y los campos Δ/= que mueven
        /// a todos a la vez.
        ///
        /// Actúa sobre los de ESTE piso y no sobre el array entero: "todas las ventanas a 1,2 m"
        /// es una frase sobre el piso que se está mirando.
        /// </summary>
        private void DrawGroupGlobals<T>(FeatureGroup<T> g, int storey, T[] arr, List<int> view)
            where T : class
        {
            var shown = new T[view.Count];
            for (int k = 0; k < view.Count; k++) shown[k] = arr[view[k]];

            if (g.arrange != null)
            {
                string spacingKey = $"{storey}:{g.key}";
                _groupSpacing.TryGetValue(spacingKey, out float pitch);

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    // Slider y no un número suelto: tantear "cuántos quiero" arrastrando es más
                    // directo, y el campo a la derecha del propio slider (el mismo control lo
                    // dibuja) sigue dejando escribir uno exacto. Fila propia porque un slider
                    // decente necesita más ancho del que cabía compartido con Spacing/Arrange.
                    EditorGUI.BeginChangeCheck();
                    int want = EditorGUILayout.IntSlider(CountLabel(g), view.Count, 0, MaxPerGroup);
                    if (EditorGUI.EndChangeCheck() && want != view.Count)
                        SetGroupCount(g, storey, view, want);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        // El paso a mano: "los pilares cada 4 m" no se podía decir, había que
                        // subir y bajar el recuento a ver qué separación salía. 0 = lo elige el
                        // reparto.
                        EditorGUILayout.LabelField(new GUIContent("Spacing",
                            "Metros entre uno y el siguiente al repartir. 0 = que lo decida el "
                            + "reparto por el sitio que haya."), GUILayout.Width(56));
                        EditorGUI.BeginChangeCheck();
                        float next = EditorGUILayout.FloatField(pitch, GUILayout.Width(44));
                        if (EditorGUI.EndChangeCheck())
                        {
                            _groupSpacing[spacingKey] = Mathf.Max(0f, next);
                            ArrangeGroup(g, storey);
                        }

                        if (GUILayout.Button(new GUIContent("Arrange",
                                "Reparte los que ya hay, sin cambiar cuántos son. Útil después de "
                                + "tocar la planta o el paso."),
                                EditorStyles.miniButton, GUILayout.Width(62)))
                            ArrangeGroup(g, storey);
                    }
                }
            }

            DrawGroupPanel(shown, $"{storey}:{g.key}", g.title.ToLowerInvariant(), g.fields);
        }

        // Paso pedido a mano por (piso, grupo). Fuera del modelo, como _foldouts: es cómo se está
        // repartiendo, no parte de la sala — lo que la sala guarda son las posiciones que salieron.
        private readonly Dictionary<string, float> _groupSpacing = new Dictionary<string, float>();

        /// <summary>Tope de cordura: por encima de esto no se está amueblando, se está llenando
        /// la sala de basura, y cada elemento cuesta geometría al reconstruir en vivo.</summary>
        private const int MaxPerGroup = 200;

        private void SetGroupCount<T>(FeatureGroup<T> g, int storey, List<int> view, int want)
            where T : class
        {
            int target = Mathf.Clamp(want, 0, MaxPerGroup);
            var snapshot = new List<int>(view);
            Defer(() =>
            {
                var arr = g.get();

                // De atrás hacia delante: la vista va en orden ascendente, así que quitando
                // primero el índice más alto los que quedan por quitar siguen siendo válidos.
                for (int k = snapshot.Count - 1; k >= target; k--)
                    if (snapshot[k] < arr.Length) ArrayUtility.RemoveAt(ref arr, snapshot[k]);

                for (int k = snapshot.Count; k < target; k++)
                {
                    var item = g.make();
                    g.setLevel(item, storey);
                    ArrayUtility.Add(ref arr, item);
                }

                g.set(arr);
                _strips.Clear(); // el recuento ha cambiado: la selección de la tira ya no aplica
                ArrangeGroup(g, storey);
            });
        }

        /// <summary>
        /// Reparte los de este piso. Sobre TODOS los del piso y no solo sobre los que pasan el
        /// buscador: repartir media docena dejando fuera a los que el filtro esconde los pondría
        /// encima de los escondidos, y el resultado dependería de lo que hubiera escrito en la
        /// caja de búsqueda, que es justo lo que no debe influir en la geometría.
        /// </summary>
        private void ArrangeGroup<T>(FeatureGroup<T> g, int storey) where T : class
        {
            var arr = g.get();
            if (arr == null) return;
            var items = new List<T>();
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] != null && StoreyOf(g.level(arr[i])) == storey) items.Add(arr[i]);
            if (items.Count == 0) return;

            _groupSpacing.TryGetValue($"{storey}:{g.key}", out float pitch);
            g.arrange(items.ToArray(), pitch);
            RebuildIfLive();
        }

        /// <summary>
        /// <paramref name="n"/> posiciones repartidas por la planta interior, con
        /// <paramref name="clearance"/> metros libres hasta cualquier pared, más el espaciado que
        /// ha salido — que es lo que deja elegir un TAMAÑO que no se toque con el vecino.
        ///
        /// Rejilla y no un reparto por relajación: una sala es rectangular casi siempre, y en una
        /// rejilla se ve a la primera que los pilares están alineados. Las casillas que caen fuera
        /// de la planta (una L, una rotonda) se descartan y se prueba con una rejilla más fina,
        /// porque descartar sin más deja menos elementos de los que se pidieron.
        /// </summary>
        private (List<Vector2> points, float spacing) SpreadInside(int n, float clearance,
            float pitch = 0f)
        {
            var inner = _def.InnerContour();
            var best = new List<Vector2>();
            float bestSpacing = 1f;
            if (inner == null || inner.Length < 3 || n <= 0) return (best, bestSpacing);

            Vector2 min = inner[0], max = inner[0];
            foreach (var p in inner) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            float w = Mathf.Max(0.01f, max.x - min.x), h = Mathf.Max(0.01f, max.y - min.y);

            // Con PASO pedido a mano la rejilla la manda él y no el recuento: se llena la planta a
            // esa separación y se cogen los n primeros. Si no caben n a ese paso, salen los que
            // caben — que es la respuesta honesta a "cada 4 m", no apretarlos a 3,1 sin avisar.
            if (pitch > 0.05f)
            {
                int pc = Mathf.Max(1, Mathf.FloorToInt(w / pitch));
                int pr = Mathf.Max(1, Mathf.FloorToInt(h / pitch));
                var got = new List<Vector2>();
                float ox = min.x + (w - (pc - 1) * pitch) * 0.5f;
                float oz = min.y + (h - (pr - 1) * pitch) * 0.5f;
                for (int r = 0; r < pr && got.Count < n; r++)
                    for (int c = 0; c < pc && got.Count < n; c++)
                    {
                        var p = new Vector2(ox + c * pitch, oz + r * pitch);
                        if (InsideWithClearance(inner, p, clearance)) got.Add(p);
                    }
                if (got.Count > 0) return (got, pitch);
            }

            for (int pass = 0; pass < 8; pass++)
            {
                int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(n * w / h)) + pass);
                int rows = Mathf.Max(1, Mathf.CeilToInt((float)n / cols) + pass);
                float sx = w / cols, sz = h / rows;

                var got = new List<Vector2>();
                for (int r = 0; r < rows && got.Count < n; r++)
                    for (int c = 0; c < cols && got.Count < n; c++)
                    {
                        var p = new Vector2(min.x + (c + 0.5f) * sx, min.y + (r + 0.5f) * sz);
                        if (InsideWithClearance(inner, p, clearance)) got.Add(p);
                    }

                if (got.Count > best.Count) { best = got; bestSpacing = Mathf.Min(sx, sz); }
                if (best.Count >= n) break;
            }

            // Si ni así caben (una planta diminuta con mucho margen), el centroide: mejor todos
            // amontonados en un sitio válido que repartidos por dentro de la pared.
            if (best.Count == 0)
            {
                Vector2 c = Vector2.zero;
                foreach (var p in inner) c += p;
                best.Add(c / inner.Length);
            }
            return (best, bestSpacing);
        }

        private static bool InsideWithClearance(Vector2[] poly, Vector2 p, float clearance)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if (poly[i].y > p.y != poly[j].y > p.y
                    && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y)
                        / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            if (!inside) return false;

            for (int i = 0; i < poly.Length; i++)
                if (DistToSegment(p, poly[i], poly[(i + 1) % poly.Length]) < clearance) return false;
            return true;
        }

        private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-8f) return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            return Vector2.Distance(p, a + ab * t);
        }

        /// <summary><paramref name="n"/> repartos por el PERÍMETRO interior: (lado, fracción).
        /// Para lo que vive en la pared — puertas y ventanas —, donde una rejilla no sirve.
        /// Se reparte sobre la longitud real y no una por lado, para que en una rotonda de 24
        /// facetas no salgan 24 puertas seguidas.</summary>
        private List<(int side, float along)> SpreadOnWalls(int n)
        {
            var result = new List<(int, float)>();
            var inner = _def.InnerContour();
            if (inner == null || inner.Length < 3 || n <= 0) return result;

            int sides = inner.Length;
            var len = new float[sides];
            float perim = 0f;
            for (int i = 0; i < sides; i++)
            {
                len[i] = Vector2.Distance(inner[i], inner[(i + 1) % sides]);
                perim += len[i];
            }
            if (perim < 1e-4f) return result;

            for (int k = 0; k < n; k++)
            {
                float target = perim * (k + 0.5f) / n;
                float run = 0f;
                for (int i = 0; i < sides; i++)
                {
                    if (target > run + len[i] && i < sides - 1) { run += len[i]; continue; }
                    result.Add((i, len[i] > 1e-4f ? Mathf.Clamp01((target - run) / len[i]) : 0.5f));
                    break;
                }
            }
            return result;
        }

        /// <summary>Añade al piso que se está mirando: el elemento nuevo nace CON su piso puesto,
        /// que es lo que hace que aparezca donde se pulsó y no siempre en la planta baja.</summary>
        private void AddToStorey<T>(FeatureGroup<T> g, int storey) where T : class
        {
            Defer(() =>
            {
                var item = g.make();
                g.setLevel(item, storey);
                var next = g.get();
                ArrayUtility.Add(ref next, item);
                g.set(next);
            });
        }

        /// <summary>
        /// Lleva al array real el orden en que la tira ha dejado la VISTA. La tira ya ha permutado
        /// `view` cuando esto corre, así que basta con escribir los elementos en ese orden dentro
        /// de las MISMAS ranuras que ocupaban (las de la vista, ordenadas): así reordenar dentro
        /// de un piso no mueve ni un elemento de otro piso, que es lo que pasaría reordenando el
        /// array entero.
        /// </summary>
        private void ReorderViewInPlace<T>(FeatureGroup<T> g, List<int> view) where T : class
        {
            var arr = g.get();
            var slots = new List<int>(view);
            slots.Sort();
            var items = new T[view.Count];
            for (int k = 0; k < view.Count; k++) items[k] = arr[view[k]];
            for (int k = 0; k < slots.Count; k++) arr[slots[k]] = items[k];
            _foldouts.Clear();
            RebuildIfLive();
        }

        /// <summary>Cabecera de sección: como <see cref="Foldout"/> pero ABIERTA por defecto —
        /// una sección que nace cerrada esconde las listas a quien abre la ventana por primera
        /// vez, que es justo cuando más hace falta verlas.</summary>
        private bool Section(string key, string label, bool openByDefault = true)
        {
            if (!_foldouts.TryGetValue(key, out bool open)) open = openByDefault;
            bool now = EditorGUILayout.Foldout(open, label, true, EditorStyles.foldoutHeader);
            _foldouts[key] = now;
            return now;
        }

        /// <summary>
        /// Color determinista por piso — planta baja, piso 1, piso 2... — para que "todo lo que
        /// cuelga de este piso" se lea de un vistazo, el mismo lenguaje visual que ya tienen los
        /// siete tipos de feature (ver <c>FeatureGroup.accentColor</c>), pero en el otro eje: ESE
        /// es "qué tipo de cosa", este es "en qué piso vive".
        ///
        /// HSV con el matiz rotado por índice, no una paleta fija: una sala con 6 entreplantas no
        /// es descabellada (ver <see cref="RoomDefinition.levels"/>, sin tope propio salvo el que
        /// impone la altura), y una paleta de N colores a mano se quedaría corta o repetiría dos
        /// pisos seguidos con el mismo color justo donde más confunde. La rotación áurea
        /// (0,618...) separa los matices consecutivos lo más posible en vez de repartirlos en
        /// pasos iguales, que con pocos pisos (2-3, el caso común) daría colores casi vecinos.
        /// </summary>
        private static Color StoreyAccentColor(int storey)
        {
            float hue = (storey * 0.618034f) % 1f;
            return Color.HSVToRGB(hue, 0.45f, 0.95f);
        }

        /// <summary>El suelo del piso <paramref name="level"/> con el contorno actual — el mismo
        /// número que usarán la malla y los colliders al reconstruir.</summary>
        private float StoreyBase(int level) =>
            _def.StoreyBaseY(level, _def.MinCeilingOver(_def.InnerContour()));

        /// <summary>
        /// Selector de piso de un elemento. Solo aparece cuando hay entreplantas: sin losas no
        /// hay pisos que elegir y el campo solo estorbaría.
        /// </summary>
        private int FloorField(Rect r, int level)
        {
            int n = _def.StoreyCount;
            if (n == 0)
            {
                EditorGUI.LabelField(r, "Floor", "Base floor (la sala no tiene entreplantas)",
                    EditorStyles.miniLabel);
                return level;
            }

            // Desplegable con el NOMBRE del piso, no un slider de números. Es el control que MUEVE
            // un elemento de piso, y "Base floor" dice lo que hace un 0 sólo si ya sabes que los
            // pisos empiezan en 0 — que es justo lo que no se adivina abriendo la herramienta.
            var names = new string[n + 1];
            names[0] = "Base floor";
            for (int k = 1; k <= n; k++) names[k] = $"Floor {k}  ({SlabTopOf(k):0.#} m up)";

            return EditorGUI.Popup(r,
                new GUIContent("Floor",
                    "En qué piso vive. Sus alturas pasan a medirse desde el suelo de ese piso, "
                    + "así que mover la losa lo mueve con ella."),
                Mathf.Clamp(level, 0, n), NamesToContents(names));
        }

        private static GUIContent[] NamesToContents(string[] names)
        {
            var c = new GUIContent[names.Length];
            for (int i = 0; i < names.Length; i++) c[i] = new GUIContent(names[i]);
            return c;
        }

        /// <summary>La muesca de color de un grupo, dibujada con GUILayout (para la cabecera vacía,
        /// fuera de un ReorderableList — la de dentro se dibuja directo con un Rect, ver
        /// <c>drawHeaderCallback</c>).</summary>
        private static void DrawAccentSwatch(Color color)
        {
            var r = GUILayoutUtility.GetRect(10f, EditorGUIUtility.singleLineHeight, GUILayout.Width(10f));
            EditorGUI.DrawRect(new Rect(r.x, r.y + 2f, 10f, r.height - 4f), color);
        }

        // ── Los siete grupos ──────────────────────────────────────────────────
        //
        // Un descriptor por tipo de feature. Lo que antes era un DrawX() con su cabecera, su
        // botón de añadir y su bucle vive ahora partido en tres: la LÍNEA que se ve en la tira,
        // las FILAS que salen al desplegarla, y cómo se REPARTEN cuando se pide una cantidad.
        // La tira, el plegado, el reordenar, el añadir/borrar, el duplicar y el panel de grupo
        // son los MISMOS para los siete — antes eran siete copias del mismo andamio.

        private FeatureGroup<RoomDefinition.WallHole> HolesGroup() =>
            new FeatureGroup<RoomDefinition.WallHole>
            {
                key = "holes",
                title = "Openings",
                accentColor = new Color(0.4f, 0.75f, 1f),
                get = () => _def.holes,
                set = a => _def.holes = a,
                level = h => h.level,
                setLevel = (h, v) => h.level = v,
                // Nace en la pared 0 y en el centro: aparece donde se ve, no escondido en una
                // esquina, que es lo que hace pensar que el botón no ha hecho nada. Nace PUERTA
                // porque es lo más pedido; subirle "Height off floor" la convierte en ventana.
                make = () => new RoomDefinition.WallHole
                    { side = 0, along = 0.5f, baseY = 0f, width = 1.6f, height = 2.2f },
                row = (h, i) =>
                {
                    string kind = h.grateBars > 0 ? "grate" : h.baseY <= 0.01f ? "door" : "window";
                    string where = h.spanCorners ? $"from wall {h.side}" : $"on wall {h.side}";
                    return $"#{i}   {kind} {where}   ·   {h.width:0.#} × {h.height:0.#} m";
                },
                // El paso no pinta nada aquí: en la pared se reparten por el perímetro.
                arrange = (items, pitch) =>
                {
                    var spots = SpreadOnWalls(items.Length);
                    for (int k = 0; k < items.Length && k < spots.Count; k++)
                    {
                        items[k].side = spots[k].side;
                        items[k].along = spots[k].along;
                    }
                },
                fields = new[]
                {
                    new GroupField<RoomDefinition.WallHole>("Wall", h => h.side,
                        (h, v) => h.side = Mathf.RoundToInt(v),
                        "Desplaza a que pared apunta cada uno, todos a la vez."),
                    new GroupField<RoomDefinition.WallHole>("Along wall", h => h.along, (h, v) => h.along = v),
                    new GroupField<RoomDefinition.WallHole>("Height off floor (m)", h => h.baseY, (h, v) => h.baseY = v),
                    new GroupField<RoomDefinition.WallHole>("Width (m)", h => h.width, (h, v) => h.width = v),
                    new GroupField<RoomDefinition.WallHole>("Height (m)", h => h.height, (h, v) => h.height = v),
                    new GroupField<RoomDefinition.WallHole>("Grate bars", h => h.grateBars,
                        (h, v) => h.grateBars = Mathf.Max(0, Mathf.RoundToInt(v))),
                },
                rows = new[]
                {
                    RowIntSlider<RoomDefinition.WallHole>("Wall", h => h.side, (h, v) => h.side = v,
                        0, Mathf.Max(0, _def.sides - 1)),
                    RowSlider<RoomDefinition.WallHole>("Along wall", h => h.along, (h, v) => h.along = v,
                        0f, 1f, "0 y 1 son las dos esquinas de esa pared."),
                    RowFloat<RoomDefinition.WallHole>("Height off floor (m)", h => h.baseY, (h, v) => h.baseY = v,
                        "0 = puerta. Súbelo y es ventana. Cuenta desde el suelo de SU piso."),
                    RowFloat<RoomDefinition.WallHole>("Width (m)", h => h.width, (h, v) => h.width = v),
                    RowFloat<RoomDefinition.WallHole>("Height (m)", h => h.height, (h, v) => h.height = v),
                    RowToggle<RoomDefinition.WallHole>("Turn corners", h => h.spanCorners,
                        (h, v) => h.spanCorners = v,
                        "Deja que la abertura doble la esquina y salga por la pared de al lado. "
                        + "Con esto el ancho se mide sobre el contorno, no sobre una pared: es la "
                        + "unica forma de pedir una puerta ancha en una planta redonda, donde cada "
                        + "faceta mide poco mas de un metro."),
                    RowIntSlider<RoomDefinition.WallHole>("Grate bars", h => h.grateBars,
                        (h, v) => h.grateBars = v, 0, 20,
                        "0 = hueco limpio. Por encima se llena de barrotes."),
                },
            };

        private FeatureGroup<RoomDefinition.FloorHole> PitsGroup() =>
            new FeatureGroup<RoomDefinition.FloorHole>
            {
                key = "pits",
                title = "Floor pits",
                accentColor = new Color(0.9f, 0.5f, 0.2f),
                get = () => _def.floorHoles,
                set = a => _def.floorHoles = a,
                level = f => f.level,
                setLevel = (f, v) => f.level = v,
                make = () => new RoomDefinition.FloorHole(),
                row = (f, i) =>
                {
                    string shape = f.sides >= 3 ? $"{f.sides}-gon" : "rect";
                    string kind;
                    if (f.level > 0)
                    {
                        var m = f.EffectiveMode();
                        kind = m == RoomDefinition.PitMode.Through ? "through the slab"
                            : m == RoomDefinition.PitMode.Bottomless ? "bottomless" : $"{f.depth:0.#} m deep";
                    }
                    else
                    {
                        kind = f.bottomless ? "bottomless" : $"{f.depth:0.#} m deep";
                    }
                    return $"#{i}   {shape}   {f.sizeX:0.#} × {f.sizeZ:0.#} m   ·   {kind}";
                },
                arrange = (items, pitch) =>
                {
                    // Margen generoso: un pozo pegado a la pared deja un labio de suelo por el
                    // que no se pasa y que no se ve venir.
                    var (pts, spacing) = SpreadInside(items.Length, 2f, pitch);
                    float side = Mathf.Clamp(spacing * 0.5f, 1.5f, 4f);
                    for (int k = 0; k < items.Length && k < pts.Count; k++)
                    {
                        items[k].position = pts[k];
                        items[k].sizeX = side;
                        items[k].sizeZ = side;
                    }
                },
                fields = new[]
                {
                    new GroupField<RoomDefinition.FloorHole>("Position X", f => f.position.x, (f, v) => f.position.x = v),
                    new GroupField<RoomDefinition.FloorHole>("Position Z", f => f.position.y, (f, v) => f.position.y = v),
                    new GroupField<RoomDefinition.FloorHole>("Size X (m)", f => f.sizeX, (f, v) => f.sizeX = v),
                    new GroupField<RoomDefinition.FloorHole>("Size Z (m)", f => f.sizeZ, (f, v) => f.sizeZ = v),
                    new GroupField<RoomDefinition.FloorHole>("Sides", f => f.sides,
                        (f, v) => f.sides = Mathf.Clamp(Mathf.RoundToInt(v), 0, 32)),
                    new GroupField<RoomDefinition.FloorHole>("Depth (m)", f => f.depth, (f, v) => f.depth = v),
                    new GroupField<RoomDefinition.FloorHole>("Yaw", f => f.yawDegrees, (f, v) => f.yawDegrees = v),
                },
                rows = new[]
                {
                    RowFloat<RoomDefinition.FloorHole>("Position X", f => f.position.x, (f, v) => f.position.x = v),
                    RowFloat<RoomDefinition.FloorHole>("Position Z", f => f.position.y, (f, v) => f.position.y = v),
                    RowFloat<RoomDefinition.FloorHole>("Size X (m)", f => f.sizeX, (f, v) => f.sizeX = v),
                    RowFloat<RoomDefinition.FloorHole>("Size Z (m)", f => f.sizeZ, (f, v) => f.sizeZ = v),
                    RowIntSlider<RoomDefinition.FloorHole>("Sides", f => f.sides, (f, v) => f.sides = v, 0, 32,
                        "0 = rectángulo (de siempre, Size X × Size Z). 3+ = un N-ágono, como una "
                        + "columna redondeada pero hueca."),
                    Disabled<RoomDefinition.FloorHole>(f => f.sides < 3,
                        RowSlider<RoomDefinition.FloorHole>("Squareness", f => f.squareness,
                            (f, v) => f.squareness = v, 0f, 1f,
                            "0 = redondo, 1 = el rectángulo Size X × Size Z exacto.")),
                    // Planta baja sigue con el toggle Bottomless de siempre (comportamiento SIN
                    // TOCAR). Un piso alto elige entre los tres modos con el popup de abajo — antes
                    // era SIEMPRE pasante y sin profundidad que elegir. UNA sola fila Depth para
                    // los dos casos: se apaga solo cuando de verdad no pinta nada (Through).
                    Disabled<RoomDefinition.FloorHole>(
                        f => f.level > 0 && f.EffectiveMode() == RoomDefinition.PitMode.Through,
                        RowFloat<RoomDefinition.FloorHole>("Depth (m)", f => f.depth, (f, v) => f.depth = v,
                            "Cuánto cuelga por debajo de la losa de SU piso. Con Bottomless sigue "
                            + "mandando: es hasta donde llegan paredes de verdad, con colisión.\n\n"
                            + "Sin efecto en Through.")),
                    Disabled<RoomDefinition.FloorHole>(f => f.level > 0,
                        RowToggle<RoomDefinition.FloorHole>("Bottomless", f => f.bottomless,
                            (f, v) => f.bottomless = v,
                            "Sin fondo: más allá de Depth no hay losa ni colisión con la que "
                            + "aterrizar. Se sigue cayendo.\n\nSolo en planta baja — un piso alto "
                            + "elige con el campo Mode de abajo.")),
                    Disabled<RoomDefinition.FloorHole>(f => f.level <= 0, PitModeRow()),
                    RowSlider<RoomDefinition.FloorHole>("Yaw", f => f.yawDegrees, (f, v) => f.yawDegrees = v,
                        -180f, 180f),
                },
            };

        private static readonly RoomDefinition.PitMode[] PitModeValues =
        {
            RoomDefinition.PitMode.Through, RoomDefinition.PitMode.Well, RoomDefinition.PitMode.Bottomless,
        };
        private static readonly GUIContent[] PitModeLabels =
            NamesToContents(new[] { "Through", "Well", "Bottomless" });

        /// <summary>Modo de un pozo en un piso alto — ver <see cref="RoomDefinition.PitMode"/>.
        /// Solo tiene sentido con <c>level &gt; 0</c>: en planta baja sigue mandando el toggle
        /// Bottomless de siempre, sin tocar.</summary>
        private Row<RoomDefinition.FloorHole> PitModeRow() =>
            OneLine<RoomDefinition.FloorHole>((r, f) =>
            {
                int idx = System.Array.IndexOf(PitModeValues, f.EffectiveMode());
                idx = EditorGUI.Popup(r, new GUIContent("Mode",
                    "Through: agujero recto, atraviesa la losa de este piso de lado a lado — sin "
                    + "fondo propio, sin paredes propias.\n\n"
                    + "Well: cuelga por debajo de la losa de este piso con paredes hasta Depth y "
                    + "un fondo en el que aterrizar — \"hay suelo, con un agujero\".\n\n"
                    + "Bottomless: igual que Well pero sin fondo — se sigue cayendo."),
                    Mathf.Max(0, idx), PitModeLabels);
                f.mode = PitModeValues[idx];
            });

        private FeatureGroup<RoomDefinition.Pillar> PillarsGroup() =>
            new FeatureGroup<RoomDefinition.Pillar>
            {
                key = "pillars",
                title = "Pillars",
                accentColor = new Color(0.65f, 0.65f, 0.7f),
                get = () => _def.pillars,
                set = a => _def.pillars = a,
                level = p => p.level,
                setLevel = (p, v) => p.level = v,
                make = () => new RoomDefinition.Pillar(),
                row = (p, i) => $"#{i}   {p.size:0.##} m   ·   {p.sides} sides",
                arrange = (items, pitch) =>
                {
                    var (pts, spacing) = SpreadInside(items.Length, 1f, pitch);
                    // El tamaño sale del hueco que ha quedado entre ellos: un cuarto del paso deja
                    // tres cuartos de aire, que es lo que hace que se lea como columnata y no como
                    // tabique. Con topes, porque un pilar de 3 m es una habitacion maciza.
                    float size = Mathf.Clamp(spacing * 0.25f, 0.3f, 1.2f);
                    for (int k = 0; k < items.Length && k < pts.Count; k++)
                    {
                        items[k].position = pts[k];
                        items[k].size = size;
                    }
                },
                fields = new[]
                {
                    new GroupField<RoomDefinition.Pillar>("Position X", p => p.position.x, (p, v) => p.position.x = v),
                    new GroupField<RoomDefinition.Pillar>("Position Z", p => p.position.y, (p, v) => p.position.y = v),
                    new GroupField<RoomDefinition.Pillar>("Size (m)", p => p.size, (p, v) => p.size = v),
                    new GroupField<RoomDefinition.Pillar>("Sides", p => p.sides,
                        (p, v) => p.sides = Mathf.Clamp(Mathf.RoundToInt(v), 3, 32)),
                    new GroupField<RoomDefinition.Pillar>("Yaw", p => p.yawDegrees, (p, v) => p.yawDegrees = v),
                },
                rows = new[]
                {
                    RowFloat<RoomDefinition.Pillar>("Position X", p => p.position.x, (p, v) => p.position.x = v),
                    RowFloat<RoomDefinition.Pillar>("Position Z", p => p.position.y, (p, v) => p.position.y = v),
                    RowFloat<RoomDefinition.Pillar>("Size (m)", p => p.size, (p, v) => p.size = v,
                        "Ancho entre caras."),
                    RowIntSlider<RoomDefinition.Pillar>("Sides", p => p.sides, (p, v) => p.sides = v, 3, 32,
                        "4 = cuadrado. Súbelo para redondearlo."),
                    RowSlider<RoomDefinition.Pillar>("Yaw", p => p.yawDegrees, (p, v) => p.yawDegrees = v,
                        -180f, 180f),
                },
            };

        /// <summary>
        /// Las plataformas: polígonos marcados sobre el suelo con una altura por punto. Los puntos
        /// se marcan ARRASTRÁNDOLOS en la vista de escena (ver <see cref="DrawPlatformHandles"/>),
        /// que es donde se ve lo que se está haciendo; aquí solo va la altura de cada uno, que en
        /// la escena habría que arrastrar en vertical y se pelearía con el arrastre en planta.
        /// </summary>
        private FeatureGroup<RoomDefinition.Platform> PlatformsGroup() =>
            new FeatureGroup<RoomDefinition.Platform>
            {
                key = "platforms",
                title = "Platforms",
                accentColor = new Color(0.55f, 0.7f, 0.5f),
                get = () => _def.platforms,
                set = a => _def.platforms = a,
                level = p => p.level,
                setLevel = (p, v) => p.level = v,
                make = () => new RoomDefinition.Platform(),
                row = (p, i) =>
                {
                    float top = 0f;
                    if (p.points != null) foreach (var pt in p.points) top = Mathf.Max(top, pt.height);
                    return $"#{i}   {p.points?.Length ?? 0} points   ·   up to {top:0.#} m";
                },
                rows = new[]
                {
                    PlatformPointsRow(),
                },
            };

        /// <summary>
        /// La lista de puntos de una plataforma: altura por punto, y los botones de añadir y
        /// quitar.
        ///
        /// La posición en planta NO se edita aquí a propósito. Se arrastra en la escena, que es
        /// donde se ve contra qué pared queda; teclear dos números por punto para una pieza de
        /// ocho es trabajo que la vista de escena ya hace mejor.
        /// </summary>
        private Row<RoomDefinition.Platform> PlatformPointsRow() =>
            new Row<RoomDefinition.Platform>(
                p => LineStep * ((p.points?.Length ?? 0) + 2),
                (rect, plat) =>
                {
                    var pts = plat.points ?? System.Array.Empty<RoomDefinition.PlatformPoint>();
                    float y = rect.y;

                    EditorGUI.LabelField(new Rect(rect.x, y, rect.width, LineH),
                        $"{pts.Length} point(s) — arrástralos en la vista de escena",
                        EditorStyles.miniLabel);
                    y += LineStep;

                    for (int i = 0; i < pts.Length; i++)
                    {
                        var row = new Rect(rect.x, y, rect.width, LineH);
                        float btn = 22f;
                        var fieldRect = new Rect(row.x, row.y, row.width - btn - 4f, row.height);

                        pts[i].height = Mathf.Max(0f, EditorGUI.FloatField(fieldRect,
                            new GUIContent($"#{i} height (m)",
                                $"({pts[i].position.x:0.#}, {pts[i].position.y:0.#}) en planta."),
                            pts[i].height));

                        // Tres puntos es el mínimo para que haya polígono: por debajo no hay pieza,
                        // solo una línea.
                        using (new EditorGUI.DisabledScope(pts.Length <= 3))
                            if (GUI.Button(new Rect(row.xMax - btn, row.y, btn, row.height), "×"))
                            {
                                int at = i;
                                Defer(() =>
                                {
                                    var list = new List<RoomDefinition.PlatformPoint>(plat.points);
                                    list.RemoveAt(at);
                                    plat.points = list.ToArray();
                                });
                            }
                        y += LineStep;
                    }

                    if (GUI.Button(new Rect(rect.x, y, rect.width, LineH), "Add point"))
                        Defer(() =>
                        {
                            var list = new List<RoomDefinition.PlatformPoint>(plat.points);
                            // El nuevo nace a media arista entre el último y el primero, que es
                            // donde cabe sin cruzar el polígono. Ponerlo en el centro lo cruzaría
                            // casi siempre, y un polígono cruzado no se puede recortar.
                            int n = list.Count;
                            var a = list[n - 1];
                            var b = list[0];
                            list.Add(new RoomDefinition.PlatformPoint
                            {
                                position = (a.position + b.position) * 0.5f,
                                height = (a.height + b.height) * 0.5f,
                            });
                            plat.points = list.ToArray();
                        });
                });

        private FeatureGroup<RoomDefinition.Block> BlocksGroup() =>
            new FeatureGroup<RoomDefinition.Block>
            {
                key = "blocks",
                title = "Blocks",
                accentColor = new Color(0.75f, 0.55f, 0.35f),
                get = () => _def.blocks,
                set = a => _def.blocks = a,
                level = b => b.level,
                setLevel = (b, v) => b.level = v,
                make = () => new RoomDefinition.Block(),
                row = (b, i) => b.holes != null && b.holes.Length > 0
                    ? $"#{i}   {b.sizeX:0.#} × {b.sizeZ:0.#} × {b.height:0.#} m   ·   {b.holes.Length} opening(s)"
                    : $"#{i}   {b.sizeX:0.#} × {b.sizeZ:0.#} × {b.height:0.#} m",
                arrange = (items, pitch) =>
                {
                    var (pts, spacing) = SpreadInside(items.Length, 1.5f, pitch);
                    float side = Mathf.Clamp(spacing * 0.45f, 0.8f, 4f);
                    for (int k = 0; k < items.Length && k < pts.Count; k++)
                    {
                        items[k].position = pts[k];
                        items[k].sizeX = side;
                        items[k].sizeZ = side;
                    }
                },
                fields = new[]
                {
                    new GroupField<RoomDefinition.Block>("Position X", b => b.position.x, (b, v) => b.position.x = v),
                    new GroupField<RoomDefinition.Block>("Position Z", b => b.position.y, (b, v) => b.position.y = v),
                    new GroupField<RoomDefinition.Block>("Size X (m)", b => b.sizeX, (b, v) => b.sizeX = v),
                    new GroupField<RoomDefinition.Block>("Size Z (m)", b => b.sizeZ, (b, v) => b.sizeZ = v),
                    new GroupField<RoomDefinition.Block>("Base Y (m)", b => b.baseY, (b, v) => b.baseY = v),
                    new GroupField<RoomDefinition.Block>("Height (m)", b => b.height, (b, v) => b.height = v),
                    new GroupField<RoomDefinition.Block>("Yaw", b => b.yawDegrees, (b, v) => b.yawDegrees = v),
                },
                rows = new[]
                {
                    RowFloat<RoomDefinition.Block>("Position X", b => b.position.x, (b, v) => b.position.x = v),
                    RowFloat<RoomDefinition.Block>("Position Z", b => b.position.y, (b, v) => b.position.y = v),
                    RowFloat<RoomDefinition.Block>("Size X (m)", b => b.sizeX, (b, v) => b.sizeX = v),
                    RowFloat<RoomDefinition.Block>("Size Z (m)", b => b.sizeZ, (b, v) => b.sizeZ = v),
                    RowFloat<RoomDefinition.Block>("Base Y (m)", b => b.baseY, (b, v) => b.baseY = v,
                        "Sobre el suelo de SU piso."),
                    RowFloat<RoomDefinition.Block>("Height (m)", b => b.height, (b, v) => b.height = v),
                    RowSlider<RoomDefinition.Block>("Yaw", b => b.yawDegrees, (b, v) => b.yawDegrees = v,
                        -180f, 180f),
                    BlockHolesRow(),
                },
            };

        private FeatureGroup<RoomDefinition.Stairs> StairsGroup() =>
            new FeatureGroup<RoomDefinition.Stairs>
            {
                key = "stairs",
                title = "Stairs",
                accentColor = new Color(0.85f, 0.75f, 0.25f),
                get = () => _def.stairs,
                set = a => _def.stairs = a,
                level = s => s.level,
                setLevel = (s, v) => s.level = v,
                make = () => new RoomDefinition.Stairs(),
                row = (s, i) => $"#{i}   {s.steps} steps   ·   rises {s.steps * s.rise:0.#} m",
                fields = new[]
                {
                    new GroupField<RoomDefinition.Stairs>("Position X", s => s.position.x, (s, v) => s.position.x = v),
                    new GroupField<RoomDefinition.Stairs>("Position Z", s => s.position.y, (s, v) => s.position.y = v),
                    new GroupField<RoomDefinition.Stairs>("Facing", s => s.yawDegrees, (s, v) => s.yawDegrees = v),
                    new GroupField<RoomDefinition.Stairs>("Width (m)", s => s.width, (s, v) => s.width = v),
                    new GroupField<RoomDefinition.Stairs>("Steps", s => s.steps,
                        (s, v) => s.steps = Mathf.Clamp(Mathf.RoundToInt(v), 1, 40)),
                    new GroupField<RoomDefinition.Stairs>("Rise per step (m)", s => s.rise, (s, v) => s.rise = v),
                    new GroupField<RoomDefinition.Stairs>("Run per step (m)", s => s.run, (s, v) => s.run = v),
                },
                rows = new[]
                {
                    RowFloat<RoomDefinition.Stairs>("Bottom step X", s => s.position.x, (s, v) => s.position.x = v),
                    RowFloat<RoomDefinition.Stairs>("Bottom step Z", s => s.position.y, (s, v) => s.position.y = v),
                    RowSlider<RoomDefinition.Stairs>("Facing", s => s.yawDegrees, (s, v) => s.yawDegrees = v,
                        -180f, 180f, "Hacia dónde sube."),
                    RowFloat<RoomDefinition.Stairs>("Width (m)", s => s.width, (s, v) => s.width = v),
                    RowIntSlider<RoomDefinition.Stairs>("Steps", s => s.steps, (s, v) => s.steps = v, 1, 40),
                    RowFloat<RoomDefinition.Stairs>("Rise per step (m)", s => s.rise, (s, v) => s.rise = v,
                        "0,18 se sube cómodo."),
                    RowFloat<RoomDefinition.Stairs>("Run per step (m)", s => s.run, (s, v) => s.run = v),
                    RowInfo<RoomDefinition.Stairs>(" ",
                        s => $"Total: {s.steps * s.rise:0.##} m up, {s.steps * s.run:0.##} m long"),
                    StairsReachRow(),
                },
            };

        private FeatureGroup<RoomDefinition.Marker> MarkersGroup() =>
            new FeatureGroup<RoomDefinition.Marker>
            {
                key = "markers",
                title = "Markers",
                accentColor = new Color(0.4f, 0.85f, 0.5f),
                get = () => _def.markers,
                set = a => _def.markers = a,
                level = m => m.level,
                setLevel = (m, v) => m.level = v,
                // Nace PROP y el tipo se cambia en la primera fila: la tira tiene UN botón de
                // añadir, y tres botones distintos por tipo eran tres formas de hacer lo mismo.
                make = () => new RoomDefinition.Marker { kind = RoomDefinition.MarkerKind.Prop },
                row = (m, i) =>
                {
                    // Un Prop cuya etiqueta no está en el catálogo se dice AQUÍ, en la línea que
                    // se ve plegada: si no, la única pista es que no aparece nada en la escena, y
                    // eso se confunde con "todavía no lo he colocado".
                    string miss = m.kind == RoomDefinition.MarkerKind.Prop
                        && !string.IsNullOrWhiteSpace(m.tag) && !PropTagResolves(m.tag)
                        ? "   ·   sin prop en el catálogo" : "";
                    return $"#{i}   {m.kind}   ·   \"{m.tag}\"{miss}";
                },
                arrange = (items, pitch) =>
                {
                    // Sin margen de pared: una luz o un punto de aparicion pegado a la esquina es
                    // legitimo, a diferencia de un pilar, que ahi solo estorba.
                    var (pts, _) = SpreadInside(items.Length, 0.4f, pitch);
                    for (int k = 0; k < items.Length && k < pts.Count; k++)
                        items[k].position = pts[k];
                },
                fields = new[]
                {
                    new GroupField<RoomDefinition.Marker>("Position X", m => m.position.x, (m, v) => m.position.x = v),
                    new GroupField<RoomDefinition.Marker>("Position Z", m => m.position.y, (m, v) => m.position.y = v),
                    new GroupField<RoomDefinition.Marker>("Height off floor (m)", m => m.y, (m, v) => m.y = v),
                    new GroupField<RoomDefinition.Marker>("Yaw", m => m.yawDegrees, (m, v) => m.yawDegrees = v),
                },
                rows = new[]
                {
                    // Solo Prop/Spawn son elegibles: Light tiene grupo propio ahora (LightsGroup).
                    // Un Popup cerrado a estos dos valores, no un EnumPopup libre, es lo que
                    // impide volver a crear un marcador Light desde aquí — MarkerKind.Light se
                    // queda en el enum solo por compatibilidad de deserialización.
                    OneLine<RoomDefinition.Marker>((r, m) =>
                    {
                        int idx = m.kind == RoomDefinition.MarkerKind.Spawn ? 1 : 0;
                        idx = EditorGUI.Popup(r, new GUIContent("Kind",
                            "No cambian la geometría: son sitios que otro sistema resuelve "
                            + "después. Arrástralos en la vista de escena."),
                            idx, MarkerKindLabels);
                        m.kind = idx == 1 ? RoomDefinition.MarkerKind.Spawn : RoomDefinition.MarkerKind.Prop;
                    }),
                    RowFloat<RoomDefinition.Marker>("Position X", m => m.position.x, (m, v) => m.position.x = v),
                    RowFloat<RoomDefinition.Marker>("Position Z", m => m.position.y, (m, v) => m.position.y = v),
                    RowFloat<RoomDefinition.Marker>("Height off floor (m)", m => m.y, (m, v) => m.y = v,
                        "Sobre el suelo de SU piso."),
                    RowSlider<RoomDefinition.Marker>("Yaw", m => m.yawDegrees, (m, v) => m.yawDegrees = v,
                        -180f, 180f),
                    PropTagRow(),
                },
            };

        private static readonly GUIContent[] MarkerKindLabels =
            NamesToContents(new[] { "Prop", "Spawn" });

        private static readonly RoomDefinition.LightBehavior[] LightBehaviorValues =
        {
            RoomDefinition.LightBehavior.Steady,
            RoomDefinition.LightBehavior.Flicker,
            RoomDefinition.LightBehavior.Dead,
        };
        private static readonly GUIContent[] LightBehaviorLabels =
            NamesToContents(new[] { "Steady", "Flicker", "Dead" });

        private FeatureGroup<RoomDefinition.Light> LightsGroup() =>
            new FeatureGroup<RoomDefinition.Light>
            {
                key = "lights",
                title = "Lights",
                accentColor = LightMarkerColor, // mismo color que el tirador en la vista de escena.
                get = () => _def.lights,
                set = a => _def.lights = a,
                level = l => l.level,
                setLevel = (l, v) => l.level = v,
                make = () => new RoomDefinition.Light(),
                row = (l, i) => l.behavior == RoomDefinition.LightBehavior.Steady
                    ? $"#{i}   range {l.lightRange:0.#} m"
                    : $"#{i}   range {l.lightRange:0.#} m   ·   {l.behavior}",
                arrange = (items, pitch) =>
                {
                    // Sin margen de pared: una luz pegada a la esquina es legítima, a diferencia
                    // de un pilar, que ahí solo estorba.
                    var (pts, _) = SpreadInside(items.Length, 0.4f, pitch);
                    for (int k = 0; k < items.Length && k < pts.Count; k++)
                        items[k].position = pts[k];
                },
                fields = new[]
                {
                    new GroupField<RoomDefinition.Light>("Position X", l => l.position.x, (l, v) => l.position.x = v),
                    new GroupField<RoomDefinition.Light>("Position Z", l => l.position.y, (l, v) => l.position.y = v),
                    new GroupField<RoomDefinition.Light>("Height off floor (m)", l => l.y, (l, v) => l.y = v),
                    new GroupField<RoomDefinition.Light>("Intensity", l => l.lightIntensity, (l, v) => l.lightIntensity = v),
                    new GroupField<RoomDefinition.Light>("Range (m)", l => l.lightRange, (l, v) => l.lightRange = v),
                },
                rows = new[]
                {
                    RowFloat<RoomDefinition.Light>("Position X", l => l.position.x, (l, v) => l.position.x = v),
                    RowFloat<RoomDefinition.Light>("Position Z", l => l.position.y, (l, v) => l.position.y = v),
                    RowFloat<RoomDefinition.Light>("Height off floor (m)", l => l.y, (l, v) => l.y = v,
                        "Sobre el suelo de SU piso."),
                    RowSlider<RoomDefinition.Light>("Yaw", l => l.yawDegrees, (l, v) => l.yawDegrees = v,
                        -180f, 180f),
                    RowColor<RoomDefinition.Light>("Color", l => l.lightColor, (l, v) => l.lightColor = v),
                    // Deslizador con campo, no solo número suelto — más intuitivo para tantear un
                    // valor, y el número al lado sigue dejando escribir uno exacto. Topes con
                    // margen de sobra sobre lo que autora una capa de verdad (lampIntensity ronda
                    // 1-3, lampRange llega a 9-12 según zona), así que no recorta nada real.
                    RowSlider<RoomDefinition.Light>("Intensity", l => l.lightIntensity, (l, v) => l.lightIntensity = v,
                        0f, 10f, "Mismo campo que autora la capa (LayerVisualConfig.lampIntensity), típico 1-3."),
                    RowSlider<RoomDefinition.Light>("Range (m)", l => l.lightRange, (l, v) => l.lightRange = v,
                        0.5f, 20f, "Alcance de la luz. El pasillo autora hasta 6-9 m según la zona."),
                    OneLine<RoomDefinition.Light>((r, l) =>
                    {
                        int idx = System.Array.IndexOf(LightBehaviorValues, l.behavior);
                        idx = EditorGUI.Popup(r, new GUIContent("Behavior",
                            "Steady: fija, como siempre.\n\n"
                            + "Flicker: parpadea en vivo (en el editor y en juego) — mismo "
                            + "comportamiento visual que las lámparas del laberinto.\n\n"
                            + "Dead: se hornea apagada, como una lámpara fundida."),
                            Mathf.Max(0, idx), LightBehaviorLabels);
                        l.behavior = LightBehaviorValues[idx];
                    }),
                    Disabled<RoomDefinition.Light>(l => l.behavior != RoomDefinition.LightBehavior.Flicker,
                        RowSlider<RoomDefinition.Light>("Flicker Hz", l => l.flickerHz, (l, v) => l.flickerHz = v,
                            0.1f, 10f, "Las lámparas del pasillo parpadean entre 0,5 y 4 Hz.")),
                },
            };

        /// <summary>
        /// Los boquetes de un bloque: una lista DENTRO de la fila desplegable del bloque, porque
        /// un tabique con puerta es un bloque con un hueco y no un tipo de feature aparte.
        ///
        /// Sin plegado propio — sería un tercer nivel de desplegables y ya no se sabría de quién
        /// es lo que estás mirando. Cada boquete son sus cinco campos seguidos, con su × .
        /// </summary>
        private Row<RoomDefinition.Block> BlockHolesRow()
        {
            const int fieldsPerHole = 5;
            return new Row<RoomDefinition.Block>(
                b => LineStep * (1 + (b.holes?.Length ?? 0) * (fieldsPerHole + 1)) + 6f,
                (r, b) =>
                {
                    float y = r.y;
                    int n = b.holes?.Length ?? 0;

                    var head = new Rect(r.x, y, r.width, LineH);
                    EditorGUI.LabelField(new Rect(head.x, head.y, head.width - 74f, head.height),
                        $"Doors/windows ({n})", EditorStyles.miniBoldLabel);
                    if (GUI.Button(new Rect(head.xMax - 70f, head.y, 70f, head.height),
                            new GUIContent("+ Opening"), EditorStyles.miniButton))
                    {
                        var next = b.holes ?? new RoomDefinition.BlockHole[0];
                        ArrayUtility.Add(ref next, new RoomDefinition.BlockHole { side = 0, along = 0.5f });
                        b.holes = next;
                        RebuildIfLive();
                    }
                    y += LineStep;

                    for (int i = 0; i < n; i++)
                    {
                        var h = b.holes[i];
                        var line = new Rect(r.x + 10f, y, r.width - 10f, LineH);
                        EditorGUI.LabelField(new Rect(line.x, line.y, line.width - 28f, line.height),
                            $"#{i}  wall {h.side}", EditorStyles.miniLabel);
                        if (GUI.Button(new Rect(line.xMax - 24f, line.y, 24f, line.height),
                                new GUIContent("×", "Remove"), EditorStyles.miniButton))
                        {
                            var next = b.holes;
                            ArrayUtility.RemoveAt(ref next, i);
                            b.holes = next;
                            RebuildIfLive();
                            return; // el array ha cambiado: el resto de esta pasada ya no vale
                        }
                        y += LineStep;

                        var f = new Rect(r.x + 20f, y, r.width - 20f, LineH);
                        h.side = EditorGUI.IntSlider(f, new GUIContent("Wall",
                            "0..3, en el mismo orden que las esquinas del bloque."), h.side, 0, 3);
                        y += LineStep; f.y = y;
                        h.along = EditorGUI.Slider(f, "Along wall", h.along, 0f, 1f);
                        y += LineStep; f.y = y;
                        h.baseY = EditorGUI.FloatField(f, new GUIContent("Height off block base (m)",
                            "0 = puerta."), h.baseY);
                        y += LineStep; f.y = y;
                        h.width = EditorGUI.FloatField(f, "Width (m)", h.width);
                        y += LineStep; f.y = y;
                        h.height = EditorGUI.FloatField(f, "Height (m)", h.height);
                        y += LineStep;
                    }
                });
        }

        /// <summary>
        /// El "llega justo" calculado en vez de a mano: un botón por losa alcanzable ajusta Steps
        /// para que el último peldaño llegue a esa losa desde el piso del tramo. Antes era dividir
        /// alturas entre rise con la calculadora y equivocarse por un peldaño — y un tramo un pelo
        /// corto ni siquiera abre su hueco.
        /// </summary>
        private Row<RoomDefinition.Stairs> StairsReachRow() =>
            new Row<RoomDefinition.Stairs>(
                _ => _def.StoreyCount == 0 ? 0f : LineStep,
                (r, s) =>
                {
                    if (_def.StoreyCount == 0 || s.rise <= 0.001f) return;

                    float minCeil = _def.MinCeilingOver(_def.InnerContour());
                    float baseY = _def.StoreyBaseY(s.level, minCeil);
                    _def.SortedLevelTops(minCeil, _levelTopsScratch);

                    var line = new Rect(r.x, r.y, r.width, LineH);
                    var content = EditorGUI.PrefixLabel(line, new GUIContent("Reach",
                        "Ajusta Steps para llegar justo a esa losa desde el piso del tramo."));

                    int reachable = 0;
                    for (int k = 0; k < _levelTopsScratch.Count; k++)
                        if (_levelTopsScratch[k] > baseY + 0.05f) reachable++;
                    if (reachable == 0) return;

                    float w = content.width / reachable, x = content.x;
                    for (int k = 0; k < _levelTopsScratch.Count; k++)
                    {
                        float top = _levelTopsScratch[k];
                        if (top <= baseY + 0.05f) continue; // su propio suelo, o uno de más abajo
                        if (GUI.Button(new Rect(x, content.y, w, content.height),
                                $"floor {k + 1}", EditorStyles.miniButton))
                        {
                            int steps = Mathf.CeilToInt((top - baseY) / s.rise);
                            s.steps = Mathf.Clamp(steps, 1, 40);
                            if (steps > 40)
                                Debug.LogWarning($"[RoomAuthoringWindow] {steps} steps needed but "
                                                 + "40 is the cap -- raise Rise per step.");
                            RebuildIfLive();
                        }
                        x += w;
                    }
                });

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

                // Duplicar: el gesto de "otro igual y le cambio una cosa", que es como se ponen
                // de verdad cuatro ventanas iguales. Clon por JSON — el mismo viaje que LoadRoom —
                // para no compartir arrays anidados (boquetes de un bloque) entre los dos.
                if (GUILayout.Button(new GUIContent("⧉", "Duplicate"),
                        EditorStyles.miniButtonMid, GUILayout.Width(24)))
                {
                    var copy = JsonUtility.FromJson<T>(JsonUtility.ToJson(array[i]));
                    ArrayUtility.Insert(ref array, i + 1, copy);
                    _foldouts.Clear(); // los índices se han corrido, igual que al borrar
                    RebuildIfLive();
                    GUIUtility.ExitGUI();
                }

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

        /// <summary>
        /// Una fila del desplegable de un elemento. Sabe LO QUE MIDE, y esa es toda la razón de
        /// que exista: un <see cref="ReorderableList"/> pide la altura de cada elemento ANTES de
        /// dibujarlo (<c>elementHeightCallback</c>), así que un detalle escrito con
        /// EditorGUILayout —que solo conoce su altura cuando ya se ha pintado— no cabe dentro.
        ///
        /// Declarando las filas, la altura es la SUMA de lo que se va a dibujar y no puede
        /// desfasarse de ello. La alternativa era medir lo dibujado y usarlo al frame siguiente,
        /// que es un elemento mal recortado cada vez que se despliega.
        ///
        /// La altura es función del elemento y no una constante porque hay filas que crecen con
        /// él — los boquetes de un bloque son una lista dentro de la lista.
        /// </summary>
        private readonly struct Row<T>
        {
            public readonly System.Func<T, float> height;
            public readonly System.Action<Rect, T> draw;

            public Row(System.Func<T, float> height, System.Action<Rect, T> draw)
            {
                this.height = height;
                this.draw = draw;
            }
        }

        private static float LineH => EditorGUIUtility.singleLineHeight;
        private static float LineStep => LineH + EditorGUIUtility.standardVerticalSpacing;

        /// <summary>Una fila de una sola línea: recorta el rect a la altura de un control y deja
        /// el espaciado estándar por debajo, para que dos filas seguidas no se toquen.</summary>
        private static Row<T> OneLine<T>(System.Action<Rect, T> draw) =>
            new Row<T>(_ => LineStep, (r, it) => draw(new Rect(r.x, r.y, r.width, LineH), it));

        private static Row<T> RowFloat<T>(string label, System.Func<T, float> get,
            System.Action<T, float> set, string tip = "") =>
            OneLine<T>((r, it) => set(it, EditorGUI.FloatField(r, new GUIContent(label, tip), get(it))));

        private static Row<T> RowIntSlider<T>(string label, System.Func<T, int> get,
            System.Action<T, int> set, int min, int max, string tip = "") =>
            OneLine<T>((r, it) => set(it, EditorGUI.IntSlider(r, new GUIContent(label, tip), get(it), min, max)));

        private static Row<T> RowSlider<T>(string label, System.Func<T, float> get,
            System.Action<T, float> set, float min, float max, string tip = "") =>
            OneLine<T>((r, it) => set(it, EditorGUI.Slider(r, new GUIContent(label, tip), get(it), min, max)));

        private static Row<T> RowToggle<T>(string label, System.Func<T, bool> get,
            System.Action<T, bool> set, string tip = "") =>
            OneLine<T>((r, it) => set(it, EditorGUI.Toggle(r, new GUIContent(label, tip), get(it))));

        private static Row<T> RowText<T>(string label, System.Func<T, string> get,
            System.Action<T, string> set, string tip = "") =>
            OneLine<T>((r, it) => set(it, EditorGUI.TextField(r, new GUIContent(label, tip), get(it))));

        private static Row<T> RowColor<T>(string label, System.Func<T, Color> get,
            System.Action<T, Color> set) =>
            OneLine<T>((r, it) => set(it, EditorGUI.ColorField(r, new GUIContent(label), get(it))));

        private static Row<T> RowInfo<T>(string label, System.Func<T, string> text) =>
            OneLine<T>((r, it) => EditorGUI.LabelField(r, label, text(it), EditorStyles.miniLabel));

        /// <summary>
        /// Fila apagada cuando <paramref name="off"/> dice que sí. Apagada y no escondida: un
        /// campo que desaparece al cambiar otra cosa hace pensar que su valor se ha perdido, y
        /// sigue ahí para cuando la condición vuelva.
        /// </summary>
        private static Row<T> Disabled<T>(System.Func<T, bool> off, Row<T> inner) =>
            new Row<T>(inner.height, (r, it) =>
            {
                using (new EditorGUI.DisabledScope(off(it))) inner.draw(r, it);
            });

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
        /// Con 2 o más elementos del mismo tipo, un panel "All N" antes de la lista, con DOS
        /// modos y un botón que los alterna:
        ///
        ///  · Δ (el de siempre): tocar un campo aplica el MISMO DESPLAZAMIENTO a cada elemento,
        ///    igual que mover un Transform con varios objetos seleccionados — lo que persiste es
        ///    la diferencia relativa entre ellos, y un elemento tocado a mano se mueve CON el
        ///    grupo sin perder lo que lo hacía distinto.
        ///  · = : fija el MISMO VALOR en todos — "todas las ventanas a 1,2 m del suelo" es una
        ///    frase de valor absoluto, y conseguirla con desplazamientos era hacer la cuenta a
        ///    mano por cada elemento, que es justo lo que este panel existe para evitar.
        ///
        /// Con 0 o 1 elemento no hay grupo que editar y no se dibuja nada.
        /// </summary>
        private void DrawGroupPanel<T>(T[] items, string key, string label, GroupField<T>[] fields)
        {
            if (items == null || items.Length < 2 || fields == null || fields.Length == 0) return;

            _groupSetMode.TryGetValue(key, out bool setMode);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"All {items.Length} {label}", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(new GUIContent(setMode ? "= set" : "Δ offset",
                            "Δ: desplaza todos lo mismo, manteniendo sus diferencias.\n"
                            + "=: fija el mismo valor en todos."),
                            EditorStyles.miniButton, GUILayout.Width(64)))
                    {
                        setMode = !setMode;
                        _groupSetMode[key] = setMode;
                    }
                }

                foreach (var f in fields)
                {
                    float shown = f.get(items[0]);
                    EditorGUI.BeginChangeCheck();
                    float next = EditorGUILayout.FloatField(new GUIContent(f.label, f.tooltip), shown);
                    if (EditorGUI.EndChangeCheck())
                    {
                        if (setMode)
                        {
                            foreach (var item in items) f.set(item, next);
                        }
                        else
                        {
                            float delta = next - shown;
                            foreach (var item in items) f.set(item, f.get(item) + delta);
                        }
                    }
                }
            }
        }

        // Modo del panel de grupo por lista ("pillars", "blocks"...). Fuera del modelo por lo
        // mismo que _foldouts: es cómo se está editando, no parte de la sala.
        private readonly Dictionary<string, bool> _groupSetMode = new Dictionary<string, bool>();

        private static readonly List<float> _levelTopsScratch = new List<float>();

        /// <summary>Puesto EN ALTURA de la losa <paramref name="levelIndex"/> entre todas
        /// (1 = la más baja): el mismo criterio de orden que <see cref="RoomDefinition.SortedLevelTops"/>,
        /// que es el que da sentido al campo Floor de cada feature.</summary>
        private int StoreyRankOf(int levelIndex)
        {
            float minCeil = _def.MinCeilingOver(_def.InnerContour());
            float mine = _def.ClampLevelHeight(_def.levels[levelIndex].height, minCeil);
            int rank = 1;
            for (int i = 0; i < _def.levels.Length; i++)
            {
                if (i == levelIndex || _def.levels[i] == null) continue;
                float other = _def.ClampLevelHeight(_def.levels[i].height, minCeil);
                if (other < mine || (Mathf.Approximately(other, mine) && i < levelIndex)) rank++;
            }
            return rank;
        }

        /// <summary>Cuántas features viven en el piso <paramref name="storey"/> — con el mismo
        /// recorte de "más allá de la última losa cae a la última" que aplica el modelo, para que
        /// el recuento diga lo que de verdad se va a construir.</summary>
        private int FeaturesOnStorey(int storey)
        {
            int Clamp(int level) => Mathf.Clamp(level, 0, _def.StoreyCount);
            int c = 0;
            foreach (var h in _def.holes) if (h != null && Clamp(h.level) == storey) c++;
            foreach (var f in _def.floorHoles) if (f != null && Clamp(f.level) == storey) c++;
            foreach (var p in _def.pillars) if (p != null && Clamp(p.level) == storey) c++;
            foreach (var b in _def.blocks) if (b != null && Clamp(b.level) == storey) c++;
            foreach (var s in _def.stairs) if (s != null && Clamp(s.level) == storey) c++;
            foreach (var m in _def.markers) if (m != null && Clamp(m.level) == storey) c++;
            foreach (var l in _def.lights) if (l != null && Clamp(l.level) == storey) c++;
            return c;
        }

        private void DrawSaveTab()
        {
            DrawValidatePanel();
            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(_preview == null))
            using (new EditorGUILayout.HorizontalScope())
            {
                string label = _loadedRoomId != null ? $"Update {_loadedRoomId}" : "Save Room To Pool";
                if (GUILayout.Button(label, GUILayout.Height(28)))
                    SaveGenerated(_loadedRoomId);

                // Solo tiene sentido si hay una sala cargada: sin eso "Save As New" y el botón de
                // arriba harían exactamente lo mismo.
                if (_loadedRoomId != null
                    && GUILayout.Button("Save As New Room", GUILayout.Height(28), GUILayout.Width(130)))
                    SaveGenerated(null);
            }

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

        // Resultado de la última pasada de Validate. Null = todavía no se ha pulsado esta sesión
        // de edición; lista vacía = sin problemas. Cacheado y no recalculado a cada frame porque
        // el chequeo de vanos no es gratis (recorre el contorno por cada boquete y cada giro) y no
        // hace falta correrlo 60 veces por segundo mientras se arrastra un slider.
        private List<string> _validateIssues;

        /// <summary>
        /// Los mismos checks que <c>RoomManifestExporter.Export()</c> aplica al exportar — cap de
        /// footprint, vanos mínimos en salas grandes, existencia de puerta practicable — pero EN
        /// VIVO, sin necesidad de guardar. Botón manual y no automático: pide la MISMA lógica que
        /// el exportador real (<see cref="RoomManifestExporter.DoorwayByQuarter"/>), así que un
        /// "sin problemas" aquí significa de verdad que el exportador no la va a descartar.
        /// </summary>
        private void DrawValidatePanel()
        {
            if (GUILayout.Button("Validate", GUILayout.Height(22)))
                RunValidation();

            if (_validateIssues == null) return;

            if (_validateIssues.Count == 0)
            {
                EditorGUILayout.HelpBox("Sin problemas — cumple los caps y tiene puerta practicable.",
                    MessageType.Info);
                return;
            }
            foreach (string issue in _validateIssues)
                EditorGUILayout.HelpBox(issue, MessageType.Warning);
        }

        private void RunValidation()
        {
            _validateIssues = new List<string>();

            // Planta frente a footprint, en cualquier modo: lo que el footprint reserva y la
            // planta no llena es boquete en el mundo (ADR-083/084 vacían el footprint entero).
            string footprintIssue = FootprintIssue(_def);
            if (footprintIssue != null) _validateIssues.Add(footprintIssue);

            // El mismo camino que usa el exportador real: una RoomEntry de usar-y-tirar con
            // SOLO los campos que DoorwayByQuarter mira (definition, tilesX, tilesZ), para no
            // duplicar su lógica de localizar el vano real sobre el contorno girado.
            var throwaway = new RoomPool.RoomEntry
                { definition = _def, tilesX = _def.tilesX, tilesZ = _def.tilesZ };
            if (!RoomManifestExporter.DoorwayByQuarter(throwaway, out var doorways, out string why))
            {
                _validateIssues.Add($"Sin puerta practicable: {why}.");
                return; // los caps de abajo no importan si ni siquiera se puede exportar.
            }

            if (_def.tilesX > RoomManifestExporter.BackendFootprintCapTiles
                || _def.tilesZ > RoomManifestExporter.BackendFootprintCapTiles)
                _validateIssues.Add($"Por encima del cap de " +
                    $"{RoomManifestExporter.BackendFootprintCapTiles}×{RoomManifestExporter.BackendFootprintCapTiles} " +
                    "tiles (ADR-084, 2×2 chunks): se exportaría, pero el backend NO la colocará.");

            // El cap de altura NO rechaza la sala: el backend recorta las capas invadidas, así que
            // la losa de la primera capa que no puede invadir le entra por dentro. Sin este aviso
            // eso se descubre jugando, con un techo cortado a media sala y sin nada que lo explique.
            if (_def.heightMeters > RoomManifestExporter.BackendHeightCapMeters)
                _validateIssues.Add($"{_def.heightMeters:0.#} m supera el cap de altura de " +
                    $"{RoomManifestExporter.BackendHeightCapMeters:0.#} m (ADR-085): se exporta, pero " +
                    "en el mundo la losa de la primera capa no invadida la atraviesa. Para probar " +
                    "en el editor da igual; para hornearla, no.");

            if ((_def.tilesX > RoomManifestExporter.BackendInChunkCapTiles
                    || _def.tilesZ > RoomManifestExporter.BackendInChunkCapTiles)
                && doorways.Count < RoomManifestExporter.MinDoorwaysForBigRooms)
                _validateIssues.Add($"Sala grande ({_def.tilesX}×{_def.tilesZ}) con solo " +
                    $"{doorways.Count} vano(s): nace incomunicada a menudo (medido: 6 de 55). Ponle " +
                    $"al menos {RoomManifestExporter.MinDoorwaysForBigRooms}, mejor uno por lado.");
        }

        private void SaveGenerated(string overwriteId)
        {
            if (SaveGeneratedRoom(_def, overwriteId, out string message, out string savedId))
            {
                Debug.Log($"[RoomAuthoringWindow] {message}");
                _loadedRoomId = savedId;
            }
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
        /// <summary>
        /// Altura del vano que se añade solo. Menos que la sala, para que quede dintel — un hueco
        /// que llega al techo no se lee como puerta, se lee como pared que falta.
        /// </summary>
        private const float AutoDoorwayHeight = 2.6f;

        /// <summary>
        /// Ancho del vano que se añade solo, en metros. El túnel que excava el backend mide un TILE
        /// (5 m), así que 4 deja media jamba a cada lado y el vano se lee como puerta en vez de como
        /// boquete del ancho del pasillo.
        /// </summary>
        private const float AutoDoorwayWidth = 4f;

        /// <summary>
        /// Garantiza que la sala tenga por dónde entrarse. No hace nada si ya hay una abertura a ras
        /// de suelo; si no la hay, añade una al modelo.
        ///
        /// **Va centrada en un TILE de 5 m, no en la pared.** Es lo que la ata al túnel que excava el
        /// backend, que trabaja en tiles: una puerta centrada en la pared de una sala de 4 tiles cae
        /// justo en la frontera entre dos tiles, y el pasillo llega medio contra el muro. `room_0`
        /// era exactamente ese caso.
        ///
        /// El tile elegido es `tiles / 2`, la MISMA cuenta que hace el backend, y el lado es el 0
        /// (+z) — el mismo al que apunta por defecto el ancla de puerta.
        ///
        /// Solo se sabe alinear con planta poligonal de 4 lados y esquinada, que es la rectangular.
        /// En cualquier otra forma la pared no es paralela a la retícula y no hay tile con el que
        /// alinear: se pone en mitad de la pared y se avisa.
        /// </summary>
        private static void EnsureDoorway(RoomDefinition def)
        {
            def.holes ??= System.Array.Empty<RoomDefinition.WallHole>();
            var existing = def.InnerContour();
            foreach (var h in def.holes)
            {
                // Una reja no es una puerta, y una ventana tampoco: tienen que dejar pasar a ras de
                // suelo. Mismo criterio con el que el exportador decidirá por dónde entra el pasillo.
                if (h == null || h.baseY > 0.05f || h.grateBars != 0 || h.width < 1f)
                    continue;

                // Hay puerta declarada, pero eso no significa que EXISTA. Un vano más ancho que su
                // propia pared se recorta contra ella y puede desaparecer entero: es lo que pasa en
                // una planta redonda, donde cada faceta mide poco más de un metro. `spanCorners` lo
                // arregla haciendo que el hueco avance por el contorno en vez de morir en su arista.
                if (!h.spanCorners && existing != null && existing.Length >= 3)
                {
                    int s = ((h.side % existing.Length) + existing.Length) % existing.Length;
                    float edgeLen = (existing[(s + 1) % existing.Length] - existing[s]).magnitude;
                    if (edgeLen < h.width)
                    {
                        h.spanCorners = true;
                        Debug.LogWarning($"[RoomAuthoringWindow] La puerta del lado {s} medía " +
                                         $"{h.width:0.#} m sobre una pared de {edgeLen:0.#} m: se " +
                                         "recortaba hasta desaparecer. Se le ha activado " +
                                         "`spanCorners` para que doble la esquina y sea un vano real.");
                    }
                }
                return;
            }

            var contour = def.InnerContour();
            if (contour == null || contour.Length < 3)
                return; // planta degenerada; el horneado ya la rechaza por otro lado

            // El sitio DONDE tiene que caer la puerta, decidido por la retícula del mundo y no por
            // la forma de la sala: el borde +z del footprint, a la altura del centro del tile
            // `tilesX / 2`. Es la MISMA cuenta con la que el backend excava el túnel, así que es lo
            // que hace que pasillo y vano se encuentren.
            float halfX = def.tilesX * 5f * 0.5f;
            float halfZ = def.tilesZ * 5f * 0.5f;
            var target = new Vector2(-halfX + (Mathf.Max(1, def.tilesX) / 2 + 0.5f) * 5f, halfZ);

            // La arista del contorno cuyo punto medio cae más cerca de ese sitio. En una planta
            // rectangular es el muro +z entero; en una redonda de 64 lados es la facetita que le
            // toca. Buscarla en vez de fijar "el lado 0" es lo que evita que la puerta salga por
            // donde el pasillo no va.
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < contour.Length; i++)
            {
                Vector2 mid = Vector2.Lerp(contour[i], contour[(i + 1) % contour.Length], 0.5f);
                float d = (mid - target).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }

            // `along` ajusta DENTRO de la arista elegida, que en una planta rectangular es larga y
            // en una redonda es corta. Se proyecta el objetivo sobre ella y se recorta.
            Vector2 p0 = contour[best], p1 = contour[(best + 1) % contour.Length];
            Vector2 edge = p1 - p0;
            float along = edge.sqrMagnitude > 1e-6f
                ? Mathf.Clamp01(Vector2.Dot(target - p0, edge) / edge.sqrMagnitude)
                : 0.5f;

            // Una arista corta (planta redonda o poligonal fina) NO puede alojar una puerta de 4 m:
            // el hueco se recorta contra su propia pared y desaparece entero — que es justo lo que
            // pasó con la primera sala de 64 lados. `spanCorners` hace que el vano avance por el
            // CONTORNO y se coma los vértices que le queden dentro, que es la única forma de abrir
            // un hueco de tamaño humano en una pared curva.
            float width = Mathf.Min(AutoDoorwayWidth, def.tilesX * 5f);
            bool needsSpan = edge.magnitude < width;

            var doorway = new RoomDefinition.WallHole
            {
                side = best,
                along = along,
                baseY = 0f,
                level = 0,
                width = width,
                height = Mathf.Min(AutoDoorwayHeight, Mathf.Max(1f, def.heightMeters - 0.4f)),
                grateBars = 0,
                spanCorners = needsSpan,
            };
            ArrayUtility.Add(ref def.holes, doorway);

            Debug.LogWarning($"[RoomAuthoringWindow] La sala no tenía ninguna abertura a ras de " +
                             $"suelo: se le ha añadido una puerta de {width:0.#} m en el lado {best} " +
                             $"de {contour.Length}, alineada con el tile por el que entra el pasillo" +
                             (needsSpan ? " y doblando esquina (pared corta)." : ".") +
                             " Sin ella el mundo la coloca como una caja sellada.");
        }

        /// <summary>
        /// Hornea o REEMPLAZA una sala generada. <paramref name="overwriteId"/> null = crea una
        /// entrada nueva (`room_N`, el próximo índice libre) como siempre; con un id ya existente
        /// en el pool, actualiza esa entrada EN EL SITIO — mismo prefab (mismo GUID, vía
        /// <see cref="PrefabUtility.SaveAsPrefabAsset"/> sobre la misma ruta) y mismo asset de
        /// malla, así que editar y volver a guardar ya no deja una copia nueva por cada guardado.
        /// </summary>
        internal static bool SaveGeneratedRoom(RoomDefinition def, string overwriteId,
            out string message, out string savedId)
        {
            savedId = null;
            if (def == null || def.tilesX < 1 || def.tilesZ < 1 || def.heightMeters <= 0f)
            {
                message = "Room parameters are not valid (size and height must be positive).";
                return false;
            }
            if (def.planMode == RoomDefinition.PlanMode.Manual
                && RoomDefinition.ContourSelfIntersects(def.InnerContour()))
            {
                // El recorte de orejas no detecta un contorno cruzado por si solo (puede
                // triangular "bien" de todas formas) -- sin este gate, un lazo dibujado a mano se
                // colaria como sala valida con geometria de pared cruzada/basura.
                message = "Manual contour is self-intersecting (two edges cross). Fix the shape " +
                          "in the Scene view and try again.";
                return false;
            }
            // Planta manual: el footprint que viaja al pool y al manifiesto es el que cubre los
            // puntos, no el que tuviera tecleado el modelo. También cubre el rehorneado desde el
            // pool, que no pasa por la ventana.
            if (FitFootprintToPlan(def))
                Debug.Log($"[RoomAuthoringWindow] Footprint ajustado a la planta manual: " +
                          $"{def.tilesX} × {def.tilesZ} tiles.");

            // ADR-083 enmienda 1: una sala SIN abertura a ras de suelo es una caja sellada, y el
            // mundo no puede abrirla — el backend excava el pasillo hasta la pared, pero la pared es
            // geometría del prefab y él no la toca. Se le pone una puerta aquí, antes de construir
            // la malla, para que malla y colisión salgan ya con el hueco.
            EnsureDoorway(def);

            BackroomsEditorFolders.EnsureFolder("Assets/Resources");
            BackroomsEditorFolders.EnsureFolder(RoomFolder);

            string id = overwriteId ?? $"room_{NextFreeIndex()}";
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
            // UV2 de lightmap: sin esto no hay iluminación horneada, y en Backrooms la luz es
            // la mitad del sitio. Se genera UNA vez, al guardar — no en cada reconstrucción en
            // vivo del preview, que sería carísimo y para nada: el preview no se hornea.
            Unwrapping.GenerateSecondaryUVSet(mesh);
            // CreateAsset a secas rompe si NextFreeIndex reutiliza un indice cuyo .prefab se borro
            // a mano pero cuyo _mesh.asset quedo huerfano (el Project no lo borra en cascada): el
            // patron ya establecido en este repo (BackroomsWatchMeshApplier/BackroomsSprayModelSwapper)
            // es cargar el existente y CopySerialized -- borrar+crear cambiaria el GUID y dejaria
            // cualquier referencia externa rota.
            var existingMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (existingMesh == null)
            {
                AssetDatabase.CreateAsset(mesh, meshPath);
            }
            else
            {
                EditorUtility.CopySerialized(mesh, existingMesh);
                Object.DestroyImmediate(mesh);
                mesh = existingMesh;
                EditorUtility.SetDirty(mesh);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(meshPath, ImportAssetOptions.ForceUpdate);
                mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            }

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

                CreateMarkers(root.transform, def);
                CreateLights(root.transform, def);

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (prefab == null)
                {
                    message = $"Failed to save prefab at {prefabPath}.";
                    return false;
                }

                var pool = LoadOrCreatePool();
                // CLON, no la referencia directa: `def` puede ser el `_def` VIVO de la ventana, que
                // se sigue mutando en las siguientes bakes de la misma sesion. Guardar la referencia
                // dejaria esta entrada (y cualquier otra que tambien apunte a `_def`) apuntando
                // silenciosamente a los parametros de la SIGUIENTE sala horneada en vez de a los
                // suyos propios.
                var clonedDef = JsonUtility.FromJson<RoomDefinition>(JsonUtility.ToJson(def));
                var existingEntry = overwriteId != null
                    ? System.Array.Find(pool.rooms, e => e != null && e.id == overwriteId)
                    : null;
                bool updated = existingEntry != null;

                var entry = existingEntry ?? new RoomPool.RoomEntry { id = id };
                entry.prefab = prefab;
                entry.tilesX = def.tilesX;
                entry.tilesZ = def.tilesZ;
                entry.heightMeters = def.heightMeters;
                entry.doorLocalPosition = doorPos;
                entry.doorLocalForward = doorFwd;
                entry.collisionBoxes = boxes.ToArray();
                entry.definition = clonedDef;

                if (!updated)
                {
                    if (overwriteId != null)
                        Debug.LogWarning($"[RoomAuthoringWindow] '{overwriteId}' ya no está en el " +
                                         "pool (¿se borró a mano?) — guardada como sala nueva en su lugar.");
                    ArrayUtility.Add(ref pool.rooms, entry);
                }
                EditorUtility.SetDirty(pool);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                // ADR-083 enmienda 1: el manifiesto que lee el backend sale del horneado, siempre.
                // ESTA es la ruta que usa la herramienta; sin la reexportación aquí, el pool queda
                // más nuevo que el manifiesto y el backend pide una sala que el cliente ya no tiene
                // — el fallo exacto que `GridChunkBuilder` caza con "pool y manifiesto desparejados".
                if (!RoomManifestExporter.Export(out string manifestMessage))
                    Debug.LogWarning($"[RoomAuthoringWindow] Manifiesto NO exportado — {manifestMessage}");

                message = $"{(updated ? "Updated" : "Saved")} {id} ({def.tilesX}×{def.tilesZ} tiles, " +
                          $"{def.sides} sides) → {prefabPath}. {mesh.vertexCount} verts, " +
                          $"{boxes.Count} collider box(es), {pool.rooms.Length} room(s) in the pool.";
                savedId = id;
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
                    // Solo cuenta una puerta de PLANTA BAJA: la ancla es por donde se entra a la
                    // sala desde el pasillo, y una puerta de piso 1 con baseY 0 está a la altura
                    // de su entreplanta — anclar ahí pondría la sala pegada a un vano en el aire.
                    if (hole == null || hole.level > 0 || hole.baseY > 0.01f) continue;
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
        /// Los marcadores de la sala (Prop/Spawn — Light tiene su propio <see cref="CreateLights"/>
        /// ahora), convertidos a hijos de verdad del prefab con <see cref="RoomMarker"/> y su
        /// etiqueta.
        /// </summary>
        private static void CreateMarkers(Transform parent, RoomDefinition def)
        {
            if (def.markers == null) return;
            // El mismo suelo por piso que usan malla y colliders: un marcador del piso 1 nace
            // sobre su losa, no flotando a su Y "cruda" desde el suelo de la sala.
            float minCeil = def.MinCeilingOver(def.InnerContour());
            foreach (var m in def.markers)
            {
                if (m == null) continue;
                var go = new GameObject($"{m.kind}_{m.tag}");
                go.transform.SetParent(parent, false);
                go.transform.localPosition = new Vector3(
                    m.position.x, def.StoreyBaseY(m.level, minCeil) + m.y, m.position.y);
                go.transform.localRotation = Quaternion.Euler(0f, m.yawDegrees, 0f);

                var marker = go.AddComponent<RoomMarker>();
                marker.kind = m.kind;
                marker.label = m.tag;
            }
        }

        /// <summary>
        /// Las luces de la sala, convertidas en <see cref="Light"/> reales — mismo patrón que
        /// <see cref="CreateMarkers"/>, en un método aparte porque ya no es un <c>Marker</c>.
        /// <see cref="RoomDefinition.LightBehavior.Flicker"/> reusa
        /// <see cref="BackroomsLighting"/>.<see cref="LampFlicker"/>, el MISMO comportamiento
        /// visual que ya tienen las lámparas proceduales del laberinto — sin enganchar su audio de
        /// zumbido (<c>FluorescentHumDirector</c>), que es un sistema por-chunk pensado para
        /// cientos de lámparas y no para una luz autorada suelta. <c>Dead</c> se hornea apagada,
        /// como una lámpara fundida.
        /// </summary>
        private static void CreateLights(Transform parent, RoomDefinition def)
        {
            if (def.lights == null) return;
            float minCeil = def.MinCeilingOver(def.InnerContour());
            var cfg = DefaultLayerVisualConfig();
            var lampMat = cfg != null ? LayerVisualMaterials.Build(cfg).lamp : null;

            foreach (var l in def.lights)
            {
                if (l == null) continue;
                var go = new GameObject("Light");
                go.transform.SetParent(parent, false);
                var pos = new Vector3(
                    l.position.x, def.StoreyBaseY(l.level, minCeil) + l.y, l.position.y);
                go.transform.localPosition = pos;
                go.transform.localRotation = Quaternion.Euler(0f, l.yawDegrees, 0f);

                var light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = l.lightColor;
                light.intensity = l.lightIntensity;
                light.range = l.lightRange;

                bool dead = l.behavior == RoomDefinition.LightBehavior.Dead;
                // El difusor: mismo panel emisivo que pone BackroomsLighting.MakeLuminaire en el
                // pasillo — sin esto una luz autorada era una bombilla invisible, la única del
                // mundo sin cuerpo. HERMANO de la Light y no hijo (escala no uniforme, ver
                // CreateLuminaireMesh). Apagado a la misma cifra que un lámpara rota procedural.
                var luminaire = CreateLuminaireMesh(parent, lampMat);
                luminaire.transform.localPosition = pos; // mismo `parent` que el Light: local vale igual.
                SetLuminaireEmission(luminaire, l.lightColor.linear * (dead ? 0.04f : LuminaireEmissionScale));

                switch (l.behavior)
                {
                    case RoomDefinition.LightBehavior.Flicker:
                        var flicker = go.AddComponent<LampFlicker>();
                        flicker.target = light;
                        flicker.baseIntensity = l.lightIntensity;
                        flicker.frequency = l.flickerHz;
                        // Desfase fijo por índice, no aleatorio: el contenido es autorado, no
                        // procedural, y no hace falta que dos sesiones vean el mismo parpadeo por
                        // separado — solo que varias luces de la misma sala no se muevan en
                        // lockstep. `mesh`/`litEmission` se dejan sin asignar a propósito: esos
                        // solo los usa el fundido de LampFlicker.StartDying, y una luz autorada
                        // nunca lo dispara (eso es puramente procedural, del pasillo) — cablearlos
                        // sería código muerto.
                        flicker.phase = System.Array.IndexOf(def.lights, l) * 0.37f;
                        break;
                    case RoomDefinition.LightBehavior.Dead:
                        light.enabled = false;
                        break;
                }
            }
        }

        private void RebuildIfLive()
        {
            if (_preview != null) Rebuild();
        }

        /// <summary>
        /// Tiradores de la planta manual, dibujados en la vista de escena. Necesitan una
        /// PREVIEW viva porque los puntos del modelo son locales (XZ, relativos al centro de la
        /// sala) y el tirador se mueve en mundo — sin el transform de la preview no hay a qué
        /// convertir.
        ///
        /// Un cubito ROJO encima de cada punto lo borra al pulsarlo; un punto AMARILLO a media
        /// arista inserta uno nuevo ahí — el mismo par de gestos que "añadir con un botón,
        /// quitar con ×" que ya usa el resto de la ventana, solo que en 3D.
        /// </summary>
        private void OnSceneGUI(SceneView view)
        {
            if (_preview == null) return;

            // Arrastrar un tirador es una edición como cualquier otra y tiene que poder deshacerse
            // desde la ventana. La vista de escena tiene su propia cola de eventos, así que el
            // arrastre se abre y se cierra aquí igual que en el panel.
            TrackUndoInteraction();

            if (_def.planMode == RoomDefinition.PlanMode.Manual) DrawManualContourHandles();
            DrawPlatformHandles();
            DrawMarkerHandles();
            DrawLightHandles();
        }

        /// <summary>
        /// Los puntos de cada plataforma, arrastrables, con la silueta dibujada por arriba y por
        /// abajo y una línea vertical uniendo cada punto con su altura.
        ///
        /// Se dibujan las dos siluetas y no solo los puntos porque una plataforma es lo que hay
        /// ENTRE ellos: con bolas sueltas no se ve la forma que se está haciendo, que es justo lo
        /// que se autora aquí.
        ///
        /// Los tiradores van a ras de suelo, no a su altura: arrastrar en planta y arrastrar en
        /// vertical con el mismo gesto se pelean, y la planta es lo que hay que ver contra las
        /// paredes. La altura se teclea en el panel.
        /// </summary>
        private void DrawPlatformHandles()
        {
            var plats = _def.platforms;
            if (plats == null || plats.Length == 0) return;

            var contour = _def.InnerContour();
            if (contour == null || contour.Length < 3) return;

            var tf = _preview.transform;
            float minCeil = _def.MinCeilingOver(contour);
            bool changed = false;

            for (int p = 0; p < plats.Length; p++)
            {
                var plat = plats[p];
                if (plat == null || plat.points == null || plat.points.Length < 3) continue;

                float baseY = _def.StoreyBaseY(plat.level, minCeil);
                int n = plat.points.Length;

                var low = new Vector3[n + 1];
                var high = new Vector3[n + 1];
                for (int i = 0; i < n; i++)
                {
                    Vector2 q = plat.points[i].position;
                    low[i] = tf.TransformPoint(new Vector3(q.x, baseY, q.y));
                    high[i] = tf.TransformPoint(
                        new Vector3(q.x, baseY + Mathf.Max(0f, plat.points[i].height), q.y));
                }
                low[n] = low[0];
                high[n] = high[0];

                Handles.color = new Color(0.55f, 0.9f, 0.55f);
                Handles.DrawAAPolyLine(2f, high);
                Handles.color = new Color(0.35f, 0.6f, 0.35f);
                Handles.DrawAAPolyLine(1.5f, low);
                for (int i = 0; i < n; i++) Handles.DrawAAPolyLine(1f, low[i], high[i]);

                for (int i = 0; i < n; i++)
                {
                    Handles.color = new Color(0.55f, 0.9f, 0.55f);
                    Vector2 q = plat.points[i].position;
                    if (DragPoint(tf, ref q, low[i]))
                    {
                        plat.points[i].position = q;
                        changed = true;
                    }
                }

                Handles.Label(high[0], $"platform {p}");
            }

            if (changed) { RebuildIfLive(); Repaint(); }
        }

        /// <summary>Un punto XZ del modelo, arrastrable en mundo. El alto lo pone quien llama: se
        /// arrastra sobre el plano del suelo, no en el aire.</summary>
        private static bool DragPoint(Transform tf, ref Vector2 point, Vector3 world)
        {
            float size = HandleUtility.GetHandleSize(world);
            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.FreeMoveHandle(world, size * 0.08f, Vector3.zero,
                Handles.SphereHandleCap);
            if (!EditorGUI.EndChangeCheck()) return false;

            Vector3 local = tf.InverseTransformPoint(moved);
            point = new Vector2(local.x, local.z);
            return true;
        }

        private void DrawManualContourHandles()
        {
            var pts = _def.manualContour;
            if (pts == null || pts.Length == 0) return;

            var tf = _preview.transform;
            bool changed = false;
            int deleteAt = -1, insertAfter = -1;

            for (int i = 0; i < pts.Length; i++)
            {
                Vector3 world = tf.TransformPoint(new Vector3(pts[i].x, 0f, pts[i].y));
                float size = HandleUtility.GetHandleSize(world);

                Handles.color = Color.yellow;
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.FreeMoveHandle(world, size * 0.08f, Vector3.zero, Handles.SphereHandleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    Vector3 local = tf.InverseTransformPoint(moved);
                    pts[i] = new Vector2(local.x, local.z);
                    changed = true;
                }
                Handles.Label(world + Vector3.up * (size * 0.3f), i.ToString());

                // Cubito rojo para borrar, con un mínimo de 3 puntos: menos de eso ya no es una
                // planta, y ProceduralContour degradaría en silencio a un resultado que nadie pidió.
                if (pts.Length > 3)
                {
                    Handles.color = Color.red;
                    Vector3 delPos = world + Vector3.up * (size * 0.55f);
                    if (Handles.Button(delPos, Quaternion.identity, size * 0.1f, size * 0.15f, Handles.CubeHandleCap))
                        deleteAt = i;
                }
            }

            // Puntos amarillos a media arista: insertar. Se dibujan en una segunda pasada para
            // no mezclar sus índices con los de borrado de la primera.
            Handles.color = new Color(1f, 0.85f, 0.2f, 0.85f);
            for (int i = 0; i < pts.Length; i++)
            {
                Vector2 mid = (pts[i] + pts[(i + 1) % pts.Length]) * 0.5f;
                Vector3 world = tf.TransformPoint(new Vector3(mid.x, 0f, mid.y));
                float size = HandleUtility.GetHandleSize(world);
                if (Handles.Button(world, Quaternion.identity, size * 0.05f, size * 0.1f, Handles.DotHandleCap))
                    insertAfter = i;
            }

            if (deleteAt >= 0)
            {
                var list = new List<Vector2>(pts);
                list.RemoveAt(deleteAt);
                _def.manualContour = list.ToArray();
                changed = true;
            }
            else if (insertAfter >= 0)
            {
                var list = new List<Vector2>(pts);
                list.Insert(insertAfter + 1, (pts[insertAfter] + pts[(insertAfter + 1) % pts.Length]) * 0.5f);
                _def.manualContour = list.ToArray();
                changed = true;
            }

            if (changed)
            {
                RebuildIfLive();
                Repaint();
            }
        }

        private static readonly Color LightMarkerColor = new Color(1f, 0.9f, 0.3f);
        private static readonly Color PropMarkerColor = new Color(0.3f, 0.7f, 1f);
        private static readonly Color SpawnMarkerColor = new Color(0.3f, 1f, 0.4f);

        /// <summary>Tiradores de los marcadores: arrastrables como los del contorno manual, con
        /// un cubito rojo para borrar. No dependen de <c>planMode</c> — un marcador tiene
        /// sentido en cualquier planta.</summary>
        private void DrawMarkerHandles()
        {
            var markers = _def.markers;
            if (markers == null || markers.Length == 0) return;

            var tf = _preview.transform;
            bool changed = false;
            int deleteAt = -1;

            for (int i = 0; i < markers.Length; i++)
            {
                var m = markers[i];
                if (m == null) continue;
                // El tirador vive donde el marcador nacera de verdad: sobre el suelo de SU piso.
                // Al arrastrar se guarda la Y RELATIVA a ese suelo, que es lo que edita el campo.
                float floorY = StoreyBase(m.level);
                Vector3 world = tf.TransformPoint(new Vector3(m.position.x, floorY + m.y, m.position.y));
                float size = HandleUtility.GetHandleSize(world);

                Handles.color = m.kind == RoomDefinition.MarkerKind.Light ? LightMarkerColor
                    : m.kind == RoomDefinition.MarkerKind.Prop ? PropMarkerColor : SpawnMarkerColor;
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.FreeMoveHandle(world, size * 0.1f, Vector3.zero, Handles.SphereHandleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    Vector3 local = tf.InverseTransformPoint(moved);
                    m.position = new Vector2(local.x, local.z);
                    m.y = local.y - floorY;
                    changed = true;
                }
                Handles.Label(world + Vector3.up * (size * 0.35f),
                    m.kind == RoomDefinition.MarkerKind.Light ? $"{i}: Light" : $"{i}: {m.kind} {m.tag}");

                Handles.color = Color.red;
                Vector3 delPos = world + Vector3.up * (size * 0.6f);
                if (Handles.Button(delPos, Quaternion.identity, size * 0.1f, size * 0.15f, Handles.CubeHandleCap))
                    deleteAt = i;
            }

            if (deleteAt >= 0)
            {
                ArrayUtility.RemoveAt(ref _def.markers, deleteAt);
                changed = true;
            }
            if (changed)
            {
                RebuildIfLive();
                Repaint();
            }
        }

        /// <summary>Crea el objeto de previsualización si no existe y (re)genera su malla.</summary>
        private void CreateOrRebuild()
        {
            if (_preview == null)
            {
                _preview = new GameObject(PreviewName);
                // SIN registrar en el deshacer de Unity, a propósito. Estaba, y era el bug: como
                // los mandos de la ventana no registraban nada, la creación de este objeto era el
                // ÚNICO paso de la pila, así que el primer Ctrl+Z borraba la sala entera en vez de
                // deshacer lo último que habías tocado. El historial de la herramienta lo lleva
                // ahora RoomAuthoringWindow.Undo.cs.
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
            // En el mismo latido que la malla: es lo que hace que un prop siga a su marcador
            // mientras se arrastra, sin pulsar nada.
            RebuildPropPreviews();
            RebuildLightPreviews();
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

            // ADR-083 enmienda 1: el manifiesto que lee el backend sale del horneado, nunca a mano.
            // Va aquí y no en un paso aparte porque un pool y un manifiesto desparejados son
            // exactamente el fallo que el digest del handshake existe para cazar — y cazarlo en el
            // handshake es tarde. Si falla, se avisa y el horneado sigue siendo válido: la sala está
            // guardada, lo único que falta es reexportar (Backrooms ▸ Export Room Manifest).
            if (!RoomManifestExporter.Export(out string manifestMessage))
                Debug.LogWarning($"[RoomAuthoringWindow] Manifiesto NO exportado — {manifestMessage}");

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
