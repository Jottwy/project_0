using System;
using System.Net;
using System.Net.Sockets;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>
    /// El socket de loopback de verdad: un `UdpClient` en `127.0.0.1:0` (puerto efímero, lo elige
    /// el sistema).
    ///
    /// **Sólo loopback, y a propósito.** Este socket no habla con internet: lo único al otro lado
    /// es el backend de esta misma máquina. Quien cruza la red es la conexión de Steam.
    /// </summary>
    public sealed class UdpTunnelSocket : ISteamTunnelSocket
    {
        private readonly UdpClient _client;
        private bool _disposed;

        public UdpTunnelSocket()
        {
            _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_client.Client.LocalEndPoint).Port;
        }

        public int Port { get; }

        public void SendTo(byte[] payload, int length, IPEndPoint target)
        {
            if (_disposed || payload == null || target == null) return;

            try
            {
                _client.Send(payload, length, target);
            }
            catch (SocketException)
            {
                // Un backend que acaba de morir deja el puerto sin escuchar y Windows contesta con
                // ICMP port unreachable, que aquí sube como excepción. No es noticia: el camino de
                // salida es el latido de 5 s del backend, no este socket.
            }
            catch (ObjectDisposedException)
            {
                // Cierre en marcha desde otro hilo.
            }
        }

        public bool TryReceive(int timeoutMs, out byte[] payload, out int length, out IPEndPoint from)
        {
            payload = null;
            length = 0;
            from = null;
            if (_disposed) return false;

            try
            {
                // `Poll` en vez de un `Receive` bloqueante: el hilo del túnel tiene que poder salir
                // cuando la sesión termina, y un `Receive` sin tope se queda colgado hasta que
                // alguien cierre el socket por debajo.
                if (!_client.Client.Poll(timeoutMs * 1000, SelectMode.SelectRead)) return false;

                IPEndPoint remote = null;
                payload = _client.Receive(ref remote);
                length = payload.Length;
                from = remote;
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _client.Close(); }
            catch (Exception) { /* cerrar dos veces no es noticia */ }
        }
    }
}
