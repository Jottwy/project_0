using UnityEngine;

namespace PolymindGames.UserInterface
{
    public sealed class CraftingStationUI : WorkstationInspectorBaseUI<CraftStation>
    {
        [SerializeField, Title("Crafting")]
        private CraftingUI _craftingUI;

        private const string DefaultStationName = "Handcraft";


        protected override void OnInspectionStarted(CraftStation workstation)
        {
            _craftingUI.UpdateCraftingLevel(workstation.CraftableLevel);
        }

        protected override void OnInspectionEnded(CraftStation workstation)
        {
            StationNameText.text = DefaultStationName;
        }
    }
}