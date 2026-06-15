using PolymindGames.BuildingSystem;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    public sealed class BookSocketBuildingUI : CharacterUIBehaviour
    {
        [SerializeField]
        private DataIdReference<BuildingPieceDefinition> _startingPiece;

        public void StartSocketBasedBuilding()
        {
            if (Character == null)
            {
                Debug.LogError("No character found in the parent of this object", gameObject);
                return;
            }

            var buildingPieceInstance = Instantiate(_startingPiece.Def.Prefab, new Vector3(0f, -1000f, 0f), Quaternion.identity);
            Character.GetCC<IBuildControllerCC>().SetBuildingPiece(buildingPieceInstance);
            Character.GetCC<IWieldablesControllerCC>().TryEquipWieldable(null);
        }
    }
}
