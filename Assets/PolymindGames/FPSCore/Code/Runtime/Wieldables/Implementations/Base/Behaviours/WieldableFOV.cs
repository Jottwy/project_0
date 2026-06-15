using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Controls the field of view (FOV) for both the camera and the view model of a wieldable object.
    /// </summary>
    [AddComponentMenu("Polymind Games/Wieldables/Behaviours/Wieldable FOV")]
    public sealed class WieldableFOV : MonoBehaviour
    {
        [SerializeField, Range(0.1f, 1f), Delayed]
        [Tooltip("Default camera field of view scaling factor.")]
        private float _cameraFOV = 1f;

        [SerializeField, Range(0f, 100f), Delayed]
        [Tooltip("Default view model field of view in degrees.")]
        private float _viewModelFOV = 55f;

        [SerializeField, Range(0.1f, 1f), Delayed]
        [Tooltip("Default scaling factor for the view model size.")]
        private float _viewModelSize = 0.5f;

        private const float DefaultEaseDuration = 0.5f;
        private IFOVHandlerCC _fovHandler;

        /// <summary>
        /// Gets the current FOV of the camera.
        /// </summary>
        public float CameraFOV => _fovHandler.CameraFOV;
        
        /// <summary>
        /// Gets the FOV of the view model.
        /// </summary>
        public float ViewModelFOV => _fovHandler.ViewModelFOV;
        
        /// <summary>
        /// Gets the size of the view model.
        /// </summary>
        public float ViewModelSize => _fovHandler.ViewModelSize;

        /// <summary>
        /// Sets the size of the view model.
        /// </summary>
        /// <param name="sizeMod">The modifier to apply to the view model size.</param>
        public void SetViewModelSize(float sizeMod) => _fovHandler.SetViewModelSize(sizeMod);
        
        /// <summary>
        /// Sets the FOV of the view model with a modifier.
        /// </summary>
        /// <param name="fovMod">The modifier to apply to the FOV.</param>
        public void SetViewModelFOV(float fovMod) => SetViewModelFOV(fovMod, DefaultEaseDuration);
        
        /// <summary>
        /// Sets the FOV of the view model with a modifier over a specific duration.
        /// </summary>
        /// <param name="fovMod">The modifier to apply to the FOV.</param>
        /// <param name="duration">The duration over which to change the FOV.</param>
        /// <param name="delay">Optional delay before applying the FOV change.</param>
        public void SetViewModelFOV(float fovMod, float duration, float delay = 0f) => _fovHandler.SetViewModelFOV(_viewModelFOV * fovMod, duration, delay);

        /// <summary>
        /// Sets the FOV of the camera with a modifier.
        /// </summary>
        /// <param name="fovMod">The modifier to apply to the FOV.</param>
        public void SetCameraFOV(float fovMod) => SetCameraFOV(fovMod, DefaultEaseDuration);
        
        /// <summary>
        /// Sets the FOV of the camera with a modifier over a specific duration.
        /// </summary>
        /// <param name="fovMod">The modifier to apply to the FOV.</param>
        /// <param name="duration">The duration over which to change the FOV.</param>
        /// <param name="delay">Optional delay before applying the FOV change.</param>
        public void SetCameraFOV(float fovMod, float duration, float delay = 0f) => _fovHandler.SetCameraFOV(_cameraFOV * fovMod, duration, delay);
        
        public void SetDefaultCameraFOV(float delay = 0f) => _fovHandler.SetCameraFOV(1f, DefaultEaseDuration, delay);
        
        private void Awake()
        {
            var wieldable = GetComponentInParent<IWieldable>();

            if (wieldable.Character != null)
                _fovHandler = wieldable.Character.GetCC<IFOVHandlerCC>();
            else
                Debug.LogError("This behaviour requires a wieldable with a parent character.");
        }

        private void OnEnable()
        {
            if (_fovHandler != null)
            {
                SetViewModelFOV(1f, 0f);
                SetViewModelSize(_viewModelSize);
                SetCameraFOV(1f);
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_fovHandler != null)
                SetViewModelFOV(1f);
        }
#endif
    }
}