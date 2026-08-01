using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace BackroomsSurvival.Tests
{
    [TestFixture]
    public class RemotePlayerManagerTests
    {
        private GameObject _managerGo;
        private RemotePlayerManager _manager;

        [SetUp]
        public void SetUp()
        {
            _managerGo = new GameObject("TestManager");
            _manager = _managerGo.AddComponent<RemotePlayerManager>();

            // El despawn tiene un margen DELIBERADO de 3 s (el default de producción, que no se
            // toca). Los tests de leave/rejoin comprueban la transición, no el temporizador, así
            // que aquí se pone a 0 para no dormir 3 s reales por test.
            _manager.missingRemoteGraceSeconds = 0f;
        }

        /// <summary>
        /// Crear un proxy pasa por ConfigureNameText, que escribe <c>TMP_Text.outlineWidth</c>;
        /// el setter de TMP toca <c>Renderer.material</c> y Unity, EN MODO EDITOR, emite por eso
        /// un log NATIVO de tipo Error ("Instantiating material due to calling renderer.material
        /// during edit mode..."). El Test Framework tumba cualquier test con un Error no
        /// esperado, así que los 6 tests que crean proxy morían ahí — y el único que pasaba era
        /// justamente el que NO crea proxy. En play mode el mensaje no se emite y TMP cachea la
        /// instancia: cero impacto de juego. No sirve LogAssert.Expect porque lo emite el
        /// runtime nativo, no nuestro código.
        ///
        /// TIENE QUE LLAMARSE DENTRO DEL CUERPO DEL TEST, no desde [SetUp]. El LogScope que
        /// juzga el test lo crea UnityLogCheckDelegatingCommand por DENTRO del comando de
        /// SetUp/TearDown (ver el orden de anidamiento en cualquier traza de UTF), así que nace
        /// después de [SetUp] y con el flag ya reseteado. Ponerlo en [SetUp] no tiene efecto —
        /// verificado: los 6 seguían rojos.
        /// </summary>
        private static void IgnoreEditModeMaterialLog()
        {
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            Object.DestroyImmediate(_managerGo);
        }

        [Test]
        public void InstantiatesRemotePlayerOnJoin()
        {
            IgnoreEditModeMaterialLog();
            var players = new List<RemotePlayerMsg>
            {
                new RemotePlayerMsg { id = 1, name = "Alice", position = Vector3.zero, rotation = 0f, animation = "idle" }
            };

            _manager.UpdateFromWorldState(players);

            Assert.AreEqual(1, _manager.ActiveCount);
            Assert.IsTrue(_manager.ActivePlayers.ContainsKey(1));
            // FormatNameTag adjunta "\nID {id}" a propósito (el id en pantalla es diagnóstico de
            // multijugador). El test esperaba solo el nombre y contradecía el contrato.
            Assert.AreEqual("Alice\nID 1", _manager.ActivePlayers[1].nameTag.text);
        }

        [Test]
        public void DestroysRemotePlayerOnLeave()
        {
            IgnoreEditModeMaterialLog();
            var players = new List<RemotePlayerMsg>
            {
                new RemotePlayerMsg { id = 1, name = "Alice", position = Vector3.zero }
            };
            _manager.UpdateFromWorldState(players);
            Assert.AreEqual(1, _manager.ActiveCount);

            _manager.UpdateFromWorldState(new List<RemotePlayerMsg>());

            Assert.AreEqual(0, _manager.ActiveCount);
            Assert.AreEqual(1, _manager.PoolCount);
        }

        [Test]
        public void ReusesPooledPlayerOnRejoin()
        {
            IgnoreEditModeMaterialLog();
            var players = new List<RemotePlayerMsg>
            {
                new RemotePlayerMsg { id = 1, name = "Alice" }
            };
            _manager.UpdateFromWorldState(players);
            _manager.UpdateFromWorldState(new List<RemotePlayerMsg>());
            Assert.AreEqual(1, _manager.PoolCount);

            var newPlayers = new List<RemotePlayerMsg>
            {
                new RemotePlayerMsg { id = 2, name = "Bob" }
            };
            _manager.UpdateFromWorldState(newPlayers);

            Assert.AreEqual(1, _manager.ActiveCount);
            Assert.AreEqual(0, _manager.PoolCount);
            Assert.AreEqual("Bob\nID 2", _manager.ActivePlayers[2].nameTag.text);
        }

        [Test]
        public void UpdatesTargetPositionForInterpolation()
        {
            IgnoreEditModeMaterialLog();
            var target = new Vector3(10f, 0f, 20f);
            var players = new List<RemotePlayerMsg>
            {
                new RemotePlayerMsg { id = 1, name = "Alice", position = target, rotation = 90f, animation = "walk" }
            };

            _manager.UpdateFromWorldState(players);

            var view = _manager.ActivePlayers[1];
            // El backend reporta el PIVOTE del jugador (pies + PlayerBaseY); el root del proxy
            // tiene el pivote en los PIES. UpdateFromWorldState resta PlayerBaseY para que los
            // pies caigan en el suelo renderizado — simétrico con PlayerPoseTransmitter, que lo
            // suma al emitir. El test esperaba la pose sin convertir y contradecía la convención;
            // ahora la fija.
            var expected = target - new Vector3(0f, GridConstants.PlayerBaseY, 0f);
            Assert.AreEqual(expected, view.targetPosition);
            Assert.AreEqual(90f, view.targetRotation);
            Assert.AreEqual("walk", view.animationState);
        }

        [Test]
        public void HandlesMultiplePlayersSimultaneously()
        {
            IgnoreEditModeMaterialLog();
            var players = new List<RemotePlayerMsg>
            {
                new RemotePlayerMsg { id = 1, name = "Alice" },
                new RemotePlayerMsg { id = 2, name = "Bob" },
                new RemotePlayerMsg { id = 3, name = "Charlie" },
            };

            _manager.UpdateFromWorldState(players);

            Assert.AreEqual(3, _manager.ActiveCount);
        }

        [Test]
        public void HandlesNullRemotePlayersGracefully()
        {
            _manager.UpdateFromWorldState(null);
            Assert.AreEqual(0, _manager.ActiveCount);
        }

        [Test]
        public void SyncsAnimationState()
        {
            IgnoreEditModeMaterialLog();
            var players = new List<RemotePlayerMsg>
            {
                new RemotePlayerMsg { id = 1, name = "Alice", animation = "run" }
            };
            _manager.UpdateFromWorldState(players);
            Assert.AreEqual("run", _manager.ActivePlayers[1].animationState);

            players[0].animation = "attack";
            _manager.UpdateFromWorldState(players);
            Assert.AreEqual("attack", _manager.ActivePlayers[1].animationState);
        }
    }
}
