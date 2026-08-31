using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>
    /// De "no sé si hay router UPnP" a un <see cref="IgdGateway"/> con el que hablar, o a un
    /// motivo por escrito de por qué no.
    ///
    /// Son tres pasos y cada uno falla distinto, así que el motivo se conserva entero:
    /// 1. **SSDP** — nadie contesta ⇒ no hay UPnP, o está apagado en el router, o el firewall se
    ///    comió las respuestas. Desde aquí no se pueden separar y no se finge que sí.
    /// 2. **Descarga de la descripción** — contestó pero su XML no se puede leer.
    /// 3. **Búsqueda del servicio WAN** — es UPnP y no es un router (impresora, tele, NAS).
    ///
    /// El primer dispositivo con servicio WAN gana. Con dos routers en la misma red la elección es
    /// arbitraria, y es un caso raro que no merece adivinación: quien tiene dos pasarelas tiene un
    /// doble NAT, y eso ya se detecta por la WAN privada.
    /// </summary>
    public static class IgdLocator
    {
        /// <summary>Lo que salió de buscar: la pasarela, o el motivo de que no haya.</summary>
        public sealed class Result
        {
            public IgdGateway Gateway { get; }

            /// El código que corresponde cuando no hay pasarela, o null si sí la hay.
            public ConnectivityCode? Code { get; }

            /// Frase para el log. Nunca null.
            public string Reason { get; }

            /// Cuántas `LOCATION` contestaron al SSDP. Cero separa "no hay UPnP en esta red" de
            /// "hay dispositivos UPnP pero ninguno es un router", que mandan a mirar sitios
            /// distintos.
            public int DevicesSeen { get; }

            private Result(IgdGateway gateway, ConnectivityCode? code, string reason, int devicesSeen)
            {
                Gateway = gateway;
                Code = code;
                Reason = reason;
                DevicesSeen = devicesSeen;
            }

            public static Result Found(IgdGateway gateway, int devicesSeen) =>
                new Result(gateway, null, $"IGD encontrado: {gateway.Service}", devicesSeen);

            public static Result NotFound(ConnectivityCode code, string reason, int devicesSeen) =>
                new Result(null, code, reason, devicesSeen);
        }

        /// <summary>
        /// Busca la pasarela. Nunca lanza y siempre termina dentro del presupuesto: hostear no
        /// puede depender de que el router de alguien conteste.
        /// </summary>
        /// <param name="localAddress">Interfaz por la que salir; ver <see cref="SsdpProbe"/>.</param>
        /// <param name="discoveryMilliseconds">Cuánto se escucha el SSDP.</param>
        /// <param name="httpTimeoutMs">Tope por descarga de descripción.</param>
        public static async Task<Result> FindGatewayAsync(string localAddress,
            int discoveryMilliseconds = 3000, int httpTimeoutMs = 4000,
            CancellationToken cancellation = default)
        {
            List<Uri> locations = await SsdpProbe
                .DiscoverAsync(localAddress, discoveryMilliseconds, cancellation).ConfigureAwait(false);

            if (locations.Count == 0)
            {
                return Result.NotFound(ConnectivityCode.UpnpUnavailable,
                    "ningún dispositivo contestó al SSDP: no hay UPnP en esta red, está apagado en " +
                    "el router, o el firewall descartó las respuestas", 0);
            }

            string lastProblem = null;

            foreach (Uri location in locations)
            {
                if (cancellation.IsCancellationRequested) break;

                string xml = await IgdGateway
                    .DownloadDescriptionAsync(location, httpTimeoutMs, cancellation).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(xml))
                {
                    lastProblem = $"{location} contestó al SSDP pero no sirvió su descripción";
                    continue;
                }

                IgdService service = IgdDescription.TryParse(xml, location, out string reason);
                if (service == null)
                {
                    lastProblem = $"{location}: {reason}";
                    continue;
                }

                return Result.Found(new IgdGateway(service, location), locations.Count);
            }

            return Result.NotFound(ConnectivityCode.UpnpUnavailable,
                lastProblem ?? "ninguno de los dispositivos UPnP de la red expone conexión WAN",
                locations.Count);
        }
    }
}
