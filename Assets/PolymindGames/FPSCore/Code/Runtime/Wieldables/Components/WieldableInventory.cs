using PolymindGames.InventorySystem;
using System.Collections.Generic;
using PolymindGames.SaveSystem;
using UnityEngine.Events;
using UnityEngine;
using System;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Takes care of selecting wieldables based on inventory items.
    /// </summary>
    [RequireCharacterComponent(typeof(IWieldablesControllerCC))]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault1)]
    public sealed class WieldableInventory : CharacterBehaviour, IWieldableInventoryCC, ISaveableComponent, IEditorValidate
    {
        [SerializeField, Range(0, 10)]
        private int _startingSlotIndex;

        [SerializeField, Range(0.5f, 5f), Title("Drop Settings")]
        private float _dropHolsterSpeed = 1.45f;

        [SerializeField, Range(0f, 10f)]
        private float _dropDelay = 0.35f;

        [SerializeField]
#if UNITY_EDITOR
        [EditorButton(nameof(AddAllWieldables), "Add all wieldable prefabs", ButtonActivityType.OnEditMode)]
#endif
        private bool _dropOnDeath;

        private Dictionary<int, WieldableItem> _wieldableItems;
        private IWieldablesControllerCC _controller;
        private IWieldable _equippedWieldable;
        private IItemContainer _holster;

        /// <inheritdoc/>
        public int SelectedIndex { get; private set; } = -1;

        /// <inheritdoc/>
        public int PreviousIndex { get; private set; } = -1;

        /// <inheritdoc/>
        public event UnityAction<int> SelectedIndexChanged;

        /// <inheritdoc/>
        public void SelectAtIndex(int index, bool allowRefresh = true)
        {
            index = Mathf.Clamp(index, -1, _holster.SlotsCount - 1);
            if (index == SelectedIndex && _equippedWieldable != null)
            {
                // Re-equip wieldable
                if (allowRefresh && _controller.ActiveWieldable != _equippedWieldable)
                {
                    EquipWieldable(SelectedIndex != -1 ? _holster.GetSlot(SelectedIndex) : SlotReference.Null);
                }

                return;
            }

            SetSelectedIndex(index);
            EquipWieldable(index != -1 ? _holster.GetSlot(index) : SlotReference.Null);
        }

        /// <inheritdoc/>
        public bool DropWieldable(bool forceDrop = false)
        {
            if (SelectedIndex == -1 || !forceDrop && _controller.State != WieldableControllerState.None)
                return false;

            var itemStack = _holster.GetItemAtIndex(SelectedIndex);
            if (itemStack.HasItem()
                && _wieldableItems.TryGetValue(itemStack.Item.Id, out var wieldable)
                && wieldable.Wieldable == _controller.ActiveWieldable)
            {
                EquipWieldable(SlotReference.Null, forceDrop ? float.MaxValue : _dropHolsterSpeed);
                CoroutineUtility.InvokeDelayed(this, Character.Inventory.DropItem, itemStack, forceDrop ? 0f : _dropDelay);
                return true;
            }

            return false;
        }

        /// <inheritdoc/>
        public IWieldable GetWieldableWithId(int itemId)
        {
            return _wieldableItems.TryGetValue(itemId, out var wieldableItem)
                ? wieldableItem.Wieldable
                : null;
        }

        private void SetSelectedIndex(int newIndex)
        {
            // Unsubscribe From Item Changed
            if (PreviousIndex != -1)
            {
                _holster.RemoveSlotChangedListener(PreviousIndex, OnSlotChanged);
            }

            // Subscribe To Item Changed
            if (newIndex != -1)
            {
                _holster.AddSlotChangedListener(newIndex, OnSlotChanged);
            }

            PreviousIndex = SelectedIndex;
            SelectedIndex = newIndex;
            SelectedIndexChanged?.Invoke(newIndex);
        }

        private void OnSlotChanged(in SlotReference slot, SlotChangeType changeType)
        {
            if (changeType == SlotChangeType.ItemChanged)
                EquipWieldable(slot, _dropHolsterSpeed);
        }

        private void EquipWieldable(SlotReference slot, float holsterSpeed = 1f)
        {
            if (_equippedWieldable != null)
            {
                _controller.TryHolsterWieldable(_equippedWieldable, holsterSpeed);
                _equippedWieldable = null;
            }

            if (slot.TryGetItem(out var item) && _wieldableItems.TryGetValue(item.Id, out var wieldableItem))
            {
                _equippedWieldable = wieldableItem.Wieldable;
                _controller.TryEquipWieldable(_equippedWieldable, holsterSpeed, () => wieldableItem.AttachToSlot(slot));
            }
        }

        #region Initialization
        protected override void OnBehaviourStart(ICharacter character)
        {
            _controller = character.GetCC<IWieldablesControllerCC>();
            InitializeWieldablesCache();

            var wieldableTag = ItemConstants.WieldableTag;
            _holster ??= Character.Inventory.FindContainer(ItemContainerFilters.WithTag(wieldableTag));

            var health = Character.HealthManager;
            health.Respawn += OnRespawn;
            health.Death += OnDeath;

            // If the index is -1, we can use the starting index since it indicates this is the first time this component has been active.
            SelectAtIndex(SelectedIndex == -1 ? _startingSlotIndex : SelectedIndex);
        }

        protected override void OnBehaviourDestroy(ICharacter character)
        {
            var health = Character.HealthManager;
            health.Respawn -= OnRespawn;
            health.Death -= OnDeath;
        }

        private void OnDeath(in DamageArgs args)
        {
            if (_dropOnDeath)
            {
                DropWieldable(forceDrop: true);
            }

            SelectAtIndex(-1);
        }

        private void OnRespawn()
        {
            SelectAtIndex(PreviousIndex);
        }

        private void InitializeWieldablesCache()
        {
            var wieldables = _controller.WieldablesRoot.gameObject.GetComponentsInFirstChildren<WieldableItem>(false, 16);
            _wieldableItems = new Dictionary<int, WieldableItem>(wieldables.Count);

            foreach (var wieldableItem in wieldables)
            {
                if (wieldableItem == null || !wieldableItem.gameObject.HasComponent<IWieldable>() ||
                    wieldableItem.ReferencedItem.IsNull)
                {
                    Debug.LogWarning("Wieldable Item or Wieldable is null.", wieldableItem);
                    continue;
                }

                if (!_wieldableItems.TryAdd(wieldableItem.ReferencedItem, wieldableItem))
                {
                    Debug.LogWarning("You're trying to instantiate a wieldable with an id that has already been added.", gameObject);
                    continue;
                }

                _controller.RegisterWieldable(wieldableItem.Wieldable);
            }
        }
        #endregion

        #region Save & Load
        [Serializable]
        private sealed class SaveData
        {
            public int SelectedIndex;
            public int PreviousIndex;
        }

        void ISaveableComponent.LoadMembers(object data)
        {
            var saveData = (SaveData)data;
            SelectedIndex = saveData.SelectedIndex;
            PreviousIndex = saveData.PreviousIndex;
        }

        object ISaveableComponent.SaveMembers() => new SaveData()
        {
            SelectedIndex = SelectedIndex,
            PreviousIndex = PreviousIndex
        };
        #endregion

        #region Editor
#if UNITY_EDITOR
        private void AddAllWieldables()
        {
            var controller = GetComponent<IWieldablesControllerCC>();
            if (controller != null)
                AddAllWieldables(controller.WieldablesRoot);
        }

        private void AddAllWieldables(Transform wieldablesRoot)
        {
            // Destroy wieldable children that are not part of a prefab instance
            for (int i = wieldablesRoot.childCount - 1; i >= 0; i--) // Iterate in reverse to avoid index issues
            {
                var child = wieldablesRoot.GetChild(i);
                if (child.gameObject.HasComponent<IWieldableItem>() || child.gameObject.HasComponent<IWieldableArmsHandlerCC>())
                {
                    // Register undo before destroying
                    Undo.DestroyObjectImmediate(child.gameObject);
                }
            }

            // Load and instantiate wieldable prefabs
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab");
            foreach (var guid in prefabGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var obj = AssetDatabase.LoadAssetAtPath<GameObject>(path);

                if (obj != null)
                {
                    if (obj.gameObject.HasComponent<IWieldableItem>())
                    {
                        GameObject wieldable = (GameObject)PrefabUtility.InstantiatePrefab(obj, wieldablesRoot);
                        wieldable.SetActive(false);
                        Undo.RegisterCreatedObjectUndo(wieldable, "Instantiate Wieldable");
                    }
                    else if (obj.gameObject.HasComponent<IWieldableArmsHandlerCC>())
                    {
                        GameObject arms = (GameObject)PrefabUtility.InstantiatePrefab(obj, wieldablesRoot);
                        arms.SetActive(true);
                        Undo.RegisterCreatedObjectUndo(arms, "Instantiate Arms");
                    }
                }
            }

            // Mark object as dirty so changes are saved
            EditorUtility.SetDirty(this);
        }

        void IEditorValidate.ValidateInEditor()
        {
            AddAllWieldables();
        }
#endif
        #endregion
    }
}