using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Local IPC bridge to the Rust backend (TCP 127.0.0.1:7777 by default).
    ///
    /// Wire format: 4-byte big-endian length prefix + MessagePack body, matching
    /// backend/src/ipc/server.rs. A background thread owns the socket: it connects,
    /// reads frames, and reconnects on failure. The latest WorldState is published
    /// atomically for the main thread; events are queued. Outbound input is written
    /// from the main thread under a lock.
    ///
    /// Singleton: NetworkInitializer owns creation and startup. Readers should use TryGetInstance.
    /// </summary>
    public sealed class IPCClient : MonoBehaviour
    {
        [Header("Connection")]
        public string serverAddress = "127.0.0.1";
        public int port = 7777;
        [Tooltip("Seconds between reconnect attempts.")]
        public float reconnectDelay = 1f;
        private static IPCClient _instance;
        private static bool _isQuitting;

        public static bool HasInstance => _instance != null;
        public static bool IsQuitting => _isQuitting;

        public static bool TryGetInstance(out IPCClient client)
        {
            client = _instance;
            return client != null;
        }

        public static void MarkQuitting()
        {
            _isQuitting = true;
        }

        public static IPCClient Instance
        {
            get
            {
                if (_isQuitting)
                    return null;

                if (_instance == null)
                {
                    _instance = FindFirstObjectByType<IPCClient>();
                    if (_instance == null)
                    {
                        var go = new GameObject("IPCClient");
                        _instance = go.AddComponent<IPCClient>();
                    }
                }
                return _instance;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            _isQuitting = false;
        }

        // ─── Published state (read from the main thread) ───
        private volatile WorldStateMsg _latestState;
        public WorldStateMsg LatestState => _latestState;

        private volatile bool _connected;
        public bool IsConnected => _connected;

        public readonly ConcurrentQueue<GameEventMsg> Events = new ConcurrentQueue<GameEventMsg>();

        public delegate void GameEventHandler(GameEventMsg ev);
        private readonly List<GameEventHandler> _eventListeners = new List<GameEventHandler>();

        public delegate void WorldStateHandler(WorldStateMsg state);
        private readonly List<WorldStateHandler> _stateListeners = new List<WorldStateHandler>();

        public delegate void MovementDeltaHandler(MovementDeltaMsg delta);
        private readonly List<MovementDeltaHandler> _deltaListeners = new List<MovementDeltaHandler>();

        // Fase 4.1 — grid_gen chunk replies (RequestChunk → ChunkData → ChunkStreamer).
        public delegate void ChunkDataHandler(GridChunkDataMsg data);
        private readonly List<ChunkDataHandler> _chunkDataListeners = new List<ChunkDataHandler>();

        public void AddEventListener(GameEventHandler handler) { lock (_eventListeners) _eventListeners.Add(handler); }
        public void RemoveEventListener(GameEventHandler handler) { lock (_eventListeners) _eventListeners.Remove(handler); }
        public void AddStateListener(WorldStateHandler handler) { lock (_stateListeners) _stateListeners.Add(handler); }
        public void RemoveStateListener(WorldStateHandler handler) { lock (_stateListeners) _stateListeners.Remove(handler); }
        public void AddMovementDeltaListener(MovementDeltaHandler handler) { lock (_deltaListeners) _deltaListeners.Add(handler); }
        public void RemoveMovementDeltaListener(MovementDeltaHandler handler) { lock (_deltaListeners) _deltaListeners.Remove(handler); }
        public void AddChunkDataListener(ChunkDataHandler handler) { lock (_chunkDataListeners) _chunkDataListeners.Add(handler); }
        public void RemoveChunkDataListener(ChunkDataHandler handler) { lock (_chunkDataListeners) _chunkDataListeners.Remove(handler); }

        private void NotifyListeners(GameEventMsg ev)
        {
            lock (_eventListeners)
                foreach (var h in _eventListeners)
                    try { h(ev); } catch { }
        }

        private void NotifyStateListeners(WorldStateMsg state)
        {
            lock (_stateListeners)
                foreach (var h in _stateListeners)
                    try { h(state); } catch { }
        }

        private void NotifyMovementDeltaListeners(MovementDeltaMsg delta)
        {
            lock (_deltaListeners)
                foreach (var h in _deltaListeners)
                    try { h(delta); } catch { }
        }

        private void NotifyChunkDataListeners(GridChunkDataMsg data)
        {
            lock (_chunkDataListeners)
                foreach (var h in _chunkDataListeners)
                    try { h(data); } catch { }
        }

        // ─── Networking internals ───
        private Thread _netThread;
        private volatile bool _running;
        private volatile bool _hasConnectedOnce;
        private TcpClient _client;
        private NetworkStream _stream;
        private readonly object _sendLock = new object();
        private int _nextRemotePlayersLogTick;

        public void ConfigureEndpoint(string address, int ipcPort)
        {
            if (string.IsNullOrWhiteSpace(address)) address = "127.0.0.1";
            bool changed = serverAddress != address || port != ipcPort;

            serverAddress = address;
            port = ipcPort;

            if (!changed) return;

            Debug.Log($"[IPCClient] Configured IPC endpoint {serverAddress}:{port}");
            lock (_sendLock)
            {
                try { _stream?.Close(); } catch { }
                try { _client?.Close(); } catch { }
                _stream = null;
            }
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            ApplyEnvironmentEndpoint();
        }

        public void StartClient()
        {
            Debug.Log($"[IPCClient] StartClient requested endpoint={serverAddress}:{port}");

            if (_isQuitting) return;
            if (_netThread != null && _netThread.IsAlive) return;

            _running = true;
            _hasConnectedOnce = false;
            _netThread = new Thread(NetworkLoop) { IsBackground = true, Name = "IPCClient" };
            _netThread.Start();
            Debug.Log($"[IPCClient] Starting IPC client endpoint={serverAddress}:{port}");
        }

        private void ApplyEnvironmentEndpoint()
        {
            string ipcAddr = Environment.GetEnvironmentVariable("IPC_ADDR");
            if (!string.IsNullOrWhiteSpace(ipcAddr))
            {
                int colon = ipcAddr.LastIndexOf(':');
                if (colon > 0 && colon < ipcAddr.Length - 1 &&
                    int.TryParse(ipcAddr.Substring(colon + 1), out int parsedPort))
                {
                    serverAddress = ipcAddr.Substring(0, colon);
                    port = parsedPort;
                    return;
                }
            }

            string ipcPort = Environment.GetEnvironmentVariable("IPC_PORT");
            if (int.TryParse(ipcPort, out int parsedIpcPort))
                port = parsedIpcPort;
        }

        private readonly ConcurrentQueue<GameEventMsg> _pendingNotify = new ConcurrentQueue<GameEventMsg>();
        private readonly ConcurrentQueue<WorldStateMsg> _pendingStateNotify = new ConcurrentQueue<WorldStateMsg>();
        private readonly ConcurrentQueue<MovementDeltaMsg> _pendingDeltaNotify = new ConcurrentQueue<MovementDeltaMsg>();
        private readonly ConcurrentQueue<GridChunkDataMsg> _pendingChunkDataNotify = new ConcurrentQueue<GridChunkDataMsg>();

        private void Update()
        {
            while (_pendingNotify.TryDequeue(out var ev))
                NotifyListeners(ev);

            while (_pendingStateNotify.TryDequeue(out var state))
                NotifyStateListeners(state);

            while (_pendingDeltaNotify.TryDequeue(out var delta))
                NotifyMovementDeltaListeners(delta);

            while (_pendingChunkDataNotify.TryDequeue(out var chunkData))
                NotifyChunkDataListeners(chunkData);
        }

        private void OnDestroy() => Shutdown();
        private void OnApplicationQuit()
        {
            _isQuitting = true;
            Shutdown();
        }

        private void Shutdown()
        {
            _running = false;
            Thread.Sleep(100);
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            try { _netThread?.Join(500); } catch { }
            if (_instance == this) _instance = null;
        }

        // ─────────────────────────── Background thread ───────────────────────────

        private void NetworkLoop()
        {
            while (_running)
            {
                try
                {
                    var client = new TcpClient();
                    client.NoDelay = true;
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    client.Connect(serverAddress, port);

                    lock (_sendLock)
                    {
                        _client = client;
                        _stream = client.GetStream();
                    }
                    _connected = true;
                    _hasConnectedOnce = true;
                    Debug.Log($"[IPCClient] Connected to {serverAddress}:{port}");

                    ReadFrames(_stream);
                }
                catch (ThreadAbortException) { return; }
                catch (Exception e)
                {
                    if (_running)
                    {
                        string message = $"[IPCClient] Connection error: {e.Message}";
                        if (_hasConnectedOnce)
                            Debug.LogWarning(message);
                        else
                            Debug.Log(message);
                    }
                }

                if (_running && _hasConnectedOnce && _connected)
                    Debug.LogWarning($"[IPCClient] Disconnected from {serverAddress}:{port}");

                _connected = false;
                lock (_sendLock) { _stream = null; }
                try { _client?.Close(); } catch { }

                if (_running) Thread.Sleep(Mathf.Max(100, (int)(reconnectDelay * 1000)));
            }
        }

        private const int ReadTimeoutMs = 5000;

        private void ReadFrames(NetworkStream stream)
        {
            var lenBuf = new byte[4];
            while (_running)
            {
                if (!ReadExactlyWithTimeout(stream, lenBuf, 4)) break;
                int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                if (len <= 0 || len > 32 * 1024 * 1024) break;

                var body = new byte[len];
                if (!ReadExactlyWithTimeout(stream, body, len)) break;

                try { Dispatch(body); }
                catch (Exception e) { Debug.LogWarning($"[IPCClient] Failed to decode message: {e.Message}"); }
            }
        }

        private void Dispatch(byte[] body)
        {
            var root = new MsgPackReader(body).ReadValue() as Dictionary<string, object>;
            if (root == null) return;
            string type = IPCParse.S(root, "type");

            switch (type)
            {
                case "world_state":
                    var ws = WorldStateMsg.Parse(root);
                    if (Environment.TickCount >= _nextRemotePlayersLogTick)
                    {
                        var ids = ws.remotePlayers.ConvertAll(r => r.id.ToString());
                        Debug.Log($"[IPCClient] Parsed remote_players count={ws.remotePlayers.Count} ids=[{string.Join(",", ids)}]");
                        int selfId = NetworkInitializer.Instance != null ? NetworkInitializer.Instance.LastSelectedNetId : 0;
                        Debug.Log($"MPTRACE step=J event=unity_parse_world_state self_id={selfId} sender_id=<none> assigned_id=<none> peer_id=<none> endpoint={serverAddress}:{port} peer_count=<unknown> remote_players_count={ws.remotePlayers.Count} remote_players_ids=[{string.Join(",", ids)}]");
                        Debug.Log($"MPTRACE step=AA event=unity_parse_world_snapshot seed={ws.worldSeed} revision={ws.worldRevision} chunks={ws.visibleChunks.Count} entities={ws.visibleEntities.Count} items={ws.visibleItems.Count}");
                        _nextRemotePlayersLogTick = Environment.TickCount + 2000;
                    }
                    _latestState = ws;
                    _pendingStateNotify.Enqueue(ws);
                    break;
                case "delta_update":
                    // ADR-009 §2: 20 Hz movement delta → MovementReconciler.
                    _pendingDeltaNotify.Enqueue(MovementDeltaMsg.Parse(root));
                    break;
                case "chunk_data":
                    // Fase 4.1: grid_gen chunk reply → ChunkStreamer (drained on the main thread).
                    _pendingChunkDataNotify.Enqueue(GridChunkDataMsg.Parse(root));
                    break;
                case "event":
                    var gameEvent = GameEventMsg.Parse(root);
                    Events.Enqueue(gameEvent);
                    _pendingNotify.Enqueue(gameEvent);
                    break;
                case "action_result":
                    // Phase 4 will consume these; ignored for now.
                    break;
            }
        }

        private bool ReadExactlyWithTimeout(NetworkStream stream, byte[] buf, int count)
        {
            int offset = 0;
            long deadline = Environment.TickCount + ReadTimeoutMs;

            while (offset < count && _running)
            {
                if (!stream.DataAvailable)
                {
                    if (Environment.TickCount >= deadline) return false;
                    Thread.Sleep(1);
                    continue;
                }

                int read;
                try { read = stream.Read(buf, offset, count - offset); }
                catch (Exception) { return false; }

                if (read <= 0) return false;
                offset += read;
                deadline = Environment.TickCount + ReadTimeoutMs;
            }

            return offset == count;
        }

        // ─────────────────────────── Sending (main thread) ───────────────────────────

        /// <summary>Send a per-frame player input packet to the backend.</summary>
        public void SendInput(Vector3 movement, Vector2 lookDelta, bool sprint, IList<string> actions = null)
        {
            var w = new MsgPackWriter();
            int fieldCount = 5;
            w.WriteMapHeader(fieldCount);
            w.WriteString("type"); w.WriteString("input");
            w.WriteString("movement"); w.WriteArrayHeader(3);
            w.WriteFloat(movement.x); w.WriteFloat(movement.y); w.WriteFloat(movement.z);
            w.WriteString("look_delta"); w.WriteArrayHeader(2);
            w.WriteFloat(lookDelta.x); w.WriteFloat(lookDelta.y);
            w.WriteString("sprint"); w.WriteBool(sprint);
            w.WriteString("actions");
            int n = actions?.Count ?? 0;
            w.WriteArrayHeader(n);
            for (int i = 0; i < n; i++) w.WriteString(actions[i]);

            SendFrame(w.ToArray());
        }

        /// <summary>
        /// ADR-009 client-prediction input: the STP client owns prediction and
        /// sends an authoritative pose (position+velocity+move_state) plus look
        /// (pitch,yaw) for server validation (Option B). inputSeq lets the server
        /// echo ack_input_seq so the reconciler can compare against its buffer.
        /// Coexists with the legacy direction-based SendInput; the server takes
        /// the prediction path only when input_seq != 0.
        /// </summary>
        public void SendPlayerInput(uint inputSeq, uint clientTick, Vector3 position,
            Vector3 velocity, byte moveState, float pitch, float yaw, ushort buttons, bool crouch = false,
            int[] equipment = null, int heldItem = 0)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(15); // 15 key/value pairs below — MUST match the count or rmp_serde drops the tail (held_item)
            w.WriteString("type"); w.WriteString("input");
            // Legacy fields kept zeroed (the server ignores them when input_seq != 0,
            // but they are non-optional in the wire schema and must be present).
            w.WriteString("movement"); w.WriteArrayHeader(3);
            w.WriteFloat(0f); w.WriteFloat(0f); w.WriteFloat(0f);
            w.WriteString("look_delta"); w.WriteArrayHeader(2);
            w.WriteFloat(0f); w.WriteFloat(0f);
            w.WriteString("sprint"); w.WriteBool(moveState == 2);
            w.WriteString("actions"); w.WriteArrayHeader(0);
            // ADR-009 prediction fields.
            w.WriteString("input_seq"); w.WriteInt(inputSeq);
            w.WriteString("client_tick"); w.WriteInt(clientTick);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(position.x); w.WriteFloat(position.y); w.WriteFloat(position.z);
            w.WriteString("velocity"); w.WriteArrayHeader(3);
            w.WriteFloat(velocity.x); w.WriteFloat(velocity.y); w.WriteFloat(velocity.z);
            w.WriteString("move_state"); w.WriteInt(moveState);
            w.WriteString("look"); w.WriteArrayHeader(2);
            w.WriteFloat(pitch); w.WriteFloat(yaw);
            w.WriteString("buttons"); w.WriteInt(buttons);
            // ADR-020: cosmetic crouch state, relayed to peers (not authoritative).
            w.WriteString("crouch"); w.WriteBool(crouch);
            // ADR-022: worn clothing item IDs [Head, Torso, Legs, Feet] (0 = empty), relayed to peers.
            w.WriteString("equipment"); w.WriteArrayHeader(4);
            for (int i = 0; i < 4; i++)
                w.WriteInt(equipment != null && i < equipment.Length ? equipment[i] : 0);
            // ADR-023: held item ID (0 = empty hands), relayed to peers (not authoritative).
            w.WriteString("held_item"); w.WriteInt(heldItem);
            SendFrame(w.ToArray());
        }

        /// <summary>
        /// Fase 4.1: ask the backend to generate one chunk via grid_gen and reply
        /// with its 5 m tile-wall bitmask ("chunk_data" → ChunkStreamer). Maps to
        /// ClientMessage::RequestChunk { cx, cz, layer }.
        ///
        /// Returns true if the frame was written to the socket, false if it was
        /// dropped (no connection yet / write failed). The caller (ChunkStreamer)
        /// uses this so it never marks a chunk "pending" for a request that never
        /// left — which otherwise stranded that chunk empty forever.
        /// </summary>
        public bool SendRequestChunk(int cx, int cz, byte layer)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(4);
            w.WriteString("type"); w.WriteString("request_chunk");
            w.WriteString("cx"); w.WriteInt(cx);
            w.WriteString("cz"); w.WriteInt(cz);
            w.WriteString("layer"); w.WriteInt(layer);
            return SendFrame(w.ToArray());
        }

        /// <summary>Send a discrete action request (craft, pickup, attack, ...).</summary>
        public void SendAction(string actionType)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString(actionType);
            w.WriteString("data"); w.WriteNil();
            SendFrame(w.ToArray());
        }

        public void SendWorldInteractRequest(long requestId, uint targetId, string targetKind, string interactionType, Vector3 playerPosition)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("world_interact");
            w.WriteString("data"); w.WriteMapHeader(5);
            w.WriteString("request_id"); w.WriteInt(requestId);
            w.WriteString("target_id"); w.WriteInt(targetId);
            w.WriteString("target_kind"); w.WriteString(targetKind);
            w.WriteString("interaction_type"); w.WriteString(interactionType);
            w.WriteString("player_position"); w.WriteArrayHeader(3);
            w.WriteFloat(playerPosition.x); w.WriteFloat(playerPosition.y); w.WriteFloat(playerPosition.z);
            SendFrame(w.ToArray());
        }

        /// <summary>
        /// Phase 1: the host registers the authoritative STP item list with the backend.
        /// The backend stores it, relays it to joiners, and echoes it in world_state.stp_items.
        /// </summary>
        public void SendSetStpItems(System.Collections.Generic.IReadOnlyList<StpItemSpec> items)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("set_stp_items");
            w.WriteString("data"); w.WriteMapHeader(1);
            w.WriteString("items"); w.WriteArrayHeader(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                w.WriteMapHeader(5);
                w.WriteString("id"); w.WriteInt(it.id);
                w.WriteString("def_id"); w.WriteInt(it.defId);
                w.WriteString("count"); w.WriteInt(it.count);
                w.WriteString("position"); w.WriteArrayHeader(3);
                w.WriteFloat(it.position.x); w.WriteFloat(it.position.y); w.WriteFloat(it.position.z);
                w.WriteString("rotation"); w.WriteFloat(it.rotation);
            }
            SendFrame(w.ToArray());
        }

        /// <summary>
        /// Phase 2: ask the host to pick up a replicated STP item by its network instance id.
        /// The host validates, removes it (vanishes for all) and grants it back via a
        /// "stp_pickup_granted" event consumed by StpPickupController.
        /// </summary>
        public void SendStpPickup(uint itemId)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("stp_pickup");
            w.WriteString("data"); w.WriteMapHeader(1);
            w.WriteString("item_id"); w.WriteInt(itemId);
            SendFrame(w.ToArray());
        }

        /// <summary>
        /// Phase 3: tell the host the local player dropped an STP item from its inventory.
        /// The host assigns a fresh net id, adds it to stp_items, and the Phase 1 relay spawns
        /// the pickup for everyone (with the Phase 2 pickup gate).
        /// </summary>
        public void SendStpDrop(long dropId, int defId, int count, Vector3 position, float rotation)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("stp_drop");
            w.WriteString("data"); w.WriteMapHeader(5);
            w.WriteString("drop_id"); w.WriteInt(dropId);
            w.WriteString("def_id"); w.WriteInt(defId);
            w.WriteString("count"); w.WriteInt(count);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(position.x); w.WriteFloat(position.y); w.WriteFloat(position.z);
            w.WriteString("rotation"); w.WriteFloat(rotation);
            SendFrame(w.ToArray());
        }

        /// <summary>
        /// Phase B1: tell the host the local player placed an STP building piece. The host
        /// assigns a fresh net id, adds it to stp_buildings, and the relay spawns the
        /// replicated piece for everyone via StpBuildingReplicator. Deduped by place_id.
        /// </summary>
        public void SendStpPlace(long placeId, int defId, Vector3 position, float rotation, uint groupId, bool isGroup)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("stp_place");
            w.WriteString("data"); w.WriteMapHeader(6);
            w.WriteString("place_id"); w.WriteInt(placeId);
            w.WriteString("def_id"); w.WriteInt(defId);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(position.x); w.WriteFloat(position.y); w.WriteFloat(position.z);
            w.WriteString("rotation"); w.WriteFloat(rotation);
            w.WriteString("group_id"); w.WriteInt(groupId);
            w.WriteString("is_group"); w.WriteBool(isGroup);
            SendFrame(w.ToArray());
        }

        /// <summary>
        /// Phase B2: tell the host the local player added one unit of build material to a
        /// replicated piece (by its B1 network instance id). The host advances the piece's
        /// authoritative progress and the relay propagates it. Deduped by addId. We never
        /// touch inventory here — STP already consumed the in-hand carryable.
        /// </summary>
        public void SendStpBuildAdd(long addId, uint buildingId, int materialId)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("stp_build_add");
            w.WriteString("data"); w.WriteMapHeader(3);
            w.WriteString("add_id"); w.WriteInt(addId);
            w.WriteString("building_id"); w.WriteInt(buildingId);
            w.WriteString("material_id"); w.WriteInt(materialId);
            SendFrame(w.ToArray());
        }

        /// <summary>
        /// Phase B2.5: the host registers the authoritative STP carryable list with the backend.
        /// The backend stores it, relays it to joiners, and echoes it in world_state.stp_carryables.
        /// </summary>
        public void SendSetStpCarryables(System.Collections.Generic.IReadOnlyList<StpCarryableSpec> carryables)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("set_stp_carryables");
            w.WriteString("data"); w.WriteMapHeader(1);
            w.WriteString("carryables"); w.WriteArrayHeader(carryables.Count);
            for (int i = 0; i < carryables.Count; i++)
            {
                var c = carryables[i];
                w.WriteMapHeader(4);
                w.WriteString("id"); w.WriteInt(c.id);
                w.WriteString("def_id"); w.WriteInt(c.defId);
                w.WriteString("position"); w.WriteArrayHeader(3);
                w.WriteFloat(c.position.x); w.WriteFloat(c.position.y); w.WriteFloat(c.position.z);
                w.WriteString("rotation"); w.WriteFloat(c.rotation);
            }
            SendFrame(w.ToArray());
        }

        /// <summary>
        /// Phase B2.5: ask the host to pick up a replicated carryable by its network id. The host
        /// validates, removes it (vanishes for all) and grants it back via a
        /// "stp_carryable_pickup_granted" event consumed by StpCarryablePickupController.
        /// </summary>
        public void SendStpCarryablePickup(uint carryableId)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("stp_carryable_pickup");
            w.WriteString("data"); w.WriteMapHeader(1);
            w.WriteString("carryable_id"); w.WriteInt(carryableId);
            SendFrame(w.ToArray());
        }

        /// <summary>
        /// Phase B2.5: tell the host the local player dropped a carryable into the world. The host
        /// assigns a fresh net id, adds it to stp_carryables, and the relay spawns it for everyone.
        /// </summary>
        public void SendStpCarryableDrop(long dropId, int defId, Vector3 position, float rotation)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("stp_carryable_drop");
            w.WriteString("data"); w.WriteMapHeader(4);
            w.WriteString("drop_id"); w.WriteInt(dropId);
            w.WriteString("def_id"); w.WriteInt(defId);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(position.x); w.WriteFloat(position.y); w.WriteFloat(position.z);
            w.WriteString("rotation"); w.WriteFloat(rotation);
            SendFrame(w.ToArray());
        }

        /// <summary>
        /// Phase B2.6: the host registers the authoritative scene-harvestable list (id + position).
        /// The backend stores it (remaining=1.0), relays it, and echoes it in stp_harvestables.
        /// </summary>
        public void SendSetStpHarvestables(System.Collections.Generic.IReadOnlyList<StpHarvestableSpec> harvestables)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("set_stp_harvestables");
            w.WriteString("data"); w.WriteMapHeader(1);
            w.WriteString("harvestables"); w.WriteArrayHeader(harvestables.Count);
            for (int i = 0; i < harvestables.Count; i++)
            {
                var h = harvestables[i];
                w.WriteMapHeader(2);
                w.WriteString("id"); w.WriteInt(h.id);
                w.WriteString("position"); w.WriteArrayHeader(3);
                w.WriteFloat(h.position.x); w.WriteFloat(h.position.y); w.WriteFloat(h.position.z);
            }
            SendFrame(w.ToArray());
        }

        /// <summary>
        /// Phase B2.6: report a harvest hit on a scene harvestable (by net id) to the host. The
        /// host reduces its authoritative health and the relay propagates it. Deduped by hitId,
        /// so two players chopping the same tree never double-count.
        /// </summary>
        public void SendStpHarvestHit(long hitId, uint harvestableId, float amount)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString("stp_harvest_hit");
            w.WriteString("data"); w.WriteMapHeader(3);
            w.WriteString("hit_id"); w.WriteInt(hitId);
            w.WriteString("harvestable_id"); w.WriteInt(harvestableId);
            w.WriteString("amount"); w.WriteFloat(amount);
            SendFrame(w.ToArray());
        }

        /// <summary>Send a UI lifecycle event (pause, save, quit, ...).</summary>
        public void SendUiEvent(string eventType)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(2);
            w.WriteString("type"); w.WriteString("ui_event");
            w.WriteString("event_type"); w.WriteString(eventType);
            SendFrame(w.ToArray());
        }

        /// <summary>
        /// Write a length-prefixed frame to the socket. Returns true if it was
        /// written, false if dropped (no live stream, or a write error — the
        /// network thread will detect the break and reconnect). Existing callers
        /// that ignore the return are unaffected.
        /// </summary>
        private bool SendFrame(byte[] body)
        {
            var frame = new byte[4 + body.Length];
            int len = body.Length;
            frame[0] = (byte)(len >> 24);
            frame[1] = (byte)(len >> 16);
            frame[2] = (byte)(len >> 8);
            frame[3] = (byte)len;
            Array.Copy(body, 0, frame, 4, body.Length);

            lock (_sendLock)
            {
                if (_stream == null) return false;
                try { _stream.Write(frame, 0, frame.Length); return true; }
                catch (Exception) { return false; /* the network thread will detect and reconnect */ }
            }
        }
    }
}
