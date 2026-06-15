using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(IHealthManager))]
    [AddComponentMenu("Polymind Games/Damage/Health Manager Events")]
    public sealed class HealthManagerEvents : MonoBehaviour
    {
        [SerializeField]
        private UnityEvent<float> _onHealthRestored;

        [SerializeField]
        private UnityEvent<float> _onDamage;

        [SerializeField]
        private UnityEvent _onDeath;

        [SerializeField]
        private UnityEvent _onRespawn;

        private void OnEnable()
        {
            var health = GetComponent<IHealthManager>();
            health.DamageReceived += OnDamage;
            health.HealthRestored += _onHealthRestored.Invoke;
            health.Death += OnDeath;
            health.Respawn += _onRespawn.Invoke;
        }

        private void OnDisable()
        {
            var health = GetComponent<IHealthManager>();
            health.DamageReceived -= OnDamage;
            health.HealthRestored -= _onHealthRestored.Invoke;
            health.Death -= OnDeath;
            health.Respawn -= _onRespawn.Invoke;
        }

        private void OnDeath(in DamageArgs args) => _onDeath?.Invoke();
        private void OnDamage(float damage, in DamageArgs args) => _onDamage?.Invoke(damage);

        #region Editor
#if UNITY_EDITOR
        private void Reset()
        {
            if (!gameObject.HasComponent<IHealthManager>())
                gameObject.AddComponent<HealthManager>();
        }
#endif
        #endregion
    }
}