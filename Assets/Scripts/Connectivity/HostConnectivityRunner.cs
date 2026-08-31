using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>
    /// El único punto desde el que el juego arranca y para la preparación de conectividad.
    ///
    /// **Todo lo caro ocurre fuera del hilo principal y nada de esto se consulta en un `Update`.**
    /// El descubrimiento SSDP escucha tres segundos y cada llamada SOAP abre un socket; hacerlo
    /// por frame sería reventar el frame time, que es exactamente el error que ya se pagó una vez
    /// con la resolución de direcciones locales (de ahí su caché de 10 s en
    /// <c>ServerBrowserBootstrap</c>). Aquí el patrón es otro y más simple: se lanza UNA vez al
    /// hostear, y quien quiera saber algo lee <see cref="Latest"/>, que es un objeto en memoria.
    ///
    /// Estado estático a propósito y no un MonoBehaviour: el ciclo de vida que le corresponde es
    /// el de la SESIÓN, no el de una escena, y las escenas se cargan y descargan en medio.
    /// </summary>
    public static class HostConnectivityRunner
    {
        private static HostConnectivityService _service;
        private static CancellationTokenSource _cancellation;
        private static Task _running;
        private static string _startedFor;

        /// <summary>
        /// Lo que se sabe ahora mismo. Nunca null: antes de empezar es un informe vacío, que dice
        /// exactamente lo que se sabe, o sea nada.
        /// </summary>
        public static HostConnectivityReport Latest { get; private set; } = new HostConnectivityReport();

        /// <summary>La preparación está en marcha.</summary>
        public static bool IsRunning => _running != null && !_running.IsCompleted;

        /// <summary>Ha terminado y el informe es definitivo.</summary>
        public static bool IsComplete => _running != null && _running.IsCompleted;

        /// <summary>
        /// Arranca la preparación para este host. **Idempotente por destino**: llamarlo otra vez
        /// con el mismo `lanIp:port` no relanza nada. Lo llama el latido del anuncio, que corre
        /// a 60 Hz, así que tiene que ser barato y no acumular tareas.
        /// </summary>
        public static void BeginForHost(string lanIp, int port, HostConnectivityOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(lanIp) || port <= 0) return;

            string key = lanIp + ":" + port;
            if (string.Equals(key, _startedFor, StringComparison.Ordinal)) return;

            // Un host que reintenta con otro puerto deja atrás el intento anterior. Se cancela y
            // se suelta: el mapeo del intento viejo, si llegó a existir, caduca solo.
            Cancel();

            _startedFor = key;
            _service = new HostConnectivityService(options ?? HostConnectivityOptions.FromEnvironment());
            Latest = _service.Report;
            _cancellation = new CancellationTokenSource();

            HostConnectivityService service = _service;
            CancellationToken token = _cancellation.Token;

            _running = Task.Run(async () =>
            {
                try
                {
                    await service.PrepareAsync(lanIp, port, null, token).ConfigureAwait(false);
                    LogOutcome(service);
                }
                catch (Exception e)
                {
                    // La preparación ya convierte cada fallo en un código; esto sólo cubre lo
                    // imprevisto. Que reviente NO puede impedir hostear.
                    Debug.LogWarning($"[HostConnectivity] la preparación terminó con excepción: {e.Message}. " +
                                     "El Host en LAN no se ve afectado.");
                }
            }, token);
        }

        /// <summary>
        /// Retira el mapeo del router. La llama el teardown de sesión. Idempotente, segura sin
        /// router y sin bloquear: el teardown no puede esperar a que conteste un router.
        /// </summary>
        public static void Release()
        {
            HostConnectivityService service = _service;
            _startedFor = null;

            if (service == null)
            {
                Cancel();
                return;
            }

            // El token NO se cancela antes de soltar: cancelarlo abortaría justo la llamada que
            // borra el mapeo, y dejar un reenvío abierto apuntando a una partida muerta es peor
            // que tardar un segundo más en cerrarla.
            Task.Run(async () =>
            {
                try
                {
                    bool ok = await service.ReleaseAsync().ConfigureAwait(false);
                    Debug.Log(ok
                        ? "[HostConnectivity] mapeo UPnP retirado."
                        : "[HostConnectivity] el router no confirmó el borrado del mapeo; caducará solo.");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[HostConnectivity] fallo al retirar el mapeo: {e.Message}. Caducará solo.");
                }
            });

            _service = null;
            _running = null;
        }

        /// <summary>Deja el estado como al arrancar el proceso. Para los tests y el cambio de sesión.</summary>
        public static void Reset()
        {
            Cancel();
            _service = null;
            _running = null;
            _startedFor = null;
            Latest = new HostConnectivityReport();
        }

        private static void Cancel()
        {
            try { _cancellation?.Cancel(); }
            catch (Exception) { /* cancelar dos veces no es noticia */ }

            _cancellation?.Dispose();
            _cancellation = null;
        }

        private static void LogOutcome(HostConnectivityService service)
        {
            // Una sola línea con formato `clave=valor`, como NETPROBE y MPTRACE: es lo que se
            // filtra con grep desde el Player.log de un tester.
            Debug.Log("[HostConnectivity] " + service.Report.ToLogLine());
            Debug.Log($"[HostConnectivity] upnp: {service.UpnpReason}");
            if (service.MappingReason != null) Debug.Log($"[HostConnectivity] mapeo: {service.MappingReason}");
            if (service.CgnatReason != null) Debug.Log($"[HostConnectivity] cgnat: {service.CgnatReason}");
            Debug.Log($"[HostConnectivity] {service.Report.DescribeReachability()}");
        }
    }
}
