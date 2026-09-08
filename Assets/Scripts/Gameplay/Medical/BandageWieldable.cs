using System.Collections;
using PolymindGames;
using PolymindGames.WieldableSystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Medical
{
    /// <summary>
    /// La venda: una acción de varios segundos que cierra la herida de UNA zona corporal y deja
    /// puesto un visual que los demás también ven.
    ///
    /// POR QUÉ NO HEREDA DE <c>HealingWieldable</c>, que hace casi esto: la clase del vendor es
    /// <c>sealed</c>, y aunque no lo fuera cura un escalar y no sabe de zonas. Lo que se copia de
    /// ella es su forma, que está bien resuelta: corrutina con espera, cancelación por el mismo
    /// botón, modificador de velocidad mientras dura y consumo del item SÓLO al completar.
    ///
    /// SE HEREDA DE <see cref="Wieldable"/> Y NO SE CUELGA UN PUENTE, por la misma razón que la
    /// linterna de manivela: <c>FPSWieldablesInput</c> hace <c>wieldable as IUseInputHandler</c>
    /// sobre el componente wieldable y no busca en los hijos, así que un puente nunca vería el
    /// botón. Heredar de la clase del vendor sin editarla es legal en esta dirección — nuestro
    /// asmdef referencia a PolymindGames y el suyo no puede referenciarnos (STATE.md ▸ NO tocar).
    ///
    /// EL ESTADO NO VIVE AQUÍ. Este componente escribe en <see cref="PlayerMedicalState.Local"/> y
    /// se olvida; quien pinta la venda en los brazos de primera persona y quien la manda a los
    /// peers leen ese mismo objeto. Por eso guardar el arma, cambiar de cámara o que otro jugador
    /// entre en rango no borra nada: la venda no es un efecto de esta acción, es un estado que esta
    /// acción provocó.
    ///
    /// SIN ANIMACIÓN, y es alcance cerrado de Alpha 1 (Joel): ni la de vendarse ni la de sacar la
    /// venda. Lo que hay es el TIEMPO — el jugador se queda quieto y lento unos segundos y, si le
    /// interrumpen, no ha vendado nada y no ha gastado la venda.
    /// </summary>
    [AddComponentMenu("Backrooms/Bandage")]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault2)]
    public sealed class BandageWieldable : Wieldable, IUseInputHandler
    {
        [Header("Aplicación")]
        [Tooltip("Segundos que hay que aguantar para que la venda quede puesta. Es LA propiedad " +
                 "que se va a tocar al balancear, por eso está aquí y no en una constante.")]
        [SerializeField, Range(0.5f, 15f)] private float applySeconds = 4f;

        [Tooltip("Multiplicador de velocidad de movimiento mientras se venda. Las dos manos " +
                 "ocupadas: andas, no corres.")]
        [SerializeField, Range(0f, 1f)] private float applySpeedMultiplier = 0.5f;

        [Header("Salud")]
        [Tooltip("Salud que devuelve una venda al completarse. Cero = sólo cierra la herida.")]
        [SerializeField, Range(0f, 100f)] private float healAmount = 25f;

        [Header("Audio")]
        [Tooltip("Se reproduce al EMPEZAR a vendar. Vacío = silencio, sin error.")]
        [SerializeField] private AudioSequence applyAudio;

        private Coroutine _applyRoutine;
        private IWieldableItem _wieldableItem;

        /// <summary>Está vendando ahora mismo.</summary>
        public bool IsApplying => _applyRoutine != null;

        /// <summary>La zona que se está tratando. Sólo vale mientras <see cref="IsApplying"/>.</summary>
        public BodyPartSide TargetSide { get; private set; }

        public ActionBlockHandler UseBlocker { get; } = new ActionBlockHandler();

        public bool IsUsing => IsApplying;

        /// <summary>
        /// Un solo botón para las dos cosas: empieza si no se está vendando, cancela si sí. Es el
        /// mismo trato que ofrece la clase del vendor —allí el botón sólo cancela, porque la acción
        /// la lanza un gestor aparte— y aquí no hay gestor: la venda se equipa y se usa.
        ///
        /// Sólo se atiende <c>Start</c>. El mantenido no aporta nada: lo que exige la acción es
        /// SEGUIR con la venda equipada, no seguir apretando.
        /// </summary>
        public bool Use(WieldableInputPhase inputPhase)
        {
            if (inputPhase != WieldableInputPhase.Start)
                return false;

            if (IsApplying)
            {
                CancelApply();
                return true;
            }

            return TryBeginApply();
        }

        /// <summary>Sin herida que tratar no se puede usar: no se gasta una venda en un brazo sano.</summary>
        public bool CanApplyNow() => !UseBlocker.IsBlocked && PlayerMedicalState.Local.HasTreatableWound;

        public override bool IsCrosshairActive() => !IsApplying;

        private void Awake()
        {
            _wieldableItem = GetComponent<IWieldableItem>();
        }

        private void Start()
        {
            SpeedModifier.AddModifier(() => IsApplying ? applySpeedMultiplier : 1f);
        }

        // Guardar el arma, morir o que el rig se reconstruya cancelan. La regla del enunciado es
        // literal: si la acción se interrumpe antes de completarse, no se aplica la venda — y como
        // el item se consume DENTRO de la corrutina, cortarla aquí también significa no gastarla.
        private void OnDisable() => CancelApply();

        private bool TryBeginApply()
        {
            if (!CanApplyNow())
                return false;

            if (!PlayerMedicalState.Local.TryGetWoundedSide(out var side))
                return false;

            TargetSide = side;
            _applyRoutine = StartCoroutine(ApplyDelayed(side));
            if (applyAudio != null)
                Audio.PlayClips(applyAudio, BodyPoint.Hands);
            return true;
        }

        private void CancelApply()
        {
            CoroutineUtility.StopCoroutine(this, ref _applyRoutine);
        }

        private IEnumerator ApplyDelayed(BodyPartSide side)
        {
            yield return new WaitForTime(applySeconds);

            // Se revalida la zona al FINAL, no sólo al empezar: entre medias el jugador ha podido
            // morir (que limpia el estado) o recibir otro golpe. `ApplyBandage` devuelve false si
            // ahí ya no hay herida, y entonces no se cobra la venda.
            if (!PlayerMedicalState.Local.ApplyBandage(side))
            {
                _applyRoutine = null;
                yield break;
            }

            if (healAmount > 0f && Character != null)
                Character.HealthManager.RestoreHealth(healAmount);

            ConsumeOne();
            _applyRoutine = null;
        }

        /// <summary>
        /// Gasta una venda del hueco al que está atado este wieldable. Se hace al COMPLETAR, nunca
        /// al empezar: una acción interrumpida no cuesta material.
        /// </summary>
        private void ConsumeOne()
        {
            if (_wieldableItem == null)
                return;

            var slot = _wieldableItem.Slot;
            if (slot.HasItem())
                slot.AdjustStack(-1);
        }
    }
}
