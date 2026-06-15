using PolymindGames.WieldableSystem;
using PolymindGames.BuildingSystem;
using System.Collections.Generic;
using JetBrains.Annotations;

namespace PolymindGames.Editor
{
    [UsedImplicitly]
    public sealed class BuildingPiecesPage : GenericDataDefinitionGroupPage<BuildingPieceCategoryDefinition, BuildingPieceDefinition>
    {
        public override string DisplayName => "Building Pieces";

        public override IEnumerable<IEditorToolPage> GetSubPages()
        {
            return new IEditorToolPage[]
            {
                new CarryablesPage(), new BuildMaterialsPage()
            };
        }

        #region Internal Types
        private sealed class CarryablesPage : GenericDataDefinitionPage<CarryableDefinition>
        {
            public override string DisplayName => "Carryables";
        }

        private sealed class BuildMaterialsPage : GenericDataDefinitionPage<BuildMaterialDefinition>
        {
            public override string DisplayName => "Build Materials";
        }
        #endregion
    }
}