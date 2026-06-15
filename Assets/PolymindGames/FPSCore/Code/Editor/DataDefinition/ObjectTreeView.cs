using System.Collections.Generic;
using UnityEditor.IMGUI.Controls;
using UnityEditor;
using UnityEngine;
using System.IO;
using System;

namespace PolymindGames.Editor
{
    using Object = UnityEngine.Object;

    public sealed class ObjectTreeViewItem : TreeViewItem
    {
        public ObjectTreeViewItem(Object unityObject, int id, int depth, string displayName) : base(
            id, depth, displayName)
        {
            UnityObject = unityObject;
        }

        public Object UnityObject { get; }
    }

    public abstract class ObjectTreeView : CustomTreeView
    {
        private readonly Object[] _selectedTargetsCache = new Object[1];
        private readonly InspectorEditorWrapper _inspector;
        private readonly List<GUID> _clipboardItemGuids;

        protected ObjectTreeView(TreeViewState state) : base(state)
        {
            _inspector = new InspectorEditorWrapper();
            _clipboardItemGuids = new List<GUID>();
            showBorder = true;
            useScrollView = true;
            enableItemHovering = true;

            RefreshItems();
            EnsureValidSelection();
        }

        public override void OnGUI(Rect rect)
        {
            base.OnGUI(rect);

            if (HasFocus())
            {
                HandleInput();
            }
        }

        public void DrawObjectInspector()
        {
            using (new GUILayout.VerticalScope())
            {
                if (!_inspector.HasTarget)
                    return;

                string label = _inspector.Targets.Length > 1
                    ? $"- ({_inspector.Targets.Length})"
                    : ((DataDefinition)_inspector.Target).FullName;
                GUILayout.Label(label, GUIStyles.Title);

                _inspector.Draw(EditorStyles.helpBox);
            }
        }

        protected override void SelectionChanged(IList<int> selectedIds)
        {
            var selectedDefinitions = GetScriptablesForSelectedIds(selectedIds);
            _inspector.SetTargets(selectedDefinitions);
            Repaint();
        }

        protected override void ContextClickedItem(int id)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Copy"), false, CopySelectedItems);

            if (_clipboardItemGuids.Count > 0)
            {
                menu.AddItem(new GUIContent("Paste"), false, () => PasteClipboardItem(_clipboardItemGuids));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Paste"), false);
            }

            menu.AddItem(new GUIContent("Rename"), false, () => BeginRename(hoveredItem));
            menu.AddItem(new GUIContent("Duplicate"), false, DuplicateSelectedItems);
            menu.AddItem(new GUIContent("Delete"), false, DeleteSelectedItems);
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Create"), false, CreateNewItem);
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Refresh"), false, RefreshItems);
            menu.ShowAsContext();
        }

        protected virtual void HandleInput()
        {
            var evt = Event.current;

            switch (evt.type)
            {
                case EventType.MouseUp:
                {
                    if (hoveredItem == null && treeViewRect.Contains(evt.mousePosition))
                    {
                        if (evt.keyCode == KeyCode.Mouse0)
                        {
                            SelectItem(null);
                            evt.Use();
                        }

                        if (evt.keyCode == KeyCode.Mouse1)
                        {
                            var menu = new GenericMenu();
                            menu.AddItem(new GUIContent("Create"), false, CreateNewItem);
                            menu.AddSeparator(string.Empty);
                            menu.AddItem(new GUIContent("Refresh"), false, Reload);
                            menu.ShowAsContext();
                        }
                    }

                    break;
                }
                case EventType.KeyDown:
                    switch (evt.keyCode)
                    {
                        case KeyCode.F5:
                            RefreshItems();
                            evt.Use();
                            break;
                        case KeyCode.Delete:
                            DeleteSelectedItems();
                            evt.Use();
                            break;
                        case KeyCode.C when evt.control:
                            CopySelectedItems();
                            evt.Use();
                            break;
                        case KeyCode.V when evt.control:
                            PasteClipboardItem(_clipboardItemGuids);
                            evt.Use();
                            break;
                        case KeyCode.D when evt.control:
                            DuplicateSelectedItems();
                            evt.Use();
                            break;
                        case KeyCode.N when evt.control:
                            CreateNewItem();
                            evt.Use();
                            break;
                    }

                    break;
            }
        }

