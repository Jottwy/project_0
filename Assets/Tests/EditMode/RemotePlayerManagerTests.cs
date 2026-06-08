using System.Collections.Generic;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

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
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_managerGo);
        }

        [Test]
        public void InstantiatesRemotePlayerOnJoin()
        {
            var players = new List<RemotePlayerMsg>
            {
                new RemotePlayerMsg { id = 1, name = "Alice", position = Vector3.zero, rotation = 0f, animation = "idle" }
            };

            _manager.UpdateFromWorldState(players);

            Assert.AreEqual(1, _manager.ActiveCount);
            Assert.IsTrue(_manager.ActivePlayers.ContainsKey(1));
            Assert.AreEqual("Alice", _manager.ActivePlayers[1].nameTag.text);
        }

        [Test]
        public void DestroysRemotePlayerOnLeave()
        {
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
            Assert.AreEqual("Bob", _manager.ActivePlayers[2].nameTag.text);
        }

        [Test]
        public void UpdatesTargetPositionForInterpolation()
        {
            var target = new Vector3(10f, 0f, 20f);
            var players = new List<RemotePlayerMsg>
            {
                new RemotePlayerMsg { id = 1, name = "Alice", position = target, rotation = 90f, animation = "walk" }
            };

            _manager.UpdateFromWorldState(players);

            var view = _manager.ActivePlayers[1];
            Assert.AreEqual(target, view.targetPosition);
            Assert.AreEqual(90f, view.targetRotation);
            Assert.AreEqual("walk", view.animationState);
        }

        [Test]
        public void HandlesMultiplePlayersSimultaneously()
        {
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
