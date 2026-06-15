using UnityEditor.ShortcutManagement;
using System.Collections.Generic;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEditor;
using System.Linq;
using System;

namespace PolymindGames.Editor
{
    public sealed class ToolsWindow : EditorWindow
    {
        [SerializeField]
        private TreeViewState _treeViewState;

        private const string TreeViewStateKey = "ToolsWindow.TreeViewState";

        private readonly GUILayoutOption[] _treeViewLayoutOptions =
            { GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true) };

        private readonly GUILayoutOption[] _treeWidthLayoutOption = { GUILayout.Width(200f) };

        private IEditorToolPage _selectedPage;
        private PagesTreeView _pagesTreeView;
        private SearchField _searchField;

        [MenuItem("Tools/Polymind Games/Tools", priority = 1000)]
        [Shortcut("Tools/Polymind Games/Tools", KeyCode.T, ShortcutModifiers.Control)]
        private static void Init() => GetOrCreateToolsWindow();

        public static void SelectPage(IEditorToolPage page)
        {
            if (page == null)
            {
                Debug.LogError("Provided page is null.");
                return;
            }

            var window = GetOrCreateToolsWindow();
            var foundItem =
                window._pagesTreeView.SearchAll(item => ((PageTreeViewItem)item).Page?.DisplayName == page.DisplayName);

            if (foundItem == null)
            {
                Debug.LogError($"Page not found. ({page.DisplayName}).");
                return;
            }

            window._pagesTreeView.SelectItem(foundItem,
                TreeViewSelectionOptions.FireSelectionChanged | TreeViewSelectionOptions.RevealAndFrame);
        }

        public static void SelectPageForObject(UnityEngine.Object unityObject)
        {
            if (unityObject == null)
            {
                Debug.LogError("Provided object is null.");
                return;
            }

            var window = GetOrCreateToolsWindow();
            var foundItem = window._pagesTreeView.SearchAll(item =>
                ((PageTreeViewItem)item).Page?.IsCompatibleWithObject(unityObject) ?? false);

            if (foundItem == null)
            {
                Debug.LogError($"Page not found for the provided Unity object ({unityObject.name}).");
                return;
            }

            window._pagesTreeView.SelectItem(foundItem,
                TreeViewSelectionOptions.FireSelectionChanged | TreeViewSelectionOptions.RevealAndFrame);
        }

        public static ToolsWindow GetOrCreateToolsWindow()
        {
            bool hasOpenInstances = HasOpenInstances<ToolsWindow>();
            var window = GetWindow<ToolsWindow>();

            if (!hasOpenInstances)
            {
                const float WindowWidth = 1100f;
                const float WindowHeight = 700f;
                float x = (Screen.currentResolution.width - WindowWidth) / 2f;
                float y = (Screen.currentResolution.height - WindowHeight) / 2f;
                window.position = new Rect(x, y, WindowWidth, WindowHeight);

                const float WindowMinWidth = 600f;
                const float WindowMinHeight = 400f;
                window.minSize = new Vector2(WindowMinWidth, WindowMinHeight);
            }

            window.titleContent = new GUIContent("Polymind Tools",
                Resources.Load<Texture2D>("Icons/Editor_PolymindLogoSmall"));

            return window;
        }

        private void OnEnable()
        {
            string stateJson = SessionState.GetString(TreeViewStateKey, null);
            _treeViewState = !string.IsNullOrEmpty(stateJson)
                ? JsonUtility.FromJson<TreeViewState>(stateJson)
                : new TreeViewState();

            _pagesTreeView = new PagesTreeView(_treeViewState);
            _searchField = new SearchField();
        }

        private void OnDisable()
        {
            SessionState.SetString(TreeViewStateKey, JsonUtility.ToJson(_treeViewState));
            _pagesTreeView.Dispose();
        }

