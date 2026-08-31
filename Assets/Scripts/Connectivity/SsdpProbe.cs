using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>
    /// Busca routers UPnP en la red local con un M-SEARCH de SSDP. Es la única parte de todo esto
    /// que toca la red local, y la que puede tardar.
    ///
    /// **El socket se ata a una interfaz concreta, y eso no es un detalle.** En la máquina donde se
    /// escribió esto hay DIEZ IPv4: la buena (`192.168.1.40`), dos de VPN y seis APIPA. Un socket
    /// sin atar deja que el sistema elija por dónde sale el multicast, y elegir mal significa
    /// mandar el M-SEARCH por el túnel de una VPN, donde no hay ningún router doméstico
    /// escuchando. El síntoma sería idéntico al de "no hay UPnP": cero respuestas.
    ///
    /// **Se pregunta tres veces.** SSDP va sobre UDP multicast y no hay retransmisión: un
    /// datagrama perdido es un router que no existe. Tres envíos con 300 ms entre ellos es lo que
    /// hacen las implementaciones que funcionan.
    ///
    /// **Medido el 2026-08-31 en la máquina de desarrollo: cero respuestas**, ni con el `ST` de
    /// `InternetGatewayDevice` ni con `ssdp:all`, ni por multicast ni por unicast a
    /// `192.168.1.1:1900`. Aquí no hay IGD. Lo que se puede validar en esta máquina es el camino
    /// de fallo, no el de éxito, y decirlo por escrito vale más que un test que finge lo
    /// contrario.
    /// </summary>
    public static class SsdpProbe
    {
        public const string MulticastAddress = "239.255.255.250";
        public const int MulticastPort = 1900;

        /// Se pregunta por el dispositivo raíz de pasarela. `ssdp:all` traería impresoras y teles
        /// y multiplicaría las descargas de descripción sin aportar nada.
        public const string GatewaySearchTarget = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";

        private const int SearchAttempts = 3;
        private const int MillisecondsBetweenAttempts = 300;

        /// `MX` es el tope de segundos que un dispositivo puede esperar antes de contestar, y el
        /// RFC le pide que reparta al azar dentro de ese margen para no saturar la red. Ponerlo a
        /// 2 y escuchar 3 s deja hueco para esa espera.
        private const int MaxWaitSeconds = 2;

        /// <summary>
        /// Las `LOCATION` únicas que contestaron, en orden de llegada. Lista vacía = no hay IGD
        /// (o el firewall se comió las respuestas), que es una respuesta legítima y no un error.
        ///
        /// Nunca lanza: cualquier fallo de socket se convierte en lista vacía. Un usuario sin
        /// UPnP no puede quedarse sin hostear por una excepción.
        /// </summary>
        /// <param name="localAddress">
        /// La dirección de la interfaz por la que salir. Null deja elegir al sistema, que en una
        /// máquina con VPN suele elegir mal.
        /// </param>
        /// <param name="listenMilliseconds">Cuánto se escucha en total.</param>
        public static Task<List<Uri>> DiscoverAsync(string localAddress, int listenMilliseconds = 3000,
            CancellationToken cancellation = default)
        {
            return Task.Run(() => Discover(localAddress, listenMilliseconds, cancellation), cancellation);
        }

        /// <summary>Versión síncrona. Bloquea hasta <paramref name="listenMilliseconds"/>.</summary>
        public static List<Uri> Discover(string localAddress, int listenMilliseconds = 3000,
            CancellationToken cancellation = default)
        {
            var found = new List<Uri>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Socket socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                IPAddress bindTo = IPAddress.Any;
                if (!string.IsNullOrWhiteSpace(localAddress) &&
                    IPAddress.TryParse(localAddress.Trim(), out IPAddress parsed) &&
                    parsed.AddressFamily == AddressFamily.InterNetwork)
                {
                    bindTo = parsed;
                }

                socket.Bind(new IPEndPoint(bindTo, 0));

                // TTL 2: un salto para el router doméstico y otro de margen. Más sería mandar
                // descubrimiento a redes que no son la nuestra.
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                socket.ReceiveTimeout = 400;

                var target = new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort);
                byte[] request = BuildSearchRequest(GatewaySearchTarget);

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, listenMilliseconds));
                int attempts = 0;
                DateTime nextAttempt = DateTime.UtcNow;
                var buffer = new byte[8192];

                while (DateTime.UtcNow < deadline)
                {
                    if (cancellation.IsCancellationRequested) break;

                    if (attempts < SearchAttempts && DateTime.UtcNow >= nextAttempt)
                    {
                        attempts++;
                        nextAttempt = DateTime.UtcNow.AddMilliseconds(MillisecondsBetweenAttempts);
                        try
                        {
                            socket.SendTo(request, target);
                        }
                        catch (SocketException)
                        {
                            // Interfaz sin multicast. Se sigue escuchando por si otro envío entra.
                        }
                    }

                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int read;
                    try
                    {
                        read = socket.ReceiveFrom(buffer, ref from);
                    }
                    catch (SocketException)
                    {
                        // Timeout de recepción: es el latido normal de este bucle, no un fallo.
                        continue;
                    }

                    if (read <= 0) continue;

                    SsdpMessage message = SsdpMessage.Parse(
                        System.Text.Encoding.UTF8.GetString(buffer, 0, read));

                    if (!message.TryGetLocation(out Uri location)) continue;
                    if (!seen.Add(location.AbsoluteUri)) continue;

                    found.Add(location);
                }
            }
            catch (Exception)
            {
                // Sin red, socket bloqueado por una política, o multicast prohibido. Es un "no hay
                // UPnP", no un fallo del juego.
            }
            finally
            {
                try { socket?.Close(); }
                catch (Exception) { /* cerrar un socket ya muerto no es noticia */ }
            }

            return found;
        }

        /// <summary>
        /// El datagrama M-SEARCH. Los saltos de línea son `\r\n` **obligatoriamente** y la línea en
        /// blanco final también: hay firmware que descarta el mensaje sin ella y no contesta nada.
        /// </summary>
        public static byte[] BuildSearchRequest(string searchTarget)
        {
            string text =
                "M-SEARCH * HTTP/1.1\r\n" +
                $"HOST: {MulticastAddress}:{MulticastPort}\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                $"MX: {MaxWaitSeconds}\r\n" +
                $"ST: {searchTarget}\r\n" +
                "\r\n";
            return System.Text.Encoding.ASCII.GetBytes(text);
        }
    }
}
