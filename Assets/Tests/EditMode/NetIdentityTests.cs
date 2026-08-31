using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// **ADR-111 — identidad de red autoritativa.** Continuación de
    /// <see cref="RemotePlayerRosterTests"/>: aquel arregló el SÍNTOMA (Unity volvía a filtrar un
    /// roster que el backend ya había filtrado); esto arregla la CAUSA (Unity no conocía el id
    /// que el host le asignó).
    ///
    /// Los dos números, y por qué no da igual cuál se use:
    ///
    /// <list type="bullet">
    ///   <item><b>propuesto</b> — <c>NET_ID</c>, lo que Unity pidió. Por defecto <b>1</b>, que es
    ///   justo el id del host.</item>
    ///   <item><b>asignado</b> — lo que el host acuñó (<c>allocate_peer_id</c>) y el backend
    ///   adoptó (<c>self.local_id = assigned_id</c>). Es el que viaja en cada cabecera, el que
    ///   acaba en cada <c>owner_id</c> y con el que el host deduplica.</item>
    /// </list>
    ///
    /// Llega a Unity por <c>world_state.local_player_id</c> (wire v53).
    ///
    /// Se prueba <see cref="NetIdentity.Resolve"/>, que es la regla pura, en vez del estado
    /// estático: <c>Proposed</c> lee <c>NetworkInitializer.Instance</c> (un
    /// <c>UnityEngine.Object</c>) y eso obligaría a montar un <c>GameObject</c> por caso. Los
    /// tests que sí necesitan el estático (<see cref="AdoptTests"/>) lo dejan como lo
    /// encontraron.
    /// </summary>
    public sealed class NetIdentityTests
    {
        /// El id por defecto del host: `NetworkInitializer.netId` y el fallback de
        /// `SelectLaunchConfig` para el rol host.
        private const int HostId = 1;

        /// <summary>
        /// **EL TEST QUE REPRODUCE EL FALLO.** Joiner que propuso 1 (el defecto) y al que el host
        /// asignó 7. Todo consumidor tiene que ver 7.
        /// </summary>
        [Test]
        public void TheAssignedIdWinsOverTheStaleProposedOne()
        {
            Assert.AreEqual(
                7,
                NetIdentity.Resolve(assigned: 7, proposed: HostId),
                "con el propuesto (1) el joiner se cree el host: borra al host de su roster, " +
                "choca los prefijos de id de petición con los de otro jugador y compara los " +
                "`owner_id` de las reclamaciones contra una identidad que no es la suya.");
        }

        /// <summary>
        /// Mientras el backend no ha mandado ningún snapshot no hay nada mejor que el propuesto,
        /// y para el host es además el correcto: nadie le asigna nada, la suya es la del bind.
        /// </summary>
        [Test]
        public void TheProposedIdIsTheBridgeUntilTheFirstSnapshot()
        {
            Assert.AreEqual(HostId, NetIdentity.Resolve(assigned: NetIdentity.Unknown, proposed: HostId));
        }

        /// <summary>
        /// Y deja de serlo para siempre en cuanto llega el asignado. Es la asimetría del contrato:
        /// el propuesto NUNCA vuelve a ganar, ni aunque el asignado parezca «raro» (un id alto de
        /// depuración, por ejemplo) — la autoridad es del host, no de la plausibilidad.
        /// </summary>
        [TestCase(2, 1)]
        [TestCase(1, 25052)]
        [TestCase(25052, 1)]
        [TestCase(4876, 4242)]
        [TestCase(65535, 1)]
        public void OnceAssignedTheProposedIdNeverWinsAgain(int assigned, int proposed)
        {
            Assert.AreEqual(assigned, NetIdentity.Resolve(assigned, proposed));
        }

        /// <summary>
        /// Sin sesión no hay identidad, y eso tiene que poder DISTINGUIRSE de «tengo el id 1».
        /// `LocalPeerId()` devuelve 0 (nunca dueño de nada) y los acuñadores de id de petición
        /// caen a su `Mathf.Max(1, …)`.
        /// </summary>
        [Test]
        public void NoSessionMeansNoIdentity()
        {
            Assert.AreEqual(NetIdentity.Unknown, NetIdentity.Resolve(assigned: 0, proposed: 0));
        }

        /// <summary>
        /// **Varios jugadores.** Dos clientes que propusieron LO MISMO (el defecto 1, el caso real
        /// de dos joiners lanzados con el entorno por defecto) quedan separados por el asignado.
        ///
        /// No es cosmético: los ids de petición se acuñan como `id * 1e9 + contador` y el host
        /// deduplica en un set global. Con el mismo prefijo, la construcción, la pintada o el
        /// drop del segundo jugador se descartan **en silencio** como duplicados.
        /// </summary>
        [Test]
        public void TwoClientsThatProposedTheSameIdEndUpWithDifferentIdentities()
        {
            const int SameProposal = HostId;
            int a = NetIdentity.Resolve(assigned: 2, proposed: SameProposal);
            int b = NetIdentity.Resolve(assigned: 3, proposed: SameProposal);

            Assert.AreNotEqual(a, b);
            Assert.AreNotEqual(
                (long)a * 1000000000L, (long)b * 1000000000L,
                "los prefijos de id de petición tienen que caer en espacios distintos");
        }
    }

    /// <summary>
    /// La adopción del id que viene en el snapshot: qué se acepta, qué se rechaza y qué se olvida.
    /// Separado de <see cref="NetIdentityTests"/> porque estos tocan el estático y lo restauran.
    /// </summary>
    public sealed class AdoptTests
    {
        [TearDown]
        public void ForgetAdoptedId() => NetIdentity.ResetForNewConnection();

        [Test]
        public void AdoptingPublishesTheAssignedId()
        {
            Assert.IsTrue(NetIdentity.Adopt(4242));
            Assert.AreEqual(4242, NetIdentity.Assigned);
        }

        /// <summary>Llega 10 veces por segundo: repetirlo no es un cambio.</summary>
        [Test]
        public void AdoptingTheSameIdAgainIsNotAChange()
        {
            NetIdentity.Adopt(4242);
            Assert.IsFalse(NetIdentity.Adopt(4242));
            Assert.AreEqual(4242, NetIdentity.Assigned);
        }

        /// <summary>
        /// Un backend anterior a la v53 no manda el campo, y un campo ausente decodifica a 0.
        /// Adoptar ese 0 como identidad colapsaría el prefijo de id de petición de TODOS los
        /// clientes al mismo espacio (`Mathf.Max(1, 0)` = 1) — que es peor que quedarse con el
        /// propuesto. Lo mismo para lo que no cabe en un `PeerId`.
        /// </summary>
        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(65536)]
        public void GarbageIsNotAnIdentity(int wire)
        {
            NetIdentity.Adopt(4242);
            Assert.IsFalse(NetIdentity.Adopt(wire));
            Assert.AreEqual(4242, NetIdentity.Assigned, "y no borra el bueno que ya había");
        }

        /// <summary>
        /// Sesión nueva, backend nuevo, asignación posiblemente distinta. Conservar la anterior es
        /// el mismo fallo de id obsoleto una partida más tarde.
        /// </summary>
        [Test]
        public void ANewConnectionForgetsTheAssignedId()
        {
            NetIdentity.Adopt(4242);
            NetIdentity.ResetForNewConnection();
            Assert.AreEqual(NetIdentity.Unknown, NetIdentity.Assigned);
            Assert.AreEqual(
                9, NetIdentity.Resolve(NetIdentity.Assigned, proposed: 9),
                "y al olvidarlo se vuelve a caer al propuesto, no a un 0 pegajoso");
        }

        /// <summary>El host se asigna a sí mismo: adoptar el 1 que ya proponía es válido.</summary>
        [Test]
        public void TheHostAdoptsItsOwnId()
        {
            Assert.IsTrue(NetIdentity.Adopt(1));
            Assert.AreEqual(1, NetIdentity.Local);
        }
    }
}
