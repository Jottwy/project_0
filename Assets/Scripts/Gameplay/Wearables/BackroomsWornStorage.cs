using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Wearables
{
    /// <summary>
    /// PROTOTIPO (ADR-147 enm. 1): al quitarte la mochila, su almacén se EMPAQUETA en la instancia del objeto y se
    /// vacía; al ponértela, se desempaqueta en los mismos huecos (D5: lo de dentro se va con la prenda). Mover las
    /// mismas instancias <see cref="Item"/> y vaciar en la misma llamada evita copias.
    ///
    /// Límites declarados: solo lo monta <c>BR_InventoryTest</c> (condición 1); el paquete vive en memoria (no
    /// sobrevive a salir de Play ni viaja por red: punto 4 del ADR sin aprobar); un paquete no suma kg (hueco de peso
    /// de la enm. 1); y se apaga en cuanto hay backend conectado (condición 2), para no reportar nunca contenedores 6+.
    ///
    /// El cinturón NO empaqueta: sus huecos son de la barra. Al quitártelo (o cambiarlo por uno menor), si queda algo en
    /// los huecos que desaparecen, la barra se COMPACTA de izquierda a derecha conservando el orden (Joel, 2026-09-13);
    /// lo que ya no quepa va a la base empezando por la derecha y, si tampoco cabe, cae al suelo (D14 enm. 1).
    /// </summary>
    public sealed class BackroomsWornStorage : MonoBehaviour
    {
        private const string BackName = "Back";
        private const string StorageName = "BackStorage";
        private const string WaistName = "Waist";
        private const string HandsName = "Holster";
        private const string BaseName = "Backpack";
        private const int HandSlots = 2;

        private readonly ConditionalWeakTable<Item, List<(int slot, ItemStack stack)>> _packed = new();
        private IItemContainer _back;
        private IItemContainer _storage;
        private IInventory _inventory;
        private IItemContainer _waist;
        private IItemContainer _hands;
        private IItemContainer _base;
        private readonly List<int> _kept = new();
        private readonly List<int> _overflow = new();
        private Item _worn;
        private bool _inert;

        private void Update()
        {
            if (_inert) return;
            if (IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected)
            {
                _inert = true;
                Unbind();
                Debug.LogWarning("[Mochilas] backend conectado: el prototipo se apaga (ADR-147 enm. 1, condición 2).");
                return;
            }
            if (_back != null) return;

            var players = Player.AllPlayers;
            var inventory = players.Count > 0 ? players[0].Inventory : null;
            if (inventory?.Containers == null || inventory.Containers.Count == 0) return;

            _back = inventory.FindContainer(ItemContainerFilters.WithName(BackName));
            _storage = inventory.FindContainer(ItemContainerFilters.WithName(StorageName));
            if (_back == null || _storage == null)
            {
                _inert = true;
                _back = null;
                Debug.LogWarning("[Mochilas] el jugador no tiene contenedores Back/BackStorage: prototipo apagado.");
                return;
            }
            _worn = Current();
            _back.SlotChanged += OnBackChanged;

            _inventory = inventory;
            _waist = inventory.FindContainer(ItemContainerFilters.WithName(WaistName));
            _hands = inventory.FindContainer(ItemContainerFilters.WithName(HandsName));
            _base = inventory.FindContainer(ItemContainerFilters.WithName(BaseName));
            if (_waist != null && _hands != null) _waist.SlotChanged += OnWaistChanged;
        }

        private void OnDestroy() => Unbind();

        private void Unbind()
        {
            if (_back != null) _back.SlotChanged -= OnBackChanged;
            if (_waist != null) _waist.SlotChanged -= OnWaistChanged;
            _back = null;
            _storage = null;
            _waist = null;
            _hands = null;
            _base = null;
            _inventory = null;
        }

        private Item Current()
        {
            var stack = _back.GetItemAtIndex(0);
            return stack.HasItem() ? stack.Item : null;
        }

        private void OnBackChanged(in SlotReference slot, SlotChangeType changeType)
        {
            var now = Current();
            if (now == _worn) return;
            if (_worn != null) Pack(_worn);
            _worn = now;
            if (_worn != null) Unpack(_worn);
        }

        private void OnWaistChanged(in SlotReference slot, SlotChangeType changeType)
        {
            WearableCapacityData belt = null;
            var worn = _waist.SlotsCount > 0 ? _waist.GetItemAtIndex(0) : ItemStack.Null;
            if (worn.HasItem()) worn.Item.Definition.TryGetDataOfType(out belt);

            int count = _hands.SlotsCount;
            int visible = VisibleHandSlots(HandSlots, belt, count);
            var stacks = new ItemStack[count];
            var occupied = new bool[count];
            for (int i = 0; i < count; i++)
            {
                stacks[i] = _hands.GetItemAtIndex(i);
                occupied[i] = stacks[i].HasItem();
            }
            if (!Compact(occupied, visible, _kept, _overflow)) return;

            // Primero se saca todo lo que se mueve: si no, el vendor lo cuenta dos veces contra el peso máximo.
            for (int k = 0; k < _kept.Count; k++)
                if (_kept[k] != k) _hands.SetItemAtIndex(_kept[k], ItemStack.Null);
            foreach (int source in _overflow)
                _hands.SetItemAtIndex(source, ItemStack.Null);

            for (int k = 0; k < _kept.Count; k++)
            {
                if (_kept[k] == k) continue;
                var stack = stacks[_kept[k]];
                int placed = _hands.SetItemAtIndex(k, stack);
                if (placed < stack.Count) Stow(new ItemStack(stack.Item, stack.Count - placed));
            }
            foreach (int source in _overflow)
                Stow(stacks[source]);
        }

        /// <summary>A la base; lo que no quepa, al suelo.</summary>
        private void Stow(ItemStack stack)
        {
            int added = _base != null ? _base.AddItem(stack).addedCount : 0;
            if (added < stack.Count)
                _inventory.DropItem(added == 0 ? stack : new ItemStack(stack.Item, stack.Count - added));
        }

        /// <summary>
        /// La regla de la barra al encoger, pura: si hay algo en un hueco que ya no existe (índice ≥ <paramref name="visible"/>),
        /// <paramref name="kept"/> recibe los índices de origen que se quedan, en orden, para ocupar 0, 1, 2…; y
        /// <paramref name="overflow"/> los que sobran, de DERECHA a izquierda. Si todo cabe donde está, no se toca nada.
        /// </summary>
        public static bool Compact(bool[] occupied, int visible, List<int> kept, List<int> overflow)
        {
            kept.Clear();
            overflow.Clear();
            bool hidden = false;
            for (int i = Mathf.Max(0, visible); i < occupied.Length; i++)
                if (occupied[i]) { hidden = true; break; }
            if (!hidden) return false;

            for (int i = 0; i < occupied.Length; i++)
            {
                if (!occupied[i]) continue;
                if (kept.Count < visible) kept.Add(i);
                else overflow.Add(i);
            }
            overflow.Reverse();
            return true;
        }

        /// <summary>Huecos de la barra que existen: las manos más lo que dé el cinturón, sin pasar de los creados.</summary>
        public static int VisibleHandSlots(int handSlots, WearableCapacityData belt, int slotsCount)
            => Mathf.Clamp(handSlots + (belt?.Slots ?? 0), 0, slotsCount);

        private void Pack(Item backpack)
        {
            var contents = new List<(int slot, ItemStack stack)>();
            for (int i = 0; i < _storage.SlotsCount; i++)
            {
                var stack = _storage.GetItemAtIndex(i);
                if (stack.HasItem()) contents.Add((i, stack));
            }
            _storage.Clear();
            _packed.Remove(backpack);
            if (contents.Count > 0) _packed.Add(backpack, contents);
        }

        private void Unpack(Item backpack)
        {
            if (!_packed.TryGetValue(backpack, out var contents)) return;
            _packed.Remove(backpack);
            foreach (var (slot, stack) in contents)
            {
                int placed = _storage.SetItemAtIndex(slot, stack);
                if (placed < stack.Count)
                    Debug.LogError($"[Mochilas] {backpack.Name}: {stack} no volvió entero al hueco {slot} ({placed}/{stack.Count}).");
            }
        }
    }
}
