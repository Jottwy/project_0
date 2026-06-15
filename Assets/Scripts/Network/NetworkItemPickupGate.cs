using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase 2: replaces the vendor ItemPickup's local-pickup behaviour on a replicated STP
    /// item. Subscribes to the item's <see cref="IInteractable.Interacted"/>; when the LOCAL
    /// player interacts, it routes the pickup through the host (<see cref="StpPickupController"/>)
    /// instead of adding to the inventory locally. StpItemReplicator destroys the vendor
    /// ItemPickup before its Start runs, so only this gate responds to the interaction.
    /// </summary>
    public sealed class NetworkItemPickupGate : MonoBehaviour
    {
        public uint itemId;

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

            StpPickupController.RequestPickup(itemId, character);
        }
    }
}
