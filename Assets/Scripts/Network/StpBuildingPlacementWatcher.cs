using PolymindGames;
using PolymindGames.BuildingSystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase B1: turns the LOCAL player's STP building placements into host-authoritative
    /// replicated pieces, with zero vendor edits. Subscribes to the public
    /// <see cref="IBuildControllerCC"/> ObjectPlaced/BuildingPieceChanged events; when the
    /// local player confirms a placement, it DESTROYS the local piece and asks the host to
    /// spawn the replicated version (SendStpPlace → process_stp_place → stp_buildings →
    /// relay → <see cref="StpBuildingReplicator"/> on every instance, incl. this one). The
    /// 1-frame local→echo round-trip is a tolerable flicker for pre-alpha (mirrors
    /// <see cref="StpNativeDropWatcher"/>).
    ///
    /// Gating: a joiner's placement does NOT persist locally — the host validates and the
    /// relay drives every spawn, so all instances stay identical. Self-bootstraps; removable.
    /// </summary>
    public sealed class StpBuildingPlacementWatcher : MonoBehaviour
    {
        private static StpBuildingPlacementWatcher _instance;

        private IBuildControllerCC _controller;
        private BuildingPiece _currentPreview;   // the controller's active preview piece
        private BuildingPiece _placedCandidate;  // the preview active just before the last change
        private uint _placeCounter;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpBuildingPlacementWatcher]");
            _instance = go.AddComponent<StpBuildingPlacementWatcher>();
            DontDestroyOnLoad(go);
        }

        // Re-resolve the local player's build controller each frame so the subscription
        // follows player (re)spawns and scene changes. Cheap: just a couple of lookups.
        private void Update()
        {
            var controller = ResolveLocalController();
            if (ReferenceEquals(controller, _controller))
                return;

            Unsubscribe();
            _controller = controller;
            Subscribe();
        }

        private static IBuildControllerCC ResolveLocalController()
        {
            var gm = GameMode.Instance;
            var character = gm != null ? gm.LocalPlayer : null;
            if (character == null)
                return null;

            return character.TryGetCC(out IBuildControllerCC cc) ? cc : null;
        }

        private void Subscribe()
        {
            if (_controller == null)
                return;

            _controller.ObjectPlaced += OnObjectPlaced;
            _controller.BuildingPieceChanged += OnBuildingPieceChanged;
            _currentPreview = _controller.BuildingPiece;
            _placedCandidate = null;
        }

        private void Unsubscribe()
        {
            if (_controller == null)
                return;

            _controller.ObjectPlaced -= OnObjectPlaced;
            _controller.BuildingPieceChanged -= OnBuildingPieceChanged;
            _currentPreview = null;
            _placedCandidate = null;
        }

        // Fired whenever the controller's active preview changes (book select, cycle, and —
        // crucially — right after a placement, when it switches to the next preview/null).
        // The placed piece is the one that was active just before this change.
        private void OnBuildingPieceChanged(BuildingPiece piece)
        {
            _placedCandidate = _currentPreview;
            _currentPreview = piece;
        }

        // Fired after a successful placement. The event arg is the NEXT preview (a vendor
        // quirk), not the placed piece — the placed piece is _placedCandidate, tracked above.
        private void OnObjectPlaced(BuildingPiece next)
        {
            var placed = _placedCandidate;
            if (placed == null)
                return; // nothing tracked (should not happen on a real placement)

            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return; // no host link → leave the local piece as-is (offline/solo fallback)

            int defId = placed.Definition.Id;
            placed.transform.GetPositionAndRotation(out var pos, out var rot);
            float yaw = rot.eulerAngles.y;
            long placeId = NextPlaceId();

            // Destroy the local piece (and its lone group, if it just created one) so the
            // host-authoritative replicated copy is the single source of truth on every
            // instance. Mirrors StpNativeDropWatcher's local-drop → host-echo handoff.
            DestroyLocalPiece(placed);

            ipc.SendStpPlace(placeId, defId, pos, yaw);
            Debug.Log($"[StpBuildingPlacementWatcher] placed def_id={defId} place_id={placeId} pos={pos:F2} → host.");

            _placedCandidate = null;
        }

        private static void DestroyLocalPiece(BuildingPiece placed)
        {
            var group = placed.ParentGroup;
            // A group piece that was the only member of a freshly-created group: drop the
            // whole group container (it would otherwise linger empty / be auto-destroyed).
            if (group != null && group.BuildingPieces.Count <= 1)
                Destroy(group.gameObject);
            else
                Destroy(placed.gameObject);
        }

        // Globally-unique per logical placement: NET_ID-prefixed counter, so two clients
        // never collide and the host can dedup a reliable retransmit (process_stp_place).
        private long NextPlaceId()
        {
            int netId = NetworkInitializer.Instance != null ? NetworkInitializer.Instance.LastSelectedNetId : 0;
            return (long)Mathf.Max(1, netId) * 1000000000L + (++_placeCounter);
        }

        private void OnDestroy()
        {
            Unsubscribe();
            if (_instance == this)
                _instance = null;
        }
    }
}
