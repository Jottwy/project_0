using PolymindGames;
using PolymindGames.BuildingSystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase B2: turns the LOCAL player's "material added to a piece" into host-authoritative
    /// construction progress, with zero vendor edits. Subscribes to the public
    /// <see cref="IConstructableBuilderCC.BuildMaterialAdded"/> — which fires for BOTH the
    /// carryable (in-hand) path AND the inventory path — so we observe "one unit of material M
    /// went into the piece with netId N" regardless of where the material came from.
    ///
    /// We do NOT touch inventory: STP already consumes the in-hand carryable when used. We only
    /// report the add to the host (SendStpBuildAdd → process_stp_build_add → stp_buildings →
    /// relay → StpBuildingReplicator applies the progress on every instance). A joiner's add is
    /// forwarded to the host by the backend. Self-bootstraps; fully removable.
    /// </summary>
    public sealed class StpBuildMaterialWatcher : MonoBehaviour
    {
        private static StpBuildMaterialWatcher _instance;

        private IConstructableBuilderCC _builder;
        private uint _addCounter;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpBuildMaterialWatcher]");
            _instance = go.AddComponent<StpBuildMaterialWatcher>();
            DontDestroyOnLoad(go);
        }

        // Follow the local player's constructable builder across (re)spawns and scene changes.
        private void Update()
        {
            var builder = ResolveLocalBuilder();
            if (ReferenceEquals(builder, _builder))
                return;

            Unsubscribe();
            _builder = builder;
            Subscribe();
        }

        private static IConstructableBuilderCC ResolveLocalBuilder()
        {
            // HasInstance, not `Instance != null`: the vendor's MonoSingleton getter LOGS AN ERROR
            // (with a stack trace) every time it is read while unset, so polling it from Update in
            // the main menu — where there is no GameMode — wrote two errors per frame. See the twin
            // guard in StpBuildingPlacementWatcher.ResolveLocalController.
            var character = GameMode.HasInstance ? GameMode.Instance.LocalPlayer : null;
            if (character == null)
                return null;

            return character.TryGetCC(out IConstructableBuilderCC cc) ? cc : null;
        }

        private void Subscribe()
        {
            if (_builder != null)
                _builder.BuildMaterialAdded += OnBuildMaterialAdded;
        }

        private void Unsubscribe()
        {
            if (_builder != null)
                _builder.BuildMaterialAdded -= OnBuildMaterialAdded;
        }

        // Fired by the vendor builder each time a unit of build material is applied (carryable
        // use OR inventory add). 'amount' is normally 1; we report one host add per unit.
        private void OnBuildMaterialAdded(BuildMaterialDefinition material, int amount)
        {
            if (material == null || _builder == null)
                return;

            var piece = _builder.CurrentConstructable?.BuildingPiece;
            if (piece == null)
                return;

            // Progress only syncs for replicated pieces (they carry the B1 net id).
            var net = piece.GetComponent<NetworkBuildingInstance>();
            if (net == null)
                return;

            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            int units = Mathf.Max(1, amount);
            for (int i = 0; i < units; i++)
                ipc.SendStpBuildAdd(NextAddId(), net.id, material.Id);
            // ADR-090: one batch of blows, one noise — at the piece, which is where the hammer is.
            WorldNoise.Report(net.transform.position, WorldNoise.HammerLoudness);

            Debug.Log($"[StpBuildMaterialWatcher] add material_id={material.Id} x{units} → building netId={net.id}.");
        }

        // Globally-unique per add: NET_ID-prefixed counter, so two clients never collide and the
        // host can dedup a reliable retransmit (process_stp_build_add).
        private long NextAddId()
        {
            int netId = NetworkInitializer.Instance != null ? NetworkInitializer.Instance.LastSelectedNetId : 0;
            return (long)Mathf.Max(1, netId) * 1000000000L + (++_addCounter);
        }

        private void OnDestroy()
        {
            Unsubscribe();
            if (_instance == this)
                _instance = null;
        }
    }
}
