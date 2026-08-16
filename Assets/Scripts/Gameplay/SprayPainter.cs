using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Audio;
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

        // Sonido. Se decide por FRAME y no por evento: "está saliendo pintura" es un estado
        // continuo, y mandarlo entero cada frame evita el fallo clásico del loop que se queda
        // encendido porque el evento de apagar se perdió en una rama de salida temprana.
        private SpraySfx _sfx;
        private bool _hissThisFrame;
        private bool _sputterThisFrame;
        private SprayCan _sfxLastCan;

        /// <summary>
        /// Hasta cuándo se considera que sale pintura, para quien lo consulte desde fuera.
        ///
        /// ES UN PESTILLO CON TIEMPO, no el booleano del frame, y la razón es el orden de ejecución:
        /// <c>PlayerPoseTransmitter</c> muestrea a 10 Hz desde SU propio Update, que puede correr
        /// antes que el nuestro. Leyendo el booleano crudo, el chorro que ven los demás parpadearía
        /// según qué componente actualice primero.
        /// </summary>
        private float _sprayingUntil;

        private const float SprayingLatchSeconds = 0.2f;

        // ─── ADR-078: el trazo en vivo hacia los demás ───
        //
        // El `place_id` se acuña al EMPEZAR el gesto y no al soltarlo, que es el cambio que hace
        // posible la fase B: es la etiqueta que empareja los borradores con la pintada definitiva,
        // así que tiene que existir antes de mandar el primer borrador.
        private long _activePlaceId;
        private float _nextDraftAt;
        private int _draftSentPoints;
        private Vector3 _draftAnchor;

        /// <summary>10 Hz, la misma cadencia que la pose. Más no se nota; menos se ve a tirones.</summary>
        private const float DraftIntervalSeconds = 0.1f;

        /// <summary>Tope por paquete (ADR-078 decisión 6): 64 puntos = 256 B de carga útil.</summary>
        private const int MaxDraftPointsPerPacket = 64;

        /// <summary>
        /// ¿Está el jugador local pintando ahora mismo? Solo pintura DE VERDAD: quedarse sin bote y
        /// seguir apretando escupe aire, y eso no pinta nada ni debe dibujar chorro a los demás.
        /// </summary>
        public static bool IsSprayingNow =>
            _instance != null && Time.time <= _instance._sprayingUntil;

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
            // El emisor es un GameObject suelto (no hijo, para que no herede escala del rig): si
            // no se retira aquí, cada recarga de escena deja uno mudo colgando para siempre.
            if (_sfx != null) Destroy(_sfx.gameObject);
        }

        private void Update()
        {
            _hissThisFrame = false;
            _sputterThisFrame = false;

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
                // Sin llamar al sonido: su objetivo se consume cada frame, así que guardar el
                // bote lo apaga por la rampa sin necesidad de un "apágate" explícito.
                _sfxLastCan = null;
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

            ApplySfx();
        }

        /// <summary>
        /// Traslada lo que ha pasado este frame al emisor, y le saca el cascabel al bote la
        /// primera vez que aparece en la mano.
        /// </summary>
        private void ApplySfx()
        {
            if (_sfx == null) _sfx = SpraySfx.Create("SpraySfx_Local", spatial: false);

            // La cámara se re-resuelve: el rig del jugador se reconstruye en runtime y la
            // referencia vieja queda en el null falso de Unity (misma lección que el motor).
            if (_sfx.follow == null)
            {
                var cam = Camera.main;
                if (cam != null) _sfx.follow = cam.transform;
            }

            if (!ReferenceEquals(_can, _sfxLastCan))
            {
                _sfxLastCan = _can;
                _sfx.Shake();
            }

            if (_hissThisFrame)
            {
                _sfx.SetSpraying(true);
                _sprayingUntil = Time.time + SprayingLatchSeconds;
            }
            else if (_sputterThisFrame)
            {
                _sfx.SetSputtering(true);
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
            // Bote vacío pero el jugador sigue apretando: escupe aire. Es la única señal de que
            // se acabó la pintura que llega sin mirar la barra del item.
            if (_can.IsEmpty) { _sputterThisFrame = true; EndStroke(); return; }

            var cam = Camera.main;
            if (cam == null) return;

            if (!TryFindWall(cam.transform.position, cam.transform.forward, out var hit))
            {
                EndStroke();
                return;
            }

            // Hay pared y hay pintura: sale chorro, se mueva la mira o no. Cobrar el siseo por
            // punto añadido lo cortaría cada vez que el jugador se queda quieto apuntando.
            _hissThisFrame = true;

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
                // ADR-078: trazo nuevo ⇒ el borrador vuelve a empezar por el índice 0, que es
                // como el receptor sabe que hay que abrir una polilínea y no continuar la
                // anterior. Sin esto, levantar el dedo y volver a pintar dibujaría en casa del
                // que mira una raya recta entre los dos trazos.
                _draftSentPoints = 0;
                _nextDraftAt = 0f;
                RefreshPreview();
                return;
            }

            if (_gesture.TryGetLast(out var last))
            {
                float meters = Vector3.Distance(last, hit.point);
                if (meters < MinStepMeters) return;
                if (_can.Spend(meters) <= 0f)
                {
                    // Se acabó a mitad de trazo: el siseo de este frame ya no vale, escupitajo.
                    _hissThisFrame = false;
                    _sputterThisFrame = true;
                    EndStroke();
                    return;
                }
            }

            _gesture.Add(hit.point);
            _idleSeconds = 0f;
            RefreshPreview();
            PumpDraft();

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
        /// <summary>
        /// ADR-078 — manda a 10 Hz los puntos NUEVOS del trazo abierto, para que los demás lo
        /// vean crecer en vez de verlo aparecer entero al soltar.
        ///
        /// El ancla se fija con el PRIMER punto del gesto y no se vuelve a mover: es el origen
        /// contra el que se miden los milímetros, y cambiarlo a mitad de trazo desplazaría todo
        /// lo ya dibujado en la pantalla del que mira.
        ///
        /// Best-effort de verdad: si no hay IPC, si no hay bote o si no hay puntos nuevos, no se
        /// manda nada y no pasa nada — al soltar llega la pintada entera igual.
        /// </summary>
        private void PumpDraft()
        {
            if (Time.time < _nextDraftAt) return;
            if (!_gesture.TryGetAnchor(out var anchor)) return;

            var ipc = IPCClient.Instance;
            if (ipc == null || !ipc.IsConnected) return;

            if (_draftSentPoints == 0) _draftAnchor = anchor;

            var bytes = _gesture.OpenStrokeToMillimetres(
                _draftSentPoints, MaxDraftPointsPerPacket, _draftAnchor);
            if (bytes == null) return;

            EnsurePlaceId();
            _nextDraftAt = Time.time + DraftIntervalSeconds;

            ipc.SendSprayDraft(_activePlaceId, _canvasLayer, _draftAnchor, _gesture.Yaw,
                _can != null ? _can.ColorIndex : (byte)0,
                _can != null ? _can.StrokeWidth / 255f : 0.02f,
                (ushort)Mathf.Min(_draftSentPoints, ushort.MaxValue), bytes);

            _draftSentPoints += bytes.Length / 4;
        }

        /// <summary>
        /// Acuña el id de esta pintada si aún no lo tiene. Se llama al mandar el primer borrador
        /// y otra vez al commitear, para que el gesto que nunca mandó borrador (sin red, o tan
        /// corto que no llegó a los 100 ms) siga teniendo su id como antes de ADR-078.
        /// </summary>
        private void EnsurePlaceId()
        {
            if (_activePlaceId != 0) return;
            int selfId = NetworkInitializer.Instance != null
                ? NetworkInitializer.Instance.LastSelectedNetId
                : 0;
            _activePlaceId = ((long)Mathf.Max(1, selfId) * 1000000000L) + _nextPlaceId++;
        }

        private SprayMsg BuildMessage() => _gesture.BuildMessage(
            _can != null ? _can.ColorIndex : (byte)0,
            _can != null ? _can.StrokeWidth : (byte)4,
            _canvasLayer);

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
                // Desde ADR-078 el id puede venir ya acuñado: los borradores lo llevan escrito
                // desde el primer paquete, y la pintada TIENE que traer el mismo o el receptor
                // se quedará con el borrador colgado hasta que caduque, doble trazo incluido.
                EnsurePlaceId();
                long placeId = _activePlaceId;

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
            _activePlaceId = 0;
            _draftSentPoints = 0;
            _nextDraftAt = 0f;
            // La previa NO se retira aquí: se queda hasta que llegue la copia autoritativa, o
            // el trazo parpadearía en el hueco entre soltar y recibir el eco del host.
        }
    }
}
