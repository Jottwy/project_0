using PolymindGames.ProceduralMotion;
using UnityEngine;

namespace PolymindGames
{
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/player/modules-and-behaviours/audio#audio-player-module")]
    public class PlayerDamageHandler : CharacterDamageHandler
    {
        [SerializeField, Range(0f, 100f), Title("Heartbeat")]
        private float _heartbeatHealthThreshold = 10f;

        [SerializeField, BeginIndent, EndIndent]
        [ShowIf(nameof(_heartbeatHealthThreshold), 0f, Comparison = UnityComparisonMethod.Greater)]
        private AudioData _heartbeatAudio = new(null);

        [SerializeField, Title("Shakes")]
        [Tooltip("Shake data defining the shake characteristics when the character takes damage.")]
        private ShakeData _headDamageShake;

        [SerializeField]
        [Tooltip("Shake data defining the shake characteristics when the character takes damage.")]
        private ShakeData _handDamageShake;

        private IShakeHandler _headShakeHandler;
        private IShakeHandler _handShakeHandler;
        private AudioSource _heartbeatSource;
        private bool _isHeartbeatAudioPlaying;

        protected override void OnBehaviourEnable(ICharacter character)
        {
            base.OnBehaviourEnable(character);
            character.HealthManager.HealthRestored += HandleHealthRestore;

            if (character is IFPSCharacter fpsCharacter)
            {
                _headShakeHandler = fpsCharacter.HeadComponents.Shake;
                _handShakeHandler = fpsCharacter.HandsComponents.Shake;
            }
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            base.OnBehaviourDisable(character);
            character.HealthManager.HealthRestored -= HandleHealthRestore;
            _headShakeHandler = null;
            _handShakeHandler = null;
        }

        protected override void PlayDamageEffects(DamageType damageType, float multiplier)
        {
            base.PlayDamageEffects(damageType, multiplier);

            // Start heartbeat loop sound.
            if (_heartbeatAudio.IsPlayable
                && Character.HealthManager.Health < _heartbeatHealthThreshold
                && !_isHeartbeatAudioPlaying)
            {
                _heartbeatSource = Character.Audio.StartLoop(_heartbeatAudio, BodyPoint.Torso);
                _isHeartbeatAudioPlaying = true;
            }

            if (_headDamageShake.IsPlayable)
                _headShakeHandler?.AddShake(in _headDamageShake, multiplier);

            if (_handDamageShake.IsPlayable)
                _handShakeHandler?.AddShake(in _handDamageShake, multiplier);
        }

        protected virtual void HandleHealthRestore(float value)
        {
            // Stop heartbeat loop sound.
            if (_isHeartbeatAudioPlaying && Character.HealthManager.Health > _heartbeatHealthThreshold)
            {
                Character.Audio.StopLoop(_heartbeatSource);
                _isHeartbeatAudioPlaying = false;
                _heartbeatSource = null;
            }
        }
    }
}