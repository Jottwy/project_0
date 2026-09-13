using System;
using System.Collections.Generic;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Body
{
    /// <summary>
    /// ADR-149 enm. 1, rebanada R1: la ropa por zonas en LOCAL. La prenda más exterior que cubre la zona golpeada protege y
    /// se rompe (<see cref="GarmentState"/>); si la zona tenía bolsillo, lo que llevaba pasa a otro hueco del inventario o cae
    /// al suelo con aviso. Quitarse una prenda con los bolsillos llenos vuelca igual. Coser (aguja e hilo, y tela si es un
    /// desgarro) o poner cinta. Condiciones de prototipo: solo <c>BR_InventoryTest</c>, inerte con backend.
    /// </summary>
    public sealed class BackroomsGarmentPrototype : MonoBehaviour
    {
        // De fuera adentro: la primera prenda que cubre la zona es la que para el golpe.
        [SerializeField]
        private string[] _layers = { "Outer", "Torso", "Legs", "Feet", "Gloves", "Head", "Face" };

        [SerializeField]
        private string[] _pocketOwners = { "Outer", "Legs" };

        [SerializeField]
        private string[] _pocketContainers = { "OuterPockets", "LegsPockets" };

        [SerializeField]
        private ItemDefinition _needle;

        [SerializeField]
        private ItemDefinition _thread;

        [SerializeField]
        private ItemDefinition _cloth;

        [SerializeField]
        private ItemDefinition _tape;

        public static BackroomsGarmentPrototype Instance { get; private set; }

        private readonly Dictionary<string, Item> _worn = new();
        private readonly List<ItemStack> _spilled = new();
        private Player _player;
        private Inventory _inventory;
        private bool _inert;
        private bool _moving;

        private void Awake() => Instance = this;

        private void OnDestroy()
        {
            Unbind();
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (_inert) return;
            if (IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected)
            {
                _inert = true;
                Unbind();
                Debug.LogWarning("[Ropa] backend conectado: la ropa por zonas se apaga (prototipo ADR-149 R1).");
                return;
            }
            if (_inventory != null) return;

            var players = Player.AllPlayers;
            var inventory = players.Count > 0 ? players[0].Inventory as Inventory : null;
            if (inventory == null || inventory.Containers == null || inventory.Containers.Count == 0) return;
            _player = players[0];
            _inventory = inventory;
            _inventory.SlotChanged += OnSlotChanged;
            foreach (var owner in _pocketOwners) _worn[owner] = WornIn(owner);
        }

        private void Unbind()
        {
            if (_inventory != null) _inventory.SlotChanged -= OnSlotChanged;
            _inventory = null;
            _player = null;
            _worn.Clear();
        }

        private IItemContainer Find(string name) => _inventory?.FindContainer(ItemContainerFilters.WithName(name));

        private Item WornIn(string containerName)
        {
            var container = Find(containerName);
            if (container == null || container.SlotsCount == 0) return null;
            var stack = container.GetItemAtIndex(0);
            return stack.HasItem() ? stack.Item : null;
        }

        private void OnSlotChanged(in SlotReference slot, SlotChangeType changeType)
        {
            if (_moving || slot.Container == null) return;
            string name = slot.Container.Name;

            int owner = Array.IndexOf(_pocketOwners, name);
            if (owner >= 0)
            {
                var now = WornIn(name);
                _worn.TryGetValue(name, out var before);
                if (now == before) return;
                _worn[name] = now;
                if (before != null) SpillAll(owner);
                return;
            }

            int pockets = Array.IndexOf(_pocketContainers, name);
            if (pockets >= 0) FixBlockedSlots(pockets);
        }

        /// <summary>
        /// Un golpe en <paramref name="zone"/>. Devuelve la protección (0-1) de la prenda más exterior que la cubre, medida
        /// ANTES de romperse, y la rompe.
        /// </summary>
        public float Absorb(BodyZone zone, float damage, DamageType type)
        {
            if (_inventory == null || !TryGetOutermost(zone, out string layer, out var worn, out var data, out int index)) return 0f;
            var state = GarmentState.Of(worn);
            var garmentZone = data.Zones[index];
            float protection = state.Protection(index, garmentZone);
            if (state.ApplyHit(index, garmentZone, type, damage, out bool pocketBroke))
                Debug.Log($"[Ropa] {worn.Name}, {BodyZones.Label(zone)}: {GarmentState.Describe(state.DamageOf(index), state.IsPocketBroken(index))}");
            if (pocketBroke) SpillZone(layer, data, index);
            return protection;
        }

        public string Describe(BodyZone zone)
        {
            if (_inventory == null || !TryGetOutermost(zone, out _, out var worn, out _, out int index)) return string.Empty;
            var state = GarmentState.Of(worn);
            string text = GarmentState.Describe(state.DamageOf(index), state.IsPocketBroken(index));
            return text.Length == 0 ? string.Empty : $"{worn.Name}: {text}";
        }

        /// <summary>Cose si hay aguja e hilo (y tela para un desgarro); si no, cinta. Devuelve el aviso para la UI.</summary>
        public string TryRepair(BodyZone zone)
        {
            if (_inventory == null) return "Sin jugador";
            if (!TryGetOutermost(zone, out _, out var worn, out _, out int index)) return $"{BodyZones.Label(zone)}: sin ropa";
            var state = GarmentState.Of(worn);
            if (!state.NeedsRepair(index)) return $"{worn.Name}: está bien";

            bool needle = Count(_needle) > 0, thread = Count(_thread) > 0;
            bool needsCloth = state.NeedsCloth(index), cloth = Count(_cloth) > 0;
            if (needle && thread && (!needsCloth || cloth))
            {
                _inventory.RemoveItemsById(_thread.Id, 1);
                if (needsCloth) _inventory.RemoveItemsById(_cloth.Id, 1);
                state.Repair(index, GarmentRepair.Sew);
                return $"Cosido: {worn.Name}, {BodyZones.Label(zone)}";
            }
            if (state.CanRepair(index, GarmentRepair.Tape) && Count(_tape) > 0)
            {
                _inventory.RemoveItemsById(_tape.Id, 1);
                state.Repair(index, GarmentRepair.Tape);
                return $"Cinta en {worn.Name}: el bolsillo sigue roto";
            }
            if (needle && thread) return "Falta una tela para coser el desgarro";
            return state.CanRepair(index, GarmentRepair.Tape) ? "Necesitas aguja e hilo, o cinta" : "Para coser: aguja e hilo";
        }

        private bool TryGetOutermost(BodyZone zone, out string layer, out Item worn, out GarmentZonesData data, out int index)
        {
            foreach (var name in _layers)
            {
                worn = WornIn(name);
                data = null;
                if (worn == null || !worn.Definition.TryGetDataOfType(out data)) continue;
                index = data.IndexOf(zone);
                if (index < 0) continue;
                layer = name;
                return true;
            }
            layer = null;
            worn = null;
            data = null;
            index = -1;
            return false;
        }

        private int Count(ItemDefinition definition)
        {
            if (definition == null) return 0;
            int count = 0;
            foreach (var container in _inventory.Containers)
                for (int i = 0; i < container.SlotsCount; i++)
                {
                    var stack = container.GetItemAtIndex(i);
                    if (stack.HasItem() && stack.Item.Id == definition.Id) count += stack.Count;
                }
            return count;
        }

        /// <summary>Lo que llevaba el bolsillo de la zona rota sale a otro hueco o al suelo.</summary>
        private void SpillZone(string layer, GarmentZonesData data, int zoneIndex)
        {
            int pockets = Array.IndexOf(_pocketOwners, layer);
            var container = pockets >= 0 ? Find(_pocketContainers[pockets]) : null;
            if (container == null) return;
            int start = data.PocketStart(zoneIndex);
            Take(container, start, start + data.Zones[zoneIndex].PocketSlots);
            foreach (var stack in _spilled) Stow(stack, true);
            FixBlockedSlots(pockets);
        }

        private void SpillAll(int pockets)
        {
            var container = Find(_pocketContainers[pockets]);
            if (container == null) return;
            Take(container, 0, container.SlotsCount);
            foreach (var stack in _spilled) Stow(stack, false);
        }

        private void Take(IItemContainer container, int from, int to)
        {
            _spilled.Clear();
            _moving = true;
            for (int i = Mathf.Max(0, from); i < to && i < container.SlotsCount; i++)
            {
                var stack = container.GetItemAtIndex(i);
                if (!stack.HasItem()) continue;
                container.SetItemAtIndex(i, ItemStack.Null);
                _spilled.Add(stack);
            }
            _moving = false;
        }

        /// <summary>Nada se queda en un hueco tachado (ni fuera de los bolsillos): se mueve a uno sano o sale.</summary>
        private void FixBlockedSlots(int pockets)
        {
            var container = Find(_pocketContainers[pockets]);
            var worn = WornIn(_pocketOwners[pockets]);
            if (container == null || worn == null || !worn.Definition.TryGetDataOfType(out GarmentZonesData data)) return;
            var state = GarmentState.Of(worn);
            for (int i = 0; i < container.SlotsCount; i++)
            {
                var stack = container.GetItemAtIndex(i);
                if (!stack.HasItem() || !IsBlocked(data, state, i)) continue;
                int free = -1;
                for (int j = 0; j < data.PocketSlots && j < container.SlotsCount; j++)
                    if (!IsBlocked(data, state, j) && !container.GetItemAtIndex(j).HasItem()) { free = j; break; }
                _moving = true;
                container.SetItemAtIndex(i, ItemStack.Null);
                int placed = free >= 0 ? container.SetItemAtIndex(free, stack) : 0;
                _moving = false;
                if (placed < stack.Count) Stow(placed == 0 ? stack : new ItemStack(stack.Item, stack.Count - placed), true);
            }
        }

        public static bool IsBlocked(GarmentZonesData data, GarmentState state, int slotIndex)
            => slotIndex >= data.PocketSlots || state.IsPocketSlotBroken(data, slotIndex);

        private void Stow(ItemStack stack, bool announceMove)
        {
            var (added, _) = _inventory.AddItem(stack);
            if (added >= stack.Count)
            {
                if (announceMove) Notify($"{stack.Item.Name} pasó a otro hueco");
                return;
            }
            _inventory.DropItem(added == 0 ? stack : new ItemStack(stack.Item, stack.Count - added));
            Notify($"Se cayó {stack.Item.Name} al suelo");
        }

        private void Notify(string message)
        {
            Debug.Log($"[Ropa] {message}");
            if (_player != null) MessageDispatcher.Instance.Dispatch(_player, MsgType.Warning, message);
        }
    }
}
