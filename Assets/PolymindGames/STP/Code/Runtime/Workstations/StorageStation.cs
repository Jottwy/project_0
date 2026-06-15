using PolymindGames.InventorySystem;
using System.Collections.Generic;
using PolymindGames.SaveSystem;
using UnityEngine;

namespace PolymindGames
{
    [DefaultExecutionOrder(ExecutionOrderConstants.AfterDefault2)]
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/interaction/interactable/demo-interactables")]
    public sealed class StorageStation : Workstation, ISaveableComponent
    {
        [Title("Storage")]
        [SerializeField, SceneObjectOnly]
        private Inventory _inventory;

        [SerializeField, SubGroup, SpaceArea]
        [DisableIf(nameof(_inventory), true)]
        private ItemContainerGenerator _defaultContainer;
        
        private IReadOnlyList<IItemContainer> _containers;

        public override IReadOnlyList<IItemContainer> GetContainers()
        {
            _containers ??= new[]
            {
                _defaultContainer.GenerateContainer(null, true, Name)
            };

            return _containers;
        }

        protected override void Start()
        {
            base.Start();

            if (_inventory != null)
                _containers = _inventory.Containers;
        }

        #region Save & Load
        void ISaveableComponent.LoadMembers(object data)
        {
            if (_inventory != null)
                return;
            
            if (data is ItemContainer container)
            {
                _containers = new[]
                {
                    container
                };
                
                container.InitializeAfterDeserialization(null, null);
            }
        }

        object ISaveableComponent.SaveMembers()
        {
            return _inventory != null ? null : _containers?[0];

        }
        #endregion
    }
}