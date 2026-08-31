using System;
using System.Collections.Generic;
using BackroomsSurvival.Connectivity;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El protocolo UPnP-IGD sin tocar la red: leer una respuesta SSDP, encontrar el servicio WAN
    /// dentro del XML de descripción, construir el sobre SOAP y leer lo que vuelve.
    ///
    /// Todo lo que se prueba aquí falla igual con un router real y sin ninguna pista: un
    /// `controlURL` mal resuelto es un 404, un argumento fuera de orden es un `402`, y un servicio
    /// que se busca sin bajar por el árbol de dispositivos sencillamente no aparece. Son fallos
    /// que no se ven en el log del juego, sólo en el del router.
    /// </summary>
    public sealed class IgdProtocolTests
    {
        // ─── SSDP ───

        /// La respuesta típica. `LOCATION` es lo único que hace falta.
        [Test]
        public void ASearchResponseYieldsItsLocation()
        {
            const string payload =
                "HTTP/1.1 200 OK\r\n" +
                "CACHE-CONTROL: max-age=120\r\n" +
                "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
                "LOCATION: http://192.168.1.1:5000/rootDesc.xml\r\n" +
                "SERVER: Linux/3.4 UPnP/1.0 MiniUPnPd/1.9\r\n\r\n";

            SsdpMessage message = SsdpMessage.Parse(payload);

            Assert.IsTrue(message.TryGetLocation(out Uri location));
            Assert.AreEqual("http://192.168.1.1:5000/rootDesc.xml", location.AbsoluteUri);
            StringAssert.Contains("MiniUPnPd", message.Server);
        }

        /// Las cabeceras no traen mayúsculas fijas: `LOCATION`, `Location` y `location` vienen de
        /// marcas distintas. Comparar con `==` deja fuera a fabricantes enteros.
        [TestCase("LOCATION")]
        [TestCase("Location")]
        [TestCase("location")]
        public void HeaderNamesAreCaseInsensitive(string header)
        {
            SsdpMessage message = SsdpMessage.Parse($"HTTP/1.1 200 OK\r\n{header}: http://10.0.0.1/d.xml\r\n\r\n");
            Assert.IsTrue(message.TryGetLocation(out Uri location));
            Assert.AreEqual("http://10.0.0.1/d.xml", location.AbsoluteUri);
        }

        /// Hay firmware que usa `\n` a secas pese a que el RFC pide `\r\n`.
        [Test]
        public void BareLineFeedsAreAccepted()
        {
            SsdpMessage message = SsdpMessage.Parse("HTTP/1.1 200 OK\nLOCATION: http://10.0.0.1/d.xml\n\n");
            Assert.IsTrue(message.TryGetLocation(out _));
        }

        /// El datagrama puede venir de un búfer reutilizado y traer cola detrás del mensaje. La
        /// lectura corta en la línea en blanco y no se cree lo que venga después.
        [Test]
        public void TrailingGarbageAfterTheBlankLineIsIgnored()
        {
            SsdpMessage message = SsdpMessage.Parse(
                "HTTP/1.1 200 OK\r\nLOCATION: http://10.0.0.1/d.xml\r\n\r\nLOCATION: http://evil/\u0000\u0000");

            Assert.IsTrue(message.TryGetLocation(out Uri location));
            Assert.AreEqual("http://10.0.0.1/d.xml", location.AbsoluteUri);
        }

        /// Una `LOCATION` que no es http es un dispositivo roto u hostil, y de ahí sale la URL a
        /// la que luego se hace una petición.
        [TestCase("file:///c:/windows/system32/config")]
        [TestCase("ftp://10.0.0.1/x")]
        [TestCase("/relativa.xml")]
        [TestCase("")]
        public void ALocationThatIsNotAbsoluteHttpIsRejected(string value)
        {
            SsdpMessage message = SsdpMessage.Parse($"HTTP/1.1 200 OK\r\nLOCATION: {value}\r\n\r\n");
            Assert.IsFalse(message.TryGetLocation(out _));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("basura sin ninguna estructura")]
        public void GarbageParsesIntoAnEmptyMessageInsteadOfThrowing(string payload)
        {
            SsdpMessage message = SsdpMessage.Parse(payload);
            Assert.IsFalse(message.TryGetLocation(out _));
        }

        /// El M-SEARCH necesita la línea en blanco final y `MAN` entrecomillado; sin eso hay
        /// routers que no contestan nada, que es indistinguible de "no hay UPnP".
        [Test]
        public void TheSearchRequestIsWellFormed()
        {
            string text = System.Text.Encoding.ASCII.GetString(
                SsdpProbe.BuildSearchRequest(SsdpProbe.GatewaySearchTarget));

            StringAssert.StartsWith("M-SEARCH * HTTP/1.1\r\n", text);
            StringAssert.Contains("MAN: \"ssdp:discover\"\r\n", text);
            StringAssert.Contains("HOST: 239.255.255.250:1900\r\n", text);
            StringAssert.Contains("ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n", text);
            Assert.IsTrue(text.EndsWith("\r\n\r\n", StringComparison.Ordinal), "falta la línea en blanco final");
        }

        // ─── Descripción del dispositivo ───

        /// XML de un router real, con el namespace por defecto y el servicio WAN a tres niveles de
        /// profundidad. Los dos detalles son los que rompen un parseo ingenuo.
        private const string RouterDescription = @"<?xml version=""1.0""?>
<root xmlns=""urn:schemas-upnp-org:device-1-0"">
  <specVersion><major>1</major><minor>0</minor></specVersion>
  <device>
    <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
    <friendlyName>Router</friendlyName>
    <serviceList>
      <service>
        <serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>
        <controlURL>/ctl/L3F</controlURL>
      </service>
    </serviceList>
    <deviceList>
      <device>
        <deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
        <deviceList>
          <device>
            <deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                <controlURL>/ctl/IPConn</controlURL>
              </service>
            </serviceList>
          </device>
        </deviceList>
      </device>
    </deviceList>
  </device>
</root>";

        /// El servicio WAN está a tres niveles: `InternetGatewayDevice` → `WANDevice` →
        /// `WANConnectionDevice`. Mirar sólo el `serviceList` de la raíz encuentra
        /// `Layer3Forwarding` y nada más, o sea que da null en todos los routers del mundo.
        [Test]
        public void TheWanServiceIsFoundThreeLevelsDeep()
        {
            IgdService service = IgdDescription.TryParse(
                RouterDescription, new Uri("http://192.168.1.1:5000/rootDesc.xml"), out string reason);

            Assert.IsNotNull(service, reason);
            Assert.AreEqual("urn:schemas-upnp-org:service:WANIPConnection:1", service.ServiceType);
            Assert.AreEqual("http://192.168.1.1:5000/ctl/IPConn", service.ControlUrl.AbsoluteUri);
        }

        /// `URLBase` manda sobre la `LOCATION` cuando está: resolver contra la `LOCATION` teniendo
        /// `URLBase` con otro puerto manda cada llamada SOAP a un sitio donde no hay nadie.
        [Test]
        public void UrlBaseWinsOverTheLocationWhenPresent()
        {
            string xml = RouterDescription.Replace(
                "<specVersion>", "<URLBase>http://192.168.1.1:49152/upnp/</URLBase><specVersion>");

            IgdService service = IgdDescription.TryParse(
                xml, new Uri("http://192.168.1.1:5000/rootDesc.xml"), out string reason);

            Assert.IsNotNull(service, reason);
            Assert.AreEqual("http://192.168.1.1:49152/ctl/IPConn", service.ControlUrl.AbsoluteUri);
        }

        /// Un `controlURL` ya absoluto se respeta tal cual.
        [Test]
        public void AnAbsoluteControlUrlIsUsedVerbatim()
        {
            string xml = RouterDescription.Replace("/ctl/IPConn", "http://192.168.1.1:49000/soap");

            IgdService service = IgdDescription.TryParse(
                xml, new Uri("http://192.168.1.1:5000/rootDesc.xml"), out _);

            Assert.AreEqual("http://192.168.1.1:49000/soap", service.ControlUrl.AbsoluteUri);
        }

        /// PPPoE: el router expone `WANPPPConnection` y no `WANIPConnection`. Es la única conexión
        /// que tiene, así que hay que aceptarla.
        [Test]
        public void APppRouterIsAccepted()
        {
            string xml = RouterDescription.Replace("WANIPConnection:1", "WANPPPConnection:1");

            IgdService service = IgdDescription.TryParse(xml, new Uri("http://192.168.1.1/d.xml"), out _);

            Assert.IsNotNull(service);
            Assert.AreEqual("urn:schemas-upnp-org:service:WANPPPConnection:1", service.ServiceType);
        }

        /// Con las dos, gana la de IP. `:2` gana a `:1`, y el tipo elegido tiene que viajar tal
        /// cual en la `SOAPAction`: un router que expone `:2` rechaza las acciones dirigidas a `:1`.
        [Test]
        public void TheNewerIpConnectionServiceWins()
        {
            string xml = RouterDescription.Replace(
                "<serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>" +
                "\n                <controlURL>/ctl/IPConn</controlURL>",
                "<serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>" +
                "\n                <controlURL>/ctl/IPConn</controlURL>" +
                "\n              </service>\n              <service>" +
                "\n                <serviceType>urn:schemas-upnp-org:service:WANIPConnection:2</serviceType>" +
                "\n                <controlURL>/ctl/IPConn2</controlURL>");

            IgdService service = IgdDescription.TryParse(xml, new Uri("http://192.168.1.1/d.xml"), out string reason);

            Assert.IsNotNull(service, reason);
            Assert.AreEqual("urn:schemas-upnp-org:service:WANIPConnection:2", service.ServiceType);
        }

        /// Una impresora, una tele o un NAS también contestan al SSDP. No son routers y hay que
        /// decirlo con esas palabras: el usuario que lee el log tiene que entender por qué su
        /// "dispositivo UPnP encontrado" no sirve.
        [Test]
        public void ANonRouterUpnpDeviceIsRejectedWithAUsefulReason()
        {
            const string printer = @"<root xmlns=""urn:schemas-upnp-org:device-1-0""><device>
                <deviceType>urn:schemas-upnp-org:device:Printer:1</deviceType>
                <serviceList><service>
                  <serviceType>urn:schemas-upnp-org:service:PrintBasic:1</serviceType>
                  <controlURL>/ctl</controlURL>
                </service></serviceList></device></root>";

            IgdService service = IgdDescription.TryParse(printer, new Uri("http://10.0.0.5/d.xml"), out string reason);

            Assert.IsNull(service);
            StringAssert.Contains("impresora", reason);
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("<root>sin cerrar")]
        [TestCase("esto no es xml en absoluto")]
        public void UnreadableDescriptionsYieldNullAndAReason(string xml)
        {
            IgdService service = IgdDescription.TryParse(xml, new Uri("http://10.0.0.1/d.xml"), out string reason);

            Assert.IsNull(service);
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
        }

        // ─── SOAP ───

        /// El orden de los argumentos NO es decorativo: UPnP los define posicionales aunque se
        /// escriban con nombre, y hay routers que devuelven `402 InvalidArgs` si se cambia.
        [Test]
        public void TheEnvelopeKeepsTheArgumentOrder()
        {
            var arguments = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("NewRemoteHost", ""),
                new KeyValuePair<string, string>("NewExternalPort", "7778"),
                new KeyValuePair<string, string>("NewProtocol", "UDP"),
            };

            string envelope = IgdSoap.BuildEnvelope(
                "urn:schemas-upnp-org:service:WANIPConnection:1", "DeletePortMapping", arguments);

            int host = envelope.IndexOf("NewRemoteHost", StringComparison.Ordinal);
            int port = envelope.IndexOf("NewExternalPort", StringComparison.Ordinal);
            int protocol = envelope.IndexOf("NewProtocol", StringComparison.Ordinal);

            Assert.Less(host, port);
            Assert.Less(port, protocol);
            StringAssert.Contains("<u:DeletePortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">",
                envelope);
        }

        /// Las comillas de `SOAPAction` no son opcionales: sin ellas hay firmware que contesta
        /// `401 Invalid Action` y no da más pistas.
        [Test]
        public void TheSoapActionHeaderIsQuoted()
        {
            Assert.AreEqual("\"urn:schemas-upnp-org:service:WANIPConnection:1#AddPortMapping\"",
                IgdSoap.ActionHeader("urn:schemas-upnp-org:service:WANIPConnection:1", "AddPortMapping"));
        }

        [Test]
        public void ArgumentValuesAreXmlEscaped()
        {
            var arguments = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("NewPortMappingDescription", "a & b <c>"),
            };

            string envelope = IgdSoap.BuildEnvelope("urn:x", "AddPortMapping", arguments);

            StringAssert.Contains("a &amp; b &lt;c&gt;", envelope);
        }

        [Test]
        public void AnExternalIpResponseIsRead()
        {
            const string body = @"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""><s:Body>
<u:GetExternalIPAddressResponse xmlns:u=""urn:schemas-upnp-org:service:WANIPConnection:1"">
<NewExternalIPAddress>88.16.240.7</NewExternalIPAddress>
</u:GetExternalIPAddressResponse></s:Body></s:Envelope>";

            Assert.AreEqual("88.16.240.7", IgdSoap.ReadValue(body, "NewExternalIPAddress"));
            Assert.IsFalse(IgdSoap.TryReadFault(body, out _, out _));
        }

        /// Los códigos que este código sabe aprovechar. `725` pide reintentar sin caducidad, `718`
        /// pide otro puerto, `714` sobre una relectura significa "no hay mapeo".
        [TestCase(725, true, false, false)]
        [TestCase(718, false, true, false)]
        [TestCase(714, false, false, true)]
        [TestCase(501, false, false, false)]
        public void FaultCodesAreClassified(int code, bool permanent, bool conflict, bool missing)
        {
            string body = Fault(code, "whatever");

            Assert.IsTrue(IgdSoap.TryReadFault(body, out int parsed, out _));
            Assert.AreEqual(code, parsed);
            Assert.AreEqual(permanent, IgdSoap.DemandsPermanentLease(parsed));
            Assert.AreEqual(conflict, IgdSoap.IsPortConflict(parsed));
            Assert.AreEqual(missing, IgdSoap.IsNoSuchEntry(parsed));
        }

        /// Un código propietario que no está en la tabla se registra con su número. Es lo único
        /// honesto: inventarle un significado al error de un firmware manda a diagnosticar mal.
        [Test]
        public void AnUnknownFaultCodeIsReportedByNumber()
        {
            Assert.IsTrue(IgdSoap.TryReadFault(Fault(801, null), out int code, out string description));
            Assert.AreEqual(801, code);
            StringAssert.Contains("801", description);
        }

        // ─── Verificación del mapeo ───

        private const string MappingResponse = @"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""><s:Body>
