using UnityEngine.UI;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    public sealed class PlayerHealthUI : CharacterUIBehaviour
    {
        [SerializeField]
        [Tooltip("The health bar image, the fill amount will be modified based on the current health value.")]
        private Image _healthBar;

        protected override void OnCharacterAttached(ICharacter character)
        {
            var health = character.HealthManager;
            health.DamageReceived += OnDamage;
            health.HealthRestored += OnRestore;
            UpdateHealthBar(health.Health, health.MaxHealth);
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            var health = character.HealthManager;
            health.DamageReceived -= OnDamage;
            health.HealthRestored -= OnRestore;
        }

        private void OnDamage(float damage, in DamageArgs args)
        {
            var health = Character.HealthManager;
            UpdateHealthBar(health.Health, health.MaxHealth);
        }

        private void OnRestore(float value)
        {
            var health = Character.HealthManager;
            UpdateHealthBar(health.Health, health.MaxHealth);
        }

        private void UpdateHealthBar(float health, float maxHealth)
        {
            _healthBar.fillAmount = health / maxHealth;
        }
    }
}