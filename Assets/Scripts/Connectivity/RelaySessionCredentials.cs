using System;
using System.Security.Cryptography;
using BackroomsSurvival.Lobbies;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>
    /// El identificador y el secreto de la sesión de relay del host — ADR-117 D9.
    ///
    /// **Los genera el HOST, no el relay.** Es lo que permite que el lobby se publique con ellos
    /// antes de que el relay conteste, y sobre todo lo que hace que el relay no tenga que repartir
    /// secretos a quien se los pida: quien no ha visto el lobby no tiene el token, y sin token no
    /// se crea ni se entra en ninguna sesión.
    ///
    /// Una por sesión de juego. <see cref="Reset"/> la tira al terminar, así que la siguiente
    /// partida no hereda el secreto de la anterior — un token reutilizado dejaría entrar a quien
    /// vio el lobby de ayer.
    /// </summary>
    public static class RelaySessionCredentials
    {
        /// <summary>Dirección del relay para esta build. Vacía = sin relay (ADR-117 opcional).</summary>
        public const string RelayAddressVariable = "BS_RELAY_ADDR";

        /// <summary>
        /// El relay por defecto. **Vacío a propósito hasta que exista la máquina**: una dirección
        /// inventada aquí haría que cada host gastara los 10 s del registro contra un puerto que no
        /// existe, en cada partida, para nada. Cuando haya VPS, se pone aquí (o por la variable).
        /// </summary>
        public const string DefaultRelayAddress = "";

        private static string _session;
        private static string _token;

        /// <summary>La dirección del relay configurada, o null si esta build no tiene.</summary>
        public static string RelayAddress
        {
            get
            {
                try
                {
                    string fromEnv = Environment.GetEnvironmentVariable(RelayAddressVariable);
                    if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
                }
                catch (Exception)
                {
                    // Un entorno que no deja leer variables no puede impedir hostear.
                }

                return string.IsNullOrWhiteSpace(DefaultRelayAddress) ? null : DefaultRelayAddress;
            }
        }

        /// <summary>Hay relay configurado para esta build.</summary>
        public static bool IsConfigured => RelayAddress != null;

        /// <summary>
        /// Las credenciales de la sesión actual, creándolas la primera vez. Devuelve
        /// <see cref="LobbyRelay.None"/> si esta build no tiene relay.
        /// </summary>
        public static LobbyRelay Current()
        {
            string address = RelayAddress;
            if (address == null) return LobbyRelay.None;

            if (_session == null || _token == null)
            {
                _session = NewSessionId();
                _token = NewToken();
                // El id sí; el token NUNCA (D9).
                UnityEngine.Debug.Log($"[Relay] Sesión nueva {_session} para el relay {address}.");
            }

            return new LobbyRelay(address, _session, _token);
        }

        /// <summary>
        /// Tira las credenciales. La llama el teardown de sesión: la partida siguiente estrena
        /// secreto, o quien vio el lobby de la anterior podría entrar en la nueva.
        /// </summary>
        public static void Reset()
        {
            _session = null;
            _token = null;
        }

        /// <summary>
        /// Un id de sesión de 64 bits, en decimal sin signo — el formato que el backend parsea.
        ///
        /// Aleatorio y no un contador: dos hosts distintos no pueden colisionar en el mismo relay,
        /// y con 64 bits la probabilidad es despreciable aunque cada partida del mundo pidiera uno.
        /// </summary>
        private static string NewSessionId()
        {
            var bytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToUInt64(bytes, 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 16 bytes en hexadecimal, de un generador CRIPTOGRÁFICO.
        ///
        /// `System.Random` no vale aquí y la diferencia no es teórica: se siembra con el reloj, así
        /// que dos partidas arrancadas en el mismo milisegundo sacarían el mismo token, y sobre
        /// todo cualquiera que sepa cuándo empezó una partida puede reproducir su secreto.
        /// </summary>
        private static string NewToken()
        {
            var bytes = new byte[LobbyRelay.TokenLength / 2];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);

            var hex = new System.Text.StringBuilder(LobbyRelay.TokenLength);
            foreach (byte b in bytes) hex.Append(b.ToString("x2"));
            return hex.ToString();
        }
    }
}
