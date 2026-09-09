using System.Collections.Generic;
using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// «X se ha unido a la partida» en el cartel del HUD. Escucha el `player_joined` del bus de
    /// eventos IPC y lo despacha por el <see cref="MessageDispatcher"/> del vendor, que es el
    /// mismo cartel que ya usa la construcción (`BuildPermission.DeniedMessage`).
    ///
    /// Se arranca solo, como <see cref="PvpFeedbackController"/>: su ciclo de vida es el del
    /// proceso, no el de una escena. La regla de qué se anuncia vive en
    /// <see cref="PlayerJoinMessages"/>, sin Unity, con sus tests.
    /// </summary>
    public sealed class PlayerJoinNotifier : MonoBehaviour
    {
        private static PlayerJoinNotifier _instance;
        private IPCClient _ipc;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[PlayerJoinNotifier]");
            _instance = go.AddComponent<PlayerJoinNotifier>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            if (_ipc == null && IPCClient.TryGetInstance(out var ipc))
            {
                _ipc = ipc;
                _ipc.AddEventListener(OnGameEvent);
            }
        }

        // Hilo principal: IPCClient.Update drena la cola de eventos.
        private void OnGameEvent(GameEventMsg ev)
        {
            if (ev == null || ev.eventType != "player_joined")
                return;

            var d = ev.data as Dictionary<string, object>;
            if (d == null)
                return;

            if (!PlayerJoinMessages.ShouldAnnounce(IPCParse.B(d, "is_host")))
                return;

            string text = PlayerJoinMessages.Joined(IPCParse.S(d, "name"));
            Debug.Log($"[PlayerJoinNotifier] player_id={IPCParse.L(d, "player_id")} -> \"{text}\"");

            // Sin personaje local todavía (el HUD no está montado) el cartel no tiene dónde
            // pintarse; el log de arriba deja constancia igual.
            var character = GameMode.HasInstance ? GameMode.Instance.LocalPlayer : null;
            if (character == null)
                return;

            MessageDispatcher.Instance.Dispatch(character, MsgType.Info, text);
        }

        private void OnDestroy()
        {
            if (_ipc != null)
                _ipc.RemoveEventListener(OnGameEvent);
            if (_instance == this)
                _instance = null;
        }
    }
}
