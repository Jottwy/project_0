using System.Collections.Generic;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase 2: persistent controller that routes STP pickups through the host and credits the
    /// local inventory ONLY on host confirmation. The item GameObject (and its
    /// <see cref="NetworkItemPickupGate"/>) is destroyed when the host removes the item, so the
    /// pending state and the credit live here, not on the item. Self-bootstraps; fully removable.
    /// </summary>
    public sealed class StpPickupController : MonoBehaviour
    {
        private static StpPickupController _instance;

        private readonly struct PendingPickup
        {
            public readonly ICharacter Recoger;
            public readonly int Count;

            public PendingPickup(ICharacter recoger, int count)
            {
                Recoger = recoger;
                Count = count;
            }
        }

        // item_id → quién lo pidió y cuánto se pidió (se acredita cuando el host concede).
        private readonly Dictionary<uint, PendingPickup> _pending = new Dictionary<uint, PendingPickup>();
        private IPCClient _ipc;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpPickupController]");
            _instance = go.AddComponent<StpPickupController>();
            DontDestroyOnLoad(go);
        }

        /// <summary>Called by a NetworkItemPickupGate when the local player interacts.</summary>
        public static void RequestPickup(uint itemId, int defId, int count, ICharacter recoger)
        {
            if (_instance != null)
                _instance.Request(itemId, defId, count, recoger);
        }

        private void Request(uint itemId, int defId, int count, ICharacter recoger)
        {
            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            // PUERTA 1 — no se pide lo que no cabe.
            //
            // El host concede BORRANDO el item del mundo, así que una concesión no se puede
            // devolver: para cuando el cliente descubre que el inventario está lleno, el item ya no
            // existe en ninguna parte. Preguntar antes es la única forma de que el caso común
            // (mochila llena, jugador insistiendo en la E) no cueste items.
            int wanted = Mathf.Max(1, count);
            var def = ItemDefinition.GetWithId(defId);
            if (def != null && recoger?.Inventory != null && SpaceFor(recoger.Inventory, def, wanted) <= 0)
            {
                Debug.Log($"[StpPickupController] sin hueco para '{def.Name}' x{wanted} (item_id={itemId}) — no se pide.");
                return;
            }

            _pending[itemId] = new PendingPickup(recoger, wanted);
            ipc.SendStpPickup(itemId);
            Debug.Log($"[StpPickupController] requested pickup item_id={itemId}");
        }

        /// <summary>
        /// Cuántas unidades admitiría el inventario ahora mismo, sin escribir nada. Suma el veredicto
        /// de cada contenedor porque el límite (peso, slots, restricciones de categoría) es de
        /// contenedor, no de inventario.
        ///
        /// Es una SONDA, no una garantía: entre la sonda y la concesión del host cabe que el
        /// inventario cambie. Por eso hay una segunda puerta en la concesión — el sobrante vuelve
        /// al mundo en vez de evaporarse.
        /// </summary>
        private static int SpaceFor(IInventory inventory, ItemDefinition def, int wanted)
        {
            // Dummy: el vendor usa este mismo idioma para preguntar sin instanciar (ItemContainer).
            var probe = new ItemStack(Item.GetDummyItem(def), wanted);
            var containers = inventory.FindContainers(_ => true);
            if (containers == null)
                return 0;

            int room = 0;
            for (int i = 0; i < containers.Count; i++)
            {
                room += Mathf.Max(0, containers[i].GetAllowedCount(probe).allowedCount);
                if (room >= wanted)
                    return wanted;
            }
            return room;
        }

        private void Update()
        {
            if (_ipc == null && IPCClient.TryGetInstance(out var ipc))
            {
                _ipc = ipc;
                _ipc.AddEventListener(OnGameEvent);
            }
        }

        // Fired on the main thread (IPCClient.Update drains the event queue).
        private void OnGameEvent(GameEventMsg ev)
        {
            if (ev == null || ev.eventType != "stp_pickup_granted")
                return;

            var d = ev.data as Dictionary<string, object>;
            if (d == null)
                return;

            uint itemId = (uint)IPCParse.L(d, "item_id");
            int defId = (int)IPCParse.L(d, "def_id");
            int count = Mathf.Max(1, (int)IPCParse.L(d, "count"));

            // Only credit if THIS client requested it (host grants to the winner only).
            if (!_pending.TryGetValue(itemId, out var pending))
                return;
            _pending.Remove(itemId);

            var recoger = pending.Recoger;
            if (recoger == null)
                return;

            var def = ItemDefinition.GetWithId(defId);
            if (def == null)
            {
                Debug.LogWarning($"[StpPickupController] grant for unknown def_id={defId} (item_id={itemId}).");
                return;
            }

            // PUERTA 2 — lo que no entre vuelve al mundo.
            //
            // `AddItem` devuelve cuánto admitió DE VERDAD, y tirar ese número era el bug: la
            // concesión ya había borrado el item del mundo, así que un inventario lleno lo hacía
            // desaparecer del juego entero. La concesión no se puede cancelar (el host ya la
            // aplicó), pero el sobrante sí se puede devolver: se suelta a los pies por el MISMO
            // camino que un drop nativo, o sea que reaparece replicado y recogible para todos.
            (int added, string rejectReason) = recoger.Inventory.AddItem(new ItemStack(new Item(def), count));
            if (added >= count)
            {
                Debug.Log($"[StpPickupController] credited '{def.Name}' x{count} (item_id={itemId}) to local inventory.");
                return;
            }

            int leftover = count - Mathf.Max(0, added);
            Debug.LogWarning($"[StpPickupController] '{def.Name}': el inventario admitió {added} de {count} " +
                $"(motivo={rejectReason}) — se devuelven {leftover} al mundo.");
            ReturnToWorld(recoger, defId, leftover);
        }

        /// <summary>
        /// Suelta al mundo lo que el inventario no admitió, por el mismo camino que un drop nativo
        /// (<c>SendStpDrop</c> → <c>process_stp_drop</c> → relay): reaparece replicado y recogible
        /// para cualquiera, en vez de existir solo en este cliente.
        ///
        /// A los pies y no donde estaba el item: el original ya no existe, y dejarlo caer donde el
        /// jugador está garantiza que lo tiene delante para recogerlo cuando haga sitio.
        /// </summary>
        private static void ReturnToWorld(ICharacter recoger, int defId, int count)
        {
            if (count <= 0)
                return;

            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
            {
                Debug.LogError($"[StpPickupController] IPC caído: no se pueden devolver {count}×def_id={defId} " +
                    "al mundo. ESE sobrante se ha perdido.");
                return;
            }

            var t = recoger.transform;
            // El transform del personaje ya está a la altura de los pies, así que no hace falta el
            // raycast al suelo que sí necesita el drop nativo (ese nace a la altura de la mano).
            Vector3 at = t.position + t.forward * 0.5f;
            long dropId = StpNativeDropWatcher.MintDropId();
            ipc.SendStpDrop(dropId, defId, count, at, t.eulerAngles.y);
            Debug.Log($"[StpPickupController] devueltos {count}×def_id={defId} al mundo drop_id={dropId} en {at:F2}.");
        }

        private void OnDestroy()
        {
            if (_ipc != null)
                _ipc.RemoveEventListener(OnGameEvent);
            if (_instance == this)
                _instance = null;
        }
    }
}
