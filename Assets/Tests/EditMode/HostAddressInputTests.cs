using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Lo que el jugador escribe —o PEGA— en el campo de IP, antes de que se convierta en
    /// `CONNECT_TO`.
    ///
    /// Nace de un fallo medido en la sesión física del 2026-08-31: el campo llevaba
    /// `"31.4.149.48\n"` de pegar la dirección, `NetworkInitializer` interpolaba
    /// `$"{ip}:{port}"` sin tocarla, y al backend le llegó `CONNECT_TO=31.4.149.48\n:7778`. No
    /// parseaba, así que no hubo handshake — y como el presupuesto de `CONNECT_TIMEOUT` solo
    /// arranca cuando hay intento, tampoco hubo veredicto: 25 segundos de «Joining…» y un
    /// «no session confirmation» que no señalaba a nada.
    ///
    /// La ruta del navegador de Steam nunca lo sufrió porque `Lobby.Sanitize` ya hacía `Trim()`.
    /// Esto le da a la ruta manual la misma higiene, en el único sitio por el que pasan las tres.
    /// </summary>
    public sealed class HostAddressInputTests
    {
        // ─── Lo que rompió en campo ───

        [Test]
        public void ATrailingNewlineFromAPasteIsStripped()
        {
            Assert.AreEqual("31.4.149.48", HostAddressInput.Normalize("31.4.149.48\n"));
        }

        [Test]
        public void AWindowsPasteWithCarriageReturnIsStrippedToo()
        {
            Assert.AreEqual("31.4.149.48", HostAddressInput.Normalize("31.4.149.48\r\n"));
        }

        [Test]
        public void SurroundingSpacesAndTabsGo()
        {
            Assert.AreEqual("1.2.3.4", HostAddressInput.Normalize("  \t1.2.3.4 \t "));
        }

        // ─── Lo que NO debe cambiar ───

        [Test]
        public void ACleanAddressIsReturnedUntouched()
        {
            Assert.AreEqual("192.168.1.40", HostAddressInput.Normalize("192.168.1.40"));
        }

        [Test]
        public void ADnsNameSurvives()
        {
            // Un host dinámico es un destino legítimo, y `IPAddress.TryParse` lo rechazaría.
            Assert.AreEqual("mihost.duckdns.org", HostAddressInput.Normalize(" mihost.duckdns.org "));
        }

        [Test]
        public void AnIpv6LiteralSurvives()
        {
            Assert.AreEqual("::1", HostAddressInput.Normalize(" ::1 "));
        }

        // ─── Vacío: el default de siempre, no un fallo ───

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("\r\n")]
        public void EmptyOrBlankFallsBackToLoopbackLikeItAlwaysDid(string raw)
        {
            Assert.AreEqual("127.0.0.1", HostAddressInput.NormalizeOrDefault(raw));
        }

        // ─── Validación: decir que no ANTES de lanzar un backend condenado ───

        [Test]
        public void ACleanAddressIsAccepted()
        {
            Assert.IsTrue(HostAddressInput.IsUsable("31.4.149.48", out string reason), reason);
            Assert.IsNull(reason);
        }

        [Test]
        public void TheFieldValueFromTheFieldIncidentIsAcceptedOnceNormalized()
        {
            // El punto entero: tras normalizar, la dirección que tumbó la sesión SIRVE. El fallo
            // nunca fue del jugador ni de la red.
            string normalized = HostAddressInput.Normalize("31.4.149.48\n");
            Assert.IsTrue(HostAddressInput.IsUsable(normalized, out _));
        }

        [TestCase("31.4.149.48 con basura")]
        [TestCase("http://31.4.149.48")]
        [TestCase("31.4.149.48:7778")]
        [TestCase("no es una dirección")]
        public void SomethingThatIsNotAHostIsRejectedWithAReason(string raw)
        {
            Assert.IsFalse(HostAddressInput.IsUsable(raw, out string reason));
            Assert.IsNotNull(reason);
            Assert.IsTrue(reason.Length > 0, "un rechazo sin motivo no se puede enseñar");
        }

        [Test]
        public void TheRejectionReasonQuotesTheRawValueSoInvisibleWhitespaceShows()
        {
            // Un motivo que dice «31.4.149.48 no vale» delante de una IP que se ve perfecta es
            // peor que no decir nada. Entrecomillado, el espacio se ve.
            HostAddressInput.IsUsable("31.4.149.48 ", out string reason);
            StringAssert.Contains("\"31.4.149.48 \"", reason);
        }

        [Test]
        public void ARejectedPortSuffixSaysWhatToDoAboutIt()
        {
            // Pegar `ip:puerto` entero en el campo de la IP es el error de dedo más probable, y
            // hay un campo aparte para el puerto: se dice.
            HostAddressInput.IsUsable("31.4.149.48:7778", out string reason);
            StringAssert.Contains("puerto", reason);
        }
    }
}
