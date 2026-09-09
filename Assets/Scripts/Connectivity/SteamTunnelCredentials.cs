using UnityEngine;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>
    /// El secreto de sesión de la vía Steam — ADR-135 D4'.
    ///
    /// **Lo genera el HOST**, viaja en la metadata de su lobby, y el joiner lo devuelve en el primer
    /// mensaje del túnel. Sirve para lo que tiene que servir: que el túnel **no sea un túnel
    /// abierto** — quien no ha visto el lobby no puede abrir sesión contra el host, así que el
    /// escaneo ciego no encuentra nada. Lo que NO es: una defensa contra quien sí ve el lobby, que
    /// es justo quien tiene derecho a entrar.
    ///
    /// Vive aparte de <see cref="RelaySessionCredentials"/> a propósito: son dos vías con vidas
    /// distintas, y compartir el secreto ataría la una a la otra sin ganar nada. ADR-117 no se toca.
    /// </summary>
    public static class SteamTunnelCredentials
    {
        private static string _secret;

        /// <summary>
        /// El secreto de esta sesión, creándolo la primera vez. Una sola vez por partida: dos
        /// llamadas seguidas devuelven el mismo.
        /// </summary>
        public static string Current()
        {
            if (_secret == null)
            {
                _secret = SteamTunnelAuth.NewSecret();
                // El VALOR no: eso es lo que hace que sea un secreto (D4'.6).
                Debug.Log("[SteamTunnel] Secreto de sesión nuevo para el túnel de Steam.");
            }

            return _secret;
        }

        /// <summary>Hay secreto creado ya. No lo crea.</summary>
        public static bool HasSecret => _secret != null;

        /// <summary>
        /// Tira el secreto. La llama el teardown de sesión: la partida siguiente estrena uno, o
        /// quien vio el lobby de ayer podría entrar hoy.
        /// </summary>
        public static void Reset() => _secret = null;
    }
}
