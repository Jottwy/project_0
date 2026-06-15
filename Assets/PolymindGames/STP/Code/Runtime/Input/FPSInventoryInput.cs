using UnityEngine.InputSystem;
using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    [AddComponentMenu("Input/Inventory Input")]
    [RequireCharacterComponent(typeof(IInventoryInspectionManagerCC))]
    public class FPSInventoryInput : PlayerInputBehaviour
    {
        [SerializeField, Title("Actions")]
        private InputActionReference _inventoryAction;

        private IInventoryInspectionManagerCC _inventoryInspection;

        #region Initialization
        protected override void OnBehaviourStart(ICharacter character) => _inventoryInspection = character.GetCC<IInventoryInspectionManagerCC>();
        protected override void OnBehaviourEnable(ICharacter character) => _inventoryAction.RegisterStarted(OnInventoryToggleInput);
        protected override void OnBehaviourDisable(ICharacter character) => _inventoryAction.UnregisterStarted(OnInventoryToggleInput);
        #endregion

        #region Input Handling
        private void OnInventoryToggleInput(InputAction.CallbackContext context)
        {
            if (Time.timeSinceLevelLoad < 0.5f)
                return;
            
            if (!_inventoryInspection.IsInspecting)
                _inventoryInspection.StartInspection(null);
            else
                _inventoryInspection.StopInspection();
        }
        #endregion
    }
}
