using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>Resultado de una llamada SOAP. Nunca lanza: el fallo es un valor.</summary>
    public sealed class IgdCallResult
    {
        public bool Ok { get; private set; }

        /// El cuerpo de la respuesta, o null.
        public string Body { get; private set; }

        /// Código de error UPnP (`718`, `725`, …), o 0 si el fallo no fue del protocolo.
        public int ErrorCode { get; private set; }

        /// Frase para el log. Null cuando fue bien.
        public string Error { get; private set; }

        public static IgdCallResult Success(string body) => new IgdCallResult { Ok = true, Body = body };

        public static IgdCallResult Fault(int code, string description, string body) =>
            new IgdCallResult { Ok = false, ErrorCode = code, Error = description, Body = body };

        public static IgdCallResult Transport(string description) =>
            new IgdCallResult { Ok = false, Error = description };
    }

    /// <summary>Lo que el router dice tener mapeado en un puerto externo.</summary>
    public sealed class PortMappingEntry
    {
        public int InternalPort { get; set; }
        public string InternalClient { get; set; }
        public bool Enabled { get; set; }
        public string Description { get; set; }
        public int LeaseSeconds { get; set; }

        /// <summary>
        /// Si esta entrada es LA NUESTRA. Es la única prueba de que el mapeo existe de verdad:
        /// un `AddPortMapping` que devuelve 200 no demuestra nada, hay routers que aceptan y no
        /// aplican, y otros que aplican pero apuntando a otro cliente interno.
        /// </summary>
        public bool Matches(string internalClient, int internalPort)
        {
            if (!Enabled) return false;
            if (InternalPort != internalPort) return false;
            return string.Equals((InternalClient ?? "").Trim(), (internalClient ?? "").Trim(),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Habla SOAP con un router. Toda la E/S de UPnP está aquí; el formato está en
    /// <see cref="IgdSoap"/> y la clasificación de lo que sale en <see cref="NatAddressPolicy"/>.
    ///
    /// **Todos los tiempos son cortos y todos los fallos son valores.** Esto corre mientras el
    /// usuario espera a entrar en su partida: un router que no contesta no puede convertirse en
    /// diez segundos de pantalla parada, y desde luego no en una excepción que suba hasta la UI.
    /// </summary>
    public sealed class IgdGateway
    {
        private const string Protocol = "UDP";

        /// Caducidad que se pide. No es 0 (permanente) a propósito: si el juego se lleva un
        /// cierre sucio y no llega a borrar el mapeo, un permanente se queda en el router para
        /// siempre. Una hora se renueva sola mientras la partida dure y se evapora si no.
        /// Los routers que sólo admiten permanentes contestan `725` y se reintenta con 0.
        public const int DefaultLeaseSeconds = 3600;

        public IgdService Service { get; }

        /// De dónde salió la descripción. Sólo para el log, pero es lo primero que se pregunta
        /// cuando el UPnP de alguien no funciona.
        public Uri Location { get; }

        public IgdGateway(IgdService service, Uri location)
        {
            Service = service;
            Location = location;
        }

        /// <summary>
        /// La IP que el ROUTER cree que tiene en su lado WAN. Es la fuente de IP pública más
        /// fiable —no depende de ningún tercero— y la única con la que se puede oler un CGNAT:
        /// una `100.64.x.x` aquí es concluyente.
        /// </summary>
        public async Task<string> GetExternalIpAsync(int timeoutMs = 4000, CancellationToken cancellation = default)
        {
            IgdCallResult result = await InvokeAsync("GetExternalIPAddress", NoArguments, timeoutMs, cancellation)
                .ConfigureAwait(false);
            if (!result.Ok) return null;

            string value = IgdSoap.ReadValue(result.Body, "NewExternalIPAddress");
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>
        /// Pide el reenvío. **Devolver Ok NO significa que el mapeo exista**: hay que releerlo con
        /// <see cref="GetSpecificPortMappingAsync"/>, y por eso este método no toca ningún peldaño
        /// de la escalera.
        ///
        /// Reintenta una vez sin caducidad si el router contesta `725`, que es un router válido
        /// diciendo "yo sólo hago mapeos permanentes".
        /// </summary>
        public async Task<IgdCallResult> AddPortMappingAsync(int externalPort, int internalPort,
            string internalClient, int leaseSeconds = DefaultLeaseSeconds, int timeoutMs = 6000,
            CancellationToken cancellation = default)
        {
            IgdCallResult result = await InvokeAsync("AddPortMapping",
                MappingArguments(externalPort, internalPort, internalClient, leaseSeconds),
                timeoutMs, cancellation).ConfigureAwait(false);

            if (result.Ok || !IgdSoap.DemandsPermanentLease(result.ErrorCode) || leaseSeconds == 0)
                return result;

            return await InvokeAsync("AddPortMapping",
                MappingArguments(externalPort, internalPort, internalClient, 0),
                timeoutMs, cancellation).ConfigureAwait(false);
        }

        /// <summary>
        /// Relee lo que el router tiene en ese puerto externo. **Ésta es la verificación**: sin
        /// ella "mapeo creado" es una suposición.
        ///
        /// Devuelve null tanto si no hay entrada (`714`) como si la llamada falló. Distinguirlo no
        /// aporta: en los dos casos el mapeo no está confirmado.
        /// </summary>
        public async Task<PortMappingEntry> GetSpecificPortMappingAsync(int externalPort,
            int timeoutMs = 4000, CancellationToken cancellation = default)
        {
            var arguments = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("NewRemoteHost", ""),
                new KeyValuePair<string, string>("NewExternalPort", externalPort.ToString()),
                new KeyValuePair<string, string>("NewProtocol", Protocol),
            };

            IgdCallResult result = await InvokeAsync("GetSpecificPortMappingEntry", arguments, timeoutMs, cancellation)
                .ConfigureAwait(false);
            if (!result.Ok) return null;

            return ReadMappingEntry(result.Body);
        }

        /// <summary>
        /// Retira el mapeo. La llama el teardown de la sesión. Que falle no es grave —el mapeo
        /// caduca solo en una hora— pero se registra: un router que no deja borrar acumula
        /// entradas y acaba rechazando la siguiente por conflicto.
        /// </summary>
        public Task<IgdCallResult> DeletePortMappingAsync(int externalPort, int timeoutMs = 4000,
            CancellationToken cancellation = default)
        {
            var arguments = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("NewRemoteHost", ""),
                new KeyValuePair<string, string>("NewExternalPort", externalPort.ToString()),
                new KeyValuePair<string, string>("NewProtocol", Protocol),
            };

            return InvokeAsync("DeletePortMapping", arguments, timeoutMs, cancellation);
        }

        /// <summary>Lee la respuesta de `GetSpecificPortMappingEntry`. Pura y por eso comprobable.</summary>
        public static PortMappingEntry ReadMappingEntry(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;

            string client = IgdSoap.ReadValue(body, "NewInternalClient");
            string port = IgdSoap.ReadValue(body, "NewInternalPort");
            if (string.IsNullOrWhiteSpace(client) || !int.TryParse((port ?? "").Trim(), out int internalPort))
                return null;

            string enabled = (IgdSoap.ReadValue(body, "NewEnabled") ?? "1").Trim();
            int.TryParse((IgdSoap.ReadValue(body, "NewLeaseDuration") ?? "0").Trim(), out int lease);

            return new PortMappingEntry
            {
                InternalClient = client.Trim(),
                InternalPort = internalPort,
                // Los routers escriben `1`/`0`, pero también se ha visto `true`. Se acepta todo lo
                // que no sea explícitamente falso.
                Enabled = !(enabled == "0" || string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase)),
                Description = IgdSoap.ReadValue(body, "NewPortMappingDescription"),
                LeaseSeconds = lease,
            };
        }

        private static readonly List<KeyValuePair<string, string>> NoArguments =
            new List<KeyValuePair<string, string>>();

        private static List<KeyValuePair<string, string>> MappingArguments(int externalPort, int internalPort,
            string internalClient, int leaseSeconds)
        {
            // EL ORDEN ES EL DEL SCPD y no se toca: UPnP define los argumentos posicionales aunque
            // se escriban con nombre, y hay routers que devuelven `402 InvalidArgs` si se cambia.
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("NewRemoteHost", ""),
                new KeyValuePair<string, string>("NewExternalPort", externalPort.ToString()),
                new KeyValuePair<string, string>("NewProtocol", Protocol),
                new KeyValuePair<string, string>("NewInternalPort", internalPort.ToString()),
                new KeyValuePair<string, string>("NewInternalClient", internalClient ?? ""),
                new KeyValuePair<string, string>("NewEnabled", "1"),
                new KeyValuePair<string, string>("NewPortMappingDescription", IgdSoap.MappingDescription),
                new KeyValuePair<string, string>("NewLeaseDuration", leaseSeconds.ToString()),
            };
        }

        private Task<IgdCallResult> InvokeAsync(string action, IReadOnlyList<KeyValuePair<string, string>> arguments,
            int timeoutMs, CancellationToken cancellation)
        {
            string envelope = IgdSoap.BuildEnvelope(Service.ServiceType, action, arguments);
            string soapAction = IgdSoap.ActionHeader(Service.ServiceType, action);
            Uri url = Service.ControlUrl;

            // `HttpWebRequest.Timeout` sólo lo respeta la ruta SÍNCRONA, así que la llamada va a un
            // hilo del pool en vez de usar la API asíncrona. Es la diferencia entre un tope real y
            // un tope decorativo: sin él, un router que abre la conexión y no contesta deja la
            // pantalla de host parada hasta que el sistema se rinda.
            return Task.Run(() => Invoke(url, soapAction, envelope, timeoutMs), cancellation);
        }

        private static IgdCallResult Invoke(Uri url, string soapAction, string envelope, int timeoutMs)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "text/xml; charset=\"utf-8\"";
                request.Headers.Add("SOAPAction", soapAction);
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.KeepAlive = false;
                // Un proxy de sistema apuntando a internet rompe una llamada a la LAN, y en Windows
                // se hereda sin que nadie lo pida.
                request.Proxy = null;

                byte[] payload = Encoding.UTF8.GetBytes(envelope);
                request.ContentLength = payload.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(payload, 0, payload.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return IgdCallResult.Success(ReadBody(response));
                }
            }
            catch (WebException e)
            {
                // Un fallo SOAP llega como HTTP 500 CON CUERPO, y el cuerpo es justo lo que hace
                // falta: sin leerlo, un `725` (este router sólo hace mapeos permanentes, reintenta)
                // sería indistinguible de "el router no contesta".
                if (e.Response is HttpWebResponse response)
                {
                    string body = ReadBody(response);
                    if (IgdSoap.TryReadFault(body, out int code, out string description))
                        return IgdCallResult.Fault(code, description, body);

                    return IgdCallResult.Transport($"HTTP {(int)response.StatusCode} sin fallo UPnP legible");
                }

                return IgdCallResult.Transport($"{e.Status}: {e.Message}");
            }
            catch (Exception e)
            {
                return IgdCallResult.Transport($"{e.GetType().Name}: {e.Message}");
            }
        }

        private static string ReadBody(HttpWebResponse response)
        {
            try
            {
                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null) return "";
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// Descarga la descripción de un dispositivo. Aparte del resto porque es un GET normal, no
        /// SOAP, y porque su fallo tiene otro significado: aquí un fallo quiere decir "esa
        /// `LOCATION` no sirve", no "el router rechazó la acción".
        /// </summary>
        public static Task<string> DownloadDescriptionAsync(Uri url, int timeoutMs = 4000,
            CancellationToken cancellation = default)
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
                    request.Proxy = null;

                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        return ReadBody(response);
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }, cancellation);
        }
    }
}
