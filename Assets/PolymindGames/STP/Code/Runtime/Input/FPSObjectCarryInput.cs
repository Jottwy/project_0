using UnityEngine;
using UnityEngine.InputSystem;

namespace PolymindGames.InputSystem.Behaviours
{
    [AddComponentMenu("Input/Object Carry Input")]
    [RequireCharacterComponent(typeof(ICarryableControllerCC))]
    public class FPSObjectCarryInput : PlayerInputBehaviour
    {
        [SerializeField, Title("Actions")]
        private InputActionReference _useAction;

        [SerializeField]
        private InputActionReference _dropAction;

        private ICarryableControllerCC _objectCarry;

        #region Initialization
        protected override void OnBehaviourStart(ICharacter character)
        {
            _objectCarry = character.GetCC<ICarryableControllerCC>();
        }

        protected override void OnBehaviourEnable(ICharacter character)
        {
            _useAction.RegisterStarted(OnUseCarriedObject);
            _dropAction.RegisterStarted(OnDropAction);
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            _useAction.UnregisterStarted(OnUseCarriedObject);
            _dropAction.UnregisterStarted(OnDropAction);
        }
        #endregion

        #region Input Handling
        private void OnUseCarriedObject(InputAction.CallbackContext obj) => _objectCarry.UseCarriedObject();
        private void OnDropAction(InputAction.CallbackContext obj) => _objectCarry.DropCarriedObjects(1);
        #endregion
    }
}