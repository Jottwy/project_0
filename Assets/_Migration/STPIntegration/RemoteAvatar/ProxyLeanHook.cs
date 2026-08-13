using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// Muestra la inclinación lateral (Q/E) de un peer, leída de los bits de lean de
    /// <c>view.buttons</c>. Es un ESTADO SOSTENIDO, así que viaja como bit y no como contador,
    /// exactamente el caso que ADR-044 reservó al dejar 14 bits libres: cero campos nuevos en el
    /// wire y cero bump de esquema.
    ///
    /// Vale la pena verlo por la misma razón que se inclina uno: asomarse por una esquina expone
    /// cabeza y hombros y deja el resto del cuerpo a cubierto. Si el proxy no se inclina, el
    /// observador ve un cuerpo recto donde el otro jugador cree tener medio cuerpo protegido, y
    /// las dos partidas dejan de contar la misma historia.
    ///
    /// Pose procedural, no clip — mismo criterio que ProxyStanceHook y ProxyMeleeHook: el Animator
    /// del proxy no tiene estado de lean y el builder rehace su controller en cada bake. Se rota la
    /// columna alrededor del eje FORWARD del avatar (el de alabeo), repartiendo el ángulo
    /// raíz→hoja para que la curva salga progresiva en vez de una bisagra. Los bones se resuelven
    /// POR NOMBRE (rig Generic) y un bone ausente es un no-op silencioso.
    ///
    /// Corre en LateUpdate DESPUÉS del Animator, como el resto de hooks de pose, y mezcla la
    /// entrada/salida en <see cref="_blendTime"/>. NO lleva centinela y no le hace falta: es un
    /// nivel, y un proxy recién sacado del pool empieza con buttons 0, que es exactamente
    /// "centrado" — el estado honesto y no uno supuesto.
    ///
    /// Ortogonal a ProxyPitchHook a propósito: aquél rota sobre <c>transform.right</c> (cabeceo) y
    /// éste sobre <c>transform.forward</c> (alabeo), así que ambos pueden aplicarse el mismo frame
    /// sobre los mismos bones sin pisarse.
    ///
    /// Removable: borra el archivo y los peers se inclinan invisiblemente; nada más cambia.
    /// </summary>
    public sealed class ProxyLeanHook : MonoBehaviour
    {
        [Header("Pose")]
        [Tooltip("Grados que se inclina el torso con el lean al máximo.")]
        [SerializeField, Min(0f)] private float _leanAngle = 20f;

        [Tooltip("Grados que acompaña la cabeza, encima del torso.")]
        [SerializeField, Min(0f)] private float _headAngle = 6f;

        [Header("Blending")]
        [Tooltip("Segundos para entrar o salir de la inclinación. Un salto seco se lee como un tirón.")]
        [SerializeField, Min(0.01f)] private float _blendTime = 0.15f;

        [Tooltip("Invierte el lado. YA CALIBRADO: el default (desactivado) es el correcto; esto " +
                 "solo existe por si un rig futuro trae los bones al revés.")]
        [SerializeField] private bool _invert;

        // Reparto raíz→hoja del ángulo de torso: la columna media abre la curva y la alta la cierra.
        private const float MiddleShare = 0.45f;
        private const float UpperShare = 0.55f;

        private RemotePlayerManager _manager;
        private Transform _middleSpine, _upperSpine, _head;
        private bool _hasRig;
        private float _current;

        private void Awake()
        {
            _middleSpine = FindBone("MiddleSpine");
            _upperSpine = FindBone("UpperSpine");
            _head = FindBone("Head");
            _hasRig = _middleSpine != null || _upperSpine != null;
        }

        // Re-arma para reuso de pool: un proxy reciclado nunca hereda la inclinación del anterior.
        private void OnEnable() => _current = 0f;

        private void LateUpdate()
        {
            if (!_hasRig)
                return;

            int buttons = ResolveButtons();

            // Los dos bits son excluyentes en origen (BodyLeanState es un enum), pero si llegaran
            // ambos por un peer que los falsee, "centrado" es la respuesta honesta.
            bool left = RemoteButtons.Has(buttons, RemoteButtons.LeanLeft);
            bool right = RemoteButtons.Has(buttons, RemoteButtons.LeanRight);
            float target = left == right ? 0f : (left ? -1f : 1f);

            float step = Time.deltaTime / Mathf.Max(0.01f, _blendTime);
            _current = Mathf.MoveTowards(_current, target, step);

            if (Mathf.Approximately(_current, 0f))
                return; // centrado del todo: no toca ningún bone, así que no pelea con los otros hooks

            // EL SIGNO BASE ES NEGATIVO Y ESTÁ MEDIDO, no elegido: una rotación POSITIVA alrededor
            // de `forward` sube el costado derecho y tumba el torso hacia su IZQUIERDA, o sea
            // justo al revés de lo que pide LeanRight. Comprobado en playtest de dos clientes
            // (2026-08-14): con el signo positivo el avatar remoto se inclinaba al lado contrario.
            // `_invert` se queda como escape de calibración, pero el default ya es el correcto.
            float w = (_invert ? 1f : -1f) * _current;
            Vector3 axis = transform.forward; // eje de alabeo del avatar, ya orientado en yaw

            ApplyBend(_middleSpine, _leanAngle * MiddleShare * w, axis);
            ApplyBend(_upperSpine, _leanAngle * UpperShare * w, axis);
            ApplyBend(_head, _headAngle * w, axis);
        }

        /// <summary>Los bits de este proxy, vía la vista de RemotePlayerManager cuya raíz somos —
        /// mismo lookup que el resto de hooks.</summary>
        private int ResolveButtons()
        {
            if (!ProxyViewLookup.TryResolve(transform, ref _manager, out var view))
                return 0;

            return view.buttons;
        }

        private static void ApplyBend(Transform bone, float degrees, Vector3 axis) =>
            ProxyRigUtil.ApplyBend(bone, degrees, axis);

        private Transform FindBone(string boneName) => ProxyRigUtil.FindBone(transform, boneName);
    }
}
