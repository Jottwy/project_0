using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Audio Barrel-Effect")]
    public sealed class FirearmAudioBarrelEffect : FirearmBarrelEffectBehaviour
    {
        [SerializeField]
        private AudioData _fireAudio = new(null);

        [SerializeField]
        private AudioData _fireTailAudio = new(null);

        public override void TriggerFireEffect() => Wieldable.Audio.PlayClip(_fireAudio, BodyPoint.Torso);

        public override void TriggerFireStopEffect()
        {
            if (_fireTailAudio.IsPlayable)
                Wieldable.Audio.PlayClip(_fireTailAudio, BodyPoint.Torso);
        }
    }
}