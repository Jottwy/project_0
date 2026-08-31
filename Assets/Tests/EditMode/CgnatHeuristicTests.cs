using BackroomsSurvival.Connectivity;
using NUnit.Framework;
using Verdict = BackroomsSurvival.Connectivity.CgnatHeuristic.Verdict;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// La heurística de CGNAT. Es el único fallo del encargo que **no tiene arreglo** con este
    /// transporte, así que acertar el veredicto vale una tarde de la vida de un usuario peleándose
    /// con la configuración de su router.
    ///
    /// El caso que más importa de todos es <see cref="AnObservedPublicIpAloneProvesNothing"/>: una
    /// máquina detrás de CGNAT ve una IP pública perfectamente normal cuando le pregunta a un eco
    /// HTTP. Ése es el dato que engaña, y por eso el juicio necesita la WAN del router.
    /// </summary>
    public sealed class CgnatHeuristicTests
    {
        // ─── Sospecha ───

        /// Indicio 1, el más fuerte: la WAN está en el rango que RFC 6598 reserva al operador.
        [TestCase("100.64.0.1")]
        [TestCase("100.96.12.7")]
        public void ACarrierGradeWanIsSuspected(string wan)
        {
            Assert.AreEqual(Verdict.Suspected, CgnatHeuristic.Evaluate(wan, null, out string reason));
            StringAssert.Contains("100.64/10", reason);
        }

        /// El indicio 1 manda aunque el eco externo devuelva una IP pública impecable — que es
        /// justo lo que va a pasar siempre que haya CGNAT.
        [Test]
        public void ACarrierGradeWanIsSuspectedEvenWithAPerfectlyNormalPublicEcho()
        {
            Assert.AreEqual(Verdict.Suspected, CgnatHeuristic.Evaluate("100.64.0.1", "88.16.240.7", out _));
        }

        /// Indicio 2: doble NAT. Puede ser del operador o del propio usuario (un router detrás del
        /// del ISP); las dos rompen el reenvío igual, así que se avisa igual.
        [TestCase("192.168.0.1")]
        [TestCase("10.0.0.1")]
        [TestCase("172.20.0.1")]
        public void APrivateWanMeansDoubleNat(string wan)
        {
            Assert.AreEqual(Verdict.Suspected, CgnatHeuristic.Evaluate(wan, "88.16.240.7", out string reason));
            StringAssert.Contains("doble NAT", reason);
        }

        /// Indicio 3: el router y el mundo no ven lo mismo. Alguien traduce por encima.
        [Test]
        public void AMismatchBetweenRouterAndWorldIsSuspected()
        {
            Assert.AreEqual(Verdict.Suspected,
                CgnatHeuristic.Evaluate("88.16.240.7", "203.0.113.9", out string reason));
            StringAssert.Contains("traduce", reason);
        }

        // ─── Sin sospecha ───

        [Test]
        public void APublicWanThatMatchesTheWorldIsUnlikely()
        {
            Assert.AreEqual(Verdict.Unlikely,
                CgnatHeuristic.Evaluate("88.16.240.7", "88.16.240.7", out string reason));
            StringAssert.Contains("coincide", reason);
        }

        /// Con WAN pública y sin eco con qué contrastar sigue siendo `Unlikely`: el indicio fuerte
        /// (el rango) ya se ha mirado y no está. Falta el cotejo, no el juicio.
        [Test]
        public void APublicWanWithNoEchoIsStillUnlikely()
        {
            Assert.AreEqual(Verdict.Unlikely, CgnatHeuristic.Evaluate("88.16.240.7", null, out _));
        }

        /// El espacio en blanco no puede convertir una coincidencia en una discrepancia: estas
        /// cadenas salen de XML SOAP y de cuerpos HTTP.
        [Test]
        public void WhitespaceDoesNotFakeAMismatch()
        {
            Assert.AreEqual(Verdict.Unlikely, CgnatHeuristic.Evaluate(" 88.16.240.7 ", "88.16.240.7\n", out _));
        }

        // ─── Desconocido ───

        /// **El caso que engaña.** Sin la WAN del router, un eco público no prueba nada: es
        /// exactamente lo que ve una máquina detrás de CGNAT. `Unknown` no es `Unlikely`, y el
        /// tipo tiene los dos valores por separado justo para esto.
        [TestCase("88.16.240.7")]
        [TestCase(null)]
        public void AnObservedPublicIpAloneProvesNothing(string echo)
        {
            Assert.AreEqual(Verdict.Unknown, CgnatHeuristic.Evaluate(null, echo, out string reason));
            StringAssert.Contains("no se pudo leer la WAN", reason);
        }

        /// Un IGD que contesta una basura (vacío, loopback, `0.0.0.0`) no sirve para juzgar. Se
        /// dice que no se sabe, no que no hay CGNAT.
        [TestCase("")]
        [TestCase("0.0.0.0")]
        [TestCase("127.0.0.1")]
        [TestCase("no-es-una-ip")]
        public void AGarbageWanAnswerYieldsUnknown(string wan)
        {
            Assert.AreEqual(Verdict.Unknown, CgnatHeuristic.Evaluate(wan, "88.16.240.7", out _));
        }

        /// El motivo nunca es null: se escribe directo en el log y en la UI.
        [TestCase("100.64.0.1", "88.16.240.7")]
        [TestCase("88.16.240.7", null)]
        [TestCase(null, null)]
        [TestCase("", "")]
        public void TheReasonIsAlwaysWritten(string wan, string echo)
        {
            CgnatHeuristic.Evaluate(wan, echo, out string reason);
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
        }
    }
}
