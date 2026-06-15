using PolymindGames.Options;
using UnityEngine.UI;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    public partial class InputOptionsUI : UserOptionsUI<InputOptions>
    {
        [SerializeField]
        private Toggle _runToggle;

        [SerializeField]
        private Toggle _crouchToggle;
        
        [SerializeField]
        private Toggle _aimToggle;
        
        [SerializeField]
        private Toggle _leanToggle;
        
        [SerializeField]
        private Toggle _autoRunToggle;
        
        [SerializeField]
        private Toggle _invertMouseToggle;

        [SerializeField]
        private Slider _mouseSensitivity;
        
        [SerializeField]
        private Slider _mouseSmoothness;
        
        protected override void Start()
        {
            base.Start();
            
            _leanToggle.onValueChanged.AddListener(UserOptions.LeanToggle.SetValue);
            _runToggle.onValueChanged.AddListener(UserOptions.RunToggle.SetValue);
            _aimToggle.onValueChanged.AddListener(UserOptions.AimToggle.SetValue);
            _autoRunToggle.onValueChanged.AddListener(UserOptions.AutoRun.SetValue);
            _crouchToggle.onValueChanged.AddListener(UserOptions.CrouchToogle.SetValue);
            _invertMouseToggle.onValueChanged.AddListener(UserOptions.InvertMouse.SetValue);
            _mouseSensitivity.onValueChanged.AddListener(UserOptions.MouseSensitivity.SetValue);
            _mouseSmoothness.onValueChanged.AddListener(val => UserOptions.MouseSmoothness.SetValue(val * 0.01f));
        }

        protected override void ResetUIState()
        {
            _runToggle.isOn = UserOptions.RunToggle;
            _aimToggle.isOn = UserOptions.AimToggle;
            _leanToggle.isOn = UserOptions.LeanToggle;
            _autoRunToggle.isOn = UserOptions.AutoRun;
            _crouchToggle.isOn = UserOptions.CrouchToogle;
            _invertMouseToggle.isOn = UserOptions.InvertMouse;

            _mouseSensitivity.maxValue = InputOptions.MaxSensitivity;
            _mouseSensitivity.minValue = InputOptions.MinSensitivity;
            _mouseSensitivity.wholeNumbers = false;
            _mouseSensitivity.value = UserOptions.MouseSensitivity;

            _mouseSmoothness.maxValue = 100f;
            _mouseSmoothness.wholeNumbers = true;
            _mouseSmoothness.value = UserOptions.MouseSmoothness * 100f;
        }
    }
}
