using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.Gameplay.Player
{
    /// <summary>
    /// FPS controller. Normal mode: WASD + mouse look + Shift sprint + Space jump.
    /// God mode (Tab toggle): fly freely, no gravity, no collision, Shift fast.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerController : MonoBehaviour
    {
        [Header("Movement")]
        public float walkSpeed   = 5f;
        public float sprintMult  = 2.5f;
        public float godSpeed    = 10f;
        public float godFastMult = 5f;
        public float jumpSpeed   = 6f;
        public float gravity     = -20f;

        [Header("Look")]
        public float sensitivity = 2f;
        public float pitchMin    = -85f;
        public float pitchMax    =  85f;

        [Header("References")]
        public Camera playerCamera;
        public Text   godModeLabel;

        private CharacterController _cc;
        private float   _pitch;
        private float   _yaw;
        private float   _velY;
        private bool    _godMode;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;
        }

        private void Update()
        {
            HandleGodToggle();
            HandleLook();
            if (_godMode) MoveGod();
            else          MoveNormal();
        }

        private void HandleGodToggle()
        {
            if (Input.GetKeyDown(KeyCode.Tab))
            {
                _godMode = !_godMode;
                _velY    = 0f;
                if (godModeLabel != null)
                    godModeLabel.enabled = _godMode;
            }
        }

        private void HandleLook()
        {
            float mx = Input.GetAxis("Mouse X") * sensitivity;
            float my = Input.GetAxis("Mouse Y") * sensitivity;
            _yaw   += mx;
            _pitch  = Mathf.Clamp(_pitch - my, pitchMin, pitchMax);

            transform.localRotation = Quaternion.Euler(0f, _yaw, 0f);
            if (playerCamera != null)
                playerCamera.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        private void MoveNormal()
        {
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");
            float spd = Input.GetKey(KeyCode.LeftShift) ? walkSpeed * sprintMult : walkSpeed;

            Vector3 move = (transform.right * h + transform.forward * v).normalized * spd;

            if (_cc.isGrounded)
            {
                _velY = -1f; // keep grounded
                if (Input.GetButtonDown("Jump"))
                    _velY = jumpSpeed;
            }
            _velY += gravity * Time.deltaTime;
            move.y = _velY;

            _cc.Move(move * Time.deltaTime);
        }

        private void MoveGod()
        {
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");
            float u = (Input.GetKey(KeyCode.Space) ? 1f : 0f)
                    - (Input.GetKey(KeyCode.LeftControl) ? 1f : 0f);

            float spd = Input.GetKey(KeyCode.LeftShift) ? godSpeed * godFastMult : godSpeed;
            Vector3 move = (transform.right * h + transform.forward * v
                          + Vector3.up * u).normalized * spd;

            // In god mode bypass CharacterController collision by moving directly.
            transform.position += move * Time.deltaTime;
        }
    }
}
