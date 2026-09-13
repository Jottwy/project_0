using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Body
{
    /// <summary>
    /// Placa de daño de la escena de pruebas (ADR-149 R0): al pisarla hace un daño de un tipo y, si lleva punto, en una
    /// altura y lado fijos del jugador, así se sabe qué zona va a caer. Sin punto, la zona se sortea (como las caídas del
    /// vendor). Solo la monta <c>BR_InventoryTest</c>.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class BackroomsHurtPad : MonoBehaviour
    {
        [SerializeField, Range(0f, 100f)]
        private float _damage = 16f;

        [SerializeField]
        private DamageType _type = DamageType.Slash;

        [SerializeField]
        private bool _usePoint = true;

        // Espacio del jugador: pies en y = 0, +x a su derecha.
        [SerializeField]
        private Vector3 _localHitPoint = new(0.12f, 0.05f, 0.2f);

        [SerializeField, Range(0.2f, 10f)]
        private float _cooldown = 1.5f;

        private float _next;

        private void Reset() => GetComponent<Collider>().isTrigger = true;

        private void OnTriggerEnter(Collider other)
        {
            if (Time.time < _next) return;
            var player = other.GetComponentInParent<Player>();
            if (player == null || player.HealthManager == null) return;
            _next = Time.time + _cooldown;
            var point = _usePoint ? player.transform.TransformPoint(_localHitPoint) : Vector3.zero;
            player.HealthManager.ReceiveDamage(_damage, new DamageArgs(_type, null, point));
        }
    }
}
