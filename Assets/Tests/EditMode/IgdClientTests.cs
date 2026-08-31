using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using BackroomsSurvival.Connectivity;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El cliente IGD contra un router de mentira que habla HTTP de verdad
    /// (<see cref="FakeUpnpDevice"/>, TcpListener en loopback).
    ///
    /// Lo que se prueba aquí no es lógica: es el trato con HTTP, que es lo que se rompe contra un
    /// router real y no se ve en ningún log del juego. En concreto que un fallo SOAP —que llega
    /// como **HTTP 500 con cuerpo**— se lea de la excepción en vez de perderse, porque de eso
    /// depende distinguir "este router sólo hace mapeos permanentes, reintenta" de "el router no
    /// contesta".
    ///
    /// **Ninguno de estos tests demuestra que el UPnP funcione en la red de nadie.** Demuestran
    /// que el cliente se comporta ante respuestas que ya sabemos que existen. En la máquina donde
    /// se escribió esto no hay IGD (medido: cero respuestas al M-SEARCH), así que el camino de
    /// éxito con hardware real sigue sin validar.
    /// </summary>
    public sealed class IgdClientTests
    {
        private FakeUpnpDevice _device;

        [SetUp]
        public void SetUp() => _device = new FakeUpnpDevice();

        [TearDown]
        public void TearDown()
        {
            _device?.Dispose();
            _device = null;
        }

        private IgdGateway Gateway()
        {
            var service = new IgdService("urn:schemas-upnp-org:service:WANIPConnection:1", _device.ControlUrl);
            return new IgdGateway(service, new Uri("http://127.0.0.1/rootDesc.xml"));
        }

        /// Se bloquea dentro de un `Task.Run` a propósito: en el editor el hilo principal tiene el
        /// `SynchronizationContext` de Unity, y bloquearlo mientras una continuación espera turno
        /// en ese mismo contexto es un interbloqueo. La biblioteca ya usa `ConfigureAwait(false)`,
        /// pero un test no debería depender de eso para no colgar el editor.
        private static T Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

        private const string AddOkBody =
            "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<s:Body><u:AddPortMappingResponse xmlns:u=\"urn:x\"/></s:Body></s:Envelope>";

        // ─── Camino normal ───

        [Test]
        public void TheExternalIpIsReadFromTheRouter()
        {
            _device.EnqueueOk(
                "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
                "<s:Body><u:GetExternalIPAddressResponse xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
                "<NewExternalIPAddress>88.16.240.7</NewExternalIPAddress>" +
                "</u:GetExternalIPAddressResponse></s:Body></s:Envelope>");

            string ip = Run(() => Gateway().GetExternalIpAsync(3000));

            Assert.AreEqual("88.16.240.7", ip);
            StringAssert.Contains(
                "SOAPAction: \"urn:schemas-upnp-org:service:WANIPConnection:1#GetExternalIPAddress\"",
                _device.Requests[0]);
        }

        [Test]
        public void AddPortMappingSendsTheArgumentsTheRouterExpects()
        {
            _device.EnqueueOk(AddOkBody);

            IgdCallResult result = Run(() => Gateway().AddPortMappingAsync(7778, 7778, "192.168.1.40", 3600, 3000));

            Assert.IsTrue(result.Ok, result.Error);
            string sent = _device.Requests[0];
            StringAssert.Contains("<NewExternalPort>7778</NewExternalPort>", sent);
            StringAssert.Contains("<NewProtocol>UDP</NewProtocol>", sent);
            StringAssert.Contains("<NewInternalClient>192.168.1.40</NewInternalClient>", sent);
            StringAssert.Contains("<NewLeaseDuration>3600</NewLeaseDuration>", sent);
            StringAssert.Contains("<NewPortMappingDescription>Backrooms Survival</NewPortMappingDescription>", sent);
        }

        // ─── Fallos de UPnP ───

        /// `725 OnlyPermanentLeasesSupported` es un router VÁLIDO diciendo "yo no caduco mapeos".
        /// Se reintenta una vez sin caducidad; tratarlo como fallo dejaría sin UPnP a una familia
        /// entera de routers.
        [Test]
        public void APermanentLeaseRouterIsRetriedWithoutExpiry()
        {
            _device.EnqueueFault(725, "OnlyPermanentLeasesSupported");
            _device.EnqueueOk(AddOkBody);

            IgdCallResult result = Run(() => Gateway().AddPortMappingAsync(7778, 7778, "192.168.1.40", 3600, 3000));

            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual(2, _device.Requests.Count, "tenía que reintentar exactamente una vez");
            StringAssert.Contains("<NewLeaseDuration>3600</NewLeaseDuration>", _device.Requests[0]);
            StringAssert.Contains("<NewLeaseDuration>0</NewLeaseDuration>", _device.Requests[1]);
        }

        /// Un conflicto de puerto NO se reintenta: repetir la misma petición daría el mismo `718`.
        /// Quién decide qué hacer con eso es el orquestador, no el cliente.
        [Test]
        public void APortConflictIsReportedWithoutRetrying()
        {
            _device.EnqueueFault(718, "ConflictInMappingEntry");

            IgdCallResult result = Run(() => Gateway().AddPortMappingAsync(7778, 7778, "192.168.1.40", 3600, 3000));

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(718, result.ErrorCode);
            Assert.IsTrue(IgdSoap.IsPortConflict(result.ErrorCode));
            Assert.AreEqual(1, _device.Requests.Count);
        }

        /// El código de error viaja en el CUERPO de un HTTP 500. Si el cliente no lo sacara de la
        /// excepción, todos los fallos de UPnP se verían iguales: "el router no contesta".
        [Test]
        public void ASoapFaultArrivesAsHttp500AndItsCodeSurvives()
        {
            _device.EnqueueFault(501, "ActionFailed");

            IgdCallResult result = Run(() => Gateway().AddPortMappingAsync(7778, 7778, "192.168.1.40", 3600, 3000));

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(501, result.ErrorCode);
            StringAssert.Contains("ActionFailed", result.Error);
        }

        /// Una relectura que devuelve `714` significa que el mapeo NO está. Se traduce a null, que
        /// es lo que impide encender el peldaño de confirmado.
        [Test]
        public void ARereadThatFindsNothingYieldsNoEntry()
        {
            _device.EnqueueFault(714, "NoSuchEntryInArray");

            PortMappingEntry entry = Run(() => Gateway().GetSpecificPortMappingAsync(7778, 3000));

            Assert.IsNull(entry);
        }

        // ─── Tiempos ───

        /// **El tope de tiempo tiene que ser real.** `HttpWebRequest.Timeout` sólo lo respeta la
        /// ruta síncrona; si la llamada usara la API asíncrona, un router que abre la conexión y se
        /// calla dejaría la pantalla de host parada hasta que se rindiera el sistema operativo.
        /// Aquí el servidor tarda 3 s a propósito y el cliente tiene 700 ms.
        [Test]
        public void ASilentRouterIsAbandonedWithinTheBudget()
        {
            _device.ResponseDelayMs = 3000;
            _device.EnqueueOk("<tarde/>");

            var clock = Stopwatch.StartNew();
            string ip = Run(() => Gateway().GetExternalIpAsync(700));
            clock.Stop();

            Assert.IsNull(ip);
            Assert.Less(clock.ElapsedMilliseconds, 2500,
                "el tope de tiempo no se aplicó: la llamada esperó al router en vez de rendirse");
        }

        /// Un puerto cerrado es un fallo de transporte, no de UPnP: `ErrorCode` sigue en 0 y el
        /// motivo no puede confundirse con un rechazo del router.
        [Test]
        public void AnUnreachableControlUrlIsATransportFailureNotAUpnpOne()
        {
            int deadPort = ClosedLoopbackPort();

            var service = new IgdService("urn:schemas-upnp-org:service:WANIPConnection:1",
                new Uri($"http://127.0.0.1:{deadPort}/ctl"));
            var gateway = new IgdGateway(service, new Uri("http://127.0.0.1/d.xml"));

            IgdCallResult result = Run(() => gateway.DeletePortMappingAsync(7778, 1500));

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(0, result.ErrorCode);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Error));
        }

        /// Descargar la descripción de una URL muerta devuelve null en vez de lanzar: es un "esa
        /// LOCATION no sirve", y el buscador tiene que poder pasar a la siguiente.
        [Test]
        public void ADeadDescriptionUrlYieldsNullInsteadOfThrowing()
        {
            int deadPort = ClosedLoopbackPort();

            string xml = Run(() => IgdGateway.DownloadDescriptionAsync(
                new Uri($"http://127.0.0.1:{deadPort}/rootDesc.xml"), 1500));

            Assert.IsNull(xml);
        }

        /// Un puerto de loopback que el sistema acaba de dar y ya está libre. Es lo más cerca de
        /// "cerrado con seguridad" que se puede pedir sin fijar un número a mano, que es como se
        /// escriben los tests que fallan un día en la máquina de otro.
        private static int ClosedLoopbackPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
