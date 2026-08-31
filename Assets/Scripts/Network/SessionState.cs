using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// El sitio donde vive LA maquina de estados de sesion del proceso.
    ///
    /// Static y no MonoBehaviour a proposito. Los tres puntos que arrancan una sesion
    /// (<c>AutoConnect</c>, <c>NetworkMenuBootstrap</c>, <c>JoinSessionUI</c>) crean sus
    /// componentes en runtime y en orden distinto segun por donde se entre; colgar la fase de
    /// cualquiera de ellos la haria depender de quien se construyo primero, que es justo el tipo
    /// de suposicion que ya costo un fallo (ver <c>SessionEndHandler.Update</c>, que se suscribe
    /// "en el primer frame en que exista" en vez de asumir un orden).
    ///
    /// El reinicio va por <see cref="RuntimeInitializeOnLoadMethod"/> con
    /// <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/>, igual que
    /// <c>IPCClient.ResetStatics</c>: con "Enter Play Mode Options" y el dominio sin recargar,
    /// un static conserva el valor de la sesion ANTERIOR del editor.
    /// </summary>
    public static class SessionState
    {
        private static SessionStateMachine _current = new SessionStateMachine();

        public static SessionStateMachine Current => _current;

        public static SessionPhase Phase => _current.Phase;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _current = new SessionStateMachine();
        }

        /// Reinicio explicito para la suite EditMode, que corre muchas fixtures en el mismo
        /// dominio y no pasa por RuntimeInitializeOnLoadMethod entre una y otra.
        public static void ResetForTests()
        {
            _current = new SessionStateMachine();
        }
    }
}
