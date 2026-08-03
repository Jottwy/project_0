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

        /// <summary>
        /// ADR-046 — frames de voz entrantes, drenados por el reproductor (Fase 4).
        ///
        /// Cola propia y NO la ruta de listeners de <see cref="Update"/>: el audio no quiere un
        /// salto al hilo principal a 60 fps por delante del buffer de jitter, y un listener
        /// invocado desde el hilo de red no puede tocar la API de Unity.
        ///
        /// TOPE DURO: sin consumidor —que es exactamente la situación entre la Fase 1 y la
        /// Fase 4— una cola sin límite crece mientras alguien habla. Al pasarse se tira lo MÁS
        /// VIEJO, que en audio en tiempo real es lo correcto de tirar.
        /// </summary>
        public readonly ConcurrentQueue<PeerVoiceMsg> PeerVoice = new ConcurrentQueue<PeerVoiceMsg>();

        /// <summary>~4 s de voz de un hablante a 25 Hz. Suficiente para que un consumidor con
        /// un hipo momentáneo no pierda nada, y lo bastante corto para que una cola sin drenar
        /// no sea una fuga.</summary>
        private const int MaxQueuedVoiceFrames = 100;

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
        // Timestamp of the last remote_players trace, NOT a precomputed "next due" tick — see the
        // wraparound note at its use site. Seeded so the first snapshot after connect always logs.
        private int _lastRemotePlayersLogTick = Environment.TickCount - RemotePlayersLogIntervalMs;
        private const int RemotePlayersLogIntervalMs = 2000;

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

        private void ReadFrames(NetworkStream stream)
        {
            var lenBuf = new byte[4];
            // One frame buffer per connection, grown on demand, instead of a fresh byte[len] per
            // message. Reused across frames along with `reader` — MsgPackReader.CheckBounds guards
            // every read against the LOGICAL length passed to Reset(), not body.Length, so a
            // shorter frame reusing this (possibly larger) buffer can never read stale bytes left
            // over from a previous, longer message (see MsgPackReader's class doc comment).
            byte[] body = new byte[4096];
            var reader = new MsgPackReader(null);
            while (_running)
            {
                if (!ReadExactlyWithTimeout(stream, lenBuf, 4)) break;
                int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                if (len <= 0 || len > 32 * 1024 * 1024) break;

                if (body.Length < len) body = new byte[Mathf.NextPowerOfTwo(len)];
                if (!ReadExactlyWithTimeout(stream, body, len)) break;

                try { reader.Reset(body, len); Dispatch(reader); }
                catch (Exception e) { Debug.LogWarning($"[IPCClient] Failed to decode message: {e.Message}"); }
            }
        }

        /// <summary>
        /// Reads the {"type": ..., ...fields} envelope directly off the wire, without
        /// materializing a Dictionary/object[]/box tree first (docs/STATE.md's "mayor coste
        /// real de la ruta de parseo"). "type" is read as the first key: serde-derive's
        /// internally-tagged enum codegen always serializes the tag before the variant's own
        /// fields (it controls emission order on the Rust side), so this holds for every
        /// ServerMessage variant, not just an observed convention. If that assumption is ever
        /// violated the frame is logged and dropped, same as any other decode failure.
        /// </summary>
        private void Dispatch(MsgPackReader r)
        {
            int n = r.ReadMapHeader();
            if (n <= 0) return;

            var typeKey = r.ReadKey();
            if (!MsgPackReader.Is(typeKey, "type"))
            {
                Debug.LogWarning("[IPCClient] Frame's first key was not \"type\" — dropped.");
                return;
            }
            string type = r.ReadString();
            int remaining = n - 1;

            switch (type)
            {
                case ProtocolMessageTypes.WorldState:
                    var ws = WorldStateMsg.Parse(r, remaining);
                    // Unchecked delta rather than `TickCount >= nextTick`: TickCount wraps every
                    // ~24.9 days of uptime, and past the wrap the plain comparison latches — the
                    // trace either goes silent or fires on every single snapshot. Same idiom and
                    // same reason as ReadExactlyWithTimeout's deadline.
                    if (unchecked(Environment.TickCount - _lastRemotePlayersLogTick) >= RemotePlayersLogIntervalMs)
                    {
                        var ids = ws.remotePlayers.ConvertAll(rp => rp.id.ToString());
                        Debug.Log($"[IPCClient] Parsed remote_players count={ws.remotePlayers.Count} ids=[{string.Join(",", ids)}]");
                        int selfId = NetworkInitializer.Instance != null ? NetworkInitializer.Instance.LastSelectedNetId : 0;
                        Debug.Log($"MPTRACE step=J event=unity_parse_world_state self_id={selfId} sender_id=<none> assigned_id=<none> peer_id=<none> endpoint={serverAddress}:{port} peer_count=<unknown> remote_players_count={ws.remotePlayers.Count} remote_players_ids=[{string.Join(",", ids)}]");
                        Debug.Log($"MPTRACE step=AA event=unity_parse_world_snapshot seed={ws.worldSeed} revision={ws.worldRevision} chunks={ws.visibleChunks.Count} entities={ws.visibleEntities.Count} items={ws.visibleItems.Count}");
                        _lastRemotePlayersLogTick = Environment.TickCount;
                    }
                    _latestState = ws;
                    _pendingStateNotify.Enqueue(ws);
                    break;
                case ProtocolMessageTypes.DeltaUpdate:
                    // ADR-009 §2: 20 Hz movement delta → MovementReconciler.
                    _pendingDeltaNotify.Enqueue(MovementDeltaMsg.Parse(r, remaining));
                    break;
                case ProtocolMessageTypes.ChunkData:
                    // Fase 4.1: grid_gen chunk reply → ChunkStreamer (drained on the main thread).
                    _pendingChunkDataNotify.Enqueue(GridChunkDataMsg.Parse(r, remaining));
                    break;
                case ProtocolMessageTypes.Event:
                    var gameEvent = GameEventMsg.Parse(r, remaining);
                    Events.Enqueue(gameEvent);
                    _pendingNotify.Enqueue(gameEvent);
                    break;
                case ProtocolMessageTypes.PeerVoice:
                    // ADR-046: encolar y recortar. El recorte va aquí y no en el consumidor
                    // porque en la Fase 1 todavía NO HAY consumidor.
                    PeerVoice.Enqueue(PeerVoiceMsg.Parse(r, remaining));
                    while (PeerVoice.Count > MaxQueuedVoiceFrames && PeerVoice.TryDequeue(out _)) { }
                    break;
                case ProtocolMessageTypes.ActionResult:
                    // Phase 4 will consume these; ignored for now.
                    break;
            }
        }

        // Cached so the per-frame reads don't allocate a closure each call (ReadFrames calls
        // ReadExactly twice per inbound message, at 10 Hz plus chunk streaming).
        private Func<bool> _isRunning;

        private bool ReadExactlyWithTimeout(NetworkStream stream, byte[] buf, int count)
        {
            _isRunning ??= () => _running;
            return IpcStreamReader.ReadExactly(stream, buf, count, _isRunning);
        }

        // ─────────────────────────── Sending (main thread) ───────────────────────────

        /// <summary>Send a per-frame player input packet to the backend.</summary>
        public void SendInput(Vector3 movement, Vector2 lookDelta, bool sprint, IList<string> actions = null)
        {
            var w = RentWriter();
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

            SendFrame(w);
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
            int[] equipment = null, int heldItem = 0, byte hitSeq = 0, bool lightOn = false,
            byte fireSeq = 0, byte meleeSeq = 0, int carryDef = 0, byte carryCount = 0)
        {
            // Hardening (postmortem of the `crouch` off-by-one, ADR-020): the map header
            // count and the number of pairs written below are kept in sync by hand. We can't
            // auto-count without rewriting MsgPackWriter (the header is emitted BEFORE the
            // pairs, and 16 crosses the fixmap→map16 encoding boundary), so every pair bumps
            // `fields` and the assert fails loudly in the editor / dev builds if a future
            // field drifts them apart (rmp_serde would silently drop the tail, as `crouch`
            // did). Debug.Assert is [Conditional("UNITY_ASSERTIONS")] → stripped from release
            // players; the message is a const literal (zero alloc on the pose hot path).
            const int FieldCount = 21;
            int fields = 0;

            var w = RentWriter();
            w.WriteMapHeader(FieldCount);
            w.WriteString("type"); w.WriteString("input"); fields++;
            // Legacy fields kept zeroed (the server ignores them when input_seq != 0,
            // but they are non-optional in the wire schema and must be present).
            w.WriteString("movement"); w.WriteArrayHeader(3);
            w.WriteFloat(0f); w.WriteFloat(0f); w.WriteFloat(0f); fields++;
            w.WriteString("look_delta"); w.WriteArrayHeader(2);
            w.WriteFloat(0f); w.WriteFloat(0f); fields++;
            w.WriteString("sprint"); w.WriteBool(moveState == 2); fields++;
            w.WriteString("actions"); w.WriteArrayHeader(0); fields++;
            // ADR-009 prediction fields.
            w.WriteString("input_seq"); w.WriteInt(inputSeq); fields++;
            w.WriteString("client_tick"); w.WriteInt(clientTick); fields++;
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(position.x); w.WriteFloat(position.y); w.WriteFloat(position.z); fields++;
            w.WriteString("velocity"); w.WriteArrayHeader(3);
            w.WriteFloat(velocity.x); w.WriteFloat(velocity.y); w.WriteFloat(velocity.z); fields++;
            w.WriteString("move_state"); w.WriteInt(moveState); fields++;
            w.WriteString("look"); w.WriteArrayHeader(2);
            w.WriteFloat(pitch); w.WriteFloat(yaw); fields++;
            // ADR-044: no longer the dead literal 0 it was until now — bit 0 = aiming, bit 1 = reloading.
            w.WriteString("buttons"); w.WriteInt(buttons); fields++;
            // ADR-020: cosmetic crouch state, relayed to peers (not authoritative).
            w.WriteString("crouch"); w.WriteBool(crouch); fields++;
            // ADR-022: worn clothing item IDs [Head, Torso, Legs, Feet] (0 = empty), relayed to peers.
            w.WriteString("equipment"); w.WriteArrayHeader(4);
            for (int i = 0; i < 4; i++)
                w.WriteInt(equipment != null && i < equipment.Length ? equipment[i] : 0);
            fields++;
            // ADR-023: held item ID (0 = empty hands), relayed to peers (not authoritative).
            w.WriteString("held_item"); w.WriteInt(heldItem); fields++;
            // ADR-024: hit-reaction counter (monotonic, wrapping; 0 = never hit), relayed to peers.
            w.WriteString("hit_seq"); w.WriteInt(hitSeq); fields++;
            // ADR-042: "my active wieldable is emitting light" (generic — any enabled Light under it).
            w.WriteString("light_on"); w.WriteBool(lightOn); fields++;
            // ADR-042: shot counter (monotonic, wrapping; 0 = never fired), relayed to peers.
            w.WriteString("fire_seq"); w.WriteInt(fireSeq); fields++;
            // ADR-044: melee-swing counter (monotonic, wrapping; 0 = never swung), relayed to peers.
            w.WriteString("melee_seq"); w.WriteInt(meleeSeq); fields++;
            // ADR-049: carry state — which CarryableDefinition is on the shoulder (0 = empty hands)
            // and how many units. A LEVEL, not a counter: nothing to sequence, a dropped frame is
            // corrected by the next one.
            w.WriteString("carry_def"); w.WriteInt(carryDef); fields++;
            w.WriteString("carry_count"); w.WriteInt(carryCount); fields++;

            Debug.Assert(fields == FieldCount,
                "SendPlayerInput: field count drifted from the map header — a pair was added/removed without updating WriteMapHeader (rmp_serde would drop the tail).");
            SendFrame(w);
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
            var w = RentWriter();
            w.WriteMapHeader(4);
            w.WriteString("type"); w.WriteString("request_chunk");
            w.WriteString("cx"); w.WriteInt(cx);
            w.WriteString("cz"); w.WriteInt(cz);
            w.WriteString("layer"); w.WriteInt(layer);
            return SendFrame(w);
        }

        /// <summary>
        /// ADR-046 — envía UNA trama de voz codificada al backend propio.
        ///
        /// Toma un rango del buffer de captura reutilizado en vez de un <c>byte[]</c> a medida:
        /// esto se llama 25 veces por segundo mientras alguien habla, y una copia por trama
        /// sería basura pura. <paramref name="count"/> = 0 no se envía — una trama vacía
        /// gastaría un datagrama para decir "silencio", que es justo lo que el silencio ya dice
        /// al no mandar nada.
        ///
        /// Devuelve false si se descartó (sin conexión, escritura fallida, o nada que mandar).
        /// </summary>
        public bool SendVoice(ushort seq, byte[] data, int count)
        {
            if (data == null || count <= 0) return false;

            var w = RentWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("voice");
            w.WriteString("seq"); w.WriteInt(seq);
            w.WriteString("data"); w.WriteBin(data, 0, count);
            return SendFrame(w);
        }

        /// <summary>
        /// Emit an {type:"action", action_type, data:{...}} frame — the shared shape of
        /// every discrete action request. <paramref name="writeData"/> writes exactly
        /// <paramref name="dataFieldCount"/> key/value pairs into the <c>data</c> map, in the
        /// order the backend expects. Byte-for-byte equivalent to the hand-rolled bodies it
        /// replaced (same map(3) envelope + same data map). Returns SendFrame's result
        /// (true = written, false = dropped); existing void callers ignore it.
        ///
        /// CAUTION: <paramref name="dataFieldCount"/> is written to the wire as-is and is NOT
        /// cross-checked against the pairs <paramref name="writeData"/> emits — the caller must
        /// keep the two in sync (unlike SendPlayerInput, whose Debug.Assert guards its count).
        /// A self-check would need to count map entries the writer does not expose, so this
        /// stays a caller contract.
        ///
        /// NOTE: this only covers action frames whose <c>data</c> is a MAP. SendAction, whose
        /// <c>data</c> is nil (0xc0, not an empty map 0x80), does not use this helper.
        /// </summary>
        private bool SendActionFrame(string actionType, int dataFieldCount, Action<MsgPackWriter> writeData)
        {
            var w = RentWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString(actionType);
            w.WriteString("data"); w.WriteMapHeader(dataFieldCount);
            writeData(w);
            return SendFrame(w);
        }

        /// <summary>
        /// ADR-025 respawn-on-demand: ask the server to respawn the (dead) local player. Sent by
        /// RespawnRequester when the native STP Respawn button fires HealthManager.Respawn. The
        /// server honors it only while the player is actually dead (spam/abuse = logged no-op),
        /// resolves a safe spawn and answers with the "player_respawned" event that arms the
        /// AuthoritativePoseApplier snap. Rides the Action channel — no wire schema change.
        /// </summary>
        public void SendRespawnRequest()
        {
            SendActionFrame(ProtocolActionTypes.RespawnRequest, 0, _ => { });
        }

        /// <summary>
        /// ADR-032: graceful save-on-quit. NetworkInitializer sends this during app teardown,
        /// BEFORE it force-kills the backend process, so the host persists the world immediately
        /// (not on the 3-min autosave timer). Zero-data action; the backend saves synchronously and
        /// then exits. Best-effort — the write is synchronous (SendFrame), so if the IPC stream is
        /// still up the frame is flushed to the socket before we return.
        /// </summary>
        public void SendSaveAndShutdown()
        {
            SendActionFrame(ProtocolActionTypes.SaveAndShutdown, 0, _ => { });
        }

        /// <summary>Send a discrete action request (craft, pickup, attack, ...).</summary>
        public void SendAction(string actionType)
        {
            var w = RentWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("action");
            w.WriteString("action_type"); w.WriteString(actionType);
            w.WriteString("data"); w.WriteNil();
            SendFrame(w);
        }

        /// <summary>
        /// ADR-025 Slice B: report REAL local damage (HealthManager.DamageReceived — falls,
        /// hazards) to the authoritative backend so server health tracks local damage and the
        /// server owns the resulting death/respawn. Rides the existing Action channel
        /// (action_type is additive, data is free-form) — NO wire schema change, no bump.
        /// </summary>
        public void SendReportDamage(float amount, string cause)
        {
            SendActionFrame(ProtocolActionTypes.ReportDamage, 2, w =>
            {
                w.WriteString("amount"); w.WriteFloat(amount);
                w.WriteString("cause"); w.WriteString(cause);
            });
        }

        /// <summary>
        /// ADR-030: report a consumed item (eat/drink) to the authoritative backend so
        /// hunger/thirst/health restore by a fixed, server-owned amount (StatInterpolator's
        /// managers are disabled — ADR-009 L2 — so without this report the survival stats can
        /// only ever go down between respawns). Trust-the-client for possession (no
        /// authoritative inventory exists to verify against, same level as report_death_loot);
        /// no request_id — local action over the ordered IPC channel, no dedupe needed.
        /// </summary>
        /// <summary>
        /// ADR-041: report a noise the AI may hear — today, a gunshot. `loudness` is a RADIUS in
        /// metres and the backend takes it at face value (clamped): keeping the weapon table on
        /// this side avoids duplicating in Rust data that belongs to Unity's weapon definitions and
        /// would drift the moment a weapon is added. Mutates nothing server-side, so the worst a
        /// forged one achieves is walking the phantom to a spot.
        /// </summary>
        public void SendReportNoise(Vector3 position, float loudness)
        {
            SendActionFrame(ProtocolActionTypes.ReportNoise, 2, w =>
            {
                w.WriteString("position"); w.WriteArrayHeader(3);
                w.WriteFloat(position.x); w.WriteFloat(position.y); w.WriteFloat(position.z);
                w.WriteString("loudness"); w.WriteFloat(loudness);
            });
        }

        public void SendConsumeItem(int itemId)
        {
            SendActionFrame(ProtocolActionTypes.ConsumeItem, 1, w =>
            {
                w.WriteString("item_id"); w.WriteInt(itemId);
            });
        }

        /// <summary>
        /// ADR-028 Fase B: report the death-loot snapshot at the local death edge. The server
        /// (gated on its own is_dead + per-death dedupe) spawns the authoritative corpse at the
        /// frozen death position. Trust-the-client, same level as position/equipment/held_item —
        /// the backend has no authoritative inventory to verify against. Rides the Action
        /// channel; the CorpseView payload is the wire change (schema v8), not this frame.
        /// `items` carries raw STP item ids (DataIdReference — may be negative) + counts.
        /// </summary>
        public void SendReportDeathLoot(int[] equipment, int heldItem, System.Collections.Generic.IReadOnlyList<CorpseLootStack> items)
        {
            SendActionFrame(ProtocolActionTypes.ReportDeathLoot, 3, w =>
            {
                w.WriteString("equipment"); w.WriteArrayHeader(4);
                for (int i = 0; i < 4; i++)
                    w.WriteInt(equipment != null && i < equipment.Length ? equipment[i] : 0);
                w.WriteString("held_item"); w.WriteInt(heldItem);
                w.WriteString("items"); w.WriteArrayHeader(items?.Count ?? 0);
                for (int i = 0; i < (items?.Count ?? 0); i++)
                {
                    w.WriteMapHeader(2);
                    w.WriteString("item_id"); w.WriteInt(items[i].itemId);
                    w.WriteString("quantity"); w.WriteInt(items[i].quantity);
                }
            });
        }

        /// <summary>
        /// ADR-028 amendment (world chests): seed one host-authoritative supply chest. Host-only
        /// server-side (a joiner's send is a logged no-op — joiners mirror chests via CorpseList);
        /// deduped by (player, request_id) against client re-sends after reconnect. Position is
        /// raycast against the RENDERED world by the caller (StpChestSpawner); loot is picked
        /// client-side (trust-the-client, same level as SendReportDeathLoot).
        /// </summary>
        /// <summary>
        /// ADR-032 amendment: report the CURRENT real STP inventory (debounced on-change by
        /// InventoryReporter) so the backend can persist it. Same items shape as
        /// SendReportDeathLoot; the backend applies the shared corpse hygiene (cap 64, qty>0).
        /// </summary>
        public void SendReportInventory(System.Collections.Generic.IReadOnlyList<CorpseLootStack> items)
        {
            SendActionFrame(ProtocolActionTypes.ReportInventory, 1, w =>
            {
                w.WriteString("items"); w.WriteArrayHeader(items?.Count ?? 0);
                for (int i = 0; i < (items?.Count ?? 0); i++)
                {
                    w.WriteMapHeader(2);
                    w.WriteString("item_id"); w.WriteInt(items[i].itemId);
                    w.WriteString("quantity"); w.WriteInt(items[i].quantity);
                }
            });
        }

        public void SendSpawnWorldChest(long requestId, Vector3 position, System.Collections.Generic.IReadOnlyList<CorpseLootStack> items)
        {
            SendActionFrame(ProtocolActionTypes.SpawnWorldChest, 3, w =>
            {
                w.WriteString("request_id"); w.WriteInt(requestId);
                w.WriteString("position"); w.WriteArrayHeader(3);
                w.WriteFloat(position.x); w.WriteFloat(position.y); w.WriteFloat(position.z);
                w.WriteString("items"); w.WriteArrayHeader(items?.Count ?? 0);
                for (int i = 0; i < (items?.Count ?? 0); i++)
                {
                    w.WriteMapHeader(2);
                    w.WriteString("item_id"); w.WriteInt(items[i].itemId);
                    w.WriteString("quantity"); w.WriteInt(items[i].quantity);
                }
            });
        }

        /// <summary>
        /// ADR-028 Fase D: report a loot withdrawal from a corpse's container. The actual item
        /// move (corpse → looter's inventory) already happened locally via StorageStationUI; this
        /// only mirrors it to the server's CorpseData so despawn-when-empty and a future cross-
        /// client relay stay correct. <paramref name="itemIndex"/> is the SERVER-side Vec index
        /// (see CorpseLootSync's index-mirroring doc — NOT necessarily the local UI slot index).
        /// </summary>
        public void SendTakeCorpseItem(uint corpseId, int itemIndex, int quantity)
        {
            SendActionFrame(ProtocolActionTypes.TakeCorpseItem, 3, w =>
            {
                w.WriteString("corpse_id"); w.WriteInt(corpseId);
                w.WriteString("item_index"); w.WriteInt(itemIndex);
                w.WriteString("quantity"); w.WriteInt(quantity);
            });
        }

        /// <summary>
        /// ADR-029 Fase 1: report a candidate PvP hit detected locally against a remote proxy.
        /// This does not apply damage. The backend currently logs/ignores it until validation and
        /// grant packets are implemented.
        /// </summary>
        public bool SendPvpHitCandidate(long requestId, int attackerId, int victimId, int weaponId,
            float damage, Vector3 origin, Vector3 direction, uint clientTick, Vector3 hitPosition)
        {
            Debug.Log(
                $"MPTRACE step=PVP event=ipc_send_pvp_hit_candidate action={ProtocolActionTypes.PvpHitCandidate} " +
                $"request_id={requestId} attacker_id={attackerId} victim_id={victimId} connected={_connected}");

            bool sent = SendActionFrame(ProtocolActionTypes.PvpHitCandidate, 9, w =>
            {
                w.WriteString("request_id"); w.WriteInt(requestId);
                w.WriteString("attacker_id"); w.WriteInt(attackerId);
                w.WriteString("victim_id"); w.WriteInt(victimId);
                w.WriteString("weapon_id"); w.WriteInt(weaponId);
                w.WriteString("damage"); w.WriteFloat(damage);
                w.WriteString("origin"); w.WriteArrayHeader(3);
                w.WriteFloat(origin.x); w.WriteFloat(origin.y); w.WriteFloat(origin.z);
                w.WriteString("direction"); w.WriteArrayHeader(3);
                w.WriteFloat(direction.x); w.WriteFloat(direction.y); w.WriteFloat(direction.z);
                w.WriteString("client_tick"); w.WriteInt(clientTick);
                w.WriteString("hit_position"); w.WriteArrayHeader(3);
                w.WriteFloat(hitPosition.x); w.WriteFloat(hitPosition.y); w.WriteFloat(hitPosition.z);
            });
            if (!sent)
            {
                Debug.LogWarning(
                    $"MPTRACE step=PVP event=ipc_send_pvp_hit_candidate_failed request_id={requestId} " +
                    $"attacker_id={attackerId} victim_id={victimId} connected={_connected}");
            }

            return sent;
        }

        public void SendWorldInteractRequest(long requestId, uint targetId, string targetKind, string interactionType, Vector3 playerPosition)
        {
            SendActionFrame(ProtocolActionTypes.WorldInteract, 5, w =>
            {
                w.WriteString("request_id"); w.WriteInt(requestId);
                w.WriteString("target_id"); w.WriteInt(targetId);
                w.WriteString("target_kind"); w.WriteString(targetKind);
                w.WriteString("interaction_type"); w.WriteString(interactionType);
                w.WriteString("player_position"); w.WriteArrayHeader(3);
                w.WriteFloat(playerPosition.x); w.WriteFloat(playerPosition.y); w.WriteFloat(playerPosition.z);
            });
        }

        /// <summary>
        /// Phase 1: the host registers the authoritative STP item list with the backend.
        /// The backend stores it, relays it to joiners, and echoes it in world_state.stp_items.
        /// </summary>
        public void SendSetStpItems(System.Collections.Generic.IReadOnlyList<StpItemSpec> items)
        {
            SendActionFrame(ProtocolActionTypes.SetStpItems, 1, w =>
            {
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
            });
        }

        /// <summary>
        /// Phase 2: ask the host to pick up a replicated STP item by its network instance id.
        /// The host validates, removes it (vanishes for all) and grants it back via a
        /// "stp_pickup_granted" event consumed by StpPickupController.
        /// </summary>
        public void SendStpPickup(uint itemId)
        {
            SendActionFrame(ProtocolActionTypes.StpPickup, 1, w =>
            {
                w.WriteString("item_id"); w.WriteInt(itemId);
            });
        }

        /// <summary>
        /// Phase 3: tell the host the local player dropped an STP item from its inventory.
        /// The host assigns a fresh net id, adds it to stp_items, and the Phase 1 relay spawns
        /// the pickup for everyone (with the Phase 2 pickup gate).
        /// </summary>
        public void SendStpDrop(long dropId, int defId, int count, Vector3 position, float rotation)
        {
            SendActionFrame(ProtocolActionTypes.StpDrop, 5, w =>
            {
                w.WriteString("drop_id"); w.WriteInt(dropId);
                w.WriteString("def_id"); w.WriteInt(defId);
                w.WriteString("count"); w.WriteInt(count);
                w.WriteString("position"); w.WriteArrayHeader(3);
                w.WriteFloat(position.x); w.WriteFloat(position.y); w.WriteFloat(position.z);
                w.WriteString("rotation"); w.WriteFloat(rotation);
            });
        }

        /// <summary>
        /// Phase B1: tell the host the local player placed an STP building piece. The host
        /// assigns a fresh net id, adds it to stp_buildings, and the relay spawns the
        /// replicated piece for everyone via StpBuildingReplicator. Deduped by place_id.
        /// </summary>
        public void SendStpPlace(long placeId, int defId, Vector3 position, float rotation, uint groupId, bool isGroup)
        {
            SendActionFrame(ProtocolActionTypes.StpPlace, 6, w =>
            {
                w.WriteString("place_id"); w.WriteInt(placeId);
                w.WriteString("def_id"); w.WriteInt(defId);
                w.WriteString("position"); w.WriteArrayHeader(3);
                w.WriteFloat(position.x); w.WriteFloat(position.y); w.WriteFloat(position.z);
                w.WriteString("rotation"); w.WriteFloat(rotation);
                w.WriteString("group_id"); w.WriteInt(groupId);
                w.WriteString("is_group"); w.WriteBool(isGroup);
            });
        }

        /// <summary>
        /// Phase B2: tell the host the local player added one unit of build material to a
        /// replicated piece (by its B1 network instance id). The host advances the piece's
        /// authoritative progress and the relay propagates it. Deduped by addId. We never
        /// touch inventory here — STP already consumed the in-hand carryable.
        /// </summary>
        public void SendStpBuildAdd(long addId, uint buildingId, int materialId)
        {
            SendActionFrame(ProtocolActionTypes.StpBuildAdd, 3, w =>
            {
                w.WriteString("add_id"); w.WriteInt(addId);
                w.WriteString("building_id"); w.WriteInt(buildingId);
                w.WriteString("material_id"); w.WriteInt(materialId);
            });
        }

        /// <summary>
        /// ADR-037: tell the host the local player cancelled a placed-but-unbuilt piece (by its
        /// B1 network instance id). The host retires it from stp_buildings and the relay makes
        /// every client's replicator destroy its copy through the stale-sweep it already runs.
        /// Deduped by demolishId. Without this the vendor's local-only Destroy is undone by the
        /// next reconcile, which is the whole "cancelling does nothing in multiplayer" bug.
        /// </summary>
        public void SendStpDemolish(long demolishId, uint buildingId)
        {
            SendActionFrame(ProtocolActionTypes.StpDemolish, 2, w =>
            {
                w.WriteString("demolish_id"); w.WriteInt(demolishId);
                w.WriteString("building_id"); w.WriteInt(buildingId);
            });
        }

        /// <summary>
        /// Phase B2.5: the host registers the authoritative STP carryable list with the backend.
        /// The backend stores it, relays it to joiners, and echoes it in world_state.stp_carryables.
        /// </summary>
        public void SendSetStpCarryables(System.Collections.Generic.IReadOnlyList<StpCarryableSpec> carryables)
        {
            SendActionFrame(ProtocolActionTypes.SetStpCarryables, 1, w =>
            {
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
            });
        }

        /// <summary>
        /// Phase B2.5: ask the host to pick up a replicated carryable by its network id. The host
        /// validates, removes it (vanishes for all) and grants it back via a
        /// "stp_carryable_pickup_granted" event consumed by StpCarryablePickupController.
        /// </summary>
        public void SendStpCarryablePickup(uint carryableId)
        {
            SendActionFrame(ProtocolActionTypes.StpCarryablePickup, 1, w =>
            {
                w.WriteString("carryable_id"); w.WriteInt(carryableId);
            });
        }

        /// <summary>
        /// Phase B2.5: tell the host the local player dropped a carryable into the world. The host
        /// assigns a fresh net id, adds it to stp_carryables, and the relay spawns it for everyone.
        /// </summary>
        public void SendStpCarryableDrop(long dropId, int defId, Vector3 position, float rotation)
        {
            SendActionFrame(ProtocolActionTypes.StpCarryableDrop, 4, w =>
            {
                w.WriteString("drop_id"); w.WriteInt(dropId);
                w.WriteString("def_id"); w.WriteInt(defId);
                w.WriteString("position"); w.WriteArrayHeader(3);
                w.WriteFloat(position.x); w.WriteFloat(position.y); w.WriteFloat(position.z);
                w.WriteString("rotation"); w.WriteFloat(rotation);
            });
        }

        /// <summary>
        /// Phase B2.6: the host registers the authoritative scene-harvestable list (id + position).
        /// The backend stores it (remaining=1.0), relays it, and echoes it in stp_harvestables.
        /// </summary>
        public void SendSetStpHarvestables(System.Collections.Generic.IReadOnlyList<StpHarvestableSpec> harvestables)
        {
            SendActionFrame(ProtocolActionTypes.SetStpHarvestables, 1, w =>
            {
                w.WriteString("harvestables"); w.WriteArrayHeader(harvestables.Count);
                for (int i = 0; i < harvestables.Count; i++)
                {
                    var h = harvestables[i];
                    w.WriteMapHeader(2);
                    w.WriteString("id"); w.WriteInt(h.id);
                    w.WriteString("position"); w.WriteArrayHeader(3);
                    w.WriteFloat(h.position.x); w.WriteFloat(h.position.y); w.WriteFloat(h.position.z);
                }
            });
        }

        /// <summary>
        /// Phase B2.6: report a harvest hit on a scene harvestable (by net id) to the host. The
        /// host reduces its authoritative health and the relay propagates it. Deduped by hitId,
        /// so two players chopping the same tree never double-count.
        /// </summary>
        public void SendStpHarvestHit(long hitId, uint harvestableId, float amount)
        {
            SendActionFrame(ProtocolActionTypes.StpHarvestHit, 3, w =>
            {
                w.WriteString("hit_id"); w.WriteInt(hitId);
                w.WriteString("harvestable_id"); w.WriteInt(harvestableId);
                w.WriteString("amount"); w.WriteFloat(amount);
            });
        }

        /// <summary>Send a UI lifecycle event (pause, save, quit, ...).</summary>
        public void SendUiEvent(string eventType)
        {
            var w = RentWriter();
            w.WriteMapHeader(2);
            w.WriteString("type"); w.WriteString("ui_event");
            w.WriteString("event_type"); w.WriteString(eventType);
            SendFrame(w);
        }

        /// <summary>
        /// Per-thread scratch writer, reused across sends so the outbound path allocates
        /// nothing steady-state (MsgPackWriter grows its buffer once and keeps it).
        ///
        /// [ThreadStatic] rather than a shared instance + lock: it makes cross-thread corruption
        /// structurally impossible instead of merely guarded, and needs no lock around the
        /// build. In practice every Send* runs on the main thread, so this is one writer for the
        /// process lifetime.
        ///
        /// CONTRACT: a frame build must not start another one on the same thread — i.e. no
        /// Send* call from inside a SendActionFrame `writeData` callback. No current caller does
        /// (the callbacks only invoke w.Write*), and nesting would silently interleave two
        /// messages into one buffer.
        /// </summary>
        [ThreadStatic] private static MsgPackWriter _scratchWriter;

        private static MsgPackWriter RentWriter()
        {
            var w = _scratchWriter ??= new MsgPackWriter();
            w.Reset();
            return w;
        }

        /// <summary>
        /// Write a length-prefixed frame to the socket. Returns true if it was
        /// written, false if dropped (no live stream, or a write error — the
        /// network thread will detect the break and reconnect). Existing callers
        /// that ignore the return are unaffected.
        ///
        /// The writer reserves the 4-byte length prefix at the head of its own buffer, so the
        /// header is stamped in place and the socket write goes straight from that buffer — no
        /// intermediate body array, no frame copy.
        /// </summary>
        private bool SendFrame(MsgPackWriter w)
        {
            int frameLength = w.StampFrameHeader();

            lock (_sendLock)
            {
                if (_stream == null) return false;
                try { _stream.Write(w.FrameBuffer, 0, frameLength); return true; }
                catch (Exception) { return false; /* the network thread will detect and reconnect */ }
            }
        }
    }
}
