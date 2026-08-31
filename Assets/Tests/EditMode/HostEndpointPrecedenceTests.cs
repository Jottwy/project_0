using System.Collections.Generic;
using BackroomsSurvival.Connectivity;
using BackroomsSurvival.Lobbies;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Qué acaba en `connect_ip`, dependiendo de hasta dónde llegó la conectividad.
    ///
    /// Es la unión de dos piezas que a propósito NO se fundieron:
    /// <see cref="HostEndpointCandidates"/> pone la IP pública confirmada delante, y
    /// <see cref="LobbyEndpointPolicy"/> —que es pura y ya tenía sus tests— sigue decidiendo con
    /// la misma regla de siempre: manda el humano, luego el primer candidato utilizable, y si no
    /// hay ninguno defendible **no se anuncia nada**.
    ///
    /// La regla que hay que proteger de un "arreglo" futuro es la última: publicar un endpoint
    /// malo le cuesta 15 s de espera a quien lo elige, y no publicarlo no le cuesta nada.
    /// </summary>
    public sealed class HostEndpointPrecedenceTests
    {
        private const string Lan = "192.168.1.40";
        private const string Public = "88.16.240.7";

        /// El valor por defecto del campo del panel. No sirve como endpoint remoto y por eso el
        /// camino interesante empieza aquí.
        private const string DefaultField = "127.0.0.1";

        [SetUp]
        public void SetUp() => HostEndpointCandidates.ResetCache();

        private static List<string> Local() => new List<string> { Lan };

        private static HostConnectivityReport Report(bool publicKnown, bool mappingConfirmed, bool cgnat,
            int port = 7778)
        {
            var report = new HostConnectivityReport();
            report.SetLan(Lan, port);
            if (publicKnown) report.SetPublicIp(Public, PublicIpSource.InternetGatewayDevice);
            if (mappingConfirmed)
            {
                report.MarkMappingRequested();
                report.MarkMappingConfirmed();
            }

            if (cgnat) report.AddCode(ConnectivityCode.CgnatSuspected);
            return report;
        }

        private static string Resolve(string field, HostConnectivityReport report, out string reason) =>
            LobbyEndpointPolicy.ResolvePublishableHost(
                field, HostEndpointCandidates.WithConfirmedPublicFirst(report, Local()), out reason);

        // ─── Con todo en orden ───

        /// El caso que persigue la tarea entera: mapeo confirmado ⇒ se anuncia la IP pública.
        [Test]
        public void AConfirmedMappingPublishesThePublicIp()
        {
            Assert.AreEqual(Public, Resolve(DefaultField, Report(true, true, false), out _));
        }

        // ─── Fallback manual ───

        /// **Lo que escribió el humano gana a la IP pública detectada.** Puede saber algo que
        /// nosotros no: un reenvío hecho a mano, un DNS dinámico, una IP fija. Quitarle esa
        /// precedencia sería decidir por él.
        [TestCase("micasa.duckdns.org")]
        [TestCase("203.0.113.9")]
        [TestCase("192.168.1.55")]
        public void WhatTheHumanTypedWinsOverEverythingDetected(string field)
        {
            Assert.AreEqual(field, Resolve(field, Report(true, true, false), out string reason));
            StringAssert.Contains("panel", reason);
        }

        // ─── Cuando la conectividad no llega ───

        /// Sin mapeo confirmado NO se anuncia la pública, aunque se conozca. Conocer la IP no
        /// significa que alguien pueda llegar a ella, y publicarla mandaría a cada joiner a
        /// esperar quince segundos a un puerto cerrado.
        [Test]
        public void KnowingThePublicIpWithoutAConfirmedMappingIsNotEnough()
        {
            Assert.AreEqual(Lan, Resolve(DefaultField, Report(true, false, false), out _));
        }

        /// UPNP_UNAVAILABLE / UPNP_MAPPING_FAILED: se anuncia la LAN, que es exactamente lo que se
        /// anunciaba antes de que existiera nada de esto. **Cero regresión en red local.**
        [Test]
        public void AUpnpFailureFallsBackToTheSameLanBehaviourAsBefore()
        {
            var report = Report(true, false, false);
            report.AddCode(ConnectivityCode.UpnpMappingFailed);

            Assert.AreEqual(Lan, Resolve(DefaultField, report, out _));
        }

        /// Con CGNAT no se anuncia la pública ni con el mapeo confirmado: ese reenvío no le sirve
        /// a nadie de fuera.
        [Test]
        public void UnderCgnatTheLanIsWhatGetsPublished()
        {
            Assert.AreEqual(Lan, Resolve(DefaultField, Report(true, true, true), out _));
        }

        /// Sin nada que anunciar, **no se anuncia**. Es la regla vieja y la que más fácil se
        /// rompería "arreglando" esto con un valor por defecto.
        [Test]
        public void WithNothingDefensibleTheGameIsNotAnnouncedAtAll()
        {
            var report = new HostConnectivityReport();
            report.SetLan(null, 7778);

            string host = LobbyEndpointPolicy.ResolvePublishableHost(
                DefaultField,
                HostEndpointCandidates.WithConfirmedPublicFirst(report, new List<string> { "169.254.5.254" }),
                out string reason);

            Assert.IsNull(host);
            StringAssert.Contains("NO se anuncia", reason);
        }

        // ─── El puerto ───

        /// El puerto que viaja es el **realmente elegido**, no el tecleado: `SelectLaunchConfig`
        /// lo desplaza cuando el suyo está ocupado, y publicar el tecleado dejaría el lobby
        /// apuntando a un puerto muerto. El informe lo lleva desde el principio y con él se pide
        /// el mapeo.
        [TestCase(7778)]
        [TestCase(7779)]
        [TestCase(51234)]
        public void TheReportCarriesThePortThatWasActuallySelected(int port)
        {
            HostConnectivityReport report = Report(true, true, false, port);

            Assert.AreEqual(port, report.Port);
            StringAssert.Contains("port=" + port, report.ToLogLine());
        }

        // ─── Coste ───

        /// Esto lo llama un `Update`. Reservar una lista por frame sería el mismo error que ya se
        /// pagó una vez abriendo un socket por frame para resolver direcciones locales.
        [Test]
        public void TheOrderedListIsNotRebuiltEveryFrame()
        {
            HostConnectivityReport report = Report(true, true, false);
            List<string> local = Local();

            IReadOnlyList<string> first = HostEndpointCandidates.WithConfirmedPublicFirst(report, local);
            IReadOnlyList<string> second = HostEndpointCandidates.WithConfirmedPublicFirst(report, local);

            Assert.AreSame(first, second);
            Assert.AreEqual(Public, first[0]);
            Assert.AreEqual(Lan, first[1]);
        }

        /// Sin IP pública confirmada se devuelve la lista de origen TAL CUAL: cero reservas en el
        /// caso más común, que es el de una máquina sin UPnP.
        [Test]
        public void WithoutAPublicHostTheSourceListIsReturnedUntouched()
        {
            List<string> local = Local();
            Assert.AreSame(local, HostEndpointCandidates.WithConfirmedPublicFirst(Report(false, false, false), local));
            Assert.AreSame(local, HostEndpointCandidates.WithConfirmedPublicFirst(null, local));
        }

        /// Un PC con IP pública directa la tiene también en su lista local. No se duplica.
        [Test]
        public void APublicAddressAlreadyInTheLocalListIsNotDuplicated()
        {
            IReadOnlyList<string> ordered = HostEndpointCandidates.WithConfirmedPublicFirst(
                Report(true, true, false), new List<string> { Public, Lan });

            Assert.AreEqual(2, ordered.Count);
            Assert.AreEqual(Public, ordered[0]);
        }
    }
}
