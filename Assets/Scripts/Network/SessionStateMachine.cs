using System;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Las fases por las que pasa UNA sesion, de menu a menu.
    ///
    /// El orden numerico NO es un orden de transicion: <see cref="Failed"/> y
    /// <see cref="Disconnected"/> son los dos finales, y los dos vuelven a
    /// <see cref="Menu"/> por el mismo sitio.
    /// </summary>
    public enum SessionPhase
    {
        /// Sin sesion. El panel es usable y Host/Join estan disponibles.
        Menu = 0,
        /// Se ha pedido lanzar el backend; el proceso todavia no esta confirmado.
        Starting = 1,
        /// Backend lanzado; se espera IPC (host) o IPC + `session_joined` (joiner).
        Connecting = 2,
        /// Sesion establecida de verdad. Para un joiner esto exige `session_joined`.
        Connected = 3,
        /// La escena de juego esta cargada.
        InGame = 4,
        /// Teardown en marcha. NO se acepta ningun arranque nuevo aqui.
        Disconnecting = 5,
        /// La sesion termino (el host se fue, conexion perdida, salida con aviso).
        Disconnected = 6,
        /// El intento no llego a establecerse (timeout, backend muerto, rechazo).
        Failed = 7,
    }

    /// <summary>
    /// La maquina de estados de sesion. UNICA fuente de verdad de la FASE.
    ///
    /// No es duena del proceso backend (lo es <see cref="NetworkInitializer"/>), ni del socket
    /// IPC (lo es <see cref="IPCClient"/>), ni del panel (lo es <c>JoinSessionUI</c>). Es duena
    /// de la pregunta "en que punto del ciclo estamos", que antes contestaban tres componentes a
    /// la vez y con criterios distintos - de ahi que un joiner pudiera ensenar "Connected" por
    /// tener solo el IPC local arriba.
    ///
    /// Sin `UnityEngine` dentro a proposito: todas las transiciones se prueban en EditMode sin
    /// levantar un backend ni una escena.
    ///
    /// TODA transicion es idempotente: llamarla dos veces no deja un estado invalido. Los
    /// metodos devuelven <c>true</c> solo cuando la llamada CAMBIO algo; el segundo Leave
    /// devuelve <c>false</c> y no toca nada.
    /// </summary>
    public sealed class SessionStateMachine
    {
        public SessionPhase Phase { get; private set; } = SessionPhase.Menu;
        public NetworkInitializer.Role Role { get; private set; } = NetworkInitializer.Role.None;

        /// Motivo del ultimo final (timeout, el host se fue, salida voluntaria). Cadena vacia
        /// mientras la sesion vive.
        public string Reason { get; private set; } = "";

        /// <summary>
        /// Contador monotono de INTENTOS. Sube en cada <see cref="RequestStart"/> y en nada mas.
        ///
        /// Es el token contra el que se comprueba cualquier callback tardio: un
        /// <c>Process.Exited</c> del backend de la sesion anterior llega en un hilo del pool y no
        /// trae identidad; comparar su generacion con esta es lo que impide que apague la sesion
        /// NUEVA. Un `bool` no serviria - el problema no es "hay teardown en marcha", es "este
        /// aviso es de OTRA sesion".
        /// </summary>
        public int Generation { get; private set; }

        /// <summary>
        /// Senal de entrada, no fase: el backend local confirmo el handshake con el host
        /// (`session_joined`). Se guarda aparte de <see cref="Phase"/> porque puede llegar antes
        /// de que la fase este en condiciones de moverse, y porque un Host nunca la emite.
        /// </summary>
        public bool JoinerConfirmed { get; private set; }

        /// Nombre de la escena en la que se entro al mundo. Vacio mientras no se ha entrado.
        public string WorldScene { get; private set; } = "";

        /// <summary>
        /// Si este intento llego a estar establecido alguna vez (paso por
        /// <see cref="SessionPhase.Connected"/>).
        ///
        /// Distingue los DOS finales, que no son el mismo y no se leen igual: "no se pudo
        /// conectar" (nunca entro: IP equivocada, host apagado, timeout de handshake) y "la
        /// sesion termino" (entro y se perdio: el host se fue, se cayo la conexion). Sin esto,
        /// un join a una IP inalcanzable acababa diciendo "Session ended", que es exactamente el
        /// mensaje que no explica nada.
        /// </summary>
        public bool WasEstablished { get; private set; }

        public event Action<SessionPhase, SessionPhase> PhaseChanged;

        // --- Predicados ---------------------------------------------------------------------

        /// Fases en las que NO hay sesion y se puede arrancar una.
        public static bool IsIdle(SessionPhase phase) =>
            phase == SessionPhase.Menu || phase == SessionPhase.Disconnected || phase == SessionPhase.Failed;

        /// Fases en las que hay una sesion viva (aunque todavia no establecida).
        public static bool IsLive(SessionPhase phase) =>
            phase == SessionPhase.Starting || phase == SessionPhase.Connecting ||
            phase == SessionPhase.Connected || phase == SessionPhase.InGame;

        /// Fases en las que el jugador esta (o va a estar de inmediato) dentro del mundo. Es la
        /// que decide la politica de cursor, y por eso NO incluye Connecting.
        public static bool IsInWorld(SessionPhase phase) =>
            phase == SessionPhase.Connected || phase == SessionPhase.InGame;

        public bool CanStart => IsIdle(Phase);

        // --- Transiciones -------------------------------------------------------------------

        /// <summary>
        /// Pide arrancar una sesion. Se acepta SOLO desde una fase ociosa: es el gate que hace
        /// que un segundo Join durante el primero se ignore en vez de lanzar un segundo backend.
        /// </summary>
        public bool RequestStart(NetworkInitializer.Role role)
        {
            if (!IsIdle(Phase)) return false;

            Generation++;
            Role = role;
            Reason = "";
            JoinerConfirmed = false;
            WorldScene = "";
            WasEstablished = false;
            Set(SessionPhase.Starting);
            return true;
        }

        /// El proceso backend esta lanzado (no necesariamente listo).
        public bool NotifyBackendLaunched()
        {
            if (Phase != SessionPhase.Starting) return false;
            Set(SessionPhase.Connecting);
            return true;
        }

        /// <summary>
        /// El IPC local esta arriba. Para Host/autosolo ESO ES la sesion (su backend es el
        /// servidor). Para un joiner no prueba nada de la sesion y la fase no se mueve: falta
        /// <see cref="NotifySessionJoined"/>.
        /// </summary>
        public bool NotifyIpcConnected()
        {
            if (Phase != SessionPhase.Starting && Phase != SessionPhase.Connecting) return false;
            if (Role == NetworkInitializer.Role.Joiner) return false;
            WasEstablished = true;
            Set(SessionPhase.Connected);
            return true;
        }

        /// El backend local registro al host tras el HandshakeAck.
        public bool NotifySessionJoined()
        {
            JoinerConfirmed = true;
            if (Phase != SessionPhase.Starting && Phase != SessionPhase.Connecting) return false;
            WasEstablished = true;
            Set(SessionPhase.Connected);
            return true;
        }

        /// La escena de juego esta cargada y activa.
        public bool NotifyEnteredWorld(string sceneName)
        {
            if (Phase != SessionPhase.Connected) return false;
            WorldScene = sceneName ?? "";
            Set(SessionPhase.InGame);
            return true;
        }

        /// <summary>
        /// El intento no llego a establecerse: timeout, backend muerto al arrancar, rechazo.
        /// Solo desde Starting/Connecting - una caida con la sesion ya en marcha es
        /// <see cref="RequestLeave"/>, no esto.
        /// </summary>
        public bool NotifyFailed(string reason)
        {
            if (Phase != SessionPhase.Starting && Phase != SessionPhase.Connecting) return false;
            Reason = string.IsNullOrEmpty(reason) ? "connection failed" : reason;
            JoinerConfirmed = false;
            WasEstablished = false;
            Set(SessionPhase.Failed);
            return true;
        }

        /// <summary>
        /// Empieza el teardown. Idempotente: el segundo Leave devuelve <c>false</c> y no toca
        /// nada, que es lo que impide que dos eventos de fin (paquete de despedida Y timeout de
        /// latido) maten una sesion que ya fue reemplazada.
        /// </summary>
        public bool RequestLeave(string reason)
        {
            if (!IsLive(Phase)) return false;
            Reason = string.IsNullOrEmpty(reason) ? "disconnected" : reason;
            Set(SessionPhase.Disconnecting);
            return true;
        }

        /// <summary>
        /// El teardown termino. <paramref name="keepReason"/> decide el final: con aviso
        /// (<see cref="SessionPhase.Disconnected"/>, el panel explica por que) o silencioso
        /// (<see cref="SessionPhase.Menu"/>, salida voluntaria - el menu del juego ya es la UI).
        /// </summary>
        public bool NotifyLeaveComplete(bool keepReason)
        {
            if (Phase != SessionPhase.Disconnecting) return false;

            // El final con aviso se parte en dos segun <see cref="WasEstablished"/>: un intento
            // que nunca llego a entrar termina en Failed ("no se pudo conectar") y una sesion que
            // si entro, en Disconnected ("la sesion termino"). Es la diferencia entre teclear mal
            // una IP y que el host se vaya, y el jugador necesita leerla.
            SessionPhase end = !keepReason
                ? SessionPhase.Menu
                : (WasEstablished ? SessionPhase.Disconnected : SessionPhase.Failed);

            Role = NetworkInitializer.Role.None;
            JoinerConfirmed = false;
            WorldScene = "";
            WasEstablished = false;
            if (!keepReason) Reason = "";
            Set(end);
            return true;
        }

        /// <summary>
        /// Vuelve al menu desde un final (Disconnected/Failed) sin arrancar nada: es el boton
        /// "Volver al menu". No vale como atajo desde una sesion viva - para eso esta
        /// <see cref="RequestLeave"/>, que si limpia recursos.
        /// </summary>
        public bool AcknowledgeAndReturnToMenu()
        {
            if (Phase != SessionPhase.Disconnected && Phase != SessionPhase.Failed) return false;
            Reason = "";
            Set(SessionPhase.Menu);
            return true;
        }

        /// Reinicio duro. Solo para arranque de proceso y para los tests.
        public void HardReset()
        {
            Phase = SessionPhase.Menu;
            Role = NetworkInitializer.Role.None;
            Reason = "";
            JoinerConfirmed = false;
            WorldScene = "";
            WasEstablished = false;
            Generation = 0;
        }

        private void Set(SessionPhase next)
        {
            if (Phase == next) return;
            SessionPhase previous = Phase;
            Phase = next;
            PhaseChanged?.Invoke(previous, next);
        }
    }
}
