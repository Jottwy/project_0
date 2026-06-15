using PolymindGames.WieldableSystem;
using PolymindGames.BuildingSystem;
using PolymindGames.InputSystem;
using UnityEngine;
using System.Linq;

namespace PolymindGames.UserInterface
{
    public sealed class SurvivalBookUI : CharacterUIBehaviour
    {
        [SerializeField]
        private InputContext _bookContext;

        [SerializeField, SpaceArea(3f)]
        private SelectableGroupBase _menuGroup;
        
        [SerializeField]
        private Canvas _menuCanvas;

        [SerializeField]
        private Canvas _contentCanvas;

        private WieldableTool _bookWieldable;
        
        public void StopBookInspection()
        {
            if (_bookWieldable == null)
                return;
            
            var controller = Character.GetCC<IWieldablesControllerCC>();
            controller.TryHolsterWieldable(_bookWieldable);
        }
        
        public void StartSocketBasedBuilding()
        {
            if (Character == null)
            {
                Debug.LogError("No character found in the parent of this object", gameObject);
                return;
            }

            var defaultBuildingPiece = BuildingManager.Instance.DefaultBuildingPiece != null
                ? BuildingManager.Instance.DefaultBuildingPiece
                : BuildingPieceDefinition.Definitions.FirstOrDefault();

            if (defaultBuildingPiece == null)
            {
                Debug.LogError("No building piece found in this project.");
                return;
            }

            var buildingPieceInstance = Instantiate(defaultBuildingPiece.Prefab, new Vector3(0f, -1000f, 0f), Quaternion.identity);
            Character.GetCC<IBuildControllerCC>().SetBuildingPiece(buildingPieceInstance);
            Character.GetCC<IWieldablesControllerCC>().TryEquipWieldable(null);
        }

        protected override void Awake()
        {
            base.Awake();
            
            _menuCanvas.worldCamera = UnityUtility.CachedMainCamera;
            _contentCanvas.worldCamera = UnityUtility.CachedMainCamera;
            
            _bookWieldable = GetComponentInParent<WieldableTool>();

            if (_bookWieldable == null)
            {
                Debug.LogError("No Book Wieldable (Simple Tool) found in the parent", gameObject);
                return;
            }

            _bookWieldable.EquippingStarted += ShowBookUI;
            _bookWieldable.HolsteringStarted += HideBookUI;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            
            if (_bookWieldable != null)
            {
                _bookWieldable.EquippingStarted -= ShowBookUI;
                _bookWieldable.HolsteringStarted -= HideBookUI;
            }
        }

        private void ShowBookUI()
        {
            _menuGroup.SelectDefault();
            InputManager.Instance.PushEscapeCallback(StopBookInspection);
            InputManager.Instance.PushContext(_bookContext);
        }

        private void HideBookUI()
        {
            _menuGroup.SelectDefault();
            InputManager.Instance.PopEscapeCallback(StopBookInspection);
            InputManager.Instance.PopContext(_bookContext);
        }
    }
}