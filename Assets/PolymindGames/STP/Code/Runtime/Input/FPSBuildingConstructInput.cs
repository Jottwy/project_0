using UnityEngine.InputSystem;
using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    [AddComponentMenu("Input/Building Construct Input")]
    [RequireCharacterComponent(typeof(IConstructableBuilderCC))]
    public class FPSBuildingConstructInput : PlayerInputBehaviour
    {
        [SerializeField, Title("Actions")]
        private InputActionReference _addMaterialAction;

        [SerializeField]
        private InputActionReference _cancelPreviewAction;
        
        private IConstructableBuilderCC _constructableBuilder;
        private ICarryableControllerCC _carryableController;

        #region Initialization
        protected override void OnBehaviourStart(ICharacter character)
        {
            _constructableBuilder = character.GetCC<IConstructableBuilderCC>();
            _carryableController = character.GetCC<ICarryableControllerCC>();
        }

        protected override void OnBehaviourEnable(ICharacter character)
        {
            _cancelPreviewAction.RegisterStarted(OnCancelPreviewStart);
            _cancelPreviewAction.RegisterCanceled(OnCancelPreviewStop);
            _addMaterialAction.RegisterPerformed(OnAddMaterialAction);

            CoroutineUtility.InvokeNextFrame(this, () => _constructableBuilder.DetectionEnabled = true);
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            _cancelPreviewAction.UnregisterStarted(OnCancelPreviewStart);
            _cancelPreviewAction.UnregisterCanceled(OnCancelPreviewStop);
            _addMaterialAction.UnregisterPerformed(OnAddMaterialAction);

            if (!UnityUtility.IsQuitting)
                _constructableBuilder.DetectionEnabled = false;
        }
        #endregion

        #region Input Handling
        private void OnCancelPreviewStart(InputAction.CallbackContext obj) => _constructableBuilder.StartCancellingPreview();
        private void OnCancelPreviewStop(InputAction.CallbackContext obj) => _constructableBuilder.StopCancellingPreview();
        private void OnAddMaterialAction(InputAction.CallbackContext obj)
        {
            if (!_carryableController.IsCarrying)
                _constructableBuilder.TryAddMaterialFromPlayer();
        }
        #endregion
    }
}
