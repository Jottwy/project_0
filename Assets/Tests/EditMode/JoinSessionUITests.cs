using BackroomsSurvival.Net;
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
            // El gate del joiner vive ahora en la maquina de estados, que es STATIC y de proceso:
            // sin este reinicio, un test que abre el gate deja el siguiente mintiendo en verde.
            SessionState.ResetForTests();
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

        // ─── El gate de "conectado" (auditoría de conectividad, 2026-08-30) ────────────────
        //
        // El fallo: `IPCClient.IsConnected` significa "Unity habló con SU PROPIO backend por TCP
        // en 127.0.0.1". Para un Host eso ES la sesión; para un Joiner no prueba nada — su backend
        // local acepta ese TCP y sirve un mundo aunque el handshake UDP contra el host no haya
        // salido nunca de la máquina. El panel usaba lo segundo como si fuera lo primero, así que
        // un join a una IP inalcanzable cargaba la escena y metía al jugador en un mundo local en
        // solitario, sin geometría WG3 y sin un solo error. Verificado con el binario real contra
        // 192.0.2.1: 11 handshakes sin respuesta, cero avisos.

        [Test]
        public void HostDoesNotWaitForAHandshakeItNeverSends()
        {
            // Su propio backend es el servidor: exigirle una confirmación de entrada lo dejaría
            // colgado en el panel para siempre. Es la mitad "no rompas localhost" del arreglo.
            Assert.IsTrue(JoinSessionUI.IsSessionEstablished(NetworkInitializer.Role.Host, false));
            Assert.IsTrue(JoinSessionUI.IsSessionEstablished(NetworkInitializer.Role.None, false));
        }

        [Test]
        public void JoinerIsNotConnectedUntilTheHandshakeCompletes()
        {
            Assert.IsFalse(
                JoinSessionUI.IsSessionEstablished(NetworkInitializer.Role.Joiner, false),
                "un joiner sin handshake no puede entrar al mundo: es el fallo entero");
            Assert.IsTrue(
                JoinSessionUI.IsSessionEstablished(NetworkInitializer.Role.Joiner, true),
                "y con el handshake hecho tiene que entrar, o el join no funcionaría nunca");
        }

        [Test]
        public void SessionJoinedOpensTheGate()
        {
            Assert.IsFalse(_ui.JoinerSessionConfirmed, "el gate arranca cerrado");
            _ui.OnSessionEvent(new GameEventMsg { eventType = "session_joined" });
            Assert.IsTrue(_ui.JoinerSessionConfirmed);
        }

        [Test]
        public void AnUnrelatedEventDoesNotOpenTheGate()
        {
            // Control negativo: `player_joined` viaja por el MISMO bus y llega cuando entra
            // cualquiera. Confundirlo con la entrada propia devolvería el fallo entero.
            _ui.OnSessionEvent(new GameEventMsg { eventType = "player_joined" });
            Assert.IsFalse(_ui.JoinerSessionConfirmed);
        }

        [Test]
        public void SessionEndedKeepsTheReasonSoThePanelCanExplainItself()
        {
            // "NO debe quedarse indefinidamente en Connecting... sin explicar por qué": el motivo
            // lo redacta el backend y muere aquí si nadie lo guarda — antes el panel escribía
            // "Disconnected" a secas y el fallo volvía a ser indiagnosticable desde la UI.
            var data = new System.Collections.Generic.Dictionary<string, object>
            {
                { "reason", "sin respuesta de 192.168.1.40:7778 tras 11 intentos" },
            };
            _ui.OnSessionEvent(new GameEventMsg { eventType = "session_ended", data = data });

            StringAssert.Contains("192.168.1.40:7778", _ui.LastSessionEndReason);
        }
    }
}
