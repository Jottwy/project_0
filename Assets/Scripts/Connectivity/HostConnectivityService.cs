using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>Lo que se le permite hacer a la preparación de conectividad.</summary>
    public sealed class HostConnectivityOptions
    {
        /// <summary>Apaga UPnP entero. Se distingue de "no hay router" en el informe.</summary>
        public const string DisableUpnpVariable = "BS_NO_UPNP";

        public bool UpnpEnabled { get; set; } = true;
        public bool ExternalEchoEnabled { get; set; } = true;
        public int DiscoveryMilliseconds { get; set; } = 3000;
        public int HttpTimeoutMs { get; set; } = 3000;

        /// Los ecos de IP pública. Null usa los de producción; los tests inyectan loopback.
        public IReadOnlyList<string> EchoEndpoints { get; set; }

        /// <summary>Lee el entorno. Una variable puesta apaga; ausente o `0` deja encendido.</summary>
        public static HostConnectivityOptions FromEnvironment()
        {
            var options = new HostConnectivityOptions();
            try
            {
                string value = Environment.GetEnvironmentVariable(DisableUpnpVariable);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    value = value.Trim();
                    options.UpnpEnabled = value == "0" || string.Equals(value, "false",
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception)
            {
                // Un entorno que no deja leer variables no puede impedir hostear.
            }

            options.ExternalEchoEnabled = !PublicIpResolver.EchoDisabledByEnvironment();
            return options;
        }
    }

    /// <summary>
    /// La secuencia entera de preparar un host para que le puedan entrar desde fuera, y su
    /// limpieza. Es quien va rellenando <see cref="HostConnectivityReport"/>, y el único sitio
    /// donde se decide qué peldaño se enciende.
    ///
    /// El orden importa y no es el obvio:
    /// 1. **Buscar el IGD.** Sin él no hay ni mapeo ni forma de saber la WAN del router.
    /// 2. **La IP pública**, con el router primero. Va ANTES del mapeo porque su respuesta puede
    ///    cambiar el sentido de todo lo demás: si la WAN es `100.64.x.x`, el reenvío que se pida
    ///    después no le sirve a nadie, y decírselo al usuario vale más que el mapeo.
    /// 3. **El mapeo**, y **la relectura**. Son dos peldaños distintos porque son dos hechos
    ///    distintos: hay routers que aceptan el `AddPortMapping` y aplican otra cosa.
    ///
    /// **El mapeo se pide igualmente con CGNAT sospechado.** La heurística puede equivocarse, la
    /// llamada cuesta dos peticiones a la LAN, y un mapeo confirmado es evidencia que se guarda.
    /// Lo que NO cambia es lo que se anuncia: con CGNAT sospechado la IP pública no se publica.
    ///
    /// Nada de esto puede impedir crear la partida. Un fallo en cualquier paso es un código en el
    /// informe, y la sesión sigue sirviendo en LAN exactamente igual que antes.
    /// </summary>
    public sealed class HostConnectivityService
    {
        private readonly HostConnectivityOptions _options;
        private IgdGateway _gateway;
        private int _mappedPort;

        public HostConnectivityService(HostConnectivityOptions options = null)
        {
            _options = options ?? new HostConnectivityOptions();
        }

        /// <summary>El informe. Se puede leer mientras <see cref="PrepareAsync"/> corre.</summary>
        public HostConnectivityReport Report { get; } = new HostConnectivityReport();

        /// <summary>La pasarela encontrada, o null. Sólo para el log y para los tests.</summary>
        public IgdGateway Gateway => _gateway;

        /// <summary>
        /// Prepara el host. Nunca lanza.
        /// </summary>
        /// <param name="lanIp">Dirección de la interfaz de salida: es el cliente interno del mapeo.</param>
        /// <param name="port">El puerto UDP REALMENTE elegido, no el tecleado.</param>
        /// <param name="locator">
        /// Cómo encontrar la pasarela. Null usa el SSDP de verdad; los tests inyectan una
        /// pasarela de mentira para no depender de que haya un router en la red de quien corre la
        /// suite.
        /// </param>
        public async Task<HostConnectivityReport> PrepareAsync(string lanIp, int port,
            Func<Task<IgdLocator.Result>> locator = null, CancellationToken cancellation = default)
        {
            Report.SetLan(lanIp, port);

            _gateway = await LocateAsync(lanIp, locator, cancellation).ConfigureAwait(false);

            PublicIpAnswer answer = await PublicIpResolver.ResolveAsync(
                _gateway, _options.ExternalEchoEnabled, _options.HttpTimeoutMs, cancellation,
                _options.EchoEndpoints).ConfigureAwait(false);

            if (answer.Ip != null)
            {
                Report.SetPublicIp(answer.Ip, answer.Source);
            }
            else
            {
                Report.AddCode(ConnectivityCode.PublicEndpointUnknown);
            }

            CgnatHeuristic.Verdict verdict =
                CgnatHeuristic.Evaluate(answer.GatewayWanIp, answer.ObservedIp, out string cgnatReason);
            CgnatReason = cgnatReason;
            if (verdict == CgnatHeuristic.Verdict.Suspected) Report.AddCode(ConnectivityCode.CgnatSuspected);

            if (_gateway != null && lanIp != null && port > 0)
                await MapAsync(lanIp, port, cancellation).ConfigureAwait(false);

            return Report;
        }

        /// El motivo del veredicto de CGNAT, para el log. Null hasta que corre la preparación.
        public string CgnatReason { get; private set; }

        /// <summary>
        /// Retira el mapeo. La llama el teardown de la sesión. Idempotente y segura sin router:
        /// las rutas de teardown también corren cuando nunca hubo UPnP.
        ///
        /// Que falle no es grave —el mapeo caduca solo en una hora— pero se registra en el
        /// informe: un router que no deja borrar acumula entradas y acaba rechazando la siguiente
        /// por conflicto.
        /// </summary>
        public async Task<bool> ReleaseAsync(CancellationToken cancellation = default)
        {
            if (_gateway == null || _mappedPort <= 0)
            {
                Report.ClearMapping();
                return true;
            }

            IgdCallResult result = await _gateway
                .DeletePortMappingAsync(_mappedPort, _options.HttpTimeoutMs, cancellation)
                .ConfigureAwait(false);

            // Un `714` al borrar significa que ya no estaba, que es exactamente el estado que se
            // quería. No es un fallo.
            bool ok = result.Ok || IgdSoap.IsNoSuchEntry(result.ErrorCode);

            _mappedPort = 0;
            Report.ClearMapping();
            return ok;
        }

        private async Task<IgdGateway> LocateAsync(string lanIp, Func<Task<IgdLocator.Result>> locator,
            CancellationToken cancellation)
        {
            if (!_options.UpnpEnabled)
            {
                Report.AddCode(ConnectivityCode.UpnpDisabled);
                UpnpReason = "UPnP apagado por configuración";
                return null;
            }

            IgdLocator.Result result = locator != null
                ? await locator().ConfigureAwait(false)
                : await IgdLocator.FindGatewayAsync(lanIp, _options.DiscoveryMilliseconds,
                    _options.HttpTimeoutMs, cancellation).ConfigureAwait(false);

            UpnpReason = result?.Reason ?? "el buscador de IGD no devolvió nada";

            if (result?.Gateway == null)
            {
                Report.AddCode(result?.Code ?? ConnectivityCode.UpnpUnavailable);
                return null;
            }

            return result.Gateway;
        }

        /// Por qué hay o no hay pasarela, para el log. Null hasta que corre la preparación.
        public string UpnpReason { get; private set; }

        private async Task MapAsync(string lanIp, int port, CancellationToken cancellation)
        {
            Report.MarkMappingRequested();
            _mappedPort = port;

            IgdCallResult added = await _gateway
                .AddPortMappingAsync(port, port, lanIp, IgdGateway.DefaultLeaseSeconds,
                    _options.HttpTimeoutMs, cancellation)
                .ConfigureAwait(false);

            if (!added.Ok)
            {
                Report.AddCode(ConnectivityCode.UpnpMappingFailed);
                MappingReason = added.ErrorCode > 0
                    ? $"el router rechazó el mapeo: {IgdSoap.DescribeError(added.ErrorCode)}"
                    : $"no se pudo pedir el mapeo: {added.Error}";
                return;
            }

            // LA VERIFICACIÓN. Sin releerlo, "mapeo creado" es una suposición, y hay routers que
            // aceptan la petición y aplican otro cliente interno o la dejan deshabilitada.
            PortMappingEntry entry = await _gateway
                .GetSpecificPortMappingAsync(port, _options.HttpTimeoutMs, cancellation)
                .ConfigureAwait(false);

            if (entry == null)
            {
                Report.AddCode(ConnectivityCode.UpnpMappingFailed);
                MappingReason = "el router aceptó el mapeo pero al releerlo no había ninguno";
                return;
            }

            if (!entry.Matches(lanIp, port))
            {
                Report.AddCode(ConnectivityCode.UpnpMappingFailed);
                MappingReason = $"el router mapeó el puerto {port} a " +
                                $"{entry.InternalClient}:{entry.InternalPort} (habilitado={entry.Enabled}), " +
                                $"que no es este PC ({lanIp}:{port})";
                return;
            }

            Report.MarkMappingConfirmed();
            MappingReason = entry.LeaseSeconds > 0
                ? $"mapeo UDP {port} confirmado, caduca en {entry.LeaseSeconds} s"
                : $"mapeo UDP {port} confirmado, permanente";
        }

        /// Qué pasó con el mapeo, para el log. Null hasta que corre la preparación.
        public string MappingReason { get; private set; }
    }
}
