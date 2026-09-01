using System;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// La higiene del campo «IP del host» del panel de conexión, en un sitio y sin dependencias
    /// de Unity para que se pueda probar.
    ///
    /// **Por qué existe.** Sesión física del 2026-08-31: el jugador PEGÓ la dirección en el campo
    /// y el texto llegó con un salto de línea al final. `JoinSessionUI` lo leía con
    /// `string.IsNullOrWhiteSpace(...) ? "127.0.0.1" : _ipField.text` —que DETECTA espacios pero
    /// no los quita— y `NetworkInitializer` lo interpolaba tal cual, así que al backend le llegó
    /// <c>CONNECT_TO=31.4.149.48\n:7778</c>.
    ///
    /// Eso no parsea. Y el daño no fue el rechazo, fue el silencio que venía después: sin
    /// dirección no hay <c>initiate_connection</c>, sin intento no arranca el presupuesto de
    /// <c>CONNECT_TIMEOUT</c>, y el backend se quedaba sirviendo un mundo en solitario sin
    /// decirle nada a nadie. El jugador veía «Joining…» 25 segundos y luego un
    /// «no session confirmation» que no señala a nada.
    ///
    /// El backend ya no se calla (ver <c>NetworkEvent::ConnectTargetInvalid</c>). Esto es la otra
    /// mitad: que el valor no salga roto de aquí, y que si no hay forma de arreglarlo se diga
    /// ANTES de lanzar un proceso condenado.
    ///
    /// La ruta del navegador de Steam nunca sufrió esto porque <c>Lobby.Sanitize</c> ya hacía
    /// <c>Trim()</c>. Esta clase le da a la ruta manual la misma higiene; no sustituye a aquélla
    /// —que además recorta longitudes de metadatos ajenos— sino que cubre el hueco que dejaba.
    /// </summary>
    public static class HostAddressInput
    {
        /// <summary>El destino de siempre cuando el campo está vacío.</summary>
        public const string DefaultHost = "127.0.0.1";

        /// <summary>
        /// El valor sin espacio en blanco alrededor. `Trim()` sin argumentos ya cubre espacio,
        /// tabulador, `\r` y `\n`, que son los cuatro que produce un pegado.
        ///
        /// NO valida: normalizar y aceptar son decisiones distintas, y hay un sitio (el log de
        /// diagnóstico) donde interesa el valor limpio aunque no sirva.
        /// </summary>
        public static string Normalize(string raw) => raw == null ? "" : raw.Trim();

        /// <summary>
        /// Lo mismo, pero un campo vacío cae al destino por defecto — que es lo que el panel ha
        /// hecho siempre y no se toca: escribir nada y darle a Join es «juega en local».
        /// </summary>
        public static string NormalizeOrDefault(string raw)
        {
            string trimmed = Normalize(raw);
            return trimmed.Length == 0 ? DefaultHost : trimmed;
        }

        /// <summary>
        /// Si esto puede ser el host de un destino. Devuelve false con un motivo redactado para
        /// enseñar, nunca solo false: el panel lo pinta tal cual.
        ///
        /// Se valida con <see cref="Uri.CheckHostName"/> y no con <c>IPAddress.TryParse</c>
        /// porque un nombre DNS —un host dinámico, que es como se hostea sin IP fija— es un
        /// destino perfectamente legítimo y `TryParse` lo rechazaría.
        /// </summary>
        public static bool IsUsable(string host, out string reason)
        {
            if (string.IsNullOrEmpty(host))
            {
                reason = "no hay dirección de host";
                return false;
            }

            // El error de dedo más probable, y tiene arreglo obvio: hay un campo aparte para el
            // puerto. Se comprueba antes que el resto para poder decir ESO en vez de un genérico.
            // Un IPv6 literal lleva dos puntos por todas partes y no es este caso.
            if (host.IndexOf(':') >= 0 && Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                reason = $"\"{host}\" lleva el puerto dentro: escribe solo la dirección aquí y " +
                         "el puerto en su propio campo";
                return false;
            }

            if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                // Entrecomillado a propósito: un motivo que diga «31.4.149.48 no vale» delante de
                // una dirección que se ve perfecta es peor que no decir nada. Así se ve el
                // espacio.
                reason = $"\"{host}\" no es una dirección ni un nombre de host válido";
                return false;
            }

            reason = null;
            return true;
        }
    }
}
