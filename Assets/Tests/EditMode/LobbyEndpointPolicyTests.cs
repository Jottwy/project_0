using System.Collections.Generic;
using BackroomsSurvival.Lobbies;
using NUnit.Framework;
using Kind = BackroomsSurvival.Lobbies.LobbyEndpointPolicy.HostAddressKind;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Qué se puede anunciar como endpoint de una partida.
    ///
    /// Nace de un fallo MEDIDO el 2026-08-31: el host publicó `connect_ip=127.0.0.1` (el valor por
    /// defecto del campo del panel) y el joiner disparó contra su propio loopback. El síntoma
    /// llegaba 15 s después como diez `UDP recv error … (os error 10054)`, que es Windows diciendo
    /// que un datagrama nuestro rebotó porque no había nadie en el destino. Ni el socket ni el
    /// protocolo tenían nada malo: el destino estaba mal.
    ///
    /// Lo que se prueba aquí es la CLASIFICACIÓN y la PRECEDENCIA. El descubrimiento de
    /// direcciones (`LocalAddressProbe`) toca el sistema y no se prueba: depende de la máquina.
    /// </summary>
    public sealed class LobbyEndpointPolicyTests
    {
        private static List<string> Candidates(params string[] items) => new List<string>(items);

        // ─── Clasificación ───

        /// Loopback es el caso que rompió. `127.0.0.1` no es "una IP que sólo vale en local":
        /// **en la máquina del joiner es la máquina del joiner**, así que el Join no falla, va a
        /// otro sitio.
        [TestCase("127.0.0.1")]
        [TestCase("127.1.2.3")]
        [TestCase("localhost")]
        [TestCase("LOCALHOST")]
        [TestCase("::1")]
        public void LoopbackIsNeverPublishable(string host)
        {
            Assert.AreEqual(Kind.Loopback, LobbyEndpointPolicy.Classify(host));
            Assert.IsFalse(LobbyEndpointPolicy.IsPublishable(host));
        }

        /// APIPA. En la máquina donde se encontró el fallo había SEIS, así que "la primera IPv4 que
        /// no sea loopback" habría publicado una de éstas — y le falla al joiner exactamente igual
        /// de silencioso que loopback.
        [TestCase("169.254.99.146")]
        [TestCase("169.254.5.254")]
        [TestCase("fe80::1")]
        public void LinkLocalIsNeverPublishable(string host)
        {
            Assert.AreEqual(Kind.LinkLocal, LobbyEndpointPolicy.Classify(host));
            Assert.IsFalse(LobbyEndpointPolicy.IsPublishable(host));
        }

        /// `0.0.0.0` es lo que el backend usa para BIND. Como destino no significa nada.
        [TestCase("0.0.0.0")]
        [TestCase("::")]
        public void TheUnspecifiedAddressIsABindNotADestination(string host)
        {
            Assert.AreEqual(Kind.Unspecified, LobbyEndpointPolicy.Classify(host));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void AnEmptyFieldIsMissing(string host)
        {
            Assert.AreEqual(Kind.Missing, LobbyEndpointPolicy.Classify(host));
        }

        [TestCase("192.168.1.40")]
        [TestCase("10.5.0.2")]
        [TestCase("81.34.12.9")]
        [TestCase(" 192.168.1.40 ")]
        public void RoutableAddressesArePublishable(string host)
        {
            Assert.AreEqual(Kind.Usable, LobbyEndpointPolicy.Classify(host));
        }

        /// Un nombre que no parsea como IP se acepta: puede ser un DNS dinámico escrito aposta, y
        /// rechazarlo sería decidir por el humano. Sólo `localhost` está exceptuado.
        [Test]
        public void AHostnameIsAcceptedBecauseTheHumanMayKnowBetter()
        {
            Assert.AreEqual(Kind.Usable, LobbyEndpointPolicy.Classify("micasa.dyndns.org"));
        }

        // ─── Precedencia ───

        /// Manda el humano. Puede haber escrito su IP pública con reenvío de puertos, que es algo
        /// que la máquina no puede deducir sola.
        [Test]
        public void WhatTheHumanTypedWinsWhenItIsUsable()
        {
            string host = LobbyEndpointPolicy.ResolvePublishableHost(
                "81.34.12.9", Candidates("192.168.1.40"), out string reason);

            Assert.AreEqual("81.34.12.9", host);
            StringAssert.Contains("panel", reason);
        }

        /// El caso exacto del fallo: campo por defecto en `127.0.0.1`, máquina con IP LAN.
        [Test]
        public void LoopbackInTheFieldIsReplacedByTheLocalAddress()
        {
            string host = LobbyEndpointPolicy.ResolvePublishableHost(
                "127.0.0.1", Candidates("192.168.1.40"), out string reason);

            Assert.AreEqual("192.168.1.40", host);
            StringAssert.Contains("loopback", reason);
        }

        /// El orden de los candidatos lo fija el llamante (la ruta por defecto primero) y se
        /// respeta: nada de reordenar aquí.
        [Test]
        public void TheFirstUsableCandidateWinsInOrder()
        {
            string host = LobbyEndpointPolicy.ResolvePublishableHost(
                null, Candidates("169.254.99.146", "192.168.1.40", "10.5.0.2"), out _);

            Assert.AreEqual("192.168.1.40", host,
                "las APIPA se saltan, pero el orden de las utilizables lo decide quien las trae");
        }

        /// **La regla que evita el fallo caro.** Sin nada defendible NO se anuncia: publicar un
        /// endpoint malo le cuesta 15 s de espera a quien lo elige; no publicar no le cuesta nada.
        [Test]
        public void WithNothingDefensibleTheGameIsNotAnnouncedAtAll()
        {
            string host = LobbyEndpointPolicy.ResolvePublishableHost(
                "127.0.0.1", Candidates("169.254.99.146", "127.0.0.1", "0.0.0.0"), out string reason);

            Assert.IsNull(host);
            StringAssert.Contains("NO se anuncia", reason);
        }

        [Test]
        public void NoCandidateListIsNotACrash()
        {
            Assert.IsNull(LobbyEndpointPolicy.ResolvePublishableHost("127.0.0.1", null, out _));
            Assert.IsNull(LobbyEndpointPolicy.ResolvePublishableHost(null, null, out _));
        }

        /// El endpoint que sale de la política tiene que ser válido para el modelo de lobby; si no,
        /// el conductor no publicaría y el arreglo no serviría de nada.
        [Test]
        public void TheResolvedHostMakesAValidLobbyEndpoint()
        {
            string host = LobbyEndpointPolicy.ResolvePublishableHost(
                "127.0.0.1", Candidates("192.168.1.40"), out _);

            var endpoint = new LobbyEndpoint(host, 7778);

            Assert.IsTrue(endpoint.IsValid);
            Assert.AreEqual("192.168.1.40", endpoint.Host);
            Assert.AreEqual(7778, endpoint.Port);
        }

        /// Y el contrapunto: un endpoint loopback NO puede colarse como anuncio. Es la regla
        /// entera, en una línea.
        [Test]
        public void ALoopbackEndpointCanNeverBeAnnounced()
        {
            Assert.IsFalse(LobbyEndpointPolicy.IsPublishable(new LobbyEndpoint("127.0.0.1", 7778).Host));
        }
    }
}
