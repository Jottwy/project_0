using UnityEngine;

namespace PolymindGames.Options
{
    [CreateAssetMenu(menuName = CreateMenuPath + "Graphics Options", fileName = nameof(GraphicsOptions))]
    public sealed partial class GraphicsOptions : UserOptions<GraphicsOptions>
    {
        [SerializeField, Disable]
        private Option<Vector2Int> _resolution = new();

        [SerializeField, Disable]
        private Option<int> _frameRateCap = new();

        [SerializeField, Disable]
        private Option<int> _quality = new();

        [SerializeField, Disable]
        private Option<EquatableEnum<FullScreenMode>> _fullscreenMode = new();

        [SerializeField]
        private Option<int> _vSyncMode = new(1);

        [SerializeField]
        private Option<float> _fieldOfView = new();

        public const int MaxFramerate = 360;
        public const float MaxFOV = 100f;
        public const float MinFOV = 50f;
        private const float DefaultFOV = 75f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init() => EnsureInstanceInitialized();

        public Option<EquatableEnum<FullScreenMode>> FullscreenMode => _fullscreenMode;
        public Option<Vector2Int> Resolution => _resolution;
        public Option<int> FrameRateCap => _frameRateCap;
        public Option<float> FieldOfView => _fieldOfView;
        public Option<int> VSyncMode => _vSyncMode;
        public Option<int> Quality => _quality;

        /// <inheritdoc/>
        protected override void Apply()
        {
            QualitySettings.SetQualityLevel(_quality.Value, true);
            Screen.SetResolution(_resolution.Value.x, _resolution.Value.y, _fullscreenMode.Value);
            QualitySettings.vSyncCount = _frameRateCap.Value > 0 ? 0 : _vSyncMode.Value;
            Application.targetFrameRate = _frameRateCap.Value;
        }

        /// <inheritdoc/>
        protected override void Reset()
        {
            var defaultResolution = GetMaxResolution();
            _resolution.SetValue(new Vector2Int(defaultResolution.width, defaultResolution.height));
            _quality.SetValue(GetMaxQualityLevel());
            _vSyncMode.SetValue(1);
            _frameRateCap.SetValue(-1);
            _fullscreenMode.SetValue(FullScreenMode.ExclusiveFullScreen);
            _fieldOfView.SetValue(DefaultFOV);
        }

        /// <summary>
        /// Gets the maximum screen resolution supported by the current display.
        /// </summary>
        /// <returns>A <see cref="Resolution"/> object representing the maximum supported resolution.</returns>
        private static Resolution GetMaxResolution()
        {
            var resolutions = Screen.resolutions;
            if (resolutions == null || resolutions.Length == 0)
            {
                Debug.LogWarning("No supported resolutions found.");
                return default(Resolution);
            }

            Resolution maxResolution = resolutions[0];
            foreach (Resolution resolution in resolutions)
            {
                if (resolution.width * resolution.height > maxResolution.width * maxResolution.height ||
                    resolution.width * resolution.height == maxResolution.width * maxResolution.height && resolution.refreshRateRatio.value > maxResolution.refreshRateRatio.value)
                {
                    maxResolution = resolution;
                }
            }

            return maxResolution;
        }

        /// <summary>
        /// Gets the index of the highest quality setting available in the current project.
        /// </summary>
        /// <returns>The index of the highest quality setting, or -1 if no quality settings are available.</returns>
        private static int GetMaxQualityLevel()
        {
            // Get the total number of quality levels available
            int qualityCount = QualitySettings.names.Length;

            if (qualityCount == 0)
            {
                Debug.LogWarning("No quality settings found.");
                return -1;
            }

            // The highest quality level index is the last index in the array
            return qualityCount - 1;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!Application.isPlaying)
                _fieldOfView.SetValue(Mathf.Clamp(_fieldOfView.Value, 30f, 100f));
        }
#endif
    }
}