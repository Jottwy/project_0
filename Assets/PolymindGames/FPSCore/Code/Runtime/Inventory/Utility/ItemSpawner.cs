using System.Collections;
using UnityEngine;

namespace PolymindGames.InventorySystem
{
    [RequireComponent(typeof(BoxCollider))]
    public sealed class ItemSpawner : MonoBehaviour
    {
        [Title("Spawn Settings")]
        [SerializeField, MinMaxSlider(0, 32)]
        private Vector2Int _itemSpawnCount = new(1, 2);

        [SerializeField, Range(0f, 10f)]
        private float _spawnDelay = 0.5f;

        [SerializeField, Range(0f, 10f)]
        private float _consecutiveSpawnDelay = 0f;

        [SerializeField, Range(0f, 100f)]
        private float _positionForce = 2f;

        [SerializeField, Range(0f, 100f)]
        private float _angularForce = 25f;

        [SerializeField, Range(0f, 1f)]
        private float _randomRotation;

        [SerializeField, Range(0f, 100f)]
        private float _itemDestroyDelay = 30f;

        [SerializeField, Title("Loot Settings")]
        [ReorderableList, IgnoreParent]
        private LootTable _lootTable;

        [SerializeField, Range(0.1f, 10f)]
        [IndentArea, HideIf(nameof(_lootTable), false)]
        private float _rarityWeight = 1f;

        [SerializeField, Title("Spawn Effects")]
        private ParticleSystem _spawnParticles;

        [SerializeField]
        private AudioData _spawnAudio = new(null);
        
        private BoxCollider _collider;
        
        public void SpawnItems()
        {
            if (_spawnDelay > 0.01f || (_itemSpawnCount.y > 1 && _consecutiveSpawnDelay > 0.01f))
            {
                StopAllCoroutines();
                StartCoroutine(SpawnItemsWithDelay(_spawnDelay));
            }
            else
            {
                SpawnItemsInstant();
            }
        }

        private void SpawnItemsInstant()
        {
            int spawnCount = GetItemSpawnCount();
            for (int i = 0; i < spawnCount; i++)
                SpawnRandomPickup();
        }

        private IEnumerator SpawnItemsWithDelay(float delay)
        {
            yield return new WaitForTime(delay);

            int spawnCount = GetItemSpawnCount();
            for (int i = 0; i < spawnCount; i++)
            {
                var instance = SpawnRandomPickup().transform;
                var instancePosition = instance.position;
                var instanceRotation = instance.rotation;

                if (_spawnParticles != null)
                    Instantiate(_spawnParticles, instancePosition, instanceRotation);
                
                if (instance.TryGetComponent(out Rigidbody rigidB))
                {
                    rigidB.linearVelocity = Random.insideUnitSphere.normalized * _positionForce;
                    rigidB.angularVelocity = instanceRotation.eulerAngles * _angularForce;
                }

                AudioManager.Instance.PlayClip3D(_spawnAudio, transform.position);

                yield return new WaitForTime(_consecutiveSpawnDelay);
            }
        }

        private ItemPickup SpawnRandomPickup()
        {
            var itemStack = _lootTable.GenerateLoot(null, _rarityWeight);
            if (!itemStack.HasItem())
                return null;

            var spawnPosition = _collider.bounds.GetRandomPoint();
            var spawnRotation = Quaternion.Lerp(Quaternion.identity, Random.rotation, _randomRotation);
            return SpawnPickup(itemStack, spawnPosition, spawnRotation);
        }

        private ItemPickup SpawnPickup(ItemStack stack, Vector3 position, Quaternion rotation)
        {
            ItemPickup pickupPrefab = stack.Item.Definition.GetPickupForItemCount(stack.Count);
            var instance = Instantiate(pickupPrefab, position, rotation);
            instance.AttachItem(stack);
            
            if (_itemDestroyDelay > 0.01f)
                Destroy(instance, _itemDestroyDelay);

            return instance;
        }
        
        private int GetItemSpawnCount() => _itemSpawnCount.GetRandomFromRange();

        private void Awake()
        {
            _collider = GetComponent<BoxCollider>();
        }
    }
}