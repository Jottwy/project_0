using BackroomsSurvival.Lobbies;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// De lobby de Steam a ficha del navegador. Es la única parte del descubrimiento que puede
    /// equivocarse con los datos, y no necesita Steam para probarse.
    ///
    /// La versión que se compara es la del WIRE (`WireSchema.Expected`), no la del build: dos
    /// builds con el mismo `Application.version` y distinto esquema no se pueden hablar.
    /// </summary>
    public sealed class SteamLobbyMapperTests
    {
        private const string Wire = "52";

        private static SteamLobbyRecord Record(
            ulong id = 1234UL, string ip = "192.168.1.40", string port = "7778",
            string wire = Wire, string players = "2", string max = "8",
            string name = "Partida de Joel", string host = "Joel", string map = "STP_Showcase",
            string state = "open", int memberCount = 0, int memberCapacity = 0) =>
            new SteamLobbyRecord
            {
                Id = id,
                ConnectIp = ip,
                ConnectPort = port,
                HostName = host,
                Name = name,
                WireVersion = wire,
                Players = players,
                MaxPlayers = max,
                Map = map,
                State = state,
                AnnouncedAt = "1700000000",
                MemberCount = memberCount,
                MemberCapacity = memberCapacity,
            };

        [Test]
        public void ConvertsAValidLobby()
        {
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(), 1000d, out Lobby lobby));

            Assert.AreEqual("steam-1234", lobby.Id.Value);
            Assert.AreEqual("Partida de Joel", lobby.Name);
            Assert.AreEqual(Wire, lobby.Version);
            Assert.AreEqual(2, lobby.Players);
            Assert.AreEqual(8, lobby.MaxPlayers);
            Assert.AreEqual("STP_Showcase", lobby.Map);
            Assert.AreEqual("192.168.1.40", lobby.Endpoint.Host);
            Assert.AreEqual(7778, lobby.Endpoint.Port);
            Assert.AreEqual(LobbyStatus.Waiting, lobby.Status);
            Assert.AreEqual(LobbyJoinability.Joinable, lobby.EvaluateJoinability(Wire, 1000d));
        }

        [Test]
        public void RejectsALobbyWithoutIdOrCapacity()
        {
            Assert.IsFalse(SteamLobbyMapper.TryMap(Record(id: 0UL), 1000d, out _));
            Assert.IsFalse(SteamLobbyMapper.TryMap(Record(max: "", memberCapacity: 0), 1000d, out _),
                "sin aforo por metadato ni por Steam no hay ficha que pintar");
            Assert.IsFalse(SteamLobbyMapper.TryMap(Record(max: "0", memberCapacity: 0), 1000d, out _));
        }

        [Test]
        public void FallsBackToWhatSteamKnowsWhenTheHostPublishedNoCounters()
        {
            // Metadatos opcionales ausentes NO tiran la ficha: Steam ya sabe cuántos hay dentro.
            Assert.IsTrue(SteamLobbyMapper.TryMap(
                Record(players: "", max: "", memberCount: 3, memberCapacity: 8), 1000d, out Lobby lobby));

            Assert.AreEqual(3, lobby.Players);
            Assert.AreEqual(8, lobby.MaxPlayers);
        }

        [Test]
        public void FallsBackToTheHostPersonaWhenThereIsNoLobbyName()
        {
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(name: "", host: "Joel"), 1000d, out Lobby lobby));
            Assert.AreEqual("Joel", lobby.Name);

            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(name: "", host: ""), 1000d, out Lobby anon));
            Assert.AreEqual(SteamLobbyMapper.DefaultName, anon.Name);
        }

        [Test]
        public void ALobbyWithoutWireVersionIsVisibleButNotJoinable()
        {
            // Rellenarlo con la nuestra sería inventarse que es compatible. Y con el App ID 480
            // compartido, un lobby sin nuestra marca puede ser de otro juego entero.
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(wire: ""), 1000d, out Lobby lobby));
            Assert.AreEqual(LobbyJoinability.VersionMismatch, lobby.EvaluateJoinability(Wire, 1000d));
        }

        [Test]
        public void AnIncompatibleWireVersionIsVisibleButNotJoinable()
        {
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(wire: "51"), 1000d, out Lobby lobby));
            Assert.AreEqual("51", lobby.Version);
            Assert.AreEqual(LobbyJoinability.VersionMismatch, lobby.EvaluateJoinability(Wire, 1000d));
        }

        [Test]
        public void AFullLobbyIsVisibleButNotJoinable()
        {
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(players: "8", max: "8"), 1000d, out Lobby lobby));
            Assert.IsTrue(lobby.IsFull);
            Assert.AreEqual(LobbyJoinability.Full, lobby.EvaluateJoinability(Wire, 1000d));
        }

        [Test]
        public void AnUnreadablePortIsVisibleButNotJoinable()
        {
            // Se lista a propósito: el jugador tiene que ver que ese servidor existe y está mal
            // anunciado, no quedarse buscándolo.
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(port: "no-soy-un-puerto"), 1000d, out Lobby lobby));
            Assert.IsFalse(lobby.Endpoint.IsValid);
            Assert.AreEqual(LobbyJoinability.InvalidEndpoint, lobby.EvaluateJoinability(Wire, 1000d));

            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(ip: ""), 1000d, out Lobby noIp));
            Assert.AreEqual(LobbyJoinability.InvalidEndpoint, noIp.EvaluateJoinability(Wire, 1000d));
        }

        [Test]
        public void TheTimestampIsOurClockAndNotTheHosts()
        {
            // `bs_at` viene del reloj del host y dos relojes distintos no se pueden restar. El TTL
            // mide "cuánto hace que lo vimos", que es lo único que este cliente puede afirmar.
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(), 5000d, out Lobby lobby));

            Assert.AreEqual(5000d, lobby.UpdatedAtUnix);
            Assert.AreEqual(SteamLobbyMapper.LobbyTtlSeconds, lobby.TtlSeconds);
            Assert.IsFalse(lobby.IsExpired(5000d));
            Assert.IsTrue(lobby.IsExpired(5000d + SteamLobbyMapper.LobbyTtlSeconds + 1d),
                "una lista olvidada tiene que envejecer");
        }

        [Test]
        public void SteamGivesNoPingSoThereIsNoPing()
        {
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(), 1000d, out Lobby lobby));
            Assert.IsFalse(lobby.HasPing, "inventar un ping haría mentir al orden por ping");
            Assert.AreEqual(Lobby.UnknownPing, lobby.PingMs);
        }

        [Test]
        public void StateRoundTripsThroughTheMetadataValue()
        {
            Assert.AreEqual(LobbyStatus.Waiting, SteamLobbyMapper.ParseState(SteamLobbyMapper.StateToText(LobbyStatus.Waiting)));
            Assert.AreEqual(LobbyStatus.InProgress, SteamLobbyMapper.ParseState(SteamLobbyMapper.StateToText(LobbyStatus.InProgress)));
            Assert.AreEqual(LobbyStatus.Closed, SteamLobbyMapper.ParseState(SteamLobbyMapper.StateToText(LobbyStatus.Closed)));
            Assert.AreEqual(LobbyStatus.Unknown, SteamLobbyMapper.ParseState(""));
            Assert.AreEqual(LobbyStatus.Unknown, SteamLobbyMapper.ParseState("basura"));
            Assert.AreEqual(LobbyStatus.Waiting, SteamLobbyMapper.ParseState("  OPEN  "));
        }

        [Test]
        public void AClosedLobbyIsVisibleButNotJoinable()
        {
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(state: "closed"), 1000d, out Lobby lobby));
            Assert.AreEqual(LobbyJoinability.Closed, lobby.EvaluateJoinability(Wire, 1000d));
        }

        [Test]
        public void ClampsAbsurdCountersInsteadOfPrintingThem()
        {
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(players: "99", max: "8"), 1000d, out Lobby lobby));
            Assert.AreEqual(8, lobby.Players);

            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(players: "-3", max: "8"), 1000d, out Lobby negative));
            Assert.AreEqual(0, negative.Players);
        }
    }
}
