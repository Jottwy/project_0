using System.Collections.Generic;
using BackroomsSurvival.Lobbies;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El descubrimiento por Steam, sin Steam. Lo que se prueba aquí es todo lo que puede
    /// equivocarse alrededor de la consulta: cero resultados que no son un error, Steam ausente,
    /// timeout, y una respuesta vieja que jamás puede pisar una consulta posterior.
    /// </summary>
    public sealed class SteamLobbyDirectoryTests
    {
        private const string Wire = "52";

        /// Steam de mentira con el gatillo en la mano.
        private sealed class FakeSteamQuery : ISteamLobbyQuery
        {
            public bool Available = true;
            public int BeginCount;
            public int AbandonCount;

            private SteamQueryState _state = SteamQueryState.Idle;
            private List<SteamLobbyRecord> _records;
            private string _error;

            public bool IsAvailable => Available;

            public void Begin(int maxResults)
            {
                BeginCount++;
                _state = SteamQueryState.Pending;
                _records = null;
                _error = null;
            }

            public SteamQueryState Poll(out IReadOnlyList<SteamLobbyRecord> records, out string error)
            {
                records = _records;
                error = _error;
                SteamQueryState state = _state;
                if (state == SteamQueryState.Completed || state == SteamQueryState.Failed)
                    _state = SteamQueryState.Idle;
                return state;
            }

            public void Abandon()
            {
                AbandonCount++;
                _state = SteamQueryState.Idle;
                _records = null;
                _error = null;
            }

            public void CompleteWith(params SteamLobbyRecord[] records)
            {
                _records = new List<SteamLobbyRecord>(records);
                _state = SteamQueryState.Completed;
            }

            public void FailWith(string error)
            {
                _error = error;
                _state = SteamQueryState.Failed;
            }

            /// La consulta se pierde por debajo: Steam se cayó entre Begin y Poll.
            public void GoIdle() => _state = SteamQueryState.Idle;
        }

        private static SteamLobbyRecord Record(ulong id = 1UL, string wire = Wire, string port = "7778") =>
            new SteamLobbyRecord
            {
                Id = id,
                ConnectIp = "192.168.1.40",
                ConnectPort = port,
                HostName = "Joel",
                Name = "Partida " + id,
                WireVersion = wire,
                Players = "1",
                MaxPlayers = "8",
                Map = "STP_Showcase",
                State = "open",
                AnnouncedAt = "1700000000",
            };

        private static LobbyDirectoryResult Capture(SteamLobbyDirectory directory, double now,
            System.Action between = null)
        {
            LobbyDirectoryResult captured = default;
            bool got = false;
            directory.Refresh(now, r => { captured = r; got = true; });
            between?.Invoke();
            directory.Tick(now);
            Assert.IsTrue(got, "el resultado tiene que llegar dentro de Tick");
            return captured;
        }

        [Test]
        public void ConvertsWhatSteamReturns()
        {
            var query = new FakeSteamQuery();
            var directory = new SteamLobbyDirectory(query);

            LobbyDirectoryResult result = Capture(directory, 1000d,
                () => query.CompleteWith(Record(1UL), Record(2UL)));

            Assert.AreEqual(LobbyDirectoryStatus.Ok, result.Status);
            Assert.AreEqual(2, result.Lobbies.Count);
            Assert.IsTrue(result.Lobbies.Contains(new LobbyId("steam-1")));
            Assert.AreEqual(1, query.BeginCount);
        }

        [Test]
        public void ZeroLobbiesIsAResultAndNotAnError()
        {
            var query = new FakeSteamQuery();
            var directory = new SteamLobbyDirectory(query);

            LobbyDirectoryResult result = Capture(directory, 1000d, () => query.CompleteWith());

            Assert.AreEqual(LobbyDirectoryStatus.Ok, result.Status);
            Assert.AreEqual(0, result.Lobbies.Count);
        }

        [Test]
        public void UnusableLobbiesAreDroppedWithoutLosingTheRest()
        {
            var query = new FakeSteamQuery();
            var directory = new SteamLobbyDirectory(query);

            LobbyDirectoryResult result = Capture(directory, 1000d, () => query.CompleteWith(
                Record(1UL),
                new SteamLobbyRecord { Id = 0UL, MaxPlayers = "8" },          // sin id
                new SteamLobbyRecord { Id = 9UL },                            // sin aforo
                Record(2UL, port: "no-soy-un-puerto")));                      // se lista igual

            Assert.AreEqual(LobbyDirectoryStatus.Ok, result.Status);
            Assert.AreEqual(2, result.Lobbies.Count);
            Assert.IsTrue(result.Lobbies.TryGet(new LobbyId("steam-2"), out Lobby broken));
            Assert.IsFalse(broken.Endpoint.IsValid);
        }

        [Test]
        public void SteamUnavailableIsACleanErrorAndNeverAnEmptyList()
        {
            // Es la regla que permite decirle al jugador "usa el Join por IP" en vez de enseñarle
            // una lista vacía que parece que no hay nadie jugando.
            var query = new FakeSteamQuery { Available = false };
            var directory = new SteamLobbyDirectory(query);

            LobbyDirectoryResult result = Capture(directory, 1000d);

            Assert.AreEqual(LobbyDirectoryStatus.Failed, result.Status);
            Assert.AreEqual(SteamLobbyDirectory.SteamUnavailableMessage, result.ErrorMessage);
            Assert.AreEqual(0, query.BeginCount, "sin Steam no se pregunta nada");
        }

        [Test]
        public void SteamUnavailableStillGoesThroughTheLoadingState()
        {
            // Contestar dentro de Refresh haría que "cargando" durara cero frames y la UI no lo
            // pudiera pintar nunca.
            var directory = new SteamLobbyDirectory(new FakeSteamQuery { Available = false });
            bool got = false;
            directory.Refresh(1000d, _ => got = true);

            Assert.IsFalse(got, "el resultado no puede llegar dentro de Refresh");
            Assert.IsTrue(directory.IsRefreshing);

            directory.Tick(1000d);
            Assert.IsTrue(got);
        }

        [Test]
        public void AFailedQueryIsReportedWithItsReason()
        {
            var query = new FakeSteamQuery();
            var directory = new SteamLobbyDirectory(query);

            LobbyDirectoryResult result = Capture(directory, 1000d, () => query.FailWith("Steam dijo que no"));

            Assert.AreEqual(LobbyDirectoryStatus.Failed, result.Status);
            Assert.AreEqual("Steam dijo que no", result.ErrorMessage);
        }

        [Test]
        public void AQueryThatNeverAnswersTimesOutInsteadOfHangingThePanel()
        {
            var query = new FakeSteamQuery();
            var directory = new SteamLobbyDirectory(query, 50, timeoutSeconds: 5d);

            LobbyDirectoryResult captured = default;
            bool got = false;
            directory.Refresh(1000d, r => { captured = r; got = true; });

            directory.Tick(1004d);
            Assert.IsFalse(got, "todavía dentro del plazo");

            directory.Tick(1006d);
            Assert.IsTrue(got);
            Assert.AreEqual(LobbyDirectoryStatus.Failed, captured.Status);
            Assert.AreEqual(SteamLobbyDirectory.TimeoutMessage, captured.ErrorMessage);
            Assert.AreEqual(1, query.AbandonCount, "una consulta que se abandona no puede seguir esperándose");
        }

        [Test]
        public void AQueryLostUnderneathDoesNotWaitForever()
        {
            // Steam se muere entre Begin y Poll: el estado vuelve a Idle y nadie va a contestar.
            var query = new FakeSteamQuery();
            var directory = new SteamLobbyDirectory(query);

            LobbyDirectoryResult result = Capture(directory, 1000d, () => query.GoIdle());

            Assert.AreEqual(LobbyDirectoryStatus.Failed, result.Status);
        }

        [Test]
        public void AStaleAnswerCanNeverOverwriteANewerQuery()
        {
            // El caso que rompe una lista: la consulta lenta contesta después de la nueva.
            var query = new FakeSteamQuery();
            var directory = new SteamLobbyDirectory(query);

            LobbyDirectoryResult first = default;
            int firstCalls = 0;
            directory.Refresh(1000d, r => { first = r; firstCalls++; });

            LobbyDirectoryResult second = default;
            bool secondGot = false;
            directory.Refresh(1001d, r => { second = r; secondGot = true; });

            Assert.AreEqual(1, firstCalls, "la primera se cierra al ser sustituida");
            Assert.AreEqual(LobbyDirectoryStatus.Cancelled, first.Status, "cancelada, que no es un error");

            query.CompleteWith(Record(7UL));
            directory.Tick(1001d);

            Assert.IsTrue(secondGot);
            Assert.AreEqual(1, firstCalls, "la vieja no puede contestar dos veces");
            Assert.IsTrue(second.Lobbies.Contains(new LobbyId("steam-7")));
        }

        [Test]
        public void CancellingIsNotAnErrorAndAbandonsTheQuery()
        {
            var query = new FakeSteamQuery();
            var directory = new SteamLobbyDirectory(query);

            LobbyDirectoryStatus status = LobbyDirectoryStatus.Ok;
            directory.Refresh(1000d, r => status = r.Status);
            directory.CancelRefresh();

            Assert.AreEqual(LobbyDirectoryStatus.Cancelled, status);
            Assert.IsFalse(directory.IsRefreshing);
            Assert.AreEqual(1, query.AbandonCount);

            directory.CancelRefresh(); // idempotente
            Assert.AreEqual(1, query.AbandonCount);
        }

        [Test]
        public void TickWithoutARequestDoesNothing()
        {
            var query = new FakeSteamQuery();
            var directory = new SteamLobbyDirectory(query);
            query.CompleteWith(Record());

            Assert.DoesNotThrow(() => directory.Tick(1000d));
            Assert.IsFalse(directory.IsRefreshing);
        }

        [Test]
        public void TheBrowserOnTopOfSteamBehavesLikeTheBrowserOnTopOfTheMock()
        {
            // El navegador no se entera de que ha cambiado el directorio: mismo filtro, mismo
            // orden, mismo TTL, mismo camino de entrada.
            var query = new FakeSteamQuery();
            var directory = new SteamLobbyDirectory(query);
            var browser = new ServerBrowserViewModel(directory, Wire);

            browser.Open(1000d);
            query.CompleteWith(Record(1UL), Record(2UL, wire: "51"));
            directory.Tick(1000d);

            Assert.AreEqual(ServerBrowserState.Ready, browser.State);
            Assert.AreEqual(2, browser.Visible.Count);

            browser.Filter.HideIncompatibleVersion = true;
            browser.ApplyFilterAndSort();
            Assert.AreEqual(1, browser.Visible.Count);
            Assert.AreEqual("steam-1", browser.Visible[0].Id.Value);
        }
    }
}
