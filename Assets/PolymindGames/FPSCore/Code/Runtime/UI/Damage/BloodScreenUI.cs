using UnityEngine;

namespace PolymindGames.UserInterface
{
    public sealed class BloodScreenUI : CharacterUIBehaviour
    {
        [SerializeField, Range(0f, 100f)]
        [Tooltip("How much damage does the player have to take for the blood screen effect to show. ")]
        private float _damageThreshold = 5f;
        
        [SerializeField, Range(0f, 100f)]
        private float _lowHealthThreshold = 15f;
        
        [SerializeField, Range(0f, 1f)]
        private float _lowHealthAlpha = 0.75f;
        
        [SerializeField, SubGroup, SpaceArea]
        [Tooltip("Image fading settings for the blood screen.")]
        private ImageFaderUI _fadeSettings;

        private Coroutine _indicatorRoutine;
        private IHealthManager _health;
        private Vector3 _lastHitPoint;

        protected override void OnCharacterAttached(ICharacter character)
        {
            _health = character.HealthManager;
            _health.DamageReceived += OnTakeDamage;
            _health.HealthRestored += OnHealthRestored;
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            _health.DamageReceived -= OnTakeDamage;
            _health.HealthRestored -= OnHealthRestored;
        }
        
        private void OnHealthRestored(float value)
        {
            float currentHealth = _health.Health;
            if (currentHealth > _lowHealthThreshold)// && currentHealth - value < _lowHealthAlpha)
                _fadeSettings.FadeTo(this, 0f);
        }

        private void OnTakeDamage(float damage, in DamageArgs args)
        {
            if (damage < _damageThreshold)
                return;

            if (_health.Health < _lowHealthThreshold && _health.IsAlive())
            {
                _fadeSettings.StartFadeCycle(this, _lowHealthAlpha, int.MaxValue);
            }
            else
            {
                float targetAlpha = damage / _health.MaxHealth;
                _fadeSettings.StartFadeCycle(this, _fadeSettings.CurrentAlpha + targetAlpha, 2);
            }
        }
    }
}
