using System.Collections;
using UnityEngine;

namespace PolymindGames.InventorySystem
{
    [CreateAssetMenu(menuName = "Polymind Games/Items/Actions/Drop Action", fileName = "ItemAction_Drop")]
    public sealed class DropAction : ItemAction
    {
        [SerializeField, Title("Dropping")]
        [Tooltip("The body point from which the item will be dropped.")]
        private BodyPoint _dropPoint;

        [SerializeField]
        private Vector3 _dropOffset;

        [SerializeField, Range(0f, 100f)]
        [Tooltip("The force applied to the dropped item.")]
        private float _dropForce = 10f;

        [SerializeField]
        [Tooltip("The audio that will play when the item is dropped.")]
        private AudioData _dropAudio;

        [SerializeField]
        [Tooltip("Determines if the item should be removed from the inventory after being dropped.")]
        private bool _removeFromInventory = true;

        private const float RotationRandomnessFactor = 0.1f;
        private const float ObstacleCheckDistance = 0.5f;
        private const float ObstacleCheckRadius = 0.5f;

        /// <inheritdoc/>
        public override float GetDuration(ICharacter character, ItemStack stack) => 0f;

        /// <inheritdoc/>
        public override bool CanPerform(ICharacter character, ItemStack stack) => true;

        /// <inheritdoc/>
        protected override IEnumerator Execute(ICharacter character, SlotReference parentSlot, ItemStack stack, float duration)
        {
            // Get the pickup prefab for the item stack.
            var pickupPrefab = stack.Item.Definition.GetPickupForItemCount(stack.Count);

            if (pickupPrefab == null)
            {
                Debug.LogError($"No pickup prefab found for item {stack.Item.Name}. Assign a generic prefab in the item's definition.", this);
                yield break;
            }

            var pickupInstance = SpawnPickup(character, parentSlot, pickupPrefab);
            if (pickupInstance == null)
                yield break;
            
            pickupInstance.AttachItem(stack);
            
            // Remove item from inventory if necessary.
            if (_removeFromInventory)
                parentSlot.Clear();
        }

        private ItemPickup SpawnPickup(ICharacter character, in SlotReference slot, ItemPickup pickupPrefab)
        {
            if (character != null)
            {
                Transform dropPoint = character.GetTransformOfBodyPoint(_dropPoint);

                // Check if an obstacle is in front of the drop point.
                bool isObstacleInFront = CheckObstacleAhead(dropPoint);

                // Calculate drop parameters.
                Vector3 centerOffset = CalculateCenterOffset(pickupPrefab);
                Vector3 dropPosition = CalculateDropPosition(dropPoint, isObstacleInFront) - centerOffset;
                Quaternion dropRotation = CalculateDropRotation(dropPoint);

                // Instantiate the pickup and apply forces.
                var pickupInstance = Instantiate(pickupPrefab, dropPosition, dropRotation);

                // Apply drop force only if there's an obstacle ahead.
                var rigidbody = pickupInstance.GetComponent<Rigidbody>();
                Vector3 dropForce = !isObstacleInFront ? (dropPoint.forward + Vector3.up * 0.25f).normalized * _dropForce : Vector3.zero;
                float dropTorque = !isObstacleInFront ? _dropForce : 0f;

                character.ThrowObject(rigidbody, dropForce, dropTorque);
                character.Audio.PlayClip(_dropAudio, BodyPoint.Torso);
                return pickupInstance;
            }
            else
            {
                var containerTransform = slot.Container?.Inventory?.transform;

                if (containerTransform == null)
                {
                    Debug.LogError("Container transform is missing. Ensure the container is properly set up.", this);
                    return null;
                }

                Vector3 dropPosition = containerTransform.position;
                AudioManager.Instance.PlayClip3D(_dropAudio, dropPosition);
                return Instantiate(pickupPrefab, dropPosition, Quaternion.identity);
            }
        }

        /// <summary>
        /// Calculates the center offset of the pickup based on its collider.
        /// </summary>
        /// <param name="pickupPrefab">The item pickup prefab.</param>
        /// <returns>The offset vector from the collider's center, or zero if no collider is found.</returns>
        private Vector3 CalculateCenterOffset(ItemPickup pickupPrefab)
        {
            if (pickupPrefab.TryGetComponent(out Collider collider))
                return collider.bounds.center - pickupPrefab.transform.position;

            return Vector3.zero;
        }

        /// <summary>
        /// Checks whether there is an obstacle in front of the drop point using a sphere cast.
        /// </summary>
        /// <param name="dropPoint">The transform representing the drop position.</param>
        /// <returns>True if an obstacle is detected, otherwise false.</returns>
        private static bool CheckObstacleAhead(Transform dropPoint)
        {
            Ray ray = new Ray(dropPoint.position, dropPoint.forward);
            return PhysicsUtility.SphereCastOptimized(ray, ObstacleCheckRadius, ObstacleCheckDistance, LayerConstants.SimpleSolidObjectsMask);
        }

        /// <summary>
        /// Determines the drop position based on whether an obstacle is detected.
        /// If an obstacle is present, the item is dropped near the character's feet instead.
        /// </summary>
        /// <param name="dropPoint">The transform representing the drop point.</param>
        /// <param name="isObstacleInFront">Whether an obstacle is detected in front.</param>
        /// <returns>The computed drop position.</returns>
        private Vector3 CalculateDropPosition(Transform dropPoint, bool isObstacleInFront)
        {
            if (isObstacleInFront)
            {
                Vector3 characterPosition = dropPoint.root.position;
                return new Vector3(characterPosition.x, dropPoint.position.y, characterPosition.z);
            }

            return dropPoint.position + dropPoint.TransformVector(_dropOffset);
        }

        /// <summary>
        /// Calculates the rotation for the dropped object, blending its forward direction with random rotation.
        /// </summary>
        /// <param name="dropPoint">The transform representing the drop point.</param>
        /// <returns>A blended rotation between the drop point's forward direction and random rotation.</returns>
        private Quaternion CalculateDropRotation(Transform dropPoint)
        {
            Quaternion forwardRotation = Quaternion.LookRotation(dropPoint.forward);
            Quaternion randomRotation = Random.rotationUniform;
            return Quaternion.Lerp(forwardRotation, randomRotation, RotationRandomnessFactor);
        }
    }
}