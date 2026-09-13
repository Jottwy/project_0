using BackroomsSurvival.Wearables;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Body
{
    /// <summary>
    /// ADR-149 enm. 1: capa un contenedor de bolsillos a los huecos SANOS de la prenda puesta en su contenedor dueño. Sin
    /// prenda o sin bolsillos, nada; con bolsillos rotos, menos huecos. Una prenda con capacidad o con zonas no entra en un
    /// bolsillo. Qué hueco concreto está tachado lo resuelve <see cref="BackroomsGarmentPrototype"/>: una restricción del
    /// vendor no sabe de índices.
    /// </summary>
    public sealed class GarmentPocketRestriction : ContainerRestriction
    {
        [SerializeField]
        private string _ownerContainer = "Outer";

        public string OwnerContainer => _ownerContainer;

        public static GarmentPocketRestriction Create(string ownerContainer)
        {
            var restriction = CreateInstance<GarmentPocketRestriction>();
            restriction._ownerContainer = ownerContainer;
            return restriction;
        }

        public override int GetAllowedCount(IItemContainer container, Item item, int requestedCount)
        {
            if (item == null) return 0;
            var owner = container?.Inventory?.FindContainer(ItemContainerFilters.WithName(_ownerContainer));
            var worn = owner != null && owner.SlotsCount > 0 && owner.GetItemAtIndex(0).HasItem() ? owner.GetItemAtIndex(0).Item : null;
            GarmentZonesData zones = null;
            worn?.Definition.TryGetDataOfType(out zones);
            int broken = zones != null ? GarmentState.Of(worn).BrokenPocketSlots(zones) : 0;
            bool isGarment = item.Definition.TryGetDataOfType<GarmentZonesData>(out _) || item.Definition.TryGetDataOfType<WearableCapacityData>(out _);
            bool stacksInto = item.IsStackable && container.ContainsItemById(item.Id);
            int used = WornCapacityRestriction.EffectiveUsedSlots(UsedSlots(container), WornCapacityRestriction.IsSwapping);
            var (allowed, reason) = Evaluate(zones?.PocketSlots ?? 0, broken, used, stacksInto, isGarment, requestedCount, worn?.Name ?? string.Empty);
            if (allowed <= 0) RejectionReason = reason;
            return allowed;
        }

        /// <summary>La regla, pura.</summary>
        public static (int allowed, string reason) Evaluate(int pocketSlots, int brokenSlots, int usedSlots, bool stacksInto,
            bool itemIsGarment, int requested, string wornName)
        {
            if (requested <= 0) return (0, string.Empty);
            if (itemIsGarment) return (0, "Una prenda no cabe en un bolsillo");
            if (pocketSlots <= 0) return (0, "Sin bolsillos");
            int intact = pocketSlots - brokenSlots;
            if (intact <= 0) return (0, "Bolsillos rotos: cóselos");
            if (!stacksInto && usedSlots >= intact)
                return (0, brokenSlots > 0 ? $"Solo quedan {intact} huecos sanos" : $"{wornName}: bolsillos llenos");
            return (requested, string.Empty);
        }

        private static int UsedSlots(IItemContainer container)
        {
            int used = 0;
            for (int i = 0; i < container.SlotsCount; i++)
                if (container.GetItemAtIndex(i).HasItem()) used++;
            return used;
        }
    }
}
