using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// El cursor del ciclo de sesion, con UN dueno y UNA regla.
    ///
    /// El problema no era que faltara un `Cursor.lockState = None` en alguna parte: era que
    /// habia cinco sitios escribiendolo sin acuerdo (JoinSessionUI al mostrar/ocultar el panel,
    /// PlayerController al arrancar y con Escape, y los tres del vendor:
    /// <c>UnityUtility.Lock/UnlockCursor</c> desde <c>GameMode</c>, <c>UIInput</c> y
    /// <c>InventoryInspectionManager</c>). El que escribia el ultimo ganaba, y quien ganaba
    /// dependia del orden de destruccion de la escena.
    ///
    /// El caso que rompia: <c>JoinSessionUI.HideMenu()</c> bloqueaba el cursor SIEMPRE, tambien
    /// cuando el panel se ocultaba estando en el menu. Volver al menu con el panel oculto dejaba
    /// el raton capturado sobre un menu que solo se usa con el raton.
    ///
    /// La regla, entera, esta en <see cref="ShouldLock"/> y es una funcion pura: se prueba en
    /// EditMode sin escenas. Este tipo NO le quita el cursor al vendor durante la partida - solo
    /// decide en las transiciones del ciclo de sesion, que es donde nadie mandaba.
    /// </summary>
    public static class SessionCursor
    {
        /// <summary>Ultimo valor aplicado por ESTE tipo. Observable para la suite de regresion.</summary>
        public static bool LastAppliedLocked { get; private set; }

        /// <summary>
        /// LA regla. Pura y sin Unity dentro.
        ///
        /// Con el panel de sesion visible el cursor SIEMPRE se libera: da igual la fase, porque
        /// un panel con campos de texto y botones que no se pueden pulsar es el fallo, no la
        /// politica. Con el panel oculto solo se captura si estamos dentro del mundo
        /// (<see cref="SessionStateMachine.IsInWorld"/>): en Menu, Disconnected, Failed,
        /// Disconnecting, Starting y Connecting el cursor se queda libre, que es lo que hace
        /// recuperable cualquier error.
        /// </summary>
        public static bool ShouldLock(bool menuVisible, SessionPhase phase)
        {
            if (menuVisible) return false;
            return SessionStateMachine.IsInWorld(phase);
        }

        /// <summary>Aplica la regla al cursor real.</summary>
        public static void Apply(bool menuVisible, SessionPhase phase)
        {
            SetLocked(ShouldLock(menuVisible, phase));
        }

        /// <summary>
        /// Devuelve el cursor al menu, pase lo que pase. Se llama al final de CADA teardown,
        /// tenga o no panel visible: es la garantia de "el cursor queda restaurado al volver al
        /// menu" para los caminos que no pasan por el panel (Quit to Menu del vendor, muerte del
        /// backend, perdida de conexion).
        /// </summary>
        public static void ReleaseToMenu()
        {
            SetLocked(false);
        }

        private static void SetLocked(bool locked)
        {
            LastAppliedLocked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
