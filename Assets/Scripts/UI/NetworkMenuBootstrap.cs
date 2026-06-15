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
                // Panel already exists (e.g. button clicked twice) — just bring it back.
                ui.SetGameplayScene(_gameplayScene);
                ui.ShowMenu("Choose Host or Join");
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
