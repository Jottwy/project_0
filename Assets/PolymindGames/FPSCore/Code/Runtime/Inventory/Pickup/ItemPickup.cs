using PolymindGames.SaveSystem;
using UnityEngine;
using System;
using PolymindGames.PoolingSystem;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Basic item pickup. References one item from the Database.
    /// </summary>
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/interaction/interactable/demo-interactables")]
    public class ItemPickup : MonoBehaviour, ISaveableComponent, IPoolableListener
    {
        [SerializeField]
        [Tooltip("Sound that will be played upon picking the item up.")]
        private AudioData _addAudio = new(null);

        [SerializeField, SpaceArea]
        [DataReference(HasAssetReference = true, HasIcon = true)]
        private DataIdReference<ItemDefinition> _item = new(0);

        [SerializeField, Range(1, 100)]
#if UNITY_EDITOR
        [ShowIf(nameof(IsItemStackable), true)]
#endif
        private int _minCount = 1;

        [SerializeField, Range(1, 100)]
#if UNITY_EDITOR
        [ShowIf(nameof(IsItemStackable), true)]
#endif
        private int _maxCount;

        [NonSerialized]
        private ItemStack _attachedItem;

        private IHoverableInteractable _interactable;

        protected IHoverableInteractable Interactable
        {
            get
            {
                if (_interactable == null && TryGetComponent(out _interactable))
                    _interactable.Interacted += OnInteracted;

                return _interactable;
            }
        }

        public ItemStack AttachedItem => _attachedItem;
        public bool ResetOnAcquiredFromPool { get; set; } = true;

        /// <summary>
        /// Links the pickup with a specified item, setting the interaction title and description.
        /// </summary>
        /// <param name="item">The item to associate with the pickup.</param>
        public virtual void AttachItem(ItemStack item)
        {
            if (!item.HasItem())
                return;
            
            _attachedItem = item;
            
            if (string.IsNullOrEmpty(Interactable.Title))
                Interactable.Title = AttachedItem.Item.Name;
            
            if (string.IsNullOrEmpty(Interactable.Description))
                Interactable.Description = AttachedItem.Item.Definition.Description;
        }
        
        protected virtual void Start()
        {
            if (!_attachedItem.HasItem())
                AttachItem(CreateDefaultItem());
        }

        /// <summary>
        /// Provides a default item instance if none is specified. Generates a random stack count within a range.
        /// </summary>
        protected virtual ItemStack CreateDefaultItem()
        {
            int count = _minCount != _maxCount ? UnityEngine.Random.Range(_minCount, _maxCount + 1) : _minCount;
            return _item.IsNull ? ItemStack.Null : new ItemStack(new Item(_item.Def), count);
        }

        /// <summary>
        /// Handles the interaction when a player interacts with the item pickup.
        /// </summary>
        protected virtual void OnInteracted(IInteractable interactable, ICharacter character)
        {
            PickUpAttachedItem(character);
        }

        protected ItemPickupAddResult PickUpAttachedItem(ICharacter character, IItemContainer specificContainer = null)
        {
            ItemPickupAddResult addResult;
            MessageArgs messageArgs;

            if (specificContainer != null)
            {
                addResult = ItemPickupUtility.PickUpItem(specificContainer, AttachedItem, out messageArgs);
                if (addResult == ItemPickupAddResult.Failed)
                    addResult = ItemPickupUtility.PickUpItem(character.Inventory, AttachedItem, out messageArgs);
            }
            else
            {
                addResult = ItemPickupUtility.PickUpItem(character.Inventory, AttachedItem, out messageArgs);
            }

            // Dispatch a message to the local player only
            if (character.IsLocalPlayer())
                MessageDispatcher.Instance.Dispatch(character, messageArgs);
            
            // Play the pickup audio if any items were successfully added
            if (addResult != ItemPickupAddResult.Failed)
                character.Audio.PlayClip(_addAudio, BodyPoint.Torso);

            // Release the item pickup if all items were fully added
            if (addResult == ItemPickupAddResult.AddedFull)
                DisposePickup();

            return addResult;
        }

        /// <summary>
        /// Releases the item pickup back to the pool if pooling is enabled, otherwise destroys the GameObject.
        /// </summary>
        private void DisposePickup()
        {
            if (TryGetComponent(out Poolable poolable))
                poolable.Release();
            else
                Destroy(gameObject);
        }

        #region Pooling
        public virtual void OnAcquired()
        {
            if (ResetOnAcquiredFromPool)
                AttachItem(CreateDefaultItem());
        }

        public virtual void OnReleased() { }
        #endregion

        #region Save & Load
        void ISaveableComponent.LoadMembers(object data) => AttachItem((ItemStack)data);
        object ISaveableComponent.SaveMembers() => AttachedItem;
		#endregion

		#region Editor
#if UNITY_EDITOR
        protected bool IsItemStackable() => !_item.IsNull && _item.Def.StackSize > 1;
        
        protected virtual void OnValidate()
        {
            _maxCount = Mathf.Max(_maxCount, _minCount);
        }
#endif
		#endregion
    }
}