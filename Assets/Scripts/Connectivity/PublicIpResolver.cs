using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>Lo que se ha averiguado sobre la IP pública, con sus dos fuentes por separado.</summary>
    public sealed class PublicIpAnswer
    {
        /// La IP pública que se va a usar, o null. Sólo se rellena con una dirección
        /// **públicamente enrutable**: la respuesta cruda del router puede ser una `100.64.x.x` y
        /// eso no es "la IP pública", es descubrir que no hay ninguna.
        public string Ip { get; set; }

        public PublicIpSource Source { get; set; } = PublicIpSource.None;

        /// La respuesta CRUDA de `GetExternalIPAddress`, enrutable o no. Se guarda tal cual porque
        /// es la mitad del juicio de <see cref="CgnatHeuristic"/>.
        public string GatewayWanIp { get; set; }

        /// La respuesta CRUDA del eco externo. La otra mitad del juicio.
        public string ObservedIp { get; set; }

        /// Frase para el log. Nunca null.
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// Averigua la IP pública del host. Dos fuentes, y **el orden no es arbitrario**.
    ///
    /// **Primero el router** (`GetExternalIPAddress`). Es la más fiable por tres razones: no
    /// depende de que ningún tercero esté vivo, no le cuenta a nadie de fuera que este PC existe,
    /// y sobre todo es la única que puede delatar un CGNAT — un eco HTTP le contesta una IP
    /// pública perfectamente normal a una máquina que está detrás de un NAT de operador, así que
    /// por sí solo es justo el dato que engaña.
    ///
    /// **Después los ecos HTTP**, y son VARIOS a propósito: depender de un solo servicio externo
    /// significa que el día que ese servicio se cae, nadie puede anunciar partida. Se prueban en
    /// orden y gana el primero que conteste algo válido.
    ///
    /// **Qué sale de esta máquina.** Una petición GET sin cuerpo ni cabeceras propias. El servicio
    /// ve la IP del que le pregunta —que es lo que se le está preguntando— y nada más: ni nombre,
    /// ni identificador de jugador, ni de partida. Se puede apagar entero con
    /// <c>BS_NO_PUBLIC_IP_LOOKUP=1</c>, y apagado se sigue pudiendo hostear: el IGD sigue
    /// contestando y la LAN no se toca.
    ///
    /// Nada de esto puede impedir crear la partida. Todos los fallos son valores, todos los topes
    /// de tiempo son cortos, y el peor caso es no saber la IP pública.
    /// </summary>
    public static class PublicIpResolver
    {
        /// <summary>Variable de entorno que apaga la consulta a servicios de terceros.</summary>
        public const string DisableEchoVariable = "BS_NO_PUBLIC_IP_LOOKUP";

        /// <summary>
        /// Los ecos, en orden. Tres y de dueños distintos: uno caído no puede dejar sin anunciar
        /// a nadie. Todos devuelven la IP en texto plano y nada más, que es lo que hace que el
        /// parseo pueda ser estricto.
        /// </summary>
        public static readonly string[] EchoEndpoints =
        {
            "https://api.ipify.org",
            "https://checkip.amazonaws.com",
            "https://icanhazip.com",
        };

        /// Tope de bytes que se leen de un eco. Una respuesta legítima son 15 bytes; cualquier
        /// cosa más grande es un portal cautivo devolviendo HTML, o algo peor.
        private const int MaxEchoBytes = 128;

        /// <summary>
        /// Resuelve. Nunca lanza.
        /// </summary>
        /// <param name="gateway">La pasarela, o null si no hay IGD.</param>
        /// <param name="allowExternalEcho">
        /// False salta los servicios de terceros. La variable de entorno también lo apaga.
        /// </param>
        /// <param name="timeoutMs">Tope por consulta, no total.</param>
        /// <param name="endpoints">
        /// Los ecos a consultar. Null usa los de producción; los tests pasan un servidor de
        /// mentira en loopback para no depender de internet.
        /// </param>
        public static async Task<PublicIpAnswer> ResolveAsync(IgdGateway gateway, bool allowExternalEcho = true,
            int timeoutMs = 2000, CancellationToken cancellation = default,
            System.Collections.Generic.IReadOnlyList<string> endpoints = null)
        {
            var answer = new PublicIpAnswer();

            if (gateway != null)
            {
                answer.GatewayWanIp = await gateway.GetExternalIpAsync(timeoutMs, cancellation).ConfigureAwait(false);

                if (NatAddressPolicy.IsPubliclyRoutable(answer.GatewayWanIp))
                {
                    answer.Ip = answer.GatewayWanIp.Trim();
                    answer.Source = PublicIpSource.InternetGatewayDevice;
                    answer.Reason = "la WAN que declara el router";
                }
            }

            bool echoAllowed = allowExternalEcho && !EchoDisabledByEnvironment();

            // El eco se consulta AUNQUE el router ya haya dado una IP buena: las dos respuestas
            // juntas son lo que permite juzgar el CGNAT, y una sola nunca basta.
            if (echoAllowed)
            {
                answer.ObservedIp = await QueryEchoesAsync(endpoints ?? EchoEndpoints, timeoutMs, cancellation)
                    .ConfigureAwait(false);

                if (answer.Ip == null && NatAddressPolicy.IsPubliclyRoutable(answer.ObservedIp))
                {
                    answer.Ip = answer.ObservedIp.Trim();
                    answer.Source = PublicIpSource.ExternalEcho;
                    answer.Reason = "un servicio externo de eco (el router no la dio)";
                }
            }

            if (answer.Ip == null)
            {
                answer.Reason = BuildFailureReason(gateway, echoAllowed, answer);
            }

            return answer;
        }

        /// <summary>
        /// Pregunta a los ecos de producción. Ver la sobrecarga con lista explícita.
        /// </summary>
        public static Task<string> QueryEchoesAsync(int timeoutMs, CancellationToken cancellation = default) =>
            QueryEchoesAsync(EchoEndpoints, timeoutMs, cancellation);

        /// <summary>
        /// Pregunta a los ecos en orden y devuelve el primero válido, o null. Cada uno con su
        /// propio tope de tiempo: el presupuesto es por consulta y no total, porque tres servicios
        /// caídos son tres fallos rápidos, no una espera larga.
        ///
        /// La lista entra por parámetro para que los tests puedan apuntar a un servidor de
        /// mentira en loopback. **Un test que llame a los ecos de verdad no es un test**: falla
        /// cuando falla la red de otro, y tarda lo que tarde internet.
        /// </summary>
        public static async Task<string> QueryEchoesAsync(System.Collections.Generic.IReadOnlyList<string> endpoints,
            int timeoutMs, CancellationToken cancellation = default)
        {
            if (endpoints == null) return null;

            foreach (string endpoint in endpoints)
            {
                if (cancellation.IsCancellationRequested) return null;

                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri url)) continue;

                string body = await GetTextAsync(url, timeoutMs, cancellation).ConfigureAwait(false);
                string ip = ParseEchoResponse(body);
                if (ip != null) return ip;
            }

            return null;
        }

        /// <summary>
        /// Lee la respuesta de un eco. **Estricta a propósito**: estos servicios devuelven la IP en
        /// texto plano y nada más, así que cualquier otra cosa —HTML de un portal cautivo, una
        /// página de error, una respuesta enorme— no es una IP y se descarta. Una validación
        /// laxa aquí acabaría publicando en el lobby una cadena que no lleva a ninguna parte.
        ///
        /// Sólo IPv4: el socket del backend hace bind en `0.0.0.0`, o sea que una IPv6 pública
        /// sería una dirección correcta a la que este juego no escucha. `icanhazip` devuelve IPv6
        /// cuando la hay, así que el caso es real.
        ///
        /// Pura: es lo único de esta clase que se puede probar sin salir a internet, y es donde
        /// está el riesgo.
        /// </summary>
        public static string ParseEchoResponse(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            if (body.Length > MaxEchoBytes) return null;

            string trimmed = body.Trim();

            // CUATRO octetos, exactamente. `IPAddress.TryParse` acepta la forma abreviada clásica
            // —`8.8` es `8.0.0.8` y `192.168.1` es `192.168.0.1`— así que una respuesta TRUNCADA a
            // medio camino se convierte en una IP pública perfectamente válida, y ésa es la que
            // acabaría en `connect_ip` del lobby. Medido, no supuesto: salió como un rojo al
            // ejecutar los tests de clasificación.
            if (trimmed.Split('.').Length != 4) return null;

            if (!IPAddress.TryParse(trimmed, out IPAddress parsed)) return null;
            if (parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
            if (!NatAddressPolicy.IsPubliclyRoutable(trimmed)) return null;

            return trimmed;
        }

        /// <summary>True si el entorno pide no hablar con terceros.</summary>
        public static bool EchoDisabledByEnvironment()
        {
            try
            {
                string value = Environment.GetEnvironmentVariable(DisableEchoVariable);
                if (string.IsNullOrWhiteSpace(value)) return false;
                value = value.Trim();
                return value != "0" && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // Un entorno que no deja leer variables no puede impedir hostear.
                return false;
            }
        }

        private static string BuildFailureReason(IgdGateway gateway, bool echoAllowed, PublicIpAnswer answer)
        {
            if (gateway != null && answer.GatewayWanIp != null)
            {
                return $"el router dice que su WAN es {answer.GatewayWanIp}, que " +
                       $"{NatAddressPolicy.Describe(NatAddressPolicy.Classify(answer.GatewayWanIp))}";
            }

            if (!echoAllowed)
            {
                return gateway == null
                    ? "no hay IGD y la consulta a servicios externos está apagada"
                    : "el router no contestó y la consulta a servicios externos está apagada";
            }

            return "ninguna fuente pudo decir cuál es la IP pública";
        }

        /// <summary>
        /// GET de texto con tope de tiempo y de tamaño. Va a un hilo del pool por la misma razón
        /// que las llamadas SOAP: <c>HttpWebRequest.Timeout</c> sólo lo respeta la ruta síncrona,
        /// y un servicio que acepta la conexión y se calla dejaría la creación de partida colgada.
        /// </summary>
        private static Task<string> GetTextAsync(Uri url, int timeoutMs, CancellationToken cancellation)
        {
            return Task.Run(() =>
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.Method = "GET";
                    request.Timeout = timeoutMs;
                    request.ReadWriteTimeout = timeoutMs;
                    request.KeepAlive = false;
                    // Nada que identifique a nadie. Lo único que este servicio va a ver es la
                    // dirección desde la que se le pregunta, que es lo que se le pregunta.
                    request.UserAgent = "BackroomsSurvival";

                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                    {
                        if (stream == null) return null;

                        var buffer = new byte[MaxEchoBytes];
                        int total = 0;
                        while (total < buffer.Length)
                        {
                            int read = stream.Read(buffer, total, buffer.Length - total);
                            if (read <= 0) break;
                            total += read;
                        }

                        return Encoding.ASCII.GetString(buffer, 0, total);
                    }
                }
                catch (Exception)
                {
                    // Sin internet, DNS caído, servicio retirado, proxy corporativo. Ninguna de las
                    // cuatro puede impedir crear una partida en LAN.
                    return null;
                }
            }, cancellation);
        }
    }
}
