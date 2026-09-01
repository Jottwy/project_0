using PolymindGames;
using PolymindGames.ResourceHarvesting;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Network id + hit observer for a replicated STP scene harvestable (Phase B2.6). Attached by
    /// <see cref="StpHarvestableSyncManager"/> to each scene <see cref="HarvestableResource"/>.
    /// Holds the host-assigned net id and the cached resource-carryable spawn config (read from
    /// the vendor spawner before it was stripped). Subscribes to the harvestable's public
    /// <c>Harvested</c> event so a REAL local hit is reported to the host. Applying the
    /// host-authoritative health back (via reflection SetState) does NOT fire <c>Harvested</c>,
    /// so there is no feedback loop.
    /// </summary>
    public sealed class NetworkHarvestableInstance : MonoBehaviour
    {
        public uint id;
        public int logDefId = -1;      // cached CarryableDefinition id of the spawned resource
        public int logCount;           // how many carryables to spawn on depletion
        public bool spawnedOnDeplete;  // host: guards one-shot depletion spawn

        /// <summary>ADR-114 D7/D8: los materiales de un mueble desmontable, resueltos a ids de
        /// `ItemDefinition`. Vacío en un árbol o una roca del vendor, que siguen soltando su
        /// carryable por `logDefId`/`logCount`.
        ///
        /// Son dos listas y no una porque son dos COSAS: un tronco es un `CarryableDefinition` (se
        /// carga a hombros) y una tabla de madera es un `ItemDefinition` (entra en la bolsa). No
        /// hay conversión entre ambos, así que quien siembra elige camino según cuál esté puesta.
        /// </summary>
        [System.Serializable]
        public struct ItemDrop
        {
            public int defId;
            public int count;
        }

        public System.Collections.Generic.List<ItemDrop> itemDrops;

        private HarvestableResource _harvestable;
        private bool _subscribed;
        private static uint _hitCounter;

        /// The vendor component this instance wraps, resolved once. Exposed so the sync manager
        /// reuses this cache instead of doing its own GetComponent per harvestable per scan.
        /// Lazy-resolves in case it is read before OnEnable (disabled object).
        public HarvestableResource Harvestable
        {
            get
            {
                if (_harvestable == null)
                    _harvestable = GetComponent<HarvestableResource>();
                return _harvestable;
            }
        }

        private void OnEnable()
        {
            if (_harvestable == null)
                _harvestable = GetComponent<HarvestableResource>();

            if (_harvestable != null && !_subscribed)
            {
                _harvestable.Harvested += OnHarvested;
                _subscribed = true;
            }
        }

        private void OnDisable()
        {
            if (_harvestable != null && _subscribed)
            {
                _harvestable.Harvested -= OnHarvested;
                _subscribed = false;
            }
        }

        // Fired by the vendor only on a REAL local hit (the sync layer applies authoritative
        // health via reflection SetState, which does not raise this). Report it to the host.
        private void OnHarvested(float amount, in DamageArgs args)
        {
            if (id == 0)
                return;

            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            ipc.SendStpHarvestHit(NextHitId(), id, amount);
        }

        // Globally-unique per hit: NET_ID-prefixed counter (host dedups via hit_id), so two
        // players chopping the same tree never double-count.
        private static long NextHitId()
        {
            int netId = NetIdentity.Local;
            return (long)Mathf.Max(1, netId) * 1000000000L + (++_hitCounter);
        }
    }
}