<u:GetSpecificPortMappingEntryResponse xmlns:u=""urn:schemas-upnp-org:service:WANIPConnection:1"">
<NewInternalPort>7778</NewInternalPort>
<NewInternalClient>192.168.1.40</NewInternalClient>
<NewEnabled>1</NewEnabled>
<NewPortMappingDescription>Backrooms Survival</NewPortMappingDescription>
<NewLeaseDuration>3600</NewLeaseDuration>
</u:GetSpecificPortMappingEntryResponse></s:Body></s:Envelope>";

        [Test]
        public void AMappingThatPointsAtUsConfirms()
        {
            PortMappingEntry entry = IgdGateway.ReadMappingEntry(MappingResponse);

            Assert.IsNotNull(entry);
            Assert.AreEqual(7778, entry.InternalPort);
            Assert.AreEqual("192.168.1.40", entry.InternalClient);
            Assert.IsTrue(entry.Enabled);
            Assert.IsTrue(entry.Matches("192.168.1.40", 7778));
        }

        /// **El caso que hace falta que exista la verificación.** Hay routers que aceptan el
        /// `AddPortMapping` y aplican otra cosa: otro cliente interno, otro puerto, o deshabilitado.
        /// Si "confirmado" se dedujera del OK del `AddPortMapping`, el host anunciaría un endpoint
        /// público que reenvía al PC del vecino.
        [TestCase("192.168.1.99", 7778)]
        [TestCase("192.168.1.40", 7779)]
        public void AMappingThatPointsSomewhereElseDoesNotConfirm(string client, int port)
        {
            PortMappingEntry entry = IgdGateway.ReadMappingEntry(MappingResponse);
            Assert.IsFalse(entry.Matches(client, port));
        }

        [Test]
        public void ADisabledMappingDoesNotConfirm()
        {
            PortMappingEntry entry = IgdGateway.ReadMappingEntry(
                MappingResponse.Replace("<NewEnabled>1</NewEnabled>", "<NewEnabled>0</NewEnabled>"));

            Assert.IsFalse(entry.Enabled);
            Assert.IsFalse(entry.Matches("192.168.1.40", 7778));
        }

        /// Se ha visto firmware que escribe `true`/`false` donde el estándar pide `1`/`0`.
        [Test]
        public void ABooleanWordIsAcceptedForEnabled()
        {
            PortMappingEntry entry = IgdGateway.ReadMappingEntry(
                MappingResponse.Replace("<NewEnabled>1</NewEnabled>", "<NewEnabled>true</NewEnabled>"));

            Assert.IsTrue(entry.Enabled);
        }

        [TestCase("")]
        [TestCase("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body/></s:Envelope>")]
        public void AnEmptyOrIncompleteMappingResponseYieldsNull(string body)
        {
            Assert.IsNull(IgdGateway.ReadMappingEntry(body));
        }

        private static string Fault(int code, string description)
        {
            string text = description == null ? "" : $"<errorDescription>{description}</errorDescription>";
            return "<?xml version=\"1.0\"?>" +
                   "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault>" +
                   "<faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>" +
                   "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\">" +
                   $"<errorCode>{code}</errorCode>{text}" +
                   "</UPnPError></detail></s:Fault></s:Body></s:Envelope>";
        }
    }
}
