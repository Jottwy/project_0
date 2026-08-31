using BackroomsSurvival.Connectivity;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// La escalera de conectividad: qué se puede afirmar en cada peldaño y qué no.
    ///
    /// El fallo que estos tests existen para impedir es de redacción, no de código: decir
    /// "internet funciona" porque se conoce la IP pública. Entre saber la IP y que entre alguien
    /// hay tres cosas que pueden fallar por separado (que no haya UPnP, que el mapeo se rechace,
    /// que se acepte y no se aplique) más una que no tiene arreglo (CGNAT).
    /// </summary>
    public sealed class HostConnectivityReportTests
    {
        private static HostConnectivityReport Fresh(int port = 7778)
        {
            var report = new HostConnectivityReport();
            report.SetLan("192.168.1.40", port);
            return report;
        }

        // ─── Peldaños ───

        [Test]
        public void AFreshReportClaimsNothing()
        {
            var report = Fresh();
            Assert.AreEqual(ConnectivityRung.None, report.Rungs);
            Assert.IsNull(report.PublicIp);
            Assert.AreEqual(PublicIpSource.None, report.PublicIpSource);
            StringAssert.Contains("LAN", report.DescribeReachability());
        }

        /// Una IP que no es públicamente enrutable **no enciende el peldaño**, aunque venga del
        /// router. Que el IGD conteste `100.64.0.1` no es conocer la IP pública: es descubrir que
        /// no hay ninguna.
        [TestCase("100.64.0.1")]
        [TestCase("192.168.1.1")]
        [TestCase("0.0.0.0")]
        [TestCase("169.254.5.254")]
        public void ANonRoutableAnswerDoesNotLightThePublicIpRung(string answer)
        {
            var report = Fresh();
            report.SetPublicIp(answer, PublicIpSource.InternetGatewayDevice);

            Assert.IsFalse(report.Has(ConnectivityRung.PublicIpKnown));
            // Se guarda igualmente: es la evidencia con la que se juzga el CGNAT.
            Assert.AreEqual(answer, report.PublicIp);
        }

        [Test]
        public void ARoutableAnswerLightsThePublicIpRung()
        {
            var report = Fresh();
            report.SetPublicIp("88.16.240.7", PublicIpSource.InternetGatewayDevice);

            Assert.IsTrue(report.Has(ConnectivityRung.PublicIpKnown));
            Assert.AreEqual(PublicIpSource.InternetGatewayDevice, report.PublicIpSource);
        }

        /// Pedir y confirmar son peldaños DISTINTOS porque hay routers que aceptan el
        /// `AddPortMapping` y no lo aplican. La confirmación sólo la da releer el mapeo.
        [Test]
        public void RequestedIsNotConfirmed()
        {
            var report = Fresh();
            report.MarkMappingRequested();

            Assert.IsTrue(report.Has(ConnectivityRung.PortMappingRequested));
            Assert.IsFalse(report.Has(ConnectivityRung.PortMappingConfirmed));
        }

        /// Confirmar sin haber pedido sería un bug del orquestador. Se corta aquí en vez de dejar
        /// que llegue a la UI convertido en una promesa.
        [Test]
        public void ConfirmingWithoutRequestingIsIgnored()
        {
            var report = Fresh();
            report.MarkMappingConfirmed();

            Assert.IsFalse(report.Has(ConnectivityRung.PortMappingConfirmed));
        }

        [Test]
        public void ConfirmingAfterRequestingWorks()
        {
            var report = Fresh();
            report.MarkMappingRequested();
            report.MarkMappingConfirmed();

            Assert.IsTrue(report.Has(ConnectivityRung.PortMappingConfirmed));
        }

        // ─── Lo que se puede AFIRMAR ───

        /// El caso que da nombre al encargo. Saber la IP pública **no** es que internet funcione, y
        /// la frase que se le enseña al usuario no puede decirlo.
        [Test]
        public void KnowingThePublicIpNeverClaimsThatInternetWorks()
        {
            var report = Fresh();
            report.SetPublicIp("88.16.240.7", PublicIpSource.ExternalEcho);

            string phrase = report.DescribeReachability();
            StringAssert.Contains("no basta", phrase);
            StringAssert.DoesNotContain("funciona", phrase);
        }

        /// Ni siquiera con el mapeo confirmado: eso demuestra que el ROUTER lo aceptó, no que un
        /// datagrama de fuera llegue.
        [Test]
        public void EvenAConfirmedMappingDoesNotClaimThatInternetWorks()
        {
            var report = Fresh();
            report.SetPublicIp("88.16.240.7", PublicIpSource.InternetGatewayDevice);
            report.MarkMappingRequested();
            report.MarkMappingConfirmed();

            string phrase = report.DescribeReachability();
            StringAssert.Contains("No está comprobado", phrase);
        }

        /// El CGNAT gana a todo lo demás: aunque el router haya confirmado un mapeo, si hay NAT de
        /// operador ese mapeo no sirve para nadie de fuera.
        [Test]
        public void CgnatOverridesEveryOtherClaim()
        {
            var report = Fresh();
            report.SetPublicIp("88.16.240.7", PublicIpSource.ExternalEcho);
            report.MarkMappingRequested();
            report.MarkMappingConfirmed();
            report.AddCode(ConnectivityCode.CgnatSuspected);

            StringAssert.Contains("CGNAT", report.DescribeReachability());
        }

        // ─── Códigos ───

        /// Los nombres viajan LITERALES: se buscan con grep en el `Player.log` de un tester
        /// copiando y pegando lo que dice la documentación. `code.ToString()` daría
        /// `UpnpUnavailable` y ese grep no encontraría nada.
        [TestCase(ConnectivityCode.UpnpUnavailable, "UPNP_UNAVAILABLE")]
        [TestCase(ConnectivityCode.UpnpDisabled, "UPNP_DISABLED")]
        [TestCase(ConnectivityCode.UpnpMappingFailed, "UPNP_MAPPING_FAILED")]
        [TestCase(ConnectivityCode.CgnatSuspected, "CGNAT_SUSPECTED")]
        [TestCase(ConnectivityCode.PublicEndpointUnknown, "PUBLIC_ENDPOINT_UNKNOWN")]
        public void CodeNamesAreTheLiteralsFromTheSpec(ConnectivityCode code, string expected)
        {
            Assert.AreEqual(expected, HostConnectivityReport.CodeName(code));
        }

        [Test]
        public void AddingTheSameCodeTwiceDoesNotStackIt()
        {
            var report = Fresh();
            report.AddCode(ConnectivityCode.UpnpUnavailable);
            report.AddCode(ConnectivityCode.UpnpUnavailable);

            Assert.AreEqual(1, report.Codes.Count);
        }

        [Test]
        public void TheLogLineCarriesEveryFactAndTheLiteralCodes()
        {
            var report = Fresh(7779);
            report.SetPublicIp("88.16.240.7", PublicIpSource.ExternalEcho);
            report.MarkEndpointPublished("88.16.240.7");
            report.AddCode(ConnectivityCode.UpnpUnavailable);

            string line = report.ToLogLine();
            StringAssert.StartsWith("NATPROBE ", line);
            StringAssert.Contains("lan=192.168.1.40", line);
            StringAssert.Contains("port=7779", line);
            StringAssert.Contains("public=88.16.240.7", line);
            StringAssert.Contains("published=88.16.240.7", line);
            StringAssert.Contains("UPNP_UNAVAILABLE", line);
        }

        [Test]
        public void TheLogLineSaysUnknownRatherThanBlankWhenNothingIsKnown()
        {
            string line = new HostConnectivityReport().ToLogLine();
            StringAssert.Contains("public=<unknown>", line);
            StringAssert.Contains("codes=<none>", line);
            StringAssert.Contains("rungs=None", line);
        }

        // ─── Limpieza ───

        /// El teardown borra el mapeo del router; el informe tiene que dejar de afirmar que existe.
        /// Un informe reutilizado que siguiera diciendo `PortMappingConfirmed` haría que la
        /// siguiente partida publicara su endpoint público sin tener mapeo ninguno.
        [Test]
        public void ClearingTheMappingDropsBothMappingRungsAndKeepsTheRest()
        {
            var report = Fresh();
            report.SetPublicIp("88.16.240.7", PublicIpSource.InternetGatewayDevice);
            report.MarkMappingRequested();
            report.MarkMappingConfirmed();
            report.MarkEndpointPublished("88.16.240.7");

            report.ClearMapping();

            Assert.IsFalse(report.Has(ConnectivityRung.PortMappingRequested));
            Assert.IsFalse(report.Has(ConnectivityRung.PortMappingConfirmed));
            Assert.IsTrue(report.Has(ConnectivityRung.PublicIpKnown));
            Assert.IsTrue(report.Has(ConnectivityRung.EndpointPublished));
        }

        /// Un peer remoto **no** demuestra que el camino público funcione: uno de la misma LAN
        /// enciende el mismo peldaño. Quién entró por dónde lo sabe el backend, no Unity.
        [Test]
        public void ARemotePeerDoesNotUpgradeTheClaim()
        {
            var report = Fresh();
            report.SetPublicIp("88.16.240.7", PublicIpSource.ExternalEcho);
            report.MarkRemotePeerObserved();

            Assert.IsTrue(report.Has(ConnectivityRung.RemotePeerObserved));
            StringAssert.Contains("no basta", report.DescribeReachability());
        }
    }
}
