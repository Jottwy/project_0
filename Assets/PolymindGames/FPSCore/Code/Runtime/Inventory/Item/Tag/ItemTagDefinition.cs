using UnityEngine;

namespace PolymindGames.InventorySystem
{
    [CreateAssetMenu(menuName = "Polymind Games/Items/Item Tag", fileName = "Tag_")]
    public sealed class ItemTagDefinition : DataDefinition<ItemTagDefinition>
    {
        public const string Untagged = "Untagged";
    }
}