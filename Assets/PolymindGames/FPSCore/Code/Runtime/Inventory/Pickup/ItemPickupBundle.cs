using System.Collections.Generic;
using PolymindGames.SaveSystem;
using System.Text;
using PolymindGames.PoolingSystem;
using UnityEngine;

namespace PolymindGames.InventorySystem
{
    public sealed class ItemPickupBundle : MonoBehaviour, ISaveableComponent, IPoolableListener
    {
        [SerializeField]
        [Tooltip("Sound that will be played upon picking the item up.")]
        private AudioData _addAudio = new(null);
        
        [SpaceArea]
        [SerializeField, ReorderableList, IgnoreParent]
        private ItemGenerator[] _items = new ItemGenerator[1];

        private IHoverableInteractable _interactable;
        private List<ItemStack> _attachedItems;

        public void AddItem(ItemStack stack)
        {
            _attachedItems ??= new List<ItemStack>();
            _attachedItems.Add(stack);
        }

        private void Awake()
        {
            _interactable = GetComponent<IHoverableInteractable>();
            _interactable.Interacted += OnInteracted;
        }

        private void Start() => AddDefaultItems();

        private void AddDefaultItems()
        {
            _attachedItems ??= new List<ItemStack>();
            
            if (_attachedItems.Count == 0)
            {
                for (int i = 0; i < _items.Length; i++)
                    AddItem(_items[i].GenerateItem());
            }

            var stringBuilder = new StringBuilder(_attachedItems.Count * 10);
            foreach (var item in _attachedItems)
            {
                stringBuilder.Append(item.ToString());
                stringBuilder.Append("\n");
            }

            _interactable.Description = stringBuilder.ToString();
        }

        private void OnInteracted(IInteractable interactable, ICharacter character)
        {
            ItemPickupAddResult totalAddResult = ItemPickupAddResult.Failed;

            foreach (var item in _attachedItems)
            {
                var addResult = ItemPickupUtility.PickUpItem(character.Inventory, item, out MessageArgs messageArgs);

                totalAddResult = addResult switch
                {
                    // Upgrade total result based on individual results
                    ItemPickupAddResult.AddedFull => totalAddResult == ItemPickupAddResult.Failed ? ItemPickupAddResult.AddedFull : totalAddResult,
                    ItemPickupAddResult.AddedPartial => ItemPickupAddResult.AddedPartial,
                    _ => totalAddResult
                };

                // Dispatch a message to the local player only
                if (character.IsLocalPlayer())
                    MessageDispatcher.Instance.Dispatch(character, messageArgs);
            }

            // Play the pickup audio if any items were successfully added
            if (totalAddResult != ItemPickupAddResult.Failed)
                character.Audio.PlayClip(_addAudio, BodyPoint.Torso);

            // Release the item pickup if all items were fully added
            if (totalAddResult == ItemPickupAddResult.AddedFull)
                DisposePickup();
        }

        /// <summary>
        /// Releases the item pickup back to the pool if pooling is enabled, otherwise destroys the GameObject.
        /// </summary>
        private void DisposePickup()
        {
            if (TryGetComponent(out Poolable poolable))
            {
                poolable.Release();
            }
            else
            {
                Destroy(gameObject);
            }
        }
        
        #region Pooling
        public void OnAcquired() => AddDefaultItems();
        public void OnReleased() => _attachedItems.Clear();
        #endregion

		#region Save & Load
        void ISaveableComponent.LoadMembers(object data)
        {
            if (_attachedItems == null)
                _attachedItems = new List<ItemStack>();
            else
                _attachedItems.Clear();

            _attachedItems.AddRange((ItemStack[])data);
        }

        object ISaveableComponent.SaveMembers() => _attachedItems.ToArray();
        #endregion
    }
}