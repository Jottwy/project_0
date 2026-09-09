using BackroomsSurvival.Lobbies;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// La vía Steam en el lobby — ADR-135 D5 y D4'.5: `bs_steam_host` y `bs_steam_auth`.
    ///
    /// Lo que se prueba es lo que rompería la vía sin dar un error: que media vía no cuente, que
    /// un `SteamId` ilegible no se convierta en una llamada a nadie, que un lobby con SÓLO Steam
    /// sea entrable, y que el host lo publique cuando toca.
    /// </summary>
    public sealed class SteamHostLobbyTests
    {
        private const ulong HostId = 76561198000000042UL;
        private const string Secret = "00112233445566778899aabbccddeeff";

        private static SteamLobbyRecord Record(string steamHost, string steamAuth,
            string connectIp = "", string connectPort = "")
        {
            return new SteamLobbyRecord
            {
                Id = 900100200300UL,
                ConnectIp = connectIp,
                ConnectPort = connectPort,
                Name = "Partida",
                WireVersion = "61",
                Players = "1",
                MaxPlayers = "8",
                Map = "Level 0",
                State = "open",
                SteamHost = steamHost,
                SteamAuth = steamAuth,
            };
        }

        // ─── El bloque: o las dos claves, o no hay vía ───

        [Test]
        public void Con_las_dos_claves_la_via_vale()
        {
            var via = new LobbySteamHost(HostId, Secret);

            Assert.IsTrue(via.IsValid);
            Assert.AreEqual(HostId, via.SteamId);
        }

        [Test]
        public void Media_via_no_cuenta()
        {
            // Sin secreto el host cierra la conexión en el primer mensaje, y sin id no hay a quién
            // llamar: las dos mitades solas prometen algo que no se puede cumplir.
            Assert.IsFalse(new LobbySteamHost(HostId, null).IsValid);
            Assert.IsFalse(new LobbySteamHost(HostId, "").IsValid);
            Assert.IsFalse(new LobbySteamHost(0UL, Secret).IsValid);
            Assert.IsFalse(LobbySteamHost.None.IsValid);
        }

        [Test]
        public void Un_secreto_de_longitud_rara_no_vale()
        {
            Assert.IsFalse(new LobbySteamHost(HostId, "corto").IsValid);
            Assert.IsFalse(new LobbySteamHost(HostId, Secret + "00").IsValid);
        }

        [Test]
        public void El_secreto_no_sale_al_pintar_la_via()
        {
            // D4'.6: se pinta y se registra, así que no puede llevar el secreto dentro.
            string texto = new LobbySteamHost(HostId, Secret).ToString();

            Assert.IsFalse(texto.Contains(Secret));
            StringAssert.Contains(HostId.ToString(), texto);
        }

        // ─── La lectura por consulta (D4'.1) ───

        [Test]
        public void Un_lobby_con_las_dos_claves_trae_su_via_steam()
        {
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(HostId.ToString(), Secret), 1000d, out Lobby lobby));

            Assert.IsTrue(lobby.SteamHost.IsValid);
            Assert.AreEqual(HostId, lobby.SteamHost.SteamId);
            Assert.AreEqual(Secret, lobby.SteamHost.Secret);
        }

        [Test]
        public void Un_steam_id_ilegible_deja_la_via_sin_existir_en_vez_de_llamar_a_cualquiera()
        {
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record("no-soy-un-id", Secret), 1000d, out Lobby lobby));

            Assert.IsFalse(lobby.SteamHost.IsValid);
            Assert.AreEqual(0UL, lobby.SteamHost.SteamId);
        }

        [Test]
        public void Un_lobby_sin_las_claves_es_exactamente_lo_que_era_antes_de_ADR_135()
        {
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record("", ""), 1000d, out Lobby lobby));

            Assert.IsFalse(lobby.SteamHost.IsValid);
        }

        // ─── Un lobby Steam-only se puede entrar ───

        [Test]
        public void Sin_endpoint_ni_relay_pero_con_steam_hay_por_donde_entrar()
        {
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(HostId.ToString(), Secret), 1000d, out Lobby lobby));

            Assert.IsTrue(lobby.HasSomeWayIn);
            Assert.AreEqual(LobbyJoinability.Joinable, lobby.EvaluateJoinability("61", 1000d));
        }

        [Test]
        public void Sin_ninguna_via_sigue_sin_poder_entrarse()
        {
            // La defensa de ADR-112 intacta: un lobby al que nadie puede entrar no engaña a nadie.
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record("", ""), 1000d, out Lobby lobby));

            Assert.IsFalse(lobby.HasSomeWayIn);
            Assert.AreEqual(LobbyJoinability.InvalidEndpoint, lobby.EvaluateJoinability("61", 1000d));
        }

        [Test]
        public void La_via_steam_sobrevive_a_las_copias_del_modelo()
        {
            // `WithPing` y `WithUpdatedAt` los usa el refresco del navegador: perder la vía ahí
            // dejaría un lobby entrable convertido en uno que no lo es, y sólo al segundo refresco.
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record(HostId.ToString(), Secret), 1000d, out Lobby lobby));

            Assert.IsTrue(lobby.WithPing(42).SteamHost.IsValid);
            Assert.IsTrue(lobby.WithUpdatedAt(2000d).SteamHost.IsValid);
        }

        // ─── La publicación por el host ───

        private sealed class FakeHost : ISteamLobbyHost
        {
            public readonly System.Collections.Generic.Dictionary<string, string> Data =
                new System.Collections.Generic.Dictionary<string, string>();

            public bool IsAvailable => true;

            public bool HasLobby { get; private set; }

            public bool EnsureLobby(string ip, int port)
            {
                HasLobby = true;
                return true;
            }

            public bool SetData(string key, string value)
            {
                Data[key] = value;
                return true;
            }

            public void CloseLobby() => HasLobby = false;
        }

        [Test]
        public void Un_host_con_solo_la_via_steam_si_publica_su_partida()
        {
            // Antes de ADR-135 esto no se publicaba: sin endpoint y sin relay, el publicador se
            // negaba. Es justo el host sin UPnP ni reenvío, o sea el caso normal.
            var host = new FakeHost();
            var publisher = new SteamLobbyPublisher(host);
            var publication = new LobbyPublication("Partida", "61", 8, "Level 0", "Unknown",
                LobbyPrivacy.Public, false, LobbyEndpoint.None, Lobby.DefaultTtlSeconds,
                hasRelay: false, hasSteamHost: true);

            Assert.IsTrue(publisher.Publish(publication, 1, LobbyStatus.Waiting, 1000d));
            Assert.IsTrue(publisher.IsPublishing);
        }

        [Test]
        public void Un_host_sin_ninguna_via_sigue_sin_publicar()
        {
            var host = new FakeHost();
            var publisher = new SteamLobbyPublisher(host);
            var publication = new LobbyPublication("Partida", "61", 8, "Level 0", "Unknown",
                LobbyPrivacy.Public, false, LobbyEndpoint.None, Lobby.DefaultTtlSeconds,
                hasRelay: false, hasSteamHost: false);

            Assert.IsFalse(publisher.Publish(publication, 1, LobbyStatus.Waiting, 1000d));
            Assert.IsFalse(publisher.IsPublishing);
        }

        [Test]
        public void El_conductor_anuncia_una_partida_que_solo_tiene_via_steam()
        {
            var state = new HostAnnouncementState(true, true, LobbyEndpoint.None, "Joel", "61",
                1, 8, "Level 0", LobbyStatus.Waiting, hasRelay: false, hasSteamHost: true);

            Assert.IsTrue(state.ShouldAnnounce);
        }

        [Test]
        public void El_conductor_no_anuncia_una_partida_sin_ninguna_via()
        {
            var state = new HostAnnouncementState(true, true, LobbyEndpoint.None, "Joel", "61",
                1, 8, "Level 0", LobbyStatus.Waiting, hasRelay: false, hasSteamHost: false);

            Assert.IsFalse(state.ShouldAnnounce);
        }
    }
}
