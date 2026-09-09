using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BackroomsSurvival.Connectivity;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// El túnel de ADR-135 hablando con Steam de verdad. **Es la ÚNICA parte del túnel que toca
    /// Steamworks**: todo lo que se puede equivocar —autorizar, mapear peer a puerto, cerrar sin
    /// dejar nada abierto— vive en <c>SteamTunnel.cs</c>, sin Steam dentro y con tests.
    ///
    /// Aquí sólo hay traducción, igual que en <see cref="FacepunchSteamLobbyQuery"/>.
    /// </summary>
    public sealed class FacepunchSteamTunnelTransport : ISteamTunnelTransport, ISocketManager, IConnectionManager
    {
        /// Cuántos mensajes se recogen por vuelta. El goteo de un join son ~820 datagramas/s
        /// (ADR-117), y el bombeo gira cada pocos milisegundos: 64 es holgado sin reservar de más.
        private const int ReceiveBatch = 64;

        private SocketManager _socket;
        private ConnectionManager _connection;

        /// Un canal por `Connection`, y **el mismo objeto siempre**: el host guarda sus enlaces en
        /// un diccionario por canal, así que devolver uno nuevo por mensaje le daría un socket de
        /// loopback por datagrama.
        private readonly Dictionary<uint, FacepunchTunnelChannel> _channels =
            new Dictionary<uint, FacepunchTunnelChannel>();

        public Action<ISteamTunnelChannel, byte[], int> OnMessage { get; set; }

        public Action<ISteamTunnelChannel> OnClosed { get; set; }

        public bool StartHost(int virtualPort)
        {
            if (!SteamLobbyManager.IsAvailable)
            {
                Debug.LogWarning("[SteamTunnel] Steam no está disponible; no se abre el túnel. " +
                                 "Host directo, LAN y relay propio no se ven afectados.");
                return false;
            }

            try
            {
                _socket = SteamNetworkingSockets.CreateRelaySocket(virtualPort, this);
                return _socket != null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SteamTunnel] CreateRelaySocket falló: {e.Message}");
                return false;
            }
        }

        public ISteamTunnelChannel Connect(ulong hostSteamId, int virtualPort)
        {
            if (!SteamLobbyManager.IsAvailable)
            {
                Debug.LogWarning("[SteamTunnel] Steam no está disponible; no se abre el túnel.");
                return null;
            }

            try
            {
                _connection = SteamNetworkingSockets.ConnectRelay(hostSteamId, virtualPort, this);
                if (_connection == null) return null;

                var channel = new FacepunchTunnelChannel(_connection.Connection, hostSteamId,
                    () => _connection != null && _connection.Connected);
                _channels[_connection.Connection.Id] = channel;
                return channel;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SteamTunnel] ConnectRelay a {hostSteamId} falló: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Recoge lo que Valve tenga pendiente. Los `OnMessage` de abajo salen **desde aquí**, o
        /// sea en el hilo del túnel, que es lo que permite no pasar por el `Update` de Unity.
        /// </summary>
        public void Poll()
        {
            try
            {
                _socket?.Receive(ReceiveBatch, true);
                _connection?.Receive(ReceiveBatch, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SteamTunnel] Receive falló: {e.Message}");
            }
        }

        public void Dispose()
        {
            try { _socket?.Close(); }
            catch (Exception) { /* cerrar dos veces no es noticia */ }

            try { _connection?.Close(); }
            catch (Exception) { /* idem */ }

            _socket = null;
            _connection = null;
            _channels.Clear();
        }

        // ─── ISocketManager: el lado del host ───
        //
        // Las dos interfaces de Steam se implementan de forma EXPLÍCITA. No es estilo: sus
        // callbacks se llaman `OnMessage` igual que la propiedad de `ISteamTunnelTransport`, y una
        // clase no puede tener las dos cosas con ese nombre (CS0102). Explícitas, además, la
        // superficie pública de esta clase queda siendo sólo la del túnel.

        /// <summary>
        /// **Se acepta la conexión, no al peer.** Quién entra de verdad lo decide el secreto de
        /// sesión en el primer mensaje (ADR-135 D4'), y eso todavía no ha llegado: aquí no hay nada
        /// que comprobar. Mientras no se autorice, `SteamTunnelHost` no le reenvía ni un byte al
        /// backend.
        /// </summary>
        void ISocketManager.OnConnecting(Connection connection, ConnectionInfo info)
        {
            connection.Accept();
        }

        void ISocketManager.OnConnected(Connection connection, ConnectionInfo info)
        {
            ulong steamId = info.Identity.SteamId.Value;
            _channels[connection.Id] = new FacepunchTunnelChannel(connection, steamId, () => true);
            Debug.Log($"[SteamTunnel] Conexión de Steam abierta con {steamId} (pendiente de autorizar).");
        }

        void ISocketManager.OnDisconnected(Connection connection, ConnectionInfo info)
        {
            if (_channels.TryGetValue(connection.Id, out FacepunchTunnelChannel channel))
            {
                _channels.Remove(connection.Id);
                OnClosed?.Invoke(channel);
            }
        }

        void ISocketManager.OnMessage(Connection connection, Steamworks.Data.NetIdentity identity,
            IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            if (!_channels.TryGetValue(connection.Id, out FacepunchTunnelChannel tunnelChannel))
            {
                tunnelChannel = new FacepunchTunnelChannel(connection, identity.SteamId.Value, () => true);
                _channels[connection.Id] = tunnelChannel;
            }

            Deliver(tunnelChannel, data, size);
        }

        // ─── IConnectionManager: el lado del joiner ───

        void IConnectionManager.OnConnecting(ConnectionInfo info)
        {
        }

        void IConnectionManager.OnConnected(ConnectionInfo info)
        {
            Debug.Log("[SteamTunnel] Conexión de Steam con el host establecida.");
        }

        void IConnectionManager.OnDisconnected(ConnectionInfo info)
        {
            Debug.LogWarning($"[SteamTunnel] La conexión de Steam se cerró: {info.EndReason}.");
        }

        void IConnectionManager.OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            if (_connection == null) return;
            if (!_channels.TryGetValue(_connection.Connection.Id, out FacepunchTunnelChannel tunnelChannel)) return;

            Deliver(tunnelChannel, data, size);
        }

        private void Deliver(ISteamTunnelChannel channel, IntPtr data, int size)
        {
            if (size <= 0 || data == IntPtr.Zero) return;

            // Se copia: el puntero que da Steam sólo vive hasta que vuelve de `Receive`.
            var payload = new byte[size];
            Marshal.Copy(data, payload, 0, size);
            OnMessage?.Invoke(channel, payload, size);
        }
    }

    /// <summary>Una <c>Connection</c> de Steam vestida de <see cref="ISteamTunnelChannel"/>.</summary>
    internal sealed class FacepunchTunnelChannel : ISteamTunnelChannel
    {
        private readonly Connection _connection;
        private readonly Func<bool> _isOpen;
        private bool _closed;

        public FacepunchTunnelChannel(Connection connection, ulong remoteSteamId, Func<bool> isOpen)
        {
            _connection = connection;
            RemoteSteamId = remoteSteamId;
            _isOpen = isOpen;
        }

        public ulong RemoteSteamId { get; }

        public bool IsOpen => !_closed && (_isOpen == null || _isOpen());

        /// <summary>
        /// **`Unreliable | NoNagle`**: un datagrama del juego es un mensaje y sale ya. La fiabilidad
        /// la pone el backend (`reliability.rs`) y doblarla aquí es exactamente lo que hay que
        /// evitar; Nagle agruparía datagramas y le añadiría latencia a un tráfico que ya va a
        /// ticks.
        /// </summary>
        public bool Send(byte[] payload, int length)
        {
            if (_closed || payload == null || length <= 0) return false;

            try
            {
                Result result = _connection.SendMessage(payload, 0, length,
                    SendType.Unreliable | SendType.NoNagle);
                return result == Result.OK;
            }
            catch (Exception)
            {
                // Una conexión que muere entre el envío y este catch no es noticia: el camino de
                // salida es el latido del backend, no este socket.
                return false;
            }
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;

            try { _connection.Close(false, 0, "tunnel closed"); }
            catch (Exception) { /* cerrar dos veces no es noticia */ }
        }
    }
}
