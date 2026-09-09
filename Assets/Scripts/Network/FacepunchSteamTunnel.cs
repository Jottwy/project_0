using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
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

        /// Techo de envío, en bytes/s, que la estimación de ancho de banda de Valve puede alcanzar.
        ///
        /// MEDIDO el 2026-09-10 en playtest real: el host enviaba 253,8 KB/s sostenidos contra un
        /// `Est avail bandwidth` de 256,0 KB/s — el default de Valve, clavado — y acumulaba ~483.000
        /// bytes en la cola de salida. Eso son **1,9 s de retraso** que no tienen nada que ver con la
        /// red (ping 24 ms, 0 % de pérdida): es cola por saturación.
        ///
        /// Subir el techo NO fuerza a usarlo. Steam sigue estimando el ancho real de la línea y no
        /// pasa de ahí; esto sólo deja de recortar por debajo al que sí tiene subida. Es alivio del
        /// SÍNTOMA: la causa es el volumen del payload y se ataca aparte.
        private const int SendRateMaxBytesPerSec = 1024 * 1024;

        private SocketManager _socket;
        private ConnectionManager _connection;

        /// Un canal por `Connection`, y **el mismo objeto siempre**: el host guarda sus enlaces en
        /// un diccionario por canal, así que devolver uno nuevo por mensaje le daría un socket de
        /// loopback por datagrama.
        private readonly Dictionary<uint, FacepunchTunnelChannel> _channels =
            new Dictionary<uint, FacepunchTunnelChannel>();

        // Fase 0 de la tanda de lag (09-09): medir RTT/jitter REALES por Steam sin tocar el wire.
        // `Connection.DetailedStatus()` envuelve ISteamNetworkingSockets::GetDetailedConnectionStatus
        // — texto ya calculado por Valve (ping, jitter, calidad), nada que el backend ni el protocolo
        // tengan que aprender. Throttled a mano porque `Poll()` gira cada pocos ms, no por frame.
        private const long DiagLogIntervalMs = 2000;
        private DateTime _lastDiagLogUtc = DateTime.MinValue;

        /// Reparto del tráfico de SALIDA por tipo de paquete. El primer campo de la cabecera de 12 B
        /// es `packet_type` (u16 big-endian, `protocol.rs:56-58`) y viaja EN CLARO: el túnel puede
        /// contarlo sin descifrar nada, sin conocer el payload y sin tocar el protocolo.
        ///
        /// Existe porque `DetailedStatus()` dice CUÁNTO se envía (253,8 KB/s medidos el 10-09) pero
        /// no DE QUÉ; sin este reparto, cualquier recorte se decide por estimación. Los tipos reales
        /// van de 0x00 a 0x5F, así que 256 cubre de sobra y el índice no puede desbordar.
        private static readonly long[] SentBytesByType = new long[256];
        private static readonly long[] SentPacketsByType = new long[256];

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
                ApplySendRateCeiling();
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
                ApplySendRateCeiling();
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
        ///
        /// **`false` = este transporte ya no sirve**, y el bombeo tiene que parar. Cuando Steam
        /// invalida el socket —la sesión se cierra, la aplicación se apaga— `Receive` revienta
        /// dentro de Facepunch, y volver a intentarlo revienta igual: en el primer playtest real
        /// esto dejó 203 excepciones repetidas porque el fallo se tragaba aquí y el bucle seguía.
        /// </summary>
        public bool Poll()
        {
            try
            {
                _socket?.Receive(ReceiveBatch, true);
                _connection?.Receive(ReceiveBatch, true);
                LogDiagnosticsIfDue();
                return true;
            }
            catch (Exception e)
            {
                // Se registra UNA vez, porque después de esto no hay más vueltas.
                Debug.LogWarning($"[SteamTunnel] Receive falló ({e.GetType().Name}: {e.Message}); " +
                                 "se abandona el transporte.");
                return false;
            }
        }

        /// <summary>
        /// Sube el techo de envío por encima del default de Valve. Global al proceso, así que se
        /// aplica ANTES de abrir socket o conexión: una conexión ya creada nace con el valor que
        /// hubiera en ese momento.
        /// </summary>
        private static void ApplySendRateCeiling()
        {
            try
            {
                SteamNetworkingUtils.SendRateMax = SendRateMaxBytesPerSec;
            }
            catch (Exception e)
            {
                // Un techo que no se deja poner no impide jugar: se sigue con el default de Valve.
                Debug.LogWarning($"[SteamTunnel] No se pudo subir SendRateMax: {e.Message}");
            }
        }

        /// <summary>
        /// Fase 0 (09-09): un log de <c>DetailedStatus()</c> por conexión activa cada
        /// <see cref="DiagLogIntervalMs"/>. Sólo lectura de lo que Valve ya mide — ni protocolo ni
        /// wire cambian, así que no hace falta ADR (regla dura #7) para verlo en un playtest real.
        /// </summary>
        private void LogDiagnosticsIfDue()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastDiagLogUtc).TotalMilliseconds < DiagLogIntervalMs)
                return;
            _lastDiagLogUtc = now;

            // Lado joiner: una única conexión saliente al host.
            if (_connection != null)
                LogConnectionStatus(_connection.Connection, _connection.Connection.Id);

            // Lado host: una por peer conectado.
            foreach (var kv in _channels)
                LogConnectionStatus(kv.Value.Connection, kv.Key);

            LogSentByType();
        }

        /// <summary>
        /// Vuelca el reparto acumulado del tráfico de salida por tipo de paquete, de mayor a menor.
        /// Acumulado desde el arranque, no por intervalo: lo que se busca es qué DOMINA, y un total
        /// no depende de que el volcado caiga en un momento representativo.
        /// </summary>
        private static void LogSentByType()
        {
            var rows = new List<string>();
            long total = 0;
            for (int type = 0; type < SentBytesByType.Length; type++)
            {
                long bytes = Interlocked.Read(ref SentBytesByType[type]);
                if (bytes == 0)
                    continue;
                total += bytes;
                rows.Add($"0x{type:X2}={bytes / 1024}KB/{Interlocked.Read(ref SentPacketsByType[type])}pkt");
            }

            if (rows.Count == 0)
                return;

            Debug.Log($"[SteamTunnel] SENT_BY_TYPE total={total / 1024}KB {string.Join(" ", rows)}");
        }

        /// <summary>Contabiliza un datagrama de salida en el reparto por tipo.</summary>
        internal static void CountSent(byte[] payload, int length)
        {
            // Un datagrama más corto que la cabecera no lleva tipo legible; se ignora en vez de
            // inventarle uno.
            if (payload == null || length < 2)
                return;

            // Big-endian, como lo escribe `PacketHeader::to_bytes`. Sólo el byte bajo indexa: los
            // tipos reales caben en él y el alto es siempre 0.
            int type = payload[1];
            Interlocked.Add(ref SentBytesByType[type], length);
            Interlocked.Increment(ref SentPacketsByType[type]);
        }

        private static void LogConnectionStatus(Connection connection, uint connectionId)
        {
            try
            {
                string status = connection.DetailedStatus();
                Debug.Log($"[SteamTunnel] RTT_DIAG conn={connectionId}\n{status}");
            }
            catch (Exception e)
            {
                // DetailedStatus puede fallar en el instante entre "conectado" y "cerrado"; no es
                // motivo para tirar el bombeo (a diferencia del fallo de Receive de arriba).
                Debug.LogWarning($"[SteamTunnel] RTT_DIAG conn={connectionId} falló: {e.Message}");
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

        // Fase 0 de la tanda de lag (09-09): sólo para que el transporte pueda pedir
        // DetailedStatus() por canal — nada de esto viaja al backend ni cambia el wire.
        internal Connection Connection => _connection;

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
                if (result == Result.OK)
                    FacepunchSteamTunnelTransport.CountSent(payload, length);
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
