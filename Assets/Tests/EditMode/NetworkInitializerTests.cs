using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace BackroomsSurvival.Tests
{
    [TestFixture]
    public class NetworkInitializerTests
    {
        private GameObject _go;
        private NetworkInitializer _init;

        /// <summary>
        /// Los tres tests que fuerzan la rama "exe no resuelto" atraviesan un fail-loud
        /// DELIBERADO: <c>LaunchBackendProcess</c> emite dos <c>Debug.LogError</c> antes de
        /// devolver false (NetworkInitializer.cs:197-201, comentario "Fail loudly: never
        /// silently continue with an unverifiable backend"). El Test Framework tumba cualquier
        /// test con un log de Error no declarado, así que estos tres fallaban DENTRO de la
        /// llamada, no en sus asserts — que de hecho se cumplen todos.
        ///
        /// Declararlos aquí, en vez de silenciarlos con ignoreFailingMessages, convierte el
        /// fail-loud en contrato verificado: si alguien baja esos LogError a LogWarning, estos
        /// tests se ponen rojos. Hoy ningún test del repo usa LogAssert (grep = 0 resultados),
        /// así que este modo de fallo estaba sin cubrir en toda la suite.
        /// </summary>
        private static void ExpectBackendNotFoundErrors()
        {
            LogAssert.Expect(LogType.Error,
                "[NetworkInitializer] MPTRACE step=RUBIK event=unity_backend_exe_path path=UNRESOLVED status=fail_loud");
            LogAssert.Expect(LogType.Error,
                "[NetworkInitializer] Backend executable not found. Build or copy backrooms_server.exe.");
        }

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
            ExpectBackendNotFoundErrors();
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
            ExpectBackendNotFoundErrors();
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
            ExpectBackendNotFoundErrors();
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
