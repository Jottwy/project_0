using PolymindGames.ProceduralMotion;
using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu("Polymind Games/Wieldables/Behaviours/Wieldable Shake Animator")]
    public sealed class WieldableShakeAnimator : AnimatorEffectTranslator<WieldableShakeAnimator.AnimationData>
    {
        private IWieldableMotion _wieldableMotion;
        private IShakeHandler _headShakeHandler;
        private IShakeHandler _handsShakeHandler;

        protected override void PlayAnimation(AnimationData anim)
        {
            if (anim.HeadShake.IsPlayable)
                _headShakeHandler.AddShake(anim.HeadShake);
            
            if (anim.HandsShake.IsPlayable)
                _handsShakeHandler.AddShake(anim.HandsShake);
        }

        private void OnEnable()
        {
            _wieldableMotion ??= GetComponent<IWieldableMotion>();
            _headShakeHandler ??= _wieldableMotion.HeadComponents.Shake;
            _handsShakeHandler ??= _wieldableMotion.HandsComponents.Shake;
        }

        #region Internal Types
        [Serializable]
        public sealed class AnimationData : AnimatorTranslatorData
        {
            [SpaceArea]
            public ShakeData HeadShake;
            public ShakeData HandsShake;
        }
        #endregion
    }
}