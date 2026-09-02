using System;
using BackroomsSurvival.Lobbies;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Las reglas de la INTEGRACIÓN del navegador con el ciclo de sesión, probadas sin Unity y
    /// sin servidor: abrir, volver, el gate de `CanStart`, el camino único de conexión, y que un
    /// intento fallido deje el panel reutilizable.
    ///
    /// El permiso para arrancar (`CanStart`) entra como PARÁMETRO. Es lo que permite probar el
    /// gate entero aquí y, a la vez, lo que garantiza que la respuesta la sigue dando
    /// `SessionStateMachine` y no una copia de su criterio metida en el navegador.
    /// </summary>
    public sealed class ServerBrowserIntegrationTests
    {
        private const string ClientVersion = "1.2.3";

        /// El "camino de conexión", contado. En el juego es JoinSessionUI; aquí es este contador.
        private sealed class FakeSink : ILobbyJoinSink
        {
            public bool Accept = true;
            public string Refusal = "sin panel";
            public int Calls;
            public LobbyEndpoint LastEndpoint;

            public LobbyRelay LastRelay;

            public bool TryJoin(LobbyEndpoint endpoint, LobbyRelay relay, string playerName, out string failure)
            {
                Calls++;
                LastEndpoint = endpoint;
                LastRelay = relay;
                failure = Accept ? null : Refusal;
                return Accept;
            }
        }

        private sealed class StubDirectory : ILobbyDirectory
        {
            private Action<LobbyDirectoryResult> _pending;
            private LobbyDirectoryResult _next = LobbyDirectoryResult.Ok(LobbyList.Empty);

            public int RefreshCount;

            public string Description => "stub";
            public bool IsRefreshing => _pending != null;

            public void Enqueue(LobbyDirectoryResult result) => _next = result;

            public void Refresh(double nowUnix, Action<LobbyDirectoryResult> onCompleted)
            {
                CancelRefresh();
                RefreshCount++;
                _pending = onCompleted;
            }

            public void CancelRefresh()
            {
                Action<LobbyDirectoryResult> pending = _pending;
                _pending = null;
                pending?.Invoke(LobbyDirectoryResult.Cancelled());
            }

            public void Tick(double nowUnix) { }

            public void Complete()
            {
                Action<LobbyDirectoryResult> pending = _pending;
                _pending = null;
                pending?.Invoke(_next);
            }
        }

        private static Lobby Make(string id, int players = 1, int maxPlayers = 8,
            string version = ClientVersion, int port = 7778, double updatedAt = 1000d, float ttl = 30f)
        {
            Assert.IsTrue(Lobby.TryCreate(id, "Server " + id, version, players, maxPlayers,
                "Level 0", "EU-West", 40, LobbyPrivacy.Public, false, "10.0.0.7", port, updatedAt,
                ttl, LobbyStatus.Waiting, out Lobby lobby));
            return lobby;
        }

        private static ServerBrowserViewModel Opened(StubDirectory stub, FakeSink sink,
            double now, params Lobby[] lobbies)
        {
            var vm = new ServerBrowserViewModel(stub, ClientVersion, sink);
            vm.Open(now);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(lobbies, now)));
            stub.Complete();
            return vm;
        }

        // ─── Fase 1: entrada desde el menú ───

        [Test]
        public void BrowseFromMenuStartsCorrectly()
        {
            var stub = new StubDirectory();
            var sink = new FakeSink();
            var vm = new ServerBrowserViewModel(stub, ClientVersion, sink);

            vm.Open(1000d);
            Assert.AreEqual(ServerBrowserState.Loading, vm.State, "abrir tiene que pedir lista, no quedarse ocioso");
            Assert.AreEqual(1, stub.RefreshCount);

            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[] { Make("a") }, 1000d)));
            stub.Complete();

            Assert.AreEqual(ServerBrowserState.Ready, vm.State);
            Assert.AreEqual(1, vm.Visible.Count);
        }

        [Test]
        public void BrowserDoesNotStartBackend()
        {
            // Abrir, refrescar y seleccionar NO pueden tocar el camino de conexión. El único
            // gesto que lo toca es Entrar.
            var stub = new StubDirectory();
            var sink = new FakeSink();
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d, Make("a"));

            vm.Refresh(1001d);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[] { Make("a") }, 1001d)));
            stub.Complete();
            Assert.IsTrue(vm.Select(new LobbyId("a")));

            Assert.AreEqual(0, sink.Calls);
        }

        [Test]
        public void BrowserBackReturnsToMultiplayerMenu()
        {
            // Lo que se puede afirmar sin Unity: volver deja el navegador ocioso, sin consulta en
            // vuelo y SIN haber tocado la sesión. Que el panel de multijugador reaparezca lo hace
            // ServerBrowserBootstrap.ReturnToConnectPanel llamando a JoinSessionUI.ShowConnectMenu,
            // y eso se comprueba en la prueba manual.
            var stub = new StubDirectory();
            var sink = new FakeSink();
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d, Make("a"));

            vm.Close();

            Assert.AreEqual(ServerBrowserState.Idle, vm.State);
            Assert.IsFalse(vm.IsRefreshing);
            Assert.AreEqual(0, sink.Calls);
        }

        [Test]
        public void ReopeningTheBrowserAsksAgainAndDoesNotStackRequests()
        {
            var stub = new StubDirectory();
            var sink = new FakeSink();
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d, Make("a"));

            vm.Close();
            vm.Open(1002d);

            Assert.AreEqual(2, stub.RefreshCount);
            Assert.AreEqual(ServerBrowserState.Loading, vm.State);
        }

        // ─── Fase 2 y 7: el gate del ciclo de sesión ───

        [Test]
        public void JoinRequiresCanStart()
        {
            var stub = new StubDirectory();
            var sink = new FakeSink();
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d, Make("a"));
            Assert.IsTrue(vm.Select(new LobbyId("a")));

            LobbyJoinRequestResult result = vm.RequestJoin("Joel", 1000d, lifecycleAllowsStart: false);

            Assert.AreEqual(LobbyJoinRequestStatus.SessionBusy, result.Status);
            Assert.AreEqual(0, sink.Calls, "con sesión viva no se llega ni a tocar el camino de conexión");
            Assert.AreNotEqual(ServerBrowserState.Joining, vm.State);
            Assert.AreEqual(ServerBrowserViewModel.SessionBusyMessage, vm.StatusMessage);
        }

        [Test]
        public void JoinUsesSingleExistingConnectionPath()
        {
            var stub = new StubDirectory();
            var sink = new FakeSink();
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d, Make("a"));
            Assert.IsTrue(vm.Select(new LobbyId("a")));

            LobbyJoinRequestResult result = vm.RequestJoin("Joel", 1000d, lifecycleAllowsStart: true);

            Assert.IsTrue(result.Started);
            Assert.AreEqual(1, sink.Calls, "una petición, una llamada: no hay segundo camino");
            Assert.AreEqual("10.0.0.7", sink.LastEndpoint.Host);
            Assert.AreEqual(7778, sink.LastEndpoint.Port);
            Assert.AreEqual(ServerBrowserState.Joining, vm.State);
        }

        [Test]
        public void DuplicateJoinIsRejected()
        {
            var stub = new StubDirectory();
            var sink = new FakeSink();
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d, Make("a"));
            Assert.IsTrue(vm.Select(new LobbyId("a")));

            Assert.IsTrue(vm.RequestJoin("Joel", 1000d, true).Started);
            LobbyJoinRequestResult second = vm.RequestJoin("Joel", 1000d, true);

            Assert.AreEqual(LobbyJoinRequestStatus.SessionBusy, second.Status);
            Assert.AreEqual(1, sink.Calls, "el segundo clic no puede lanzar un segundo intento");
            Assert.AreEqual(ServerBrowserViewModel.JoinInFlightMessage, vm.StatusMessage);
        }

        [Test]
        public void FailedJoinReturnsBrowserToUsableState()
        {
            var stub = new StubDirectory();
            var sink = new FakeSink();
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d, Make("a"));
            Assert.IsTrue(vm.Select(new LobbyId("a")));
            Assert.IsTrue(vm.RequestJoin("Joel", 1000d, true).Started);

            vm.NotifyJoinFailed("no session confirmation from 10.0.0.7:7778");

            Assert.AreEqual(ServerBrowserState.Ready, vm.State, "nada de quedarse en Conectando… para siempre");
            Assert.IsFalse(vm.IsJoining);
            Assert.AreEqual("no session confirmation from 10.0.0.7:7778", vm.StatusMessage);
        }

        [Test]
        public void RetryAfterFailedJoinWorks()
        {
            var stub = new StubDirectory();
            var sink = new FakeSink();
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d, Make("a"));
            Assert.IsTrue(vm.Select(new LobbyId("a")));

            Assert.IsTrue(vm.RequestJoin("Joel", 1000d, true).Started);
            vm.NotifyJoinFailed("timeout");

            LobbyJoinRequestResult retry = vm.RequestJoin("Joel", 1001d, true);

            Assert.IsTrue(retry.Started);
            Assert.AreEqual(2, sink.Calls);
            Assert.AreEqual("a", vm.Selected.Id.Value, "reintentar no obliga a volver a elegir servidor");
        }

        [Test]
        public void LeavingSessionAllowsBrowserAgain()
        {
            // Mientras la sesión vive, el gate dice que no; cuando termina (CanStart vuelve a ser
            // cierto) el MISMO navegador entra sin reconstruirse.
            var stub = new StubDirectory();
            var sink = new FakeSink();
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d, Make("a"));
            Assert.IsTrue(vm.Select(new LobbyId("a")));

            Assert.AreEqual(LobbyJoinRequestStatus.SessionBusy,
                vm.RequestJoin("Joel", 1000d, lifecycleAllowsStart: false).Status);
            Assert.AreEqual(0, sink.Calls);

            Assert.IsTrue(vm.RequestJoin("Joel", 1001d, lifecycleAllowsStart: true).Started);
            Assert.AreEqual(1, sink.Calls);
        }

        [Test]
        public void SucceededJoinLeavesTheBrowserReusable()
        {
            var stub = new StubDirectory();
            var sink = new FakeSink();
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d, Make("a"));
            Assert.IsTrue(vm.Select(new LobbyId("a")));
            Assert.IsTrue(vm.RequestJoin("Joel", 1000d, true).Started);

            vm.NotifyJoinSucceeded();

            Assert.IsFalse(vm.IsJoining);
            Assert.AreEqual(ServerBrowserState.Ready, vm.State);
        }

        // ─── Fase 7: validación del lobby antes de tocar nada ───

        [Test]
        public void ExpiredLobbyCannotBeJoined()
        {
            var stub = new StubDirectory();
            var sink = new FakeSink();
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d, Make("a", updatedAt: 1000d, ttl: 5f));
            Assert.IsTrue(vm.Select(new LobbyId("a")));

            LobbyJoinRequestResult result = vm.RequestJoin("Joel", 1010d, true);

            Assert.AreEqual(LobbyJoinRequestStatus.RejectedByLobby, result.Status);
            Assert.AreEqual(LobbyJoinability.Expired, result.Reason);
            Assert.AreEqual(0, sink.Calls);
            Assert.IsNull(vm.Selected, "una ficha caducada deja de ser seleccionable en el acto");
            Assert.AreEqual(0, vm.Visible.Count);
        }

        [Test]
        public void InvalidLobbyCannotBeJoined()
        {
            var stub = new StubDirectory();
            var sink = new FakeSink();
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d,
                Make("broken", port: 0), Make("full", players: 8, maxPlayers: 8),
                Make("old", version: "0.0.1"));

            AssertRejected(vm, sink, "broken", LobbyJoinability.InvalidEndpoint);
            AssertRejected(vm, sink, "full", LobbyJoinability.Full);
            AssertRejected(vm, sink, "old", LobbyJoinability.VersionMismatch);
            Assert.AreEqual(0, sink.Calls);
        }

        [Test]
        public void JoinWithNothingSelectedIsNotAnAttempt()
        {
            var stub = new StubDirectory();
            var sink = new FakeSink();
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d, Make("a"));

            LobbyJoinRequestResult result = vm.RequestJoin("Joel", 1000d, true);

            Assert.AreEqual(LobbyJoinRequestStatus.NoSelection, result.Status);
            Assert.AreEqual(0, sink.Calls);
        }

        [Test]
        public void ARefusalFromTheConnectionPathDoesNotLeaveTheBrowserJoining()
        {
            var stub = new StubDirectory();
            var sink = new FakeSink { Accept = false, Refusal = "No hay panel de conexión vivo." };
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d, Make("a"));
            Assert.IsTrue(vm.Select(new LobbyId("a")));

            LobbyJoinRequestResult result = vm.RequestJoin("Joel", 1000d, true);

            Assert.AreEqual(LobbyJoinRequestStatus.SinkRefused, result.Status);
            Assert.IsFalse(vm.IsJoining, "un no del panel no puede dejar el navegador colgado");
            Assert.AreEqual("No hay panel de conexión vivo.", vm.StatusMessage);
        }

        // ─── Lista y selección ───

        [Test]
        public void BrowserSelectionInvalidatedAfterRefresh()
        {
            var stub = new StubDirectory();
            var sink = new FakeSink();
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d, Make("a"), Make("b"));
            Assert.IsTrue(vm.Select(new LobbyId("a")));

            vm.Refresh(1001d);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[] { Make("b") }, 1001d)));
            stub.Complete();

            Assert.IsNull(vm.Selected, "el servidor elegido dejó de anunciarse");
            Assert.IsFalse(vm.SelectedId.IsValid);
        }

        [Test]
        public void EmptyDirectoryIsNotError()
        {
            var stub = new StubDirectory();
            var sink = new FakeSink();
            ServerBrowserViewModel vm = Opened(stub, sink, 1000d);

            Assert.AreEqual(ServerBrowserState.Empty, vm.State);
            Assert.AreNotEqual(ServerBrowserState.Error, vm.State);
            Assert.AreEqual(ServerBrowserViewModel.NoServersMessage, vm.StatusMessage);
        }

        [Test]
        public void DiscoveryFailureSaysSoWithoutLosingTheDetail()
        {
            var stub = new StubDirectory();
            var sink = new FakeSink();
            var vm = new ServerBrowserViewModel(stub, ClientVersion, sink);

            vm.Open(1000d);
            stub.Enqueue(LobbyDirectoryResult.Failed("HTTP 503"));
            stub.Complete();

            Assert.AreEqual(ServerBrowserState.Error, vm.State);
            Assert.AreEqual(ServerBrowserViewModel.DiscoveryFailedMessage, vm.StatusMessage);
            Assert.AreEqual("HTTP 503", vm.ErrorDetail);
        }

        [Test]
        public void BrowserDoesNotDuplicateListeners()
        {
            // El riesgo real: reabrir el panel y volver a suscribirse sin soltar lo anterior.
            var stub = new StubDirectory();
            var vm = new ServerBrowserViewModel(stub, ClientVersion, new FakeSink());
            Assert.AreEqual(0, vm.ChangedListenerCount);

            Action handler = () => { };
            vm.Changed += handler;
            Assert.AreEqual(1, vm.ChangedListenerCount);

            vm.Changed -= handler;
            Assert.AreEqual(0, vm.ChangedListenerCount, "el desenganche tiene que dejarlo a cero");
        }

        private static void AssertRejected(ServerBrowserViewModel vm, FakeSink sink, string id,
            LobbyJoinability expected)
        {
            Assert.IsTrue(vm.Select(new LobbyId(id)), "la fila tiene que estar visible para elegirla");
            LobbyJoinRequestResult result = vm.RequestJoin("Joel", 1000d, true);
            Assert.AreEqual(LobbyJoinRequestStatus.RejectedByLobby, result.Status);
            Assert.AreEqual(expected, result.Reason);
        }
    }
}
