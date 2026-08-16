using BackroomsSurvival.Net;
using PolymindGames;
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
    /// FUENTE DE LAS BARRAS (2026-08-16): los managers de STP — la MISMA fuente que pollea el
    /// HUD del vendor (PlayerStatsUI), así que reloj y HUD coinciden POR CONSTRUCCIÓN, con
    /// conexión (StatInterpolator escribe el snapshot en los managers) y sin ella (drenan en
    /// local; el reloj muestra ese drain local igual que el HUD — es el precio asumido del
    /// cambio). Antes se leía <c>IPCClient.LatestState</c> crudo y las dos superficies podían
    /// divergir cuando el snapshot dejaba de llegar con el HUD aún vivo.
    ///
    /// CORDURA es la excepción: el vendor NO tiene manager de cordura (es stat del backend),
    /// así que sigue leyendo el snapshot IPC. FATIGA es la <c>stamina</c> por decisión de
    /// diseño (2026-08-16): fatiga ES stamina, no una stat lenta aparte.
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

        [Tooltip("Espeja la cara horizontalmente (escala X negativa).\n\n" +
                 "PARA QUÉ: la normal del canvas acaba mirando hacia el ojo en vez de alejándose, " +
                 "así que la cara se ve POR DETRÁS y el texto sale invertido. Se dibuja igualmente " +
                 "porque el material de UI lleva Cull Off.\n\n" +
                 "Corregir la orientación por rotación (localEuler.x = -90) se probó y no salió; " +
                 "esto invierte la geometría en su sitio, sin mover el encaje ya resuelto.\n\n" +
                 "Solo el eje X: Y y Z se quedan positivos, y el factor de tamaño es el mismo, así " +
                 "que la cara no cambia ni de sitio ni de tamaño — solo de mano.")]
        [SerializeField] private bool mirrorX = false;

        [Header("Proyección del canvas")]
        [Tooltip("Material que reproyecta el vértice al FOV del viewmodel (BR_UIWarp), para que " +
                 "la cara comparta proyección con el brazo y con el cuerpo del reloj, que warpean " +
                 "por shader.\n\n" +
                 "Se aplica a TODOS los Graphic de la cara — fondo, etiquetas, canales de barra y " +
                 "sus rellenos. Vaciar el campo devuelve la cara entera a UI/Default sin tocar " +
                 "código, y entonces vuelve a despegarse del reloj.\n\n" +
                 "El nombre se queda en 'screenMaterial' aunque ya no sea solo el fondo: " +
                 "renombrarlo rompería la referencia serializada en BR_Wieldable_Watch.prefab, y " +
                 "el síntoma sería un campo a null en silencio, o sea la cara despegada sin más " +
                 "aviso.")]
        public Material screenMaterial;

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

        // Fuentes de stats — misma fuente que el HUD del vendor. Se re-resuelven por estado
        // (fake-null tras el rig rebuild), nunca se cachean de por vida. Ver UpdateBars.
        private Character _character;
        private IHungerManagerCC _hunger;
        private IThirstManagerCC _thirst;
        private IStaminaManagerCC _stamina;
        private IHealthManager _health;
        private float _nextResolveRetry;

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

            ApplyCanvasMaterial(canvasGo);
            SetLayerRecursive(canvasGo, ViewModelLayer);
        }

        /// <summary>
        /// Pone el material de warp en TODOS los Graphic de la cara, de una pasada y al final —
        /// cuando ya existen todos.
        ///
        /// UN RECORRIDO Y NO UNA LISTA: los <c>Fill_*</c> cuelgan de su <c>Track_*</c>, no del
        /// canvas, así que enumerar variable a variable se los dejaría fuera y las barras serían
        /// lo único que no seguiría a la muñeca. <c>GetComponentsInChildren</c> los coge sin
        /// nombrarlos, y cubre de paso cualquier Graphic que se añada aquí en el futuro. El
        /// <c>true</c> incluye inactivos: hoy no hay ninguno, pero una fila que naciera apagada
        /// se quedaría sin material y el fallo aparecería solo al encenderla.
        ///
        /// LOS <c>Text</c> ENTRAN TAMBIÉN, y no rompen la fuente. <c>Graphic.UpdateMaterial</c>
        /// (Graphic.cs:669-677) asigna material y textura por SEPARADO al CanvasRenderer:
        /// <c>SetMaterial(materialForRendering)</c> y <c>SetTexture(mainTexture)</c>. Y
        /// <c>Text.mainTexture</c> (Text.cs:55-67) devuelve el atlas de la fuente ANTES de mirar
        /// el material, así que el atlas gana y se enlaza igual sobre el <c>_MainTex</c> del
        /// nuestro. BR_UIWarp puede recibirlo porque copia el fragment de UI/Default entero,
        /// incluido el <c>_TextureSampleAdd</c> que convierte un atlas de solo-alfa en glifo
        /// blanco; sin esa línea el texto saldría negro.
        /// </summary>
        private void ApplyCanvasMaterial(GameObject canvasGo)
        {
            if (screenMaterial == null)
                return;

            foreach (var graphic in canvasGo.GetComponentsInChildren<Graphic>(true))
                graphic.material = screenMaterial;
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

            // LA CARA HEREDA LA ESCALA DEL RIG, igual que la malla del reloj. Aquí se dividía entre
            // `Mathf.Abs(parent.lossyScale.x)` para que `faceSizeMeters` significara metros de
            // mundo exactos — y eso hacía al canvas INMUNE a la escala del viewmodel: se quedaba
            // del mismo tamaño mientras el reloj seguía al nodo `Camera`, que `CameraFOVHandler`
            // escala con el `viewModelSize` del item equipado. Con la antorcha (0.5) el reloj se
            // encogía a la mitad y la esfera no: se veía al DOBLE.
            //
            // No se notaba antes porque `WristWatchOverlay` clavaba `viewModelSize = 1` mientras
            // el reloj estaba fuera. Al quitar aquel forzado, cada item impone la suya y el resto
            // de esta división quedó al descubierto. Es el mismo error que ya se probó y descartó
            // en juego para el brazo: ser inmune a la escala del vendor es ser inconsistente.
            //
            // `faceSizeMeters` pasa por tanto a estar en metros DEL ESPACIO DEL HUESO, no del
            // mundo. Es lo que se quiere: el reloj entero, cara incluida, cambia de tamaño junto.
            float faceScale = faceSizeMeters.x / DesignWidth;
            _canvasRt.localScale = new Vector3(mirrorX ? -faceScale : faceScale, faceScale, faceScale);
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
            fill.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
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
            UpdateBars();
        }

        /// <summary>
        /// Lee los managers de STP y actualiza las barras. Sigue el patrón de re-resolución
        /// del proyecto (contrato en <c>LocalPlayerLocator</c>): el rig rebuild destruye y
        /// recrea los managers, así que cachear en Awake dejaría al reloj leyendo cadáveres —
        /// exactamente el fallo que tuvo StatInterpolator. Detección por estado y reintento a
        /// reloj de 0.5 s, no un barrido por frame.
        ///
        /// RETENCIÓN: una fuente ausente (mitad de rebuild, menú, desconexión) deja su barra
        /// como estaba en vez de caer a cero — un reloj que marca todo vacío al perder la
        /// fuente se lee como "te estás muriendo", que es la lectura contraria a la real.
        /// </summary>
        private void UpdateBars()
        {
            if (AnyStatSourceLost() && Time.unscaledTime >= _nextResolveRetry)
            {
                _nextResolveRetry = Time.unscaledTime + 0.5f;
                ResolveStatSources();
            }

            // Rangos heterogéneos — normalización por stat a los 0..100 que espera SetBar:
            // hambre/sed van 0..Max (100 por defecto), stamina la normaliza STP a 0..1,
            // salud es absoluta sobre MaxHealth.
            if (Alive(_hunger))
                SetBar(0, _hunger.Hunger / Mathf.Max(1e-4f, _hunger.MaxHunger) * 100f);
            if (Alive(_thirst))
                SetBar(1, _thirst.Thirst / Mathf.Max(1e-4f, _thirst.MaxThirst) * 100f);
            if (Alive(_stamina))
                SetBar(3, _stamina.Stamina * 100f);
            if (Alive(_health))
                SetBar(4, _health.Health / Mathf.Max(1e-4f, _health.MaxHealth) * 100f);

            // CORDURA: sin manager en el vendor (stat del backend) — snapshot IPC, con la
            // misma retención que el resto.
            if (IPCClient.TryGetInstance(out var ipc))
            {
                var state = ipc.LatestState;
                if (state != null && state.localPlayer != null)
                    SetBar(2, state.localPlayer.stats.sanity);
            }
        }

        private void ResolveStatSources()
        {
            _character = LocalPlayerLocator.Find<Character>();

            _hunger = null;
            _thirst = null;
            _stamina = null;
            _health = null;

            if (_character == null)
                return;

            _character.TryGetCC(out _hunger);
            _character.TryGetCC(out _thirst);
            _character.TryGetCC(out _stamina);
            _health = _character.HealthManager;
        }

        private bool AnyStatSourceLost() =>
            !Alive(_hunger) || !Alive(_thirst) || !Alive(_stamina) || !Alive(_health);

        // Los managers se guardan como interfaz, y el `== null` sobre una interfaz es igualdad
        // de referencia pura: NO ve el fake-null de Unity. El cast a UnityEngine.Object
        // recupera el operador sobrecargado, que es lo que detecta el componente destruido.
        private static bool Alive(object source) =>
            source != null && !(source is Object obj && obj == null);

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
