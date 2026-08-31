using BackroomsSurvival.Lobbies;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El circuito `HOST → publicador → directorio → navegador`, entero y en memoria.
    ///
    /// Lo que estos tests demuestran es la FORMA del interfaz, no que exista descubrimiento: el
    /// publicador de verdad no existe todavía (no hay servicio de lobbies, ni LAN, ni enumeración
    /// de lobbies de Steam). Sirven para que el día que aparezca no haya que inventarse el
    /// contrato con prisa, y para que el TTL esté probado del lado del que anuncia.
    /// </summary>
    public sealed class LobbyPublisherTests
    {
        private const string ClientVersion = "1.2.3";

        private static LobbyPublication Publication(int maxPlayers = 8, string host = "192.168.1.40",
            int port = 7778, float ttl = 30f) =>
            new LobbyPublication("Partida de Joel", ClientVersion, maxPlayers, "Level 0", "EU-West",
                LobbyPrivacy.Public, false, new LobbyEndpoint(host, port), ttl);

        [Test]
        public void PublishesAJoinableLobbyIntoTheDirectory()
        {
            var directory = new MockLobbyDirectory();
            var publisher = new MockLobbyPublisher(directory);

            Assert.IsTrue(publisher.Publish(Publication(), players: 1, LobbyStatus.Waiting, 1000d));
            Assert.IsTrue(publisher.IsPublishing);

            LobbyList list = directory.Snapshot(1000d);
            Assert.AreEqual(1, list.Count);
            Assert.IsTrue(list.TryGet(publisher.PublishedId, out Lobby published));
            Assert.AreEqual("Partida de Joel", published.Name);
            Assert.AreEqual(1, published.Players);
            Assert.AreEqual(8, published.MaxPlayers);
            Assert.AreEqual("192.168.1.40", published.Endpoint.Host);
            Assert.AreEqual(LobbyJoinability.Joinable,
                published.EvaluateJoinability(ClientVersion, 1000d));
        }

        [Test]
        public void TheIdIsDerivedFromTheEndpointSoRepublishingIsTheSameRow()
        {
            var directory = new MockLobbyDirectory();
            var publisher = new MockLobbyPublisher(directory);

            publisher.Publish(Publication(), 1, LobbyStatus.Waiting, 1000d);
            LobbyId first = publisher.PublishedId;
            publisher.Publish(Publication(), 2, LobbyStatus.InProgress, 1001d);

            Assert.AreEqual(first, publisher.PublishedId);
            Assert.AreEqual(1, directory.Snapshot(1001d).Count, "republicar no puede duplicar la fila");
        }

        [Test]
        public void TouchUpdatesPlayersAndKeepsTheAnnouncementAlive()
        {
            var directory = new MockLobbyDirectory();
            var publisher = new MockLobbyPublisher(directory);
            publisher.Publish(Publication(ttl: 10f), 1, LobbyStatus.Waiting, 1000d);

            publisher.Touch(players: 4, LobbyStatus.InProgress, 1008d);

            LobbyList list = directory.Snapshot(1009d).WithoutExpired(1009d);
            Assert.AreEqual(1, list.Count);
            Assert.IsTrue(list.TryGet(publisher.PublishedId, out Lobby published));
            Assert.AreEqual(4, published.Players);
            Assert.AreEqual(LobbyStatus.InProgress, published.Status);
        }

        [Test]
        public void AnAnnouncementNobodyRenewsExpiresOnItsOwn()
        {
            // Un directorio no se entera de que un host MUERE, sólo de que deja de anunciarse.
            var directory = new MockLobbyDirectory();
            var publisher = new MockLobbyPublisher(directory);
            publisher.Publish(Publication(ttl: 10f), 1, LobbyStatus.Waiting, 1000d);

            Assert.AreEqual(1, directory.Snapshot(1005d).WithoutExpired(1005d).Count);
            Assert.AreEqual(0, directory.Snapshot(1050d).WithoutExpired(1050d).Count);
        }

        [Test]
        public void WithdrawRemovesItImmediatelyAndIsIdempotent()
        {
            var directory = new MockLobbyDirectory();
            var publisher = new MockLobbyPublisher(directory);
            publisher.Publish(Publication(), 1, LobbyStatus.Waiting, 1000d);

            publisher.Withdraw();
            publisher.Withdraw();

            Assert.IsFalse(publisher.IsPublishing);
            Assert.AreEqual(0, directory.Snapshot(1000d).Count);
            Assert.AreEqual(LobbyId.None, publisher.PublishedId);
        }

        [Test]
        public void RejectsAPublicationThatWouldNotSurviveTheSanitiser()
        {
            var directory = new MockLobbyDirectory();
            var publisher = new MockLobbyPublisher(directory);

            Assert.IsFalse(publisher.Publish(Publication(maxPlayers: 0), 0, LobbyStatus.Waiting, 1000d));
            Assert.IsFalse(publisher.IsPublishing);
            Assert.AreEqual(0, directory.Snapshot(1000d).Count);
        }

        [Test]
        public void TheWholeCircuitEndsInTheBrowserWithAJoinableRow()
        {
            // HOST → publicador → directorio → navegador, sin red y sin Unity.
            var directory = new MockLobbyDirectory { LatencySeconds = 0f };
            var publisher = new MockLobbyPublisher(directory);
            publisher.Publish(Publication(), 1, LobbyStatus.Waiting, 1000d);

            var browser = new ServerBrowserViewModel(directory, ClientVersion);
            browser.Open(1000d);
            directory.Tick(1000d);

            Assert.AreEqual(ServerBrowserState.Ready, browser.State);
            Assert.AreEqual(1, browser.Visible.Count);
            Assert.IsTrue(browser.Select(publisher.PublishedId));
            Assert.IsTrue(browser.TryGetSelectedEndpoint(1000d, out LobbyEndpoint endpoint, out _));
            Assert.AreEqual("192.168.1.40", endpoint.Host);
            Assert.AreEqual(7778, endpoint.Port);
        }
    }
}
