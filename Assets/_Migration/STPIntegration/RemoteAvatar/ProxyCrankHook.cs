using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-133 — que se VEA que otro jugador está dando cuerda a su linterna: la manivela del
    /// modelo que lleva en la mano gira mientras el bit <see cref="RemoteButtons.Cranking"/> está
    /// puesto. Es el momento en que el vecino no puede correr, y verlo es lo que lo hace legible.
    ///
    /// NO ESTÁ EN EL PREFAB DEL AVATAR, y es la única razón de que este hook sea distinto de los
    /// demás de esta carpeta. Los otros los cuelga el builder al hornear, y rehornear el avatar es
    /// la operación que ya se llevó por delante once `m_IsKinematic` puestos a mano. Éste lo añade
    /// <see cref="ProxyHeldItemHook"/> en runtime, SOBRE EL MODELO DE MANO que acaba de instanciar,
    /// cuando ese modelo trae un hijo llamado "Crank" — así que vive y muere con el item en la
    /// mano, y un peer que no lleva linterna no lo tiene nunca.
    ///
    /// Tampoco podía ir en el prefab del PICKUP: `NeutralizeToVisualOnly` destruye todos los
    /// MonoBehaviour del modelo instanciado, y con razón — un pickup en la mano de otro no es
    /// recogible. De ahí que se añada DESPUÉS de neutralizar.
    ///
    /// Item-agnóstico por diseño, como todo lo de aquí: nunca pregunta «¿es la linterna?», pregunta
    /// «¿el modelo tiene una manivela?». Un molinillo, una radio de cuerda o un torno con un hijo
    /// "Crank" girarían igual el día que existan.
    ///
    /// La velocidad es una constante y no viaja: es cosmética, y el número es el mismo con el que
    /// gira la del propio jugador (`CrankFlashlightWieldable.revolutionsPerSecond`, 1 vuelta/s).
    /// Si algún día se hace configurable, el sitio es el item, no el wire.
    /// </summary>
    public sealed class ProxyCrankHook : MonoBehaviour
    {
        /// <summary>Nombre del hijo que gira. El mismo que ponen el aplicador del modelo y el
        /// creador del pickup: ver <c>BackroomsCrankFlashlightModelApplier.CrankNodeName</c>.</summary>
        public const string CrankNodeName = "Crank";

        /// <summary>Una vuelta por segundo, como la del propio jugador.</summary>
        private const float DegreesPerSecond = 360f;

        /// <summary>Eje de giro en local de la manivela: la normal de su disco, +Z en la malla
        /// canónica (el nodo va montado girado 90° en Y, así que cae perpendicular al costado).
        /// El mismo que escribe el aplicador en el wieldable (`CrankSpinAxis`); no hay forma de
        /// compartir la constante porque este assembly no ve el de Editor — el test
        /// `TheCrankSpinsOnTheSameAxisInHandAndOnTheProxy` es lo que los ata.</summary>
        private static readonly Vector3 Axis = Vector3.forward;

        private Transform _proxyRoot;
        private Transform _crank;
        private RemotePlayerManager _manager;

        /// <summary>
        /// Busca la manivela en <paramref name="heldModel"/> y, si la hay, cuelga el hook de ese
        /// mismo objeto. Devuelve null cuando no hay manivela: el llamador no tiene que saber nada.
        /// </summary>
        public static ProxyCrankHook AttachIfCranked(GameObject heldModel, Transform proxyRoot)
        {
            if (heldModel == null || proxyRoot == null)
                return null;

            Transform crank = null;
            foreach (var t in heldModel.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != CrankNodeName) continue;
                crank = t;
                break;
            }
            if (crank == null)
                return null;

            var hook = heldModel.AddComponent<ProxyCrankHook>();
            hook._proxyRoot = proxyRoot;
            hook._crank = crank;
            return hook;
        }

        private void LateUpdate()
        {
            if (_crank == null || _proxyRoot == null) return;
            if (!ProxyViewLookup.TryResolve(_proxyRoot, ref _manager, out var view)) return;

            bool cranking = RemoteButtons.Has(view.buttons, RemoteButtons.Cranking) && !view.dead;
            if (!cranking) return;

            // Se deja donde para, como una manivela de verdad: nadie la devuelve al reposo al
            // soltarla, y un salto de vuelta cantaría más que un ángulo cualquiera.
            _crank.localRotation *= Quaternion.AngleAxis(DegreesPerSecond * Time.deltaTime, Axis);
        }
    }
}
