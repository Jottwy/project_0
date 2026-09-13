using System.Collections.Generic;
using System.Reflection;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using UnityEngine;

namespace BackroomsSurvival.Wearables
{
    /// <summary>
    /// PROTOTIPO (INVENTORY-ROADMAP D6 enm. 2): el peso máximo del inventario sale del equipo, una base pequeña del
    /// cuerpo más el <see cref="WearableCapacityData.MaxKg"/> de lo que llevas puesto en los contenedores de equipo. Al
    /// llegar al máximo no deja coger más: eso ya lo hace el vendor con <c>Inventory.MaxWeight</c>.
    ///
    /// El vendor guarda el máximo en un campo privado sin setter; se escribe por reflexión (no se edita el vendor).
    /// Límites declarados, como <see cref="BackroomsWornStorage"/>: solo lo monta <c>BR_InventoryTest</c> y se apaga
    /// con backend conectado. Lo que ya llevas no se suelta si el máximo baja por debajo del peso actual: solo
    /// impide coger más.
    /// </summary>
    public sealed class BackroomsCarryWeight : MonoBehaviour
    {
        private static readonly FieldInfo MaxWeightField =
            typeof(Inventory).GetField("_maxWeight", BindingFlags.NonPublic | BindingFlags.Instance);

        [SerializeField, Range(0f, 100f)]
        private float _baseKg = 10f;

        // Solo lo PUESTO: la base y la barra pueden llevar una mochila guardada, y esa no suma.
        [SerializeField]
        private string[] _wornContainers = { "Back", "Waist" };

        private readonly List<WearableCapacityData> _worn = new();
        private Inventory _inventory;
        private bool _inert;

        private void Update()
        {
            if (_inert) return;
            if (IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected)
            {
                _inert = true;
                Unbind();
                Debug.LogWarning("[Carga] backend conectado: el máximo por equipo se apaga (prototipo).");
                return;
            }
            if (_inventory != null) return;

            var players = Player.AllPlayers;
            var inventory = players.Count > 0 ? players[0].Inventory as Inventory : null;
            if (inventory == null || inventory.Containers == null || inventory.Containers.Count == 0) return;
            if (MaxWeightField == null)
            {
                _inert = true;
                Debug.LogError("[Carga] Inventory._maxWeight ya no existe en el vendor: prototipo apagado.");
                return;
            }

            _inventory = inventory;
            _inventory.SlotChanged += OnSlotChanged;
            Apply();
            // Las barras de carga del vendor solo se refrescan con Changed; re-atarlas enseña ya el máximo nuevo.
            foreach (var display in FindObjectsByType<InventoryWeightDisplayUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                display.AttachToInventory(_inventory);
        }

        private void OnDestroy() => Unbind();

        private void Unbind()
        {
            if (_inventory != null) _inventory.SlotChanged -= OnSlotChanged;
            _inventory = null;
        }

        // SlotChanged llega antes que Changed: la barra del vendor ya lee el máximo nuevo al refrescarse.
        private void OnSlotChanged(in SlotReference slot, SlotChangeType changeType)
        {
            if (slot.Container != null && IsWorn(slot.Container.Name)) Apply();
        }

        private bool IsWorn(string containerName)
        {
            foreach (var name in _wornContainers)
                if (name == containerName) return true;
            return false;
        }

        private void Apply()
        {
            _worn.Clear();
            foreach (var name in _wornContainers)
            {
                var container = _inventory.FindContainer(ItemContainerFilters.WithName(name));
                if (container == null) continue;
                for (int i = 0; i < container.SlotsCount; i++)
                {
                    var stack = container.GetItemAtIndex(i);
                    if (stack.HasItem() && stack.Item.Definition.TryGetDataOfType(out WearableCapacityData capacity))
                        _worn.Add(capacity);
                }
            }
            MaxWeightField.SetValue(_inventory, MaxWeight(_baseKg, _worn));
        }

        /// <summary>La regla, pura: base del cuerpo más lo que suma cada prenda puesta.</summary>
        public static float MaxWeight(float baseKg, IReadOnlyList<WearableCapacityData> worn)
        {
            float max = baseKg;
            if (worn != null)
                foreach (var capacity in worn)
                    if (capacity != null) max += capacity.MaxKg;
            return max;
        }
    }
}
