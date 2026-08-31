using System;
using System.Collections.Generic;
using BackroomsSurvival.Lobbies;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El navegador entero SIN Unity y SIN servidor: refresco, estados de carga/vacío/error,
    /// selección, TTL y filtro. El reloj lo pone el test, así que no hay esperas reales.
    /// </summary>
    public sealed class ServerBrowserViewModelTests
    {
        private const string ClientVersion = "1.2.3";

        /// <summary>
        /// Directorio de pruebas con el gatillo en la mano: la consulta no se resuelve hasta que
        /// el test lo dice. Es lo que permite observar el estado "cargando" sin corrutinas.
        /// </summary>
        private sealed class StubLobbyDirectory : ILobbyDirectory
        {
            private Action<LobbyDirectoryResult> _pending;
            private LobbyDirectoryResult _next = LobbyDirectoryResult.Ok(LobbyList.Empty);

            public int RefreshCount;
            public int CancelledCount;

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
                if (pending == null) return;
                CancelledCount++;
                pending(LobbyDirectoryResult.Cancelled());
            }

            public void Tick(double nowUnix) { }

            /// Entrega el resultado encolado, como haría el directorio real al llegar la respuesta.
            public void Complete()
            {
                Action<LobbyDirectoryResult> pending = _pending;
                _pending = null;
                pending?.Invoke(_next);
            }
        }

        private static Lobby Make(string id, int players = 1, int maxPlayers = 8,
            string version = ClientVersion, int ping = 40, double updatedAt = 1000d, float ttl = 30f,
            bool password = false, string map = "Level 0", string region = "EU-West")
        {
            Assert.IsTrue(Lobby.TryCreate(id, "Server " + id, version, players, maxPlayers, map,
                region, ping, LobbyPrivacy.Public, password, "10.0.0.1", 7778, updatedAt, ttl,
                LobbyStatus.Waiting, out Lobby lobby));
            return lobby;
        }

        private static ServerBrowserViewModel NewViewModel(StubLobbyDirectory stub) =>
            new ServerBrowserViewModel(stub, ClientVersion);

        // ─── Refresco y estados ───

        [Test]
        public void StartsIdleWithNothingToShow()
        {
            var vm = NewViewModel(new StubLobbyDirectory());
            Assert.AreEqual(ServerBrowserState.Idle, vm.State);
            Assert.AreEqual(0, vm.Visible.Count);
            Assert.IsNull(vm.Selected);
        }

        [Test]
        public void RefreshShowsLoadingBeforeTheAnswerArrives()
        {
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);

            vm.Refresh(1000d);
            Assert.AreEqual(ServerBrowserState.Loading, vm.State, "el panel tiene que poder pintar la espera");
            Assert.IsTrue(vm.IsRefreshing);

            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[] { Make("a") }, 1000d)));
            stub.Complete();

            Assert.AreEqual(ServerBrowserState.Ready, vm.State);
            Assert.AreEqual(1, vm.Visible.Count);
            Assert.IsFalse(vm.IsRefreshing);
        }

        [Test]
        public void EmptyDirectoryIsNotAnError()
        {
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);

            vm.Refresh(1000d);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Empty));
            stub.Complete();

            Assert.AreEqual(ServerBrowserState.Empty, vm.State);
            Assert.AreEqual(ServerBrowserViewModel.NoServersMessage, vm.StatusMessage);
        }

        [Test]
        public void EmptyBecauseOfTheFilterSaysSo()
        {
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);
            vm.Filter.HideFull = true;

            vm.Refresh(1000d);
            stub.Enqueue(LobbyDirectoryResult.Ok(
                LobbyList.Create(new[] { Make("full", players: 8, maxPlayers: 8) }, 1000d)));
            stub.Complete();

            Assert.AreEqual(ServerBrowserState.Empty, vm.State);
            Assert.AreEqual(1, vm.TotalCount);
            Assert.AreEqual(1, vm.HiddenByFilterCount);
            Assert.AreEqual(ServerBrowserViewModel.FilteredOutMessage, vm.StatusMessage);
        }

        [Test]
        public void FailureKeepsTheOldListUnderTheError()
        {
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);

            vm.Refresh(1000d);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[] { Make("a") }, 1000d)));
            stub.Complete();

            vm.Refresh(1001d);
            stub.Enqueue(LobbyDirectoryResult.Failed("directorio caído"));
            stub.Complete();

            Assert.AreEqual(ServerBrowserState.Error, vm.State);
            Assert.AreEqual(ServerBrowserViewModel.DiscoveryFailedMessage, vm.StatusMessage);
            Assert.AreEqual("directorio caído", vm.ErrorDetail, "el motivo técnico se guarda aparte del aviso");
            Assert.AreEqual(1, vm.Visible.Count, "una lista vieja con un aviso es mejor que una tabla en blanco");
        }

        [Test]
        public void ASecondRefreshDiscardsTheAnswerOfTheFirst()
        {
            // Si la lenta contestara después de la nueva, repintaría la tabla con datos viejos.
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);

            vm.Refresh(1000d);
            vm.Refresh(1001d);
            Assert.AreEqual(1, stub.CancelledCount);

            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[] { Make("nuevo") }, 1001d)));
            stub.Complete();

            Assert.AreEqual(ServerBrowserState.Ready, vm.State);
            Assert.AreEqual("nuevo", vm.Visible[0].Id.Value);
        }

        [Test]
        public void CancellingDoesNotLookLikeAnError()
        {
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);

            vm.Refresh(1000d);
            vm.CancelRefresh();

            Assert.AreEqual(ServerBrowserState.Idle, vm.State);
            Assert.AreEqual("", vm.StatusMessage);
        }

        [Test]
        public void RaisesChangedSoTheUiCanRepaint()
        {
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);
            int changes = 0;
            vm.Changed += () => changes++;

            vm.Refresh(1000d);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[] { Make("a") }, 1000d)));
            stub.Complete();

            Assert.GreaterOrEqual(changes, 2, "al menos el paso a Loading y la llegada del resultado");
        }

        // ─── TTL ───

        [Test]
        public void ExpiredEntriesNeverEnterTheTable()
        {
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);

            vm.Refresh(1000d);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[]
            {
                Make("alive", updatedAt: 1000d, ttl: 30f),
                Make("ghost", updatedAt: 900d, ttl: 30f),
            }, 1000d)));
            stub.Complete();

            Assert.AreEqual(1, vm.Visible.Count);
            Assert.AreEqual("alive", vm.Visible[0].Id.Value);
        }

        [Test]
        public void ARowThatExpiresWhileYouLookAtItDisappearsOnItsOwn()
        {
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);

            vm.Refresh(1000d);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[]
            {
                Make("a", updatedAt: 1000d, ttl: 5f),
            }, 1000d)));
            stub.Complete();
            Assert.AreEqual(1, vm.Visible.Count);

            vm.Tick(1003d);
            Assert.AreEqual(1, vm.Visible.Count, "todavía dentro del TTL");

            vm.Tick(1006d);
            Assert.AreEqual(0, vm.Visible.Count);
            Assert.AreEqual(ServerBrowserState.Empty, vm.State,
                "quedarse en Ready con cero filas es la pantalla en blanco sin explicación");
        }

        // ─── Selección ───

        [Test]
        public void SelectsOnlyRowsThatAreActuallyVisible()
        {
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);

            vm.Refresh(1000d);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[] { Make("a"), Make("b") }, 1000d)));
            stub.Complete();

            Assert.IsTrue(vm.Select(new LobbyId("a")));
            Assert.AreEqual("a", vm.Selected.Id.Value);
            Assert.IsFalse(vm.Select(new LobbyId("no-existe")));
            Assert.AreEqual("a", vm.Selected.Id.Value, "un id inexistente no borra la selección buena");
        }

        [Test]
        public void SelectionSurvivesARefreshThatStillListsIt()
        {
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);

            vm.Refresh(1000d);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[] { Make("a", players: 1) }, 1000d)));
            stub.Complete();
            Assert.IsTrue(vm.Select(new LobbyId("a")));

            vm.Refresh(1001d);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[] { Make("a", players: 4) }, 1001d)));
            stub.Complete();

            Assert.AreEqual("a", vm.Selected.Id.Value);
            Assert.AreEqual(4, vm.Selected.Players, "la ficha se refresca, la selección no se pierde");
        }

        [Test]
        public void SelectionDiesWithTheLobby()
        {
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);

            vm.Refresh(1000d);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[]
            {
                Make("a", updatedAt: 1000d, ttl: 5f), Make("b"),
            }, 1000d)));
            stub.Complete();
            Assert.IsTrue(vm.Select(new LobbyId("a")));

            vm.Tick(1006d);
            Assert.IsNull(vm.Selected, "el botón de entrar no puede seguir apuntando a un fantasma");
            Assert.IsFalse(vm.SelectedId.IsValid);
        }

        [Test]
        public void FilteringAwayTheSelectedRowClearsTheSelection()
        {
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);

            vm.Refresh(1000d);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[]
            {
                Make("full", players: 8, maxPlayers: 8), Make("free"),
            }, 1000d)));
            stub.Complete();
            Assert.IsTrue(vm.Select(new LobbyId("full")));

            vm.Filter.HideFull = true;
            vm.ApplyFilterAndSort();

            Assert.IsNull(vm.Selected);
        }

        // ─── La frontera hacia la conexión ───

        [Test]
        public void OnlyAJoinableSelectionHandsOutAnEndpoint()
        {
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);

            vm.Refresh(1000d);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[]
            {
                Make("ok"),
                Make("full", players: 8, maxPlayers: 8),
                Make("old", version: "0.0.1"),
                Make("locked", password: true),
            }, 1000d)));
            stub.Complete();

            Assert.IsTrue(vm.Select(new LobbyId("ok")));
            Assert.IsTrue(vm.TryGetSelectedEndpoint(1000d, out LobbyEndpoint endpoint, out LobbyJoinability reason));
            Assert.AreEqual(LobbyJoinability.Joinable, reason);
            Assert.AreEqual("10.0.0.1", endpoint.Host);
            Assert.AreEqual(7778, endpoint.Port);

            AssertNotJoinable(vm, "full", LobbyJoinability.Full);
            AssertNotJoinable(vm, "old", LobbyJoinability.VersionMismatch);
            AssertNotJoinable(vm, "locked", LobbyJoinability.PasswordRequired);
        }

        [Test]
        public void NoSelectionHandsOutNothing()
        {
            var vm = NewViewModel(new StubLobbyDirectory());
            Assert.IsFalse(vm.TryGetSelectedEndpoint(1000d, out LobbyEndpoint endpoint, out _));
            Assert.IsFalse(endpoint.IsValid);
        }

        private static void AssertNotJoinable(ServerBrowserViewModel vm, string id, LobbyJoinability expected)
        {
            Assert.IsTrue(vm.Select(new LobbyId(id)));
            Assert.IsFalse(vm.TryGetSelectedEndpoint(1000d, out LobbyEndpoint endpoint, out LobbyJoinability reason));
            Assert.AreEqual(expected, reason);
            Assert.IsFalse(endpoint.IsValid);
        }

        // ─── Filtro y orden aplicados por el navegador ───

        [Test]
        public void AppliesSortWithoutTalkingToTheDirectoryAgain()
        {
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);
            vm.Sort.Key = LobbySortKey.Ping;

            vm.Refresh(1000d);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(new[]
            {
                Make("slow", ping: 300), Make("fast", ping: 20),
            }, 1000d)));
            stub.Complete();
            Assert.AreEqual("fast", vm.Visible[0].Id.Value);

            vm.Sort.Descending = true;
            vm.ApplyFilterAndSort();

            Assert.AreEqual("slow", vm.Visible[0].Id.Value);
            Assert.AreEqual(1, stub.RefreshCount, "reordenar no es volver a preguntar");
        }

        [Test]
        public void CorruptEntriesAreDroppedInsteadOfBreakingTheTable()
        {
            var stub = new StubLobbyDirectory();
            var vm = NewViewModel(stub);

            var raw = new List<Lobby> { Make("good"), null, null };
            vm.Refresh(1000d);
            stub.Enqueue(LobbyDirectoryResult.Ok(LobbyList.Create(raw, 1000d)));
            stub.Complete();

            Assert.AreEqual(1, vm.Visible.Count);
            Assert.AreEqual(ServerBrowserState.Ready, vm.State);
        }
    }
}
