using PolymindGames.MovementSystem;
using UnityEngine.InputSystem;
using PolymindGames.Options;
using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    [AddComponentMenu("Input/Movement Input")]
    public class FPSMovementInput : PlayerInputBehaviour, IMovementInputProvider
    {
        public Vector2 RawMovement => _processedMovementValue;
        public Vector3 ProcessedMovement => Vector3.ClampMagnitude(_rootTransform.TransformVector(new Vector3(_processedMovementValue.x, 0f, _processedMovementValue.y)), 1f);

        public bool IsRunning => _run; 
        public bool IsCrouching => _crouch;
        public bool IsJumping => _jump;

        [SerializeField, Title("Actions")]
        private InputActionReference _moveInput;

        [SerializeField]
        private InputActionReference _jumpInput;
        
        [SerializeField]
        private InputActionReference _runInput;

        [SerializeField]
        private InputActionReference _crouchInput;

        [SerializeField, Range(0f, 1f), Title("Settings")]
        private float _jumpReleaseDelay = 0.05f;

        private Transform _rootTransform;
        private Vector2 _processedMovementValue;
        private float _releaseJumpBtnTime;
        private bool _crouch;
        private bool _jump;
        private bool _run;


        #region Initialization
        protected override void Awake()
        {
            base.Awake();
            _rootTransform = transform.root;
        }

        protected override void OnBehaviourEnable(ICharacter character)
        {
            ResetInputStates();
            _moveInput.EnableAction();
            _crouchInput.EnableAction();
            _runInput.EnableAction();
            _jumpInput.EnableAction();
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            ResetInputStates();
            _moveInput.DisableAction();
            _crouchInput.DisableAction();
            _runInput.DisableAction();
            _jumpInput.DisableAction();
        }
        #endregion

        #region Input Handling
        public void MarkCrouchInputUsed() => _crouch = false;
        public void MarkRunInputUsed() => _run = false;
        public void MarkJumpInputUsed() => _jump = false;
        
        private void ResetInputStates() 
        {
            _run = false;
            _jump = false;
            _releaseJumpBtnTime = 0f;
            _processedMovementValue = Vector2.zero;
        }
        
        private void Update()
        {
            // Handle movement input.
            _processedMovementValue = _moveInput.action.ReadValue<Vector2>();

            // Handle run input.
            if (InputOptions.Instance.RunToggle)
            {
                if (_runInput.action.triggered)
                    _run = !_run;
            }
            else
            {
                _run = _runInput.action.ReadValue<float>() > 0.1f;
            }

            // Handle crouch input.
            if (InputOptions.Instance.CrouchToogle)
            {
                if (_crouchInput.action.triggered)
                    _crouch = !_crouch;
            }
            else
            {
                _crouch = _crouchInput.action.ReadValue<float>() > 0.1f;
            }

            // Handle jump input.
            if (Time.time > _releaseJumpBtnTime || !_jump)
            {
                bool isJumping = _jumpInput.action.triggered;
                _jump = isJumping;

                if (isJumping)
                    _releaseJumpBtnTime = Time.time + _jumpReleaseDelay;
            }
        }
        #endregion
    }
}