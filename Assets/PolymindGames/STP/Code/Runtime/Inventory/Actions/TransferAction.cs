using System.Collections.Generic;
using System.Collections;
using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    [CreateAssetMenu(menuName = "Polymind Games/Items/Actions/Transfer Action", fileName = "ItemAction_AutoMove")]
    public sealed class TransferAction : ItemAction
    {
        /// <inheritdoc/>
        public override float GetDuration(ICharacter character, ItemStack stack) => 0f;

        /// <inheritdoc/>
        public override bool CanPerform(ICharacter character, ItemStack stack) => stack.HasItem();

        /// <inheritdoc/>
        protected override IEnumerator Execute(ICharacter character, SlotReference parentSlot, ItemStack stack, float duration)
        {
            if (!parentSlot.IsValid())
                yield break;

            var externalContainers = GetInspectedContainers(character.GetCC<IInventoryInspectionManagerCC>());
            var characterContainers = character.Inventory.Containers;

            // Determine whether the parent slot belongs to an external container
            bool isExternalSlot = externalContainers.Contains(parentSlot.Container);

            if (isExternalSlot)
            {
                if (parentSlot.TransferOrSwapToUntaggedContainer(characterContainers))
                    yield break;
                
                if (parentSlot.TransferOrSwapToTaggedContainer(characterContainers))
                    yield break;
                
                if (parentSlot.TransferOrSwapToTaggedContainer(externalContainers))
                    yield break;
                
                parentSlot.TransferOrSwapToUntaggedContainer(externalContainers);
            }
            else
            {
                if (parentSlot.TransferOrSwapToTaggedContainer(externalContainers))
                    yield break;
                
                if (parentSlot.TransferOrSwapToUntaggedContainer(externalContainers))
                    yield break;
                
                if (parentSlot.TransferOrSwapToTaggedContainer(characterContainers))
                    yield break;
                
                parentSlot.TransferOrSwapToUntaggedContainer(characterContainers);
            }
        }

        /// <summary>
        /// Retrieves external containers from the specified inventory inspection manager.
        /// </summary>
        private static IReadOnlyList<IItemContainer> GetInspectedContainers(IInventoryInspectionManagerCC inspector)
            => inspector == null ? Array.Empty<IItemContainer>() : inspector.InspectedContainers;
    }
}