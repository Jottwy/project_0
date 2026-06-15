using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Will register damage events from outside and pass them to the parent character.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    [AddComponentMenu("Polymind Games/Damage/Character Hitbox")]
    public sealed class CharacterHitbox : CharacterBehaviour, IDamageHandler
    {
        [SerializeField]
        private bool _isCritical;

        [SerializeField, Range(0f, 100f)]
        private float _damageMultiplier = 1f;

        public DamageResult HandleDamage(float damage, in DamageArgs args)
        {
            var health = Character.HealthManager;

            if (health.IsDead())
                return DamageResult.Ignored;
            
            damage = health.ReceiveDamage(damage * _damageMultiplier, in args);
            
            DamageResult result = health.GetDamageResultBasedOnStatus(_isCritical);
            DamageTracker.RegisterDamage(this, result, damage, in args);
            
            return result;
        }
    }
}