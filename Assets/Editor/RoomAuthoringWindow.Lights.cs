#if UNITY_EDITOR
// Partición de RoomAuthoringWindow: la VISTA PREVIA en vivo de las luces. El resto vive en
// RoomAuthoringWindow.cs. Mismo patrón que RoomAuthoringWindow.Props.cs: los objetos NO se
// destruyen y recrean en cada Rebuild() —Rebuild() corre en CUALQUIER cambio de CUALQUIER
// pestaña, así que hacerlo destruiría y recrearía las Light en cada tecla que se toque en toda
// la ventana, y una Light recreada cada frame parpadea de verdad en la vista de escena (efecto
// visible, no solo desperdicio de CPU). Se reusa la instancia y solo se reposiciona/retiñe;
// recrear solo si cambia CUÁNTAS luces hay.
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.Gameplay.World;

namespace BackroomsSurvival.EditorTools
{
    public sealed partial class RoomAuthoringWindow
    {
        /// <summary>Igual que <see cref="PropsRootName"/>: raíz aparte y no colgada de la sala,
        /// para que estos objetos de SOLO preview nunca se cuelen en lo que se hornea (el
        /// horneado manual guarda el root entero y recoge sus componentes).</summary>
        private const string LightsRootName = "Room_Preview_Lights";

        private GameObject _lightsRoot;
        private bool _showLights = true;
        private readonly List<Light> _lightInstances = new List<Light>();
        /// <summary>El difusor visible de cada luz, HERMANO de su Light y no hijo — mismo motivo
        /// que en <c>BackroomsLighting.PlaceFluorescentLights</c>: el difusor tiene escala no
        /// uniforme (panel plano) y un hijo posicionado ahí vería su propio offset dividido por
        /// esa escala.</summary>
        private readonly List<MeshRenderer> _luminaireInstances = new List<MeshRenderer>();

        private void DrawLightsToolbar()
        {
            EditorGUI.BeginChangeCheck();
            _showLights = GUILayout.Toggle(_showLights, new GUIContent("Lights",
                    "Enseña las luces reales de la sala, iluminando la vista de escena mientras se "
                    + "edita — con parpadeo en vivo si el comportamiento es Flicker. Solo se ve: "
                    + "no cambia lo que se hornea."),
                EditorStyles.toolbarButton, GUILayout.Width(52));
            if (EditorGUI.EndChangeCheck())
                RebuildLightPreviews();
        }

        /// <summary>
        /// Pone al día las luces de la vista previa. Se llama desde <see cref="Rebuild"/>, en el
        /// mismo latido que la malla — arrastrar un slider de intensidad se VE cambiar, no hace
        /// falta pulsar nada. Recrea las instancias SOLO si el recuento ha cambiado (se ha
        /// añadido/borrado una luz); mover o retocar las que ya hay las reposiciona en el sitio,
        /// igual que <see cref="RebuildPropPreviews"/> con sus props.
        /// </summary>
        private void RebuildLightPreviews()
        {
            if (_preview == null || !_showLights || _def.lights == null || _def.lights.Length == 0)
            {
                ClearLightPreviews();
                return;
            }

            EnsureLightsRoot();
            if (_lightInstances.Count != _def.lights.Length)
            {
                ClearLightInstancesOnly();
                // PropCatalog() puede no resolver nada (proyecto sin ningún LayerVisualConfig en
                // Resources) — a diferencia de RebuildPropPreviews, aquí no hay ningún prop
                // resuelto que lo garantice de antemano, así que el null hay que guardarlo a mano.
                var cfg = PropCatalog();
                var lampMat = cfg != null ? LayerVisualMaterials.Build(cfg).lamp : null;
                for (int i = 0; i < _def.lights.Length; i++)
                {
                    var go = new GameObject($"Light_{i}");
                    go.transform.SetParent(_lightsRoot.transform, false);
                    MarkDontSave(go);
                    var light = go.AddComponent<Light>();
                    light.type = LightType.Point;
                    _lightInstances.Add(light);

                    var luminaire = CreateLuminaireMesh(_lightsRoot.transform, lampMat);
                    MarkDontSave(luminaire.gameObject);
                    _luminaireInstances.Add(luminaire);
                }
            }

            float minCeil = _def.MinCeilingOver(_def.InnerContour());
            for (int i = 0; i < _lightInstances.Count; i++)
            {
                var l = _def.lights[i];
                var light = _lightInstances[i];
                if (l == null || light == null) continue;

                var pos = new Vector3(l.position.x, _def.StoreyBaseY(l.level, minCeil) + l.y, l.position.y);
                light.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, l.yawDegrees, 0f));
                light.color = l.lightColor;
                light.range = l.lightRange;
                // Una luz Dead se ve APAGADA en el preview, no se esconde el marcador entero — es
                // la misma información que "Behavior: Dead" en la lista, pero VISTA. Flicker se
                // anima en TickFlickerPreviews (EditorApplication.update, ver más abajo) — aquí
                // solo se deja fijada la intensidad base de la que parte la onda.
                light.intensity = l.behavior == RoomDefinition.LightBehavior.Dead ? 0f : l.lightIntensity;

