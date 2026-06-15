using UnityEngine;

namespace PolymindGames
{
    [RequireComponent(typeof(Rigidbody))]
    public sealed class HitboxEnableRigidbodyEvent : MonoBehaviour, IDamageHandler
    {
        public ICharacter Character => null;

        public DamageResult HandleDamage(float damage, in DamageArgs args)
        {
            GetComponent<Rigidbody>().isKinematic = false;
            return DamageResult.Normal;
        }
    }
}