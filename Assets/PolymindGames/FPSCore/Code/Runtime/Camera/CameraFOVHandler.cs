using PolymindGames.ProceduralMotion;
using PolymindGames.Options;
using System.Linq;
using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Handles the World & View Model FOV of a character's camera.
    /// </summary>
    [RequireCharacterComponent(typeof(IMotorCC))]
    public sealed class CameraFOVHandler : CharacterBehaviour, IFOVHandlerCC
    {
        [SerializeField, Range(30f, 90f), Title("Field Of View (View Model)")]
        private float _baseViewModelFOV = 60f;

        [SerializeField]
        private EaseType _viewModelEaseType = EaseType.QuadInOut;

        [SerializeField]
        private string _viewModelFOVEnabledProperty = "_FOVEnabled";

        [SerializeField]
        private string _viewModelFOVProperty = "_FOV";

        [SerializeField, NotNull, Title("Field Of View (Camera)")]
        private Camera _camera;

        [SerializeField]
        private EaseType _cameraEaseType = EaseType.QuadInOut;

        [SerializeField, Range(0.1f, 5f)]
        private float _airborneFOVMod = 1.05f;

        [SerializeField]
        private AnimationCurve _speedFOVMultiplier = new(new Keyframe(0f, 1f), new Keyframe(1f, 1f));

        [SerializeField]
#if UNITY_EDITOR
        [EditorButton(nameof(PingOptions))]
#endif
        private AnimationCurve _heightFOVMultiplier = new(new Keyframe(0f, 1f), new Keyframe(1f, 1f));

        private Tween<float> _viewModelFOVTween;
        private Tween<float> _cameraFOVTween;
        private float _cameraFOVTweenMod = 1f;
        private float _cameraFOVMod = 1f;
        private int _viewModelFOVId;
        private IMotorCC _motor;

        public float CameraFOV => _camera.fieldOfView;
        public float ViewModelFOV => _viewModelFOVTween.GetCurrentValue();
        public float ViewModelSize => transform.localScale.x;

        public void SetViewModelSize(float size) => transform.localScale = Vector3.one * size;

        public void SetCameraFOV(float fovMultiplier, float duration = 0.3f, float delay = 0f)
        {
            _cameraFOVTweenMod = fovMultiplier;
            float targetFOV = GraphicsOptions.Instance.FieldOfView * fovMultiplier;
            _cameraFOVTween.SetEndValue(targetFOV)
                .SetDuration(duration)
                .SetDelay(delay)
                .Restart();
        }

        public void SetViewModelFOV(float fov, float duration = 0.01f, float delay = 0f)
        {
            _viewModelFOVTween.SetEndValue(fov)
                .SetDuration(duration)
                .SetDelay(delay)
                .Restart();
        }

        private void Awake()
        {
            var option = GraphicsOptions.Instance.FieldOfView;
            option.Changed += OnFOVSettingChanged;
            
            // Camera FOV initialization
            {
                float cameraFOV = option.Value;
                _camera.fieldOfView = cameraFOV;
                _cameraFOVTween = cameraFOV.Tween(cameraFOV, 0f, null)
                    .SetEasing(_cameraEaseType)
                    .SetUnscaledTime(true)
                    .AutoRelease(false)
                    .Stop();
            }

            // View model FOV initialization
            {
                _viewModelFOVId = Shader.PropertyToID(_viewModelFOVProperty);
                Shader.SetGlobalFloat(_viewModelFOVEnabledProperty, 1f);

                _viewModelFOVTween = _baseViewModelFOV.Tween(_baseViewModelFOV, 0f, null)
                    .SetEasing(_viewModelEaseType)
                    .SetUnscaledTime(true)
                    .AutoRelease(false)
                    .Stop();
            }
        }

        protected override void OnDestroy()
        {
            _cameraFOVTween.Release();
            _viewModelFOVTween.Release();
            
            Shader.SetGlobalFloat(_viewModelFOVEnabledProperty, 0f);
            GraphicsOptions.Instance.FieldOfView.Changed -= OnFOVSettingChanged;
        }

        protected override void OnBehaviourStart(ICharacter character)
        {
            _motor = character.GetCC<IMotorCC>();
        }

        private void OnFOVSettingChanged(float fov)
        {
            float targetFOV = fov * _cameraFOVTweenMod;
            _cameraFOVTween.SetEndValue(targetFOV)
                .SetDuration(1f)
                .Restart();
        }

        private void Update()
        {
            _cameraFOVMod = Mathf.Lerp(_cameraFOVMod, GetCameraFOVMod(), Time.deltaTime * 3f);
            _camera.fieldOfView = _cameraFOVTween.GetCurrentValue() * _cameraFOVMod;

            Shader.SetGlobalFloat(_viewModelFOVId, _viewModelFOVTween.GetCurrentValue());
        }

        private float GetCameraFOVMod()
        {
            float multiplier = 1f;

            var velocity = _motor.Velocity;
            var horizontalVel = new Vector2(velocity.x, velocity.z);
            multiplier *= _speedFOVMultiplier.Evaluate(horizontalVel.magnitude);
            multiplier *= _heightFOVMultiplier.Evaluate(_motor.Height);

            if (!_motor.IsGrounded)
                multiplier *= _airborneFOVMod;

            return multiplier;
        }

        #region Editor
#if UNITY_EDITOR
        private void PingOptions()
        {
            var resourceObject = Resources.LoadAll<GraphicsOptions>(string.Empty).FirstOrDefault();
            if (resourceObject == null)
            {
                Debug.LogError($"No {nameof(GraphicsOptions)} found in the resources folder!");
                return;
            }

            UnityEditor.EditorGUIUtility.PingObject(resourceObject);
        }
        
        private void OnValidate()
        {
            if (!Application.isPlaying || !enabled)
                return;

            _viewModelFOVTween?.SetEasing(_viewModelEaseType).SetStartValue(_baseViewModelFOV);
            _viewModelFOVId = Shader.PropertyToID(_viewModelFOVProperty);
            Shader.SetGlobalFloat(_viewModelFOVEnabledProperty, 1f);
        }
#endif
        #endregion
    }
}