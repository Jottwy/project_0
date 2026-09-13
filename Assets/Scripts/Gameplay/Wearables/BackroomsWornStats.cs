using System.Collections.Generic;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Wearables
{
    /// <summary>
    /// PROTOTIPO: suma los <see cref="WearableStatData"/> de lo que llevas PUESTO y lo aplica. De momento la velocidad,
    /// por el mismo <c>SpeedModifier</c> del vendor que el frenado por carga (<see cref="BackroomsCarrySpeed"/>): los dos
    /// se multiplican. La suma se recorta a un tope para que apilar prendas no dispare nada.
    ///
    /// Solo cuentan los contenedores de equipo; una prenda guardada en la mochila no da nada. Límites declarados, como
    /// el resto del prototipo: solo <c>BR_InventoryTest</c>, apagado con backend conectado.
    /// </summary>
    public sealed class BackroomsWornStats : MonoBehaviour
    {
        [SerializeField]
        private string[] _wornContainers = { "Head", "Torso", "Legs", "Feet", "Back", "Waist", "Outer", "Gloves", "Face" };

        [SerializeField, Range(-50f, 0f)]
        private float _minSpeedPct = -30f;

        [SerializeField, Range(0f, 50f)]
        private float _maxSpeedPct = 20f;

        private readonly List<WearableStatData> _worn = new();
        private Inventory _inventory;
        private IMovementControllerCC _movement;
        private float _speed = 1f;
        private bool _inert;

        public float Speed => _speed;

        private void Update()
        {
            if (_inert) return;
            if (IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected)
            {
                _inert = true;
                Unbind();
                Debug.LogWarning("[Ropa] backend conectado: los modificadores por prenda se apagan (prototipo).");
                return;
            }
            if (_movement != null) return;

            var players = Player.AllPlayers;
            if (players.Count == 0) return;
            var player = players[0];
            var inventory = player.Inventory as Inventory;
            if (inventory == null || inventory.Containers == null || inventory.Containers.Count == 0) return;
            if (!player.TryGetCC(out IMovementControllerCC movement)) return;

            _inventory = inventory;
            _movement = movement;
            _inventory.SlotChanged += OnSlotChanged;
            _movement.SpeedModifier.AddModifier(GetSpeed);
            Apply();
        }

        private void OnDestroy() => Unbind();

        private void Unbind()
        {
            if (_inventory != null) _inventory.SlotChanged -= OnSlotChanged;
            if (_movement != null) _movement.SpeedModifier.RemoveModifier(GetSpeed);
            _inventory = null;
            _movement = null;
            _speed = 1f;
        }

        private float GetSpeed() => _speed;

        private void OnSlotChanged(in SlotReference slot, SlotChangeType changeType)
        {
            if (changeType != SlotChangeType.CountChanged && slot.Container != null && IsWorn(slot.Container.Name)) Apply();
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
                    if (stack.HasItem() && stack.Item.Definition.TryGetDataOfType(out WearableStatData stats))
                        _worn.Add(stats);
                }
            }
            float speed = SpeedMultiplier(_worn, _minSpeedPct, _maxSpeedPct);
            if (Mathf.Approximately(speed, _speed)) return;
            _speed = speed;
            Debug.Log($"[Ropa] velocidad por prendas x{_speed:0.00}");
        }

        /// <summary>La regla, pura: suma de porcentajes de lo puesto, recortada a [min, max], como multiplicador.</summary>
        public static float SpeedMultiplier(IReadOnlyList<WearableStatData> worn, float minPct, float maxPct)
        {
            float pct = 0f;
            if (worn != null)
                foreach (var stats in worn)
                    if (stats != null) pct += stats.SpeedPct;
            return 1f + Mathf.Clamp(pct, minPct, maxPct) / 100f;
        }
    }
}
