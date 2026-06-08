using BackroomsSurvival.UI;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    [TestFixture]
    public class JoinSessionUITests
    {
        private GameObject _uiGo;
        private JoinSessionUI _ui;

        [SetUp]
        public void SetUp()
        {
            _uiGo = new GameObject("TestJoinUI");
            _ui = _uiGo.AddComponent<JoinSessionUI>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_uiGo);
        }

        [Test]
        public void InitialStateIsIdle()
        {
            Assert.AreEqual(JoinSessionUI.PanelState.Idle, _ui.State);
        }

        [Test]
        public void DefaultFieldValues()
        {
            Assert.AreEqual("127.0.0.1", _ui.ServerIP);
            Assert.AreEqual("7778", _ui.Port);
            Assert.AreEqual("Player", _ui.PlayerName);
        }
    }
}
