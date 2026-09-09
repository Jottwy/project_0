using System;
using System.Net;
using System.Threading;
using UnityEngine;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>
    /// El único punto desde el que el juego abre y cierra el túnel de Steam — ADR-135 D3.
    ///
    /// **Hilo propio, no `Update`.** El goteo de chunks de un join son ~820 datagramas/s medidos
    /// (ADR-117), y a 60 Hz cada salto de fotograma añadiría hasta 16 ms por sentido y por salto.
    /// `SteamClient.RunCallbacks` se queda donde estaba, en el `Update` de `SteamLobbyManager`: lo
    /// que corre aquí es el bombeo de ESTOS sockets, que la API de red de Valve sí admite desde
    /// otro hilo.
    ///
    /// Estático y no `MonoBehaviour` por lo mismo que <see cref="HostConnectivityRunner"/>: su ciclo
    /// de vida es el de la SESIÓN, y las escenas se cargan y descargan en medio.
    /// </summary>
    public static class SteamTunnelRunner
    {
        /// <summary>
        /// El «puerto virtual» de SNS, que no es un puerto de red: es una etiqueta para que dos
        /// extremos se entiendan. Las dos puntas tienen que usar el mismo número y por eso vive
        /// aquí, en una constante compartida.
        /// </summary>
        public const int VirtualPort = 7778;

        /// Cuánto espera cada enlace por un datagrama antes de ceder el turno. Corto a propósito:
        /// con varios joiners, un enlace que se quedara esperando retrasaría a todos los demás.
        private const int PumpTimeoutMs = 2;

        private static ISteamTunnelTransport _transport;
        private static SteamTunnelHost _host;
        private static SteamTunnelJoiner _joiner;
        private static Thread _thread;
        private static volatile bool _running;
        private static readonly object Gate = new object();

        /// <summary>El túnel está abierto y bombeando.</summary>
        public static bool IsRunning => _running;

        /// <summary>
        /// Cierra el túnel también cuando el jugador **cierra el juego** sin pasar por el teardown
        /// de sesión (Alt+F4, la X, el `Quit` del menú).
        ///
        /// Hacía falta y se vio en el primer playtest real: el `Step` de `SessionEndHandler` cubre
        /// el fin de SESIÓN, pero al cerrar la aplicación nadie llamaba aquí, y el hilo —que es
        /// `IsBackground`— seguía vivo bombeando contra un Steam que ya se estaba apagando hasta
        /// que el proceso moría. `Application.quitting` es el único punto que cubre las tres
        /// puertas de salida a la vez.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void HookApplicationQuit()
        {
            Application.quitting -= OnApplicationQuitting;
            Application.quitting += OnApplicationQuitting;
        }

        private static void OnApplicationQuitting()
        {
            if (!_running && _transport == null) return;

            Debug.Log("[SteamTunnel] la aplicación se cierra: se cierra el túnel.");
            Shutdown();
        }

        /// <summary>Papel del túnel abierto, para el log y para la suite.</summary>
        public static string Role { get; private set; }

        /// <summary>
        /// El puerto de loopback del joiner: lo que viaja en `CONNECT_STEAM`. 0 si no hay túnel de
        /// joiner abierto.
        /// </summary>
        public static int JoinerLocalPort => _joiner?.LocalPort ?? 0;

        public static int AuthorizedPeers => _host?.AuthorizedCount ?? 0;

        public static int RejectedPeers => _host?.RejectedCount ?? 0;

        /// <summary>
        /// Abre el túnel del HOST contra el backend local. `backendNetPort` es el puerto realmente
        /// elegido (`NetworkInitializer.LastSelectedNetPort`), no el tecleado.
        ///
        /// Idempotente: con un túnel ya abierto no hace nada y devuelve true.
        /// </summary>
        public static bool BeginHost(ISteamTunnelTransport transport, string secret, int backendNetPort)
        {
            if (transport == null || backendNetPort <= 0) return false;
            if (string.IsNullOrWhiteSpace(secret)) return false;

            lock (Gate)
            {
                if (_running) return true;

                if (!transport.StartHost(VirtualPort))
                {
                    transport.Dispose();
                    Debug.LogWarning("[SteamTunnel] No se pudo abrir el socket de Steam del host. " +
                                     "El Host directo, la LAN y el relay propio no se ven afectados.");
                    return false;
                }

                var backend = new IPEndPoint(IPAddress.Loopback, backendNetPort);
                _host = new SteamTunnelHost(secret, backend, () => new UdpTunnelSocket());
                _transport = transport;
                _transport.OnMessage = (channel, payload, length) => _host.OnMessage(channel, payload, length);
                _transport.OnClosed = channel => _host.OnClosed(channel);

                Role = "host";
                StartPump();
                Debug.Log($"[SteamTunnel] Túnel de host abierto; el backend local escucha en {backend}.");
                return true;
            }
        }

        /// <summary>
        /// Abre el túnel del JOINER contra el `SteamId` del host y devuelve el puerto de loopback
        /// que hay que poner en `CONNECT_STEAM`. Devuelve 0 si no se pudo.
        ///
        /// Se llama **antes** de lanzar el backend: el puerto tiene que existir para poder
        /// pasárselo.
        /// </summary>
        public static int BeginJoiner(ISteamTunnelTransport transport, ulong hostSteamId, string secret)
        {
            if (transport == null || hostSteamId == 0UL) return 0;
            if (string.IsNullOrWhiteSpace(secret)) return 0;

            lock (Gate)
            {
                if (_running) return _joiner?.LocalPort ?? 0;

                ISteamTunnelChannel channel = transport.Connect(hostSteamId, VirtualPort);
                if (channel == null)
                {
                    transport.Dispose();
                    Debug.LogWarning($"[SteamTunnel] No se pudo conectar por Steam con {hostSteamId}. " +
                                     "Las demás vías de la secuencia siguen intactas.");
                    return 0;
                }

                var socket = new UdpTunnelSocket();
                _joiner = new SteamTunnelJoiner(channel, socket, secret);
                _transport = transport;
                _transport.OnMessage = (_, payload, length) => _joiner.OnMessage(payload, length);
                _transport.OnClosed = _ => { };

                Role = "joiner";
                StartPump();
                Debug.Log($"[SteamTunnel] Túnel de joiner abierto contra {hostSteamId}; " +
                          $"el backend local hablará por 127.0.0.1:{socket.Port}.");
                return socket.Port;
            }
        }

        /// <summary>
        /// Cierra el túnel y suelta todo: hilo, conexiones de Steam y sockets de loopback.
        /// Idempotente y segura sin Steam — la llama el teardown de sesión, que también corre
        /// cuando el túnel nunca se abrió.
        /// </summary>
        public static void Shutdown()
        {
            Thread thread;
            lock (Gate)
            {
                if (!_running && _transport == null && _host == null && _joiner == null) return;

                _running = false;
                thread = _thread;
                _thread = null;
            }

            // Fuera del lock: el hilo puede estar dentro de un `Poll` que necesita el lock para
            // terminar, y esperarlo con el lock cogido sería un abrazo mortal.
            if (thread != null && thread.IsAlive)
            {
                // Tope corto: el bombeo cede el turno cada pocos milisegundos, así que si no sale
                // en medio segundo es que está atascado, y el teardown no puede esperar a nadie.
                thread.Join(500);
            }

            lock (Gate)
            {
                _host?.Dispose();
                _joiner?.Dispose();
                _transport?.Dispose();
                _host = null;
                _joiner = null;
                _transport = null;
                Role = null;
            }

            Debug.Log("[SteamTunnel] Túnel cerrado.");
        }

        private static void StartPump()
        {
            _running = true;
            _thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "BackroomsSteamTunnel",
            };
            _thread.Start();
        }

        private static void Pump()
        {
            while (_running)
            {
                PumpOutcome outcome;
                try
                {
                    outcome = SteamTunnelPump.Once(_transport, _host, _joiner, PumpTimeoutMs);
                }
                catch (Exception e)
                {
                    // Que el túnel reviente NO puede llevarse la partida por delante: las otras
                    // tres vías de la secuencia siguen su camino.
                    Debug.LogWarning($"[SteamTunnel] el bombeo terminó con excepción: {e.Message}");
                    break;
                }

                if (outcome == PumpOutcome.Stopped)
                {
                    // El transporte se acabó — lo normal es que la sesión esté cerrándose y Steam
                    // ya haya invalidado el socket. **No se reintenta.** Hasta el 2026-09-09 sí se
                    // reintentaba, porque el fallo se tragaba dentro de `Poll`: en el primer
                    // playtest real eso dejó 203 excepciones repetidas girando a toda velocidad
                    // después de que el backend ya hubiera muerto.
                    Debug.Log("[SteamTunnel] el transporte dejó de responder; se para el bombeo.");
                    break;
                }

                // Sin nada que mover, ceder el turno: si no, el bucle quema un núcleo. Antes esto
                // sólo pasaba con el host sin peers autorizados, así que un joiner en silencio
                // —o un host con peers y sin tráfico— giraba a tope.
                if (outcome == PumpOutcome.Idle) Thread.Sleep(PumpTimeoutMs);
            }

            // Que el hilo haya salido tiene que verse desde fuera: `IsRunning` mintiendo deja al
            // anuncio publicando una vía Steam que ya no escucha (`PublishSteamTunnel`).
            _running = false;
        }
    }
}
