using System.Collections.Generic;
using System.Net;

namespace BackroomsSurvival.Lobbies
{
    /// <summary>
    /// Qué dirección se puede ANUNCIAR como endpoint de una partida, y cuál no.
    ///
    /// Existe por un fallo medido el 2026-08-31: el host publicaba el contenido literal del campo
    /// de IP del panel, cuyo valor por defecto es `127.0.0.1`, así que el lobby anunciaba
    /// `connect_ip=127.0.0.1`. En la misma máquina eso funciona por casualidad —el backend hace
    /// bind en `0.0.0.0`, que cubre loopback— pero en **cualquier otro PC significa "yo mismo"**:
    /// el joiner dispara contra su propio loopback, nadie contesta, y el fallo llega 15 s después
    /// como diez `UDP recv error … (os error 10054)`, que es el sistema operativo diciendo "no hay
    /// nadie en el destino".
    ///
    /// **Publicar una dirección mala cuesta más que no publicar.** Quien elige ese lobby paga 15 s
    /// de espera y un mensaje que no le dice nada; un lobby que no aparece no le cuesta nada. Por
    /// eso <see cref="ResolvePublishableHost"/> devuelve null antes que adivinar.
    ///
    /// Sin Unity ni sockets dentro: los candidatos los trae el llamante, ya ordenados. Lo que se
    /// prueba aquí es la CLASIFICACIÓN y la PRECEDENCIA, que es lo que puede equivocarse.
    /// </summary>
    public static class LobbyEndpointPolicy
    {
        /// <summary>Por qué una dirección sirve o no como endpoint remoto.</summary>
        public enum HostAddressKind
        {
            /// Sirve: se puede anunciar.
            Usable,

            /// `127.0.0.0/8`, `::1` o el nombre `localhost`. En otra máquina significa esa otra
            /// máquina.
            Loopback,

            /// `0.0.0.0` o `::`. Es un bind, no un destino.
            Unspecified,

            /// APIPA `169.254.0.0/16` o `fe80::/10`. Es lo que Windows se inventa cuando una
            /// interfaz no tiene DHCP; no la enruta nadie. En esta máquina hay **seis**, así que
            /// "la primera IPv4 que no sea loopback" es un algoritmo que falla casi siempre.
            LinkLocal,

            /// Vacío o sólo espacios.
            Missing,
        }

        /// <summary>
        /// Clasifica una dirección. **Un nombre que no parsea como IP se considera utilizable a
        /// propósito**: puede ser un DNS dinámico que el humano escribió aposta, y rechazarlo
        /// sería decidir por él. La única excepción es `localhost`, que es loopback con otro
        /// nombre.
        /// </summary>
        public static HostAddressKind Classify(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return HostAddressKind.Missing;

            string trimmed = host.Trim();

            if (trimmed.Equals("localhost", System.StringComparison.OrdinalIgnoreCase))
                return HostAddressKind.Loopback;

            if (!IPAddress.TryParse(trimmed, out IPAddress ip))
                return HostAddressKind.Usable;

            if (IPAddress.IsLoopback(ip)) return HostAddressKind.Loopback;
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return HostAddressKind.Unspecified;
            if (ip.IsIPv6LinkLocal) return HostAddressKind.LinkLocal;

            byte[] bytes = ip.GetAddressBytes();
            if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254) return HostAddressKind.LinkLocal;

            return HostAddressKind.Usable;
        }

        public static bool IsPublishable(string host) => Classify(host) == HostAddressKind.Usable;

        /// <summary>
        /// La dirección que se anuncia, o null si no hay ninguna defendible.
        ///
        /// Precedencia: **manda lo que el humano escribió** si sirve —puede saber algo que
        /// nosotros no, como una IP pública con reenvío de puertos—; si no sirve, el primer
        /// candidato utilizable de la lista, que el llamante trae ya ordenada (la interfaz de la
        /// ruta por defecto primero); y si tampoco, null.
        /// </summary>
        /// <param name="field">Lo escrito en el campo de IP del panel.</param>
        /// <param name="candidates">Direcciones locales, en orden de preferencia. Puede ser null.</param>
        /// <param name="reason">Frase para el log: por qué salió lo que salió.</param>
        public static string ResolvePublishableHost(string field, IReadOnlyList<string> candidates, out string reason)
        {
            HostAddressKind fieldKind = Classify(field);
            if (fieldKind == HostAddressKind.Usable)
            {
                reason = "la IP escrita en el panel";
                return field.Trim();
            }

            if (candidates != null)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (!IsPublishable(candidates[i])) continue;

                    reason = $"la IP del campo no vale ({Describe(fieldKind)}); " +
                             $"se anuncia la dirección local {candidates[i]}";
                    return candidates[i].Trim();
                }
            }

            reason = $"la IP del campo no vale ({Describe(fieldKind)}) y no se encontró ninguna " +
                     "dirección local enrutable; NO se anuncia la partida";
            return null;
        }

        public static string Describe(HostAddressKind kind)
        {
            switch (kind)
            {
                case HostAddressKind.Loopback: return "es loopback, en otro PC significa ese otro PC";
                case HostAddressKind.Unspecified: return "es una dirección de bind, no un destino";
                case HostAddressKind.LinkLocal: return "es link-local/APIPA, no la enruta nadie";
                case HostAddressKind.Missing: return "está vacía";
                default: return "sirve";
            }
        }
    }
}
