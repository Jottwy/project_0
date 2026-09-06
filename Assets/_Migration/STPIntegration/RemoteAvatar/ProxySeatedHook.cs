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
    ///    mueve, así que el árbol de locomoción se queda en su centro para siempre. Y la ALTURA la
    ///    resuelve <see cref="PlantFeet"/> midiendo, no una constante: un clip Humanoid trae su
    ///    propia posición de cuerpo y dónde deja los pies depende del rig, así que el cuerpo se
    ///    desplaza cada fotograma lo justo para que la planta toque el suelo del proxy. Va en el
    ///    cliente porque es geometría del modelo, y una constante de modelo en el servidor deja de
    ///    significar lo que dice su comentario al primer re-horneado.
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
        private Transform _head, _neck, _chest;
        private RuntimeAnimatorController _originalController;
        private AnimatorOverrideController _override;
        private bool _seated;
        private bool _hadTarget;
        private Vector2 _awayPose;
        private float _phase;

        private void Awake()
        {
            // **EL ANIMATOR ES EL QUE MUEVE LA MALLA QUE SE VE, y hay que preguntárselo a la
            // MALLA, no al animator.**
            //
            // Un proxy lleva varios Animator humanoides: el del vendor en el raíz y, en el prefab del
            // faceling, el de su propio cuerpo colgando de un hijo. Tres heurísticas fallaron, y las
            // tres se vieron en captura como «un tío DE PIE dentro de su silla»: coger el primero
            // posaba al invisible; coger el primero que cuelga de un hijo fallaba al revés en el
            // avatar humano; y preguntar «¿tiene alguna malla encendida debajo?» siempre contesta que
            // sí en el raíz, porque debajo del raíz está TODO.
            //
            // La respuesta exacta la da el propio `SkinnedMeshRenderer`: sus huesos pertenecen a UN
            // esqueleto, y el Animator de ese esqueleto es el primero que se encuentra subiendo desde
            // el hueso. Ése es el que hay que posar.
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!smr.enabled || !smr.gameObject.activeInHierarchy)
                    continue;
                var bone = smr.rootBone != null ? smr.rootBone
                    : (smr.bones != null && smr.bones.Length > 0 ? smr.bones[0] : null);
                if (bone == null)
                    continue;
                var owner = bone.GetComponentInParent<Animator>();
                if (owner == null || !owner.isHuman)
                    continue;
                _animator = owner;
                break;
            }
            // Sin malla encendida todavía (un cuerpo que enciende sus renderers más tarde): se coge
            // el primer humanoide y `Awake` no vuelve a correr, pero el caso no se da en los prefabs
            // que existen hoy y un vigilante de pie es una degradación visible, no un error mudo.
            if (_animator == null)
            {
                foreach (var a in GetComponentsInChildren<Animator>(true))
                {
                    if (!a.isHuman)
                        continue;
                    _animator = a;
                    break;
                }
            }
            if (_animator == null)
                return;

            _head = _animator.GetBoneTransform(HumanBodyBones.Head);
            _neck = _animator.GetBoneTransform(HumanBodyBones.Neck);
            _chest = _animator.GetBoneTransform(HumanBodyBones.Chest)
                ?? _animator.GetBoneTransform(HumanBodyBones.Spine);

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
        }

        private void OnDisable() => SetSeated(false);

        private void LateUpdate()
        {
            if (_animator == null)
                return;

            SetSeated(ResolveSeated());
            if (!_seated)
                return;

            PlantFeet();
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
                // La altura la resuelve `PlantFeet` con el cuerpo ya posado.
                ApplyOverride();
            }
            else if (_originalController != null)
            {
                _animator.runtimeAnimatorController = _originalController;
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

        /// <summary>
        /// **LA ALTURA DEL ASIENTO SE MIDE, NO SE ESCRIBE — y costó cuatro capturas.**
        ///
        /// Un clip Humanoid lleva su propia posición de cuerpo, y dónde deja los pies depende del
        /// RIG: una constante calibrada con un esqueleto deja al otro medio metro en el aire o medio
        /// metro bajo tierra (visto: caderas a 19 cm BAJO el suelo con la pose puesta). En vez de un
        /// número por especie se corrige el error real: estamos en LateUpdate, o sea con el cuerpo ya
        /// posado por el Animator, así que se mira dónde ha quedado el pie más bajo y se sube o baja
        /// el esqueleto lo justo para que la PLANTA toque el suelo del proxy.
        ///
        /// **Se mueve la CADERA y no un hijo del raíz.** El raíz lo reescribe `RemotePlayerManager`
        /// con la pose de red cada fotograma, y el cuerpo visible no siempre cuelga de un hijo: en el
        /// avatar humano el Animator está en el propio raíz. La cadera es la raíz del esqueleto en
        /// los dos casos, arrastra a todo el cuerpo con ella, y el Animator la vuelve a escribir en
        /// el fotograma siguiente — así que esto no acumula.
        ///
        /// `leftFeetBottomHeight` es la distancia del tobillo a la planta que Unity deriva del propio
        /// Avatar: el suelo del pie sale del rig y no de otra constante a mano.
        /// </summary>
        private void PlantFeet()
        {
            var hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
            var left = _animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var right = _animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (hips == null || (left == null && right == null))
                return;

            float lowest = Mathf.Min(
                left != null ? left.position.y : float.MaxValue,
                right != null ? right.position.y : float.MaxValue);
            float error = lowest - (transform.position.y + _animator.leftFeetBottomHeight);
            if (Mathf.Abs(error) < 0.001f)
                return;

            var p = hips.position;
            p.y -= error;
            hips.position = p;
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
