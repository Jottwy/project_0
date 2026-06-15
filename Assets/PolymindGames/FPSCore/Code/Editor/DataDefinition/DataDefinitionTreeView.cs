using System.Collections.Generic;
using UnityEditor.IMGUI.Controls;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    public sealed class DataDefinitionTreeView<T> : ObjectTreeView where T : DataDefinition<T>
    {
        public DataDefinitionTreeView(TreeViewState state) : base(state)
        { }
        
        protected override TreeViewItem BuildRoot()
        {
            var root = new ObjectTreeViewItem(null, -1, -1, "Root");
            var pages = BuildDefinitionTreeItems();
            SetupParentsAndChildrenFromDepths(root, pages);
            return root;
        }
        
        private static IList<TreeViewItem> BuildDefinitionTreeItems()
        {
            var list = new List<TreeViewItem>();
            var definitions = DataDefinition<T>.Definitions;

            int i = 0;
            foreach (var definition in definitions)
            {
                list.Add(new ObjectTreeViewItem(definition, i++, 0, definition.Name)
                {
                    icon = definition.Icon != null ? definition.Icon.texture : null
                });
            }

            return list;
        }

        protected override void RefreshItems()
        {
            base.RefreshItems();
            
            var validationContext = new DataDefinition.ValidationContext(true, DataDefinition.ValidationTrigger.Refresh);
            
            DataDefinition<T>.ReloadDefinitions_EditorOnly();
            var memberDefinitions = DataDefinition<T>.Definitions;
                
            foreach (var member in memberDefinitions)
                member.Validate_EditorOnly(validationContext);
        }

        protected override void CreateNewItem()
        {
            var newDefinition = DataDefinitionAssetUtility.CreateDefinition<T>(null, " New");
            DataDefinitionAssetUtility.SaveDefinition(newDefinition, typeof(T).Name);
            SaveAssetsAndReload();

            SelectScriptable(newDefinition, TreeViewSelectionOptions.FireSelectionChanged | TreeViewSelectionOptions.RevealAndFrame, true);
        }

        protected override void DeleteSelectedItems()
        {
            if (!HasValidSelection())
                return;

            var selectedItems = GetSelectedTreeViewItems();
            SelectItem(rootItem);

            foreach (var item in selectedItems)
                DataDefinitionAssetUtility.DeleteDefinition((T)item.UnityObject);

            SaveAssetsAndReload();
        }

        protected override void DuplicateSelectedItems()
        {
            if (!HasValidSelection())
                return;

            var newDefinitions = new List<ScriptableObject>();
            foreach (var originalItem in GetSelectedTreeViewItems())
            {
                T newDefinition = DataDefinitionAssetUtility.CreateDefinition((T)originalItem.UnityObject, " Copy");
                DataDefinitionAssetUtility.SaveDefinition(newDefinition, newDefinition.Name);
                newDefinitions.Add(newDefinition);
            }

            SaveAssetsAndReload();
            SelectScriptables(newDefinitions, TreeViewSelectionOptions.FireSelectionChanged | TreeViewSelectionOptions.RevealAndFrame);
        }
        
        protected override void OnObjectRenamed(Object treeViewObject, string newName)
        {
            if (treeViewObject is DataDefinition definition)
            {
                definition.Name = newName;
            }
        }

        protected override void PasteClipboardItem(List<GUID> clipboardItemGuids)
        {
            if (clipboardItemGuids.Count == 0)
                return;

            var createdDefinitions = new List<ScriptableObject>();
            foreach (var guid in clipboardItemGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                T originalDefinition = AssetDatabase.LoadAssetAtPath<T>(path);

                if (originalDefinition == null)
                    continue;

                T newDefinition = DataDefinitionAssetUtility.CreateDefinition(originalDefinition, " Pasted");
                DataDefinitionAssetUtility.SaveDefinition(newDefinition, newDefinition.Name);
                createdDefinitions.Add(newDefinition);
            }

            clipboardItemGuids.Clear();
            SaveAssetsAndReload();
            SelectScriptables(createdDefinitions, TreeViewSelectionOptions.FireSelectionChanged | TreeViewSelectionOptions.RevealAndFrame);
        }
        
        private void SaveAssetsAndReload()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Reload();
        }
    }
}