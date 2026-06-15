using PolymindGames.UserInterface;
using UnityEngine.InputSystem;
using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    [AddComponentMenu("Input/Pause Input")]
    [RequireComponent(typeof(PauseMenu))]
    public class PauseUIInput : PlayerUIInputBehaviour
    {
        [SerializeField, Title("Actions")]
        private InputActionReference _pauseAction;

        private PauseMenu _pauseMenu;

        #region Initialization
        private void Start() => _pauseMenu = GetComponent<PauseMenu>();
        private void OnEnable() => _pauseAction.action.performed += OnPauseInput;
        private void OnDisable() => _pauseAction.action.performed -= OnPauseInput;
        #endregion

        #region Input handling
        private void OnPauseInput(InputAction.CallbackContext ctx)
        {
            if (Time.timeSinceLevelLoad > 0.5f)
                _pauseMenu.TryPause();
        }
        #endregion
    }
}
