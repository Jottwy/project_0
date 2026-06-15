using UnityEngine;
using System;

namespace PolymindGames
{
    [AddComponentMenu("Polymind Games/Damage/Character Damage Handler")]
    public class CharacterDamageHandler : CharacterBehaviour
    {
        [SerializeField, MinMaxSlider(0f, 1f)]
        [Tooltip("Range of normalized damage within which audio and animation are triggered. Damage below the minimum threshold will not trigger.")]
        private Vector2 _damageThreshold = new(0.05f, 0.35f);

        [SerializeField, Range(0f, 1f)]
        private float _minDamageMultiplier = 0.4f;
        
        [SerializeField]
        [Tooltip("Only play damage audio and animation when the character is alive.")]
        private bool _playOnlyWhenAlive;

        [SerializeField, Title("Audio")]
        [Tooltip("The general damage audio to be played when this entity receives damage.")]
        private AudioData _damageAudio = new(null);

        [SerializeField]
        [Tooltip("Specific damage audio clips for different damage types.")]
        [ReorderableList(ListStyle.Lined), LabelByChild(nameof(DamageAudio.Type))]
        private DamageAudio[] _damageTypeAudio;

        [Title("Animation")]
        [SerializeField, Tooltip("Enable to play hit animation when damaged.")]
        private bool _playHitAnimation;

        private static readonly int _getHit = Animator.StringToHash("GetHit");
        private static readonly int _getHitForce = Animator.StringToHash("GetHitForce");

        protected override void OnBehaviourEnable(ICharacter character)
        {
            var healthManager = character.HealthManager;
            healthManager.DamageReceived += HandleDamage;
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            var healthManager = character.HealthManager;
            healthManager.DamageReceived -= HandleDamage;
        }

        protected virtual void PlayDamageEffects(DamageType damageType, float multiplier)
        {
            PlayDamageAudio(damageType, multiplier);
            PlayDamageAnimation(damageType, multiplier);
        }
        
        protected void HandleDamage(float damage, in DamageArgs args)
        {
            var healthManager = Character.HealthManager;
            if (_playOnlyWhenAlive && healthManager.IsDead())
                return;
            
            float normalizedDamage = damage / healthManager.MaxHealth;
            if (normalizedDamage < _damageThreshold.x)
                return;

            float multiplier = Mathf.Max(_minDamageMultiplier, normalizedDamage.Normalize(_damageThreshold.x, _damageThreshold.y));
            PlayDamageEffects(args.DamageType, multiplier);
        }

        /// <summary>
        /// Plays the damage animation based on the damage type and multiplier.
        /// </summary>
        /// <param name="damageType">Type of damage received.</param>
        /// <param name="multiplier">Multiplier indicating the intensity of damage.</param>
        protected void PlayDamageAnimation(DamageType damageType, float multiplier)
        {
            if (!_playHitAnimation)
                return;
            
            var animator = Character.Animator;
            animator.SetFloat(_getHitForce, multiplier);
            animator.SetTrigger(_getHit);
        }

        /// <summary>
        /// Plays the appropriate damage audio based on the damage type and multiplier.
        /// </summary>
        /// <param name="damageType">Type of damage received.</param>
        /// <param name="multiplier">Multiplier indicating the intensity of damage.</param>
        protected void PlayDamageAudio(DamageType damageType, float multiplier)
        {
            var audioPlayer = Character.Audio;

            // Look for specific damage type audio
            foreach (var damageAudio in _damageTypeAudio)
            {
                if (damageAudio.Type == damageType)
                {
                    var config = damageAudio.Audio;
                    if (config.IsPlayable)
                        audioPlayer.PlayClip(config, BodyPoint.Head, multiplier);
                    
                    return;
                }
            }

            // If specific damage type audio not found, play general damage audio
            if (_damageAudio.IsPlayable)
                audioPlayer.PlayClip(_damageAudio, BodyPoint.Head, multiplier);
        }

        #region Internal Types
        [Serializable]
        private struct DamageAudio
        {
            public DamageType Type;
            public AudioData Audio;
        }
		#endregion
    }
}