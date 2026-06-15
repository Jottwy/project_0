using PolymindGames.InventorySystem;
using System.Collections.Generic;
using JetBrains.Annotations;

namespace PolymindGames.Editor
{
    [UsedImplicitly]
    public sealed class ItemsPage : GenericDataDefinitionGroupPage<ItemCategoryDefinition, ItemDefinition>
    {
        public override string DisplayName => "Items";
        
        public override IEnumerable<IEditorToolPage> GetSubPages()
        {
            return new IEditorToolPage[]
            {
                new ItemPropertiesPage(),
                new ItemTagsPage()
            };
        }
        
        #region Internal Types
        private sealed class ItemPropertiesPage : GenericDataDefinitionPage<ItemPropertyDefinition>
        {
            public override string DisplayName => "Properties";
        }
    
        private sealed class ItemTagsPage : GenericDataDefinitionPage<ItemTagDefinition>
        {
            public override string DisplayName => "Tags";
        }
        #endregion
    }
}