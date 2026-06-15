using UnityEditor.IMGUI.Controls;
using System;

namespace PolymindGames.Editor
{
    /// <summary>
    /// Abstract base class for managing tree views in the Unity editor. Provides methods for searching,
    /// expanding items, and handling selections.
    /// </summary>
    public abstract class CustomTreeView : TreeView
    {
        private readonly int[] _selectedCache = new int[1];

        protected CustomTreeView(TreeViewState state) : base(state)
        { }

        /// <summary>
        /// Iterates through all TreeViewItems in the TreeView hierarchy and calls a specified action on each.
        /// </summary>
        /// <param name="rootItem">The root item of the TreeView hierarchy.</param>
        /// <param name="action">The action to call on each TreeViewItem.</param>
        public static void ForEachItem(TreeViewItem rootItem, Action<TreeViewItem> action)
        {
            if (rootItem == null || action == null)
                return;

            // Perform the action on the current item
            action(rootItem);

            // Recursively call the method for each child
            if (rootItem.hasChildren)
            {
                foreach (var child in rootItem.children)
                {
                    ForEachItem(child, action);
                }
            }
        }
        
        /// <summary>
        /// Searches all items in the tree view that match the given criteria.
        /// </summary>
        /// <param name="criteria">A function to test each <see cref="TreeViewItem"/> against.</param>
        /// <param name="ignoreRoot">Should the root be ignored</param>
        /// <returns>The first item that matches the criteria, or <c>null</c> if no match is found.</returns>
        public TreeViewItem SearchAll(Func<TreeViewItem, bool> criteria)
        {
            return Search(rootItem, criteria);
        }

        /// <summary>
        /// Recursively searches for a <see cref="TreeViewItem"/> starting from the given item that matches the given criteria.
        /// </summary>
        /// <param name="item">The starting item to search from.</param>
        /// <param name="criteria">A function to test each <see cref="TreeViewItem"/> against.</param>
        /// <returns>The first item that matches the criteria, or <c>null</c> if no match is found.</returns>
        public TreeViewItem Search(TreeViewItem item, Func<TreeViewItem, bool> criteria)
        {
            if (item == null || criteria == null)
            {
                return null;
            }

            // Check if the current item matches the criteria
            if (criteria(item))
            {
                return item;
            }

            // Recursively search through children if they exist
            if (item.hasChildren)
            {
                foreach (var child in item.children)
                {
                    var result = Search(child, criteria);
                    if (result != null)
                    {
                        return result;
                    }
                }
            }

            return null;
        }

        public void SelectItem(TreeViewItem item, TreeViewSelectionOptions options = TreeViewSelectionOptions.FireSelectionChanged)
        {
            if (item == null)
            {
                _selectedCache[0] = -1;
                SetSelection(_selectedCache, options);
                return;
            }

            _selectedCache[0] = item.id;
            SetSelection(_selectedCache, options);
        }

        protected bool HasValidSelection()
        {
            var selectedIds = GetSelection();
            return selectedIds.Count > 0 && !(selectedIds.Count == 1 && selectedIds[0] == 0);
        }

        protected void EnsureValidSelection()
        {
            if (state.selectedIDs.Count == 0)
            {
                _selectedCache[0] = rootItem.hasChildren ? rootItem.children[0].id : -1;
                SetSelection(_selectedCache, TreeViewSelectionOptions.FireSelectionChanged);
            }
            else
            {
                state.selectedIDs.RemoveAll(id => FindItem(id, rootItem) == null);
                SetSelection(state.selectedIDs, TreeViewSelectionOptions.FireSelectionChanged | TreeViewSelectionOptions.RevealAndFrame);
            }
        }
    }
}