using UnityEngine;

namespace PolymindGames.Options
{
    [CreateAssetMenu(menuName = CreateMenuPath + "Input Options", fileName = nameof(InputOptions))]
    public sealed partial class InputOptions : UserOptions<InputOptions>
    {
        [SerializeField]
        private Option<bool> _invertMouse = new();

        [SerializeField]
        private Option<float> _mouseSensitivity = new(1f);
        
        [SerializeField]
        private Option<float> _mouseSmoothness = new(0.3f);
        
        [SerializeField]
        private Option<bool> _crouchToggle = new(true);
        
        [SerializeField]
        private Option<bool> _runToggle = new();
        
        [SerializeField]
        private Option<bool> _aimToggle = new();        
        
        [SerializeField]
        private Option<bool> _leanToggle = new();
        
        [SerializeField]
        private Option<bool> _autoRun = new();

        public const float MaxSensitivity = 10f;
        public const float MinSensitivity = 0.1f;
        public const float MaxSmoothness = 1f;
        
        public Option<float> MouseSensitivity => _mouseSensitivity;
        public Option<float> MouseSmoothness => _mouseSmoothness;
        public Option<bool> InvertMouse => _invertMouse;
        public Option<bool> CrouchToogle => _crouchToggle;
        public Option<bool> RunToggle => _runToggle;
        public Option<bool> LeanToggle => _leanToggle;
        public Option<bool> AimToggle => _aimToggle;
        public Option<bool> AutoRun => _autoRun;

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!Application.isPlaying)
            {
                _mouseSensitivity.SetValue(Mathf.Clamp(_mouseSensitivity.Value, MinSensitivity, MaxSensitivity));
                _mouseSmoothness.SetValue(Mathf.Clamp(_mouseSmoothness.Value, 0f, MaxSmoothness));
            }
        }
#endif
    }
}