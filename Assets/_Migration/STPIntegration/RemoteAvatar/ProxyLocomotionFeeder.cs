using BackroomsSurvival.Gameplay;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// Drives the locomotion BlendTree of a remote-player avatar (proxy) by writing the
    /// vendor <c>MovementSpeed</c> float every frame, derived from the proxy's own
    /// network-driven motion — NOT from a packet field and NOT from STP's native motor.
    ///
    /// Why this exists: the proxy body (MTP_PlayerViewer variant) is a pure visual viewer
    /// with no CharacterControllerMotor / PlayerMovementController, and NOTHING in the
    /// project writes the controller's <c>MovementSpeed</c> parameter. Left alone the float
    /// stays at 0 and the avatar is stuck in Idle. RemotePlayerManager interpolates the
    /// avatar's Transform toward each world_state sample (exp-smoothing lerp), so reading
    /// <see cref="Transform.position"/> per frame yields a clean planar velocity to map onto
    /// the BlendTree's vendor "tier" scale (Idle=0, Walk=1, Run=3 — not m/s, not normalized).
    ///
    /// TELEPORT GUARD (critical): chunk displacement and respawns move the avatar many metres
    /// in a single frame. A raw delta then would read as a huge "run" spike. Any single-frame
    /// XZ jump beyond <see cref="_teleportDistance"/> is treated as a teleport: velocity is not
    /// computed, the tier resets to 0, and the previous position is re-baselined.
    ///
    /// DIRECCIÓN (fase 1 del sistema 3P, 2026-09-03): además del escalar de siempre, escribe el par
    /// <c>MoveX</c>/<c>MoveY</c> — la MISMA velocidad, pero expresada en el espacio local del avatar
    /// (X = su derecha, Y = su frente) y con el módulo en la escala de tiers. Eso es lo que separa
    /// "va a 2 m/s" de "va a 2 m/s HACIA ATRÁS", que es la información que al proxy le faltaba desde
    /// ADR-013: un peer que retrocede o que se desplaza de lado caminaba de frente.
    ///
    /// El escalar <c>MovementSpeed</c> se sigue escribiendo con la MISMA cuenta y el MISMO suavizado
    /// de antes, bit a bit: lo leen <c>ProxyRevealHook</c> (que lo reenvía al cuerpo real del
    /// robapieles) y cualquier controller que aún no se haya re-horneado. Los dos canales conviven;
    /// ninguno depende del otro.
    ///
    /// LOS UMBRALES NO SE TOCAN. `_walkSpeed`/`_runSpeed` (1,5 / 4,5) están por debajo de las
    /// velocidades reales del motor (2,8 / 5,5 en FPS_Player.prefab) porque el proxy no mide al
    /// jugador: mide su propia posición interpolada, y el lerp de <c>RemotePlayerManager</c>
    /// (positionSmoothing 22) recorta los picos. Son valores calibrados a ojo en Play y validados;
    /// esta fase añade un eje, no re-calibra el que ya funciona.
    ///
    /// El vector se suaviza con su PROPIO SmoothDamp (mismo `_smoothTime`), no derivándolo del
    /// escalar: así un cambio de dirección cruza el cero de forma continua — que es justo lo que se
    /// ve cuando alguien frena para retroceder — en vez de saltar de +frente a −frente en un frame.
    ///
    /// Lives entirely under _Migration/ and depends only on UnityEngine + the Animator; the
    /// vendor prefab/controller are untouched. Attach to the avatar root (where the Animator
    /// is). Fully removable: delete the file and the proxy falls back to a static Idle pose.
    /// </summary>
    public sealed class ProxyLocomotionFeeder : MonoBehaviour
    {
        [Header("Animator")]
        [Tooltip("Animator that owns the MovementSpeed BlendTree. Auto-filled from this GameObject if left empty.")]
        [SerializeField] private Animator _animator;

        [Header("Speed → tier mapping (m/s)")]
        [Tooltip("Planar speed below this is treated as standing still (tier 0 / Idle).")]
        [SerializeField, Min(0f)] private float _deadzoneSpeed = 0.1f;
        [Tooltip("Planar speed mapped to tier 1 (Walk). deadzone..walk → 0..1.")]
        [SerializeField, Min(0f)] private float _walkSpeed = 1.5f;
        [Tooltip("Planar speed mapped to tier 3 (Run). walk..run → 1..3; above this clamps to 3.")]
        [SerializeField, Min(0f)] private float _runSpeed = 4.5f;

        [Header("Smoothing")]
        [Tooltip("SmoothDamp time applied to the tier before writing it, to avoid walk↔run flicker at the edges.")]
        [SerializeField, Min(0f)] private float _smoothTime = 0.12f;

        [Header("Teleport guard")]
        [Tooltip("Single-frame XZ jump (m) above which the move is treated as a teleport " +
                 "(chunk displacement / respawn): velocity is not computed and the tier resets to 0. " +
                 "Absolute metres is dt-robust: even running at runSpeed on a low framerate stays well below this.")]
        [SerializeField, Min(0f)] private float _teleportDistance = 2.0f;

        private const string MovementSpeedParam = "MovementSpeed";
        private const string MoveXParam = "MoveX";
        private const string MoveYParam = "MoveY";

        private int _hashMovementSpeed;
        private int _hashMoveX;
        private int _hashMoveY;
        // Los ejes direccionales solo existen en el controller re-horneado por
        // ProxyAnimatorControllerBuilder. Sondeamos UNA vez, igual que ProxyCrouchHook con
        // "Crouched": escribir un parámetro que el controller no declara es un warning por llamada,
        // y a 60 fps por peer eso es el patrón que ya inundó un Editor.log en este proyecto.
        private bool _hasDirectionParams;

        private Vector3 _prevPos;
        private bool _hasPrevPos;
        private float _smoothedTier;
        private float _smoothVelocity;
        private Vector2 _smoothedMove;
        private Vector2 _smoothedMoveVelocity;

        private void Awake()
        {
            _hashMovementSpeed = Animator.StringToHash(MovementSpeedParam);
            _hashMoveX = Animator.StringToHash(MoveXParam);
            _hashMoveY = Animator.StringToHash(MoveYParam);
            if (_animator == null)
                _animator = GetComponent<Animator>();
        }

        // Re-baseline on enable so pool reuse (RemotePlayerManager toggles SetActive) and the
        // initial spawn snap never produce a velocity spike: first LateUpdate just captures pos.
        // La sonda de parámetros va AQUÍ y no en Awake: ProxyControllerBinder ata el controller en
        // su propio Awake (ejecución -100, o sea antes que el nuestro), pero un proxy sacado del pool
        // puede haber cambiado de prefab entre ocupantes — re-sondear por spawn cuesta un bucle sobre
        // cinco parámetros y quita una dependencia de orden que nadie va a recordar.
        private void OnEnable()
        {
            _hasDirectionParams = HasDirectionParams();
            _hasPrevPos = false;
            _smoothedTier = 0f;
            _smoothVelocity = 0f;
            _smoothedMove = Vector2.zero;
            _smoothedMoveVelocity = Vector2.zero;
            WriteTier(0f);
            WriteMove(Vector2.zero);
        }

        private void LateUpdate()
        {
            if (_animator == null)
                return;

            Vector3 pos = transform.position;

            if (!_hasPrevPos)
            {
                _prevPos = pos;
                _hasPrevPos = true;
                WriteTier(0f);
                WriteMove(Vector2.zero);
                return;
            }

            Vector3 delta = pos - _prevPos;
            var planarDelta = new Vector3(delta.x, 0f, delta.z);
            float planarDist = planarDelta.magnitude;

            // Teleport: skip this frame's velocity, drop to Idle, re-baseline.
            if (planarDist > _teleportDistance)
            {
                _prevPos = pos;
                _smoothedTier = 0f;
                _smoothVelocity = 0f;
                _smoothedMove = Vector2.zero;
                _smoothedMoveVelocity = Vector2.zero;
                WriteTier(0f);
                WriteMove(Vector2.zero);
                return;
            }

            _prevPos = pos;

            float dt = Time.deltaTime;
            float speed = dt > 0f ? planarDist / dt : 0f;
            float targetTier = ProxyLocomotionMath.SpeedToTier(speed, _deadzoneSpeed, _walkSpeed, _runSpeed);

            _smoothedTier = Mathf.SmoothDamp(_smoothedTier, targetTier, ref _smoothVelocity, _smoothTime);
            WriteTier(_smoothedTier);

            if (!_hasDirectionParams)
                return;

            // A espacio LOCAL con la rotación de ESTE frame: RemotePlayerManager escribe el yaw en
            // Update y nosotros corremos en LateUpdate, así que la referencia ya está puesta al día.
            // Es deliberado que la dirección sea relativa a hacia dónde MIRA ahora: si el peer gira
            // sobre el sitio mientras camina, lo que cambia es su strafe, y eso es exactamente lo que
            // el árbol tiene que ver.
            Vector3 worldVelocity = dt > 0f ? planarDelta / dt : Vector3.zero;
            Vector3 localVelocity = transform.InverseTransformDirection(worldVelocity);
            Vector2 targetMove = ProxyLocomotionMath.VelocityToTiers(
                localVelocity, _deadzoneSpeed, _walkSpeed, _runSpeed);

            _smoothedMove = Vector2.SmoothDamp(
                _smoothedMove, targetMove, ref _smoothedMoveVelocity, _smoothTime);
            WriteMove(_smoothedMove);
        }

        private void WriteTier(float value) => _animator.SetFloat(_hashMovementSpeed, value);

        private void WriteMove(Vector2 move)
        {
            if (!_hasDirectionParams || _animator == null)
                return;

            _animator.SetFloat(_hashMoveX, move.x);
            _animator.SetFloat(_hashMoveY, move.y);
        }

        // True only if the controller actually declares BOTH direction axes. Same probe idiom as
        // ProxyCrouchHook.HasCrouchedParam: a proxy still running a pre-directional controller (an
        // un-rebaked prefab) keeps working on the scalar alone instead of spamming warnings.
        private bool HasDirectionParams()
        {
            if (_animator == null || _animator.runtimeAnimatorController == null)
                return false;

            bool hasX = false, hasY = false;
            foreach (var p in _animator.parameters)
            {
                if (p.type != AnimatorControllerParameterType.Float)
                    continue;
                if (p.name == MoveXParam) hasX = true;
                else if (p.name == MoveYParam) hasY = true;
            }
            return hasX && hasY;
        }

#if UNITY_EDITOR
        private void Reset() => _animator = GetComponent<Animator>();
#endif
    }
}
