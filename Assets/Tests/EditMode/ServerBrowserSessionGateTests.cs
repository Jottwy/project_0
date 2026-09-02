using BackroomsSurvival.Lobbies;
using BackroomsSurvival.Net;
using BackroomsSurvival.UI;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El adaptador que une el navegador con el ÚNICO camino de conexión, y el gate de fase que
    /// lo protege. Necesita Unity (toca `SessionState` y un componente), pero no red, ni backend,
    /// ni escena de juego.
    /// </summary>
    public sealed class ServerBrowserSessionGateTests
    {
        private static readonly LobbyEndpoint Valid = new LobbyEndpoint("10.0.0.7", 7778);

        [SetUp]
        public void SetUp() => SessionState.ResetForTests();

        [TearDown]
        public void TearDown() => SessionState.ResetForTests();

        [Test]
        public void SinkRefusesWhileASessionIsLive()
        {
            var sink = new JoinSessionLobbyJoinSink();
            Assert.IsTrue(SessionState.Current.RequestStart(NetworkInitializer.Role.Joiner));
            Assert.IsFalse(SessionState.Current.CanStart);

            Assert.IsFalse(sink.TryJoin(Valid, LobbyRelay.None, "Joel", out string failure));
            Assert.IsNotEmpty(failure);
            StringAssert.Contains("sesión", failure);
        }

        [Test]
        public void SinkRefusesAnInvalidEndpointBeforeTouchingTheSession()
        {
            var sink = new JoinSessionLobbyJoinSink();
            Assert.IsFalse(sink.TryJoin(new LobbyEndpoint("10.0.0.7", 0), LobbyRelay.None, "Joel",
                out string failure));
            Assert.IsNotEmpty(failure);
            Assert.AreEqual(SessionPhase.Menu, SessionState.Phase, "no se toca la sesión para decir que no");
        }

        [Test]
        public void SinkRefusesWhenThereIsNoConnectPanel()
        {
            // Sin `JoinSessionUI` montado no hay camino de conexión al que entrar, y el sink NO
            // se inventa uno llamando a NetworkInitializer por su cuenta.
            var sink = new JoinSessionLobbyJoinSink();
            Assert.IsTrue(SessionState.Current.CanStart);

            bool started = sink.TryJoin(Valid, LobbyRelay.None, "Joel", out string failure);

            if (!started) Assert.IsNotEmpty(failure);
            Assert.AreEqual(SessionPhase.Menu, SessionState.Phase,
                "un no del panel no puede haber movido la máquina de estados");
        }

        // ─── Coexistencia con el Join por IP (Fase 5) ───

        [Test]
        public void BrowsingNeverStartsABackend()
        {
            // `BackendLaunchCount` es el contador REAL de procesos lanzados. Descubrir, listar y
            // seleccionar no puede moverlo ni un punto.
            int before = NetworkInitializer.BackendLaunchCount;

            ILobbyDirectory directory = ServerBrowserBootstrap.CreateDirectory();
            var browser = new ServerBrowserViewModel(directory, ServerBrowserBootstrap.ClientVersion);
            browser.Open(1000d);
            browser.Tick(1000d);
            browser.Tick(1001d);
            browser.CancelRefresh();

            Assert.AreEqual(before, NetworkInitializer.BackendLaunchCount);
            Assert.AreEqual(SessionPhase.Menu, SessionState.Phase);
        }

        [Test]
        public void SteamFailingDoesNotBlockTheManualIpPath()
        {
            // Sin Steam el navegador da un error limpio; la sesión no se toca y `CanStart` sigue
            // en cierto, que es lo que hace que [Join] por IP funcione igual que siempre.
            var directory = new SteamLobbyDirectory(new UnavailableSteamQuery());
            var browser = new ServerBrowserViewModel(directory, ServerBrowserBootstrap.ClientVersion);

            browser.Open(1000d);
            browser.Tick(1000d);

            Assert.AreEqual(ServerBrowserState.Error, browser.State);
            Assert.AreEqual(ServerBrowserViewModel.DiscoveryFailedMessage, browser.StatusMessage);
            Assert.IsTrue(SessionState.Current.CanStart, "el Join manual sigue disponible");
            Assert.AreEqual(SessionPhase.Menu, SessionState.Phase);
        }

        [Test]
        public void TheClientVersionIsTheWireVersionAndNotTheBuildVersion()
        {
            // Dos builds con el mismo `Application.version` y distinto esquema no se pueden
            // hablar; y al revés, un cambio de versión de marketing no puede partir el navegador.
            Assert.AreEqual(WireSchema.Expected.ToString(), ServerBrowserBootstrap.ClientVersion);
        }

        [Test]
        public void PublisherAndReaderUseTheSameMetadataKeys()
        {
            // Si divergen, el host publica con unas claves y el navegador lee con otras: la lista
            // sale VACÍA y sin un solo error, que es el fallo más difícil de ver.
            Assert.IsTrue(SteamLobbyKeyParity.KeysMatch(out string mismatch), mismatch);
        }

        private sealed class UnavailableSteamQuery : ISteamLobbyQuery
        {
            public bool IsAvailable => false;
            public void Begin(int maxResults) => Assert.Fail("no se pregunta a Steam si no está");
            public void Abandon() { }

            public SteamQueryState Poll(out System.Collections.Generic.IReadOnlyList<SteamLobbyRecord> records,
                out string error)
            {
                records = null;
                error = null;
                return SteamQueryState.Idle;
            }
        }

        [Test]
        public void ReconfiguringTheBrowserDoesNotLeaveTheOldViewModelListening()
        {
            var go = new GameObject("TestServerBrowser");
            try
            {
                var ui = go.AddComponent<ServerBrowserUI>();
                ui.Configure(new MockLobbyDirectory(), new JoinSessionLobbyJoinSink(), "1.2.3", null);
                ServerBrowserViewModel first = ui.ViewModel;
                Assert.AreEqual(1, first.ChangedListenerCount);

                ui.Configure(new MockLobbyDirectory(), new JoinSessionLobbyJoinSink(), "1.2.3", null);

                Assert.AreEqual(0, first.ChangedListenerCount, "el view model viejo tiene que quedar suelto");
                Assert.AreEqual(1, ui.ViewModel.ChangedListenerCount);
                Assert.AreNotSame(first, ui.ViewModel);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
