using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Wearables
{
    /// <summary>
    /// Capa un contenedor precreado según lo que lleves puesto en su contenedor dueño (ADR-147, enfoque A):
    /// sin prenda, 0 huecos; con prenda, sus huecos y sus kg (D6 enmendado: el máximo de la prenda IMPIDE
    /// guardar). Una prenda con capacidad nunca entra aquí: profundidad 1 y, de paso, no se puede meter la
    /// mochila en su propio almacén. Es una restricción del vendor heredada fuera de su ensamblado, así que
    /// el arrastre a un hueco concreto tampoco se la salta (<c>SetItemAtIndex</c> pasa por
    /// <c>GetAllowedCount</c>).
    /// </summary>
    public sealed class WornCapacityRestriction : ContainerRestriction
    {
        [SerializeField]
        private string _ownerContainer = "Back";

        // D14: huecos que hay aunque no lleves nada puesto (las 2 manos de la barra).
        [SerializeField, Range(0, 9)]
        private int _baseSlots;

        // La barra no tiene tope de kg propio: lo pone el total del inventario.
        [SerializeField]
        private bool _limitWeight = true;

        // Intercambio de dos huecos ya ocupados en curso (BackroomsSlotFeedback): el objeto que sale libera su hueco.
        private static int s_swapDepth;

        public string OwnerContainer => _ownerContainer;

        public static void BeginSwap() => s_swapDepth++;

        public static void EndSwap() => s_swapDepth = Mathf.Max(0, s_swapDepth - 1);

        /// <summary>Huecos que cuentan como usados: en un intercambio, uno menos (el que sale deja el suyo).</summary>
        public static int EffectiveUsedSlots(int usedSlots, bool swapping) => swapping && usedSlots > 0 ? usedSlots - 1 : usedSlots;

        public static WornCapacityRestriction Create(string ownerContainer)
        {
            var restriction = CreateInstance<WornCapacityRestriction>();
            restriction._ownerContainer = ownerContainer;
            return restriction;
        }

        public override int GetAllowedCount(IItemContainer container, Item item, int requestedCount)
        {
            if (item == null) return 0;
            var worn = WornItem(container);
            WearableCapacityData capacity = null;
            worn?.Definition.TryGetDataOfType(out capacity);
            bool stacksInto = item.IsStackable && container.ContainsItemById(item.Id);
            var (allowed, reason) = Evaluate(capacity, worn != null ? worn.Name : string.Empty, EffectiveUsedSlots(UsedSlots(container), s_swapDepth > 0),
                container.Weight, item.Weight, stacksInto, item.Definition.TryGetDataOfType<WearableCapacityData>(out _),
                requestedCount, _baseSlots, _limitWeight);
            if (allowed <= 0) RejectionReason = reason;
            return allowed;
        }

        /// <summary>El objeto puesto en el contenedor dueño, o null.</summary>
        public Item WornItem(IItemContainer container)
        {
            var owner = container?.Inventory?.FindContainer(ItemContainerFilters.WithName(_ownerContainer));
            if (owner == null || owner.SlotsCount == 0) return null;
            var stack = owner.GetItemAtIndex(0);
            return stack.HasItem() ? stack.Item : null;
        }

        /// <summary>
        /// La regla entera, pura: cuántos caben y, si ninguno, por qué. Separada del vendor para poder testearla
        /// sin inventario vivo.
        /// </summary>
        public static (int allowed, string reason) Evaluate(WearableCapacityData capacity, string wornName, int usedSlots,
            float currentKg, float unitKg, bool stacksInto, bool itemIsWearable, int requested)
            => Evaluate(capacity, wornName, usedSlots, currentKg, unitKg, stacksInto, itemIsWearable, requested, 0, true);

        public static (int allowed, string reason) Evaluate(WearableCapacityData capacity, string wornName, int usedSlots,
            float currentKg, float unitKg, bool stacksInto, bool itemIsWearable, int requested, int baseSlots, bool limitWeight)
        {
            if (requested <= 0) return (0, string.Empty);
            if (itemIsWearable) return (0, "Una mochila no cabe dentro de otra");
            int slots = baseSlots + (capacity?.Slots ?? 0);
            if (slots <= 0) return (0, "Sin mochila puesta");
            if (!stacksInto && usedSlots >= slots)
                return (0, capacity != null ? $"{wornName} está llena" : "No hay hueco libre");
            if (!limitWeight || capacity == null) return (requested, string.Empty);

            const float epsilon = 0.0001f;
            float free = capacity.MaxKg - currentKg;
            int byWeight = Mathf.FloorToInt((free + epsilon) / Mathf.Max(unitKg, epsilon));
            if (byWeight <= 0) return (0, $"{wornName} no aguanta más de {capacity.MaxKg:0.#} kg");
            return (Mathf.Min(requested, byWeight), string.Empty);
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