        private void OnGUI()
        {
            using (new GUILayout.HorizontalScope())
            {
                // Draw the tree view in a vertical scope
                using (new GUILayout.VerticalScope(EditorStyles.helpBox, _treeWidthLayoutOption))
                {
                    GUILayout.Space(2f);

                    // Draw the search field for filtering items in the tree view
                    DrawSearchField();

                    // Handle input for navigation within the tree view
                    HandleInput();

                    // Render the tree view GUI with the calculated layout options
                    _pagesTreeView.OnGUI(GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, _treeViewLayoutOptions));
                }

                // Draw the content of the selected tree view item if one is focused
                DrawSelectedItemContent();
            }
        }

        /// <summary>
        /// Draws the content of the currently selected tree view item
        /// </summary>
        private void DrawSelectedItemContent()
        {
            using (new GUILayout.VerticalScope())
            {
                _pagesTreeView.SelectedItem?.Page?.DrawContent();
            }
        }

        /// <summary>
        /// Draws the search field for filtering tree view items
        /// </summary>
        private void DrawSearchField()
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Space(2f);
                _pagesTreeView.searchString = _searchField.OnToolbarGUI(_pagesTreeView.searchString); 
            }
        }

        /// <summary>
        /// Handles keyboard input for navigating between tree views
        /// </summary>
        private void HandleInput()
        {
            var evt = Event.current;
            if (evt.type == EventType.KeyDown && evt.shift)
            {
                switch (evt.keyCode)
                {
                    case KeyCode.LeftArrow:
                        if (!_pagesTreeView.HasFocus())
                        {
                            _pagesTreeView.SetFocus();
                            evt.Use();
                        }

                        break;
                    case KeyCode.RightArrow:
                        var selectedPage = _pagesTreeView.SelectedItem.Page;
                        if (!selectedPage.HasFocus())
                        {
                            selectedPage.SetFocus();
                            evt.Use();
                        }

                        break;
                }
            }
        }

        #region Internal Types
        private sealed class PageTreeViewItem : TreeViewItem
        {
            public readonly IEditorToolPage Page;

            public PageTreeViewItem(IEditorToolPage page, int id, int depth, string displayName) : base(id, depth,
                displayName)
            {
                Page = page;
            }
        }

        private sealed class PagesTreeView : CustomTreeView
        {
            private PageTreeViewItem _selectedItem;

            public PagesTreeView(TreeViewState state) : base(state)
            {
                showBorder = true;
                showAlternatingRowBackgrounds = true;

                Reload();
                EnsureValidSelection();
            }

            public PageTreeViewItem SelectedItem => _selectedItem;

            public void Dispose()
            {
                ForEachItem(rootItem, item => ((PageTreeViewItem)item).Page?.Dispose());
            }

            protected override void SelectionChanged(IList<int> selectedIds)
            {
                var item = FindItem(selectedIds[0], rootItem);
                _selectedItem = item as PageTreeViewItem;
                _selectedItem?.Page?.Refresh();
            }

            protected override TreeViewItem BuildRoot()
            {
                var root = new PageTreeViewItem(null, -1, -1, "Root");
                var pages = BuildPagesList();
                SetupParentsAndChildrenFromDepths(root, pages);
                return root;
            }

            protected override bool CanMultiSelect(TreeViewItem item) => false;

            private static IList<TreeViewItem> BuildPagesList()
            {
                var rootPages = InitializeRootPages();
                var pagesList = new List<PageTreeViewItem>(rootPages.Length * 4);

                foreach (var page in rootPages)
                    pagesList.AddRange(GenerateSubPages(page, 0));

                for (int i = 0; i < pagesList.Count; i++)
                    pagesList[i].id = i + 100;

                // Convert List<PageTreeViewItem> to IList<TreeViewItem>
                return pagesList.ConvertAll(item => (TreeViewItem)item);
            }

            private static IEditorToolPage[] InitializeRootPages()
            {
                var pageTypes = typeof(RootToolPage).Assembly.GetTypes()
                    .Where(type => !type.IsAbstract && type.IsSubclassOf(typeof(RootToolPage))).ToArray();

                var pages = new IEditorToolPage[pageTypes.Length];
                for (int i = 0; i < pages.Length; i++)
                    pages[i] = (IEditorToolPage)Activator.CreateInstance(pageTypes[i]);

                Array.Sort(pages);
                return pages;
            }

            private static IEnumerable<PageTreeViewItem> GenerateSubPages(IEditorToolPage toolPage, int depth)
            {
                yield return new PageTreeViewItem(toolPage, 0, depth, toolPage.DisplayName);
                foreach (var subPage in toolPage.GetSubPages().OrderBy(page => page.Order))
                {
                    // Recursively get sub-pages
                    foreach (var page in GenerateSubPages(subPage, depth + 1))
                    {
                        yield return page;
                    }
                }
            }
        }

        #endregion
    }
}