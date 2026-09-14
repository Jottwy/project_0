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
    /// desgarro) o poner cinta. Condiciones de prototipo: solo <c>BR_InventoryTest</c>. Con backend (ADR-149 R2b) la
    /// protección la aplica el servidor: aquí se le manda por zona (<c>report_protection</c>) y la prenda se rompe con cada
    /// <c>body_hit</c>, también con los golpes que no pasaron por este cliente.
    /// </summary>
    public sealed class BackroomsGarmentPrototype : MonoBehaviour
    {
        // De fuera adentro: la primera prenda que cubre la zona es la que para el golpe.
        [SerializeField]
        private string[] _layers = { "Outer", "Torso", "Legs", "Feet", "GloveL", "GloveR", "Head", "Face" };

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
        private readonly byte[] _protection = new byte[BodyZones.Count];
        private readonly byte[] _sentProtection = new byte[BodyZones.Count];
        private Player _player;
        private Inventory _inventory;
        private IPCClient _ipc;
        private bool _protectionSent;
        private float _nextProtectionCheck;
        private bool _moving;

        private void Awake() => Instance = this;

        private void OnDestroy()
        {
            Unbind();
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (_ipc == null && IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected)
            {
                _ipc = ipc;
                _ipc.AddEventListener(OnGameEvent);
                _protectionSent = false;
                Debug.Log("[Ropa] backend conectado: protege el servidor y la ropa se rompe con body_hit (ADR-149 R2b).");
            }
            if (_inventory == null && !TryBind()) return;
            if (_ipc != null) SyncProtection();
        }

        private bool TryBind()
        {
            var players = Player.AllPlayers;
            var inventory = players.Count > 0 ? players[0].Inventory as Inventory : null;
            if (inventory == null || inventory.Containers == null || inventory.Containers.Count == 0) return false;
            _player = players[0];
            _inventory = inventory;
            _inventory.SlotChanged += OnSlotChanged;
            foreach (var owner in _pocketOwners) _worn[owner] = WornIn(owner);
            return true;
        }

        private void Unbind()
        {
            if (_inventory != null) _inventory.SlotChanged -= OnSlotChanged;
            if (_ipc != null) _ipc.RemoveEventListener(OnGameEvent);
            _ipc = null;
            _inventory = null;
            _player = null;
            _worn.Clear();
        }

        /// <summary>Cada medio segundo, la protección por zona; si cambió (o nunca se mandó), al backend.</summary>
        private void SyncProtection()
        {
            if (Time.unscaledTime < _nextProtectionCheck) return;
            _nextProtectionCheck = Time.unscaledTime + 0.5f;
            for (int z = 0; z < BodyZones.Count; z++)
            {
                _protection[z] = 0;
                if (!TryGetOutermost((BodyZone)z, out _, out var worn, out var data, out int index)) continue;
                _protection[z] = ToPercent(GarmentState.Of(worn).Protection(index, data.Zones[index]));
            }
            if (_protectionSent && System.Linq.Enumerable.SequenceEqual(_protection, _sentProtection)) return;
            _ipc.SendReportProtection(_protection);
            System.Array.Copy(_protection, _sentProtection, _protection.Length);
            _protectionSent = true;
        }

        public static byte ToPercent(float protection) => (byte)Mathf.Clamp(Mathf.RoundToInt(protection * 100f), 0, 100);

        private void OnGameEvent(GameEventMsg ev)
        {
            if (_inventory == null || !BodyStateMirror.TryReadHit(ev, out var zone, out string cause, out float damage)) return;
            var type = Enum.TryParse(cause, out DamageType parsed) ? parsed : DamageType.Undefined;
            Absorb(zone, damage, type);
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
            {
                Debug.Log($"[Ropa] {worn.Name}, {BodyZones.Label(zone)}: {GarmentState.Describe(state.DamageOf(index), state.IsPocketBroken(index))}");
                InventoryReporter.MarkDirty();
            }
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

            var (needles, threads, cloths, tapes) = Materials();
            if (GarmentRepairPlan.CanSew(state, index, needles, threads, cloths, out string sewMissing)) return Repair(worn, index, GarmentRepair.Sew);
            if (GarmentRepairPlan.CanTape(state, index, tapes, out _)) return Repair(worn, index, GarmentRepair.Tape);
            return state.CanRepair(index, GarmentRepair.Tape) ? $"Falta {sewMissing}, o cinta" : $"Para coser falta: {sewMissing}";
        }

        /// <summary>Sastrería: cose o pone cinta en una zona concreta de una prenda puesta. Devuelve el aviso para la UI.</summary>
        public string Repair(Item worn, int index, GarmentRepair repair)
        {
            if (_inventory == null) return "Sin jugador";
            if (worn == null || !worn.Definition.TryGetDataOfType(out GarmentZonesData data) || index < 0 || index >= data.Zones.Count)
                return "Esa prenda ya no está";
            var state = GarmentState.Of(worn);
            string where = $"{worn.Name}, {BodyZones.Label(data.Zones[index].Zone)}";
            var (needles, threads, cloths, tapes) = Materials();
            if (repair == GarmentRepair.Sew)
            {
                if (!GarmentRepairPlan.CanSew(state, index, needles, threads, cloths, out string missing))
                    return missing.Length == 0 ? $"{where}: nada que coser" : $"Para coser falta: {missing}";
                bool needsCloth = state.NeedsCloth(index);
                _inventory.RemoveItemsById(_thread.Id, 1);
                if (needsCloth) _inventory.RemoveItemsById(_cloth.Id, 1);
                state.Repair(index, GarmentRepair.Sew);
                InventoryReporter.MarkDirty();
                return $"Cosido: {where}";
            }
            if (!GarmentRepairPlan.CanTape(state, index, tapes, out string noTape))
                return noTape.Length == 0 ? $"{where}: no admite cinta" : "Falta cinta";
            _inventory.RemoveItemsById(_tape.Id, 1);
            state.Repair(index, GarmentRepair.Tape);
            InventoryReporter.MarkDirty();
            return state.IsPocketBroken(index) ? $"Cinta en {where}: el bolsillo sigue roto" : $"Cinta en {where}";
        }

        /// <summary>Lo que hay en el inventario para coser: agujas, hilo, tela y cinta.</summary>
        public (int needles, int threads, int cloths, int tapes) Materials()
            => _inventory == null ? (0, 0, 0, 0) : (Count(_needle), Count(_thread), Count(_cloth), Count(_tape));

        /// <summary>Sastrería: cada zona de cada prenda por zonas puesta, de fuera adentro y en el orden de sus zonas.</summary>
        public void CollectWornZones(List<WornGarmentZone> zones)
        {
            zones.Clear();
            if (_inventory == null) return;
            foreach (var name in _layers)
            {
                var worn = WornIn(name);
                if (worn == null || !worn.Definition.TryGetDataOfType(out GarmentZonesData data)) continue;
                for (int i = 0; i < data.Zones.Count && i < GarmentZonesData.MaxZones; i++)
                    if (Covers(name, data.Zones[i].Zone)) zones.Add(new WornGarmentZone(worn, data, i));
            }
        }

        private bool TryGetOutermost(BodyZone zone, out string layer, out Item worn, out GarmentZonesData data, out int index)
        {
            foreach (var name in _layers)
            {
                worn = WornIn(name);
                data = null;
                if (worn == null || !worn.Definition.TryGetDataOfType(out data)) continue;
                index = data.IndexOf(zone);
                if (index < 0 || !Covers(name, zone)) continue;
                layer = name;
                return true;
            }
            layer = null;
            worn = null;
            data = null;
            index = -1;
            return false;
        }

        /// <summary>Un guante vale para cualquier mano; el hueco donde va decide cuál cubre.</summary>
        public static bool Covers(string layer, BodyZone zone) => layer switch
        {
            "GloveL" => zone == BodyZone.HandL,
            "GloveR" => zone == BodyZone.HandR,
            _ => true,
        };

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
