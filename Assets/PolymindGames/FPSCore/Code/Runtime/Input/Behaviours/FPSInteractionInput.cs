using UnityEngine.InputSystem;
using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    [AddComponentMenu("Input/Interaction Input")]
    [RequireCharacterComponent(typeof(IInteractionHandlerCC))]
    public class FPSInteractionInput : PlayerInputBehaviour
    {
        [SerializeField, Title("Actions")]
        private InputActionReference _interactAction;

        private IInteractionHandlerCC _interaction;
        
        #region Initialization
        protected override void OnBehaviourStart(ICharacter character)
        {
            _interaction = character.GetCC<IInteractionHandlerCC>();
        }

        protected override void OnBehaviourEnable(ICharacter character)
        {
            _interactAction.RegisterStarted(OnInteractStart);
            _interactAction.RegisterCanceled(OnInteractStop);
            CoroutineUtility.InvokeNextFrame(this, () => _interaction.InteractionEnabled = true);
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            _interactAction.UnregisterStarted(OnInteractStart);
            _interactAction.UnregisterCanceled(OnInteractStop);
            _interaction.InteractionEnabled = false;
        }
        #endregion

        #region Input Handling
        private void OnInteractStart(InputAction.CallbackContext obj) => _interaction.StartInteraction();
        private void OnInteractStop(InputAction.CallbackContext obj) => _interaction.StopInteraction();
        #endregion
    }
}
