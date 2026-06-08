using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    [TestFixture]
    public class NetworkInitializerTests
    {
        private GameObject _go;
        private NetworkInitializer _init;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TestNetInit");
            _init = _go.AddComponent<NetworkInitializer>();
        }

        [TearDown]
        public void TearDown()
        {
            _init.Shutdown();
            Object.DestroyImmediate(_go);
        }

        [Test]
        public void InitialRoleIsNone()
        {
            Assert.AreEqual(NetworkInitializer.Role.None, _init.CurrentRole);
            Assert.IsFalse(_init.IsBackendReady);
        }

        [Test]
        public void StartAsHostSetsRole()
        {
            _init.backendPath = "nonexistent/path.exe";
            _init.fallbackBackendPath = "also/nonexistent.exe";
            _init.executableName = "nonexistent_server_xyz.exe";
            _init.StartAsHost("TestPlayer");

            Assert.AreEqual(NetworkInitializer.Role.Host, _init.CurrentRole);
            Assert.IsTrue(_init.StatusMessage.Contains("Error"));
        }

        [Test]
        public void StartAsJoinerSetsRole()
        {
            _init.backendPath = "nonexistent/path.exe";
            _init.fallbackBackendPath = "also/nonexistent.exe";
            _init.executableName = "nonexistent_server_xyz.exe";
            _init.StartAsJoiner("192.168.1.1", 7778, "Joiner");

            Assert.AreEqual(NetworkInitializer.Role.Joiner, _init.CurrentRole);
            Assert.IsTrue(_init.StatusMessage.Contains("Error"));
        }

        [Test]
        public void ShutdownResetsState()
        {
            _init.backendPath = "nonexistent/path.exe";
            _init.fallbackBackendPath = "also/nonexistent.exe";
            _init.executableName = "nonexistent_server_xyz.exe";
            _init.StartAsHost("Test");
            _init.Shutdown();

            Assert.AreEqual(NetworkInitializer.Role.None, _init.CurrentRole);
            Assert.IsFalse(_init.IsBackendReady);
        }

        [Test]
        public void HostLaunchesBackendWithValidPath()
        {
            // Uses the real backend path — verifies process launch works
            // when the exe exists. Skip if not built.
            string relPath = "backend/target/release/backrooms_server.exe";
            string fullPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "..", relPath));

            if (!System.IO.File.Exists(fullPath))
            {
                relPath = "backend/target/debug/backrooms_server.exe";
                fullPath = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Application.dataPath, "..", relPath));
            }

            if (!System.IO.File.Exists(fullPath))
            {
                Assert.Ignore("Backend exe not built; skipping launch test");
                return;
            }

            _init.startupTimeout = 1f;
            _init.StartAsHost("TestPlayer");

            Assert.AreEqual(NetworkInitializer.Role.Host, _init.CurrentRole);
            Assert.IsFalse(_init.StatusMessage.Contains("Error"),
                $"Expected no error but got: {_init.StatusMessage}");

            _init.Shutdown();
        }
    }
}
