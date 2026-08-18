using System.Collections.Generic;
using BackroomsSurvival.Net;
using BackroomsSurvival.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem; // New Input System (project uses activeInputHandler = 1)
using UnityEngine.UI;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// First-person controller for the local player.
    ///
    /// The Rust backend is authoritative for POSITION: this script sends the
    /// desired world-space movement direction every frame and snaps/lerps the
    /// body to the position the backend reports back (no client-side prediction).
    /// Look (yaw/pitch) is handled locally so the camera stays responsive.
    ///
    /// Drop this on an empty GameObject. It reuses the scene's Main Camera (or
    /// creates one) as a child eye.
    /// </summary>
    public sealed class PlayerController : MonoBehaviour
    {
        [Header("Look")]
        [Tooltip("Degrees of rotation per pixel of mouse movement.")]
        public float lookSensitivity = 0.1f;
        public float maxPitch = 89f;

        [Header("Movement feel")]
        [Tooltip("How quickly the body eases toward the backend's authoritative position.")]
        public float positionSmoothing = 18f;

        private IPCClient _ipc;
        private Transform _camera;
        private float _yaw;
        private float _pitch;
        private bool _hasSpawned;
        private bool _cursorLocked = true;
        private float _nextTransformSentLogTime;
        private readonly List<string> _queuedActions = new List<string>(4);

        private void Awake()
        {
            // Reuse an existing camera (child or scene Main Camera), else create one.
            var childCam = GetComponentInChildren<Camera>();
            if (childCam != null)
            {
                _camera = childCam.transform;
            }
            else if (Camera.main != null)
            {
                _camera = Camera.main.transform;
                _camera.SetParent(transform, false);
                _camera.localPosition = Vector3.zero;
                _camera.localRotation = Quaternion.identity;
            }
            else
            {
                var camGo = new GameObject("PlayerCamera");
                camGo.transform.SetParent(transform, false);
                camGo.AddComponent<Camera>();
                if (FindFirstObjectByType<AudioListener>() == null) camGo.AddComponent<AudioListener>();
                camGo.tag = "MainCamera";
                _camera = camGo.transform;
            }

            _yaw = transform.eulerAngles.y;
        }

        private void Start()
        {
            IPCClient.TryGetInstance(out _ipc);
            SetCursorLocked(!JoinSessionUI.IsAnyMenuVisible);
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;

            if (_ipc == null)
                IPCClient.TryGetInstance(out _ipc);
            if (_ipc == null)
                return;

            if (ShouldIgnoreGameplayInput())
            {
                _queuedActions.Clear();
                ApplyAuthoritativePosition();
                return;
            }

            HandleCursorToggle(keyboard);
            HandleLook(mouse, out Vector2 lookDelta);
            CollectActions(keyboard, mouse);

            // ADR-009: PlayerController is DEPRECATED and is NOT an IPC sender. It used to emit
            // SendInput + SendAction EVERY frame (unthrottled). With the STP rig live this ran as
            // a SECOND sender alongside PlayerPoseTransmitter (~20 Hz) and the per-frame firehose
            // saturated the backend's IPC write loop → "write loop lagged" → backend exit.
            // PlayerPoseTransmitter is now the single authoritative pose sender; this legacy
            // controller transmits nothing. (Local look stays responsive via HandleLook above.)
            _queuedActions.Clear();

            ApplyAuthoritativePosition();
        }

        private void HandleLook(Mouse mouse, out Vector2 lookDelta)
        {
            Vector2 raw = (mouse != null && _cursorLocked) ? mouse.delta.ReadValue() : Vector2.zero;
            lookDelta = raw * lookSensitivity;

            _yaw += lookDelta.x;
            _pitch = Mathf.Clamp(_pitch - lookDelta.y, -maxPitch, maxPitch);

            transform.localRotation = Quaternion.Euler(0f, _yaw, 0f);
            if (_camera != null) _camera.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        private void CollectActions(Keyboard keyboard, Mouse mouse)
        {
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) _queuedActions.Add("attack");
            if (keyboard != null && keyboard.eKey.wasPressedThisFrame) _queuedActions.Add("interact");
            if (keyboard != null && keyboard.gKey.wasPressedThisFrame) _queuedActions.Add("drop");
        }

        private void ApplyAuthoritativePosition()
        {
            var state = _ipc.LatestState;
            if (state == null || state.localPlayer == null) return;

            Vector3 target = state.localPlayer.position;
            if (!_hasSpawned)
            {
                transform.position = target;
                _hasSpawned = true;
            }
            else
            {
                float t = 1f - Mathf.Exp(-positionSmoothing * Time.deltaTime);
                transform.position = Vector3.Lerp(transform.position, target, t);
            }
        }

        private void HandleCursorToggle(Keyboard keyboard)
        {
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                SetCursorLocked(!_cursorLocked);
                _ipc.SendUiEvent(_cursorLocked ? "resume" : "pause");
            }
        }

        private void SetCursorLocked(bool locked)
        {
            _cursorLocked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private bool ShouldIgnoreGameplayInput()
        {
            if (JoinSessionUI.IsAnyMenuVisible)
                return true;

            if (Cursor.lockState != CursorLockMode.Locked)
                return true;

            if (JoinSessionUI.IsUserEditingInput())
                return true;

            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && selected.GetComponent<InputField>() != null;
        }
    }
}
