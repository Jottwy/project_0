using System;
using System.Collections.Generic;
using BackroomsSurvival.Net;
using BackroomsSurvival.UI;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El ciclo de vida de sesion, de menu a menu y otra vez.
    ///
    /// QUE PRUEBA Y QUE NO. Todo lo de aqui corre sin editor, sin escena y sin backend: prueba
    /// las REGLAS (la maquina de estados, la politica de cursor, los gates de idempotencia y de
    /// generacion). Lo que NO puede probar es el cableado a Unity — que el `Quit to Menu` del
    /// vendor dispare `activeSceneChanged`, que `Process.Exited` llegue, que el backend suelte de
    /// verdad sus sockets. Eso vive en `docs/architecture/SESSION_LIFECYCLE.md` como
    /// procedimiento manual y en `tools/dev/CheckOrphanBackends.ps1` como sonda.
    ///
    /// La leccion de la tanda anterior, aplicada: unos verdes sobre funciones sueltas no dicen
    /// nada del pipeline entero. Por eso los ciclos completos se recorren ENTEROS y repetidos
    /// cinco veces, no por transiciones sueltas.
    /// </summary>
    [TestFixture]
    public class SessionLifecycleTests
    {
        private SessionStateMachine _m;

        [SetUp]
        public void SetUp()
        {
            SessionState.ResetForTests();
            _m = SessionState.Current;
        }

        // ─── Recorridos completos ────────────────────────────────────────────────────────────

        /// Un JOIN entero, hasta dentro del mundo. Es el camino que la UI tiene que poder pintar.
        private void RunJoinToWorld()
        {
            Assert.IsTrue(_m.RequestStart(NetworkInitializer.Role.Joiner), "Join rechazado desde una fase ociosa");
            Assert.IsTrue(_m.NotifyBackendLaunched());
            Assert.AreEqual(SessionPhase.Connecting, _m.Phase);

            // EL FALLO ORIGINAL, en una sola linea: el IPC local arriba NO convierte a un joiner
            // en conectado. Su backend acepta ese TCP aunque el host no exista.
            Assert.IsFalse(_m.NotifyIpcConnected(), "el IPC local no puede establecer la sesion de un joiner");
            Assert.AreEqual(SessionPhase.Connecting, _m.Phase);

            Assert.IsTrue(_m.NotifySessionJoined());
            Assert.AreEqual(SessionPhase.Connected, _m.Phase);

            Assert.IsTrue(_m.NotifyEnteredWorld("STP_Showcase"));
            Assert.AreEqual(SessionPhase.InGame, _m.Phase);
        }

        private void RunHostToWorld()
        {
            Assert.IsTrue(_m.RequestStart(NetworkInitializer.Role.Host));
            Assert.IsTrue(_m.NotifyBackendLaunched());
            // Para un host su propio backend ES el servidor: el IPC local si es la sesion.
            Assert.IsTrue(_m.NotifyIpcConnected());
            Assert.AreEqual(SessionPhase.Connected, _m.Phase);
            Assert.IsTrue(_m.NotifyEnteredWorld("STP_Showcase"));
        }

        private void RunLeave(bool keepReason)
        {
            Assert.IsTrue(_m.RequestLeave("test leave"));
            Assert.AreEqual(SessionPhase.Disconnecting, _m.Phase);
            Assert.IsTrue(_m.NotifyLeaveComplete(keepReason));
        }

        [Test]
        public void JoinDisconnectJoinFiveTimesLeavesNoResidue()
        {
            for (int i = 0; i < 5; i++)
            {
                RunJoinToWorld();
                RunLeave(keepReason: false);

                Assert.AreEqual(SessionPhase.Menu, _m.Phase, $"vuelta {i}: no se volvio al menu");
                Assert.IsTrue(_m.CanStart, $"vuelta {i}: Join no vuelve a estar disponible");
                Assert.IsFalse(_m.JoinerConfirmed,
                    $"vuelta {i}: el gate del joiner sobrevivio a la sesion — la SIGUIENTE entraria " +
                    "al mundo con la confirmacion de la anterior");
                Assert.AreEqual(NetworkInitializer.Role.None, _m.Role, $"vuelta {i}: el rol sobrevivio");
                Assert.AreEqual("", _m.WorldScene, $"vuelta {i}: la escena de la sesion anterior sobrevivio");
                Assert.AreEqual(i + 1, _m.Generation, "cada intento tiene que tener generacion propia");
            }
        }

        [Test]
        public void HostDisconnectHostFiveTimesLeavesNoResidue()
        {
            for (int i = 0; i < 5; i++)
            {
                RunHostToWorld();
                RunLeave(keepReason: false);

                Assert.AreEqual(SessionPhase.Menu, _m.Phase, $"vuelta {i}");
                Assert.IsTrue(_m.CanStart, $"vuelta {i}: Host no vuelve a estar disponible");
                Assert.AreEqual(NetworkInitializer.Role.None, _m.Role, $"vuelta {i}");
            }
        }

        [Test]
        public void JoinGameplayMenuJoinWorksWithoutTouchingAnythingElse()
        {
            // El caso del `Quit to Menu` del vendor: se sale de la escena de juego sin pasar por
            // ningun teardown de red, y el enganche de escena lo convierte en un Leave.
            RunJoinToWorld();
            Assert.AreEqual("STP_Showcase", _m.WorldScene);

            // Lo que hace SessionEndHandler.OnActiveSceneChanged al ver otra escena.
            Assert.IsTrue(_m.RequestLeave("left to menu"));
            Assert.IsTrue(_m.NotifyLeaveComplete(keepReason: false));

            Assert.IsTrue(_m.CanStart, "salir al menu tiene que dejar Join disponible otra vez");
            RunJoinToWorld();
            Assert.AreEqual(SessionPhase.InGame, _m.Phase);
        }

        // ─── Fallos y reintentos ─────────────────────────────────────────────────────────────

        [Test]
        public void JoinTimeoutJoin()
        {
            for (int i = 0; i < 5; i++)
            {
                Assert.IsTrue(_m.RequestStart(NetworkInitializer.Role.Joiner));
                _m.NotifyBackendLaunched();
                Assert.IsTrue(_m.NotifyFailed("sin respuesta de 192.0.2.1:7778"));

                Assert.AreEqual(SessionPhase.Failed, _m.Phase);
                StringAssert.Contains("192.0.2.1:7778", _m.Reason,
                    "el motivo lo redacta el backend y tiene que llegar al panel: sin el vuelve el " +
                    "'Connecting... sin explicar por que'");
                Assert.IsTrue(_m.CanStart, "un timeout tiene que dejar la UI recuperable");
                Assert.IsFalse(_m.JoinerConfirmed);
            }
        }

        [Test]
        public void ReconnectAfterTimeoutSucceeds()
        {
            Assert.IsTrue(_m.RequestStart(NetworkInitializer.Role.Joiner));
            _m.NotifyBackendLaunched();
            _m.NotifyFailed("timeout");

            // Reintentar desde Failed: exactamente el boton [Retry].
            RunJoinToWorld();
            Assert.AreEqual(SessionPhase.InGame, _m.Phase);
            Assert.AreEqual("", _m.Reason, "el veredicto del intento anterior no puede sobrevivir al nuevo");
        }

        [Test]
        public void AJoinThatNeverGotInEndsAsFailedNotAsSessionEnded()
        {
            // EL CAMINO REAL de un timeout de join, que NO pasa por NotifyFailed: el backend agota
            // su CONNECT_TIMEOUT y manda `session_ended`, y eso entra por el teardown normal. Sin
            // distinguir "nunca entro" de "entro y se perdio", una IP mal tecleada acababa
            // diciendo "Session ended" — el mensaje que no explica nada.
            Assert.IsTrue(_m.RequestStart(NetworkInitializer.Role.Joiner));
            _m.NotifyBackendLaunched();
            _m.NotifyIpcConnected(); // el TCP local; el handshake nunca llega

            Assert.IsTrue(_m.RequestLeave("sin respuesta de 192.0.2.1:7778 tras 11 intentos"));
            Assert.IsTrue(_m.NotifyLeaveComplete(keepReason: true));

            Assert.AreEqual(SessionPhase.Failed, _m.Phase, "nunca se establecio: es 'no se pudo conectar'");
            StringAssert.Contains("192.0.2.1:7778", _m.Reason);
            Assert.IsTrue(_m.CanStart);
        }

        [Test]
        public void ASessionThatDidGetInEndsAsDisconnected()
        {
            // Control positivo del test de arriba: el MISMO camino con la sesion establecida
            // tiene que dar el OTRO final, o la distincion no existiria.
            RunJoinToWorld();
            Assert.IsTrue(_m.RequestLeave("host left"));
            Assert.IsTrue(_m.NotifyLeaveComplete(keepReason: true));
            Assert.AreEqual(SessionPhase.Disconnected, _m.Phase);
        }

        [Test]
        public void JoinErrorJoin()
        {
            // Backend que no arranca (exe ausente, puerto imposible): mismo final recuperable.
            Assert.IsTrue(_m.RequestStart(NetworkInitializer.Role.Joiner));
            Assert.IsTrue(_m.NotifyFailed("backend executable not found"));
            Assert.AreEqual(SessionPhase.Failed, _m.Phase);

            Assert.IsTrue(_m.RequestStart(NetworkInitializer.Role.Joiner), "tras un error hay que poder reintentar");
        }

        [Test]
        public void ConnectionLostInGameEndsWithAReasonAndThenJoinWorks()
        {
            RunJoinToWorld();

            // Perdida de conexion en partida: NO es NotifyFailed (esa fase ya paso), es un Leave
            // con motivo, y termina en Disconnected para que el panel lo explique.
            Assert.IsFalse(_m.NotifyFailed("host se fue"),
                "una caida con la sesion en marcha no puede degradarse a 'no llego a conectar'");
            Assert.IsTrue(_m.RequestLeave("host left"));
            Assert.IsTrue(_m.NotifyLeaveComplete(keepReason: true));

            Assert.AreEqual(SessionPhase.Disconnected, _m.Phase);
            Assert.AreEqual("host left", _m.Reason);
            Assert.IsTrue(_m.CanStart);
            RunJoinToWorld();
        }

        // ─── Idempotencia y doble accion ─────────────────────────────────────────────────────

        [Test]
        public void LeaveCalledTwiceIsHarmless()
        {
            RunHostToWorld();

            Assert.IsTrue(_m.RequestLeave("primero"));
            // El segundo evento de fin (paquete de despedida Y timeout de latido) llega SIEMPRE.
            Assert.IsFalse(_m.RequestLeave("segundo"), "el segundo Leave no puede volver a desmontar nada");
            Assert.AreEqual("primero", _m.Reason, "ni sobrescribir el motivo del primero");
            Assert.AreEqual(SessionPhase.Disconnecting, _m.Phase);

            Assert.IsTrue(_m.NotifyLeaveComplete(keepReason: true));
            Assert.IsFalse(_m.NotifyLeaveComplete(keepReason: true), "cerrar dos veces tampoco");
            Assert.AreEqual(SessionPhase.Disconnected, _m.Phase);
        }

        [Test]
        public void LeaveWithNoSessionDoesNothing()
        {
            Assert.AreEqual(SessionPhase.Menu, _m.Phase);
            Assert.IsFalse(_m.RequestLeave("nada que abandonar"));
            Assert.AreEqual(SessionPhase.Menu, _m.Phase);
            Assert.AreEqual("", _m.Reason);
        }

        [Test]
        public void JoinPressedTwiceStartsOneSession()
        {
            Assert.IsTrue(_m.RequestStart(NetworkInitializer.Role.Joiner));
            int generation = _m.Generation;

            Assert.IsFalse(_m.RequestStart(NetworkInitializer.Role.Joiner), "el segundo Join tiene que rechazarse");
            Assert.AreEqual(generation, _m.Generation,
                "un segundo intento aceptado significaria un segundo backend lanzado");

            _m.NotifyBackendLaunched();
            Assert.IsFalse(_m.RequestStart(NetworkInitializer.Role.Joiner), "ni durante Connecting");
            Assert.AreEqual(generation, _m.Generation);
        }

        [Test]
        public void HostDuringJoiningIsRejected()
        {
            Assert.IsTrue(_m.RequestStart(NetworkInitializer.Role.Joiner));
            Assert.IsFalse(_m.RequestStart(NetworkInitializer.Role.Host));
            Assert.AreEqual(NetworkInitializer.Role.Joiner, _m.Role, "el rol no puede cambiar a mitad de intento");
        }

        [Test]
        public void StartDuringTeardownIsRejected()
        {
            RunHostToWorld();
            _m.RequestLeave("saliendo");

            // La ventana entre "empieza el teardown" y "termina": un Join aqui reutilizaria el
            // backend que se esta matando.
            Assert.IsFalse(_m.RequestStart(NetworkInitializer.Role.Joiner));
            Assert.IsFalse(_m.CanStart);
        }

        [Test]
        public void BackToMenuIsOnlyAnAcknowledgement()
        {
            RunHostToWorld();
            // Desde una sesion VIVA no se puede "volver al menu" sin limpiar: si se pudiera, el
            // backend se quedaria corriendo — que es literalmente el fallo del `Quit to Menu`.
            Assert.IsFalse(_m.AcknowledgeAndReturnToMenu());

            RunLeave(keepReason: true);
            Assert.AreEqual(SessionPhase.Disconnected, _m.Phase);
            Assert.IsTrue(_m.AcknowledgeAndReturnToMenu());
            Assert.AreEqual(SessionPhase.Menu, _m.Phase);
            Assert.AreEqual("", _m.Reason);
            Assert.IsFalse(_m.AcknowledgeAndReturnToMenu(), "y es idempotente");
        }

        // ─── El backend anterior no manda en la sesion nueva ─────────────────────────────────

        [Test]
        public void AStaleBackendExitCannotTouchTheNewSession()
        {
            // Sesion 1.
            Assert.IsTrue(_m.RequestStart(NetworkInitializer.Role.Host));
            int firstLaunch = _m.Generation;
            RunLeave(keepReason: false);

            // Sesion 2.
            Assert.IsTrue(_m.RequestStart(NetworkInitializer.Role.Host));
            int secondLaunch = _m.Generation;
            Assert.AreNotEqual(firstLaunch, secondLaunch);

            // El `Process.Exited` del backend de la sesion 1 llega AHORA, en un hilo del pool.
            Assert.IsFalse(NetworkInitializer.ShouldApplyBackendExit(firstLaunch, secondLaunch),
                "la muerte del backend viejo apagaba IsBackendReady y sobrescribia el estado de la sesion nueva");
            Assert.IsTrue(NetworkInitializer.ShouldApplyBackendExit(secondLaunch, secondLaunch),
                "y la del backend en curso si tiene que aplicarse, o una muerte real pasaria desapercibida");
        }

        [Test]
        public void EveryAttemptGetsItsOwnGeneration()
        {
            var seen = new HashSet<int>();
            for (int i = 0; i < 5; i++)
            {
                Assert.IsTrue(_m.RequestStart(NetworkInitializer.Role.Joiner));
                Assert.IsTrue(seen.Add(_m.Generation), "dos intentos con la misma generacion: el token no distingue");
                _m.NotifyBackendLaunched();
                _m.NotifyFailed("timeout");
            }
        }

        // ─── Cursor ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void CursorIsFreeInEveryPhaseThatIsNotTheWorld()
        {
            foreach (SessionPhase phase in Enum.GetValues(typeof(SessionPhase)))
            {
                bool inWorld = phase == SessionPhase.Connected || phase == SessionPhase.InGame;
                Assert.AreEqual(inWorld, SessionCursor.ShouldLock(menuVisible: false, phase: phase),
                    $"fase {phase}: politica de cursor equivocada");
            }
        }

        [Test]
        public void AVisibleMenuAlwaysFreesTheCursor()
        {
            // Da igual la fase: un panel con campos y botones que no se pueden pulsar ES el fallo.
            foreach (SessionPhase phase in Enum.GetValues(typeof(SessionPhase)))
                Assert.IsFalse(SessionCursor.ShouldLock(menuVisible: true, phase: phase), $"fase {phase}");
        }

        [Test]
        public void CursorIsRestoredAfterEveryExit()
        {
            for (int i = 0; i < 5; i++)
            {
                RunJoinToWorld();
                Assert.IsTrue(SessionCursor.ShouldLock(menuVisible: false, phase: _m.Phase),
                    "dentro del mundo y sin panel, el cursor va capturado");

                RunLeave(keepReason: i % 2 == 0);
                SessionCursor.ReleaseToMenu();
                Assert.IsFalse(SessionCursor.LastAppliedLocked, $"vuelta {i}: el cursor no se restauro");
                Assert.IsFalse(SessionCursor.ShouldLock(menuVisible: false, phase: _m.Phase),
                    $"vuelta {i}: la fase de salida sigue pidiendo cursor capturado");

                if (_m.Phase != SessionPhase.Menu) _m.AcknowledgeAndReturnToMenu();
            }
        }

        // ─── La UI no puede mentir ───────────────────────────────────────────────────────────

        [Test]
        public void NoPhaseOutsideTheWorldIsPaintedAsConnected()
        {
            foreach (SessionPhase phase in Enum.GetValues(typeof(SessionPhase)))
            {
                foreach (NetworkInitializer.Role role in Enum.GetValues(typeof(NetworkInitializer.Role)))
                {
                    var panel = JoinSessionUI.PanelStateFor(phase, role);
                    bool inWorld = phase == SessionPhase.Connected || phase == SessionPhase.InGame;
                    Assert.AreEqual(inWorld, panel == JoinSessionUI.PanelState.Connected,
                        $"fase {phase} rol {role} pintada como {panel}");
                }
            }
        }

        [Test]
        public void AJoinerNeverShowsConnectedForHavingIpcAlone()
        {
            Assert.IsTrue(_m.RequestStart(NetworkInitializer.Role.Joiner));
            _m.NotifyBackendLaunched();
            _m.NotifyIpcConnected(); // el TCP local, que un joiner tiene SIEMPRE

            Assert.AreEqual(SessionPhase.Connecting, _m.Phase);
            Assert.AreEqual(JoinSessionUI.PanelState.Joining,
                JoinSessionUI.PanelStateFor(_m.Phase, _m.Role),
                "es el fallo entero: con la IP inalcanzable el panel decia Connected y cargaba la escena");
            Assert.IsFalse(JoinSessionUI.IsSessionEstablished(_m.Role, _m.JoinerConfirmed));
        }

        [Test]
        public void AHostIsConnectedAsSoonAsItsOwnBackendAnswers()
        {
            // La otra mitad del arreglo: no romper localhost. Un host no manda handshake ninguno.
            Assert.IsTrue(_m.RequestStart(NetworkInitializer.Role.Host));
            _m.NotifyBackendLaunched();
            Assert.IsTrue(_m.NotifyIpcConnected());
            Assert.AreEqual(JoinSessionUI.PanelState.Connected, JoinSessionUI.PanelStateFor(_m.Phase, _m.Role));
        }

        // ─── Suscripciones ───────────────────────────────────────────────────────────────────

        [Test]
        public void SubscribingTwiceLeavesOneListener()
        {
            // El cliente IPC sobrevive a la sesion (hilo y singleton siguen vivos para que la
            // siguiente los reutilice), asi que un suscriptor que se re-suscriba sin quitarse
            // primero corria DOS veces por evento el resto de la vida del proceso.
            var list = new List<IPCClient.GameEventHandler>();
            IPCClient.GameEventHandler handler = _ => { };

            Assert.IsTrue(IPCClient.AddUnique(list, handler));
            Assert.IsFalse(IPCClient.AddUnique(list, handler), "la segunda suscripcion tiene que rechazarse");
            Assert.AreEqual(1, list.Count);

            list.Remove(handler);
            Assert.AreEqual(0, list.Count, "y un solo Remove tiene que dejarlo limpio");
        }

        [Test]
        public void DifferentSubscribersAreNotConfusedWithADuplicate()
        {
            // Control negativo: dos objetos distintos suscribiendo el MISMO metodo son delegados
            // distintos y los dos tienen que entrar, o se perderian oyentes legitimos.
            var list = new List<IPCClient.GameEventHandler>();
            var a = new Probe();
            var b = new Probe();

            Assert.IsTrue(IPCClient.AddUnique<IPCClient.GameEventHandler>(list, a.OnEvent));
            Assert.IsTrue(IPCClient.AddUnique<IPCClient.GameEventHandler>(list, b.OnEvent));
            Assert.AreEqual(2, list.Count);
        }

        private sealed class Probe
        {
            public void OnEvent(GameEventMsg ev) { }
        }
    }
}
