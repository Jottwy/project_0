using BackroomsSurvival.Lobbies;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El modelo del navegador de servidores: saneado de datos ajenos, TTL y el veredicto de
    /// "¿puedo entrar?". Nada de esto necesita servidor, ni Unity, ni red.
    /// </summary>
    public sealed class LobbyModelTests
    {
        private const string ClientVersion = "1.2.3";

        private static Lobby Make(
            string id = "a", int players = 1, int maxPlayers = 8, string version = ClientVersion,
            bool password = false, LobbyPrivacy privacy = LobbyPrivacy.Public,
            LobbyStatus status = LobbyStatus.Waiting, string host = "10.0.0.1", int port = 7778,
            double updatedAt = 1000d, float ttl = 30f, int ping = 40,
            string map = "Level 0", string region = "EU-West", string name = null)
        {
            Assert.IsTrue(Lobby.TryCreate(id, name ?? ("Server " + id), version, players, maxPlayers,
                map, region, ping, privacy, password, host, port, updatedAt, ttl, status, out Lobby lobby));
            return lobby;
        }

        // ─── LobbyId ───

        [Test]
        public void LobbyIdIgnoresCaseAndSurroundingWhitespace()
        {
            Assert.AreEqual(new LobbyId("AB-01"), new LobbyId("  ab-01 "));
            Assert.AreEqual(new LobbyId("AB-01").GetHashCode(), new LobbyId("ab-01").GetHashCode());
        }

        [Test]
        public void BlankLobbyIdIsNotValid()
        {
            Assert.IsFalse(new LobbyId("   ").IsValid);
            Assert.IsFalse(new LobbyId(null).IsValid);
            Assert.AreEqual(LobbyId.None, new LobbyId(""));
        }

        // ─── Datos corruptos o incompletos ───

        [Test]
        public void RejectsLobbyWithoutId()
        {
            Assert.IsFalse(Lobby.TryCreate("  ", "x", ClientVersion, 1, 4, "m", "r", 10,
                LobbyPrivacy.Public, false, "10.0.0.1", 7778, 0d, 30f, LobbyStatus.Waiting, out _));
        }

        [Test]
        public void RejectsLobbyWithoutCapacity()
        {
            Assert.IsFalse(Lobby.TryCreate("a", "x", ClientVersion, 0, 0, "m", "r", 10,
                LobbyPrivacy.Public, false, "10.0.0.1", 7778, 0d, 30f, LobbyStatus.Waiting, out _));
        }

        [Test]
        public void ClampsPlayerCountIntoCapacity()
        {
            // Un servidor que anuncia 99/8 no es un servidor con 99 jugadores: es un contador
            // roto. Se recorta al aforo en vez de pintar "99/8" en la tabla.
            Lobby lobby = Make(players: 99, maxPlayers: 8);
            Assert.AreEqual(8, lobby.Players);
            Assert.IsTrue(lobby.IsFull);

            Lobby negative = Make(players: -5);
            Assert.AreEqual(0, negative.Players);
            Assert.IsTrue(negative.IsEmpty);
        }

        [Test]
        public void FillsMissingTextWithSomethingPrintable()
        {
            Assert.IsTrue(Lobby.TryCreate("srv-7", null, null, 1, 4, null, null, -9,
                LobbyPrivacy.Public, false, null, 0, -1d, 0f, LobbyStatus.Unknown, out Lobby lobby));

            Assert.AreEqual("srv-7", lobby.Name, "sin nombre, el id es mejor que una fila en blanco");
            Assert.AreEqual("Unknown", lobby.Map);
            Assert.AreEqual("Unknown", lobby.Region);
            Assert.AreEqual("", lobby.Version);
            Assert.AreEqual(Lobby.UnknownPing, lobby.PingMs);
            Assert.IsFalse(lobby.HasPing);
            Assert.AreEqual(Lobby.DefaultTtlSeconds, lobby.TtlSeconds);
            Assert.AreEqual(0d, lobby.UpdatedAtUnix);
            Assert.IsFalse(lobby.Endpoint.IsValid);
        }

        [Test]
        public void TruncatesAbsurdlyLongNames()
        {
            string huge = new string('x', 400);
            Lobby lobby = Make(name: huge);
            Assert.AreEqual(Lobby.MaxNameLength, lobby.Name.Length);
        }

        [Test]
        public void KeepsBrokenEndpointVisibleButNotJoinable()
        {
            // Esconderlo dejaría al jugador buscando un servidor que SÍ está anunciado.
            Lobby lobby = Make(port: 0);
            Assert.IsFalse(lobby.Endpoint.IsValid);
            Assert.AreEqual(LobbyJoinability.InvalidEndpoint,
                lobby.EvaluateJoinability(ClientVersion, 1000d));
        }

        // ─── TTL ───

        [Test]
        public void ExpiresOnlyAfterTheTtlElapses()
        {
            Lobby lobby = Make(updatedAt: 1000d, ttl: 30f);
            Assert.IsFalse(lobby.IsExpired(1029d));
            Assert.IsFalse(lobby.IsExpired(1030d), "el borde exacto todavía es válido");
            Assert.IsTrue(lobby.IsExpired(1030.1d));
        }

        [Test]
        public void ZeroTtlMeansNeverExpires()
        {
            var lobby = new Lobby(new LobbyId("a"), "n", ClientVersion, 1, 8, "m", "r", 10,
                LobbyPrivacy.Public, false, new LobbyEndpoint("10.0.0.1", 7778), 0d, 0f,
                LobbyStatus.Waiting);
            Assert.IsFalse(lobby.IsExpired(999999d));
        }

        [Test]
        public void ExpiredBeatsEveryOtherReason()
        {
            // Una ficha caducada no describe nada: no tiene sentido decir "está llena".
            Lobby lobby = Make(players: 8, maxPlayers: 8, password: true, version: "otra",
                updatedAt: 1000d, ttl: 10f);
            Assert.AreEqual(LobbyJoinability.Expired, lobby.EvaluateJoinability(ClientVersion, 2000d));
        }

        // ─── Veredicto de entrada ───

        [Test]
        public void JoinableWhenThereIsRoomAndVersionsMatch()
        {
            Assert.AreEqual(LobbyJoinability.Joinable,
                Make().EvaluateJoinability(ClientVersion, 1000d));
        }

        [Test]
        public void FullServerIsNotJoinable()
        {
            Assert.AreEqual(LobbyJoinability.Full,
                Make(players: 8, maxPlayers: 8).EvaluateJoinability(ClientVersion, 1000d));
        }

        [Test]
        public void VersionMismatchWinsOverBeingFull()
        {
            // El orden importa: un servidor lleno de otra versión no se arregla esperando hueco.
            Lobby lobby = Make(players: 8, maxPlayers: 8, version: "9.9.9");
            Assert.AreEqual(LobbyJoinability.VersionMismatch,
                lobby.EvaluateJoinability(ClientVersion, 1000d));
        }

        [Test]
        public void VersionComparisonIsExactAndOrdinal()
        {
            Assert.IsTrue(Make(version: "1.2.3").IsCompatibleWith(" 1.2.3 "));
            Assert.IsFalse(Make(version: "1.2.3").IsCompatibleWith("1.2.30"));
            Assert.IsFalse(Make(version: "1.2.3").IsCompatibleWith("1.2.3-rc1"));
            Assert.IsFalse(Make(version: "").IsCompatibleWith(ClientVersion),
                "sin versión anunciada no se puede afirmar compatibilidad");
        }

        [Test]
        public void PasswordIsTheLastGate()
        {
            Lobby lobby = Make(password: true);
            Assert.AreEqual(LobbyJoinability.PasswordRequired,
                lobby.EvaluateJoinability(ClientVersion, 1000d));
            Assert.AreEqual(LobbyJoinability.Joinable,
                lobby.EvaluateJoinability(ClientVersion, 1000d, passwordSupplied: true));
        }

        [Test]
        public void ClosedAndPrivateAreRejectedBeforeLookingAtTheEndpoint()
        {
            Assert.AreEqual(LobbyJoinability.Closed,
                Make(status: LobbyStatus.Closed, port: 0).EvaluateJoinability(ClientVersion, 1000d));
            Assert.AreEqual(LobbyJoinability.Private,
                Make(privacy: LobbyPrivacy.Private, port: 0).EvaluateJoinability(ClientVersion, 1000d));
        }

        [Test]
        public void FriendsOnlyStillJoinableIfItReachesTheList()
        {
            // El directorio no debería anunciarlo, pero si lo anuncia el cliente no inventa una
            // regla que el servidor no ha declarado.
            Assert.AreEqual(LobbyJoinability.Joinable,
                Make(privacy: LobbyPrivacy.FriendsOnly).EvaluateJoinability(ClientVersion, 1000d));
        }

        // ─── LobbyList ───

        [Test]
        public void ListDropsNullsAndKeepsTheFreshestDuplicate()
        {
            LobbyList list = LobbyList.Create(new[]
            {
                Make("dup", players: 1, updatedAt: 1000d),
                null,
                Make("dup", players: 7, updatedAt: 1100d),
                Make("other"),
            }, 1100d);

            Assert.AreEqual(2, list.Count);
            Assert.IsTrue(list.TryGet(new LobbyId("DUP"), out Lobby dup));
            Assert.AreEqual(7, dup.Players, "dos anuncios del mismo id son el mismo servidor");
        }

        [Test]
        public void ListPrunesExpiredEntries()
        {
            LobbyList list = LobbyList.Create(new[]
            {
                Make("alive", updatedAt: 1000d, ttl: 30f),
                Make("ghost", updatedAt: 900d, ttl: 30f),
            }, 1000d);

            LobbyList pruned = list.WithoutExpired(1000d);
            Assert.AreEqual(1, pruned.Count);
            Assert.IsFalse(pruned.Contains(new LobbyId("ghost")));
            Assert.AreEqual(2, list.Count, "la foto original es inmutable");
        }

        [Test]
        public void PruningNothingReturnsTheSameInstance()
        {
            LobbyList list = LobbyList.Create(new[] { Make("a", updatedAt: 1000d) }, 1000d);
            Assert.AreSame(list, list.WithoutExpired(1000d));
        }
    }
}
