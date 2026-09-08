using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Medical;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// La venda: estado médico por zona corporal, y el bit con el que ese estado llega a los demás.
    ///
    /// Lo que se prueba aquí es la MÁQUINA, no el visual: qué brazo se hiere con qué golpe, qué
    /// consume una venda y qué no, y que los dos bits nuevos siguen siendo los que se acordaron.
    /// El visual (una banda colgada de un hueso) se ve o no se ve en una captura; esto es lo que
    /// se puede romper en silencio.
    /// </summary>
    [TestFixture]
    public class BandageMedicalStateTests
    {
        private GameObject _playerObject;
        private Transform _player;
        private PlayerMedicalState _state;

        [SetUp]
        public void SetUp()
        {
            _playerObject = new GameObject("player");
            _player = _playerObject.transform;
            // Instancia propia y NO `PlayerMedicalState.Local`: el singleton lo comparten todos los
            // tests de la corrida y un residuo de uno se leería como un bug del siguiente.
            _state = new PlayerMedicalState();
        }

        [TearDown]
        public void TearDown()
        {
            if (_playerObject != null)
                Object.DestroyImmediate(_playerObject);
        }

        [Test]
        public void ABodyStartsWhole()
        {
            Assert.AreEqual(BodyPartCondition.Healthy, _state.ConditionOf(BodyPartSide.Left));
            Assert.AreEqual(BodyPartCondition.Healthy, _state.ConditionOf(BodyPartSide.Right));
            Assert.IsFalse(_state.HasTreatableWound);
        }

        /// <summary>
        /// Un rasguño no abre una herida. Sin este corte, rozar una pared dejaría los dos brazos
        /// vendables y la venda perdería todo su significado.
        /// </summary>
        [Test]
        public void ScratchesDoNotWound()
        {
            var side = _state.ReportDamage(PlayerMedicalState.MinWoundDamage - 0.01f,
                _player.position + Vector3.right, Vector3.zero, _player);

            Assert.IsNull(side);
            Assert.IsFalse(_state.HasTreatableWound);
        }

        [Test]
        public void AHitOnTheRightWoundsTheRightArm()
        {
            var side = _state.ReportDamage(20f, _player.position + _player.right * 0.4f, Vector3.zero, _player);

            Assert.AreEqual(BodyPartSide.Right, side);
            Assert.IsTrue(_state.IsWounded(BodyPartSide.Right));
            Assert.IsFalse(_state.IsWounded(BodyPartSide.Left));
        }

        /// <summary>
        /// El lado sale de los ejes DEL JUGADOR, no de los del mundo. Girado 180°, el mismo punto de
        /// mundo cae en el brazo contrario — y ése es justo el fallo que un test con el jugador en la
        /// identidad no vería nunca.
        /// </summary>
        [Test]
        public void TheSideIsReadInThePlayersOwnAxes()
        {
            _player.rotation = Quaternion.Euler(0f, 180f, 0f);

            var side = _state.ReportDamage(20f, _player.position + Vector3.right * 0.4f, Vector3.zero, _player);

            Assert.AreEqual(BodyPartSide.Left, side);
        }

        /// <summary>
        /// Sin punto de impacto queda la fuerza, y la fuerza va AL REVÉS: el empuje sale del
        /// atacante y te aparta, así que un golpe que te lanza a tu derecha entró por tu izquierda.
        /// </summary>
        [Test]
        public void ThePushComesFromTheOppositeSide()
        {
            var side = _state.ReportDamage(20f, Vector3.zero, _player.right * 5f, _player);

            Assert.AreEqual(BodyPartSide.Left, side);
        }

        /// <summary>
        /// Caídas, hambre y veneno no traen ni punto ni fuerza. El reparto no puede reventar ni
        /// cebarse siempre con el mismo brazo: reparte antes de repetir.
        /// </summary>
        [Test]
        public void DamageWithoutADirectionSpreadsAcrossBothArms()
        {
            var first = _state.ReportDamage(20f, Vector3.zero, Vector3.zero, null);
            var second = _state.ReportDamage(20f, Vector3.zero, Vector3.zero, null);

            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.AreNotEqual(first, second, "los dos golpes ciegos cayeron en el mismo brazo");
            Assert.IsTrue(_state.IsWounded(BodyPartSide.Left));
            Assert.IsTrue(_state.IsWounded(BodyPartSide.Right));
        }

        [Test]
        public void BandagingClosesTheWound()
        {
            _state.ReportDamage(20f, _player.position + _player.right * 0.4f, Vector3.zero, _player);

            Assert.IsTrue(_state.ApplyBandage(BodyPartSide.Right));
            Assert.AreEqual(BodyPartCondition.Bandaged, _state.ConditionOf(BodyPartSide.Right));
            Assert.IsFalse(_state.HasTreatableWound);
        }

        /// <summary>
        /// Ni un brazo sano ni uno ya vendado consumen venda. Es el gate del que cuelga que la
        /// acción no gaste material por nada — el wieldable no decide esto, lo pregunta.
        /// </summary>
        [Test]
        public void AHealthyOrAlreadyBandagedArmRefusesTheBandage()
        {
            Assert.IsFalse(_state.ApplyBandage(BodyPartSide.Left), "un brazo sano no gasta venda");

            _state.ReportDamage(20f, _player.position - _player.right * 0.4f, Vector3.zero, _player);
            Assert.IsTrue(_state.ApplyBandage(BodyPartSide.Left));
            Assert.IsFalse(_state.ApplyBandage(BodyPartSide.Left), "una venda no se pone dos veces");
        }

        /// <summary>
        /// La venda se PIERDE con el siguiente golpe en esa zona. Es lo que impide que el visual sea
        /// decorativo: si no se pudiera perder, daría igual llevarla.
        /// </summary>
        [Test]
        public void ANewHitTearsTheBandageOff()
        {
            _state.ReportDamage(20f, _player.position + _player.right * 0.4f, Vector3.zero, _player);
            _state.ApplyBandage(BodyPartSide.Right);

            _state.ReportDamage(20f, _player.position + _player.right * 0.4f, Vector3.zero, _player);

            Assert.AreEqual(BodyPartCondition.Wounded, _state.ConditionOf(BodyPartSide.Right));
        }

        /// <summary>La herida que ofrece es la ÚLTIMA: vendarse trata lo que acaba de doler.</summary>
        [Test]
        public void TheOfferedWoundIsTheMostRecentOne()
        {
            _state.ReportDamage(20f, _player.position - _player.right * 0.4f, Vector3.zero, _player);
            _state.ReportDamage(20f, _player.position + _player.right * 0.4f, Vector3.zero, _player);

            Assert.IsTrue(_state.TryGetWoundedSide(out var side));
            Assert.AreEqual(BodyPartSide.Right, side);
        }

        [Test]
        public void WithoutAWoundThereIsNothingToTreat()
        {
            Assert.IsFalse(_state.TryGetWoundedSide(out _));
        }

        /// <summary>
        /// Morir limpia el cuerpo, y lo ANUNCIA: los visuales se apagan porque les llega el cambio,
        /// no porque alguien vaya a buscarlos.
        /// </summary>
        [Test]
        public void DeathClearsTheBodyAndAnnouncesIt()
        {
            _state.ReportDamage(20f, _player.position + _player.right * 0.4f, Vector3.zero, _player);
            _state.ApplyBandage(BodyPartSide.Right);

            var announced = new List<(BodyPartSide, BodyPartCondition)>();
            _state.Changed += (side, condition) => announced.Add((side, condition));

            _state.ResetAll();

            Assert.AreEqual(BodyPartCondition.Healthy, _state.ConditionOf(BodyPartSide.Right));
            CollectionAssert.Contains(announced, (BodyPartSide.Right, BodyPartCondition.Healthy));
        }

        /// <summary>Una zona que ya estaba sana no anuncia nada: repintar por nada es la forma de
        /// que un hook barato deje de serlo.</summary>
        [Test]
        public void ResettingAWholeBodyAnnouncesNothing()
        {
            int changes = 0;
            _state.Changed += (_, _) => changes++;

            _state.ResetAll();

            Assert.AreEqual(0, changes);
        }

        [Test]
        public void TheBandageBitsAreTheOnesAgreed()
        {
            Assert.AreEqual(1 << 7, RemoteButtons.BandagedArmLeft,
                "si esto cambia, cambia el significado de un bit que ya viaja entre versiones");
            Assert.AreEqual(1 << 8, RemoteButtons.BandagedArmRight);
        }

        /// <summary>
        /// Los dos brazos son independientes, a diferencia de los bits de inclinación: se puede
        /// llevar venda en los dos a la vez y el lector de uno no puede confundirse con el otro.
        /// </summary>
        [Test]
        public void BothArmsCanBeBandagedAtOnce()
        {
            int buttons = RemoteButtons.BandagedArmLeft | RemoteButtons.BandagedArmRight;

            Assert.IsTrue(RemoteButtons.Has(buttons, RemoteButtons.BandagedArmLeft));
            Assert.IsTrue(RemoteButtons.Has(buttons, RemoteButtons.BandagedArmRight));
            Assert.IsFalse(RemoteButtons.Has(buttons, RemoteButtons.Cranking));
            Assert.IsFalse(RemoteButtons.Has(buttons, RemoteButtons.Seated));
        }
    }
}
