using System;

namespace BackroomsSurvival.Lobbies
{
    public enum LobbyDirectoryStatus
    {
        Ok = 0,

        /// La consulta no llegó o volvió ilegible. `ErrorMessage` lleva el motivo para la UI.
        Failed = 1,

        /// La canceló el cliente (cerró el panel, pidió otro refresh). No es un error y NO debe
        /// pintarse como tal: distinguirla es lo que evita el "Error de red" fantasma al salir.
        Cancelled = 2,
    }

    /// <summary>
    /// Lo que devuelve una consulta al directorio. Una lista VACÍA con estado Ok es un resultado
    /// legítimo ("no hay partidas") y no lo mismo que un fallo; la UI pinta cosas distintas.
    /// </summary>
    public readonly struct LobbyDirectoryResult
    {
        public readonly LobbyDirectoryStatus Status;
        public readonly LobbyList Lobbies;
        public readonly string ErrorMessage;

        private LobbyDirectoryResult(LobbyDirectoryStatus status, LobbyList lobbies, string errorMessage)
        {
            Status = status;
            Lobbies = lobbies ?? LobbyList.Empty;
            ErrorMessage = errorMessage ?? "";
        }

        public bool IsOk => Status == LobbyDirectoryStatus.Ok;

        public static LobbyDirectoryResult Ok(LobbyList lobbies) =>
            new LobbyDirectoryResult(LobbyDirectoryStatus.Ok, lobbies, null);

        public static LobbyDirectoryResult Failed(string message) =>
            new LobbyDirectoryResult(LobbyDirectoryStatus.Failed, LobbyList.Empty, message);

        public static LobbyDirectoryResult Cancelled() =>
            new LobbyDirectoryResult(LobbyDirectoryStatus.Cancelled, LobbyList.Empty, null);
    }

    /// <summary>
    /// De dónde sale la lista de partidas. La UI depende de ESTO y de nada más: ni HTTP, ni
    /// JSON, ni Steam, ni el backend. Cambiar el mock por el Lobby Directory real tiene que ser
    /// cambiar la implementación que se inyecta, sin tocar una línea del navegador.
    ///
    /// Por qué callback + <see cref="Tick"/> y no `async Task`:
    ///  - el resultado tiene que entregarse en el hilo de Unity, y un `await` sobre un
    ///    `HttpClient` vuelve donde le apetece al SynchronizationContext que haya montado;
    ///  - la suite EditMode puede avanzar el tiempo A MANO y observar cada estado intermedio
    ///    (cargando, llegó, falló) sin corrutinas ni esperas reales. Un mock que sólo sabe
    ///    responder al instante nunca prueba el estado "cargando", que es donde vive el bloqueo
    ///    del menú.
    ///
    /// El reloj lo pone SIEMPRE quien llama (`nowUnix`): ni el modelo ni el directorio miran
    /// `Time.*`, que es lo que los hace comprobables y lo que impide que el TTL avance en pausa.
    /// </summary>
    public interface ILobbyDirectory
    {
        /// Para diagnóstico en pantalla ("mock", "https://…"). Nunca se parsea.
        string Description { get; }

        /// <summary>Hay una consulta en vuelo.</summary>
        bool IsRefreshing { get; }

        /// <summary>
        /// Pide la lista. Si ya había una en vuelo, la anterior se cancela y su callback recibe
        /// <see cref="LobbyDirectoryStatus.Cancelled"/> — nunca dos resultados vivos a la vez,
        /// que es como una respuesta lenta repinta encima de otra más nueva.
        /// </summary>
        void Refresh(double nowUnix, Action<LobbyDirectoryResult> onCompleted);

        /// <summary>Cancela la consulta en vuelo, si la hay. Idempotente.</summary>
        void CancelRefresh();

        /// <summary>
        /// Avanza la consulta en vuelo. Se llama desde el `Update` del panel. El resultado se
        /// entrega DENTRO de esta llamada, así que el llamante siempre está en el hilo de Unity.
        /// </summary>
        void Tick(double nowUnix);
    }
}
