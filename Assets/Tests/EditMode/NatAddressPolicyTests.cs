using BackroomsSurvival.Connectivity;
using NUnit.Framework;
using Kind = BackroomsSurvival.Connectivity.NatAddressPolicy.PublicAddressKind;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Qué direcciones son alcanzables desde internet y cuáles no.
    ///
    /// Ojo con no confundir esto con <see cref="LobbyEndpointPolicyTests"/>: allí se prueba qué se
    /// puede ANUNCIAR (y una `192.168.x.x` sí se puede, para una partida en LAN), aquí qué es
    /// PÚBLICO (y esa misma dirección no lo es). Las dos respuestas son correctas porque las
    /// preguntas son distintas.
    ///
    /// El caso que justifica la clase entera es `100.64.0.0/10`: parece pública, no es privada de
    /// las que todo el mundo reconoce, y es exactamente donde el operador mete a sus abonados
    /// cuando no les da IP propia.
    /// </summary>
    public sealed class NatAddressPolicyTests
    {
        // ─── Lo que el encargo pide rechazar explícitamente ───

        /// Loopback. En la máquina del joiner significa la máquina del joiner.
        [TestCase("127.0.0.1")]
        [TestCase("127.255.255.254")]
        [TestCase("::1")]
        public void LoopbackIsRejected(string address)
        {
            Assert.AreEqual(Kind.Loopback, NatAddressPolicy.Classify(address));
            Assert.IsFalse(NatAddressPolicy.IsPubliclyRoutable(address));
        }

        /// `0.0.0.0` es lo que el backend usa para BIND (`NetworkManager::bind`). Publicarlo como
        /// destino no significa nada.
        [TestCase("0.0.0.0")]
        [TestCase("0.1.2.3")]
        [TestCase("::")]
        public void TheUnspecifiedAddressIsRejected(string address)
        {
            Assert.AreEqual(Kind.Unspecified, NatAddressPolicy.Classify(address));
            Assert.IsFalse(NatAddressPolicy.IsPubliclyRoutable(address));
        }

        /// APIPA. En ESTA máquina hay seis (medido: `Get-NetIPAddress` da 169.254.99.146,
        /// .252.251, .216.252, .89.134, .103.76 y .148.130), así que cualquier heurística que
        /// coja "la primera que no sea loopback" acierta una de cada siete.
        [TestCase("169.254.0.1")]
        [TestCase("169.254.99.146")]
        [TestCase("169.254.255.255")]
        [TestCase("fe80::1")]
        public void ApipaIsRejected(string address)
        {
            Assert.AreEqual(Kind.LinkLocal, NatAddressPolicy.Classify(address));
            Assert.IsFalse(NatAddressPolicy.IsPubliclyRoutable(address));
        }

        /// LAN privada: bien clasificada, y **no** pública. `192.168.1.40` es la dirección real de
        /// la interfaz Ethernet de la máquina de desarrollo.
        [TestCase("10.0.0.1")]
        [TestCase("10.5.0.2")]
        [TestCase("172.16.0.1")]
        [TestCase("172.31.255.254")]
        [TestCase("192.168.1.40")]
        [TestCase("fd00::1")]
        public void PrivateLanIsClassifiedAsSuch(string address)
        {
            Assert.AreEqual(Kind.PrivateRfc1918, NatAddressPolicy.Classify(address));
            Assert.IsTrue(NatAddressPolicy.IsPrivateLan(address));
            Assert.IsFalse(NatAddressPolicy.IsPubliclyRoutable(address));
        }

        /// El borde de `172.16/12`. `172.15` y `172.32` **son públicas**, y una comparación
        /// perezosa (`b[0] == 172`) se las tragaría enteras.
        [TestCase("172.15.255.255")]
        [TestCase("172.32.0.1")]
        public void TheEdgesOfTheClassBPrivateRangeAreNotPrivate(string address)
        {
            Assert.AreEqual(Kind.Public, NatAddressPolicy.Classify(address));
        }

        // ─── CGNAT ───

        /// `100.64.0.0/10`. El rango del NAT de operador.
        [TestCase("100.64.0.1")]
        [TestCase("100.100.50.4")]
        [TestCase("100.127.255.254")]
        public void CarrierGradeNatRangeIsDetected(string address)
        {
            Assert.AreEqual(Kind.CarrierGradeNat, NatAddressPolicy.Classify(address));
            Assert.IsFalse(NatAddressPolicy.IsPubliclyRoutable(address));
        }

        /// El borde de `/10`, que es el error fácil: `100.64` a `100.127`, **no** hasta `100.255`.
        /// `100.128.x.x` es espacio público de verdad y marcarlo como CGNAT dejaría a un host
        /// perfectamente alcanzable con el aviso de "no puedes aceptar conexiones".
        [TestCase("100.63.255.255")]
        [TestCase("100.128.0.1")]
        [TestCase("100.255.255.255")]
        public void TheEdgesOfTheCarrierGradeRangeAreStillPublic(string address)
        {
            Assert.AreEqual(Kind.Public, NatAddressPolicy.Classify(address));
            Assert.IsTrue(NatAddressPolicy.IsPubliclyRoutable(address));
        }

        // ─── Público, y lo que no es una dirección ───

        [TestCase("8.8.8.8")]
        [TestCase("1.1.1.1")]
        [TestCase("88.16.240.7")]
        [TestCase("2001:db8::1")]
        public void PublicAddressesAreRoutable(string address)
        {
            Assert.AreEqual(Kind.Public, NatAddressPolicy.Classify(address));
            Assert.IsTrue(NatAddressPolicy.IsPubliclyRoutable(address));
        }

        [TestCase("224.0.0.1")]
        [TestCase("239.255.255.250")]
        [TestCase("255.255.255.255")]
        [TestCase("240.0.0.1")]
        public void MulticastAndReservedAreNotHosts(string address)
        {
            Assert.AreEqual(Kind.Reserved, NatAddressPolicy.Classify(address));
            Assert.IsFalse(NatAddressPolicy.IsPubliclyRoutable(address));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void MissingIsMissing(string address)
        {
            Assert.AreEqual(Kind.Missing, NatAddressPolicy.Classify(address));
            Assert.IsFalse(NatAddressPolicy.IsPubliclyRoutable(address));
        }

        /// Un nombre DNS **no se adivina**. Podría resolver a una IP pública o a la LAN, y
        /// resolverlo sería E/S dentro de una clase que a propósito no la hace. El llamante
        /// decide; lo que no puede es creerse una respuesta inventada.
        [TestCase("micasa.duckdns.org")]
        [TestCase("localhost")]
        [TestCase("192.168.1")]
        [TestCase("no es una ip")]
        public void ANameIsNotClassifiedAsAnAddress(string address)
        {
            Assert.AreEqual(Kind.NotAnIpLiteral, NatAddressPolicy.Classify(address));
            Assert.IsFalse(NatAddressPolicy.IsPubliclyRoutable(address));
        }

        /// Una IPv4 vestida de IPv6 tiene que clasificarse por lo que ES. Si no,
        /// `::ffff:192.168.1.40` saldría "pública" y el host anunciaría su LAN a internet.
        [Test]
        public void AnIPv4MappedAddressIsClassifiedByItsIPv4()
        {
            Assert.AreEqual(Kind.PrivateRfc1918, NatAddressPolicy.Classify("::ffff:192.168.1.40"));
            Assert.AreEqual(Kind.Loopback, NatAddressPolicy.Classify("::ffff:127.0.0.1"));
        }

        /// El espacio en blanco alrededor no cambia la clasificación: estas cadenas vienen de
        /// XML SOAP y de cuerpos HTTP, donde un `\n` de más es lo normal.
        [Test]
        public void SurroundingWhitespaceDoesNotChangeTheVerdict()
        {
            Assert.AreEqual(Kind.Public, NatAddressPolicy.Classify("  8.8.8.8\n"));
            Assert.AreEqual(Kind.CarrierGradeNat, NatAddressPolicy.Classify("\t100.64.0.1 "));
        }
    }
}
