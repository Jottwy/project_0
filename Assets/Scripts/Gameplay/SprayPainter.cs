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
        /// Separación mínima entre muestras, EN METROS de pared. Sin esto, quedarse quieto
        /// apretando el gatillo llenaría los 512 puntos con el mismo punto repetido y cerraría la
        /// pintada sin haber dibujado nada. En metros y no en pasos de retícula porque la
        /// retícula ya no existe hasta el final: el gesto se guarda en mundo.
        /// </summary>
        private const float MinStepMeters = 0.006f;

        /// <summary>Refresco de la vista previa. 30 Hz basta para que el trazo se sienta pegado
        /// a la mira, y rasterizar 256×256 en cada frame sería tirar el presupuesto.</summary>
        private const float PreviewInterval = 1f / 30f;

        private static SprayPainter _instance;

        // Rig del jugador local, re-resuelto en vivo: el rig de STP se reconstruye en runtime y
        // deja la caché en null-de-Unity (lección de PlayerPoseTransmitter).
        private CharacterControllerMotor _motor;
        private ICharacter _character;
        private IWieldablesControllerCC _wieldablesController;
        private SprayCan _can;
        private GameObject _canCachedFor;
        private float _nextPoll;

        // El gesto vivo, en coordenadas de MUNDO. El lienzo no se fija al empezar: se ajusta a lo
        // que de verdad se ha pintado, en cada refresco de la previa y otra vez al mandarlo.
        private readonly SprayGesture _gesture = new SprayGesture();
        private byte _canvasLayer;
        private float _idleSeconds;
        private float _nextPreview;
        private long _nextPlaceId = 1;

        /// <summary>Diagnóstico y tests: hay una pintada a medias esperando a cerrarse.</summary>
        public bool HasPendingSpray => !_gesture.IsEmpty;

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

            if (!TryFindWall(cam.transform.position, cam.transform.forward, out var hit))
            {
                EndStroke();
                return;
            }

            _gesture.SetWall(SprayCanvas.YawFromNormal(hit.normal));

            // Si el gesto se saliera del tope de 2 m, se CIERRA la pintada aquí y empieza otra en
            // el punto actual. Antes esto se clampeaba, y clampear es justo lo que apelmazaba el
            // trazo contra un borde invisible en vez de seguir a la mira.
            if (_gesture.WouldExceedCanvas(hit.point))
            {
                Commit();
                _gesture.SetWall(SprayCanvas.YawFromNormal(hit.normal));
            }

            if (!_gesture.StrokeOpen)
            {
                if (_gesture.StrokeCount >= SprayCanvas.MaxStrokes) return;
                if (_gesture.PointCount >= SprayCanvas.MaxPoints) return;
                _gesture.BeginStroke();
                _gesture.Add(hit.point);
                _idleSeconds = 0f;
                RefreshPreview();
                return;
            }

            if (_gesture.TryGetLast(out var last))
            {
                float meters = Vector3.Distance(last, hit.point);
                if (meters < MinStepMeters) return;
                if (_can.Spend(meters) <= 0f) { EndStroke(); return; }
            }

            _gesture.Add(hit.point);
            _idleSeconds = 0f;
            RefreshPreview();

            // Al tocar el tope de puntos se cierra Y se manda: seguir muestreando sin sitio sería
            // pintar en el vacío, con el jugador moviendo el bote y sin que salga nada.
            if (_gesture.PointCount >= SprayCanvas.MaxPoints)
                Commit();
        }

        /// <summary>
        /// Busca la primera superficie PINTABLE del rayo, no el primer impacto a secas.
        ///
        /// Es la diferencia entre poder pintar la parte baja de una pared y no poder. Apuntando
        /// hacia abajo el rayo suele rozar el SUELO antes de tocar el muro, y con un `Raycast`
        /// normal ese roce se lleva el disparo entero: el suelo no es pintable, así que se
        /// descartaba todo y el jugador veía el bote dejar de responder sin motivo aparente.
        /// Mirando más allá del primer impacto, el rodapié y el arranque del muro vuelven a ser
        /// pintables.
        ///
        /// `RaycastNonAlloc` con buffer reutilizado: esto corre en cada frame mientras se pinta,
        /// y `RaycastAll` dejaría un array nuevo por frame.
        /// </summary>
        private bool TryFindWall(Vector3 origin, Vector3 direction, out RaycastHit wall)
        {
            wall = default;
            int count = Physics.RaycastNonAlloc(new Ray(origin, direction), _hits,
                SprayCanvas.MaxPlaceDistance, ~0, QueryTriggerInteraction.Ignore);
            if (count <= 0) return false;

            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                var h = _hits[i];
                if (h.distance >= best) continue;
                if (!SprayCanvas.IsPaintableWall(origin, h.point, h.normal)) continue;
                best = h.distance;
                wall = h;
                found = true;
            }
            return found;
        }

        /// <summary>Buffer de impactos, reutilizado. 8 caben de sobra en un pasillo.</summary>
        private readonly RaycastHit[] _hits = new RaycastHit[8];

        private void EndStroke()
        {
            if (!_gesture.StrokeOpen) return;
            _gesture.EndStroke();
            RefreshPreview();
        }

        /// <summary>
        /// Redibuja el trazo en curso en local, throttleado. Es lo que hace que pintar se sienta
        /// pintar: sin esto no aparece nada hasta que el host devuelve la pintada, más de un
        /// segundo después de soltar, y el jugador está dibujando a ciegas.
        /// </summary>
        private void RefreshPreview(bool force = false)
        {
            if (!force && Time.unscaledTime < _nextPreview) return;
            _nextPreview = Time.unscaledTime + PreviewInterval;

            var renderer = SprayRenderer.Instance;
            if (renderer == null) return;

            var msg = BuildMessage();
            if (msg == null) renderer.ClearPreview();
            else renderer.ShowPreview(msg);
        }

        /// <summary>
        /// El gesto convertido a lo que viaja por el wire, con el lienzo AJUSTADO a lo pintado.
        /// Lo comparten la previa y el envío, así que lo que se ve mientras se pinta es lo mismo
        /// que se manda — si divergieran, el trazo saltaría al soltar.
        /// </summary>
        private SprayMsg BuildMessage()
        {
            if (!_gesture.TryFit(out var centre, out float sizeX, out float sizeY)) return null;

            int total = _gesture.TotalStrokesIncludingOpen;
            var strokes = new List<SprayStrokeMsg>(total);
            for (int i = 0; i < total; i++)
            {
                var points = _gesture.ProjectStroke(i, centre, sizeX, sizeY);
                if (points.Length < 2) continue;
                strokes.Add(new SprayStrokeMsg
                {
                    color = _can != null ? _can.ColorIndex : (byte)0,
                    width = _can != null ? _can.StrokeWidth : (byte)4,
                    points = points,
                });
            }
            if (strokes.Count == 0) return null;

            int cx = Mathf.FloorToInt(centre.x / SprayMsg.ChunkSize);
            int cz = Mathf.FloorToInt(centre.z / SprayMsg.ChunkSize);
            return new SprayMsg
            {
                id = 0,
                cx = cx,
                cz = cz,
                layer = _canvasLayer,
                lx = centre.x - cx * SprayMsg.ChunkSize,
                ly = centre.y,
                lz = centre.z - cz * SprayMsg.ChunkSize,
                yaw = _gesture.Yaw,
                sizeX = sizeX,
                sizeY = sizeY,
                strokes = strokes.ToArray(),
            };
        }

        /// <summary>
        /// Manda la pintada y vacía el estado. El id se particiona por peer con el mismo esquema
        /// que <c>WorldInteractor.MakeRequestId</c>, y eso NO es cosmético: el host deduplica en
        /// un set global, así que dos joiners empezando su contador en 1 harían que la pintada
        /// del segundo se descartara como duplicada.
        /// </summary>
        public void Commit()
        {
            _gesture.EndStroke();
            var msg = BuildMessage();
            if (msg == null) { Reset(); return; }

            var ipc = IPCClient.Instance;
            if (ipc != null)
            {
                int selfId = NetworkInitializer.Instance != null
                    ? NetworkInitializer.Instance.LastSelectedNetId
                    : 0;
                long placeId = ((long)Mathf.Max(1, selfId) * 1000000000L) + _nextPlaceId++;

                ipc.SendSprayPlace(placeId, msg.layer, msg.WorldPos, msg.yaw,
                    msg.sizeX, msg.sizeY, msg.strokes);

                Debug.Log($"MPTRACE step=SPRAY event=unity_spray_sent place_id={placeId} " +
                          $"strokes={msg.strokes.Length} points={_gesture.PointCount} " +
                          $"canvas={msg.sizeX:F2}x{msg.sizeY:F2}m " +
                          $"paint_left={(_can != null ? _can.PaintMeters : 0f):F1}");
            }

            Reset();
        }

        private void Reset()
        {
            _gesture.Clear();
            _idleSeconds = 0f;
            // La previa NO se retira aquí: se queda hasta que llegue la copia autoritativa, o
            // el trazo parpadearía en el hueco entre soltar y recibir el eco del host.
        }
    }
}
