using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Lightweight main-menu helper that brings up the JoinSession connect panel on
    /// demand. Wire a "Multiplayer" button's onClick to <see cref="ShowConnectPanel"/>.
    ///
    /// It creates a single persistent "NetworkSession" object holding NetworkInitializer
    /// + JoinSessionUI. NetworkInitializer.Awake marks that object DontDestroyOnLoad, so
    /// the connection and overlay survive the load into the gameplay scene. The panel
    /// then drives Host/Join; on connect, JoinSessionUI loads <see cref="_gameplayScene"/>
    /// through STP's LevelManager — i.e. connect first, then enter the world.
    /// </summary>
    public sealed class NetworkMenuBootstrap : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Gameplay scene to load once connected. Must be in Build Settings.")]
        private string _gameplayScene = "STP_Showcase";

        /// <summary>Shows (or re-shows) the connect panel. Safe to call repeatedly.</summary>
        public void ShowConnectPanel()
        {
            var ui = FindFirstObjectByType<JoinSessionUI>();
            if (ui != null)
            {
                // Panel already exists (e.g. button clicked twice, o se vuelve del juego al menú)
                // — se reabre en estado de partida. `ShowMenu` a secas sólo lo hacía visible y lo
                // dejaba con el PanelState de la sesión anterior; si esa terminó en `Connected`,
                // el panel salía sin los botones de Host y Join. Ver ShowConnectMenu.
                ui.SetGameplayScene(_gameplayScene);
                ui.ShowConnectMenu();
                return;
            }

            // Reuse the persistent NetworkInitializer object if one already exists;
            // adding a second NetworkInitializer would self-destroy via its singleton guard.
            var init = NetworkInitializer.Instance;
            GameObject host = init != null ? init.gameObject : new GameObject("NetworkSession");
            if (init == null)
                host.AddComponent<NetworkInitializer>(); // Awake -> DontDestroyOnLoad(host)

            ui = host.AddComponent<JoinSessionUI>();
            ui.SetGameplayScene(_gameplayScene);
            // JoinSessionUI.Start() builds the UI and shows "Choose Host or Join".
            Debug.Log("[NetworkMenuBootstrap] Connect panel requested.");
        }
        /// <summary>
        /// Abre el panel SIN click cuando la sesión viene dada por el entorno
        /// (<c>SESSION_MODE</c> / <c>CONNECT_TO</c>), que es como arranca
        /// <c>tools/dev/RunMultiInstancePlaytest.ps1</c>.
        ///
        /// Sin esto el arranque automático no existía pese a estar documentado: `JoinSessionUI`
        /// LEE esas variables, pero en su `Start()` — y el único camino que lo creaba era el click
        /// en «Multiplayer» del menú principal. Con el MainMenu de primera escena, un playtest
        /// automatizado se quedaba renderizando el menú para siempre: cuatro instancias vivas,
        /// consumiendo CPU, sin backend ni un puerto abierto. Medido el 10-09.
        ///
        /// Sólo actúa si alguna de las dos variables está puesta, así que una partida normal
        /// —donde nadie las define— sigue esperando al click, exactamente como hasta ahora.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoOpenPanelWhenSessionComesFromEnvironment()
        {
            if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("SESSION_MODE")) &&
                string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("CONNECT_TO")))
                return;

            // El de la escena trae su `_gameplayScene` serializado; sólo se fabrica uno cuando la
            // escena de arranque no lo tiene (y entonces vale el valor por defecto del campo).
            var bootstrap = FindFirstObjectByType<NetworkMenuBootstrap>();
            if (bootstrap == null)
                bootstrap = new GameObject("[NetworkMenuBootstrap]").AddComponent<NetworkMenuBootstrap>();

            Debug.Log("[NetworkMenuBootstrap] SESSION_MODE/CONNECT_TO presentes: se abre el panel sin click.");
            bootstrap.ShowConnectPanel();
        }

        private void Awake()
        {
            PolymindGames.UserInterface.MainMenu.OnMultiplayerClicked += ShowConnectPanel;
        }

        private void OnDestroy()
        {
            PolymindGames.UserInterface.MainMenu.OnMultiplayerClicked -= ShowConnectPanel;
        }
    }
}
