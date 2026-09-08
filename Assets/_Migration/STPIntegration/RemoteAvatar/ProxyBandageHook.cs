using BackroomsSurvival.Gameplay.Medical;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// La venda del VECINO: pinta en el avatar remoto la venda que ese jugador lleva puesta.
    ///
    /// LO QUE VIAJA ES UN BIT POR BRAZO, en los bits libres de <c>buttons</c> (ADR-044): cero bytes
    /// de wire, cero campos nuevos, cero cambios en el backend, que relaya <c>buttons</c> sin
    /// mirarlo. La misma cuenta que salió para el chorro de pintura y para la cuerda de la linterna.
    ///
    /// Y ES UN ESTADO, NO UN EFECTO, que es la diferencia con esos dos: el chorro se ve mientras se
    /// pinta y luego se apaga, pero la venda sigue puesta. Por eso da igual cuándo entres en rango
    /// del vecino — el bit viaja en CADA pose, así que un jugador que se vendó hace diez minutos
    /// aparece vendado en el primer datagrama que recibas de él, sin nada que reenviar ni recordar.
    ///
    /// La banda la construye <see cref="BandageVisual"/>, el mismo código que la pone en los brazos
    /// de primera persona: lo que tú te ves y lo que te ven es literalmente la misma malla.
    ///
    /// Perezoso: un peer que nunca se venda no crea ni un GameObject. Y las bandas cuelgan de los
    /// huesos del proxy, así que un proxy reciclado por el pool las conserva — de ahí el rearme de
    /// <c>OnEnable</c>, para que el jugador nuevo no herede la venda del anterior ni un fotograma.
    /// </summary>
    public sealed class ProxyBandageHook : MonoBehaviour
    {
        [Header("Anclaje")]
        [Tooltip("Antebrazo izquierdo del cuerpo en tercera persona. Ojo: NO se llama igual que en " +
                 "los brazos de primera persona (allí es Forearm.L).")]
        [SerializeField] private string _leftForearmBone = "LowerArm.L";

        [SerializeField] private string _rightForearmBone = "LowerArm.R";

        [Header("Banda")]
        [Tooltip("Radio de la banda. Es el del PROXY, casi la mitad que el de primera " +
                 "persona: aquellos brazos son el viewmodel y estos un humano a escala.")]
        [SerializeField, Min(0f)] private float _radius = BandageVisual.ProxyRadius;
        [SerializeField, Min(0f)] private float _length = BandageVisual.DefaultLength;
        [SerializeField, Range(0f, 1f)] private float _alongBone = BandageVisual.DefaultAlongBone;

        private RemotePlayerManager _manager;
        private Transform _leftForearm;
        private Transform _rightForearm;
        private GameObject _leftBandage;
        private GameObject _rightBandage;

        private void Awake()
        {
            _leftForearm = ProxyRigUtil.FindBone(transform, _leftForearmBone);
            _rightForearm = ProxyRigUtil.FindBone(transform, _rightForearmBone);
        }

        private void OnEnable()
        {
            if (_leftBandage != null) _leftBandage.SetActive(false);
            if (_rightBandage != null) _rightBandage.SetActive(false);
        }

        // LateUpdate: los huesos los escribe el Animator y la banda cuelga de uno de ellos. Aquí no
        // se recoloca nada cada frame —el emparentado ya la lleva— sólo se decide si se ve.
        private void LateUpdate()
        {
            if (_leftForearm == null && _rightForearm == null)
                return;
            if (!ProxyViewLookup.TryResolve(transform, ref _manager, out var view))
                return;

            // Un cadáver no lleva venda pintada: el mismo criterio que el resto de hooks, que
            // apagan su visual con `view.dead` en vez de dejarlo colgado sobre el ragdoll.
            bool alive = !view.dead;
            Show(ref _leftBandage, _leftForearm,
                 alive && RemoteButtons.Has(view.buttons, RemoteButtons.BandagedArmLeft));
            Show(ref _rightBandage, _rightForearm,
                 alive && RemoteButtons.Has(view.buttons, RemoteButtons.BandagedArmRight));
        }

        private void Show(ref GameObject bandage, Transform forearm, bool visible)
        {
            if (forearm == null)
                return;

            if (bandage == null)
            {
                if (!visible)
                    return;

                bandage = BandageVisual.Attach(forearm, _radius, _length, _alongBone);
                if (bandage == null)
                    return;
            }

            if (bandage.activeSelf != visible)
                bandage.SetActive(visible);
        }
    }
}
