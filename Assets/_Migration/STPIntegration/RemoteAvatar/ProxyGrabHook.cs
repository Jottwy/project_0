using BackroomsSurvival.Gameplay; // PhantomAttackHandler
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// The creature's half of the kill: while it is holding the local player, both arms REACH for
    /// them, the chest leans in and the head tracks them. Procedural, per frame, no clip.
    ///
    /// WHY PROCEDURAL AND NOT AN ANIMATION CLIP. Same reasoning ProxyMeleeHook already settled for
    /// the swing: the proxy's Animator is rebuilt from scratch by ProxyAnimatorControllerBuilder on
    /// every bake, so a hand-authored grab state would be destroyed by the next rebuild — and a
    /// canned clip cannot reach a target that is in a different place every time. A two-bone FK aim
    /// CONVERGES on wherever the victim actually is, which a clip fundamentally cannot.
    ///
    /// IT DRIVES THE REAL FORM, NOT THE DISGUISE. A kill only happens out of SPRINT, and SPRINT is
    /// revealed (ADR-038), so the body on screen is the real form and the vendor renderers are
    /// hidden. Bones are addressed through <see cref="HumanBodyBones"/> rather than by name: the
    /// real form is rigged and VALIDATED as Humanoid by PhantomRealFormBuilder, while the two rigs
    /// name their bones differently (`LeftArm` vs `UpperArm.L`) — the exact mismatch that already
    /// cost this project a silent no-op when ProxyMeleeHook looked for `Forearm.R`. Falls back to
    /// the vendor Animator if no real form is wired, so a V1 prefab still animates.
    ///
    /// LATEUPDATE, not Update: the Animator writes bone poses during its own update, so anything
    /// applied earlier is overwritten the same frame.
    ///
    /// PULLS ITS STATE. PhantomAttackHandler knows who is grabbing whom, but it lives in the
    /// BackroomsSurvival assembly and this file is in Assembly-CSharp — the reference only goes one
    /// way, so this reads a static rather than being called.
    ///
    /// Costs one reference comparison per frame for every proxy that is not grabbing anyone, which
    /// is all of them almost always. Removable: delete the file and the kill still happens, the
    /// creature just does not reach for you.
    /// </summary>
    public sealed class ProxyGrabHook : MonoBehaviour
    {
        [Header("Reach")]
        [Tooltip("Fraction of the hold over which the arms come up. Well under 1, so it is reaching " +
                 "BEFORE it has you rather than arriving late. Expressed as a fraction and not in " +
                 "seconds because the hold's length lives in PhantomAttackHandler, and two places " +
                 "owning the same duration is how they drift apart.")]
        [SerializeField, Range(0.05f, 1f)] private float _reachFraction = 0.32f;

        [Tooltip("How completely the arms are allowed to leave their animated pose (0..1). Below 1 " +
                 "the locomotion underneath still shows through, which keeps it from going stiff.")]
        [SerializeField, Range(0f, 1f)] private float _armWeight = 0.92f;

        [Tooltip("How far the chest leans toward the victim (degrees).")]
        [SerializeField, Range(0f, 40f)] private float _leanDegrees = 16f;

        [Tooltip("How completely the head turns onto the victim (0..1).")]
        [SerializeField, Range(0f, 1f)] private float _headWeight = 0.75f;

        [Header("Shove")]
        [Tooltip("Extra thrust the arms snap through in the final moment, as it throws you.")]
        [SerializeField, Range(0f, 1f)] private float _shoveAt = 0.86f;

        private Animator _animator;
        private bool _resolved;
        private Transform _lUpper, _lLower, _lHand;
        private Transform _rUpper, _rLower, _rHand;
        private Transform _chest, _head;

        private void OnEnable()
        {
            // Pool reuse: the real form is a different object once the proxy is recycled.
            _resolved = false;
        }

        private void LateUpdate()
        {
            // Not the one holding anybody — which is every proxy, almost always.
            if (!ReferenceEquals(PhantomAttackHandler.ActiveGrabber, transform))
                return;

            if (!_resolved)
                ResolveRig();
            if (_animator == null)
                return;

            float t = PhantomAttackHandler.GrabProgress01;
            var victim = PhantomAttackHandler.GrabVictimPoint;

            // Reach ramps in fast, then holds. SmoothStep so the arms accelerate and settle rather
            // than travelling at a constant rate, which is what makes procedural motion look robotic.
            float reach = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / _reachFraction));

            // The shove: at the very end the hands drive PAST the victim, which is the motion that
            // reads as throwing rather than releasing. The corpse impulse is timed to this.
            var aimAt = victim;
            if (t > _shoveAt)
            {
                float k = (t - _shoveAt) / Mathf.Max(1e-3f, 1f - _shoveAt);
                aimAt += (victim - transform.position).normalized * (1.6f * k);
            }

            float w = _armWeight * reach;
            AimLimb(_lUpper, _lLower, _lHand, aimAt, w);
            AimLimb(_rUpper, _rLower, _rHand, aimAt, w);

            LeanChest(aimAt, reach);
            TurnHead(victim, reach);
        }

        /// <summary>
        /// Two-bone FK aim: swing the upper bone so the limb points at the target, then the lower
        /// bone so the hand does. Not IK — there is no length constraint and the hand does not
        /// necessarily LAND on the target, which is correct here: the creature is reaching for you,
        /// and a solver that snapped its palm exactly onto the camera every frame would look pasted.
        /// </summary>
        private static void AimLimb(Transform upper, Transform lower, Transform hand, Vector3 target, float w)
        {
            if (upper == null || lower == null || w <= 0f)
                return;

            AimBone(upper, lower.position, target, w);
            if (hand != null)
                AimBone(lower, hand.position, target, w * 0.85f);
        }

        /// <summary>Rotate `bone` so that the direction bone→`tip` points at `target`, blended by w.</summary>
        private static void AimBone(Transform bone, Vector3 tip, Vector3 target, float w)
        {
            Vector3 have = tip - bone.position;
            Vector3 want = target - bone.position;
            if (have.sqrMagnitude < 1e-6f || want.sqrMagnitude < 1e-6f)
                return;

            var delta = Quaternion.FromToRotation(have.normalized, want.normalized);
            bone.rotation = Quaternion.Slerp(bone.rotation, delta * bone.rotation, w);
        }

        private void LeanChest(Vector3 target, float reach)
        {
            if (_chest == null || _leanDegrees <= 0f)
                return;

            Vector3 flat = target - _chest.position;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f)
                return;

            // Around the chest's own right axis, so the lean follows wherever the body is facing.
            var axis = Vector3.Cross(Vector3.up, flat.normalized);
            _chest.rotation = Quaternion.AngleAxis(_leanDegrees * reach, axis) * _chest.rotation;
        }

        private void TurnHead(Vector3 target, float reach)
        {
            if (_head == null || _headWeight <= 0f)
                return;

            Vector3 want = target - _head.position;
            if (want.sqrMagnitude < 1e-6f)
                return;

            // Blend the whole head onto the victim. Its own forward is the model's, so this uses the
            // same FromToRotation trick as the limbs rather than assuming which axis is "forward".
            AimBone(_head, _head.position + _head.forward, target, _headWeight * reach);
        }

        /// <summary>
        /// Prefer the REAL FORM's Animator (the body actually on screen during a kill) and fall back
        /// to the vendor one. Both are Humanoid, so `GetBoneTransform` works on either and neither
        /// depends on what the rig happens to call its bones.
        /// </summary>
        private void ResolveRig()
        {
            _resolved = true;
            _animator = null;

            var animators = GetComponentsInChildren<Animator>(true);
            Animator vendor = null;
            foreach (var a in animators)
            {
                if (a == null || !a.isHuman)
                    continue;
                if (a.transform == transform)
                    vendor = a;                       // the disguise, on the root
                else if (a.gameObject.activeInHierarchy)
                    _animator = a;                    // the real form, active ⇒ this is a reveal
            }
            // Explicit null test, not `??=`: Unity's overloaded equality reports a DESTROYED object
            // as null, and the null-coalescing operators bypass that overload entirely.
            if (_animator == null)
                _animator = vendor;
            if (_animator == null)
                return;

            _lUpper = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            _lLower = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            _lHand = _animator.GetBoneTransform(HumanBodyBones.LeftHand);
            _rUpper = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            _rLower = _animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            _rHand = _animator.GetBoneTransform(HumanBodyBones.RightHand);
            _chest = _animator.GetBoneTransform(HumanBodyBones.Chest)
                     ?? _animator.GetBoneTransform(HumanBodyBones.Spine);
            _head = _animator.GetBoneTransform(HumanBodyBones.Head);

            if (_lUpper == null || _rUpper == null)
            {
                Debug.LogWarning("[ProxyGrabHook] Humanoid avatar has no arm bones — the grab will " +
                                 "not animate. Check the real form's rig import (it must be Humanoid).");
            }
        }
    }
}
