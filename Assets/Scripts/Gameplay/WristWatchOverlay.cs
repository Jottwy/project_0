using System.Collections;
using System.Collections.Generic;
using PolymindGames;
using PolymindGames.WieldableSystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// MODO OVERLAY del reloj (2026-08-15) — el brazo cuelga directo del rig de cámara y TODO el
    /// pipeline de wieldables del vendor se destruye en la instancia.
    ///
    /// POR QUÉ EXISTE: la primera integración montó el reloj sobre seis sistemas simultáneos
    /// (Animator + override de pose + MotionMixer con perfil + offsets de WieldableMotion + warp
    /// de FOV por shader + curvas de cámara). Cada ajuste milimétrico exigía pelear con la capa
    /// equivocada y costó una jornada entera de diagnóstico — el historial está en los comentarios
    /// de WristPoseOverride. Para un brazo QUIETO con un reloj, ese pipeline no aporta: aporta
    /// interferencia.
    ///
    /// QUÉ CAMBIA PARA EDITAR: nada anima los huesos, así que la pose y el encuadre se editan EN
    /// EL PREFAB con el gizmo de siempre y Ctrl+S — sin Play, sin Capture Pose, sin Copy
    /// Component. Lo que se ve en el prefab es lo que sale en juego. Los únicos números de
    /// runtime son los dos del balanceo y el encuadre inicial de ViewModel, aquí abajo.
    ///
    /// EL WARP DE FOV SE APAGA POR MATERIAL (`_FOV_Enabled = 0`): la malla del brazo usa
    /// LitFieldOfView_SSS, que re-proyecta vértices con el global `_FOV`. Ese global lo pisa CADA
    /// wieldable equipado (linterna, spray...) vía WieldableFOV, así que un brazo overlay que lo
    /// respetase saltaría de tamaño al cambiar de item en la otra mano. Con el warp apagado, el
    /// brazo y el canvas de la cara se dibujan con la misma proyección SIEMPRE — la divergencia
    /// brazo/cara que costó medio día de sondas no puede volver.
    ///
    /// CONVIVE CON LA OTRA MANO: al no pasar por el WieldablesController, sacar el reloj no
    /// enfunda la linterna. Mirar la hora mientras alumbras es exactamente el gesto del concepto.
    /// </summary>
    public sealed class WristWatchOverlay : MonoBehaviour
    {
        [Header("Inercia — derivada del movimiento REAL, nunca inventada")]
        [Tooltip("Grados máximos de retardo del brazo al girar la cámara. 2.5 = el valor con el " +
                 "que el conjunto quedó validado en juego.")]
        public float lookSwayDegrees = 2.5f;

        [Tooltip("Cuánto se hunde/eleva el brazo con la velocidad vertical real (saltos, " +
                 "aterrizajes, escaleras). Metros por m/s de velocidad de cámara.")]
        public float verticalInertia = 0.008f;

        [Tooltip("Impulso de muelle al sacar el reloj: el brazo entra pasado de rosca y asienta. " +
                 "Es lo que sustituye a la animación de equipar del vendor. 0 = solo deslizamiento.")]
        public float equipKickDegrees = 14f;

        [Tooltip("Velocidad de recuperación del muelle (más alto = asienta antes). Independiente " +
                 "del framerate por construcción.")]
        public float springRecovery = 7f;

        [Tooltip("Segundos del deslizamiento de mostrar/ocultar.")]
        public float slideSeconds = 0.15f;

        [Header("Proyección — que Play se vea como el prefab")]
        [Tooltip("FOV del viewmodel mientras el reloj está fuera. IGUAL que el de la cámara (100) " +
                 "a propósito: con el warp de shader apagado por material, cualquier otro valor " +
                 "haría que el brazo se dibujara con una proyección distinta a la del mundo.")]
        public float viewModelFov = 100f;

        [Tooltip("Escala del rig de viewmodel mientras el reloj está fuera. Sin fijarla, el reloj " +
                 "hereda la del último wieldable equipado y la perspectiva cambia sola.\n\n" +
                 "NO intentes compensar esto escalando el overlay: la escala se aplica alrededor " +
                 "del origen del root, a la altura de los PIES del rig donante, y el reloj vive " +
                 "1.578 m por encima — cada 0.1 de escala lo mueve 16 cm en vertical. Se intentó y " +
                 "mandó el reloj fuera del encuadre.")]
        public float viewModelSize = 1f;


        [Tooltip("Sin Animator, el brazo derecho queda congelado en la pose de agarrar la brújula " +
                 "del donante — flotando sin sentido. La referencia enseña solo el izquierdo; " +
                 "apagado por defecto.")]
        public bool hideRightArm = true;

        private Transform _viewModel;
        private Vector3 _viewModelBasePos;
        private Quaternion _viewModelBaseRot;
        private float _lastBodyY;
        private float _lastYaw;
        private float _lastPitch;
        private Vector2 _swayLag;  // muelle del RATÓN, techo lookSwayDegrees
        private Vector2 _kickLag;  // muelle del SAQUE, techo equipKickDegrees — separados a
                                   // propósito: compartir muelle dejaba que un ratón rápido usara
                                   // el techo del kick (14°) y el brazo barría 15 cm de arco
        private float _yLag;       // hundimiento vertical en metros
        private Coroutine _slide;
        private Coroutine _restore;
        private MonoBehaviour _runner;
        private Transform _scaleAnchor;
        private Vector3 _baseLocalPosition;
        private IFOVHandlerCC _fovHandler;
        private float _savedViewModelFov;
        private float _savedViewModelSize;
        private bool _projectionSaved;

        /// <summary>
        /// Poda el pipeline del vendor y cuelga la instancia del rig de cámara. DEBE llamarse con
        /// la instancia AÚN INACTIVA: varios Awake del vendor hacen trabajo destructivo
        /// (Wieldable.Awake termina en SetActive(false), WieldableMotion.Awake caza el Character…)
        /// y la única forma de que no corran jamás es que el componente ya no exista cuando el
        /// GameObject despierte. De ahí también los DestroyImmediate: Destroy normal es diferido y
        /// el Awake ganaría la carrera.
        /// </summary>
        /// <param name="coroutineRunner">
        /// Objeto SIEMPRE ACTIVO donde correr la restauración de proyección. No puede correr en
        /// este componente: al guardar el reloj el GameObject se desactiva al acabar el
        /// deslizamiento, y desactivar un objeto MATA sus corrutinas — la restauración se quedaría
        /// a medias justo en el caso que intenta cubrir (esperar a que el objeto de la otra mano
        /// termine de equiparse).
        /// </param>
        public void Setup(Transform wieldablesRoot, MonoBehaviour coroutineRunner)
        {
            _runner = coroutineRunner;

            // Orden: primero los que dependen de IWieldable, el propio WieldableTool al final.
            DestroyAll<WieldableCameraCurvesAnimator>();
            DestroyAll<WieldableRotatingElement>();
            DestroyAll<WieldableFOV>();
            DestroyAll<WieldableMotion>();
            DestroyAll<WieldableAnimator>();
            DestroyAll<WristPoseOverride>(); // la pose ya vive en los transforms del prefab
            DestroyAll<Animator>();          // nada debe reescribir huesos
            DestroyAll<WieldableTool>();

            transform.SetParent(wieldablesRoot, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;

            // El encuadre viene TAL CUAL del prefab — única fuente de verdad. Una versión anterior
            // lo pisaba aquí con números en código, lo que convertía cualquier ajuste guardado en
            // el prefab en papel mojado al siguiente arranque.
            _viewModel = transform.Find("ViewModel");
            if (_viewModel != null)
            {
                _viewModelBasePos = _viewModel.localPosition;
                _viewModelBaseRot = _viewModel.localRotation;
            }

            // Instancias de material por renderer (r.material clona): apagar el warp aquí no toca
            // el material compartido del vendor ni a la brújula real.
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (hideRightArm && r.name == "RightArm")
                    r.enabled = false;

                foreach (var m in r.materials)
                    if (m.HasProperty("_FOV_Enabled"))
                        m.SetFloat("_FOV_Enabled", 0f);
            }
        }

        /// <summary>
        /// Cuelga el reloj de un nodo propio que ANULA la escala del viewmodel, y lo hace en el
        /// único sitio donde la anulación es exacta: con el MISMO ORIGEN que el nodo que escala.
        ///
        /// EL ACOPLE QUE ROMPE: <c>viewModelSize</c> no es un shader — <c>CameraFOVHandler</c>
        /// escala el transform del nodo `Camera`, y de ahí cuelgan los dos brazos. Cuando la
        /// antorcha pone 0.5, el brazo del reloj se encoge con ella. El FOV en cambio NO acopla:
        /// el brazo del reloj lleva `_FOV_Enabled = 0` por material, así que ignora el global.
        /// Por eso aquí solo se neutraliza la escala y no se toca el FOV de nadie.
        ///
        /// POR QUÉ EN UN NODO NUEVO Y NO EN EL PROPIO OVERLAY, que es como falló la primera vez:
        /// escalar por s y contra-escalar por 1/s solo se cancela si ambas ocurren alrededor del
        /// MISMO punto. El vendor escala alrededor del ojo; el overlay colgaba de
        /// `Wieldables/Root`, desplazado (0,-1.6,+0.35). Centros distintos dejan una traslación
        /// residual proporcional a ese desplazamiento — 1,58 m que mandaron el brazo fuera del
        /// encuadre. Con el ancla a localPosition cero bajo el nodo que escala, S(s)·S(1/s) es la
        /// identidad exacta y no queda residuo.
        ///
        /// El offset de `Wieldables/Root` se reaplica a mano al overlay, sumando localPosition por
        /// la cadena: esos nodos tienen rotación identidad (verificado), así que sumar basta.
        /// </summary>
        private void BuildScaleAnchor(Transform wieldablesRoot)
        {
            var scaler = ResolveScalerTransform();

            if (scaler == null)
            {
                // Sin nodo que escale, el montaje de siempre: colgar del root del vendor.
                transform.SetParent(wieldablesRoot, false);
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
                Debug.LogWarning("[WristWatch] No se encontró el nodo que escala el viewmodel; " +
                                 "el reloj heredará la escala de lo que lleves en la otra mano.");
                return;
            }

            var rootOffset = Vector3.zero;
            for (var t = wieldablesRoot; t != null && t != scaler; t = t.parent)
                rootOffset += t.localPosition;

            var anchorGo = new GameObject("BR_WristWatch_ScaleAnchor");
            _scaleAnchor = anchorGo.transform;
            _scaleAnchor.SetParent(scaler, false);
            _scaleAnchor.localPosition = Vector3.zero;
            _scaleAnchor.localRotation = Quaternion.identity;

            transform.SetParent(_scaleAnchor, false);
            transform.localPosition = rootOffset;
            transform.localRotation = Quaternion.identity;

            // Se guarda como BASE porque LateUpdate reescribe localPosition cada frame para la
            // inercia vertical. Sin esto, ese cero borraba el offset y dejaba los pies del rig en
            // el ojo — con el reloj 1,58 m por encima y fuera del encuadre.
            _baseLocalPosition = rootOffset;

            Debug.Log($"[WristWatch] Ancla de escala bajo '{scaler.name}'. " +
                      $"Offset de Wieldables/Root reaplicado: {rootOffset}.", this);
        }

        private Transform ResolveScalerTransform()
        {
            var fov = ResolveFovHandler();
            return fov is Component c ? c.transform : null;
        }

        /// <summary>
        /// Anula cada frame la escala que el vendor ponga. Cada frame y no solo al sacar el reloj:
        /// basta equipar la antorcha con el reloj ya fuera para que el vendor la reescriba.
        /// </summary>
        private void ApplyScaleImmunity()
        {
            if (_scaleAnchor == null) return;

            float s = _scaleAnchor.parent != null ? _scaleAnchor.parent.localScale.x : 1f;
            if (Mathf.Abs(s) < 1e-4f) s = 1f;

            _scaleAnchor.localScale = Vector3.one / s;
        }

        private void OnDestroy()
        {
            // El ancla vive bajo la cámara del jugador, no bajo el reloj: si no se destruye a mano
            // se queda ahí para siempre con la escala congelada del último frame.
            if (_scaleAnchor != null)
                Destroy(_scaleAnchor.gameObject);
        }

        private void DestroyAll<T>() where T : Component
        {
            foreach (var c in GetComponentsInChildren<T>(true))
                DestroyImmediate(c);
        }

        public bool IsShown => gameObject.activeSelf;

        public void Toggle()
        {
            if (IsShown) Hide();
            else Show();
        }

        public void Show()
        {
            gameObject.SetActive(true);

            ApplyProjection();

            // El "equipar" es este impulso: el brazo entra girado de más y su muelle lo asienta.
            // Da el peso de una animación de sacar sin autorar ningún clip. Muelle PROPIO: el del
            // ratón tiene un techo mucho más bajo y no debe heredar este recorrido.
            _kickLag += new Vector2(equipKickDegrees, -equipKickDegrees * 0.6f);
            _yLag -= 0.04f;

            if (_slide != null) StopCoroutine(_slide);
            _slide = StartCoroutine(Slide(shown: true));
        }

        public void Hide()
        {
            RestoreProjection();
            if (_slide != null) StopCoroutine(_slide);
            _slide = StartCoroutine(Slide(shown: false));
        }

        /// <summary>
        /// Fija FOV y escala del viewmodel al sacar el reloj, y los devuelve al guardarlo. Es el
        /// estado con el que el conjunto quedó validado en juego.
        ///
        /// LIMITACIÓN CONOCIDA, no resuelta: <c>IFOVHandlerCC</c> es un recurso COMPARTIDO de una
        /// sola ranura, así que equipar otro wieldable con el reloj ya fuera lo reescribe y el
        /// reloj cambia de tamaño. Se intentó arreglar haciendo al reloj inmune (compensando la
        /// escala heredada) y FUE PEOR: la escala se aplica alrededor del origen del root, a la
        /// altura de los pies del rig donante, y el reloj vive 1.578 m por encima — compensar un
        /// 0.5 exige escala 2.0 y eso lo manda 1.58 m hacia arriba, fuera del encuadre. Cualquier
        /// intento futuro tiene que corregir además localPosition por ese desplazamiento.
        /// </summary>
        private void ApplyProjection()
        {
            var fov = ResolveFovHandler();
            if (fov == null || _projectionSaved) return;

            _savedViewModelFov = fov.ViewModelFOV;
            _savedViewModelSize = fov.ViewModelSize;
            _projectionSaved = true;

            fov.SetViewModelFOV(viewModelFov, 0.12f);
            fov.SetViewModelSize(viewModelSize);
        }

        /// <summary>
        /// Devuelve la proyección al guardar el reloj — pidiéndosela al wieldable que esté en la
        /// mano AHORA, no restaurando lo que había cuando se sacó.
        ///
        /// EL BUG QUE ARREGLA: restaurar los valores guardados al SACAR el reloj los aplica ya
        /// caducados. Si entre medias equipaste la antorcha (que pone 45 grados y escala 0.5),
        /// guardar el reloj le devolvía a la antorcha unos valores que no son suyos y quedaba con
        /// el FOV cambiado — el "equipo algo en la derecha y luego se bugea" que reportó Joel.
        ///
        /// El toggle de `enabled` dispara el `OnEnable` del propio <c>WieldableFOV</c>, que es
        /// quien sabe cuáles son sus números. Nadie tiene que adivinarlos ni guardarlos.
        ///
        /// Sin wieldable activo se cae a los valores guardados: es el caso de manos vacías, donde
        /// no hay nadie a quien preguntar y lo de antes sí era correcto.
        /// </summary>
        private void RestoreProjection()
        {
            if (!_projectionSaved) return;
            _projectionSaved = false;

            var runner = _runner != null ? _runner : this;
            if (_restore != null) runner.StopCoroutine(_restore);
            _restore = runner.StartCoroutine(RestoreWhenVendorReady());
        }

        /// <summary>
        /// Insiste durante unos frames hasta que el wieldable de la otra mano pueda reaplicar SU
        /// proyección.
        ///
        /// POR QUÉ INSISTIR: al guardar el reloj, el objeto de la otra mano puede estar a mitad de
        /// equiparse — su GameObject aún inactivo — y entonces no hay a quien preguntarle. Un solo
        /// intento fallaba en silencio y caía a los valores guardados, ya caducados: la antorcha
        /// se quedaba con una proyección que no es la suya y se veía lejos. Intermitente, porque
        /// depende de en qué punto de su animación de equipar pilles el momento.
        /// </summary>
        private IEnumerator RestoreWhenVendorReady()
        {
            for (int i = 0; i < 30; i++)
            {
                var character = Net.LocalPlayerLocator.Find<Character>();
                if (character != null && character.TryGetCC(out IWieldablesControllerCC controller))
                {
                    var active = controller.ActiveWieldable;
                    if (active != null && active.gameObject != null)
                    {
                        var vendorFov = active.gameObject.GetComponentInChildren<WieldableFOV>(true);
                        if (vendorFov != null && vendorFov.gameObject.activeInHierarchy)
                        {
                            // El toggle dispara su OnEnable, que es quien sabe sus números.
                            vendorFov.enabled = false;
                            vendorFov.enabled = true;
                            _restore = null;
                            yield break;
                        }
                    }
                }

                yield return null;
            }

            // Manos vacías o nadie con WieldableFOV: no hay a quien preguntar y los valores
            // guardados son lo correcto.
            var fov = ResolveFovHandler();
            if (fov != null)
            {
                fov.SetViewModelFOV(_savedViewModelFov, 0f);
                fov.SetViewModelSize(_savedViewModelSize);
            }
            _restore = null;
        }

        /// <summary>
        /// Reafirma FOV y escala si alguien los ha cambiado.
        ///
        /// DURACIÓN CERO, y es la corrección de un fallo real: <c>ViewModelFOV</c> devuelve el
        /// valor EN CURSO del tween, no su destino. Con una duración >0, mientras el tween viaja
        /// la diferencia contra el objetivo sigue siendo grande, así que el frame siguiente vuelve
        /// a llamar y REINICIA el tween — que así nunca llega. El síntoma es un FOV congelado a
        /// medio camino que aparece "a veces", según lo que estuviera pasando ese frame.
        /// Instantáneo no puede entrar en ese bucle: al frame siguiente la diferencia ya es cero.
        /// </summary>
        private void ReassertProjection()
        {
            if (!_projectionSaved) return;

            var fov = ResolveFovHandler();
            if (fov == null) return;

            if (Mathf.Abs(fov.ViewModelSize - viewModelSize) > 0.001f)
                fov.SetViewModelSize(viewModelSize);

            if (Mathf.Abs(fov.ViewModelFOV - viewModelFov) > 0.5f)
                fov.SetViewModelFOV(viewModelFov, 0f);
        }

        private IFOVHandlerCC ResolveFovHandler()
        {
            if (_fovHandler != null) return _fovHandler;

            var character = Net.LocalPlayerLocator.Find<Character>();
            if (character != null) character.TryGetCC(out _fovHandler);
            return _fovHandler;
        }

        /// <summary>Desliza el brazo desde abajo al mostrar y hacia abajo al ocultar — el
        /// sustituto de 30 líneas de la animación de equipar del vendor.</summary>
        private IEnumerator Slide(bool shown)
        {
            const float drop = 0.25f;
            float from = shown ? -drop : 0f;
            float to = shown ? 0f : -drop;

            for (float t = 0f; t < slideSeconds; t += Time.deltaTime)
            {
                float k = Mathf.SmoothStep(0f, 1f, t / slideSeconds);
                SetSlideOffset(Mathf.Lerp(from, to, k));
                yield return null;
            }

            SetSlideOffset(to);
            _slide = null;

            if (!shown)
                gameObject.SetActive(false);
        }

        private void SetSlideOffset(float y)
        {
            if (_viewModel != null)
                _viewModel.localPosition = _viewModelBasePos + new Vector3(0f, y, 0f);
        }

#if UNITY_EDITOR
        /// <summary>
        /// Copia la pose viva (todos los huesos + el encuadre del ViewModel) de ESTA instancia al
        /// prefab en disco, en caliente desde Play. Existe porque el flujo de ajuste es girar
        /// huesos con el gizmo sobre la instancia — y sin esto, todo muere al salir de Play (con
        /// 22 huesos, Copy Component por hueso no es un flujo, es un castigo).
        ///
        /// El emparejado es por NOMBRE de nodo: los huesos del rig tienen nombres únicos y lo que
        /// no exista en el prefab (el canvas, que se construye en runtime) se salta solo. Para el
        /// ViewModel se guarda la BASE, no el valor del frame — el bob y el sway lo desplazan
        /// cada LateUpdate y guardar el valor vivo hornearía media onda de balanceo como offset.
        /// </summary>
        [ContextMenu("Guardar pose viva al prefab")]
        private void SaveLivePoseToPrefab()
        {
            const string prefabPath = "Assets/Resources/Wieldables/BR_Wieldable_Watch.prefab";

            var contents = UnityEditor.PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var targets = new Dictionary<string, Transform>();
                foreach (var t in contents.GetComponentsInChildren<Transform>(true))
                    targets[t.name] = t;

                int saved = 0;
                foreach (var live in GetComponentsInChildren<Transform>(true))
                {
                    if (!targets.TryGetValue(live.name, out var target) || target == contents.transform)
                        continue;

                    if (live == _viewModel)
                    {
                        target.localPosition = _viewModelBasePos;
                        target.localRotation = _viewModelBaseRot;
                    }
                    else
                    {
                        target.localPosition = live.localPosition;
                        target.localRotation = live.localRotation;
                    }
                    saved++;
                }

                UnityEditor.PrefabUtility.SaveAsPrefabAsset(contents, prefabPath, out bool ok);
                Debug.Log(ok
                    ? $"[WristWatch] Pose guardada al prefab: {saved} nodos."
                    : "[WristWatch] FALLO guardando el prefab.", this);
            }
            finally
            {
                UnityEditor.PrefabUtility.UnloadPrefabContents(contents);
            }
        }
