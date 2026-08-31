using BackroomsSurvival.Lobbies;
using UnityEngine;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// EL PUNTO DE INTEGRACIÓN, y el único fichero del navegador de servidores que sabe que
    /// existe un panel de conexión.
    ///
    /// Lo que hace: coge el host y el puerto del lobby elegido y los mete por la MISMA puerta por
    /// la que entra hoy el auto-join de Steam — <see cref="JoinSessionUI.TryBeginSteamJoin"/>—,
    /// que rellena los campos, pinta "Connecting…", bloquea la UI y llama a
    /// <c>NetworkInitializer.StartAsJoiner</c>. No abre un segundo camino de conexión: eso es
    /// justo lo que no se puede hacer.
    ///
    /// DEUDA CONOCIDA, a resolver por quien sea dueño de JoinSessionUI / SessionStateMachine:
    ///
    ///  1. El método se llama `TryBeginSteamJoin` y loguea "(steam)" aunque el origen sea el
    ///     navegador. Lo limpio es renombrarlo a `TryBeginJoin(ip, port, playerName, string
    ///     source)` y que Steam pase "steam" y esto pase "browser". NO se toca desde aquí: ese
    ///     fichero pertenece al trabajo de sesión que corre en paralelo.
    ///  2. Devuelve `true` tanto si el intento arrancó como si el gate interno de fase
    ///     (`RejectIfBusy`) lo descartó. Desde fuera no se distingue "conectando" de "ignorado
    ///     porque ya había sesión". Lo limpio es que devuelva un motivo. Mientras tanto, este
    ///     sink comprueba ANTES <see cref="SessionState"/>.<c>Current.CanStart</c>, que es la
    ///     misma condición que usa el gate, para no mentirle al navegador.
    ///
    /// Ninguna de las dos deudas justifica tocar red ni la máquina de estados: las dos son
    /// cosméticas para el usuario y ninguna cambia el protocolo.
    /// </summary>
    public sealed class JoinSessionLobbyJoinSink : ILobbyJoinSink
    {
        public bool TryJoin(LobbyEndpoint endpoint, string playerName, out string failure)
        {
            if (!endpoint.IsValid)
            {
                failure = "El servidor anuncia una dirección inválida.";
                return false;
            }

            if (!Net.SessionState.Current.CanStart)
            {
                failure = "Ya hay una sesión en marcha (" + Net.SessionState.Phase + ").";
                return false;
            }

            string name = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim();
            // `endpoint.Alternate` es la LAN que anunció el host (`bs_lan_ip`). El primer intento
            // va SIEMPRE a lo anunciado; la alternativa sólo la usa el [Retry].
            if (!JoinSessionUI.TryBeginSteamJoin(endpoint.Host, endpoint.Port, name,
                    endpoint.HasAlternate ? endpoint.Alternate : null))
            {
                failure = "No hay panel de conexión vivo.";
                return false;
            }

            Debug.Log($"[ServerBrowser] Join solicitado a {endpoint} como '{name}'.");
            failure = null;
            return true;
        }
    }
}
