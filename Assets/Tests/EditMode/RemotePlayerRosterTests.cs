using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// **A — «el Host es invisible para los joiners».** El defecto NO está en la red.
    ///
    /// Cadena de identidad, verificada en el código:
    ///
    /// 1. Unity **propone** un id por `NET_ID` y lo guarda en
    ///    `NetworkInitializer.LastSelectedNetId` (`NetworkInitializer.cs:989`);
    /// 2. el host **asigna** el id de verdad (`handlers.rs:1221 allocate_peer_id`) y el backend del
    ///    joiner lo adopta: `self.local_id = assigned_id` (`handlers.rs:1071`);
    /// 3. **nada se lo cuenta a Unity.** `WorldState` (`ipc/mod.rs:570`) no tiene ningún campo con
    ///    el id local: sólo `local_player` y `remote_players`;
    /// 4. el backend construye `remote_players` recorriendo `net.peers` (`game_loop.rs:7185`), y
    ///    **un nodo nunca se registra a sí mismo como peer** — `allocate_peer_id` evita
    ///    `self.local_id` y el manejador de `PeerList` hace `if info.id == self.local_id continue`.
    ///    O sea: **la lista que llega a Unity YA excluye al local**.
    ///
    /// Con eso, el filtro extra de Unity no puede aportar nada correcto y sí puede quitar: filtra
    /// contra un id **propuesto**, no contra el **asignado**. Cuando los dos difieren y el
    /// propuesto coincide con el de un peer real, ese peer **desaparece en silencio**. Y el valor
    /// por defecto de `NetworkInitializer.netId` es **1**, que es justo el id del host: un joiner
    /// que arranque con `NET_ID=1` en el entorno borra al host de su mundo y sigue viendo a los
    /// demás joiners. Ése es el síntoma exacto que se reportó.
    /// </summary>
    public sealed class RemotePlayerRosterTests
    {
        /// El id del host es siempre 1 (`NetworkInitializer.netId` por defecto, y el fallback de
        /// `SelectLaunchConfig` para el rol host).
        private const int HostId = 1;

        /// <summary>
        /// **EL TEST QUE REPRODUCE EL FALLO.** Un joiner cuyo `LastSelectedNetId` quedó en 1 —el
        /// valor por defecto, o un `NET_ID=1` heredado del entorno— borra al host de su mundo.
        /// El backend ya lo había excluido de sí mismo y se lo estaba mandando bien.
        /// </summary>
        [Test]
        public void TheHostIsRenderedEvenIfOurStaleSelfIdCollidesWithIts()
        {
            Assert.IsTrue(
                RemotePlayerManager.ShouldTrackRemote(HostId, unitySelfId: HostId),
                "El backend ya excluye al jugador local de `remote_players`; si Unity vuelve a " +
                "filtrar por un id que NO es el asignado, borra a un peer real. Con NET_ID=1 ese " +
                "peer es el host, y el síntoma es «el host es invisible para los joiners».");
        }

        /// La lista viene ya filtrada, así que **todo lo que llega se pinta**. No hay ningún caso
        /// en el que Unity deba descartar una entrada por identidad: si el backend la mandó, es
        /// remota.
        [TestCase(1, 1)]
        [TestCase(2, 2)]
        [TestCase(25052, 25052)]
        [TestCase(1, 25052)]
        [TestCase(25052, 1)]
        [TestCase(3, 0)]
        public void EverythingTheBackendSendsGetsAProxy(int remoteId, int unitySelfId)
        {
            Assert.IsTrue(RemotePlayerManager.ShouldTrackRemote(remoteId, unitySelfId));
        }

        /// La asimetría que hacía el fallo difícil de leer: con el id propuesto distinto del
        /// asignado, los OTROS joiners se veían perfectamente y sólo faltaba el host. No era
        /// «la red va mal», era «este id concreto se cae».
        [Test]
        public void OtherJoinersWereNeverAffectedAndThatIsWhyItLookedLikeANetworkBug()
        {
            const int staleSelfId = HostId;
            const int anotherJoiner = 25052;

            Assert.IsTrue(RemotePlayerManager.ShouldTrackRemote(anotherJoiner, staleSelfId),
                "otro joiner nunca colisionaba con el id 1, así que se veía");
            Assert.IsTrue(RemotePlayerManager.ShouldTrackRemote(HostId, staleSelfId),
                "el host sí colisionaba — y ésa era toda la diferencia");
        }
    }
}
