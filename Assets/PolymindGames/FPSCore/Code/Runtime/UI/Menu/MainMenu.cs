using PolymindGames.InputSystem;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [DefaultExecutionOrder(ExecutionOrderConstants.MonoSingleton)]
    public sealed class MainMenu : MonoBehaviour
    {
        [SerializeField]
        private InputContext _menuContext;
        
        public void QuitGame() => LevelManager.Instance.FadeInAndQuitGame();
        public static System.Action OnMultiplayerClicked;
        public void RedirectToMultiplayerAddon() => OnMultiplayerClicked?.Invoke();

        private void OnEnable() => InputManager.Instance.PushContext(_menuContext);
        private void OnDisable() => InputManager.Instance.PopContext(_menuContext);
    }
}