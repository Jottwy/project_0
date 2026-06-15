using System.Runtime.CompilerServices;
using System.Collections.Generic;
using PolymindGames.SaveSystem;
using PolymindGames.Options;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace PolymindGames
{
    using Debug = UnityEngine.Debug;

    [OptionalCharacterComponent(typeof(IMotorCC))]
    public sealed class CharacterLookHandler : CharacterBehaviour, ILookHandlerCC, ISaveableComponent
    {
        [SerializeField]
        [Tooltip("Optional: Used in lowering/increasing the current sensitivity based on the FOV")]
        private Camera _camera;

        [SerializeField, NotNull]
        [Tooltip("Transform to rotate Up & Down.")]
        private Transform _xTransform;

        [SerializeField, NotNull]
        [Tooltip("Transform to rotate Left & Right.")]
        private Transform _yTransform;

        [SpaceArea]
        [SerializeField, Range(0, 20)]
        private int _smoothSteps = 8;
        
        [SerializeField]
#if UNITY_EDITOR
        [EditorButton(nameof(PingOptions))]
#endif
        [Tooltip("Vertical look limits (in angles).")]
        private Vector2 _lookLimits = new(-60f, 90f);

        private LookHandlerInputDelegate _additiveInput;
        private LookHandlerInputDelegate _input;
        private bool _hasFOVCamera;
        private float _sensitivity;
        private Vector2 _additiveLookInput;
        private Vector2 _viewAngles;
        private Vector2 _lookDelta;
        private Vector2 _lookInput;

        private readonly List<Vector2> _smoothBuffer = new();
        private Vector2 _currentInput;
        private Vector2 _smoothMove;

        private const float SensitivityMod = 0.05f;

        public Vector2 ViewAngles => _viewAngles;
        public Vector2 LookInput => _lookInput;
        public Vector2 LookDelta => _lookDelta;

        /// <summary>
        /// Sets the look input delegate.
        /// </summary>
        /// <param name="input">The look input delegate.</param>
        public void SetLookInput(LookHandlerInputDelegate input)
        {
            _lookInput = Vector2.zero;
            enabled = input != null;
            _input = input;
        }

        /// <summary>
        /// Sets the additive look input delegate.
        /// </summary>
        /// <param name="input">The additive look input delegate.</param>
        public void SetAdditiveLookInput(LookHandlerInputDelegate input)
        {
            if (_additiveInput != null)
                _viewAngles += _additiveLookInput;
            
            _additiveInput = input;
        }

        /// <summary>
        /// Called when the behaviour starts.
        /// </summary>
        /// <param name="character">The character instance.</param>
        protected override void OnBehaviourStart(ICharacter character)
        {
            ValidateTransforms();
            _hasFOVCamera = _camera != null;
        }

        /// <summary>
        /// Called when the behaviour is enabled.
        /// </summary>
        /// <param name="character">The character instance.</param>
        protected override void OnBehaviourEnable(ICharacter character)
        {
            if (character.TryGetCC(out IMotorCC motor))
                motor.Teleported += OnTeleport;
        }

        /// <summary>
        /// Called when the behaviour is disabled.
        /// </summary>
        /// <param name="character">The character instance.</param>
        protected override void OnBehaviourDisable(ICharacter character)
        {
            if (character.TryGetCC(out IMotorCC motor))
                motor.Teleported -= OnTeleport;
        }

        private void Awake() => enabled = false;

        /// <summary>
        /// Resets the view angles when the character is teleported.
        /// </summary>
        private void OnTeleport() => _viewAngles = new Vector2(0f, _yTransform.localEulerAngles.y);

        private void LateUpdate()
        {
            float deltaTime = Time.deltaTime;
            
            _sensitivity = GetTargetSensitivity(_sensitivity, deltaTime);
            _lookInput = GetInput() * (_sensitivity * SensitivityMod);
            Vector2 additiveInput = GetAdditiveInput();

            MoveView(_lookInput, additiveInput);
        }

        /// <summary>
        /// Gets the target sensitivity based on current settings.
        /// </summary>
        /// <param name="currentSens">The current sensitivity.</param>
        /// <param name="deltaTime">The time delta.</param>
        /// <returns>The target sensitivity.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float GetTargetSensitivity(float currentSens, float deltaTime)
        {
            float targetSensitivity = InputOptions.Instance.MouseSensitivity;
            if (_hasFOVCamera)
                targetSensitivity *= _camera.fieldOfView / GraphicsOptions.Instance.FieldOfView;
            return Mathf.Lerp(currentSens, targetSensitivity, deltaTime * 5f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector2 GetAdditiveInput()
        {
            _additiveLookInput = _additiveInput?.Invoke() ?? Vector2.zero;
            return _additiveLookInput;
        }

        private Vector2 GetInput()
        {
            float smoothWeight = InputOptions.Instance.MouseSmoothness;

            if (smoothWeight < 0.01f)
                return _input.Invoke();

            Vector2 input = _input.Invoke();

            _smoothSteps = Mathf.Clamp(_smoothSteps, 1, 20);
            while (_smoothBuffer.Count > _smoothSteps)
                _smoothBuffer.RemoveAt(0);

            _smoothBuffer.Add(input);

            Vector2 smoothedInput = Vector2.zero;
            float totalWeight = 0f;

            // Calculate weights based on exponential decay
            float weight = 1f;
            for (int i = _smoothBuffer.Count - 1; i >= 0; i--)
            {
                smoothedInput += _smoothBuffer[i] * weight;
                totalWeight += weight;
                weight *= smoothWeight;
            }

            // Normalize by the total weight
            if (totalWeight > 0f)
                smoothedInput /= totalWeight;

            return smoothedInput;
        }
        
        private void MoveView(Vector2 lookInput, Vector2 additiveInput)
        {
            Vector2 prevViewAngles = _viewAngles;
            
            // _viewAngles.x += ClampAngle(lookInput.x * (InputOptions.Instance.InvertMouse ? 1f : -1f));
            _viewAngles.x = ClampAngle(_viewAngles.x + (lookInput.x * (InputOptions.Instance.InvertMouse ? 1f : -1f)));
            _viewAngles.y += lookInput.y;
            
            _lookDelta.x = _viewAngles.x - prevViewAngles.x;
            _lookDelta.y = _viewAngles.y - prevViewAngles.y;

            float yRotation = _viewAngles.y + additiveInput.y; 
            _yTransform.localRotation = Quaternion.Euler(0f, yRotation, 0f);
            
            float xRotation = ClampAngle(_viewAngles.x + additiveInput.x);
            _xTransform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
        }

        /// <summary>
        /// Clamps the given angle within the specified limits.
        /// </summary>
        /// <param name="angle">The angle to clamp.</param>
        /// <returns>The clamped angle.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float ClampAngle(float angle) => Mathf.Clamp(angle, _lookLimits.x, _lookLimits.y);

        /// <summary>
        /// Validates the transforms assigned to this handler.
        /// </summary>
        private void ValidateTransforms()
        {
            if (!_xTransform)
                Debug.LogError("Assign the X Transform in the inspector!", gameObject);

            if (!_yTransform)
                Debug.LogError("Assign the Y Transform in the inspector!", gameObject);
        }

        #region Editor
#if UNITY_EDITOR
        [Conditional("UNITY_EDITOR")]
        private void PingOptions()
        {
            var resourceObject = Resources.LoadAll<InputOptions>(string.Empty).FirstOrDefault();
            if (resourceObject == null)
            {
                Debug.LogError($"No {nameof(InputOptions)} found in the resources folder!");
                return;
            }

            UnityEditor.EditorGUIUtility.PingObject(resourceObject);
        }
#endif
        #endregion

        #region Save & Load
        // ADR-009: STP save disabled — Rust is the single source of truth.
        void ISaveableComponent.LoadMembers(object data) { }
        object ISaveableComponent.SaveMembers() => null;
        #endregion
    }
}