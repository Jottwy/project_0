using PolymindGames.PoolingSystem;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Explosion Impact-Effect")]
    public class FirearmExplosionImpactEffect : FirearmImpactEffectBehaviour, IFirearmImpactEffect
    {
        [SerializeField, PrefabObjectOnly]
        [Tooltip("Pooled explosion prefab.")]
        private ExplosionEffect _explosionPrefab;

        public override void TriggerHitEffect(in RaycastHit hit, Vector3 hitDirection, float speed, float travelledDistance)
        {
            var explosion = PoolManager.Instance.Get(_explosionPrefab, hit.point, Quaternion.identity);
            explosion.Detonate(Wieldable.Character);
        }

        public override void TriggerHitEffect(Collision collision, float travelledDistance)
        {
            var explosion = PoolManager.Instance.Get(_explosionPrefab, collision.GetContact(0).point, Quaternion.identity);
            explosion.Detonate(Wieldable.Character);
        }

        protected override void Awake()
        {
            base.Awake();
            if (!PoolManager.Instance.HasPool(_explosionPrefab))
                PoolManager.Instance.RegisterPool(_explosionPrefab, new SceneObjectPool<ExplosionEffect>(_explosionPrefab, gameObject.scene, PoolCategory.Projectiles, 2, 8, 120f));
        }
    }
}