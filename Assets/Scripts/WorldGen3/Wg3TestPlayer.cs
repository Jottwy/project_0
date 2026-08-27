using UnityEngine;
using UnityEngine.InputSystem;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// Jugador mínimo para la escena de prueba de WG3. Andar, mirar y chocar; nada más.
    ///
    /// PROPIO Y NO EL DEL JUEGO, a propósito. El controlador real arrastra el stack de
    /// PolymindGames —inventario, salud, viewmodel, mixer de audio, la sesión— y en F0 cualquiera
    /// de esas piezas puede fallar por su cuenta y disfrazarse de fallo de geometría. Aquí, si te
    /// atraviesa una pared, es la pared.
    ///
    /// USA EL INPUT SYSTEM NUEVO porque el proyecto tiene <c>activeInputHandler: 1</c>: la clase
    /// <c>UnityEngine.Input</c> de siempre no lee nada y lanza excepción en cuanto se toca.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class Wg3TestPlayer : MonoBehaviour
    {
        [Header("Movimiento")]
        public float walkSpeed = 3.4f;
        public float sprintSpeed = 6.2f;
        public float gravity = -18f;

        [Header("Mirada")]
        public float lookSensitivity = 0.12f;
        public float pitchLimit = 88f;

        [Tooltip("El mundo del que sale el punto de aparición. Se busca solo si queda vacío.")]
        public Wg3TestWorld world;

        private CharacterController _controller;
        private Transform _eye;
        private float _pitch;
        private float _verticalSpeed;
        private bool _looking = true;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();

            // Escalón por encima de la contrahuella de las escaleras del catálogo (0,18 m) o el
            // jugador se queda encallado en el primer peldaño y parece un fallo de colisión.
            _controller.stepOffset = Mathf.Max(_controller.stepOffset, 0.32f);
            _controller.slopeLimit = 55f;

            _eye = transform.Find("Eye");
            if (_eye == null)
            {
                var eye = new GameObject("Eye");
                eye.transform.SetParent(transform, false);
                eye.transform.localPosition = new Vector3(0f, 0.72f, 0f);
                _eye = eye.transform;
            }
            if (_eye.GetComponent<Camera>() == null) _eye.gameObject.AddComponent<Camera>();

            if (world == null) world = FindFirstObjectByType<Wg3TestWorld>();
        }

        private void Start()
        {
            Respawn();
            SetLooking(true);
        }

        private void OnDisable() => SetLooking(false);

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.escapeKey.wasPressedThisFrame) SetLooking(!_looking);
            if (keyboard.rKey.wasPressedThisFrame && world != null) { world.Reseed(); Respawn(); }
            if (keyboard.tKey.wasPressedThisFrame && world != null) { world.Generate(); Respawn(); }

            Look();
            Move(keyboard);
        }

        private void Look()
        {
            if (!_looking || Mouse.current == null) return;

            Vector2 delta = Mouse.current.delta.ReadValue() * lookSensitivity;
            transform.Rotate(0f, delta.x, 0f, Space.Self);
            _pitch = Mathf.Clamp(_pitch - delta.y, -pitchLimit, pitchLimit);
            _eye.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        private void Move(Keyboard keyboard)
        {
            var input = new Vector2(
                (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f),
                (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f));
            if (input.sqrMagnitude > 1f) input.Normalize();

            float speed = keyboard.leftShiftKey.isPressed ? sprintSpeed : walkSpeed;
            Vector3 planar = (transform.right * input.x + transform.forward * input.y) * speed;

            // Se mantiene una velocidad negativa pequeña en suelo: a cero, el controlador pierde
            // el contacto en cada bajada de escalón y `isGrounded` parpadea.
            if (_controller.isGrounded && _verticalSpeed < 0f) _verticalSpeed = -2f;
            _verticalSpeed += gravity * Time.deltaTime;

            _controller.Move((planar + Vector3.up * _verticalSpeed) * Time.deltaTime);
        }

        /// <summary>Devuelve al jugador a la pieza semilla. Se llama al arrancar y tras cada
        /// regeneración: sin esto, un mundo nuevo te deja dentro de una pared del anterior.</summary>
        public void Respawn()
        {
            Respawn(world != null ? world.SpawnPoint : new Vector3(0f, 1f, 0f));
        }

        /// <summary>Coloca al jugador en un punto concreto. Lo usa el arranque en vivo, donde el
        /// mundo no lo compone esta escena sino que llega por el wire.</summary>
        public void Respawn(Vector3 point)
        {
            // El `CharacterController` IGNORA una asignación de `transform.position` mientras está
            // activo: mueve por `Move` y reimpone su posición interna. Apagarlo es la única forma
            // de teletransportar, y olvidarlo se ve como un teleport que "no hace nada".
            _controller.enabled = false;
            transform.position = point;
            _controller.enabled = true;
            _verticalSpeed = 0f;
        }

        private void SetLooking(bool value)
        {
            _looking = value;
            Cursor.lockState = value ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !value;
        }

        private void OnGUI()
        {
            const int pad = 10;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            style.normal.textColor = Color.white;
            string seed = world != null ? world.worldSeed.ToString() : "—";
            int pieces = world?.World != null ? world.World.placements.Count : 0;
            GUI.Label(new Rect(pad, pad, 640f, 60f),
                $"WASD mover · Shift correr · Esc soltar ratón · R nueva semilla · T regenerar\n" +
                $"semilla {seed} · {pieces} piezas", style);
        }
    }
}