        protected abstract void CreateNewItem();
        protected abstract void DeleteSelectedItems();
        protected abstract void DuplicateSelectedItems();
        protected abstract void PasteClipboardItem(List<GUID> clipboardItemGuids);

        protected virtual void RefreshItems()
        {
            _clipboardItemGuids.Clear();
            Reload();
        }

        protected virtual void CopySelectedItems()
        {
            _clipboardItemGuids.Clear();

            if (!HasValidSelection())
                return;

            foreach (var item in GetSelectedTreeViewItems())
                _clipboardItemGuids.Add(AssetDatabase.GUIDFromAssetPath(AssetDatabase.GetAssetPath(item.UnityObject)));
        }

        protected override bool CanRename(TreeViewItem item)
            => item is ObjectTreeViewItem;

        protected override void RenameEnded(RenameEndedArgs args)
        {
            if (!args.acceptedRename || args.newName == args.originalName)
                return;

            // Find the tree view item by its ID
            if (FindItem(args.itemID, rootItem) is not ObjectTreeViewItem treeViewItem)
                return;

            string newName = args.newName.Trim();

            if (string.IsNullOrEmpty(newName))
            {
                Debug.LogWarning("New name cannot be empty.");
                return;
            }

            // Ensure the name doesn't contain invalid file characters
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Debug.LogWarning("New name contains invalid characters.");
                return;
            }

            var scriptableObject = treeViewItem.UnityObject;

            // Rename the asset file to match the new name
            string assetPath = AssetDatabase.GetAssetPath(scriptableObject);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogError("Failed to find asset path for the definition.");
                return;
            }

            newName = AssetDatabaseUtility.RenameAssetWithUniqueName(assetPath, newName);
            scriptableObject.name = newName;
            treeViewItem.displayName = newName.RemovePrefix();
            OnObjectRenamed(scriptableObject, newName);
            EditorUtility.SetDirty(scriptableObject);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Repaint();
        }

        protected virtual void OnObjectRenamed(Object treeViewObject, string newName) { }

        protected List<ObjectTreeViewItem> GetSelectedTreeViewItems()
        {
            var selectedIds = GetSelection();
            var selectedItems = new List<ObjectTreeViewItem>();

            foreach (int id in selectedIds)
                selectedItems.Add((ObjectTreeViewItem)FindItem(id, rootItem));

            return selectedItems;
        }

        protected void SelectScriptables(List<ScriptableObject> scriptables, TreeViewSelectionOptions options)
        {
            var selection = new int[scriptables.Count];
            for (int i = 0; i < scriptables.Count; i++)
            {
                var def = scriptables[i];
                selection[i] = SearchAll(item => ((ObjectTreeViewItem)item).UnityObject == def).id;
            }

            SetSelection(selection, options);
        }

        protected void SelectScriptable(ScriptableObject scriptable, TreeViewSelectionOptions options, bool beginRename)
        {
            var newItem = SearchAll(item => ((ObjectTreeViewItem)item).UnityObject == scriptable);
            SelectItem(newItem, options);

            if (beginRename)
                BeginRename(newItem);
        }

        private Object[] GetScriptablesForSelectedIds(IList<int> selectedIds)
        {
            switch (selectedIds.Count)
            {
                case 0:
                    return Array.Empty<Object>();
                case 1:
                    if (selectedIds[0] == rootItem.id)
                        return Array.Empty<Object>();

                    _selectedTargetsCache[0] = ((ObjectTreeViewItem)FindItem(selectedIds[0], rootItem)).UnityObject;
                    return _selectedTargetsCache;
                default:
                    var selectedDefinitions = new Object[selectedIds.Count];
                    var firstDef = ((ObjectTreeViewItem)FindItem(selectedIds[0], rootItem)).UnityObject;
                    var firstDefType = firstDef.GetType();
                    selectedDefinitions[0] = firstDef;

                    for (int i = 1; i < selectedDefinitions.Length; i++)
                    {
                        var foundDef = ((ObjectTreeViewItem)FindItem(selectedIds[i], rootItem)).UnityObject;
                        selectedDefinitions[i] = foundDef.GetType() == firstDefType ? foundDef : null;
                    }

                    return selectedDefinitions;
            }
        }
    }
}