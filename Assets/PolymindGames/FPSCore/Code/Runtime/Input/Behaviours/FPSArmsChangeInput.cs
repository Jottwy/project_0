using PolymindGames.WieldableSystem;
using UnityEngine.InputSystem;
using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    [AddComponentMenu("Input/Change Arms Input")]
    public sealed class FPSArmsChangeInput : PlayerInputBehaviour
    {
        [SerializeField, Title("Actions")]
        private InputActionReference _changeArmsAction; 

        private WieldableArmsHandler _armsHandler;


        #region Initialization
        protected override void OnBehaviourStart(ICharacter character)
        {
            _armsHandler = GetComponent<WieldableArmsHandler>();
        }
        
        protected override void OnBehaviourEnable(ICharacter character) => _changeArmsAction.RegisterStarted(ToggleArmsAction);
        protected override void OnBehaviourDisable(ICharacter character) => _changeArmsAction.UnregisterStarted(ToggleArmsAction);
        #endregion

        #region Input Handling
        private void ToggleArmsAction(InputAction.CallbackContext obj) => _armsHandler.ToggleNextArmSet();
        #endregion
    }
}
