using BackroomsSurvival.Lobbies;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-117 en el lado del lobby: qué hace falta para que una sesión de relay cuente, y qué
    /// cambia en el navegador cuando un host no tiene ningún endpoint directo que anunciar.
    ///
    /// Es el caso medido el 2026-09-02: dos personas en redes distintas, ninguno de los dos
    /// routers con UPnP, y el navegador enseñando `192.168.1.168:7778` a alguien que no podía
    /// llegar ahí ni queriendo.
    /// </summary>
    public sealed class RelayLobbyTests
    {
        private const string Token = "aabbccddeeff00112233445566778899";
        private const string RelayAddress = "203.0.113.9:7790";
        private const string Session = "12345678901234567890";

        private static LobbyRelay ValidRelay() => new LobbyRelay(RelayAddress, Session, Token);

        private static Lobby RelayOnlyLobby(string version = "55")
        {
            Assert.IsTrue(Lobby.TryCreate("steam-1", "A12ex", version, 1, 50, "Level0", "Unknown",
                Lobby.UnknownPing, LobbyPrivacy.Public, false,
                // Sin `connect_ip`: el host no tenía ningún endpoint defendible.
                null, 0, 1000d, 60f, LobbyStatus.Waiting, null, ValidRelay(), out Lobby lobby));
            return lobby;
        }

        // ─── Qué cuenta como sesión de relay ────────────────────────────────────────────────

        [Test]
        public void UnRelayCompletoEsValido()
        {
            LobbyRelay relay = ValidRelay();

            Assert.IsTrue(relay.IsValid);
            Assert.AreEqual(RelayAddress, relay.Address);
            Assert.AreEqual(Session, relay.Session);
        }

        [Test]
        public void MediaSesionDeRelayNoCuenta()
        {
            // Las tres claves valen como bloque. Media sesión no sirve para entrar —sin token el
            // relay deniega, sin id no hay a qué entrar— y admitirla convertiría un lobby mal
            // publicado en uno que promete algo que no puede cumplir.
            Assert.IsFalse(new LobbyRelay(null, Session, Token).IsValid, "sin dirección");
            Assert.IsFalse(new LobbyRelay(RelayAddress, null, Token).IsValid, "sin sesión");
            Assert.IsFalse(new LobbyRelay(RelayAddress, Session, null).IsValid, "sin token");
            Assert.IsFalse(new LobbyRelay("", "", "").IsValid, "vacías");
            Assert.IsFalse(LobbyRelay.None.IsValid);
        }

        [Test]
        public void UnTokenConLongitudQueNoEsLaDelBackendNoCuenta()
        {
            // El backend exige 32 hexadecimales (16 bytes). Uno más corto lo rechazaría el relay
            // tras gastar los doce segundos de la etapa; rechazarlo aquí cuesta cero.
            Assert.IsFalse(new LobbyRelay(RelayAddress, Session, "aabb").IsValid);
            Assert.IsFalse(new LobbyRelay(RelayAddress, Session, Token + "00").IsValid);
            Assert.AreEqual(32, LobbyRelay.TokenLength);
        }

        [Test]
        public void ElTokenNoAsomaAlPintarNiAlRegistrar()
        {
            // ADR-117 D9: el token no se escribe en ningún log, y `ToString` es justo por donde se
            // colaría —lo llama cada `Debug.Log` que interpole la sesión.
            string texto = ValidRelay().ToString();

            Assert.IsFalse(texto.Contains(Token), texto);
            StringAssert.Contains(RelayAddress, texto);
        }

        // ─── Lo que cambia en el navegador ──────────────────────────────────────────────────

        [Test]
        public void UnLobbySoloConRelaySePuedeEntrar()
        {
            // LA REGLA NUEVA (D7). Antes esto era `InvalidEndpoint` y el botón salía apagado, que
            // es exactamente lo que le habría pasado a Alejandro.
            Lobby lobby = RelayOnlyLobby();

            Assert.IsTrue(lobby.Relay.IsValid);
            Assert.IsFalse(lobby.Endpoint.IsValid, "no hay endpoint directo, y no hace falta");
            Assert.IsTrue(lobby.IsRelayOnly);
            Assert.IsTrue(lobby.HasSomeWayIn);
            Assert.AreEqual(LobbyJoinability.Joinable, lobby.EvaluateJoinability("55", 1000d));
        }

        [Test]
        public void UnLobbySinEndpointYSinRelaySigueSinPoderEntrarse()
        {
            // La defensa de ADR-112 intacta: publicar algo a lo que nadie puede entrar le cuesta al
            // jugador el tiempo de descubrirlo y no le ahorra nada.
            Assert.IsTrue(Lobby.TryCreate("steam-2", "Perdido", "55", 1, 50, "Level0", "Unknown",
                Lobby.UnknownPing, LobbyPrivacy.Public, false, null, 0, 1000d, 60f,
                LobbyStatus.Waiting, out Lobby lobby));

            Assert.IsFalse(lobby.HasSomeWayIn);
            Assert.IsFalse(lobby.IsRelayOnly, "sin relay no es relay-only, es inentrable");
            Assert.AreEqual(LobbyJoinability.InvalidEndpoint, lobby.EvaluateJoinability("55", 1000d));
        }

        [Test]
        public void ElRelayNoSaltaLasDemasReglasDeEntrada()
        {
            // Tener relay es tener CAMINO, no permiso. La versión, el aforo y la caducidad siguen
            // mandando exactamente igual — si no, un lobby con relay dejaría entrar a un build que
            // no sabe leer el mundo que sirve.
            Assert.AreEqual(LobbyJoinability.VersionMismatch,
                RelayOnlyLobby("54").EvaluateJoinability("55", 1000d));

            Assert.AreEqual(LobbyJoinability.Expired,
                RelayOnlyLobby().EvaluateJoinability("55", 5000d));

            Assert.IsTrue(Lobby.TryCreate("steam-3", "Lleno", "55", 50, 50, "Level0", "Unknown",
                Lobby.UnknownPing, LobbyPrivacy.Public, false, null, 0, 1000d, 60f,
                LobbyStatus.Waiting, null, ValidRelay(), out Lobby lleno));
            Assert.AreEqual(LobbyJoinability.Full, lleno.EvaluateJoinability("55", 1000d));
        }

        [Test]
        public void UnLobbyConLasDosViasNoEsRelayOnly()
        {
            // Lo normal cuando el host SÍ tiene UPnP: se anuncian las dos y el orden lo decide la
            // secuencia del backend, no el navegador.
            Assert.IsTrue(Lobby.TryCreate("steam-4", "ConUPnP", "55", 1, 50, "Level0", "Unknown",
                Lobby.UnknownPing, LobbyPrivacy.Public, false, "94.73.55.235", 7778, 1000d, 60f,
                LobbyStatus.Waiting, null, ValidRelay(), out Lobby lobby));

            Assert.IsTrue(lobby.Endpoint.IsValid);
            Assert.IsTrue(lobby.Relay.IsValid);
            Assert.IsFalse(lobby.IsRelayOnly);
            Assert.AreEqual(LobbyJoinability.Joinable, lobby.EvaluateJoinability("55", 1000d));
        }

        [Test]
        public void UnLobbySinRelayEsExactamenteLoQueEraAntesDeAdr117()
        {
            // Cero regresión: la partida en LAN de siempre.
            Assert.IsTrue(Lobby.TryCreate("steam-5", "EnLan", "55", 1, 50, "Level0", "Unknown",
                Lobby.UnknownPing, LobbyPrivacy.Public, false, "192.168.1.40", 7778, 1000d, 60f,
                LobbyStatus.Waiting, out Lobby lobby));

            Assert.IsFalse(lobby.Relay.IsValid);
            Assert.IsTrue(lobby.HasSomeWayIn);
            Assert.AreEqual(LobbyJoinability.Joinable, lobby.EvaluateJoinability("55", 1000d));
        }

        // ─── De Steam al modelo ─────────────────────────────────────────────────────────────

        [Test]
        public void LasTresClavesDeSteamLleganAlModelo()
        {
            var record = new SteamLobbyRecord
            {
                Id = 42UL,
                ConnectIp = "",
                ConnectPort = "",
                Name = "A12ex",
                WireVersion = "55",
                MaxPlayers = "50",
                Players = "1",
                State = "open",
                RelayAddr = RelayAddress,
                RelaySession = Session,
                RelayToken = Token,
            };

            Assert.IsTrue(SteamLobbyMapper.TryMap(record, 1000d, out Lobby lobby));
            Assert.IsTrue(lobby.Relay.IsValid);
            Assert.AreEqual(RelayAddress, lobby.Relay.Address);
            Assert.AreEqual(Session, lobby.Relay.Session);
            Assert.IsTrue(lobby.IsRelayOnly, "sin connect_ip y con relay");
        }

        [Test]
        public void UnLobbyDeSteamSinClavesDeRelayNoInventaNinguna()
        {
            // Es la mayoría de los lobbies mientras no haya relay desplegado, y tienen que seguir
            // funcionando igual.
            var record = new SteamLobbyRecord
            {
                Id = 43UL,
                ConnectIp = "192.168.1.40",
                ConnectPort = "7778",
                Name = "EnLan",
                WireVersion = "55",
                MaxPlayers = "50",
                Players = "1",
                State = "open",
            };

            Assert.IsTrue(SteamLobbyMapper.TryMap(record, 1000d, out Lobby lobby));
            Assert.IsFalse(lobby.Relay.IsValid);
            Assert.IsTrue(lobby.Endpoint.IsValid);
        }

        [Test]
        public void UnRelayAMediasQueLlegaDeSteamSeDescarta()
        {
            // Un host de otra versión, o una escritura de metadata a medias. No se puede confiar en
            // que lo que llega por Steam esté completo.
            var record = new SteamLobbyRecord
            {
                Id = 44UL,
                ConnectIp = "",
                ConnectPort = "",
                Name = "Roto",
                WireVersion = "55",
                MaxPlayers = "50",
                Players = "1",
                State = "open",
                RelayAddr = RelayAddress,
                RelaySession = Session,
                RelayToken = "", // la clave que falta
            };

            Assert.IsTrue(SteamLobbyMapper.TryMap(record, 1000d, out Lobby lobby));
            Assert.IsFalse(lobby.Relay.IsValid);
            Assert.AreEqual(LobbyJoinability.InvalidEndpoint, lobby.EvaluateJoinability("55", 1000d),
                "sin endpoint y con medio relay no hay por dónde entrar");
        }

        // ─── El anuncio del host ────────────────────────────────────────────────────────────

        [Test]
        public void ElHostAnunciaSuPartidaAunqueNoTengaEndpointSiTieneRelay()
        {
            var conRelay = new HostAnnouncementState(true, true, LobbyEndpoint.None, "A12ex", "55",
                1, 50, "Level0", LobbyStatus.Waiting, true);
            var sinNada = new HostAnnouncementState(true, true, LobbyEndpoint.None, "A12ex", "55",
                1, 50, "Level0", LobbyStatus.Waiting, false);

            Assert.IsTrue(conRelay.ShouldAnnounce, "con relay hay por dónde entrar");
            Assert.IsFalse(sinNada.ShouldAnnounce, "sin ninguna vía sigue sin anunciarse");
        }

        [Test]
        public void ElAnuncioSigueExigiendoSerHostYTenerSesionViva()
        {
            // El relay no relaja el ciclo de sesión: un lobby anunciado desde el menú sería un
            // lobby fantasma, que es justo lo que `HostAnnouncementDriver` existe para evitar.
            var noHost = new HostAnnouncementState(false, true, LobbyEndpoint.None, "x", "55",
                1, 50, "Level0", LobbyStatus.Waiting, true);
            var noEstablecida = new HostAnnouncementState(true, false, LobbyEndpoint.None, "x", "55",
                1, 50, "Level0", LobbyStatus.Waiting, true);

            Assert.IsFalse(noHost.ShouldAnnounce);
            Assert.IsFalse(noEstablecida.ShouldAnnounce);
        }
    }
}
