using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using BackroomsSurvival.Connectivity;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// De dónde sale la IP pública, y qué pasa cuando no sale de ningún sitio.
    ///
    /// **Ningún test de aquí sale a internet.** Los ecos entran por parámetro y apuntan a un
    /// servidor de mentira en loopback (<see cref="FakeUpnpDevice"/>, que también sirve GET). Un
    /// test que llamara a `api.ipify.org` de verdad fallaría cuando falle la red de quien lo corre
    /// y tardaría lo que tarde internet: eso no es un test, es un monitor.
    /// </summary>
    public sealed class PublicIpResolverTests
    {
        private static T Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

        private static IgdGateway GatewayFor(FakeUpnpDevice device) =>
            new IgdGateway(
                new IgdService("urn:schemas-upnp-org:service:WANIPConnection:1", device.ControlUrl),
                new Uri("http://127.0.0.1/d.xml"));

        private static string ExternalIpBody(string ip) =>
            "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<s:Body><u:GetExternalIPAddressResponse xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
            $"<NewExternalIPAddress>{ip}</NewExternalIPAddress>" +
            "</u:GetExternalIPAddressResponse></s:Body></s:Envelope>";

        // ─── Lectura de la respuesta de un eco ───

        /// El salto de línea final lo mandan casi todos.
        [TestCase("88.16.240.7")]
        [TestCase("88.16.240.7\n")]
        [TestCase("  88.16.240.7  \r\n")]
        public void APlainPublicIPv4IsAccepted(string body)
        {
            Assert.AreEqual("88.16.240.7", PublicIpResolver.ParseEchoResponse(body));
        }

        /// Un eco que devuelve una dirección no enrutable está roto, o hay un proxy contestando
        /// por él. Publicarla dejaría el lobby apuntando a ninguna parte.
        [TestCase("192.168.1.40")]
        [TestCase("127.0.0.1")]
        [TestCase("100.64.0.1")]
        [TestCase("0.0.0.0")]
        [TestCase("169.254.1.1")]
        public void ANonRoutableAnswerIsRejected(string body)
        {
            Assert.IsNull(PublicIpResolver.ParseEchoResponse(body));
        }

        /// IPv6 se rechaza aunque sea pública: el socket del backend hace bind en `0.0.0.0`, o sea
        /// que sería una dirección correcta a la que este juego no escucha. `icanhazip` devuelve
        /// IPv6 cuando la hay, así que el caso es real y no defensa gratuita.
        [TestCase("2001:db8::1")]
        [TestCase("2a02:9000::1")]
        public void APublicIPv6IsRejectedBecauseTheTransportIsIPv4(string body)
        {
            Assert.IsNull(PublicIpResolver.ParseEchoResponse(body));
        }

        /// Un portal cautivo devuelve HTML con un 200. Sin el tope de tamaño y la validación
        /// estricta, esa página acabaría en `connect_ip`.
        [Test]
        public void HtmlFromACaptivePortalIsRejected()
        {
            Assert.IsNull(PublicIpResolver.ParseEchoResponse("<html><body>Inicia sesión</body></html>"));
            Assert.IsNull(PublicIpResolver.ParseEchoResponse(new string('8', 400)));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("no soy una ip")]
        public void GarbageIsRejected(string body)
        {
            Assert.IsNull(PublicIpResolver.ParseEchoResponse(body));
        }

        // ─── Varios ecos ───

        /// **La razón de que haya tres y no uno.** El primero está caído; el segundo contesta y la
        /// consulta sale adelante. Con un único servicio, el día que se cae nadie puede anunciar
        /// partida a internet.
        [Test]
        public void ADeadEchoFallsThroughToTheNextOne()
        {
            using (var good = new FakeUpnpDevice())
            {
                good.EnqueueOk("88.16.240.7\n");

                var endpoints = new List<string>
                {
                    $"http://127.0.0.1:{ClosedPort()}/",
                    good.ControlUrl.AbsoluteUri,
                };

                Assert.AreEqual("88.16.240.7", Run(() => PublicIpResolver.QueryEchoesAsync(endpoints, 1500)));
            }
        }

        /// Un eco que contesta basura tampoco corta la cadena: se descarta y se prueba el
        /// siguiente, igual que uno caído.
        [Test]
        public void AnEchoThatAnswersGarbageIsSkipped()
        {
            using (var broken = new FakeUpnpDevice())
            using (var good = new FakeUpnpDevice())
            {
                broken.EnqueueOk("<html>error</html>");
                good.EnqueueOk("88.16.240.7");

                var endpoints = new List<string> { broken.ControlUrl.AbsoluteUri, good.ControlUrl.AbsoluteUri };

                Assert.AreEqual("88.16.240.7", Run(() => PublicIpResolver.QueryEchoesAsync(endpoints, 1500)));
            }
        }

        [Test]
        public void WithEveryEchoDownTheAnswerIsSimplyUnknown()
        {
            var endpoints = new List<string>
            {
                $"http://127.0.0.1:{ClosedPort()}/",
                $"http://127.0.0.1:{ClosedPort()}/",
            };

            Assert.IsNull(Run(() => PublicIpResolver.QueryEchoesAsync(endpoints, 1000)));
        }

        /// **El tope de tiempo del eco.** Un servicio que acepta la conexión y se calla no puede
        /// dejar la creación de partida colgada: aquí tarda 3 s y el presupuesto es 700 ms.
        [Test]
        public void ASilentEchoIsAbandonedWithinTheBudget()
        {
            using (var slow = new FakeUpnpDevice())
            {
                slow.ResponseDelayMs = 3000;
                slow.EnqueueOk("88.16.240.7");

                var clock = Stopwatch.StartNew();
                string ip = Run(() => PublicIpResolver.QueryEchoesAsync(
                    new List<string> { slow.ControlUrl.AbsoluteUri }, 700));
                clock.Stop();

                Assert.IsNull(ip);
                Assert.Less(clock.ElapsedMilliseconds, 2500, "el tope de tiempo del eco no se aplicó");
            }
        }

        // ─── Precedencia ───

        /// El router manda. No depende de terceros, no le cuenta a nadie de fuera que este PC
        /// existe, y es la única fuente que puede delatar un CGNAT.
        [Test]
        public void TheRouterAnswerWins()
        {
            using (var router = new FakeUpnpDevice())
            {
                router.EnqueueOk(ExternalIpBody("88.16.240.7"));

                PublicIpAnswer answer = Run(() => PublicIpResolver.ResolveAsync(
                    GatewayFor(router), allowExternalEcho: false, timeoutMs: 1500));

                Assert.AreEqual("88.16.240.7", answer.Ip);
                Assert.AreEqual(PublicIpSource.InternetGatewayDevice, answer.Source);
                Assert.AreEqual("88.16.240.7", answer.GatewayWanIp);
            }
        }

        /// **La respuesta cruda del router se guarda aunque no sirva como IP pública.** Es la
        /// mitad del juicio de CGNAT: sin ella, `100.64.0.1` se perdería y el diagnóstico se
        /// quedaría en "no se sabe la IP pública", que manda a mirar el sitio equivocado.
        [Test]
        public void ACarrierGradeWanIsKeptAsEvidenceButNotUsedAsThePublicIp()
        {
            using (var router = new FakeUpnpDevice())
            {
                router.EnqueueOk(ExternalIpBody("100.64.0.1"));

                PublicIpAnswer answer = Run(() => PublicIpResolver.ResolveAsync(
                    GatewayFor(router), allowExternalEcho: false, timeoutMs: 1500));

                Assert.IsNull(answer.Ip);
                Assert.AreEqual("100.64.0.1", answer.GatewayWanIp);
                Assert.AreEqual(PublicIpSource.None, answer.Source);
                StringAssert.Contains("100.64", answer.Reason);
            }
        }

        /// Sin IGD, el eco es el respaldo: la IP se conoce y la fuente lo dice.
        [Test]
        public void WithoutARouterTheEchoIsTheFallback()
        {
            using (var echo = new FakeUpnpDevice())
            {
                echo.EnqueueOk("88.16.240.7\n");

                PublicIpAnswer answer = Run(() => PublicIpResolver.ResolveAsync(
                    null, allowExternalEcho: true, timeoutMs: 1500, cancellation: default,
                    endpoints: new List<string> { echo.ControlUrl.AbsoluteUri }));

                Assert.AreEqual("88.16.240.7", answer.Ip);
                Assert.AreEqual(PublicIpSource.ExternalEcho, answer.Source);
            }
        }

        /// **El eco se consulta AUNQUE el router ya haya dado una IP buena**, porque las dos
        /// juntas son lo que permite juzgar el CGNAT. Que discrepen es un indicio; con una sola
        /// respuesta no hay nada que comparar.
        [Test]
        public void BothSourcesAreQueriedSoTheyCanBeCompared()
        {
            using (var router = new FakeUpnpDevice())
            using (var echo = new FakeUpnpDevice())
            {
                router.EnqueueOk(ExternalIpBody("192.0.2.5"));
                echo.EnqueueOk("88.16.240.7");

                PublicIpAnswer answer = Run(() => PublicIpResolver.ResolveAsync(
                    GatewayFor(router), allowExternalEcho: true, timeoutMs: 1500, cancellation: default,
                    endpoints: new List<string> { echo.ControlUrl.AbsoluteUri }));

                Assert.AreEqual("192.0.2.5", answer.GatewayWanIp);
                Assert.AreEqual("88.16.240.7", answer.ObservedIp);
                Assert.AreEqual(CgnatHeuristic.Verdict.Suspected,
                    CgnatHeuristic.Evaluate(answer.GatewayWanIp, answer.ObservedIp, out _));
            }
        }

        [Test]
        public void WithNoRouterAndNoEchoTheAnswerSaysSoInsteadOfGuessing()
        {
            PublicIpAnswer answer = Run(() => PublicIpResolver.ResolveAsync(
                null, allowExternalEcho: false, timeoutMs: 500));

            Assert.IsNull(answer.Ip);
            Assert.AreEqual(PublicIpSource.None, answer.Source);
            StringAssert.Contains("apagada", answer.Reason);
        }

        // ─── El interruptor ───

        /// Se puede apagar la salida a terceros sin dejar de poder hostear. Es la palanca para
        /// quien no quiera que ningún servicio externo vea su dirección.
        [TestCase("1", true)]
        [TestCase("true", true)]
        [TestCase("0", false)]
        [TestCase("false", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void TheEnvironmentSwitchIsHonoured(string value, bool expected)
        {
            string previous = Environment.GetEnvironmentVariable(PublicIpResolver.DisableEchoVariable);
            try
            {
                Environment.SetEnvironmentVariable(PublicIpResolver.DisableEchoVariable, value);
                Assert.AreEqual(expected, PublicIpResolver.EchoDisabledByEnvironment());
            }
            finally
            {
                Environment.SetEnvironmentVariable(PublicIpResolver.DisableEchoVariable, previous);
            }
        }

        /// Los ecos de producción son varios y de dueños distintos: es la invariante que impide
        /// que alguien "simplifique" a uno solo y reintroduzca el punto único de fallo.
        [Test]
        public void ProductionShipsMoreThanOneEcho()
        {
            Assert.GreaterOrEqual(PublicIpResolver.EchoEndpoints.Length, 2);
            CollectionAssert.AllItemsAreUnique(PublicIpResolver.EchoEndpoints);
            foreach (string endpoint in PublicIpResolver.EchoEndpoints)
                StringAssert.StartsWith("https://", endpoint);
        }

        private static int ClosedPort()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
