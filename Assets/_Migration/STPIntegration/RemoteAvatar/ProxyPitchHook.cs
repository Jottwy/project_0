using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-021: tilts this remote proxy's head/neck (and, at extreme angles, the upper spine) to
    /// match the peer's networked camera pitch (<c>view.pitch</c>, degrees, quantized to 1°). The
    /// proxy rig is GENERIC (not Humanoid), so there is no <c>Animator.GetBoneTransform</c>; bones are
    /// resolved BY NAME (Head/Neck/UpperSpine/MiddleSpine of the MaleSurvivor skeleton) and cached.
    ///
    /// The bend is applied in <c>LateUpdate</c> (AFTER the Animator writes the locomotion/jump/pickup
    /// pose) as an ADDITIVE world-space rotation around the avatar's right axis (<c>transform.right</c>,
    /// already yaw-oriented by RemotePlayerManager). Going through world space — instead of a bone's
    /// arbitrary local axis — makes "nodding" correct regardless of how the Generic rig was authored.
    /// Rotations are applied root→leaf so the bend accumulates naturally: the head ends at the full
    /// pitch, neck and spine ease the curve. Head carries 60% of the nod, neck 40%.
    ///
    /// The pitch is read from the RemotePlayerManager view whose root is this GameObject — same lookup
    /// as ProxyCrouchHook, no change to RemotePlayerManager. Attach to the avatar root (same GameObject
    /// as the Animator). Removable: delete the file and the proxy simply looks forward; locomotion,
    /// jump, pickup and crouch are unaffected.
    ///
    /// PASADA 1a (2026-09-14): para un JUGADOR el cabeceo ya no se suma, se APUNTA. Sumado sobre el clip
    /// de agachado (cabeza a 70° hacia el suelo, medido) el peer agachado miraba al suelo con cabeceo 0.
    /// Ahora se mide la cabeza tras el Animator y se corrige la diferencia, repartida entre pecho,
    /// cuello y cabeza (<see cref="ProxyHeadPitchSolver"/>). Los huesos salen del Animator humanoide que
    /// pinta la malla visible; el rig ES Humanoid (lo de "GENERIC" de arriba está desfasado). Criaturas,
    /// sentados, forma real y cadáveres siguen por la ruta sumada de abajo, sin cambios.
    /// </summary>
    public sealed class ProxyPitchHook : MonoBehaviour
    {
        [Header("Tuning")]
        [Tooltip("Lerp speed of the applied pitch (higher = snappier).")]
        [SerializeField, Min(0f)] private float _lerpSpeed = 10f;

        [Tooltip("Above this absolute pitch (degrees) the upper spine starts to lean.")]
        [SerializeField, Min(0f)] private float _spineLeanThreshold = 45f;

        [Tooltip("Maximum spine lean (degrees) reached at ±90° pitch (ADR-021 cap).")]
        [SerializeField, Min(0f)] private float _maxSpineLean = 30f;

        [Tooltip("Flip the nod direction. The right-axis sign depends on the Generic rig; calibrate in play-test.")]
        [SerializeField] private bool _invertPitch;

        // Head carries 60% of the nod, neck 40% (added rotations; root→leaf accumulation puts the
        // head tip at the full pitch).
        private const float HeadShare = 0.6f;
        private const float NeckShare = 0.4f;

        private RemotePlayerManager _manager;
        private Transform _head;
        private Transform _neck;
        private Transform _upperSpine;
        private Transform _middleSpine;
        private bool _hasRig;
        private float _current;

        // Cabeceo ABSOLUTO (pasada 1a, 2026-09-14): huesos humanoides del esqueleto que pinta la malla
        // visible y el eje de mirada de la cabeza calibrado en la pose por defecto del prefab.
        private Transform _humanHead;
        private Transform _humanNeck;
        private Transform _humanChest;
        private Vector3 _headLocalLook;
        private bool _hasHuman;

        private void Awake()
        {
            _head = FindBone("Head");
            _neck = FindBone("Neck");
            _upperSpine = FindBone("UpperSpine");
            _middleSpine = FindBone("MiddleSpine");
            _hasRig = _head != null; // no head bone → nothing to drive
            ResolveHumanoid();
        }

        /// <summary>
        /// El Animator que hay que medir es el del esqueleto al que está pegada la malla que se VE, no el
        /// primero que aparezca: un proxy puede llevar dos humanoides vivos (ProxySeatedHook, ADR-131).
        ///
        /// La calibración va en Awake porque aquí el Animator todavía no ha posado nada: los huesos están
        /// en la pose por defecto del prefab, de pie y mirando al frente del raíz. Medido con el arnés: de
        /// pie con cabeceo 0 la cabeza sale a +6°, o sea que esa pose es una referencia sana.
        /// </summary>
        private void ResolveHumanoid()
        {
            Animator animator = null;
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
                animator = owner;
                break;
            }
            if (animator == null)
                return;

            _humanHead = animator.GetBoneTransform(HumanBodyBones.Head);
            if (_humanHead == null)
                return;
            _humanNeck = animator.GetBoneTransform(HumanBodyBones.Neck);
            _humanChest = animator.GetBoneTransform(HumanBodyBones.UpperChest)
                ?? animator.GetBoneTransform(HumanBodyBones.Chest);
            _headLocalLook = ProxyHeadPitchSolver.LocalLookAxis(_humanHead.rotation, transform.forward);
            _hasHuman = true;
        }

        // Re-arm for pool reuse: clear the applied pitch so a recycled proxy never starts pre-tilted.
        private void OnEnable() => _current = 0f;

        private void LateUpdate()
        {
            if (!_hasRig && !_hasHuman)
                return;

            float target = ResolvePitch(out bool absolute);
            float t = 1f - Mathf.Exp(-Mathf.Max(0f, _lerpSpeed) * Time.deltaTime);
            _current = Mathf.Lerp(_current, target, t);

            float p = _invertPitch ? -_current : _current;

            if (absolute && _hasHuman)
            {
                ProxyHeadPitchSolver.AimHead(transform, _humanChest, _humanNeck, _humanHead, _headLocalLook,
                    ProxyHeadPitchSolver.HeadTarget(p));
                return;
            }

            if (!_hasRig)
                return;
            Vector3 axis = transform.right; // yaw-oriented right of the avatar root

            // Root→leaf so bends accumulate. Spine lean (only past the threshold) first, then the
            // neck/head nod on top.
            float lean = SpineLean(p);
            ApplyBend(_middleSpine, lean * 0.5f, axis);
            ApplyBend(_upperSpine, lean * 0.5f, axis);
            ApplyBend(_neck, p * NeckShare, axis);
            ApplyBend(_head, p * HeadShare, axis);
        }

        /// <summary>
        /// This proxy's networked pitch (degrees), via the RemotePlayerManager view whose root is us.
        ///
        /// <paramref name="absolute"/> sólo para un JUGADOR de pie o agachado. Criaturas, el vigilante
        /// sentado (su cabeza la apunta ProxySeatedHook), la forma real del robapieles y un cadáver se
        /// quedan con el giro sumado de siempre: con cabeceo 0 no tocan nada, que es lo que hacían, y un
        /// cabeceo absoluto les enderezaría la cabeza que su propio clip o hook decide.
        /// </summary>
        private float ResolvePitch(out bool absolute)
        {
            absolute = false;
            if (!ProxyViewLookup.TryResolve(transform, ref _manager, out var view))
                return 0f;

            absolute = view.species == 0 && !view.revealed && !view.dead
                && !RemoteButtons.Has(view.buttons, RemoteButtons.Seated);
            // Leyendo, la cabeza baja al libro: el cabeceo de la cámara de quien lee no dice nada, el libro le llena la
            // pantalla (bit BookOpen, 2026-09-14).
            if (absolute && RemoteButtons.Has(view.buttons, RemoteButtons.BookOpen))
                return ProxyBookHold.ReadingPitchDegrees;
            return view.pitch;
        }

        // Spine lean: zero below the threshold, ramping to ±_maxSpineLean at ±90°.
        private float SpineLean(float pitch)
        {
            float abs = Mathf.Abs(pitch);
            if (abs <= _spineLeanThreshold)
                return 0f;
            float span = Mathf.Max(1f, 90f - _spineLeanThreshold);
            float frac = Mathf.Clamp01((abs - _spineLeanThreshold) / span);
            return Mathf.Sign(pitch) * frac * _maxSpineLean;
        }

        private static void ApplyBend(Transform bone, float degrees, Vector3 axis) =>
            ProxyRigUtil.ApplyBend(bone, degrees, axis);

        private Transform FindBone(string boneName) => ProxyRigUtil.FindBone(transform, boneName);
    }
}
