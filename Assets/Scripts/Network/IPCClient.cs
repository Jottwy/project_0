using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Local IPC bridge to the Rust backend (TCP 127.0.0.1:7777).
    ///
    /// Wire format: 4-byte big-endian length prefix + MessagePack body, matching
    /// backend/src/ipc/server.rs. A background thread owns the socket: it connects,
    /// reads frames, and reconnects on failure. The latest WorldState is published
    /// atomically for the main thread; events are queued. Outbound input is written
    /// from the main thread under a lock.
    ///
    /// Singleton: access via <see cref="Instance"/>; it self-creates if needed.
    /// </summary>
    public sealed class IPCClient : MonoBehaviour
    {
        [Header("Connection")]
        public string serverAddress = "127.0.0.1";
        public int port = 7777;
        [Tooltip("Seconds between reconnect attempts.")]
        public float reconnectDelay = 1f;

        private static IPCClient _instance;
        public static IPCClient Instance
        {
            get
            {
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

        public void AddEventListener(GameEventHandler handler) { lock (_eventListeners) _eventListeners.Add(handler); }
        public void RemoveEventListener(GameEventHandler handler) { lock (_eventListeners) _eventListeners.Remove(handler); }
        public void AddStateListener(WorldStateHandler handler) { lock (_stateListeners) _stateListeners.Add(handler); }
        public void RemoveStateListener(WorldStateHandler handler) { lock (_stateListeners) _stateListeners.Remove(handler); }

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

        // ─── Networking internals ───
        private Thread _netThread;
        private volatile bool _running;
        private TcpClient _client;
        private NetworkStream _stream;
        private readonly object _sendLock = new object();

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            _running = true;
            _netThread = new Thread(NetworkLoop) { IsBackground = true, Name = "IPCClient" };
            _netThread.Start();
        }

        private readonly ConcurrentQueue<GameEventMsg> _pendingNotify = new ConcurrentQueue<GameEventMsg>();
        private readonly ConcurrentQueue<WorldStateMsg> _pendingStateNotify = new ConcurrentQueue<WorldStateMsg>();

        private void Update()
        {
            while (_pendingNotify.TryDequeue(out var ev))
                NotifyListeners(ev);

            while (_pendingStateNotify.TryDequeue(out var state))
                NotifyStateListeners(state);
        }

        private void OnDestroy() => Shutdown();
        private void OnApplicationQuit() => Shutdown();

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
                    Debug.Log($"[IPCClient] Connected to {serverAddress}:{port}");

                    ReadFrames(_stream);
                }
                catch (ThreadAbortException) { return; }
                catch (Exception e)
                {
                    if (_running) Debug.LogWarning($"[IPCClient] Connection error: {e.Message}");
                }

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
                    _latestState = ws;
                    _pendingStateNotify.Enqueue(ws);
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

        /// <summary>Send a UI lifecycle event (pause, save, quit, ...).</summary>
        public void SendUiEvent(string eventType)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(2);
            w.WriteString("type"); w.WriteString("ui_event");
            w.WriteString("event_type"); w.WriteString(eventType);
            SendFrame(w.ToArray());
        }

        private void SendFrame(byte[] body)
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
                if (_stream == null) return;
                try { _stream.Write(frame, 0, frame.Length); }
                catch (Exception) { /* the network thread will detect and reconnect */ }
            }
        }
    }
}
