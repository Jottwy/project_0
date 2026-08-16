using System.Collections;
using System.Collections.Generic;
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
    /// EL WARP DE FOV NO SE APAGA: SE COMPARTE (2026-08-16). Hasta esta fecha aquí ponía que el
    /// warp se desactivaba por material con `_FOV_Enabled = 0`. Era falso por partida doble: el
    /// nombre real de la propiedad es `_FOVEnabled`, y además está declarada GLOBAL en
    /// LitFieldOfView_SSS.shadergraph (`m_GeneratePropertyBlock: false`), así que ningún material
    /// puede pisarla. El brazo warpeó SIEMPRE; lo que no warpeaba era todo lo demás del reloj.
    ///
    /// La solución fue la contraria a la que describía el comentario: hacer que warpen las TRES
    /// superficies. El brazo ya lo hacía, el cuerpo del reloj pasó a LitFieldOfView_SSS y el
    /// canvas de la cara a BR_UIWarp (mismo warp, escrito a mano porque el subtarget Canvas de
    /// URP ignora VertexDescription.Position). Al compartir proyección, el VALOR de `_FOV` deja
    /// de importar: las tres se mueven juntas con cualquiera.
    ///
    /// POR ESO ESTE COMPONENTE YA NO TOCA `_FOV` NI LA ESCALA DEL VIEWMODEL. Los dicta el
    /// wieldable que lleves en la derecha, como para cualquier otro item. Forzarlos era una
    /// muleta para que la esfera no-warpeada coincidiera con el brazo, y tenía un precio: con el
    /// reloj fuera, la antorcha y su brazo se dibujaban al 64% de su tamaño.
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

        /// <summary>
        /// Poda el pipeline del vendor y cuelga la instancia del rig de cámara. DEBE llamarse con
        /// la instancia AÚN INACTIVA: varios Awake del vendor hacen trabajo destructivo
        /// (Wieldable.Awake termina en SetActive(false), WieldableMotion.Awake caza el Character…)
        /// y la única forma de que no corran jamás es que el componente ya no exista cuando el
        /// GameObject despierte. De ahí también los DestroyImmediate: Destroy normal es diferido y
        /// el Awake ganaría la carrera.
        /// </summary>
        /// <param name="coroutineRunner">
        /// SIN USO desde que el componente dejó de forzar la proyección (2026-08-16): existía para
        /// correr la restauración de `_FOV` fuera de este objeto, que se desactiva al guardar el
        /// reloj. Ya no hay nada que restaurar. El parámetro se conserva porque quien llama vive
        /// en WristWatchHandler y cambiar la firma queda fuera del alcance de este cambio.
        /// </param>
        public void Setup(Transform wieldablesRoot, MonoBehaviour coroutineRunner)
        {
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

            // Solo visibilidad. Aquí había además un intento de apagar el warp por material
            // (`m.SetFloat("_FOV_Enabled", 0f)`) que no hacía nada: el nombre correcto es
            // `_FOVEnabled` y la propiedad es global, así que `HasProperty` devolvía false y el
            // SetFloat ni se ejecutaba. De paso, `r.materials` clonaba el array de materiales de
            // TODOS los renderers en cada equipado — instancias huérfanas y batching roto a cambio
            // de nada.
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (hideRightArm && r.name == "RightArm")
                    r.enabled = false;
            }
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
            if (_slide != null) StopCoroutine(_slide);
            _slide = StartCoroutine(Slide(shown: false));
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
            // Cero como base: Setup cuelga el overlay de `wieldablesRoot` con localPosition cero.
            // Aquí había un `_baseLocalPosition` que solo escribía el ancla de escala, código
            // muerto sin call sites, así que el campo valía siempre Vector3.zero.
            transform.localPosition = new Vector3(0f, _yLag, 0f);
        }
    }
}
