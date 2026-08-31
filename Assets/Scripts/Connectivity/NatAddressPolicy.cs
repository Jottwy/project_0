using System.Net;
using System.Net.Sockets;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>
    /// Si una dirección es alcanzable DESDE INTERNET, que es una pregunta distinta de la que
    /// contesta <see cref="Lobbies.LobbyEndpointPolicy"/>.
    ///
    /// Las dos conviven a propósito y no se pisan. `LobbyEndpointPolicy` decide qué se puede
    /// ANUNCIAR: una `192.168.1.40` sirve perfectamente para una partida en LAN y por eso allí es
    /// `Usable`. Aquí la pregunta es otra —"¿puede un desconocido de fuera llegar a esto?"— y la
    /// misma `192.168.1.40` es `PrivateRfc1918`, o sea que no. Fundir las dos clasificaciones en
    /// una sola habría obligado a elegir cuál de las dos respuestas es la buena, y las dos lo son.
    ///
    /// **`100.64.0.0/10` es la razón de que esta clase exista.** Es el rango que RFC 6598 reserva
    /// para el NAT del operador (CGNAT). Parece pública —no es ninguno de los rangos privados que
    /// todo el mundo reconoce— y no lo es: si la WAN del router está ahí, el operador comparte una
    /// IP pública entre cientos de abonados, no hay puerto que reenviar y **ninguna cantidad de
    /// UPnP lo arregla**. Detectarlo es la diferencia entre decírselo al usuario y dejarle
    /// media hora peleándose con la configuración de su router.
    ///
    /// Sin sockets, sin Unity, sin DNS: entra una cadena y sale una clasificación. Lo que puede
    /// equivocarse aquí son los límites de los rangos, y eso se prueba.
    /// </summary>
    public static class NatAddressPolicy
    {
        /// <summary>Qué clase de dirección es, desde el punto de vista de "¿me alcanzan de fuera?".</summary>
        public enum PublicAddressKind
        {
            /// Enrutable en internet. Es la única que permite que alguien de fuera se conecte.
            Public,

            /// `10/8`, `172.16/12`, `192.168/16` (RFC 1918) o `fc00::/7` (ULA). Vale para LAN y
            /// nada más. Como WAN de un router significa **doble NAT**.
            PrivateRfc1918,

            /// `100.64.0.0/10` (RFC 6598). El NAT del operador. Ver el comentario de la clase.
            CarrierGradeNat,

            /// `169.254/16` o `fe80::/10`. Lo que el sistema se inventa sin DHCP.
            LinkLocal,

            /// `127/8` o `::1`.
            Loopback,

            /// `0.0.0.0` o `::`. Es un bind, no un destino.
            Unspecified,

            /// Multicast, broadcast dirigido y `240/4`. No es el host de nadie.
            Reserved,

            /// Vacía o sólo espacios.
            Missing,

            /// No parsea como IP. **No se adivina**: un nombre DNS puede resolver a lo que sea, y
            /// esta clase no resuelve nombres a propósito (una consulta DNS es E/S, y esto es
            /// lógica pura). El llamante decide qué hacer con un nombre.
            NotAnIpLiteral,
        }

        /// <summary>Clasifica una dirección literal. Nunca lanza.</summary>
        public static PublicAddressKind Classify(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return PublicAddressKind.Missing;
            if (!IPAddress.TryParse(address.Trim(), out IPAddress ip)) return PublicAddressKind.NotAnIpLiteral;
            return Classify(ip);
        }

        /// <summary>Clasifica una <see cref="IPAddress"/> ya parseada.</summary>
        public static PublicAddressKind Classify(IPAddress ip)
        {
            if (ip == null) return PublicAddressKind.Missing;

            if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ClassifyV6(ip);
            if (ip.AddressFamily != AddressFamily.InterNetwork) return PublicAddressKind.Reserved;

            byte[] b = ip.GetAddressBytes();

            // El orden importa: `0.0.0.0` cae en `0/8` y `127.0.0.1` en `127/8`, así que los casos
            // exactos van antes que los rangos que los contienen.
            if (b[0] == 0) return PublicAddressKind.Unspecified;
            if (b[0] == 127) return PublicAddressKind.Loopback;
            if (b[0] == 169 && b[1] == 254) return PublicAddressKind.LinkLocal;

            if (b[0] == 10) return PublicAddressKind.PrivateRfc1918;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return PublicAddressKind.PrivateRfc1918;
            if (b[0] == 192 && b[1] == 168) return PublicAddressKind.PrivateRfc1918;

            // RFC 6598: 100.64.0.0/10 ⇒ el segundo octeto va de 64 a 127, no hasta 255. Escribir
            // `b[1] >= 64` a secas se tragaría 100.128–100.255, que SÍ son públicas.
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return PublicAddressKind.CarrierGradeNat;

            // 224/4 multicast, 240/4 reservado (255.255.255.255 incluido).
            if (b[0] >= 224) return PublicAddressKind.Reserved;

            return PublicAddressKind.Public;
        }

        private static PublicAddressKind ClassifyV6(IPAddress ip)
        {
            if (IPAddress.IPv6Loopback.Equals(ip)) return PublicAddressKind.Loopback;
            if (IPAddress.IPv6Any.Equals(ip)) return PublicAddressKind.Unspecified;
            if (ip.IsIPv6LinkLocal) return PublicAddressKind.LinkLocal;
            if (ip.IsIPv6Multicast) return PublicAddressKind.Reserved;

            byte[] b = ip.GetAddressBytes();

            // `fc00::/7` — direcciones locales únicas. Es el RFC 1918 de IPv6.
            if ((b[0] & 0xFE) == 0xFC) return PublicAddressKind.PrivateRfc1918;

            // `::ffff:a.b.c.d` — una IPv4 vestida de IPv6. Se clasifica por lo que de verdad es;
            // si no, un `::ffff:192.168.1.40` saldría "pública".
            if (ip.IsIPv4MappedToIPv6) return Classify(ip.MapToIPv4());

            return PublicAddressKind.Public;
        }

        /// <summary>True sólo si la dirección es alcanzable desde internet.</summary>
        public static bool IsPubliclyRoutable(string address) => Classify(address) == PublicAddressKind.Public;

        /// <summary>
        /// True si la dirección es de una LAN privada. Es lo que se espera ver en la interfaz
        /// local del host; **como WAN de un router es doble NAT**, que es un hallazgo.
        /// </summary>
        public static bool IsPrivateLan(string address) => Classify(address) == PublicAddressKind.PrivateRfc1918;

        /// <summary>Frase para el log y para la UI. En español porque la lee un humano.</summary>
        public static string Describe(PublicAddressKind kind)
        {
            switch (kind)
            {
                case PublicAddressKind.Public: return "pública y enrutable";
                case PublicAddressKind.PrivateRfc1918: return "privada (RFC 1918); como WAN significa doble NAT";
                case PublicAddressKind.CarrierGradeNat: return "CGNAT del operador (RFC 6598 100.64/10)";
                case PublicAddressKind.LinkLocal: return "link-local/APIPA, no la enruta nadie";
                case PublicAddressKind.Loopback: return "loopback, en otra máquina significa esa otra máquina";
                case PublicAddressKind.Unspecified: return "dirección de bind, no un destino";
                case PublicAddressKind.Reserved: return "reservada o multicast, no es el host de nadie";
                case PublicAddressKind.Missing: return "vacía";
                default: return "no es una IP literal";
            }
        }
    }
}