                // El difusor: mismo sitio que la Light (sin el offset "cuelga por debajo del
                // panel" del pasillo — aquí Joel coloca la Light donde quiere y el panel la seña
                // en vez de imponerle un hueco propio que autorar). Apagado en Dead, igual que
                // hace BackroomsLighting con una lámpara rota; el parpadeo NO le toca la emisión
                // — el pasillo tampoco lo hace en su ciclo normal, solo al fundirse.
                var luminaire = _luminaireInstances[i];
                if (luminaire == null) continue;
                luminaire.transform.position = pos;
                bool dead = l.behavior == RoomDefinition.LightBehavior.Dead;
                SetLuminaireEmission(luminaire, l.lightColor.linear * (dead ? 0.04f : LuminaireEmissionScale));
            }
        }

        private void EnsureLightsRoot()
        {
            if (_lightsRoot != null) return;
            _lightsRoot = new GameObject(LightsRootName);
            MarkDontSave(_lightsRoot);
        }

        private void ClearLightInstancesOnly()
        {
            foreach (var light in _lightInstances)
                if (light != null) DestroyImmediate(light.gameObject);
            _lightInstances.Clear();
            foreach (var luminaire in _luminaireInstances)
                if (luminaire != null) DestroyImmediate(luminaire.gameObject);
            _luminaireInstances.Clear();
        }

        private void ClearLightPreviews()
        {
            _lightInstances.Clear();
            _luminaireInstances.Clear();
            if (_lightsRoot != null) DestroyImmediate(_lightsRoot);
            _lightsRoot = null;
        }

        /// <summary>El difusor visible de una luz: el mismo panel emisivo plano que
        /// <c>BackroomsLighting.MakeLuminaire</c> pone en el pasillo — sin él una Light autorada
        /// era una bombilla invisible, la única luz del mundo sin cuerpo. Colisión quitada: es
        /// decorativo, y colisionar con el propio panel no aporta nada que las cajas de la sala
        /// no den ya.</summary>
        private static MeshRenderer CreateLuminaireMesh(Transform parent, Material lampMat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Luminaire";
            if (go.TryGetComponent<Collider>(out var col)) DestroyImmediate(col);
            go.transform.SetParent(parent, false);
            go.transform.localScale = new Vector3(1.65f, 0.08f, 1.65f); // panel plano de techo

            var r = go.GetComponent<MeshRenderer>();
            if (lampMat != null) r.sharedMaterial = lampMat;
            return r;
        }

        /// <summary>Mismo valor que el <c>lampEmission</c> por defecto de <c>LayerVisualConfig</c>
        /// — el panel no tiene su propio campo de emisión autorado (sería un campo nuevo solo
        /// para el brillo cosmético del difusor, y no lo pidió nadie); se fija a la constante que
        /// ya usa el resto del mundo cuando esa capa no la sobreescribe.</summary>
        private const float LuminaireEmissionScale = 1.3f;

        private static void SetLuminaireEmission(MeshRenderer r, Color emission)
        {
            _luminaireMpb ??= new MaterialPropertyBlock();
            _luminaireMpb.Clear();
            _luminaireMpb.SetColor(LayerVisualMaterials.EmissionColorId, emission);
            r.SetPropertyBlock(_luminaireMpb);
        }

        private static MaterialPropertyBlock _luminaireMpb;

        /// <summary>La capa por defecto para el difusor de una luz HORNEADA — a diferencia del
        /// preview (que reusa <see cref="PropCatalog"/>, ya resuelto e interactivo), esto corre
        /// en <c>CreateLights</c>, un método estático llamado también desde fuera de la ventana
        /// (<c>RoomManifestExporter.RebakeSealedRooms</c>, el creador de salas multi-chunk) —
        /// sitios sin ventana ni catálogo elegido a mano. Misma resolución que
        /// <see cref="PropCatalog"/>, sin caché de instancia porque un horneado es un evento
        /// suelto, no algo que se repita 60 veces por segundo.</summary>
        private static LayerVisualConfig DefaultLayerVisualConfig()
        {
            var cfg = Resources.Load<LayerVisualConfig>("LayerVisuals/Layer0_Vestibulo");
            if (cfg != null) return cfg;
            var all = Resources.LoadAll<LayerVisualConfig>("LayerVisuals");
            return all != null && all.Length > 0 ? all[0] : null;
        }

        /// <summary>
        /// Parpadeo EN VIVO en el editor. <c>BackroomsLighting.LampFlicker</c> —el mismo
        /// comportamiento que se hornea con la sala— es un <c>MonoBehaviour.Update()</c> normal,
        /// SIN <c>[ExecuteAlways]</c>: fuera de Play Mode Unity nunca lo llama, así que añadir el
        /// componente al preview no habría animado nada. En vez de eso se reusa su función de
        /// onda, pura y estática (<see cref="LampFlicker.FlickerWave"/>), empujada a mano desde
        /// <see cref="EditorApplication.update"/> — que SÍ tickea con la ventana abierta, con o
        /// sin foco (confirmado por los runners de disparo del proyecto).
        ///
        /// Se engancha en <see cref="OnEnable"/>/se desengancha en <see cref="OnDisable"/> igual
        /// que <c>SceneView.duringSceneGui</c>: si no, el delegado sigue vivo tras cerrar la
        /// ventana y sigue tocando luces que ya no existen.
        /// </summary>
        private void TickFlickerPreviews()
        {
            if (_lightInstances.Count == 0 || _def.lights == null) return;
            bool any = false;
            double t = EditorApplication.timeSinceStartup;
            for (int i = 0; i < _lightInstances.Count && i < _def.lights.Length; i++)
            {
                var l = _def.lights[i];
                var light = _lightInstances[i];
                if (l == null || light == null || l.behavior != RoomDefinition.LightBehavior.Flicker) continue;
                any = true;
                float phase = i * 0.37f; // MISMO desfase que CreateLights fija al hornear.
                float wave = LampFlicker.FlickerWave((float)t, phase, l.flickerHz);
                light.intensity = Mathf.Lerp(l.lightIntensity * 0.3f, l.lightIntensity, wave);
            }
            if (any) SceneView.RepaintAll();
        }

        /// <summary>
        /// Tiradores de las luces en la vista de escena — mismo gesto que
        /// <c>DrawMarkerHandles</c>: arrastrable, con un cubito rojo para borrar. Se pierde si no
        /// se porta: los marcadores de tipo Light YA eran arrastrables antes de que las luces
        /// tuvieran grupo propio, así que quitárselo sería una regresión, no una limpieza.
        /// </summary>
        private void DrawLightHandles()
        {
            var lights = _def.lights;
            if (lights == null || lights.Length == 0) return;

            var tf = _preview.transform;
            bool changed = false;
            int deleteAt = -1;

            for (int i = 0; i < lights.Length; i++)
            {
                var l = lights[i];
                if (l == null) continue;
                // El tirador vive donde la luz nacerá de verdad: sobre el suelo de SU piso. Al
                // arrastrar se guarda la Y RELATIVA a ese suelo, que es lo que edita el campo.
                float floorY = StoreyBase(l.level);
                Vector3 world = tf.TransformPoint(new Vector3(l.position.x, floorY + l.y, l.position.y));
                float size = HandleUtility.GetHandleSize(world);

                Handles.color = LightMarkerColor;
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.FreeMoveHandle(world, size * 0.1f, Vector3.zero, Handles.SphereHandleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    Vector3 local = tf.InverseTransformPoint(moved);
                    l.position = new Vector2(local.x, local.z);
                    l.y = local.y - floorY;
                    changed = true;
                }
                Handles.Label(world + Vector3.up * (size * 0.35f), $"{i}: Light ({l.behavior})");

                Handles.color = Color.red;
                Vector3 delPos = world + Vector3.up * (size * 0.6f);
                if (Handles.Button(delPos, Quaternion.identity, size * 0.1f, size * 0.15f, Handles.CubeHandleCap))
                    deleteAt = i;
            }

            if (deleteAt >= 0)
            {
                ArrayUtility.RemoveAt(ref _def.lights, deleteAt);
                changed = true;
            }
            if (changed)
            {
                RebuildIfLive();
                Repaint();
            }
        }

        // ── generador: mismas luces que el pasillo ──────────────────────────
        //
        // Pide expresamente Joel: "quiero que se visualicen las mismas luces que en el
        // generador" — las fluorescentes automáticas que BackroomsLighting.PlaceFluorescentLights
        // reparte por el laberinto, no las que se ponen a mano. En vez de que el pasillo intente
        // iluminar el INTERIOR de la sala en runtime (que no sabe nada de su techo real — altura,
        // inclinación, entreplantas — y le dejaría una luz flotando a la altura estándar del
        // chunk dentro de una sala de 12 m), la sala se autora ya con sus propias luces, hechas
        // con el MISMO modelo estadístico y el mismo LayerVisualConfig que el pasillo, PERO contra
        // el techo de verdad de la sala. A partir de ahí son luces del grupo Lights como cualquier
        // otra: se editan, se borran, se les cambia el comportamiento.

        private int _lightGenSeed = 1;

        private void DrawGenerateLightsSection()
        {
            if (!Section("secGenLights", "Generate Lights (from Layer)", openByDefault: false)) return;

            using (new EditorGUI.IndentLevelScope())
            {
                var cfg = PropCatalog();
                EditorGUILayout.HelpBox(
                    "Rellena el grupo Lights con el MISMO modelo que usa el pasillo para sus "
                    + "fluorescentes (densidad, % roto, % parpadeo, color/alcance) tomado de "
                    + $"\"{(cfg != null ? cfg.name : "(sin capa)")}\" (mismo catálogo que Props, "
                    + "arriba a la izquierda), pero contra el techo REAL de esta sala — altura, "
                    + "inclinación y cada entreplanta. Generar REEMPLAZA todas las luces.",
                    MessageType.None);

                using (new EditorGUI.DisabledScope(cfg == null))
                using (new EditorGUILayout.HorizontalScope())
                {
                    _lightGenSeed = EditorGUILayout.IntField(new GUIContent("Seed",
                        "Misma semilla, mismo patrón de rotas/parpadeantes."), _lightGenSeed);
                    if (GUILayout.Button("Roll", GUILayout.Width(50)))
                        _lightGenSeed = UnityEngine.Random.Range(1, 999999);
                    if (GUILayout.Button("Generate", GUILayout.Width(75)))
                    {
                        if (_def.lights.Length == 0 || EditorUtility.DisplayDialog(
                                "Replace the lights?",
                                $"Esta sala tiene {_def.lights.Length} luz(ces). Generar las borra todas.",
                                "Generate", "Cancel"))
                            GenerateLightsFromLayer(cfg, _lightGenSeed);
                    }
                }
            }
        }

        /// <summary>Mismo criterio por-tile de <c>BackroomsLighting.PlaceFluorescentLights</c>:
        /// una tirada de <c>lightDensity</c> por tile de 5 m, y de las que salen, una fracción
        /// <c>brokenLampChance</c> apagada (Dead) y, de las que quedan, un 30 % parpadeando —
        /// EXACTAMENTE la constante <c>BackroomsLighting.FlickerChance</c>, copiada aquí porque es
        /// `private const` y autorar no debe depender de un cambio de visibilidad en el sistema
        /// del laberinto para seguir siendo fiel a él.
        ///
        /// Recorre cada piso (planta baja + cada <see cref="RoomDefinition.Level"/>) por SU propia
        /// rejilla de tiles, con la luz al techo de SU piso — <c>StoreyCeilingY</c> para un piso
        /// intermedio (losa plana), <c>CeilingYAt(punto)</c> para el de arriba (que sí puede
        /// inclinarse). Mismo margen bajo el panel que usa el laberinto (0,3 m).</summary>
        private const double GeneratedFlickerChance = 0.30;
        private const float GeneratedLightDropBelowCeiling = 0.3f;

        private void GenerateLightsFromLayer(LayerVisualConfig cfg, int seed)
        {
            if (cfg == null) return;
            var rng = new System.Random(seed);
            var inner = _def.InnerContour();
            if (inner == null || inner.Length < 3) return;

            Vector2 min = inner[0], max = inner[0];
            foreach (var p in inner) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }

            float minCeil = _def.MinCeilingOver(inner);
            int storeys = _def.levels?.Length ?? 0;
            var newLights = new List<RoomDefinition.Light>();

            for (int storey = 0; storey <= storeys && newLights.Count < MaxPerGroup; storey++)
            {
                float floorY = _def.StoreyBaseY(storey, minCeil);
                bool topStorey = storey == storeys;

                int tx = Mathf.Max(1, Mathf.RoundToInt((max.x - min.x) / GridVisualConstants.TileSize));
                int tz = Mathf.Max(1, Mathf.RoundToInt((max.y - min.y) / GridVisualConstants.TileSize));
                for (int iz = 0; iz < tz && newLights.Count < MaxPerGroup; iz++)
                    for (int ix = 0; ix < tx && newLights.Count < MaxPerGroup; ix++)
                    {
                        if (rng.NextDouble() >= cfg.lightDensity) continue;

                        var p = new Vector2(
                            min.x + (ix + 0.5f) * GridVisualConstants.TileSize,
                            min.y + (iz + 0.5f) * GridVisualConstants.TileSize);
                        // Mismo margen que separa un pilar de la pared (InsideWithClearance ya
                        // existe para esto) — una luz pegada a la pared no se distingue de una
                        // rota por el modelo, pero SÍ se ve mal empotrada en el muro.
                        if (!InsideWithClearance(inner, p, 0.5f)) continue;

                        float ceilY = topStorey
                            ? _def.CeilingYAt(p)
                            : _def.StoreyCeilingY(storey, minCeil);

                        bool broken = rng.NextDouble() < cfg.brokenLampChance;
                        bool flickering = !broken && rng.NextDouble() < GeneratedFlickerChance;

                        newLights.Add(new RoomDefinition.Light
                        {
                            position = p,
                            y = (ceilY - GeneratedLightDropBelowCeiling) - floorY,
                            level = storey,
                            lightColor = cfg.lampColor,
                            lightIntensity = cfg.lampIntensity,
                            lightRange = cfg.lampRange,
                            behavior = broken ? RoomDefinition.LightBehavior.Dead
                                : flickering ? RoomDefinition.LightBehavior.Flicker
                                : RoomDefinition.LightBehavior.Steady,
                        });
                    }
            }

            _def.lights = newLights.ToArray();
            RebuildIfLive();
            Debug.Log($"[RoomAuthoringWindow] Generated {newLights.Count} light(s) from " +
                      $"'{cfg.name}' (seed {seed}).");
        }
    }
}
#endif
