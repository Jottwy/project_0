#if UNITY_EDITOR
// Partición de RoomAuthoringWindow: la VISTA PREVIA de los props. El resto vive en
// RoomAuthoringWindow.cs.
using System.Collections.Generic;
using System.Text;
using BackroomsSurvival.Gameplay.GridWorld;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    public sealed partial class RoomAuthoringWindow
    {
        /// <summary>
        /// Los marcadores de tipo Prop dejan de ser un punto y pasan a VERSE: si su etiqueta casa
        /// con algo del catálogo de props del mundo, ahí aparece el mueble, en vivo mientras se
        /// arrastra. Antes la etiqueta era texto a ciegas — escribías "desk" y no sabías si el
        /// mundo iba a poner un escritorio, ni de qué tamaño, ni si cabía donde lo habías puesto.
        ///
        /// SOLO previsualización: estos objetos no se hornean con la sala. Cuelgan de una raíz
        /// APARTE y no del root de la sala, y ese detalle no es cosmético — el horneado manual
        /// guarda el root entero como prefab y recoge sus BoxCollider para el proxy de colisión,
        /// así que un prop colgando de ahí acabaría dentro de la sala horneada y bloqueando el
        /// paso. Lo que se hornea de un marcador sigue siendo lo de siempre: un
        /// <see cref="RoomMarker"/> con su etiqueta, para que la resuelva quien toque.
        /// </summary>
        private const string PropsRootName = "Room_Preview_Props";

        private GameObject _propsRoot;

        /// <summary>De dónde salen los props. Es un <see cref="LayerVisualConfig"/> y no un
        /// catálogo propio para que lo que se ve aquí sea EXACTAMENTE lo que el mundo pone en esa
        /// capa: un catálogo aparte se desincronizaría del real sin avisar.</summary>
        private LayerVisualConfig _propCatalog;
        private bool _propCatalogSearched;

        private bool _showProps = true;

        // Firma del conjunto de props ya instanciado (etiquetas, en orden). Reconstruir
        // instanciando prefabs corre en CADA pulsación de un slider, y eso es instanciar y
        // destruir docenas de objetos por segundo mientras se arrastra. Si la firma no ha
        // cambiado, los de antes valen y solo hay que MOVERLOS.
        private string _propsSignature;
        private readonly List<GameObject> _propInstances = new List<GameObject>();

        /// <summary>El catálogo elegido, o el primero que haya en Resources la primera vez.</summary>
        private LayerVisualConfig PropCatalog()
        {
            if (_propCatalog != null || _propCatalogSearched) return _propCatalog;
            _propCatalogSearched = true;

            // El de la capa 0 por defecto: es la capa que se autora, y elegir "el primero que
            // devuelva el buscador" haría que el mismo proyecto se viera distinto según en qué
            // orden estén los assets.
            _propCatalog = Resources.Load<LayerVisualConfig>("LayerVisuals/Layer0_Vestibulo");
            if (_propCatalog != null) return _propCatalog;

            var all = Resources.LoadAll<LayerVisualConfig>("LayerVisuals");
            if (all != null && all.Length > 0) _propCatalog = all[0];
            return _propCatalog;
        }

        /// <summary>Los mandos de la vista previa, en la barra de herramientas: es un ajuste de
        /// lo que se VE, no de la sala, y por eso no vive en ninguna pestaña.</summary>
        private void DrawPropsToolbar()
        {
            EditorGUI.BeginChangeCheck();
            _showProps = GUILayout.Toggle(_showProps, new GUIContent("Props",
                    "Enseña el mueble real de cada marcador Prop cuya etiqueta case con el "
                    + "catálogo. Solo se ve: no se hornea con la sala."),
                EditorStyles.toolbarButton, GUILayout.Width(52));

            var next = (LayerVisualConfig)EditorGUILayout.ObjectField(
                PropCatalog(), typeof(LayerVisualConfig), false, GUILayout.Width(130));
            if (EditorGUI.EndChangeCheck())
            {
                _propCatalog = next;
                _propCatalogSearched = true;
                _propsSignature = null; // catálogo distinto: hay que resolver otra vez
                RebuildPropPreviews();
            }
        }

        /// <summary>
        /// Pone al día los props de la vista previa. Se llama desde <see cref="Rebuild"/>, así que
        /// va en el mismo latido que la malla y no hace falta pulsar nada.
        /// </summary>
        private void RebuildPropPreviews()
        {
            if (_preview == null || !_showProps)
            {
                ClearPropPreviews();
                return;
            }

            // Los marcadores Prop con etiqueta que resuelve, en orden. Los que no resuelven se
            // quedan como el punto de siempre: no se inventa un cubo, porque un cubo gris diría
            // "esto es un prop" cuando lo que pasa es que esa etiqueta no existe en el catálogo.
            var resolved = new List<(RoomDefinition.Marker marker, PropEntry entry)>();
            if (_def.markers != null)
                foreach (var m in _def.markers)
                {
                    if (m == null || m.kind != RoomDefinition.MarkerKind.Prop) continue;
                    if (!TryResolveProp(m.tag, out var entry)) continue;
                    resolved.Add((m, entry));
                }

            if (resolved.Count == 0)
            {
                ClearPropPreviews();
                return;
            }

            var sig = new StringBuilder();
            foreach (var (m, _) in resolved) sig.Append(m.tag).Append('|');
            string signature = sig.ToString();

            if (signature != _propsSignature || _propInstances.Count != resolved.Count)
            {
                ClearPropPreviews();
                EnsurePropsRoot();

                var mats = LayerVisualMaterials.Build(PropCatalog());
                foreach (var (_, entry) in resolved)
                {
                    GameObject go = entry.prefab != null
                        ? (GameObject)PrefabUtility.InstantiatePrefab(entry.prefab, _propsRoot.transform)
                        : PlaceholderFactory.Create(entry.placeholderType, mats.prop, 0.5f);
                    if (go == null) continue;

                    if (go.transform.parent != _propsRoot.transform)
                        go.transform.SetParent(_propsRoot.transform, false);

                    // DontSave en cada hijo, no solo en la raíz: el flag no se hereda, y un prop
                    // sin él se guardaría en la escena al pulsar Ctrl+S y quedaría ahí suelto.
                    MarkDontSave(go);
                    _propInstances.Add(go);
                }
                _propsSignature = signature;
            }

            // Colocar SIEMPRE, hayan sido creados ahora o no: es lo que hace que arrastrar la
            // altura de un piso mueva sus props con él sin volver a instanciar nada.
            float minCeil = _def.MinCeilingOver(_def.InnerContour());
            for (int i = 0; i < _propInstances.Count && i < resolved.Count; i++)
            {
                var m = resolved[i].marker;
                if (_propInstances[i] == null) continue;
                float y = _def.StoreyBaseY(m.level, minCeil) + m.y;
                _propInstances[i].transform.SetPositionAndRotation(
                    new Vector3(m.position.x, y, m.position.y),
                    Quaternion.Euler(0f, m.yawDegrees, 0f));
            }
        }

        /// <summary>
        /// ¿Hay algo en el catálogo que responda a esta etiqueta? Se prueba primero por
        /// <c>placeholderType</c> —el vocabulario que el mundo ya usa: desk, chair, cabinet…— y
        /// después por el NOMBRE del prefab, para que una entrada con modelo propio responda a su
        /// nombre sin tener que inventarle un tipo.
        ///
        /// Sin distinguir mayúsculas ni espacios de sobra: la etiqueta la escribe una persona a
        /// mano, y "Desk " fallando en silencio es exactamente la clase de pista que no se ve.
        /// </summary>
        private bool TryResolveProp(string label, out PropEntry entry)
        {
            entry = default;
            var cfg = PropCatalog();
            if (cfg == null || string.IsNullOrWhiteSpace(label)) return false;

            string want = label.Trim().ToLowerInvariant();

            foreach (var e in CatalogEntries(cfg))
                if (!string.IsNullOrEmpty(e.placeholderType)
                    && e.placeholderType.Trim().ToLowerInvariant() == want)
                {
                    entry = e;
                    return true;
                }

            foreach (var e in CatalogEntries(cfg))
                if (e.prefab != null && e.prefab.name.ToLowerInvariant() == want)
                {
                    entry = e;
                    return true;
                }

            // Y por último, un prefab etiquetado del proyecto, POR SU NOMBRE. Por el nombre y no
            // por el tag porque el tag es el filtro —"cuáles se ofrecen"—, no la identidad: veinte
            // prefabs con el tag "RoomProp" son veinte props distintos, y escribir "RoomProp" no
            // dice cuál de ellos quieres.
            foreach (var t in TaggedPrefabs())
                if (t.name.ToLowerInvariant() == want)
                {
                    entry = new PropEntry { prefab = t.prefab };
                    return true;
                }

            return false;
        }

        /// <summary>Todas las entradas del catálogo: las de la capa y las de cada zona. Las de
        /// zona cuentan porque un mueble que solo sale en OFFICE sigue siendo un mueble que se
        /// puede querer poner a mano en una sala autorada.</summary>
        private static IEnumerable<PropEntry> CatalogEntries(LayerVisualConfig cfg)
        {
            if (cfg.props != null)
                foreach (var e in cfg.props) yield return e;

            if (cfg.zonePropSets == null) yield break;
            foreach (var set in cfg.zonePropSets)
            {
                if (set.props == null) continue;
                foreach (var e in set.props) yield return e;
            }
        }

        /// <summary>
        /// Un prefab del proyecto que lleva un TAG de Unity en su raíz. La segunda fuente de
        /// props, además del catálogo del mundo: etiquetas un prefab con, digamos, "RoomProp" y
        /// aparece para elegir sin tener que darlo de alta en ningún sitio.
        /// </summary>
        private readonly struct TaggedPrefab
        {
            public readonly string tag, name;
            public readonly GameObject prefab;

            public TaggedPrefab(string tag, string name, GameObject prefab)
            {
                this.tag = tag; this.name = name; this.prefab = prefab;
            }
        }

        private List<TaggedPrefab> _taggedPrefabs;

        /// <summary>
        /// Los prefabs del proyecto con tag propio, cacheados. El barrido abre CADA prefab para
        /// leerle el tag a la raíz, así que no puede correr por frame — y este sí corre por frame
        /// a través de la resolución de cada marcador. Se hace una vez y se rehace a mano con
        /// "Rescan", que además es la única forma honesta: un prefab recién etiquetado no avisa.
        ///
        /// Solo la RAÍZ. Un hijo etiquetado no es un prop que se pueda colocar por su cuenta —
        /// colocarlo arrancaría un trozo suelto de otro objeto.
        /// </summary>
        private List<TaggedPrefab> TaggedPrefabs()
        {
            if (_taggedPrefabs != null) return _taggedPrefabs;
            _taggedPrefabs = new List<TaggedPrefab>();

            foreach (string guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;

                // CompareTag lanza si el tag ya no existe en el proyecto (pasa con prefabs de
                // vendor que traen tags de otro proyecto), así que se lee la cadena a pelo.
                string tag = go.tag;
                if (string.IsNullOrEmpty(tag) || tag == "Untagged") continue;

                _taggedPrefabs.Add(new TaggedPrefab(tag, go.name, go));
            }

            _taggedPrefabs.Sort((a, b) =>
            {
                int byTag = string.Compare(a.tag, b.tag, System.StringComparison.OrdinalIgnoreCase);
                return byTag != 0
                    ? byTag
                    : string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase);
            });
            return _taggedPrefabs;
        }

        private void RescanTaggedPrefabs()
        {
            _taggedPrefabs = null;
            _propsSignature = null; // puede resolver lo que antes no resolvía
            RebuildPropPreviews();
        }

        /// <summary>Las etiquetas que el catálogo sabe resolver, sin repetir y en orden. Es lo que
        /// convierte el campo Tag de "escribe a ciegas y a ver" en una lista para elegir.</summary>
        private List<string> KnownPropTags()
        {
            var seen = new List<string>();
            var cfg = PropCatalog();
            if (cfg == null) return seen;

            foreach (var e in CatalogEntries(cfg))
            {
                string name = !string.IsNullOrWhiteSpace(e.placeholderType)
                    ? e.placeholderType.Trim()
                    : e.prefab != null ? e.prefab.name : null;
                if (name != null && !seen.Contains(name)) seen.Add(name);
            }
            seen.Sort(System.StringComparer.OrdinalIgnoreCase);
            return seen;
        }

        /// <summary>¿Esta etiqueta pinta algo? Lo usa la lista para avisar de una que no resuelve
        /// — un prop que no aparece no es un error, pero saberlo ANTES de hornear sí importa.</summary>
        private bool PropTagResolves(string label) => TryResolveProp(label, out _);

        /// <summary>
        /// La fila del campo Tag: texto libre MÁS un desplegable con lo que el catálogo conoce.
        /// Libre y no solo desplegable a propósito: la etiqueta también la leen sistemas que no
        /// son el de props (aparición de jugadores, loot), así que "loot_common" tiene que poder
        /// escribirse aunque aquí no dibuje nada.
        /// </summary>
        private Row<RoomDefinition.Marker> PropTagRow() =>
            OneLine<RoomDefinition.Marker>((r, m) =>
            {
                const float pickW = 22f;
                var fieldRect = new Rect(r.x, r.y, r.width - pickW - 2f, r.height);
                bool unresolved = m.kind == RoomDefinition.MarkerKind.Prop
                    && !string.IsNullOrWhiteSpace(m.tag) && !PropTagResolves(m.tag);

                m.tag = EditorGUI.TextField(fieldRect, new GUIContent("Tag",
                    "Texto libre que resuelve un catálogo externo. Si casa con el catálogo de "
                    + "props, el mueble se ve ya en la escena; si no, sigue siendo una etiqueta "
                    + "válida para quien la use (aparición, loot...)."), m.tag);

                if (unresolved)
                {
                    // Un icono, no un HelpBox: la fila no puede cambiar de alto sin desbaratar la
                    // altura que el desplegable ya ha declarado.
                    var warn = EditorGUIUtility.IconContent("console.warnicon.sml");
                    warn.tooltip = "Esta etiqueta no está en el catálogo: no se verá ningún prop.";
                    EditorGUI.LabelField(new Rect(fieldRect.xMax - 18f, r.y, 18f, r.height), warn);
                }

                if (!GUI.Button(new Rect(r.xMax - pickW, r.y, pickW, r.height),
                        new GUIContent("▾", "Etiquetas que el catálogo sabe resolver"),
                        EditorStyles.miniButton))
                    return;

                var menu = new GenericMenu();
                var marker = m; // la lambda del menú corre DESPUÉS: la fila ya no está en pie

                void Pick(string value)
                {
                    marker.tag = value;
                    RebuildIfLive();
                }

                // Fuente 1: el catálogo del mundo, lo que de verdad amuebla esa capa.
                var tags = KnownPropTags();
                if (tags.Count == 0)
                    menu.AddDisabledItem(new GUIContent("Catalogue/(vacío o sin asignar)"));
                foreach (string t in tags)
                {
                    string captured = t; // sin esto, todas las entradas escribirían la última
                    menu.AddItem(new GUIContent($"Catalogue/{t}"),
                        string.Equals(m.tag, t, System.StringComparison.OrdinalIgnoreCase),
                        () => Pick(captured));
                }

                // Fuente 2: prefabs del proyecto con tag de Unity, agrupados POR TAG — que es lo
                // que hace navegable una lista de prefabs sueltos.
                menu.AddSeparator("");
                var tagged = TaggedPrefabs();
                if (tagged.Count == 0)
                {
                    menu.AddDisabledItem(new GUIContent("Tagged prefabs/(ningún prefab con tag propio)"));
                    menu.AddDisabledItem(new GUIContent(
                        "Tagged prefabs/Crea un tag y ponlo en la RAÍZ del prefab"));
                }
                foreach (var t in tagged)
                {
                    var captured = t;
                    menu.AddItem(new GUIContent($"Tagged prefabs/{t.tag}/{t.name}"),
                        string.Equals(m.tag, t.name, System.StringComparison.OrdinalIgnoreCase),
                        () => Pick(captured.name));
                }

                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Rescan project prefabs"), false, RescanTaggedPrefabs);
                menu.ShowAsContext();
            });

        private void EnsurePropsRoot()
        {
            if (_propsRoot != null) return;
            _propsRoot = new GameObject(PropsRootName);
            MarkDontSave(_propsRoot);
        }

        private static void MarkDontSave(GameObject go)
        {
            go.hideFlags = HideFlags.DontSave;
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                t.gameObject.hideFlags = HideFlags.DontSave;
        }

        private void ClearPropPreviews()
        {
            _propInstances.Clear();
            _propsSignature = null;
            if (_propsRoot != null) DestroyImmediate(_propsRoot);
            _propsRoot = null;
        }
    }
}
#endif
