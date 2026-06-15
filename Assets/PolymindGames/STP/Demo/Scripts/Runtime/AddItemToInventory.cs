using PolymindGames.InventorySystem;
using UnityEngine;

namespace PolymindGames.Demo
{
    public sealed class AddItemToInventory : MonoBehaviour
    {
        [SerializeField]
        private AudioData _addAudio = new(null);
        
        public void AddItemToCharacter(ICharacter character)
        {
            var result = ItemPickupUtility.PickUpItem(character.Inventory, CreateItem(), out var messageArgs);

            if (result != ItemPickupAddResult.Failed)
            {
                character.Audio.PlayClip(_addAudio, BodyPoint.Torso);
            }

            if (ReferenceEquals(character, GameMode.Instance.LocalPlayer))
            {
                MessageDispatcher.Instance.Dispatch(character, messageArgs);
            }
        }

        public void AddItemToCollider(Collider col)
        {
            if (col.TryGetComponent(out ICharacter character))
            {
                AddItemToCharacter(character);
            }
        }

        private ItemStack CreateItem()
        {
            var itemDef = ItemDefinition.Definitions.SelectRandom();
            int count = Random.Range(1, itemDef.StackSize + 1);
            return new ItemStack(new Item(itemDef), count);
        }
    }
}