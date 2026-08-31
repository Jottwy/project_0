using BackroomsSurvival.Lobbies;
using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// `bs_lan_ip`: la segunda dirección del host, de punta a punta.
    ///
    /// **Por qué existe.** Cuando el host consigue mapeo UPnP confirmado, `connect_ip` pasa a ser
    /// su IP pública. Un joiner de la MISMA red que llame a esa IP sólo llega si el router hace
    /// hairpin (NAT loopback), y muchos routers domésticos no lo hacen. Sin esta clave, dos
    /// personas en el mismo salón dejarían de poder jugar juntas justo cuando el host consigue
    /// UPnP — o sea, la mejora rompería lo que ya funcionaba.
    ///
    /// **Por qué es sólo para el reintento.** Desde el joiner no se puede saber si está en la
    /// misma red que el host: dos casas distintas pueden ser las dos `192.168.1.0/24`.
    /// Adivinarlo mandaría a gente de fuera a una dirección privada. Primero lo anunciado, la
    /// alternativa después: así no puede equivocarse.
    /// </summary>
    public sealed class LanFallbackMetadataTests
    {
        private static SteamLobbyRecord Record(string connectIp, string lanIp) => new SteamLobbyRecord
        {
            Id = 1234UL,
            ConnectIp = connectIp,
            ConnectPort = "7778",
            Name = "Partida",
            WireVersion = "41",
            Players = "1",
            MaxPlayers = "8",
            Map = "Level 0",
            State = "open",
            LanIp = lanIp,
        };

        // ─── La clave ───

        /// Las claves están declaradas DOS veces —en el lado sin Steam y en el que habla con
        /// Steam— y si divergen el host publica con una y el navegador lee con otra: la lista sale
        /// vacía **sin un solo error**. De ahí la comprobación de paridad.
        [Test]
        public void TheLanKeyIsDeclaredIdenticallyOnBothSides()
        {
            Assert.AreEqual("bs_lan_ip", SteamLobbyKeys.LanIp);
            Assert.AreEqual(SteamLobbyKeys.LanIp, SteamLobbyManager.LanIpKey);
            Assert.IsTrue(SteamLobbyKeyParity.KeysMatch(out string mismatch), mismatch);
        }

        // ─── Del lobby al destino ───

        [Test]
        public void APublishedLanAddressReachesTheEndpointAsTheAlternate()
        {
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record("88.16.240.7", "192.168.1.40"), 1000d, out Lobby lobby));

            Assert.AreEqual("88.16.240.7", lobby.Endpoint.Host);
            Assert.AreEqual("192.168.1.40", lobby.Endpoint.Alternate);
            Assert.IsTrue(lobby.Endpoint.HasAlternate);
        }

        /// Un host sin UPnP anuncia su LAN en las dos claves. No hay nada que reintentar, y el
        /// [Retry] tiene que seguir siendo "repite lo mismo".
        [Test]
        public void TheSameAddressTwiceIsNotAnAlternate()
        {
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record("192.168.1.40", "192.168.1.40"), 1000d, out Lobby lobby));
            Assert.IsFalse(lobby.Endpoint.HasAlternate);
        }

        /// Un host viejo, de antes de esta clave, no la publica. `GetData` de Steam devuelve
        /// cadena vacía para una clave ausente —nunca null—, así que ése es el caso real.
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void ALobbyWithoutTheKeyHasNoAlternate(string lanIp)
        {
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record("88.16.240.7", lanIp), 1000d, out Lobby lobby));

            Assert.IsNull(lobby.Endpoint.Alternate);
            Assert.IsFalse(lobby.Endpoint.HasAlternate);
        }

        /// La alternativa NO se valida contra la política de direcciones públicas, y es
        /// deliberado: es una dirección de LAN, así que ni es pública ni tiene por qué parecerlo.
        [Test]
        public void ThePrivateAlternateIsKeptEvenThoughItIsNotPubliclyRoutable()
        {
            Assert.IsTrue(SteamLobbyMapper.TryMap(Record("88.16.240.7", "10.0.0.5"), 1000d, out Lobby lobby));
            Assert.AreEqual("10.0.0.5", lobby.Endpoint.Alternate);
        }

        // ─── Identidad ───

        /// **La alternativa no forma parte de la identidad del endpoint.** Dos fichas del mismo
        /// servidor —una leída antes de que el host publicara su LAN y otra después— tienen que
        /// seguir siendo el mismo destino; si la alternativa contara, la selección del navegador
        /// se perdería sola en el refresco siguiente.
        [Test]
        public void TheAlternateDoesNotChangeTheIdentityOfAnEndpoint()
        {
            var withoutAlternate = new LobbyEndpoint("88.16.240.7", 7778);
            var withAlternate = new LobbyEndpoint("88.16.240.7", 7778, "192.168.1.40");

            Assert.AreEqual(withoutAlternate, withAlternate);
            Assert.AreEqual(withoutAlternate.GetHashCode(), withAlternate.GetHashCode());
        }

        /// El endpoint sigue siendo válido o inválido por lo de siempre: la alternativa no rescata
        /// a un destino principal roto.
        [Test]
        public void AnAlternateDoesNotMakeABrokenEndpointValid()
        {
            Assert.IsFalse(new LobbyEndpoint(null, 7778, "192.168.1.40").IsValid);
            Assert.IsFalse(new LobbyEndpoint("88.16.240.7", 0, "192.168.1.40").IsValid);
        }

        /// El espacio en blanco alrededor se recorta, igual que en el host: estas cadenas salen de
        /// metadatos de Steam escritos por otra máquina.
        [Test]
        public void TheAlternateIsTrimmed()
        {
            Assert.AreEqual("192.168.1.40", new LobbyEndpoint("88.16.240.7", 7778, "  192.168.1.40 ").Alternate);
        }
    }
}
