using System;
using System.Threading.Tasks;
using BackroomsSurvival.Connectivity;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// La secuencia completa: buscar el router, averiguar la IP pública, pedir el reenvío,
    /// **releerlo**, y decidir qué se puede anunciar.
    ///
    /// Cada test enchufa una pasarela de mentira (<see cref="FakeUpnpDevice"/>) con las respuestas
    /// exactas de un router concreto. Es la única forma de cubrir el camino de ÉXITO: en la
    /// máquina donde se escribió esto no hay ningún IGD (medido: cero respuestas al M-SEARCH ni
    /// por multicast ni por unicast al gateway), así que sin esto el éxito sería código que nunca
    /// se ejecuta.
    ///
    /// El eco externo va apagado en todos: un test no sale a internet.
    /// </summary>
    public sealed class HostConnectivityServiceTests
    {
        private const string Lan = "192.168.1.40";
        private const int Port = 7778;

        private static T Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

        private static HostConnectivityOptions QuietOptions() => new HostConnectivityOptions
        {
            UpnpEnabled = true,
            ExternalEchoEnabled = false,
            HttpTimeoutMs = 2000,
        };

        private static Func<Task<IgdLocator.Result>> LocatorFor(FakeUpnpDevice device)
        {
            var gateway = new IgdGateway(
                new IgdService("urn:schemas-upnp-org:service:WANIPConnection:1", device.ControlUrl),
                new Uri("http://127.0.0.1/rootDesc.xml"));

            return () => Task.FromResult(IgdLocator.Result.Found(gateway, 1));
        }

        private static Func<Task<IgdLocator.Result>> NoGateway() =>
            () => Task.FromResult(IgdLocator.Result.NotFound(
                ConnectivityCode.UpnpUnavailable, "nadie contestó al SSDP", 0));

        private static string ExternalIp(string ip) =>
            "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<s:Body><u:GetExternalIPAddressResponse xmlns:u=\"urn:x\">" +
            $"<NewExternalIPAddress>{ip}</NewExternalIPAddress>" +
            "</u:GetExternalIPAddressResponse></s:Body></s:Envelope>";

        private const string AddOk =
            "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<s:Body><u:AddPortMappingResponse xmlns:u=\"urn:x\"/></s:Body></s:Envelope>";

        private static string MappingEntry(string client, int internalPort, string enabled = "1",
            int lease = 3600) =>
            "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<s:Body><u:GetSpecificPortMappingEntryResponse xmlns:u=\"urn:x\">" +
            $"<NewInternalPort>{internalPort}</NewInternalPort>" +
            $"<NewInternalClient>{client}</NewInternalClient>" +
            $"<NewEnabled>{enabled}</NewEnabled>" +
            "<NewPortMappingDescription>Backrooms Survival</NewPortMappingDescription>" +
            $"<NewLeaseDuration>{lease}</NewLeaseDuration>" +
            "</u:GetSpecificPortMappingEntryResponse></s:Body></s:Envelope>";

        // ─── El camino que todo esto persigue ───

        /// Router con UPnP, WAN pública, mapeo pedido y **releído apuntando a este PC**. Es el
        /// único caso en el que se anuncia la IP pública.
        [Test]
        public void AGoodRouterLightsEveryRungAndYieldsAPublishablePublicHost()
        {
            using (var router = new FakeUpnpDevice())
            {
                router.EnqueueOk(ExternalIp("88.16.240.7"));
                router.EnqueueOk(AddOk);
                router.EnqueueOk(MappingEntry(Lan, Port));

                var service = new HostConnectivityService(QuietOptions());
                HostConnectivityReport report = Run(() =>
                    service.PrepareAsync(Lan, Port, LocatorFor(router)));

                Assert.IsTrue(report.Has(ConnectivityRung.PublicIpKnown));
                Assert.IsTrue(report.Has(ConnectivityRung.PortMappingRequested));
                Assert.IsTrue(report.Has(ConnectivityRung.PortMappingConfirmed));
                Assert.AreEqual("88.16.240.7", report.ConfirmedPublicHost);
                CollectionAssert.IsEmpty(report.Codes);
                StringAssert.Contains("confirmado", service.MappingReason);
            }
        }

        // ─── Los tres fallos de UPnP, cada uno con su código ───

        /// **Éste es el fallo que justifica que exista la relectura.** El router contesta 200 al
        /// `AddPortMapping` y luego resulta que el reenvío apunta a otro PC. Sin releerlo, el host
        /// anunciaría una IP pública que lleva al ordenador del vecino.
        [Test]
        public void ARouterThatAcceptsAndAppliesSomethingElseIsNotConfirmed()
        {
            using (var router = new FakeUpnpDevice())
            {
                router.EnqueueOk(ExternalIp("88.16.240.7"));
                router.EnqueueOk(AddOk);
                router.EnqueueOk(MappingEntry("192.168.1.99", Port));

                var service = new HostConnectivityService(QuietOptions());
                HostConnectivityReport report = Run(() =>
                    service.PrepareAsync(Lan, Port, LocatorFor(router)));

                Assert.IsTrue(report.Has(ConnectivityRung.PortMappingRequested));
                Assert.IsFalse(report.Has(ConnectivityRung.PortMappingConfirmed));
                Assert.IsTrue(report.HasCode(ConnectivityCode.UpnpMappingFailed));
                Assert.IsNull(report.ConfirmedPublicHost, "no se puede anunciar un reenvío ajeno");
                StringAssert.Contains("192.168.1.99", service.MappingReason);
            }
        }

        /// Un mapeo que el router aplica pero deja deshabilitado tampoco vale.
        [Test]
        public void ADisabledMappingIsNotConfirmed()
        {
            using (var router = new FakeUpnpDevice())
            {
                router.EnqueueOk(ExternalIp("88.16.240.7"));
                router.EnqueueOk(AddOk);
                router.EnqueueOk(MappingEntry(Lan, Port, enabled: "0"));

                HostConnectivityReport report = Run(() =>
                    new HostConnectivityService(QuietOptions()).PrepareAsync(Lan, Port, LocatorFor(router)));

                Assert.IsFalse(report.Has(ConnectivityRung.PortMappingConfirmed));
                Assert.IsTrue(report.HasCode(ConnectivityCode.UpnpMappingFailed));
            }
        }

        /// El puerto externo ya lo tiene otro (`718`). Se registra el fallo, no se confirma nada.
        [Test]
        public void APortConflictIsReportedAsAMappingFailure()
        {
            using (var router = new FakeUpnpDevice())
            {
                router.EnqueueOk(ExternalIp("88.16.240.7"));
                router.EnqueueFault(718, "ConflictInMappingEntry");

                var service = new HostConnectivityService(QuietOptions());
                HostConnectivityReport report = Run(() =>
                    service.PrepareAsync(Lan, Port, LocatorFor(router)));

                Assert.IsTrue(report.HasCode(ConnectivityCode.UpnpMappingFailed));
                Assert.IsFalse(report.Has(ConnectivityRung.PortMappingConfirmed));
                StringAssert.Contains("ConflictInMappingEntry", service.MappingReason);
            }
        }

        /// **UPNP_UNAVAILABLE: el caso de esta misma máquina de desarrollo.** No hay router que
        /// conteste. El host sigue funcionando; lo que no hay es endpoint público.
        [Test]
        public void WithNoGatewayTheCodeIsUpnpUnavailableAndTheHostStillWorks()
        {
            var service = new HostConnectivityService(QuietOptions());
            HostConnectivityReport report = Run(() => service.PrepareAsync(Lan, Port, NoGateway()));

            Assert.IsTrue(report.HasCode(ConnectivityCode.UpnpUnavailable));
            Assert.IsTrue(report.HasCode(ConnectivityCode.PublicEndpointUnknown));
            Assert.IsFalse(report.Has(ConnectivityRung.PortMappingRequested));
            Assert.IsNull(report.ConfirmedPublicHost);
            Assert.AreEqual(Lan, report.LanIp, "la LAN se sigue sabiendo: la partida sirve en red local");
            StringAssert.Contains("LAN", report.DescribeReachability());
        }

        /// **UPNP_DISABLED es otro código que UPNP_UNAVAILABLE**, y la diferencia importa: "no hay
        /// router que lo soporte" y "no quisimos preguntar" mandan a mirar sitios distintos.
        [Test]
        public void TurningUpnpOffIsReportedAsDisabledNotAsUnavailable()
        {
            var options = QuietOptions();
            options.UpnpEnabled = false;

            var service = new HostConnectivityService(options);
            HostConnectivityReport report = Run(() => service.PrepareAsync(Lan, Port, NoGateway()));

            Assert.IsTrue(report.HasCode(ConnectivityCode.UpnpDisabled));
            Assert.IsFalse(report.HasCode(ConnectivityCode.UpnpUnavailable));
            Assert.IsFalse(report.Has(ConnectivityRung.PortMappingRequested));
        }

        // ─── CGNAT ───

        /// El router tiene UPnP, acepta el mapeo y lo aplica bien — y **da igual**: su WAN está en
        /// el rango del NAT del operador, así que ese reenvío no le sirve a nadie de fuera. Se
        /// avisa y NO se anuncia la IP pública.
        [Test]
        public void UnderCarrierGradeNatNothingIsPublishedEvenWithAConfirmedMapping()
        {
            using (var router = new FakeUpnpDevice())
            {
                router.EnqueueOk(ExternalIp("100.64.0.1"));
                router.EnqueueOk(AddOk);
                router.EnqueueOk(MappingEntry(Lan, Port));

                var service = new HostConnectivityService(QuietOptions());
                HostConnectivityReport report = Run(() =>
                    service.PrepareAsync(Lan, Port, LocatorFor(router)));

                Assert.IsTrue(report.HasCode(ConnectivityCode.CgnatSuspected));
                Assert.IsTrue(report.Has(ConnectivityRung.PortMappingConfirmed),
                    "el mapeo se pide igual: la heurística puede equivocarse y la evidencia se guarda");
                Assert.IsNull(report.ConfirmedPublicHost);
                StringAssert.Contains("CGNAT", report.DescribeReachability());
            }
        }

        /// Doble NAT: la WAN del router es privada. Mismo aviso.
        [Test]
        public void ADoubleNatIsAlsoFlagged()
        {
            using (var router = new FakeUpnpDevice())
            {
                router.EnqueueOk(ExternalIp("192.168.0.1"));
                router.EnqueueOk(AddOk);
                router.EnqueueOk(MappingEntry(Lan, Port));

                var service = new HostConnectivityService(QuietOptions());
                HostConnectivityReport report = Run(() =>
                    service.PrepareAsync(Lan, Port, LocatorFor(router)));

                Assert.IsTrue(report.HasCode(ConnectivityCode.CgnatSuspected));
                StringAssert.Contains("doble NAT", service.CgnatReason);
            }
        }

        // ─── Limpieza ───

        /// El teardown borra el reenvío. Después, el informe ya no puede seguir diciendo que hay
        /// mapeo: si lo dijera, la siguiente partida publicaría su IP pública sin tener ninguno.
        [Test]
        public void ReleaseDeletesTheMappingAndStopsClaimingIt()
        {
            using (var router = new FakeUpnpDevice())
            {
                router.EnqueueOk(ExternalIp("88.16.240.7"));
                router.EnqueueOk(AddOk);
                router.EnqueueOk(MappingEntry(Lan, Port));
                router.EnqueueOk("<?xml version=\"1.0\"?><s:Envelope " +
                                 "xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
                                 "<s:Body><u:DeletePortMappingResponse xmlns:u=\"urn:x\"/></s:Body></s:Envelope>");

                var service = new HostConnectivityService(QuietOptions());
                Run(() => service.PrepareAsync(Lan, Port, LocatorFor(router)));
                Assert.IsTrue(service.Report.Has(ConnectivityRung.PortMappingConfirmed));

                Assert.IsTrue(Run(() => service.ReleaseAsync()));

                Assert.IsFalse(service.Report.Has(ConnectivityRung.PortMappingRequested));
                Assert.IsFalse(service.Report.Has(ConnectivityRung.PortMappingConfirmed));
                Assert.IsNull(service.Report.ConfirmedPublicHost);
                StringAssert.Contains("DeletePortMapping", router.Requests[3]);
            }
        }

        /// Borrar algo que ya no está (`714`) es exactamente el estado que se quería. No es fallo.
        [Test]
        public void DeletingAMappingThatIsAlreadyGoneCountsAsSuccess()
        {
            using (var router = new FakeUpnpDevice())
            {
                router.EnqueueOk(ExternalIp("88.16.240.7"));
                router.EnqueueOk(AddOk);
                router.EnqueueOk(MappingEntry(Lan, Port));
                router.EnqueueFault(714, "NoSuchEntryInArray");

                var service = new HostConnectivityService(QuietOptions());
                Run(() => service.PrepareAsync(Lan, Port, LocatorFor(router)));

                Assert.IsTrue(Run(() => service.ReleaseAsync()));
            }
        }

        /// El teardown corre también cuando nunca hubo router. No puede lanzar ni colgarse.
        [Test]
        public void ReleaseWithoutAGatewayIsSafe()
        {
            var service = new HostConnectivityService(QuietOptions());
            Run(() => service.PrepareAsync(Lan, Port, NoGateway()));

            Assert.IsTrue(Run(() => service.ReleaseAsync()));
            Assert.IsTrue(Run(() => service.ReleaseAsync()), "y es idempotente");
        }
    }
}
