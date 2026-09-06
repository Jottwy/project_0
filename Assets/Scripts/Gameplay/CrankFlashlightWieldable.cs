using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.WieldableSystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-133 bloque A — la linterna de manivela: la única fuente de luz que no se gasta del todo,
    /// porque siempre puedes pararte a darle cuerda. Y ése es justo el precio: pararte.
    ///
    /// POR QUÉ ES UN <see cref="Wieldable"/> Y NO UN COMPONENTE PUENTE, que es la diferencia con
    /// <see cref="SprayCan"/>. El bote se cuelga de un <c>WieldableTool</c> del vendor porque le
    /// basta con el pulsado: <c>WieldableTool.Use</c> ignora todo lo que no sea
    /// <c>WieldableInputPhase.Start</c>. La manivela es un MANTENIDO, y el mantenido sólo llega a
    /// quien implemente <c>IUseInputHandler</c> — <c>FPSWieldablesInput</c> hace
    /// `wieldable as IUseInputHandler` sobre el componente wieldable, no busca en los hijos. Así
    /// que la fase `Hold` no existe para un puente; hay que SER el wieldable. Se hereda de la clase
    /// del vendor sin editarla, que es legal en esta dirección (nuestro asmdef referencia a
    /// PolymindGames; el suyo no puede referenciarnos a nosotros).
    ///
    /// LA CARGA VIVE EN LA PROPIEDAD `Durability` DEL ITEM, igual que la pintura del bote y por las
    /// mismas dos razones que allí se ganaron gratis: es propiedad de INSTANCIA (cada linterna con
    /// la suya, se guarda, sobrevive al saqueo del cadáver) y la UI del vendor que pinta el estado
    /// del wieldable equipado ya la lee. Cero interfaz que escribir.
    ///
    /// EL PARPADEO VA POR `intensity`, NUNCA POR `Light.enabled`, y esto no es una preferencia de
    /// estilo: ADR-042 relaya a los peers un `light_on` que el cliente calcula como "¿hay alguna
    /// `Light` HABILITADA bajo el wieldable activo?" a 10 Hz. Apagar el componente para hacer un
    /// destello pone ese bit a bailar — los demás jugadores verían un estrobo y la detección por
    /// linterna de ADR-080 (te ven a 38 m) te perdería y te recuperaría en cada muestra. Con
    /// `intensity` el bit se queda quieto en `true` y el parpadeo es cosa tuya y de nadie más.
    /// El interruptor de verdad SÍ toca `enabled`, que es exactamente lo que debe relayarse.
    ///
    /// NADA AQUÍ ES DETERMINISTA A PROPÓSITO. Joel pidió que la cuerda no rinda siempre igual y que
    /// la batería se note vieja: hay tres aleatorios y ninguno toca worldgen ni viaja por el wire.
    /// El único que PERSISTE es la salud de la batería, que es propiedad de item y por eso hace que
    /// encontrar otra linterna signifique algo.
    /// </summary>
    [AddComponentMenu("Backrooms/Crank Flashlight")]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault2)]
    public sealed class CrankFlashlightWieldable : Wieldable, IUseInputHandler, IAimInputHandler
    {
        [Header("Modelo")]
        [Tooltip("La manivela: el hijo que gira. Su PIVOTE tiene que estar en el eje de giro, no " +
                 "en el centro de la pieza, o la manivela orbitará en vez de girar.")]
        [SerializeField] private Transform crank;

        [Tooltip("Eje de giro de la manivela, en espacio LOCAL de la propia manivela. Con la malla " +
                 "canónica que hornea el aplicador —brazo hacia +Y desde el eje— el barrido es " +
                 "sobre Z.")]
        [SerializeField] private Vector3 crankAxis = Vector3.forward;

        [Tooltip("El haz. Se busca en los hijos si se deja vacío.")]
        [SerializeField] private Light beam;

        [Header("Cuerda")]
        [Tooltip("Vueltas por segundo mientras se mantiene el botón de uso.")]
        [SerializeField, Range(0.2f, 4f)] private float revolutionsPerSecond = 1f;

        [Tooltip("Segundos de luz que entrega UNA vuelta, mínimo. Sortea entre mín y máx en cada " +
                 "vuelta: no se da la cuerda igual dos veces.")]
        [SerializeField, Range(1f, 60f)] private float secondsPerRevolutionMin = 18f;

        [SerializeField, Range(1f, 60f)] private float secondsPerRevolutionMax = 22f;

        [Tooltip("Multiplicador de velocidad de movimiento mientras se da cuerda. Una mano " +
                 "ocupada: andas, no huyes.")]
        [SerializeField, Range(0f, 1f)] private float crankSpeedMultiplier = 0.4f;

        [Header("Batería")]
        [Tooltip("Segundos de luz de una carga LLENA con la batería a estrenar. Lo que rinde de " +
                 "verdad es esto por la salud de la batería.")]
        [SerializeField, Range(10f, 600f)] private float fullChargeSeconds = 60f;

        [Tooltip("Salud de batería que se sortea la primera vez que se empuña una linterna que " +
                 "aún no la tiene. Se queda escrita en el item: una linterna vieja lo será siempre.")]
        [SerializeField, Range(0.1f, 1f)] private float batteryHealthMin = 0.8f;

        [SerializeField, Range(0.1f, 1f)] private float batteryHealthMax = 1f;

        [Header("Haz")]
        [Tooltip("Intensidad del haz con carga sana. El parpadeo y la reserva la multiplican.")]
        [SerializeField, Range(0f, 20f)] private float beamIntensity = 4f;

        [Tooltip("Fracción de carga por debajo de la cual el haz baja de intensidad. Es el aviso " +
                 "que se ve sin mirar la barra.")]
        [SerializeField, Range(0f, 0.5f)] private float lowChargeFraction = 0.15f;

        [SerializeField, Range(0.1f, 1f)] private float lowChargeDim = 0.55f;

        [Header("Parpadeo al dar cuerda")]
        [Tooltip("A cuánto cae la intensidad en un destello.")]
        [SerializeField, Range(0f, 1f)] private float flickerDipMin = 0.15f;

        [SerializeField, Range(0f, 1f)] private float flickerDipMax = 0.45f;

        [Tooltip("Duración de un destello, en segundos.")]
        [SerializeField] private Vector2 flickerDipSeconds = new(0.04f, 0.16f);

        [Tooltip("Hueco entre destellos, en segundos.")]
        [SerializeField] private Vector2 flickerGapSeconds = new(0.2f, 0.8f);

        /// <summary>
        /// Nombre de la propiedad de item que guarda la salud de la batería. Se resuelve por NOMBRE
        /// y perezosamente porque la definición la crea un menú de editor: una linterna en un
        /// proyecto sin ella rinde el 100 % y no revienta.
        /// </summary>
        private const string BatteryHealthPropertyName = "Battery Health";

        /// <summary>Deriva lenta del gasto, ±5 %. Ni la barra tiembla ni el gasto es un reloj.</summary>
        private const float DrainDriftAmplitude = 0.05f;
        private const float DrainDriftSpeed = 0.15f;

        private IWieldableItem _wieldableItem;
        private ItemProperty _charge;
        private float _fallbackCharge = 1f;
        private float _batteryHealth = 1f;

        private bool _isCranking;
        private bool _beamOn = true;
        private float _crankAngle;
        private float _driftSeed;

        private float _flickerFactor = 1f;
        private float _flickerTimer;
        private bool _inDip;

        public ActionBlockHandler UseBlocker { get; } = new();
        public ActionBlockHandler AimBlocker { get; } = new();

        /// <summary>Se está dando cuerda AHORA. Lo lee el modificador de velocidad y, en el bloque
        /// B, el bit 6 de `buttons` que verán los demás.</summary>
        public bool IsCranking => _isCranking;

        public bool IsUsing => _isCranking;
        public bool IsAiming => false;

        /// <summary>Carga restante, 0..1. Es lo que la UI del vendor dibuja como barra.</summary>
        public float Charge
        {
            get
            {
                var p = ChargeProperty;
                return Mathf.Clamp01(p != null ? p.Float : _fallbackCharge);
            }
            private set
            {
                float v = Mathf.Clamp01(value);
                var p = ChargeProperty;
                if (p != null) p.Float = v;
                else _fallbackCharge = v;
            }
        }

        /// <summary>Segundos de luz que da una carga llena ESTA linterna, ya con su batería.</summary>
        public float CapacitySeconds => Mathf.Max(1f, fullChargeSeconds * _batteryHealth);

        /// <summary>Salud de la batería de esta linterna, 0..1. Persistida en el item.</summary>
        public float BatteryHealth => _batteryHealth;

        /// <summary>
        /// Una vuelta completa de manivela. El bloque B cuelga de aquí el chirrido y el estímulo
        /// de ruido de ADR-041; el bloque A sólo la anuncia.
        /// </summary>
        public event System.Action CrankRevolutionCompleted;

        private ItemProperty ChargeProperty
        {
            get
            {
                if (_wieldableItem == null) return null;
                var item = _wieldableItem.Slot.GetItem();
                return item != null && item.TryGetProperty(ItemConstants.Durability, out var p) ? p : null;
            }
        }

        /// <summary>
        /// El bloqueo de uso del vendor es el que impide dar cuerda corriendo: un
        /// <c>MovementUseBlocker</c> con "usar mientras corres" apagado pone el blocker, y aquí se
        /// lee sin preguntar por el estado de movimiento. Misma razón que el bote: nadie cablea
        /// "¿estoy corriendo?" dos veces.
        /// </summary>
        public override bool IsCrosshairActive() => !UseBlocker.IsBlocked;

        public bool Use(WieldableInputPhase inputPhase)
        {
            switch (inputPhase)
            {
                case WieldableInputPhase.Start:
                case WieldableInputPhase.Hold:
                    if (UseBlocker.IsBlocked)
                    {
                        _isCranking = false;
                        return false;
                    }
                    _isCranking = true;
                    return true;

                default:
                    _isCranking = false;
                    return false;
            }
        }

        /// <summary>
        /// El interruptor. Va en el botón de apuntar porque el de uso ya es la manivela, y porque
        /// una linterna sin interruptor obliga a vaciarla para dejar de delatarte (ADR-080).
        /// Sólo en `Start`: un mantenido apagaría y encendería a 60 Hz.
        /// </summary>
        public bool Aim(WieldableInputPhase inputPhase)
        {
            if (inputPhase != WieldableInputPhase.Start || AimBlocker.IsBlocked)
                return false;

            _beamOn = !_beamOn;
            return true;
        }

        private void Start()
        {
            _driftSeed = Random.value * 1000f;
            SpeedModifier.AddModifier(() => _isCranking ? crankSpeedMultiplier : 1f);

            _wieldableItem = GetComponent<IWieldableItem>();
            if (_wieldableItem == null)
            {
                Debug.LogError("[CrankFlashlight] Sin IWieldableItem en el wieldable: la carga no " +
                               "vivirá en el item y no se guardará.", gameObject);
            }
            else
            {
                _wieldableItem.AttachedSlotChanged += OnAttachedSlotChanged;
                OnAttachedSlotChanged(_wieldableItem.Slot);
            }

            // Reserva por si el prefab llega sin la referencia puesta. Se exige SPOT: buscar
            // cualquier `Light` incluyendo inactivas se quedaría con la luz de fuego que la
            // linterna hereda de la antorcha (apagada, pero ahí), y la linterna alumbraría naranja
            // desde el puño.
            if (beam == null)
            {
                foreach (var candidate in GetComponentsInChildren<Light>(true))
                {
                    if (candidate.type != LightType.Spot) continue;
                    beam = candidate;
                    break;
                }
            }

            if (beam == null)
                Debug.LogError("[CrankFlashlight] Sin Light bajo el wieldable: no alumbra y los " +
                               "peers no verán nada (ADR-042 lee luces, no items).", gameObject);
        }

        private void OnDisable() => _isCranking = false;

        /// <summary>
        /// La salud de la batería se sortea UNA vez por linterna y se escribe en el item. Si la
        /// propiedad no existe en el proyecto, o el item aún no está, se rinde al 100 %: es
        /// degradación, no fallo.
        /// </summary>
        private void OnAttachedSlotChanged(SlotReference slot)
        {
            _batteryHealth = 1f;

            if (!slot.TryGetItem(out var item))
                return;

            var definition = ItemPropertyDefinition.GetWithName(BatteryHealthPropertyName);
            if (definition == null || !item.TryGetProperty(definition.Id, out var health))
                return;

            if (health.Float < 0.01f)
                health.Float = Random.Range(batteryHealthMin, batteryHealthMax);

            _batteryHealth = Mathf.Clamp(health.Float, 0.1f, 1f);
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            UpdateCrank(dt);
            UpdateCharge(dt);
            UpdateFlicker(dt);
            UpdateBeam();
        }

        /// <summary>
        /// La manivela gira mientras se mantiene, y la carga entra POR VUELTA COMPLETA, no de forma
        /// continua: así el jugador ve el escalón y aprende cuánto vale una vuelta. Lo que sortea
        /// cada vuelta es cuánto vale, entre el mínimo y el máximo.
        /// </summary>
        private void UpdateCrank(float dt)
        {
            if (!_isCranking)
                return;

            float delta = revolutionsPerSecond * 360f * dt;
            _crankAngle += delta;

            if (crank != null && crankAxis.sqrMagnitude > 0.0001f)
                crank.localRotation *= Quaternion.AngleAxis(delta, crankAxis.normalized);

            while (_crankAngle >= 360f)
            {
                _crankAngle -= 360f;

                float min = Mathf.Min(secondsPerRevolutionMin, secondsPerRevolutionMax);
                float max = Mathf.Max(secondsPerRevolutionMin, secondsPerRevolutionMax);
                Charge += Random.Range(min, max) / CapacitySeconds;

                CrankRevolutionCompleted?.Invoke();
            }
        }

        /// <summary>
        /// Gasta sólo cuando alumbra de verdad. La deriva es ruido Perlin y no un aleatorio por
        /// fotograma: tiene que ser una batería que rinde algo distinto cada rato, no una barra
        /// que tiembla.
        /// </summary>
        private void UpdateCharge(float dt)
        {
            if (!_beamOn)
                return;

            float charge = Charge;
            if (charge <= 0f)
                return;

            float drift = 1f + DrainDriftAmplitude *
                (Mathf.PerlinNoise(Time.time * DrainDriftSpeed, _driftSeed) * 2f - 1f);

            Charge = charge - dt / CapacitySeconds * drift;
        }

        /// <summary>
        /// Destellos aleatorios mientras se da cuerda: la dinamo vieja protesta. Alterna hueco y
        /// bajón, ambos con duración sorteada — un patrón regular se lee como un efecto y no como
        /// una avería.
        /// </summary>
        private void UpdateFlicker(float dt)
        {
            if (!_isCranking)
            {
                _flickerFactor = 1f;
                _flickerTimer = 0f;
                _inDip = false;
                return;
            }

            _flickerTimer -= dt;
            if (_flickerTimer > 0f)
                return;

            _inDip = !_inDip;

            if (_inDip)
            {
                _flickerFactor = Random.Range(Mathf.Min(flickerDipMin, flickerDipMax),
                                              Mathf.Max(flickerDipMin, flickerDipMax));
                _flickerTimer = Random.Range(flickerDipSeconds.x, flickerDipSeconds.y);
            }
            else
            {
                _flickerFactor = 1f;
                _flickerTimer = Random.Range(flickerGapSeconds.x, flickerGapSeconds.y);
            }
        }

        /// <summary>
        /// `enabled` sólo cambia con el interruptor o al agotarse la carga — que es exactamente lo
        /// que los demás deben ver. Todo lo demás pasa por `intensity`. Ver la cabecera.
        /// </summary>
        private void UpdateBeam()
        {
            if (beam == null)
                return;

            float charge = Charge;
            bool lit = _beamOn && charge > 0f;

            if (beam.enabled != lit)
                beam.enabled = lit;

            if (!lit)
                return;

            float reserve = charge < lowChargeFraction
                ? Mathf.Lerp(lowChargeDim, 1f, charge / Mathf.Max(0.0001f, lowChargeFraction))
                : 1f;

            beam.intensity = beamIntensity * _flickerFactor * reserve;
        }

        #region Editor
#if UNITY_EDITOR
        protected override void DrawDebugGUI()
        {
            GUILayout.Label($"Carga: {Charge:P0} de {CapacitySeconds:F0} s");
            GUILayout.Label($"Batería: {_batteryHealth:P0}");
            GUILayout.Label($"Cuerda: {_isCranking} · haz: {_beamOn}");
            GUILayout.Label($"Velocidad: {SpeedModifier.EvaluateValue():F2}");
        }
#endif
        #endregion
    }
}
