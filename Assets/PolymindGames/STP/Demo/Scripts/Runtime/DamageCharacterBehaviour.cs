using UnityEngine;

namespace PolymindGames.Demo
{
    public sealed class DamageCharacterBehaviour : MonoBehaviour
    {
        [SerializeField, MinMaxSlider(0f, 100f)]
        private Vector2 _damage;

        [SerializeField, Range(0f, 100f)]
        private float _hitImpulse = 5f;

        public void DamageCharacter(ICharacter character)
        {
            Vector3 pos = transform.position;
            DamageArgs args = new(DamageType.Slash, null, pos, (pos - character.transform.position).normalized * _hitImpulse);
            character.HealthManager.ReceiveDamage(_damage.GetRandomFromRange(), args);
        }
    }
}