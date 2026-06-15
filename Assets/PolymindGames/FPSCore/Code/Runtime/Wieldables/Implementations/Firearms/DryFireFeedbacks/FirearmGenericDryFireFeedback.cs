using PolymindGames.ProceduralMotion;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Generic Dry-Fire-Feedback")]
    public class FirearmGenericDryFireFeedback : FirearmDryFireFeedbackBehaviour
    {
        [SerializeField]
        private bool _dryFireAnimation;

        [SerializeField]
        private ShakeData _headDryFireShake;
        
        [SerializeField]
        private ShakeData _handsDryFireShake;

        [SerializeField]
        private AudioData _dryFireAudio = new(null);

        public override void TriggerDryFireFeedback()
        {
            Wieldable.Audio.PlayClip(_dryFireAudio, BodyPoint.Hands);
            var motion = Wieldable.Motion;
            
            if (_headDryFireShake.IsPlayable)
                motion.HeadComponents.Shake.AddShake(_headDryFireShake);
            
            if (_handsDryFireShake.IsPlayable)
                motion.HandsComponents.Shake.AddShake(_handsDryFireShake);

            if (_dryFireAnimation)
            {
                var animator = Wieldable.Animator;
                animator.SetBool(AnimationConstants.IsEmpty, true);
                animator.SetTrigger(AnimationConstants.Shoot);
            }
        }
    }
}