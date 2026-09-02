using BackroomsSurvival.Lobbies;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El paso de "he elegido esta fila" a "conéctate a este host y puerto". No toca red: el
    /// destino se entrega a un <see cref="ILobbyJoinSink"/>, que en el juego es el panel de
    /// conexión y aquí es un doble.
    /// </summary>
    public sealed class LobbyJoinRouterTests
    {
        private const string ClientVersion = "1.2.3";

        private sealed class FakeSink : ILobbyJoinSink
        {
            public bool Accept = true;
            public string Refusal = "ocupado";
            public int Calls;
            public LobbyEndpoint LastEndpoint;
            public string LastPlayerName;

            /// ADR-117: el router entrega también la sesión de relay del lobby.
            public LobbyRelay LastRelay;

            public bool TryJoin(LobbyEndpoint endpoint, LobbyRelay relay, string playerName, out string failure)
            {
                Calls++;
                LastEndpoint = endpoint;
                LastRelay = relay;
                LastPlayerName = playerName;
                failure = Accept ? null : Refusal;
                return Accept;
            }
        }

        private static Lobby Make(int players = 1, int maxPlayers = 8, string version = ClientVersion,
            bool password = false, LobbyStatus status = LobbyStatus.Waiting, int port = 7778,
            double updatedAt = 1000d, float ttl = 30f)
        {
            Assert.IsTrue(Lobby.TryCreate("srv", "Server", version, players, maxPlayers, "Level 0",
                "EU-West", 40, LobbyPrivacy.Public, password, "10.0.0.5", port, updatedAt, ttl,
                status, out Lobby lobby));
            return lobby;
        }

        [Test]
        public void AJoinableLobbyReachesTheConnectionPathWithItsEndpoint()
        {
            var sink = new FakeSink();
            var router = new LobbyJoinRouter(sink, ClientVersion);

            LobbyJoinRequestResult result = router.Request(Make(), "Joel", 1000d);

            Assert.IsTrue(result.Started);
            Assert.AreEqual(1, sink.Calls);
            Assert.AreEqual("10.0.0.5", sink.LastEndpoint.Host);
            Assert.AreEqual(7778, sink.LastEndpoint.Port);
            Assert.AreEqual("Joel", sink.LastPlayerName);
        }

        [Test]
        public void NothingSelectedIsNotAnAttempt()
        {
            var sink = new FakeSink();
            LobbyJoinRequestResult result = new LobbyJoinRouter(sink, ClientVersion).Request(null, "Joel", 1000d);

            Assert.AreEqual(LobbyJoinRequestStatus.NoSelection, result.Status);
            Assert.AreEqual(0, sink.Calls);
        }

        [Test]
        public void PasswordProtectedIsRefusedHereAndNotByTheHandshake()
        {
            // El protocolo actual no lleva campo por el que mandar una contraseña. Intentarlo
            // sería comerse un rechazo que la UI no sabría explicar.
            var sink = new FakeSink();
            LobbyJoinRequestResult result =
                new LobbyJoinRouter(sink, ClientVersion).Request(Make(password: true), "Joel", 1000d);

            Assert.AreEqual(LobbyJoinRequestStatus.NotSupportedYet, result.Status);
            Assert.AreEqual(LobbyJoinability.PasswordRequired, result.Reason);
            Assert.AreEqual(0, sink.Calls, "no se llama a la conexión para que falle luego");
        }

        [Test]
        public void FullVersionExpiredClosedAndBrokenEndpointNeverReachTheConnectionPath()
        {
            var sink = new FakeSink();
            var router = new LobbyJoinRouter(sink, ClientVersion);

            AssertRejected(router, Make(players: 8, maxPlayers: 8), LobbyJoinability.Full);
            AssertRejected(router, Make(version: "0.0.9"), LobbyJoinability.VersionMismatch);
            AssertRejected(router, Make(updatedAt: 900d, ttl: 30f), LobbyJoinability.Expired);
            AssertRejected(router, Make(status: LobbyStatus.Closed), LobbyJoinability.Closed);
            AssertRejected(router, Make(port: 0), LobbyJoinability.InvalidEndpoint);

            Assert.AreEqual(0, sink.Calls);
        }

        [Test]
        public void ARefusalFromTheConnectionPathIsReportedVerbatim()
        {
            var sink = new FakeSink { Accept = false, Refusal = "Ya hay una sesión en marcha." };
            LobbyJoinRequestResult result =
                new LobbyJoinRouter(sink, ClientVersion).Request(Make(), "Joel", 1000d);

            Assert.AreEqual(LobbyJoinRequestStatus.SinkRefused, result.Status);
            Assert.AreEqual("Ya hay una sesión en marcha.", result.Message);
        }

        [Test]
        public void WithoutASinkNothingExplodes()
        {
            LobbyJoinRequestResult result = new LobbyJoinRouter(null, ClientVersion).Request(Make(), "Joel", 1000d);
            Assert.AreEqual(LobbyJoinRequestStatus.SinkRefused, result.Status);
        }

        [Test]
        public void EveryVerdictHasAnExplanationForTheHuman()
        {
            foreach (LobbyJoinability verdict in System.Enum.GetValues(typeof(LobbyJoinability)))
            {
                string text = LobbyJoinRouter.Explain(verdict);
                if (verdict == LobbyJoinability.Joinable)
                {
                    Assert.AreEqual("", text);
                    continue;
                }

                Assert.IsNotEmpty(text, "el motivo " + verdict + " se pinta en la fila y en el pie");
            }
        }

        private static void AssertRejected(LobbyJoinRouter router, Lobby lobby, LobbyJoinability expected)
        {
            LobbyJoinRequestResult result = router.Request(lobby, "Joel", 1000d);
            Assert.AreEqual(LobbyJoinRequestStatus.RejectedByLobby, result.Status);
            Assert.AreEqual(expected, result.Reason);
            Assert.IsNotEmpty(result.Message);
        }
    }
}