#endif

        /// <summary>
        /// Inercia derivada del movimiento REAL — nada de ondas propias. La versión anterior
        /// añadía un seno de bob encima del head-bob que la cámara del vendor ya aplica: dos ondas
        /// desfasadas, el brazo patinaba contra el mundo y empeoraba al correr o saltar (Joel lo
        /// midió apagándolo: "hace bastante diferencia"). Aquí no hay onda que pueda desfasarse:
        /// el brazo solo REACCIONA a lo que la cámara hace de verdad —llega tarde al giro, se
        /// hunde al aterrizar— y un muelle lo devuelve. Sin entrada, sin movimiento.
        ///
        /// Tres decisiones que son la diferencia con lo descartado:
        /// - La rotación se aplica al ROOT, no al ViewModel: el root es identidad bajo el rig de
        ///   cámara, así que sus ejes SON los de cámara — el retardo de guiñada gira en guiñada.
        ///   Aplicado al ViewModel giraba en diagonales, porque su base lleva la pose del brazo.
        /// - Amortiguación con exponencial (1-exp(-k·dt)): mismo asentamiento a 60 que a 150 FPS.
        ///   El Lerp(dt*k) anterior respiraba con el framerate.
        /// - La vertical usa la VELOCIDAD real de la cámara en Y: saltos, aterrizajes y escaleras
        ///   hunden o elevan el brazo en fase con lo que pasa, no con un reloj interno.
        /// </summary>
        private void LateUpdate()
        {
            if (_viewModel == null)
                return;

            var cam = Camera.main;
            if (cam == null)
                return;

            // Reafirma la proyección del reloj cada frame mientras está fuera. Mientras miras la
            // hora manda el reloj; al guardarlo, RestoreProjection le devuelve la suya al objeto
            // de la otra mano preguntándosela a él.
            //
            // SE PROBÓ LA ALTERNATIVA "INDEPENDENCIA TOTAL" (un ancla que anula la escala del
            // vendor) Y SE DESCARTÓ EN JUEGO: funciona como promete —el reloj deja de seguir la
            // escala ajena— pero eso es justo lo que NO se quiere. Con la antorcha en 0.5, el
            // brazo del reloj se quedaba a tamaño 1 y salía al doble que todo lo demás, llenando
            // la pantalla. Ser inmune a la escala del vendor es ser inconsistente con el juego.
            ReassertProjection();

            float dt = Mathf.Max(Time.deltaTime, 1e-5f);
            float damp = 1f - Mathf.Exp(-springRecovery * dt);

            var e = cam.transform.eulerAngles;
            float dYaw = Mathf.DeltaAngle(_lastYaw, e.y);
            float dPitch = Mathf.DeltaAngle(_lastPitch, e.x);
            _lastYaw = e.y;
            _lastPitch = e.x;

            // Muelle del ratón: techo BAJO y suyo. El pitch entra a la mitad que la guiñada — el
            // brazo cruza horizontal y el retardo vertical se percibe el doble de grande.
            // Muelle del ratón: techo BAJO y suyo. El pitch entra a la mitad que la guiñada.
            // (El análisis dice que el signo del cabeceo debería ir negado y que las ganancias
            // podrían subir con el pivote en el ojo. Ambas cosas quedan FUERA hasta poder probarlas
            // de una en una sobre este estado validado.)
            _swayLag += new Vector2(-dYaw * 0.5f, dPitch * 0.25f);
            _swayLag = Vector2.ClampMagnitude(_swayLag, lookSwayDegrees);
            _swayLag = Vector2.Lerp(_swayLag, Vector2.zero, damp);

            // Muelle del saque: solo lo alimenta Show(), aquí únicamente decae.
            _kickLag = Vector2.ClampMagnitude(_kickLag, equipKickDegrees);
            _kickLag = Vector2.Lerp(_kickLag, Vector2.zero, damp);

            // Velocidad vertical del CUERPO, no de la cámara: la cámara pivota en arco al hacer
            // pitch y ese arco fabricaba "saltos" fantasma con solo mover el ratón — el defecto
            // que rompía el brazo al mirar arriba/abajo. La raíz del personaje no pivota.
            float bodyY = cam.transform.root.position.y;
            float vy = (bodyY - _lastBodyY) / dt;
            _lastBodyY = bodyY;
            float targetY = Mathf.Clamp(-vy * verticalInertia, -0.035f, 0.035f);
            _yLag = Mathf.Lerp(_yLag, targetY, damp);

            // REVERTIDO al estado que Joel validó ("reloj de lujo", 2026-08-16). Lo que había aquí
            // —compensación de pivote en el ojo— es geométricamente correcto sobre el papel y
            // resolvía el travelling del cabeceo, pero se metió junto con otros dos cambios y el
            // conjunto volvió a descolocar el brazo. No se reintroduce hasta poder probarlo SOLO,
            // sobre este estado bueno y con permiso.
            float pitchTotal = _swayLag.y + _kickLag.y;
            float yawTotal = _swayLag.x + _kickLag.x;

            // Al ROOT: ejes de cámara. El slide de mostrar/ocultar toca el ViewModel, así que no
            // se pisan aunque coincidan en el tiempo.
            transform.localRotation = Quaternion.Euler(pitchTotal, yawTotal, yawTotal * 0.3f);
            transform.localPosition = _baseLocalPosition + new Vector3(0f, _yLag, 0f);
        }
    }
}
