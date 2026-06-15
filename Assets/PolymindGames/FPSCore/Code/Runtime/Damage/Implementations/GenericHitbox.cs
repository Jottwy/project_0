using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Handles incoming damage events and relays them to health management systems or other event handlers.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    [AddComponentMenu("Polymind Games/Damage/Generic Hitbox")]
    public sealed class GenericHitbox : MonoBehaviour, IDamageHandler
    {
        [SerializeField, Range(0f, 100f)]
        [Tooltip("Damage multiplier applied to incoming damage.")]
        private float _damageMultiplier = 1f;
    
        [SerializeField]
        [Tooltip("Indicates whether this hitbox applies critical damage.")]
        private bool _isCritical;
    
        [SerializeField, SpaceArea]
        [Tooltip("Event triggered when this hitbox takes damage. Note: Health managers will also be notified if present.")]
        private UnityEvent<float> _onDamage;
    
        private IHealthManager _health;
    
        /// <summary>
        /// The character associated with this hitbox (if any). Always returns null for this class.
        /// </summary>
        public ICharacter Character => null;
    
        /// <summary>
        /// Event triggered when the hitbox takes damage.
        /// </summary>
        public event UnityAction<float> OnDamage
        {
            add => _onDamage.AddListener(value);
            remove => _onDamage.RemoveListener(value);
        }
    
        /// <inheritdoc/>
        public DamageResult HandleDamage(float damage, in DamageArgs args)
        {
            if (_health != null)
            {
                if (_health.IsDead())
                    return DamageResult.Ignored;
    
                // Apply damage multiplier
                damage *= _damageMultiplier;
                damage = _health.ReceiveDamage(damage, in args);
    
                DamageResult result = _health.GetDamageResultBasedOnStatus(_isCritical);
                _onDamage.Invoke(-damage);
    
                // Register the damage event in the damage tracker
                DamageTracker.RegisterDamage(this, result, damage, in args);
                return result;
            }
            else
            {
                // Apply damage multiplier and invoke the damage event
                damage *= _damageMultiplier;
                _onDamage.Invoke(-damage);
    
                DamageTracker.RegisterDamage(this, DamageResult.Normal, damage, in args);
                return DamageResult.Normal;
            }
        }

        private void OnEnable() => GetComponent<Collider>().enabled = true;
        private void OnDisable() => GetComponent<Collider>().enabled = false;
        private void Start() => _health = transform.GetComponentInParent<IHealthManager>();
    }
}