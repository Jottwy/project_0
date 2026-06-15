using PolymindGames.ProceduralMotion;
using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu("Polymind Games/Wieldables/Behaviours/Wieldable Camera Forces Animator")]
    public sealed class WieldableCameraForcesAnimator : AnimatorEffectTranslator<WieldableCameraForcesAnimator.AnimationData>
    {
        private AdditiveForceMotion _forceMotion;

        protected override void PlayAnimation(AnimationData anim)
        {
            var forces = anim.Forces;
            for (int i = 0; i < forces.Length; i++)
            {
                if (forces[i].Delay < 0.01f)
                    _forceMotion.AddRotationForce(forces[i].SpringForce);
                else
                    _forceMotion.AddDelayedRotationForce(forces[i]);
            }
        }

        private void OnEnable()
        {
            if (_forceMotion == null)
            {
                var wieldable = GetComponentInParent<IWieldable>();
                _forceMotion = wieldable.Motion.HeadComponents.Mixer?.GetMotion<AdditiveForceMotion>();
            }
        }
        
        #region Internal Types
        [Serializable]
        public sealed class AnimationData : AnimatorTranslatorData
        {
            [SpaceArea] 
            [ReorderableList(ListStyle.Lined, elementLabel: "Force")]
            public DelayedSpringForce3D[] Forces;
        }
        #endregion
    }
}