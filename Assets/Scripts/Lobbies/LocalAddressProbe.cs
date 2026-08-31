using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace BackroomsSurvival.Lobbies
{
    /// <summary>
    /// Descubre la dirección local con la que esta máquina saldría a la red, en orden de
    /// preferencia. La política de qué se puede publicar vive aparte, en
    /// <see cref="LobbyEndpointPolicy"/>; aquí sólo se mira el sistema.
    ///
    /// **Por qué no vale "la primera IPv4 que no sea loopback".** En la máquina donde se encontró
    /// el fallo hay **diez** IPv4: la buena (`192.168.1.40`, Ethernet/DHCP), dos de VPN
    /// (`10.5.0.2` de NordLynx y `26.213.115.149` de Radmin) y **seis APIPA `169.254.x.x`**.
    /// Publicar una APIPA le falla al joiner exactamente igual de silencioso que publicar
    /// `127.0.0.1`: 15 s y el mismo `os error 10054`.
    ///
    /// **Cómo se elige.** Se abre un socket UDP y se hace `Connect` a una dirección de
    /// documentación. En UDP eso **no envía un solo byte**: sólo obliga al sistema a resolver la
    /// ruta y a asignar la interfaz de salida, que se lee con `LocalEndPoint`. Es la única forma
    /// portable de preguntar "¿por dónde salgo yo?" sin depender de nombres de interfaz ni de
    /// métricas de ruta. Si falla, se cae a enumerar las interfaces.
    ///
    /// Limitación conocida y anotada: con una VPN levantada, la ruta por defecto es la de la VPN,
    /// así que eso es lo que se publica. Es la respuesta correcta a la pregunta que se hace (por
    /// dónde salgo), y puede no ser la que el humano quiere; para eso el campo del panel gana
    /// (ver la precedencia de <see cref="LobbyEndpointPolicy.ResolvePublishableHost"/>).
    /// </summary>
    public static class LocalAddressProbe
    {
        /// TEST-NET-3 (RFC 5737), reservada para documentación. Se elige a propósito una que no es
        /// de nadie: no se le manda nada, pero un lector del código no tiene por qué fiarse de esa
        /// frase, y con esta dirección tampoco importaría.
        private const string RouteProbeAddress = "203.0.113.1";

        /// Puerto `discard` (RFC 863). Da igual: no se transmite.
        private const int RouteProbePort = 9;

        /// <summary>
        /// Direcciones locales candidatas, la de la ruta por defecto primero. Nunca lanza: un
        /// sistema sin red devuelve lista vacía, y eso es una respuesta.
        /// </summary>
        public static List<string> Candidates()
        {
            var found = new List<string>();

            string viaRoute = ViaDefaultRoute();
            if (viaRoute != null) found.Add(viaRoute);

            foreach (string address in ViaHostEntry())
            {
                if (!found.Contains(address)) found.Add(address);
            }

            return found;
        }

        /// <summary>
        /// La dirección de la interfaz que lleva la ruta por defecto, o null. `Connect` sobre un
        /// socket UDP no transmite: sólo fija destino y ruta.
        /// </summary>
        public static string ViaDefaultRoute()
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(IPAddress.Parse(RouteProbeAddress), RouteProbePort);
                    if (!(socket.LocalEndPoint is IPEndPoint local)) return null;
                    string address = local.Address.ToString();
                    return LobbyEndpointPolicy.IsPublishable(address) ? address : null;
                }
            }
            catch (Exception)
            {
                // Sin ruta por defecto, sin red, o el socket bloqueado. No es un error del juego:
                // se cae al enumerado.
                return null;
            }
        }

        /// <summary>Respaldo: las IPv4 del nombre de la máquina, filtradas por la política.</summary>
        public static List<string> ViaHostEntry()
        {
            var found = new List<string>();
            try
            {
                IPHostEntry entry = Dns.GetHostEntry(Dns.GetHostName());
                foreach (IPAddress ip in entry.AddressList)
                {
                    if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                    string address = ip.ToString();
                    if (!LobbyEndpointPolicy.IsPublishable(address)) continue;
                    if (!found.Contains(address)) found.Add(address);
                }
            }
            catch (Exception)
            {
                // Un DNS que no contesta no puede impedir hostear.
            }

            return found;
        }
    }
}
