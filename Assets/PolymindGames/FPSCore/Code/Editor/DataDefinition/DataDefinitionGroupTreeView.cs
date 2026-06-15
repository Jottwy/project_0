using System.Collections.Generic;
using UnityEditor.IMGUI.Controls;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    public sealed class DataDefinitionGroupTreeView<Group, Member> : ObjectTreeView
        where Group : GroupDefinition<Group, Member>
        where Member : GroupMemberDefinition<Member, Group>
    {
        public DataDefinitionGroupTreeView(TreeViewState state) : base(state) { }

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

            int id = 0;
            var groupDefinitions = GroupDefinition<Group, Member>.Definitions;
            foreach (var group in groupDefinitions)
            {
                list.Add(new ObjectTreeViewItem(group, id++, 0, group.Name)
                {
                    icon = group.Icon != null ? group.Icon.texture : null
                });

                foreach (var member in group.Members)
                {
                    if (member == null)
                        continue;

                    list.Add(new ObjectTreeViewItem(member, id++, 1, member.Name)
                    {
                        icon = member.Icon != null ? member.Icon.texture : null
                    });
                }
            }

            return list;
        }

        protected override void RefreshItems()
        {
            var validationContext = new DataDefinition.ValidationContext(true, DataDefinition.ValidationTrigger.Refresh);

            GroupDefinition<Group, Member>.ReloadDefinitions_EditorOnly();
            var groupDefinitions = GroupDefinition<Group, Member>.Definitions;

            foreach (var group in groupDefinitions)
                group.Validate_EditorOnly(validationContext);

            GroupMemberDefinition<Member, Group>.ReloadDefinitions_EditorOnly();
            var memberDefinitions = GroupMemberDefinition<Member, Group>.Definitions;

            foreach (var member in memberDefinitions)
                member.Validate_EditorOnly(validationContext);

            base.RefreshItems();
        }

        protected override bool CanStartDrag(CanStartDragArgs args)
        {
            return string.IsNullOrEmpty(searchString) && ((ObjectTreeViewItem)args.draggedItem).UnityObject is Member;
        }

        protected override void SetupDragAndDrop(SetupDragAndDropArgs args) 
        {
            // Start a drag-and-drop operation
            DragAndDrop.PrepareStartDrag();

            // Collect the dragged items
            var draggedItems = args.draggedItemIDs
                .Select(id => FindItem(id, rootItem) as ObjectTreeViewItem)
                .Where(item => item.UnityObject is Member)
                .ToArray();

            // Save the dragged items to the DragAndDrop object
            DragAndDrop.objectReferences = draggedItems
                .Select(item => item.UnityObject)
                .ToArray();

            DragAndDrop.paths = draggedItems
                .Select(item => ((DataDefinition)item.UnityObject).Name)
                .ToArray();

            DragAndDrop.SetGenericData("TreeViewDraggedItems", draggedItems);

            DragAndDrop.StartDrag("Dragging Items");
        }

        protected override DragAndDropVisualMode HandleDragAndDrop(DragAndDropArgs args)
        {
            if (args.dragAndDropPosition is DragAndDropPosition.UponItem or DragAndDropPosition.BetweenItems)
            {
                if (args.parentItem == rootItem)
                    return DragAndDropVisualMode.Rejected;

                if (args.performDrop)
                {
                    // Handle the drop operation
                    if (DragAndDrop.GetGenericData("TreeViewDraggedItems") is ObjectTreeViewItem[] draggedItems)
                    {
                        var parentGroup = GetContainingGroup((ObjectTreeViewItem)args.parentItem);
                        foreach (var draggedItem in draggedItems)
                        {
                            if (draggedItem.UnityObject is Member member)
                            {
                                member.ParentGroup.RemoveMember_EditorOnly(member);
                                member.SetParentGroup_EditorOnly(parentGroup);
                            }
                        }

                        SaveAssetsAndReload();
                    }
                }

                return DragAndDropVisualMode.Move;
            }

            return DragAndDropVisualMode.None;
        }

        protected override void CreateNewItem()
        {
            DataDefinition newDefinition = CreateDef();
            SaveAssetsAndReload();
            SelectScriptable(newDefinition, TreeViewSelectionOptions.RevealAndFrame | TreeViewSelectionOptions.FireSelectionChanged, true);

            DataDefinition CreateDef()
            {
                var selection = GetSelection();
                if (selection.Count > 0 && selection[0] != rootItem.id)
                {
                    var member = DataDefinitionAssetUtility.CreateDefinition<Member>(null, " New");
                    var parentGroup = GetContainingGroup(FindItem(selection[0], rootItem));
                    member.SetParentGroup_EditorOnly(parentGroup);
                    DataDefinitionAssetUtility.SaveDefinition(member, member.Name);

                    return member;
                }

                var groupDef = DataDefinitionAssetUtility.CreateDefinition<Group>(null, " New");
                DataDefinitionAssetUtility.SaveDefinition(groupDef, typeof(Group).Name);
                return groupDef;
            }
        }

        protected override void DeleteSelectedItems()
        {
            if (!HasValidSelection())
                return;

            // Show a confirmation dialog to the user
            bool confirmed = EditorUtility.DisplayDialog(
                "Delete Selected Items",
                "Are you sure you want to delete (move to trash) the selected items? This action cannot be undone.",
                "Delete",
                "Cancel"
            );

            // Proceed only if the user confirms
            if (!confirmed)
                return;

            var selectedItems = GetSelectedTreeViewItems();
            SelectItem(rootItem);

            foreach (var item in selectedItems)
            {
                if (item.UnityObject == null)
                    continue;

                if (item.UnityObject is Group group)
                {
                    foreach (var member in group.Members)
                        DataDefinitionAssetUtility.DeleteDefinition(member);

                    DataDefinitionAssetUtility.DeleteDefinition(group);
                }
                else
                {
                    var member = (Member)item.UnityObject;
                    member.ParentGroup.RemoveMember_EditorOnly(member);
                    DataDefinitionAssetUtility.DeleteDefinition(member);
                }
            }

            SaveAssetsAndReload();
        }

        protected override void DuplicateSelectedItems()
        {
            if (!HasValidSelection())
                return;

            var newDefinitions = new List<ScriptableObject>();
            var selectedItems = GetSelectedTreeViewItems();
            foreach (var selectedItem in selectedItems)
            {
                switch (selectedItem.UnityObject)
                {
                    case Member member:
                        if (selectedItems.Exists(item => item.UnityObject is Group group && member.ParentGroup == group))
                            continue;

                        newDefinitions.Add(DuplicateMember(member, member.ParentGroup));
                        break;
                    case Group group:
                        var duplicatedGroup = DuplicateGroup(group);
                        newDefinitions.Add(duplicatedGroup);

                        foreach (var member in duplicatedGroup.Members)
                            newDefinitions.Add(member);
                        break;
                }
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

            var newDefinitions = new List<ScriptableObject>();
            var definitions = LoadDefinitionsFromClipboard(clipboardItemGuids);

            // Paste members.
            if (!definitions.Exists(definition => definition is Group))
            {
                var selection = GetSelection();
                Group selectedGroup = GetContainingGroup(FindItem(selection[0], rootItem));

                foreach (var member in definitions)
                    newDefinitions.Add(DuplicateMember((Member)member, selectedGroup));
            }
            // Paste groups.
            else
            {
                foreach (var definition in definitions)
                {
                    if (definition is Group group)
                    {
                        var duplicatedGroup = DuplicateGroup(group);
                        newDefinitions.Add(duplicatedGroup);

                        foreach (var member in duplicatedGroup.Members)
                            newDefinitions.Add(member);
                    }
                }
            }

            clipboardItemGuids.Clear();
            SaveAssetsAndReload();
            SelectScriptables(newDefinitions, TreeViewSelectionOptions.FireSelectionChanged | TreeViewSelectionOptions.RevealAndFrame);
        }

        private static List<DataDefinition> LoadDefinitionsFromClipboard(List<GUID> clipboardItemGuids)
        {
            var list = new List<DataDefinition>();
            foreach (var guid in clipboardItemGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var definition = AssetDatabase.LoadAssetAtPath<DataDefinition>(path);
                if (definition != null)
                    list.Add(definition);
            }

            return list;
        }

        private static Member DuplicateMember(Member member, Group parentGroup)
        {
            var newMember = DataDefinitionAssetUtility.CreateDefinition(member, " Copy");
            newMember.SetParentGroup_EditorOnly(parentGroup);
            DataDefinitionAssetUtility.SaveDefinition(newMember, newMember.Name);
            return newMember;
        }

        private static Group DuplicateGroup(Group group)
        {
            var newGroup = DataDefinitionAssetUtility.CreateDefinition(group, " Copy");

            var originalMembers = group.Members;
            var newMembers = new Member[originalMembers.Length];

            for (int i = 0; i < newMembers.Length; i++)
                newMembers[i] = DuplicateMember(originalMembers[i], newGroup);

            newGroup.SetMembers_EditorOnly(newMembers);
            DataDefinitionAssetUtility.SaveDefinition(newGroup, newGroup.Name);

            return newGroup;
        }

        private static Group GetContainingGroup(TreeViewItem treeViewItem)
        {
            var definition = ((ObjectTreeViewItem)treeViewItem).UnityObject;
            return definition switch
            {
                Member member => member.ParentGroup,
                Group group => group,
                _ => null
            };
        }
        
        private void SaveAssetsAndReload()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Reload();
        }
    }
}