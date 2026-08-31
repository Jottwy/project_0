using System;
using System.Collections.Generic;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>
    /// Lectura de una respuesta SSDP. **Pura**: entra el texto de un datagrama y sale lo que dice.
    /// El socket vive en <see cref="SsdpProbe"/>.
    ///
    /// SSDP es HTTP escrito sobre UDP y no del todo bien. Lo que hay que aguantar, y por qué cada
    /// una es un caso real y no defensa gratuita:
    ///
    /// - **Las cabeceras no tienen mayúsculas fijas.** `LOCATION`, `Location` y `location` salen
    ///   de routers distintos. Comparar con `==` deja fuera a marcas enteras.
    /// - **El salto de línea a veces es `\n` y no `\r\n`.** El RFC dice `\r\n`; hay firmware que no.
    /// - **Hay basura después del cuerpo.** Un datagrama reutilizado de un búfer más grande trae
    ///   cola, así que la lectura no puede confiar en que el final del texto sea el final del
    ///   mensaje.
    ///
    /// Nada de esto se descubre leyendo el RFC: es lo que aparece cuando el código se pone delante
    /// de routers de verdad. Se escribe aquí para que la próxima persona no lo vuelva a deducir.
    /// </summary>
    public sealed class SsdpMessage
    {
        private readonly Dictionary<string, string> _headers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// La primera línea, tal cual (`HTTP/1.1 200 OK`, `NOTIFY * HTTP/1.1`, …).
        public string StartLine { get; private set; } = "";

        /// `LOCATION`: la URL del XML de descripción del dispositivo. Es lo único que de verdad
        /// hace falta de una respuesta SSDP.
        public string Location => Header("LOCATION");

        /// `ST` en una respuesta a M-SEARCH, `NT` en un anuncio espontáneo.
        public string SearchTarget => Header("ST") ?? Header("NT");

        /// `SERVER`: marca y firmware. No se usa para decidir nada; va al log, que es donde sirve
        /// cuando un usuario reporta que su router concreto no funciona.
        public string Server => Header("SERVER");

        public string Header(string name) => _headers.TryGetValue(name, out string value) ? value : null;

        /// <summary>
        /// Lee un datagrama. Nunca lanza: un mensaje ilegible se convierte en un objeto sin
        /// cabeceras, no en una excepción que hay que atrapar en el bucle de recepción.
        /// </summary>
        public static SsdpMessage Parse(string payload)
        {
            var message = new SsdpMessage();
            if (string.IsNullOrEmpty(payload)) return message;

            // Se corta en la línea en blanco: lo que venga después es cuerpo o cola del búfer, y
            // en SSDP no hay cuerpo.
            string[] lines = payload.Replace("\r\n", "\n").Split('\n');

            bool first = true;
            foreach (string raw in lines)
            {
                if (raw.Length == 0) break;

                if (first)
                {
                    message.StartLine = raw.Trim();
                    first = false;
                    continue;
                }

                int colon = raw.IndexOf(':');
                if (colon <= 0) continue;

                string name = raw.Substring(0, colon).Trim();
                string value = raw.Substring(colon + 1).Trim();
                if (name.Length == 0) continue;

                // La primera gana. Una cabecera repetida es un mensaje malformado y quedarse con
                // la última sería igual de arbitrario, pero la primera es la que el resto del
                // mundo usa.
                if (!message._headers.ContainsKey(name)) message._headers[name] = value;
            }

            return message;
        }

        /// <summary>
        /// La `LOCATION` como <see cref="Uri"/> absoluta, o null. Se exige **http** explícitamente:
        /// una `LOCATION` que apunte a `file://` o a un esquema raro es un dispositivo hostil o
        /// roto, y de ahí sale la URL a la que luego se hace una petición.
        /// </summary>
        public bool TryGetLocation(out Uri location)
        {
            location = null;
            string raw = Location;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;

            location = parsed;
            return true;
        }
    }
}
