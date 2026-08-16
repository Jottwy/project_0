using BackroomsSurvival.Net;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// PLACEHOLDER (2026-08-15) — la cara del reloj de muñeca del concepto. Vive colgada del nodo
    /// de la malla dentro de <c>BR_Wieldable_Watch</c> y se construye ENTERA en <c>Awake</c>: no
    /// se autora ni una línea del canvas por YAML (memoria <c>unity-yaml-hand-editing-hazard</c>:
    /// una línea en blanco de más se comió un asset entero y no dio error).
    ///
    /// LAYER: el canvas y TODOS sus hijos van a <see cref="ViewModelLayer"/>, la misma que la
    /// malla del wieldable (<c>CompassBase</c> ya viene en <c>m_Layer: 10</c>). La cámara de
    /// viewmodel del vendor es la única que dibuja esa layer y tiene su propio FOV
    /// (<c>CameraFOVHandler.ViewModelFOV</c>); un canvas dejado en <c>Default</c> sale recortado
    /// por el near clip de la cámara principal, o directamente no sale. Es el fallo que se lleva
    /// la primera tarde si no se sabe.
    ///
    /// CUATRO BARRAS LLEVAN DATO REAL — salud, hambre, sed y cordura, que son cuatro de los cinco
    /// campos que el backend manda en <c>PlayerStats</c> y que ya llegan interpolados. FATIGA es
    /// RELLENO VISUAL FIJO: el backend tiene <c>stamina</c>, pero decidir si "fatiga" ES esa
    /// stamina o una stat lenta aparte está sin resolver, y meter stamina bajo una etiqueta que
    /// quizá signifique otra cosa haría que el playtest juzgara un dato equivocado. Se pinta fija
    /// para poder juzgar la composición de cinco filas del concepto sin comprometer el dato.
    ///
    /// LA HORA TAMPOCO ES REAL: el backend no tiene reloj de mundo (<c>remaining_hours</c> es
    /// caducidad por item, otra cosa). Se deriva del tiempo de sesión arrancando en
    /// <see cref="ClockStartHour"/> para que la esquina del concepto no salga vacía.
    /// </summary>
    public sealed class WristWatchDisplay : MonoBehaviour
    {
        /// <summary>Índice de la layer "ViewModel" en TagManager.asset.</summary>
        public const int ViewModelLayer = 10;

        private const float ClockStartHour = 6f;

        [Header("Anclaje")]
        [Tooltip("Hueso del rig del que cuelga la cara. Se busca por nombre dentro del wieldable. " +
                 "Si no aparece, el log lista los nombres disponibles.")]
        public string anchorBoneName = "Hand.L";

        [Header("Colocación — se re-aplica cada frame, ajustable EN PLAY")]
        [Tooltip("Desplazamiento respecto al hueso, en metros.")]
        public Vector3 localOffset = new Vector3(-0.06f, -0.04f, 0f);

        [Tooltip("Rotación local, en grados.")]
        public Vector3 localEuler = Vector3.zero;

        [Tooltip("Tamaño físico de la cara, en metros (ancho, alto). Reloj EXAGERADO a propósito: " +
                 "el placeholder hereda la pose de la brújula, que deja la muñeca más lejos de la " +
                 "cara que el concepto. Con pose propia esto baja a ~0.04 (reloj real).")]
        public Vector2 faceSizeMeters = new Vector2(0.09f, 0.11f);

        [Header("Contenido")]
        [Tooltip("Valor fijo 0..100 de FATIGA hasta que se decida si es la stamina del backend.")]
        [Range(0f, 100f)] public float fatiguePlaceholder = 59f;

        // Unidades de diseño del canvas. La escala del transform las convierte a metros, así que
        // este número es solo la rejilla en la que se dibuja: subirlo NO agranda el reloj.
        private const float DesignWidth = 180f;

        // El alto de la rejilla sale de la proporción pedida en metros — así `faceSizeMeters.y`
        // hace algo. Antes era una constante y el campo Y del inspector no tenía ningún efecto:
        // se podía poner 0.07 y seguía dibujándose a 0.11.
        private float DesignHeight =>
            DesignWidth * Mathf.Clamp(faceSizeMeters.y / Mathf.Max(1e-4f, faceSizeMeters.x), 0.25f, 4f);

        // Maquetación en fracciones de la altura de la cara. Los valores son los de la rejilla
        // 180x220 que Joel validó en pantalla (reloj 42, fila 32), convertidos a proporción para
        // que sobrevivan a un cambio de forma.
        private const float ClockTopFraction = 0.03f;
        private const float ClockHeightFraction = 0.12f;
        private const float RowsTopFraction = 42f / 220f;
        private const float RowHeightFraction = 32f / 220f;

        private Canvas _canvas;
        private RectTransform _canvasRt;
        private Image[] _fills;
        private Text _clockText;

        // Orden y color copiados del concepto, de arriba abajo.
        private static readonly string[] RowLabels = { "HAMBRE", "SED", "CORDURA", "FATIGA", "SALUD" };
        private static readonly Color[] RowColors =
        {
            new Color(0.85f, 0.70f, 0.25f), // hambre
            new Color(0.30f, 0.62f, 0.88f), // sed
            new Color(0.62f, 0.45f, 0.85f), // cordura
            new Color(0.80f, 0.82f, 0.30f), // fatiga (relleno)
            new Color(0.85f, 0.28f, 0.28f), // salud
        };

        private void Awake()
        {
            BuildFace();
        }

        private void BuildFace()
        {
            var canvasGo = new GameObject("WatchFaceCanvas");
            canvasGo.transform.SetParent(ResolveAnchor(), false);

            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;

            // El RectTransform lo crea AddComponent<Canvas>, así que la colocación se aplica
            // DESPUÉS: fijarla sobre el Transform de antes la pierde al convertirse.
            _canvasRt = (RectTransform)canvasGo.transform;
            var canvasRt = _canvasRt;
            ApplyPlacement();

            // Fondo de la pantalla. Casi negro, no negro puro: el cristal de un reloj apagado
            // nunca es más oscuro que la habitación, y en Level 0 eso se nota.
            var bg = CreateImage("Screen", canvasRt, new Color(0.05f, 0.05f, 0.06f, 0.95f));
            bg.rectTransform.anchorMin = Vector2.zero;
            bg.rectTransform.anchorMax = Vector2.one;
            bg.rectTransform.offsetMin = Vector2.zero;
            bg.rectTransform.offsetMax = Vector2.zero;

            _clockText = CreateText("Clock", canvasRt, string.Empty, 22);
            _clockText.alignment = TextAnchor.UpperCenter;
            AnchorBand(_clockText.rectTransform, ClockTopFraction, ClockHeightFraction, 6f);

            _fills = new Image[RowLabels.Length];

            for (int i = 0; i < RowLabels.Length; i++)
                BuildRow(canvasRt, i, RowsTopFraction + i * RowHeightFraction);

            SetLayerRecursive(canvasGo, ViewModelLayer);
        }

        /// <summary>
        /// Hueso del que cuelga la cara, buscado por nombre DENTRO del wieldable.
        ///
        /// La búsqueda arranca en el GameObject que lleva el <c>Wieldable</c>, no en
        /// <c>transform.root</c>: la raíz de verdad es el personaje entero, y ahí hay un segundo
        /// esqueleto (el cuerpo del jugador) con huesos que se llaman igual — engancharía el
        /// brazo equivocado.
        ///
        /// POR QUÉ NO SIRVE EL NODO DE LA MALLA: en el donante, <c>CompassBase</c> es un HUESO,
        /// no la malla — su único componente es un Transform, y está a <c>y: 1.36</c> del origen
        /// del wieldable (las mallas son tres SkinnedMeshRenderer que se deforman por skinning y
        /// no cuelgan de él). Colgar ahí la cara la dejaba flotando metro y medio por encima de
        /// la mano, que es exactamente lo que se vio en la primera pasada.
        /// </summary>
        private Transform ResolveAnchor()
        {
            var wieldable = GetComponentInParent<PolymindGames.WieldableSystem.Wieldable>();
            Transform searchRoot = wieldable != null ? wieldable.transform : transform;

            var bone = FindDescendant(searchRoot, anchorBoneName);
            if (bone != null)
                return bone;

            // Sin log, un nombre mal escrito se manifiesta como "el reloj está en otro sitio",
            // que es de las cosas más caras de diagnosticar a ojo.
            var names = new System.Text.StringBuilder();
            foreach (var t in searchRoot.GetComponentsInChildren<Transform>(true))
                names.Append(t.name).Append(' ');

            Debug.LogWarning(
                $"[WristWatch] No hay hueso \"{anchorBoneName}\" bajo {searchRoot.name}. " +
                $"La cara se queda en {transform.name}. Huesos disponibles: {names}", this);

            return transform;
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            if (root.name == name)
                return root;

            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindDescendant(root.GetChild(i), name);
                if (found != null)
                    return found;
            }

            return null;
        }

        /// <summary>
        /// Coloca y escala la cara desde los campos públicos. Se llama cada frame a propósito:
        /// es lo que permite mover el reloj con el juego corriendo hasta dar con la pose, que es
        /// para lo que existe este placeholder. Cuando los números estén fijados, esto puede
        /// pasar a llamarse una sola vez.
        /// </summary>
        private void ApplyPlacement()
        {
            if (_canvasRt == null)
                return;

            _canvasRt.localPosition = localOffset;
            _canvasRt.localRotation = Quaternion.Euler(localEuler);
            // También aquí y no solo al construir: así cambiar la proporción en Play re-forma la
            // cara en vivo, que es la mitad del sentido de tener estos campos expuestos.
            _canvasRt.sizeDelta = new Vector2(DesignWidth, DesignHeight);

            // Se compensa la escala heredada del rig para que faceSizeMeters signifique metros de
            // verdad y no "metros multiplicados por lo que traiga el esqueleto".
            var parent = _canvasRt.parent;
            float inherited = parent != null ? Mathf.Abs(parent.lossyScale.x) : 1f;
            if (inherited < 1e-6f)
                inherited = 1f;

            // Una sola escala para los dos ejes: deformar la rejilla estiraría también el texto.
            _canvasRt.localScale = Vector3.one * (faceSizeMeters.x / DesignWidth / inherited);
        }

        private void BuildRow(RectTransform parent, int index, float rowTopFraction)
        {
            var label = CreateText("Label_" + RowLabels[index], parent, RowLabels[index], 14);
            label.alignment = TextAnchor.LowerLeft;
            label.color = new Color(0.78f, 0.76f, 0.70f);
            AnchorBand(label.rectTransform, rowTopFraction, RowHeightFraction * 0.5f, 14f);

            // Canal de la barra. Se dibuja siempre, también a valor 0: una fila que desaparece
            // entera al vaciarse se lee como fallo del reloj, no como stat en cero.
            var track = CreateImage("Track_" + RowLabels[index], parent, new Color(1f, 1f, 1f, 0.10f));
            AnchorBand(track.rectTransform,
                rowTopFraction + RowHeightFraction * 0.5f, RowHeightFraction * 0.22f, 14f);

            var fill = CreateImage("Fill_" + RowLabels[index], track.rectTransform, RowColors[index]);
            fill.rectTransform.anchorMin = Vector2.zero;
            fill.rectTransform.anchorMax = Vector2.one;
            fill.rectTransform.offsetMin = Vector2.zero;
            fill.rectTransform.offsetMax = Vector2.zero;
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 0f;

            _fills[index] = fill;
        }

        /// <summary>
        /// Coloca un elemento en una banda horizontal definida en FRACCIONES de la altura de la
        /// cara, medidas desde arriba. Al ser anclas normalizadas y no offsets absolutos, cambiar
        /// la proporción del reloj recoloca las filas solo — sin re-maquetar nada por frame.
        /// </summary>
        private static void AnchorBand(RectTransform rt, float topFraction, float heightFraction,
                                       float sidePadding)
        {
            rt.anchorMin = new Vector2(0f, 1f - topFraction - heightFraction);
            rt.anchorMax = new Vector2(1f, 1f - topFraction);
            rt.pivot = new Vector2(0.5f, 0.5f);
            // Con las dos anclas estiradas, offsetMin/offsetMax definen el rect entero: sizeDelta
            // deja de intervenir y el margen lateral es literal.
            rt.offsetMin = new Vector2(sidePadding, 0f);
            rt.offsetMax = new Vector2(-sidePadding, 0f);
        }

        private static Image CreateImage(string name, Transform parent, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private static Text CreateText(string name, Transform parent, string content, int fontSize)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            var txt = go.AddComponent<Text>();
            txt.text = content;
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize = fontSize;
            txt.color = new Color(0.88f, 0.86f, 0.80f);
            txt.raycastTarget = false;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            return txt;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++)
                SetLayerRecursive(go.transform.GetChild(i).gameObject, layer);
        }

        // LateUpdate y no Update: la animación del wieldable escribe los huesos en Update, así que
        // colocar antes que ella deja la cara un frame por detrás de la muñeca — un temblor que se
        // lee como "el canvas no está pegado" justo cuando más se mira.
        private void LateUpdate() => ApplyPlacement();

        private void Update()
        {
            if (_fills == null)
                return;

            UpdateClock();

            // Sin backend (menú, desconexión) las barras se quedan como estaban en vez de caer a
            // cero: un reloj que marca todo vacío al perder conexión se lee como "te estás
            // muriendo", que es exactamente la lectura contraria a la real.
            if (!IPCClient.TryGetInstance(out var ipc))
                return;

            var state = ipc.LatestState;
            if (state == null || state.localPlayer == null)
                return;

            var stats = state.localPlayer.stats;
            SetBar(0, stats.hunger);
            SetBar(1, stats.thirst);
            SetBar(2, stats.sanity);
            SetBar(3, fatiguePlaceholder); // relleno: ver la cabecera de la clase
            SetBar(4, stats.health);
        }

        private void UpdateClock()
        {
            if (_clockText == null)
                return;

            float totalMinutes = ClockStartHour * 60f + Time.time / 60f;
            int hours = Mathf.FloorToInt(totalMinutes / 60f) % 24;
            int minutes = Mathf.FloorToInt(totalMinutes) % 60;
            _clockText.text = $"{hours:00}:{minutes:00}";
        }

        private void SetBar(int index, float value0To100)
        {
            var fill = _fills[index];
            if (fill != null)
                fill.fillAmount = Mathf.Clamp01(value0To100 / 100f);
        }
    }
}
