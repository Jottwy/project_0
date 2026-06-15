using UnityEngine;
using System;

namespace PolymindGames.InventorySystem
{
    [CreateAssetMenu(menuName = "Polymind Games/Items/Item Category", fileName = "Category_")]
    public sealed class ItemCategoryDefinition : GroupDefinition<ItemCategoryDefinition, ItemDefinition>
    {
#if UNITY_EDITOR
	    [SerializeField, SpaceArea]
		[DataReference(NullElement = ItemTagDefinition.Untagged)]
		private DataIdReference<ItemTagDefinition> _defaultTag;
#endif

		[SerializeField]
		[ReorderableList(ListStyle.Lined, HasLabels = false)]
		private ItemAction[] _baseActions = Array.Empty<ItemAction>();

		public ItemAction[] BaseActions => _baseActions;
		
#if UNITY_EDITOR
	    public DataIdReference<ItemTagDefinition> DefaultTag => _defaultTag;
#endif
	}
}