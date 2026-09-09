using System.Collections.Generic;
using BackroomsSurvival.Lobbies;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-136 D8 — lo que una invitación lee del lobby tiene que ser lo MISMO que lee el navegador.
    /// El fallo que cierra: la invitación entraba sin relay ni vía Steam porque sólo miraba
    /// `connect_ip`/`connect_port`.
    /// </summary>
    [TestFixture]
    public class LobbyJoinTargetTests
    {
        private static readonly string Secret = new string('a', LobbySteamHost.SecretLength);
        private static readonly string Token = new string('b', LobbyRelay.TokenLength);

        private static LobbyJoinTarget Read(Dictionary<string, string> data) =>
            LobbyJoinTarget.FromMetadata(key => data.TryGetValue(key, out string v) ? v : null);

        [Test]
        public void Una_invitacion_lleva_las_tres_vias_como_el_navegador()
        {
            var target = Read(new Dictionary<string, string>
            {
                [SteamLobbyKeys.ConnectIp] = "88.16.240.7",
                [SteamLobbyKeys.ConnectPort] = "7778",
                [SteamLobbyKeys.LanIp] = "192.168.1.40",
                [SteamLobbyKeys.RelayAddr] = "relay.example:4242",
                [SteamLobbyKeys.RelaySession] = "0x1234",
                [SteamLobbyKeys.RelayToken] = Token,
                [SteamLobbyKeys.SteamHost] = "76561198000000001",
                [SteamLobbyKeys.SteamAuth] = Secret,
            });

            Assert.IsTrue(target.HasDirect);
            Assert.AreEqual("88.16.240.7", target.Ip);
            Assert.AreEqual(7778, target.Port);
            Assert.AreEqual("192.168.1.40", target.FallbackIp);
            Assert.IsTrue(target.Relay.IsValid, "el relay tiene que sobrevivir a la lectura");
            Assert.IsTrue(target.SteamHost.IsValid, "y la vía Steam también");
            Assert.AreEqual(76561198000000001UL, target.SteamHost.SteamId);
        }

        /// ADR-117 D7: el host sin endpoint defendible no publica `connect_ip`, y AUN ASÍ se entra.
        [Test]
        public void Sin_endpoint_directo_pero_con_steam_se_puede_entrar()
        {
            var target = Read(new Dictionary<string, string>
            {
                [SteamLobbyKeys.SteamHost] = "76561198000000001",
                [SteamLobbyKeys.SteamAuth] = Secret,
            });

            Assert.IsFalse(target.HasDirect);
            Assert.IsNull(target.Ip);
            Assert.IsTrue(target.HasSomeWayIn);
        }

        [Test]
        public void Sin_ninguna_via_no_hay_a_donde_ir()
        {
            var target = Read(new Dictionary<string, string>());
            Assert.IsFalse(target.HasSomeWayIn);
        }

        /// Una IP sin puerto (o con un puerto imposible) no es un endpoint: se cae al resto de vías.
        [Test]
        public void Una_ip_sin_puerto_valido_no_cuenta_como_directo()
        {
            var sinPuerto = Read(new Dictionary<string, string> { [SteamLobbyKeys.ConnectIp] = "88.16.240.7" });
            Assert.IsFalse(sinPuerto.HasDirect);

            var puertoMalo = Read(new Dictionary<string, string>
            {
                [SteamLobbyKeys.ConnectIp] = "88.16.240.7",
                [SteamLobbyKeys.ConnectPort] = "70000",
            });
            Assert.IsFalse(puertoMalo.HasDirect);
        }

        /// Mismas reglas de bloque que el navegador: dos de tres claves de relay no son relay, y un
        /// `SteamId` ilegible es «sin vía Steam».
        [Test]
        public void Media_via_no_es_una_via()
        {
            var target = Read(new Dictionary<string, string>
            {
                [SteamLobbyKeys.RelayAddr] = "relay.example:4242",
                [SteamLobbyKeys.RelaySession] = "0x1234",
                [SteamLobbyKeys.SteamHost] = "no-es-un-numero",
                [SteamLobbyKeys.SteamAuth] = Secret,
            });

            Assert.IsFalse(target.Relay.IsValid);
            Assert.IsFalse(target.SteamHost.IsValid);
            Assert.IsFalse(target.HasSomeWayIn);
        }
    }
}
