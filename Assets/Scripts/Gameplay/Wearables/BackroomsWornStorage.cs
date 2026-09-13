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
    /// </summary>
    public sealed class BackroomsWornStorage : MonoBehaviour
    {
        private const string BackName = "Back";
        private const string StorageName = "BackStorage";

        private readonly ConditionalWeakTable<Item, List<(int slot, ItemStack stack)>> _packed = new();
        private IItemContainer _back;
        private IItemContainer _storage;
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
        }

        private void OnDestroy() => Unbind();

        private void Unbind()
        {
            if (_back != null) _back.SlotChanged -= OnBackChanged;
            _back = null;
            _storage = null;
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
