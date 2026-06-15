using PolymindGames.PostProcessing;
using PolymindGames.InputSystem;
using UnityEngine.Audio;
using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Deals with the Player death and respawn behaviour.
    /// </summary>
    public class PlayerDeathHandler : CharacterBehaviour
    {
        [SerializeField, NotNull]
        private InputContext _context;
        
        [SerializeField, NotNull, Title("Effects")]
        private VolumeAnimationProfile _deathEffect;

        [SerializeField, NotNull]
        private AudioMixerSnapshot _deathSnapshot;

        [NewLabel("Fade Duration")]
        [SerializeField, IndentArea, Range(0f, 10f)]
        private float _audioFadeDuration = 3f;

        [SerializeField]
        private AudioData _deathAudio = new(null);

        protected override void OnBehaviourEnable(ICharacter character)
        {
            var health = character.HealthManager;
            health.Respawn += OnPlayerRespawn;
            health.Death += OnPlayerDeath;
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            var health = character.HealthManager;
            health.Respawn -= OnPlayerRespawn;
            health.Death -= OnPlayerDeath;
            
            if (health.IsDead())
                InputManager.Instance.PopContext(_context);
        }
        
        protected virtual void OnPlayerDeath(in DamageArgs args)
        {
            InputManager.Instance.PushContext(_context);
            _deathSnapshot.TransitionTo(_audioFadeDuration);
            PostProcessingManager.Instance.CancelAllAnimations();
            PostProcessingManager.Instance.PlayAnimation(this, _deathEffect);
            Character.Audio.PlayClip(_deathAudio, BodyPoint.Torso);
        }

        protected virtual void OnPlayerRespawn()
        {
            InputManager.Instance.PopContext(_context);
            AudioManager.Instance.DefaultSnapshot.TransitionTo(1f);
            PostProcessingManager.Instance.CancelAnimation(this, _deathEffect);
        }
    }
}