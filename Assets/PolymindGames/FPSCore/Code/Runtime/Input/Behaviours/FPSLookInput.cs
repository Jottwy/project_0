using UnityEngine.InputSystem;
using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    [AddComponentMenu("Input/Look Input")]
    [RequireCharacterComponent(typeof(ILookHandlerCC))]
    public class FPSLookInput : PlayerInputBehaviour
    {
        [SerializeField, Title("Actions")]
        private InputActionReference _lookAction;

        private ILookHandlerCC _lookHandler;

        #region Initialization
        protected override void OnBehaviourStart(ICharacter character)
        {
            _lookHandler = character.GetCC<ILookHandlerCC>();
        }

        protected override void OnBehaviourEnable(ICharacter character)
        {
            _lookAction.EnableAction();
            _lookHandler.SetLookInput(GetInput);
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            _lookAction.DisableAction();
            _lookHandler.SetLookInput(null);
        }
        #endregion

        #region Input Handling
        private Vector2 GetInput()
        {
#if UNITY_EDITOR
            if (Time.timeSinceLevelLoad < 0.15f)
                return Vector2.zero;
#endif
            
            Vector2 lookInput = _lookAction.action.ReadValue<Vector2>();
            (lookInput.x, lookInput.y) = (lookInput.y, lookInput.x);
            return lookInput;
        }
        #endregion
    }
}