using System.Collections;
using UnityEngine;

namespace PolymindGames.InventorySystem
{
    [CreateAssetMenu(menuName = "Polymind Games/Items/Actions/Equip Action", fileName = "ItemAction_Equip")]
    public sealed class EquipAction : ItemAction
    {
        /// <inheritdoc/>
        public override float GetDuration(ICharacter character, ItemStack stack) => 0f;

        /// <inheritdoc/>
        public override bool CanPerform(ICharacter character, ItemStack stack)
            => !stack.Item.Definition.Tag.IsNull;

        /// <inheritdoc/>
        protected override IEnumerator Execute(ICharacter character, SlotReference parentSlot, ItemStack stack, float duration)
        {
            var itemTag = stack.Item.Definition.Tag;
            var targetContainer = character.Inventory.FindContainer(ItemContainerFilters.WithTag(itemTag));
    
            if (targetContainer == null) yield break;

            bool isWieldable = itemTag == ItemConstants.WieldableTag;
            bool sameContainer = parentSlot.Container == targetContainer;

            if (isWieldable && character.TryGetCC(out IWieldableInventoryCC selection))
            {
                if (sameContainer)
                {
                    selection.SelectAtIndex(targetContainer.FindSlot(ItemSlotFilters.WithItem(stack.Item)).Index);
                    yield break;
                }

                if (targetContainer.AddItem(stack).addedCount > 0)
                {
                    selection.SelectAtIndex(targetContainer.FindSlot(ItemSlotFilters.WithItem(stack.Item)).Index);
                    parentSlot.Clear();
                }
                else
                {
                    parentSlot.TransferOrSwapWithSlot(targetContainer.GetSlot(selection.SelectedIndex));
                }
            }
            else
            {
                if (!sameContainer && targetContainer.AddItem(stack).addedCount > 0)
                {
                    parentSlot.Clear();
                }
                else
                {
                    parentSlot.TransferOrSwapWithSlot(targetContainer.GetSlot(targetContainer.SlotsCount - 1));
                }
            }
        }
    }
}