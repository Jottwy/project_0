using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase B2.5: replaces the vendor CarryablePickup's local-carry behaviour on a replicated
    /// carryable. Subscribes to the object's <see cref="IInteractable.Interacted"/>; when the
    /// LOCAL player interacts, it routes the pickup through the host
    /// (<see cref="StpCarryablePickupController"/>) instead of carrying it locally.
    /// StpCarryableReplicator destroys the vendor CarryablePickup before its Start runs, so only
    /// this gate responds to the interaction. Mirrors <see cref="NetworkItemPickupGate"/>.
    /// </summary>
    public sealed class StpCarryablePickupGate : MonoBehaviour
    {
        public uint carryableId;

        private IInteractable _interactable;
        private bool _subscribed;

        private void OnEnable()
        {
            if (_interactable == null)
                _interactable = GetComponent<IInteractable>();

            if (_interactable != null && !_subscribed)
            {
                _interactable.Interacted += OnInteracted;
                _subscribed = true;
            }
        }

        private void OnDisable()
        {
            if (_interactable != null && _subscribed)
            {
                _interactable.Interacted -= OnInteracted;
                _subscribed = false;
            }
        }

        private void OnInteracted(IInteractable interactable, ICharacter character)
        {
            // Only the local player's interaction drives a request; the host decides.
            if (character == null || !character.IsLocalPlayer())
                return;

            StpCarryablePickupController.RequestPickup(carryableId, character);
        }
    }
}
