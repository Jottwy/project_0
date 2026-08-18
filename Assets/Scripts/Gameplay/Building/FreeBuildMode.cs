#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.BuildingSystem;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BackroomsSurvival.Gameplay.Building
{
    /// <summary>
    /// MODO TEST — F9: lo que coloques se termina solo, sin gastar materiales.
    ///
    /// Existe porque con los carryables del mundo a cero (recorte de escasez de 2026-08-17) no hay
    /// metal ni piedra que recoger, y sin material una pieza se queda en plano para siempre: no se
    /// puede probar NADA de lo que se construye. Colocar el plano ya era gratis; lo que estaba
    /// bloqueado era completarlo.
    ///
    /// NO SALTA LA REGLA DE TERRITORIO, y es deliberado (decisión de Joel): sigues necesitando una
    /// habitación construible y tu marcador plantado. Si además se saltara el territorio, encender
    /// este modo dejaría de probar justo la regla que ADR-081 acaba de montar.
    ///
    /// POR QUÉ ESTO NO ABRE UN AGUJERO DE AUTORIDAD, aunque lo decida el cliente: el host **no
    /// valida inventario** al construir. `process_stp_build_add` acumula el material que le digan y
    /// nada más; quien descuenta de tu mochila es el vendor, en el cliente. O sea que "construir
    /// gratis" ya era expresable con el protocolo tal cual está — esto solo lo automatiza. No hay
    /// mensaje nuevo, ni bump de wire, ni cambio en el backend. Lo único que sí abriría un agujero
    /// —saltarse el territorio— es justo lo que este modo no hace.
    ///
    /// Compilado SOLO en editor y en builds de desarrollo: en una release no existe ni el símbolo.
    /// </summary>
    public sealed class FreeBuildMode : MonoBehaviour
    {
        /// <summary>Cada cuánto se barre la escena buscando piezas a medio construir. No es por
        /// frame: solo corre con el modo ENCENDIDO, y medio segundo es imperceptible para quien
        /// acaba de colocar algo.</summary>
        private const float SweepIntervalSeconds = 0.5f;

        private static FreeBuildMode _instance;

        /// <summary>Piezas ya pagadas, por id de red. Sin esto se reenviarían los mismos añadidos en
        /// cada barrido hasta que el relay del host diese la vuelta — el progreso llega replicado,
        /// no al instante.</summary>
        private readonly HashSet<uint> _paid = new HashSet<uint>();

        private bool _enabled;
        private float _nextSweep;
        private uint _addCounter;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[FreeBuildMode]");
            _instance = go.AddComponent<FreeBuildMode>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            // Teclado leído del Input System directamente y no por una acción del vendor: esto es
            // una herramienta de prueba y no debe aparecer en el mapa de controles del juego ni
            // depender del contexto de input activo (que en modo construcción está empujado).
            if (Keyboard.current != null && Keyboard.current.f9Key.wasPressedThisFrame)
                Toggle();

            if (!_enabled || Time.unscaledTime < _nextSweep)
                return;

            _nextSweep = Time.unscaledTime + SweepIntervalSeconds;
            PayForEverything();
        }

        private void Toggle()
        {
            _enabled = !_enabled;
            // Al apagar se olvida lo pagado: si se vuelve a encender, una pieza que quedó a medias
            // por un relay perdido se vuelve a intentar en vez de quedarse colgada para siempre.
            if (!_enabled)
                _paid.Clear();

            Debug.Log($"MPTRACE step=BP event=free_build_toggled enabled={_enabled}");

            var character = GameMode.HasInstance ? GameMode.Instance.LocalPlayer : null;
            if (character != null)
            {
                MessageDispatcher.Instance.Dispatch(character, MsgType.Info,
                    _enabled
                        ? "MODO TEST: construcción gratis ACTIVADA (F9)"
                        : "MODO TEST: construcción gratis desactivada (F9)");
            }
        }

        /// <summary>
        /// Paga lo que le falte a cada pieza replicada a medio construir.
        ///
        /// Se barre la escena en vez de engancharse a la colocación porque la pieza que el jugador
        /// coloca NO es la que acaba en el mundo: `StpBuildingPlacementWatcher` la destruye y el host
        /// devuelve la copia replicada (con su `NetworkBuildingInstance`). Barrer, además, cubre lo
        /// que ya estuviera a medias antes de encender el modo, que es lo que "todo gratis" quiere
        /// decir.
        /// </summary>
        private void PayForEverything()
        {
            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            var pieces = FindObjectsByType<NetworkBuildingInstance>(FindObjectsSortMode.None);
            for (int i = 0; i < pieces.Length; i++)
            {
                var net = pieces[i];
                if (net == null || _paid.Contains(net.id))
                    continue;

                var piece = net.GetComponent<BuildingPiece>();
                if (piece == null)
                    continue;

                var constructable = piece.Constructable;
                if (constructable == null || constructable.IsConstructed)
                {
                    _paid.Add(net.id); // ya está: no volver a mirarla
                    continue;
                }

                int sent = 0;
                var requirements = constructable.GetBuildRequirements();
                for (int r = 0; r < requirements.Count; r++)
                {
                    var req = requirements[r];
                    int missing = req.RequiredAmount - req.CurrentAmount;
                    for (int unit = 0; unit < missing; unit++)
                    {
                        ipc.SendStpBuildAdd(NextAddId(), net.id, req.BuildMaterialId);
                        sent++;
                    }
                }

                // Se marca como pagada aunque no faltara nada: el barrido siguiente no tiene por qué
                // volver a preguntárselo.
                _paid.Add(net.id);
                if (sent > 0)
                    Debug.Log($"MPTRACE step=BP event=free_build_paid building={net.id} units={sent}");
            }
        }

        /// <summary>Mismo esquema que `StpBuildMaterialWatcher`: contador con prefijo de NET_ID, para
        /// que dos clientes no colisionen y el host pueda deduplicar una retransmisión.</summary>
        private long NextAddId()
        {
            int netId = NetworkInitializer.Instance != null ? NetworkInitializer.Instance.LastSelectedNetId : 0;
            return (long)Mathf.Max(1, netId) * 1000000000L + (++_addCounter);
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
#endif
