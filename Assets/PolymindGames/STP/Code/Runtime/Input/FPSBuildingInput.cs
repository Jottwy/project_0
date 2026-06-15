using PolymindGames.BuildingSystem;
using UnityEngine.InputSystem;
using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    [AddComponentMenu("Input/Building Input")]
    [RequireCharacterComponent(typeof(IBuildControllerCC))]
    public class FPSBuildingInput : PlayerInputBehaviour
    {
        [SerializeField, Title("Actions")]
        private InputActionReference _placePreviewAction;

        [SerializeField]
        private InputActionReference _buildingRotateAction;

        [SerializeField]
        private InputActionReference _buildingCycleAction;

        private IBuildControllerCC _buildController;

        #region Initialization
        protected override void OnBehaviourStart(ICharacter character)
        {
            _buildController = character.GetCC<IBuildControllerCC>();
        }

        protected override void OnBehaviourEnable(ICharacter character)
        {
            _buildingCycleAction.RegisterStarted(OnCycleInput);
            _buildingRotateAction.RegisterPerformed(OnRotateInput);
            _placePreviewAction.RegisterStarted(OnPlaceInput);
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            _buildingCycleAction.UnregisterStarted(OnCycleInput);
            _buildingRotateAction.UnregisterPerformed(OnRotateInput);
            _placePreviewAction.UnregisterStarted(OnPlaceInput);
        }
        #endregion

        #region Input Handling
        private void OnCycleInput(InputAction.CallbackContext ctx)
        {
            if (_buildController.BuildingPiece == null || _buildController.BuildingPiece is FreeBuildingPiece)
                return;

            bool next = ctx.ReadValue<float>() > 0.1f;
            var newDef = BuildingPieceDefinition.GetNextGroupBuildingPiece(_buildController.BuildingPiece.Definition, next);
            var newPiece = Instantiate(newDef.Prefab, new Vector3(0f, -1000f, 0f), Quaternion.identity).GetComponent<BuildingPiece>();
            _buildController.SetBuildingPiece(newPiece);
        }
        
        private void OnPlaceInput(InputAction.CallbackContext ctx) => _buildController.TryPlaceBuildingPiece();
        private void OnRotateInput(InputAction.CallbackContext ctx) => _buildController.RotationOffset += ctx.ReadValue<float>();
        #endregion
    }
}