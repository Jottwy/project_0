using System.Collections.Generic;
using BackroomsSurvival.Net;
using BackroomsSurvival.UI;
using PolymindGames;
using PolymindGames.MovementSystem;
using PolymindGames.WieldableSystem;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-068 S3 — captura: convierte "el jugador arrastra la mira por una pared con el bote en
    /// la mano" en la petición que el host valida.
    ///
    /// Item-agnóstico por diseño: nunca pregunta "¿es el bote de spray?", pregunta si el
    /// wieldable ACTIVO trae un <see cref="SprayCan"/>. Mismo criterio que <c>ReadLightOn</c>
    /// (ADR-042) y que <c>TorchShadowCaster</c> (ADR-065), y por la misma razón — el prefab es
    /// del vendor y no se edita desde dentro.
    ///
    /// Lo que manda es una PETICIÓN. Aquí no se ancla al chunk, no se acuña id y no se decide
    /// nada: el host deriva el chunk, revalida cada tope contra la posición que él conoce del
    /// jugador y numera. Lo que sí se hace en cliente es no dejar pintar lo que se sabe que se
    /// va a rechazar, para que el jugador se entere al apuntar y no después del trazo.
    /// </summary>
    [DisallowMultipleComponent]
    public class SprayPainter : MonoBehaviour
    {
        /// <summary>Cada cuánto se vuelve a mirar qué lleva el jugador en la mano.</summary>
        private const float WieldablePollSeconds = 0.25f;

        /// <summary>
        /// Silencio tras soltar el gatillo que da la pintada por terminada y la manda. Permite
        /// varios trazos en el MISMO lienzo — que es lo que separa un mural de una fila de
        /// pintadas sueltas, cada una gastando su plaza del cap del chunk.
        /// </summary>
        private const float CommitDelaySeconds = 1.2f;

        /// <summary>
        /// Separación mínima entre muestras, en pasos de retícula. Sin esto, quedarse quieto
        /// apretando el gatillo llenaría los 512 puntos con el mismo punto repetido y cerraría la
        /// pintada sin haber dibujado nada.
        /// </summary>
        private const int MinStepGrid = 2;

        private static SprayPainter _instance;

        // Rig del jugador local, re-resuelto en vivo: el rig de STP se reconstruye en runtime y
        // deja la caché en null-de-Unity (lección de PlayerPoseTransmitter).
        private CharacterControllerMotor _motor;
        private ICharacter _character;
        private IWieldablesControllerCC _wieldablesController;
        private SprayCan _can;
        private GameObject _canCachedFor;
        private float _nextPoll;

        // Lienzo en curso. Se fija con el PRIMER impacto y no se mueve mientras dure la pintada:
        // si siguiera al puntero, cada muestra reencuadraría el dibujo y los trazos previos se
        // desplazarían solos.
        private bool _hasCanvas;
        private Vector3 _canvasCentre;
        private float _canvasYaw;
        private float _canvasSize;
        private byte _canvasLayer;

        private readonly List<SprayStrokeMsg> _strokes = new List<SprayStrokeMsg>();
        private readonly List<byte> _points = new List<byte>();
        private int _pointsInSpray;
        private bool _strokeOpen;
        private byte _lastU, _lastV;
        private float _idleSeconds;
        private long _nextPlaceId = 1;

        /// <summary>Diagnóstico y tests: hay una pintada a medias esperando a cerrarse.</summary>
        public bool HasPendingSpray => _hasCanvas && (_strokes.Count > 0 || _points.Count > 0);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;
            var go = new GameObject("SprayPainter");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<SprayPainter>();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void Update()
        {
            if (Time.time >= _nextPoll)
            {
                _nextPoll = Time.time + WieldablePollSeconds;
                _can = ResolveActiveCan();
            }

            // Guardar el bote (o quedarse sin pintura) cierra lo que hubiera a medias en vez de
            // dejarlo colgando hasta la próxima vez que se saque.
            if (_can == null)
            {
                if (HasPendingSpray) Commit();
                Reset();
                return;
            }

            bool holding = TriggerHeld();
            if (holding) Paint();
            else EndStroke();

            if (!holding && HasPendingSpray)
            {
                _idleSeconds += Time.deltaTime;
                if (_idleSeconds >= CommitDelaySeconds) Commit();
            }
        }

        /// <summary>
        /// El <see cref="SprayCan"/> del wieldable ACTIVO, o null.
        ///
        /// El baile con el null es literal de <c>TorchShadowCaster.ResolveEmittingLight</c> y no
        /// es paranoia: un wieldable DESTRUIDO sigue devolviendo una referencia de interfaz
        /// no-nula (el `?.` de C# no ve el null falso de Unity) y tocar su `.gameObject` lanza.
        /// </summary>
        private SprayCan ResolveActiveCan()
        {
            if (_motor == null)
            {
                _motor = LocalPlayerLocator.Find<CharacterControllerMotor>();
                _character = null;
                _wieldablesController = null;
            }
            if (_motor == null) return null;

            if (_character == null) _character = _motor.GetComponentInParent<ICharacter>();
            if (_wieldablesController == null && _character != null)
                _wieldablesController = _character.GetCC<IWieldablesControllerCC>();
            if (_wieldablesController == null) return null;

            var wieldable = _wieldablesController.ActiveWieldable;
            bool destroyed = wieldable is Object uo && uo == null;
            var go = (wieldable == null || destroyed) ? null : wieldable.gameObject;
            if (go == null)
            {
                _canCachedFor = null;
                return null;
            }

            if (!ReferenceEquals(_canCachedFor, go))
            {
                _canCachedFor = go;
                _can = go.GetComponentInChildren<SprayCan>(true);
            }
            return _can;
        }

        /// <summary>
        /// Gatillo. Input System nuevo y no <c>UnityEngine.Input</c>: el proyecto tiene
        /// <c>activeInputHandler: 1</c> y el API viejo LANZA, no devuelve false.
        /// </summary>
        private static bool TriggerHeld()
        {
            if (JoinSessionUI.IsAnyMenuVisible) return false;
            if (Cursor.lockState != CursorLockMode.Locked) return false;
            var mouse = Mouse.current;
            return mouse != null && mouse.leftButton.isPressed;
        }

        private void Paint()
        {
            if (_can.IsEmpty) { EndStroke(); return; }

            var cam = Camera.main;
            if (cam == null) return;

            var ray = new Ray(cam.transform.position, cam.transform.forward);
            if (!Physics.Raycast(ray, out var hit, SprayCanvas.MaxPlaceDistance, ~0,
                    QueryTriggerInteraction.Ignore))
            {
                EndStroke();
                return;
            }

            if (!SprayCanvas.IsPaintableWall(cam.transform.position, hit.point, hit.normal))
            {
                EndStroke();
                return;
            }

            if (!_hasCanvas)
            {
                _canvasCentre = hit.point;
                _canvasYaw = SprayCanvas.YawFromNormal(hit.normal);
                _canvasSize = _can.CanvasMeters;
                _canvasLayer = 0;
                _hasCanvas = true;
            }

            SprayCanvas.WorldToCanvas(hit.point, _canvasCentre, _canvasYaw,
                _canvasSize, _canvasSize, out byte u, out byte v);

            if (!_strokeOpen)
            {
                if (_strokes.Count >= SprayCanvas.MaxStrokes) return;
                if (_pointsInSpray >= SprayCanvas.MaxPoints) return;
                _points.Clear();
                _points.Add(u); _points.Add(v);
                _pointsInSpray++;
                _lastU = u; _lastV = v;
                _strokeOpen = true;
                _idleSeconds = 0f;
                return;
            }

            int du = u - _lastU, dv = v - _lastV;
            if (du * du + dv * dv < MinStepGrid * MinStepGrid) return;

            float meters = SprayCanvas.CanvasStepMeters(_lastU, _lastV, u, v, _canvasSize, _canvasSize);
            if (_can.Spend(meters) <= 0f) { EndStroke(); return; }

            _points.Add(u); _points.Add(v);
            _pointsInSpray++;
            _lastU = u; _lastV = v;
            _idleSeconds = 0f;

            // Al tocar techo se cierra Y se manda: seguir muestreando sin sitio sería pintar en
            // el vacío, y el jugador estaría moviendo el bote sin que salga nada.
            if (_pointsInSpray >= SprayCanvas.MaxPoints)
            {
                EndStroke();
                Commit();
            }
        }

        private void EndStroke()
        {
            if (!_strokeOpen) return;
            _strokeOpen = false;

            if (_points.Count >= 2 && _strokes.Count < SprayCanvas.MaxStrokes)
            {
                _strokes.Add(new SprayStrokeMsg
                {
                    color = _can != null ? _can.ColorIndex : (byte)0,
                    width = _can != null ? _can.StrokeWidth : (byte)4,
                    points = _points.ToArray(),
                });
            }
            _points.Clear();
        }

        /// <summary>
        /// Manda la pintada y vacía el estado. El id se particiona por peer con el mismo esquema
        /// que <c>WorldInteractor.MakeRequestId</c>, y eso NO es cosmético: el host deduplica en
        /// un set global, así que dos joiners empezando su contador en 1 harían que la pintada
        /// del segundo se descartara como duplicada.
        /// </summary>
        public void Commit()
        {
            EndStroke();
            if (_strokes.Count == 0) { Reset(); return; }

            var ipc = IPCClient.Instance;
            if (ipc != null)
            {
                int selfId = NetworkInitializer.Instance != null
                    ? NetworkInitializer.Instance.LastSelectedNetId
                    : 0;
                long placeId = ((long)Mathf.Max(1, selfId) * 1000000000L) + _nextPlaceId++;

                ipc.SendSprayPlace(placeId, _canvasLayer, _canvasCentre, _canvasYaw,
                    _canvasSize, _canvasSize, _strokes);

                Debug.Log($"MPTRACE step=SPRAY event=unity_spray_sent place_id={placeId} " +
                          $"strokes={_strokes.Count} points={_pointsInSpray} " +
                          $"paint_left={(_can != null ? _can.PaintMeters : 0f):F1}");
            }

            Reset();
        }

        private void Reset()
        {
            _strokes.Clear();
            _points.Clear();
            _pointsInSpray = 0;
            _strokeOpen = false;
            _hasCanvas = false;
            _idleSeconds = 0f;
        }
    }
}
