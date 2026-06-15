using System.Collections;
using UnityEngine;

namespace PolymindGames.InventorySystem
{
    [CreateAssetMenu(menuName = "Polymind Games/Items/Actions/Dismantle Action", fileName = "ItemAction_Dismantle")]
    public sealed class DismantleAction : ItemAction
    {
        [Title("Dismantling")]
        [SerializeField, Suffix("sec"), Clamp(0f, 100f)]
        private float _durationPerGivenItem = 2f;

        [SerializeField]
        private DataIdReference<ItemPropertyDefinition> _durabilityProperty;

        [SerializeField]
        private AudioData _dismantleAudio;

        /// <inheritdoc/>
        public override float GetDuration(ICharacter character, ItemStack stack)
        {
            if (stack.Item.Definition.TryGetDataOfType<CraftingData>(out var craftData))
                return craftData.Blueprint.Length * _durationPerGivenItem;

            return 0f;
        }

        /// <inheritdoc/>
        public override bool CanPerform(ICharacter character, ItemStack stack)
            => stack.Item.Definition.TryGetDataOfType<CraftingData>(out var craftData) && craftData.AllowDismantle;

        /// <inheritdoc/>
        protected override IEnumerator Execute(ICharacter character, SlotReference parentSlot, ItemStack stack, float duration)
        {
            character.Audio.PlayClip(_dismantleAudio, BodyPoint.Torso);
            yield return new WaitForTime(duration);
            DismantleItem(character, parentSlot, stack);
        }

        private void DismantleItem(ICharacter character, in SlotReference parentSlot, ItemStack stack)
        {
            // Retrieve crafting data for the item.
            var item = stack.Item;
            var craftData = item.Definition.GetDataOfType<CraftingData>();

            // Calculate dismantle efficiency based on item durability.
            float durabilityFactor = item.GetProperty(_durabilityProperty)?.Float / 100f ?? 1f;
            float dismantleEfficiency = craftData.DismantleEfficiency * durabilityFactor;

            // Decrease the stack count of the item.
            parentSlot.AdjustStack(-1);

            // Dispatch a message about dismantling the item.
            MessageDispatcher.Instance.Dispatch(character, MsgType.Error, $"Dismantled {item.Name}", item.Definition.Icon);
            
            // Add blueprint items to the character's inventory.
            foreach (var blueprintItem in craftData.Blueprint)
            {
                // Calculate the amount of each blueprint item to add based on dismantle efficiency.
                int amountToAdd = Mathf.CeilToInt(blueprintItem.Amount * dismantleEfficiency);
        
                // Attempt to add the item to the inventory.
                int addedCount = character.Inventory.AddItemsById(blueprintItem.Item, amountToAdd).addedCount;

                // If not all items could be added to the inventory, perform drop action.
                if (addedCount < amountToAdd)
                    character.Inventory.DropItem(new ItemStack(new Item(blueprintItem.Item.Def), amountToAdd - addedCount));
                else
                {
                    // Dispatch a message about adding the item to the inventory.
                    string msg = addedCount > 1 ? $"Added {blueprintItem.Item.Name} x {addedCount}" : $"Added {blueprintItem.Item.Name}";
                    MessageDispatcher.Instance.Dispatch(character, MsgType.Info, msg, blueprintItem.Item.Def.Icon);
                }
            }
        }
    }
}