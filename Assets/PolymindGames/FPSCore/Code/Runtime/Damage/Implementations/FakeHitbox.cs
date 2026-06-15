using UnityEngine;

namespace PolymindGames
{
    [RequireComponent(typeof(Collider))]
    [AddComponentMenu("Polymind Games/Damage/Fake Hitbox")]
    public sealed class FakeHitbox : MonoBehaviour, IDamageHandler
    {
        [SerializeField]
        private bool _isCritical = false;

        [SerializeField]
        private bool _raiseEvent = true;
        
        public ICharacter Character => null;

        public DamageResult HandleDamage(float damage, in DamageArgs args)
        {
            var result = _isCritical ? DamageResult.Critical : DamageResult.Normal;
            
            if (_raiseEvent)
                DamageTracker.RegisterDamage(this, result, damage, in args);
            
            return result;
        }

        private void OnEnable() => GetComponent<Collider>().enabled = true;
        private void OnDisable() => GetComponent<Collider>().enabled = false;
    }
}