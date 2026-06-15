using PolymindGames.InventorySystem;

namespace PolymindGames
{
    /// <summary>
    /// You can extend this by creating another partial class with the same name.
    /// </summary>
    public static partial class ItemConstants
    {
        public static readonly DataIdReference<ItemPropertyDefinition> Durability = ItemPropertyDefinition.GetWithName("Durability").Id;
        public static readonly DataIdReference<ItemPropertyDefinition> AmmoInMagazine = ItemPropertyDefinition.GetWithName("Ammo In Magazine").Id;
        public static readonly DataIdReference<ItemPropertyDefinition> FirearmMode = ItemPropertyDefinition.GetWithName("Firearm Mode").Id;
        
        public static readonly DataIdReference<ItemTagDefinition> WieldableTag = ItemTagDefinition.GetWithName("Wieldable")?.Id ?? 0;
        public static readonly DataIdReference<ItemTagDefinition> HeadEquipmentTag = ItemTagDefinition.GetWithName("Head Equipment")?.Id ?? 0;
        public static readonly DataIdReference<ItemTagDefinition> TorsoEquipmentTag = ItemTagDefinition.GetWithName("Torso Equipment")?.Id ?? 0;
        public static readonly DataIdReference<ItemTagDefinition> LegsEquipmentTag = ItemTagDefinition.GetWithName("Legs Equipment")?.Id ?? 0;
        public static readonly DataIdReference<ItemTagDefinition> FeetEquipmentTag = ItemTagDefinition.GetWithName("Feet Equipment")?.Id ?? 0;
        public static readonly DataIdReference<ItemTagDefinition> SightAttachmentTag = ItemTagDefinition.GetWithName("Sight Attachment")?.Id ?? 0;
    }
}