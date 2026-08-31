using BackroomsSurvival.Lobbies;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El directorio de mentira. Se prueba porque es el que sostiene TODO lo demás mientras no
    /// exista el Lobby Directory real: si el catálogo deja de cubrir un caso, el navegador deja
    /// de poder probarse a mano sin que nadie se entere.
    /// </summary>
    public sealed class MockLobbyDirectoryTests
    {
        private const string ClientVersion = "1.2.3";

        private static MockLobbyDirectory Default() => MockLobbyDirectory.CreateDefault(ClientVersion);

        private static LobbyDirectoryResult RunOne(MockLobbyDirectory mock, double now)
        {
            LobbyDirectoryResult captured = default;
            bool got = false;
            mock.LatencySeconds = 0f;
            mock.Refresh(now, result =>
            {
                captured = result;
                got = true;
            });
            mock.Tick(now);
            Assert.IsTrue(got, "el resultado tiene que llegar dentro de Tick");
            return captured;
        }

        [Test]
        public void DoesNotAnswerBeforeItsSimulatedLatency()
        {
            var mock = Default();
            mock.LatencySeconds = 0.5f;
            bool got = false;
            mock.Refresh(1000d, _ => got = true);

            mock.Tick(1000.2d);
            Assert.IsFalse(got, "sin esto no se puede ver nunca el estado de carga");
            Assert.IsTrue(mock.IsRefreshing);

            mock.Tick(1000.6d);
            Assert.IsTrue(got);
            Assert.IsFalse(mock.IsRefreshing);
        }

        [Test]
        public void CancelDeliversCancelledAndNotAFailure()
        {
            var mock = Default();
            LobbyDirectoryStatus status = LobbyDirectoryStatus.Ok;
            mock.Refresh(1000d, result => status = result.Status);
            mock.CancelRefresh();

            Assert.AreEqual(LobbyDirectoryStatus.Cancelled, status);
            Assert.IsFalse(mock.IsRefreshing);
        }

        [Test]
        public void FailNextRefreshFailsExactlyOnce()
        {
            var mock = Default();
            mock.FailNextRefresh = true;

            LobbyDirectoryResult failed = RunOne(mock, 1000d);
            Assert.AreEqual(LobbyDirectoryStatus.Failed, failed.Status);
            Assert.AreEqual(MockLobbyDirectory.DefaultFailureMessage, failed.ErrorMessage);

            LobbyDirectoryResult ok = RunOne(mock, 1001d);
            Assert.AreEqual(LobbyDirectoryStatus.Ok, ok.Status);
            Assert.Greater(ok.Lobbies.Count, 0);
        }

        [Test]
        public void CatalogueCoversEveryCaseTheBrowserHasToPaint()
        {
            LobbyList list = RunOne(Default(), 1000d).Lobbies;

            Assert.IsTrue(HasMatch(list, l => l.IsEmpty), "servidor vacío");
            Assert.IsTrue(HasMatch(list, l => !l.IsEmpty && !l.IsFull), "servidor a medio llenar");
            Assert.IsTrue(HasMatch(list, l => l.IsFull), "servidor lleno");
            Assert.IsTrue(HasMatch(list, l => l.RequiresPassword), "servidor con contraseña");
            Assert.IsTrue(HasMatch(list, l => !l.IsCompatibleWith(ClientVersion)), "versión incompatible");
            Assert.IsTrue(HasMatch(list, l => l.HasPing && l.PingMs < 60), "ping bajo");
            Assert.IsTrue(HasMatch(list, l => l.HasPing && l.PingMs >= 60 && l.PingMs < 180), "ping medio");
            Assert.IsTrue(HasMatch(list, l => l.HasPing && l.PingMs >= 180), "ping alto");
            Assert.IsTrue(HasMatch(list, l => !l.HasPing), "ping sin medir");
            Assert.IsTrue(HasMatch(list, l => l.Privacy != LobbyPrivacy.Public), "lobby no público");
            Assert.IsTrue(HasMatch(list, l => l.Status == LobbyStatus.Closed), "partida cerrada");
            Assert.IsTrue(HasMatch(list, l => !l.Endpoint.IsValid), "endpoint inválido");
            Assert.GreaterOrEqual(CountDistinctMaps(list), 3, "varios mapas");
        }

        [Test]
        public void TheGhostServerIsAlreadyExpiredWhenItArrives()
        {
            // Está en la foto, pero caducado: es lo que hace que el podado por TTL se pueda ver
            // sin esperar 30 segundos reales.
            LobbyList list = RunOne(Default(), 1000d).Lobbies;
            Assert.IsTrue(list.TryGet(new LobbyId("eu-ghost-01"), out Lobby ghost));
            Assert.IsTrue(ghost.IsExpired(1000d));
            Assert.IsFalse(list.WithoutExpired(1000d).Contains(new LobbyId("eu-ghost-01")));
        }

        [Test]
        public void OneLobbyOnlyShowsUpFromTheSecondRefresh()
        {
            var mock = Default();
            var id = new LobbyId("eu-late-01");

            Assert.IsFalse(RunOne(mock, 1000d).Lobbies.Contains(id));
            Assert.IsTrue(RunOne(mock, 1001d).Lobbies.Contains(id), "el caso 'refresco y sale uno nuevo'");
            Assert.AreEqual(2, mock.RefreshCount);
        }

        [Test]
        public void SnapshotTimestampsAreRelativeToTheClockYouPassIn()
        {
            // Sin esto, un catálogo con fechas fijas caducaría entero al día siguiente y los
            // tests empezarían a fallar solos.
            var mock = Default();
            LobbyList early = mock.Snapshot(1000d);
            LobbyList late = mock.Snapshot(9_000_000d);

            Assert.IsTrue(early.TryGet(new LobbyId("eu-empty-01"), out Lobby a));
            Assert.IsTrue(late.TryGet(new LobbyId("eu-empty-01"), out Lobby b));
            Assert.AreEqual(1000d, a.UpdatedAtUnix);
            Assert.AreEqual(9_000_000d, b.UpdatedAtUnix);
            Assert.IsFalse(b.IsExpired(9_000_000d));
        }

        [Test]
        public void EveryTemplateSurvivesTheSameSanitiserAsRealData()
        {
            // CreateDefault construye por Lobby.TryCreate: una plantilla mal escrita revienta al
            // crear el mock, no en producción.
            Assert.DoesNotThrow(() => MockLobbyDirectory.CreateDefault(null));
            Assert.DoesNotThrow(() => MockLobbyDirectory.CreateDefault("  "));
        }

        private static bool HasMatch(LobbyList list, System.Func<Lobby, bool> predicate)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (predicate(list[i])) return true;
            }

            return false;
        }

        private static int CountDistinctMaps(LobbyList list)
        {
            var maps = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < list.Count; i++) maps.Add(list[i].Map);
            return maps.Count;
        }
    }
}
