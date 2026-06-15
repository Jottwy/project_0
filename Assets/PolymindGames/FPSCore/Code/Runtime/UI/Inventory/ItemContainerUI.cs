using PolymindGames.InventorySystem;
using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine;
using System;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PolymindGames.UserInterface
{
    public class ItemContainerUI : MonoBehaviour
    {
        [SerializeField]
        private string _containerName;
        
        [SerializeField, PrefabObjectOnly]
        private ItemSlotUIBase _slotTemplate;
        
        private IItemContainer _container;
        private List<ItemSlotUIBase> _itemSlotsUI;

        public string ContainerName => _containerName;
        public IItemContainer Container => _container;
        public IReadOnlyList<ItemSlotUIBase> ItemSlotsUI => _itemSlotsUI;
        
        public event UnityAction<IItemContainer> AttachedContainerChanged;

        public void AttachToContainer(IItemContainer container)
        {
            if (container == _container)
                return;
            
            _container = container;
            _itemSlotsUI ??= new List<ItemSlotUIBase>(container.SlotsCount);
            GenerateSlots(container.SlotsCount);
            
            for (int i = 0; i < container.SlotsCount; i++)
            {
                _itemSlotsUI![i].AttachToSlot(container.GetSlot(i));
            }
            
            AttachedContainerChanged?.Invoke(container);
        }

        public void DetachFromContainer()
        {
            if (_container == null)
                return;

            foreach (var slotUI in _itemSlotsUI)
            {
                slotUI.AttachToSlot(SlotReference.Null);
            }

            _container = null;
        }

        public void Sort() => _container?.SortItems(ItemSorters.ByFullName);

        private void OnDestroy() => DetachFromContainer();

        private void GenerateSlots(int count)
        {
            if (count == _itemSlotsUI.Count)
                return;

            if (count is < 0 or > IItemContainer.MaxSlotsCount)
                throw new IndexOutOfRangeException();

            transform.GetComponentsInFirstChildren(_itemSlotsUI);

            if (count < _itemSlotsUI.Count)
            {
                for (int i = count; i < _itemSlotsUI.Count; i++)
                {
                    _itemSlotsUI[i].gameObject.SetActive(false);
                }

                for (int i = 0; i < count; i++)
                {
                    _itemSlotsUI[i].gameObject.SetActive(true);
                }

                // Only trim the list reference, not destroy anything
                _itemSlotsUI.RemoveRange(count, _itemSlotsUI.Count - count);
                return;
            }

            int amountToActivate = _itemSlotsUI.Count < count ? _itemSlotsUI.Count : count;
            for (int i = 0; i < amountToActivate; i++)
                _itemSlotsUI[i].gameObject.SetActive(true);

            if (count > _itemSlotsUI.Count)
            {
                if (_slotTemplate == null)
                {
                    Debug.LogError("No slot template is provided, can't generate any slots.", gameObject);
                    return;
                }

                int slotsToCreateCount = count - _itemSlotsUI.Count;

                _itemSlotsUI.Capacity = count;
                for (int i = 0; i < slotsToCreateCount; i++)
                {
                    ItemSlotUIBase slotInterface = Instantiate(_slotTemplate, transform);
                    slotInterface.gameObject.SetActive(true);
                    slotInterface.GetComponent<RectTransform>().anchoredPosition = Vector2.zero;
                    _itemSlotsUI.Add(slotInterface);
                }
            }
        }

        #region Editor
#if UNITY_EDITOR
        public void GenerateSlots_EditorOnly(int count)
        {
            if (Application.isPlaying)
                return;

            _itemSlotsUI ??= new List<ItemSlotUIBase>();
            transform.GetComponentsInFirstChildren(_itemSlotsUI);

            if (count == _itemSlotsUI.Count)
                return;

            if (count is < 0 or > 128)
                throw new IndexOutOfRangeException();

            Undo.RegisterFullObjectHierarchyUndo(gameObject, "Generate Slots");

            if (count < _itemSlotsUI.Count)
            {
                for (int i = count; i < _itemSlotsUI.Count; i++)
                {
                    var toDestroy = _itemSlotsUI[i].gameObject;
                    Undo.DestroyObjectImmediate(toDestroy);
                }

                for (int i = 0; i < count; i++)
                {
                    Undo.RecordObject(_itemSlotsUI[i].gameObject, "Activate Slot");
                    _itemSlotsUI[i].gameObject.SetActive(true);
                }

                _itemSlotsUI.RemoveRange(count, _itemSlotsUI.Count - count);
                return;
            }

            int amountToActivate = _itemSlotsUI.Count < count ? _itemSlotsUI.Count : count;
            for (int i = 0; i < amountToActivate; i++)
            {
                Undo.RecordObject(_itemSlotsUI[i].gameObject, "Activate Slot");
                _itemSlotsUI[i].gameObject.SetActive(true);
            }

            if (count > _itemSlotsUI.Count)
            {
                if (_slotTemplate == null)
                {
                    Debug.LogError("No slot template is provided, can't generate any slots.", gameObject);
                    return;
                }

                int slotsToCreateCount = count - _itemSlotsUI.Count;

                for (int i = 0; i < slotsToCreateCount; i++)
                {
                    var slot = (ItemSlotUIBase)PrefabUtility.InstantiatePrefab(_slotTemplate, transform);
                    Undo.RegisterCreatedObjectUndo(slot.gameObject, "Create Slot");
                    slot.gameObject.SetActive(true);
                    slot.GetComponent<RectTransform>().anchoredPosition = Vector2.zero;
                    _itemSlotsUI.Add(slot);
                }
            }
        }
#endif
        #endregion
    }
}