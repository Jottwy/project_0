using BackroomsSurvival.Gameplay.Building;
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
    ///
    /// Grid walls (<see cref="GridWallBuildingPiece"/>) and their infill panels
    /// (<see cref="GridPanelBuildingPiece"/>) get two extras, both consequences of that same
    /// round-trip and both scoped to those two piece types only: the slot is reserved locally while
    /// the request is in flight (<see cref="GridWallReservations"/>), and the preview is re-armed so
    /// pieces can be chained without reopening the survival book (<see cref="RearmPreview"/>).
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
            // HasInstance, not `Instance != null`: the vendor's MonoSingleton getter LOGS AN ERROR
            // (with a stack trace) every time it is read while unset, so polling it from Update in
            // the main menu — where there is no GameMode — wrote two errors per frame. Measured at
            // 2.5M lines of Player.log in minutes, which buries every other diagnostic. Same guard
            // NoiseReporter and GridTestWorld already use.
            var character = GameMode.HasInstance ? GameMode.Instance.LocalPlayer : null;
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

            // ADR-081 fase 1: no se le manda al host lo que el host ya va a rechazar. Se mide contra
            // la posición de la PIEZA y no la del jugador, que es exactamente lo que valida
            // `process_stp_place` — de pie dentro de la zona se puede apuntar fuera de ella, y ese
            // caso lo cubre esta guarda y no la de BuildZoneGate.
            //
            // Va DESPUÉS de la guarda de conexión a propósito: sin host no hay regla de territorio
            // que aplicar y la partida offline se queda como estaba.
            if (!BuildPermission.CanBuildAt(placed.transform.position))
            {
                var owner = GameMode.HasInstance ? GameMode.Instance.LocalPlayer : null;
                DestroyLocalPiece(placed);
                _placedCandidate = null;
                if (owner != null)
                    MessageDispatcher.Instance.Dispatch(owner, MsgType.Error, BuildPermission.DeniedMessage);
                return;
            }

            // Captured before DestroyLocalPiece: the re-arm below needs the definition, and reading
            // it off a destroyed component is a trap waiting for the day Destroy stops being
            // end-of-frame.
            var definition = placed.Definition;
            int defId = definition.Id;
            placed.transform.GetPositionAndRotation(out var pos, out var rot);
            float yaw = rot.eulerAngles.y;
            long placeId = NextPlaceId();

            // Phase B3: report the structure this piece belongs to so the host keeps the whole
            // structure in one group on every client. A GroupBuildingPiece carries sockets and
            // needs pose-cell dedup; a free piece (campfire) does not. If the piece attached to
            // an already-replicated group, send that group's id; if it created a brand-new local
            // group, send 0 so the host mints a fresh one.
            bool isGroup = placed is GroupBuildingPiece;
            uint groupId = 0;
            var group = placed.ParentGroup;
            if (group != null && group.gameObject.GetComponent<NetworkBuildingGroupInstance>() is { } comp)
                groupId = comp.groupId;

            // Destroy the local piece (and its lone group, if it just created one) so the
            // host-authoritative replicated copy is the single source of truth on every
            // instance. Mirrors StpNativeDropWatcher's local-drop → host-echo handoff.
            DestroyLocalPiece(placed);

            ipc.SendStpPlace(placeId, defId, pos, yaw, groupId, isGroup);
            Debug.Log($"[StpBuildingPlacementWatcher] placed def_id={defId} place_id={placeId} group_id={groupId} is_group={isGroup} pos={pos:F2} → host.");

            // A grid wall and its infill panels are standalone, so the host does not pose-cell dedup
            // them and the slot stays physically empty for the whole round-trip. Hold it locally
            // until the replicated piece arrives, or a fast second placement lands a duplicate in the
            // same slot. The panel needs this at least as badly as the wall: a frame's cells are 1 m
            // apart, so chaining drywall is a burst of placements with no pause between them.
            if (placed is GridWallBuildingPiece or GridPanelBuildingPiece)
            {
                GridWallReservations.Reserve(pos, yaw);
                // Deferred a frame on purpose — see RearmPreview.
                CoroutineUtility.InvokeNextFrameSafe(this, () => RearmPreview(definition));
            }

            _placedCandidate = null;
        }

        /// <summary>
        /// Puts a fresh preview of the same piece back in the player's hands so walls can be built
        /// one after another.
        ///
        /// The vendor only re-arms automatically for <see cref="GroupBuildingPiece"/>
        /// (CharacterBuildController.HandleSuccessfulPlacement passes
        /// <c>createNew: _buildingPiece is GroupBuildingPiece</c>), and that class is sealed, so
        /// neither a grid wall nor a grid panel can inherit the behaviour — it ends build mode after
        /// every single piece and the survival book has to be reopened for the next one. Hence this
        /// hook, which runs from the ObjectPlaced event, i.e. AFTER the controller has already torn
        /// build mode down; the SetBuildingPiece call below pushes the input context straight back.
        ///
        /// Restricted to the two grid-snapped types on purpose: re-arming the free pieces (campfire,
        /// sleeping bag) would change vendor behaviour nobody asked to change.
        ///
        /// Deferred one frame by the caller. Re-arming inline would run while the place action is
        /// still dispatching its own callbacks: SetBuildingPiece pops and re-pushes the building
        /// input context, and that toggles every PlayerInputBehaviour — including the one whose
        /// callback we are currently inside, which unregisters and re-registers the very action
        /// being dispatched. The Input System tolerates that, but nothing is gained by relying on
        /// it. One frame with no preview is invisible.
        /// </summary>
        private void RearmPreview(BuildingPieceDefinition definition)
        {
            if (_controller == null || definition == null || definition.Prefab == null)
                return;

            // A frame passed: the player may have hit Escape or picked something else out of the
            // book. Only fill an empty hand — never stomp a choice they made in the meantime.
            if (_controller.BuildingPiece != null)
                return;

            // Same spawn recipe as the survival book and the vendor's own chaining: parked far below
            // the world so the fresh preview never flashes at the origin before its first
            // UpdatePlacement positions it.
            var preview = Instantiate(definition.Prefab, new Vector3(0f, -1000f, 0f), Quaternion.identity);
            _controller.SetBuildingPiece(preview);
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
