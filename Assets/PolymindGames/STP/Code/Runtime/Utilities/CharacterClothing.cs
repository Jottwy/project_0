using PolymindGames.InventorySystem;
using UnityEngine;
using System;

namespace PolymindGames
{
    [DefaultExecutionOrder(ExecutionOrderConstants.AfterDefault2)]
    public sealed class CharacterClothing : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Should this component attach itself to its parent character when the game starts?")]
        private bool _attachToCharacter = true;
        
        [SerializeField, NotNull]
        private SkinnedMeshRenderer _bodyRenderer;

        [SerializeField]
        private AudioData _clothChangedAudio;
        
        [SerializeField, LabelByChild("Type"), SpaceArea]
        [ReorderableList(fixedSize: true, Draggable = false)]
        private ClothingClassData[] _clothing;

        private readonly ClothingItemData[] _activeClothes = new ClothingItemData[BodyPointUtility.TotalBodyPoints];

        private IItemContainer _feetContainer;
        private IItemContainer _headContainer;
        private IItemContainer _legsContainer;
        private IItemContainer _torsoContainer;
        private ICharacter _character;

        public void AttachToCharacter(ICharacter character)
        {
            if (character == null || character.Inventory == null)
            {
                Debug.LogError("This component requires a character with an inventory.", gameObject);
                return;
            }

            _character = character;
            var inventory = character.Inventory;

            _headContainer = inventory.FindContainer(ItemContainerFilters.WithTag(ItemConstants.HeadEquipmentTag));
            _torsoContainer = inventory.FindContainer(ItemContainerFilters.WithTag(ItemConstants.TorsoEquipmentTag));
            _legsContainer = inventory.FindContainer(ItemContainerFilters.WithTag(ItemConstants.LegsEquipmentTag));
            _feetContainer = inventory.FindContainer(ItemContainerFilters.WithTag(ItemConstants.FeetEquipmentTag));

            _headContainer.SlotChanged += OnSlotChanged;
            _torsoContainer.SlotChanged += OnSlotChanged;
            _legsContainer.SlotChanged += OnSlotChanged;
            _feetContainer.SlotChanged += OnSlotChanged;

            OnClothingChanged(BodyPoint.Head, _headContainer.GetSlot(0), false);
            OnClothingChanged(BodyPoint.Torso, _torsoContainer.GetSlot(0), false);
            OnClothingChanged(BodyPoint.Legs, _legsContainer.GetSlot(0), false);
            OnClothingChanged(BodyPoint.Feet, _feetContainer.GetSlot(0), false);
        }

        public void DetachFromCharacter()
        {
            if (_character == null)
                return;
            
            _headContainer.SlotChanged -= OnSlotChanged;
            _torsoContainer.SlotChanged -= OnSlotChanged;
            _legsContainer.SlotChanged -= OnSlotChanged;
            _feetContainer.SlotChanged -= OnSlotChanged;
            
            _headContainer = null;
            _torsoContainer = null;
            _legsContainer = null;
            _feetContainer = null;
            _character = null;
        }
        
        public void SetClothing(BodyPoint bodyPoint, int itemId)
        {
            var clothingItem = GetClothingData(bodyPoint, itemId);
            SetClothing(bodyPoint, clothingItem);
        }

        private void Start()
        {
            if (!_attachToCharacter)
                return;
            
            _character = GetComponentInParent<ICharacter>();
            AttachToCharacter(_character);
        }

        private void OnDestroy()
        {
            DetachFromCharacter();
        }

        private void OnSlotChanged(in SlotReference slot, SlotChangeType changeType)
        {
            if (changeType == SlotChangeType.CountChanged)
                return;
            
            var container = slot.Container;
            BodyPoint bodyPoint = GetBodyPointFromContainer(container);
            OnClothingChanged(bodyPoint, slot, true);
        }

        private void OnClothingChanged(BodyPoint bodyPoint, in SlotReference slot, bool playEffects = true)
        {
            int itemId = slot.GetItem()?.Id ?? 0;
            SetClothing(bodyPoint, itemId);
            
            if (playEffects)
                _character?.Audio.PlayClip(_clothChangedAudio, bodyPoint);
        }

        private BodyPoint GetBodyPointFromContainer(IItemContainer container)
        {
            if (container == _headContainer) 
                return BodyPoint.Head;
            
            if (container == _torsoContainer)
                return BodyPoint.Torso;
            
            if (container == _legsContainer)
                return BodyPoint.Legs;

            return BodyPoint.Feet;
        }

        private ClothingItemData GetClothingData(BodyPoint bodyPoint, int itemId)
        {
            var clothingItems = _clothing[(int)bodyPoint].Items;
            foreach (var clothingItem in clothingItems)
            {
                if (clothingItem.Item == itemId)
                    return clothingItem;
            }

            return null;
        }

        private void SetClothing(BodyPoint bodyPoint, ClothingItemData clothingItem)
        {
            int bodyPointIndex = (int)bodyPoint;

            var prevActiveItem = _activeClothes[bodyPointIndex];
            prevActiveItem?.Renderer.gameObject.SetActive(false);

            _activeClothes[bodyPointIndex] = clothingItem;
            clothingItem?.Renderer.gameObject.SetActive(true);

            UpdateOpacityMasksInShader(bodyPoint, clothingItem);
        }

        private void UpdateOpacityMasksInShader(BodyPoint bodyPoint, ClothingItemData clothingItem)
        {
            string shaderProperty = $"_OpacityMask_{bodyPoint}";
            Texture2D opacityMask = clothingItem?.OpacityMask;
            _bodyRenderer.material.SetTexture(shaderProperty, opacityMask);
        }

        #region Internal Types
        [Serializable]
        private struct ClothingClassData
        {
            [Hide, JetBrains.Annotations.UsedImplicitly]
            public BodyPoint Type;

            [LabelByChild("Renderer")]
            [ReorderableList(ListStyle.Lined, HasHeader = false)]
            public ClothingItemData[] Items;

            public ClothingClassData(BodyPoint type)
            {
                Type = type;
                Items = Array.Empty<ClothingItemData>();
            }
        }

        [Serializable]
        private class ClothingItemData
        {
            public DataIdReference<ItemDefinition> Item;

            [NotNull]
            public SkinnedMeshRenderer Renderer;

            public Texture2D OpacityMask;
        }
        #endregion
        
        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_clothing == null || _clothing.Length != BodyPointUtility.TotalBodyPoints)
            {
                _clothing = new ClothingClassData[BodyPointUtility.TotalBodyPoints];
                for (int i = 0; i < _clothing.Length; i++)
                    _clothing[i] = new ClothingClassData(BodyPointUtility.BodyPoints[i]);
            }

            for (int i = 0; i < _clothing.Length; i++)
                _clothing[i].Type = BodyPointUtility.BodyPoints[i];
        }
#endif
        #endregion
    }
}