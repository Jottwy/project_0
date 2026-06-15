using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Hold Trigger")]
    public class FirearmHoldTrigger : FirearmTriggerBehaviour
    {
        [SerializeField, Range(0f, 3f)]
        private float _holdDuration = 0.2f;

        [SerializeField, Title("Effects")]
        private AudioData _holdAudio;

        [SerializeField]
        private WieldableMovingPart _movingParts;

        private float _holdEndTime;

        public override void HoldTrigger()
        {
            base.HoldTrigger();

            if (_holdEndTime < 0f)
                return;

            if (Time.time >= _holdEndTime)
            {
                RaiseShootEvent();
                _holdEndTime = -1f;
                _movingParts.StopMovement();
            }
        }

        public override void ReleaseTrigger()
        {
            base.ReleaseTrigger();
            _movingParts.StopMovement();
        }

        protected override void TapTrigger()
        {
            _movingParts.BeginMovement();
            _holdEndTime = Time.time + _holdDuration;
            Wieldable.Audio.PlayClip(_holdAudio, BodyPoint.Hands);
        }

        private void LateUpdate() => _movingParts.UpdateMovement();
    }
}