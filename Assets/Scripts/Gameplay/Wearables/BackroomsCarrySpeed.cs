using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Wearables
{
    /// <summary>
    /// PROTOTIPO (INVENTORY-ROADMAP D6 enm. 3, Joel 2026-09-13): la carga frena. Andar y correr se multiplican por una
    /// curva del peso contra el máximo que da el equipo (<see cref="BackroomsCarryWeight"/>): empieza desde el primer kg,
    /// apenas se nota al principio y aprieta pasada la mitad; en el máximo, la mitad de velocidad.
    ///
    /// Va por el <c>SpeedModifier</c> del controlador de movimiento del vendor, como la superficie o curarse: no se
    /// edita el vendor. El movimiento hoy es del cliente, así que no toca el wire. Límites declarados, como el resto del
    /// prototipo: solo lo monta <c>BR_InventoryTest</c> y se apaga con backend conectado.
    /// </summary>
    public sealed class BackroomsCarrySpeed : MonoBehaviour
    {
        [SerializeField, Range(0.1f, 1f)]
        private float _speedAtMax = 0.5f;

        // 1 = recta; 2 = suave al principio y más fuerte pasada la mitad (lo pedido).
        [SerializeField, Range(1f, 4f)]
        private float _curvePower = 2f;

        private IInventory _inventory;
        private IMovementControllerCC _movement;
        private bool _inert;

        private void Update()
        {
            if (_inert) return;
            if (IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected)
            {
                _inert = true;
                Unbind();
                Debug.LogWarning("[Carga] backend conectado: el frenado por carga se apaga (prototipo).");
                return;
            }
            if (_movement != null) return;

            var players = Player.AllPlayers;
            if (players.Count == 0) return;
            var player = players[0];
            if (player.Inventory == null || !player.TryGetCC(out IMovementControllerCC movement)) return;

            _inventory = player.Inventory;
            _movement = movement;
            _movement.SpeedModifier.AddModifier(GetMultiplier);
        }

        private void OnDestroy() => Unbind();

        private void Unbind()
        {
            if (_movement != null) _movement.SpeedModifier.RemoveModifier(GetMultiplier);
            _movement = null;
            _inventory = null;
        }

        private float GetMultiplier()
            => _inventory != null ? Multiplier(_inventory.Weight, _inventory.MaxWeight, _speedAtMax, _curvePower) : 1f;

        /// <summary>
        /// La regla, pura: <c>1 − (1 − speedAtMax) · (peso/máximo)^power</c>, con la proporción recortada a [0, 1]. Si el
        /// máximo baja por debajo de lo que llevas, frena como en el máximo.
        /// </summary>
        public static float Multiplier(float weight, float maxWeight, float speedAtMax, float power)
        {
            if (maxWeight <= 0f) return weight > 0f ? speedAtMax : 1f;
            float ratio = Mathf.Clamp01(weight / maxWeight);
            return 1f - (1f - speedAtMax) * Mathf.Pow(ratio, power);
        }
    }
}
