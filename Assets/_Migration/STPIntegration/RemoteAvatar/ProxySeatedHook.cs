using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-131 D5/D6 — el VIGILANTE sentado: la pose, la cabeza que te sigue y la respiración.
    ///
    /// Se enciende con el bit <see cref="RemoteButtons.Seated"/> de <c>view.buttons</c>, que el
    /// servidor escribe una vez al dar de alta al peer (ADR-131 D4). Es un ESTADO SOSTENIDO en un
    /// campo que ya viajaba: ni campo nuevo, ni bump de esquema, ni una línea de Rust en el relay.
    ///
    /// Tres cosas, y ninguna viaja:
    ///
    /// 1. **La pose.** Un `AnimatorOverrideController` sustituye el clip de IDLE del
    ///    `ProxyLocomotionController` por la pose horneada (<c>FacelingSeatedPoseBuilder</c>). Sin
    ///    estado nuevo en el controller y sin transición: un vigilante no sale de idle porque no se
    ///    mueve, así que el árbol de locomoción se queda en su centro para siempre. Y el cuerpo baja
    ///    <see cref="_seatDrop"/> en LOCAL, porque la posición que manda el servidor es la del SUELO
    ///    (el asiento es geometría del cliente: la silla y la cadera de este esqueleto).
    ///
    /// 2. **La cabeza.** Sigue a la cámara local mientras esté dentro de ±<see cref="_coneDeg"/>
    ///    respecto del yaw del CUERPO — que no se gira nunca: mira a donde mira la silla. Cuando te
    ///    sales del cono, la cabeza **no vuelve suavemente**: SALTA en un fotograma a otra pose
    ///    sorteada y se queda ahí hasta que vuelvas a entrar. Un seguimiento que se pierde despacio
    ///    se lee como un muñeco mal orientado; una cabeza que ya está mirando a otro lado cuando te
    ///    vuelves se lee como que se movió mientras no mirabas, que es de lo que va esta especie.
    ///
    /// 3. **La respiración.** ±<see cref="_breathDeg"/> en el pecho, con fase por peer para que dos
    ///    vigilantes de la misma sala no respiren a la vez. Es lo ÚNICO que se mueve, y existe para
    ///    que la pose no se lea como una estatua ni como una animación colgada.
    ///
    /// Los huesos se resuelven por el Animator HUMANOID (`GetBoneTransform`) y no por nombre: el
    /// cuerpo del faceling y el del vendor son esqueletos distintos y sólo el rig humanoide los hace
    /// la misma pregunta. Un hueso ausente es un no-op silencioso, como en el resto de los hooks.
    ///
    /// Corre en LateUpdate DESPUÉS del Animator, igual que ProxyLeanHook y ProxyPitchHook, y sobre
    /// ejes distintos de los suyos — pero da igual: un vigilante nunca se inclina ni cabecea, porque
    /// nadie le escribe esos bits.
    ///
    /// Removable: borra el archivo y los vigilantes salen de pie, quietos y sin mirarte. Nada más
    /// cambia.
    /// </summary>
    public sealed class ProxySeatedHook : MonoBehaviour
    {
        [Header("Pose")]
        [Tooltip("La pose sentada horneada por 'Backrooms ▸ Facelings ▸ Build Seated Pose Clip'. " +
                 "La cablea RemoteAvatarPrefabBuilder; sin ella el vigilante sale de pie.")]
        [SerializeField] private AnimationClip _seatedClip;

        [Tooltip("Cuánto baja el cuerpo para que las nalgas queden en el asiento. La posición que " +
                 "manda el servidor es la del SUELO (ADR-131 D2): esto es la altura de la silla.")]
        [SerializeField, Min(0f)] private float _seatDrop = 0.45f;

        [Header("Cabeza")]
        [Tooltip("Medio cono de seguimiento, en grados de yaw respecto del cuerpo. ADR-131 D6: ±90.")]
        [SerializeField, Range(10f, 90f)] private float _coneDeg = 90f;

        [Tooltip("Tope de cabeceo, arriba y abajo.")]
        [SerializeField, Range(0f, 60f)] private float _pitchClampDeg = 35f;

        [Tooltip("Reparto del giro entre cuello y cabeza. El resto se lo lleva la cabeza.")]
        [SerializeField, Range(0f, 1f)] private float _neckShare = 0.4f;

        [Header("Respiración")]
        [Tooltip("Amplitud, en grados, del balanceo del pecho.")]
        [SerializeField, Min(0f)] private float _breathDeg = 0.8f;

        [Tooltip("Respiraciones por segundo.")]
        [SerializeField, Min(0.01f)] private float _breathHz = 0.22f;

        /// <summary>Poses de cabeza a las que salta cuando te sales del cono: (yaw, pitch) en
        /// grados respecto del cuerpo. Mirar a la mesa, a la mampara de la derecha, a la de la
        /// izquierda, al techo y al frente.</summary>
        private static readonly Vector2[] AwayPoses =
        {
            new Vector2(0f, 25f),
            new Vector2(55f, 5f),
            new Vector2(-55f, 5f),
            new Vector2(10f, -30f),
            new Vector2(0f, 0f),
        };

        private RemotePlayerManager _manager;
        private Animator _animator;
        private Transform _body, _head, _neck, _chest;
        private RuntimeAnimatorController _originalController;
        private AnimatorOverrideController _override;
        private Vector3 _bodyRest;
        private bool _seated;
        private bool _hadTarget;
        private Vector2 _awayPose;
        private float _phase;

        private void Awake()
        {
            foreach (var a in GetComponentsInChildren<Animator>(true))
            {
                if (!a.isHuman)
                    continue;
                _animator = a;
                break;
            }
            if (_animator == null)
                return;

            _head = _animator.GetBoneTransform(HumanBodyBones.Head);
            _neck = _animator.GetBoneTransform(HumanBodyBones.Neck);
            _chest = _animator.GetBoneTransform(HumanBodyBones.Chest)
                ?? _animator.GetBoneTransform(HumanBodyBones.Spine);

            // El cuerpo que se baja al asiento es el hijo que cuelga del raíz, no el raíz: el raíz
            // lo coloca RemotePlayerManager con la pose de red cada fotograma, así que cualquier
            // desplazamiento que se le escriba aquí lo pisa el siguiente paquete.
            _body = _animator.transform == transform ? null : TopChildOf(_animator.transform);
            if (_body != null)
                _bodyRest = _body.localPosition;
        }

        /// <summary>El hijo directo de este raíz del que cuelga <paramref name="descendant"/>.</summary>
        private Transform TopChildOf(Transform descendant)
        {
            var t = descendant;
            while (t != null && t.parent != transform)
                t = t.parent;
            return t;
        }

        // Un proxy reciclado del pool no hereda ni la pose ni el controller del anterior: `buttons`
        // vuelve a 0 al soltarlo, así que "de pie" es el estado honesto de arranque.
        private void OnEnable()
        {
            _seated = false;
            _hadTarget = false;
            _awayPose = Vector2.zero;
            // Fase por instancia y no por id: el id no está resuelto todavía en OnEnable, y lo único
            // que hace falta es que dos vigilantes no coincidan.
            _phase = Random.value * Mathf.PI * 2f;
            if (_body != null)
                _body.localPosition = _bodyRest;
        }

        private void OnDisable() => SetSeated(false);

        private void LateUpdate()
        {
            if (_animator == null)
                return;

            SetSeated(ResolveSeated());
            if (!_seated)
                return;

            AimHead();
            Breathe();
        }

        private bool ResolveSeated()
        {
            if (!ProxyViewLookup.TryResolve(transform, ref _manager, out var view))
                return false;
            return RemoteButtons.Has(view.buttons, RemoteButtons.Seated);
        }

        private void SetSeated(bool value)
        {
            if (value == _seated)
                return;
            _seated = value;

            if (value)
            {
                ApplyOverride();
                if (_body != null)
                    _body.localPosition = _bodyRest + Vector3.down * _seatDrop;
            }
            else
            {
                if (_originalController != null)
                    _animator.runtimeAnimatorController = _originalController;
                if (_body != null)
                    _body.localPosition = _bodyRest;
            }
        }

        /// <summary>
        /// Sustituye el clip de idle por la pose. El override se construye UNA vez por proxy y se
        /// reutiliza: crear un `AnimatorOverrideController` reinstancia el estado del Animator, y
        /// hacerlo cada vez que el bit parpadeara daría un tirón por paquete.
        /// </summary>
        private void ApplyOverride()
        {
            if (_seatedClip == null)
                return;

            if (_override == null)
            {
                _originalController = _animator.runtimeAnimatorController;
                if (_originalController == null)
                    return;

                _override = new AnimatorOverrideController(_originalController)
                {
                    name = _originalController.name + " (Seated)"
                };

                // El clip de IDLE, por nombre: es el único del controller que un vigilante llega a
                // reproducir (el árbol de locomoción se queda en su centro). Si el bake del
                // controller le cambia el nombre al idle, esto deja de encontrarlo y el vigilante
                // sale de pie — visible, y por eso se avisa.
                bool replaced = false;
                var pairs = new System.Collections.Generic.List<
                    System.Collections.Generic.KeyValuePair<AnimationClip, AnimationClip>>();
                _override.GetOverrides(pairs);
                for (int i = 0; i < pairs.Count; i++)
                {
                    var original = pairs[i].Key;
                    if (original == null || original.name.IndexOf("Idle",
                            System.StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    pairs[i] = new System.Collections.Generic.KeyValuePair<AnimationClip, AnimationClip>(
                        original, _seatedClip);
                    replaced = true;
                }
                if (!replaced)
                {
                    Debug.LogWarning("[ProxySeatedHook] El controller del proxy no tiene ningún clip " +
                        "con 'Idle' en el nombre: el vigilante se queda de pie.");
                    return;
                }
                _override.ApplyOverrides(pairs);
            }

            _animator.runtimeAnimatorController = _override;
        }

        private void AimHead()
        {
            var cam = Camera.main;
            if (cam == null)
                return;

            var head = _head != null ? _head : _neck;
            if (head == null)
                return;

            Vector3 to = cam.transform.position - head.position;
            // El yaw se mide contra el frente del CUERPO, que es el de la silla: es lo que define el
            // cono, y por eso no puede medirse contra la cabeza (que ya está girada del fotograma
            // anterior y realimentaría su propio giro).
            Vector3 flat = Vector3.ProjectOnPlane(to, transform.up);
            float yaw = flat.sqrMagnitude > 1e-6f
                ? Vector3.SignedAngle(transform.forward, flat, transform.up)
                : 0f;
            float pitch = -Mathf.Atan2(to.y, flat.magnitude) * Mathf.Rad2Deg;

            bool inCone = Mathf.Abs(yaw) <= _coneDeg;
            if (inCone)
            {
                _hadTarget = true;
            }
            else
            {
                // EL SALTO SECO (ADR-131 D6): al salir del cono se sortea otra pose UNA vez, y se
                // mantiene. Volver a sortearla cada fotograma sería un tic nervioso, y no sortear
                // ninguna dejaría la cabeza clavada donde te perdió, que se lee como un fallo.
                if (_hadTarget)
                {
                    _hadTarget = false;
                    _awayPose = AwayPoses[Random.Range(0, AwayPoses.Length)];
                }
                yaw = _awayPose.x;
                pitch = _awayPose.y;
            }

            yaw = Mathf.Clamp(yaw, -_coneDeg, _coneDeg);
            pitch = Mathf.Clamp(pitch, -_pitchClampDeg, _pitchClampDeg);

            float neckYaw = yaw * _neckShare;
            float neckPitch = pitch * _neckShare;
            ProxyRigUtil.ApplyBend(_neck, neckYaw, transform.up);
            ProxyRigUtil.ApplyBend(_neck, neckPitch, transform.right);
            ProxyRigUtil.ApplyBend(_head, yaw - neckYaw, transform.up);
            ProxyRigUtil.ApplyBend(_head, pitch - neckPitch, transform.right);
        }

        private void Breathe()
        {
            if (_chest == null || _breathDeg <= 0f)
                return;
            float w = Mathf.Sin(Time.time * _breathHz * Mathf.PI * 2f + _phase);
            ProxyRigUtil.ApplyBend(_chest, _breathDeg * w, transform.right);
        }
    }
}
