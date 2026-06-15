using UnityEditor.IMGUI.Controls;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.Editor
{
    public abstract class DataDefinitionPage : ToolPage
    { }

    public abstract class GenericDataDefinitionPage<T> : DataDefinitionPage where T : DataDefinition<T>
    {
        private readonly GUILayoutOption[] _treeViewLayoutOptions =
        {
            GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true)
        };

        private readonly GUILayoutOption[] _treeWidthLayoutOption =
        {
            GUILayout.Width(225f)
        };
        
        private readonly Lazy<DataDefinitionTreeView<T>> _content;
        private readonly SearchField _searchField;

        private const string TreeViewStateKey = "GenericDataDefinitionsPage.TreeViewState";

        protected GenericDataDefinitionPage()
        {
            string stateJson = SessionState.GetString(TreeViewStateKey + DisplayName, null);
            var treeViewState = !string.IsNullOrEmpty(stateJson)
                ? JsonUtility.FromJson<TreeViewState>(stateJson)
                : new TreeViewState();
            
            _content = new Lazy<DataDefinitionTreeView<T>>(() => new DataDefinitionTreeView<T>(treeViewState));
            _searchField = new SearchField();
        }

        public override void DrawContent()
        {
            var treeView = _content.Value;
            
            // Tree view section
            using (new GUILayout.HorizontalScope())
            {
                using (new GUILayout.VerticalScope(_treeWidthLayoutOption))
                {
                    GUILayout.Space(2f);
                    GUILayout.Label(DisplayName, GUIStyles.Title);

                    using (new GUILayout.VerticalScope(EditorStyles.helpBox, _treeViewLayoutOptions))
                    {
                        // Search field
                        GUILayout.Space(2f);

                        GUILayout.BeginHorizontal();
                        GUILayout.Space(2f);
                        treeView.searchString = _searchField.OnToolbarGUI(treeView.searchString);
                        GUILayout.EndHorizontal();

                        // Draw tree view
                        treeView.OnGUI(GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                            _treeViewLayoutOptions));
                    }
                }

                // Main content view
                GUILayout.BeginVertical();
                treeView.DrawObjectInspector();
                GUILayout.EndVertical();
            }
        }

        public override void Dispose()
        {
            if (_content.IsValueCreated)
            {
                SessionState.SetString(TreeViewStateKey + DisplayName, JsonUtility.ToJson(_content.Value.state));
            }
        }

        public override bool HasFocus()
        {
            return _content.Value.HasFocus();
        }

        public override void SetFocus()
        {
            _content.Value.SetFocus();
            Event.current.Use();
        }

        public override void Refresh()
        {
            _content.Value.Reload();
        }

        public override bool IsCompatibleWithObject(UnityEngine.Object unityObject)
        {
            if (unityObject is T definition)
            {
                var foundItem = _content.Value.SearchAll(item => item is ObjectTreeViewItem castedItem && castedItem.UnityObject == definition);
                _content.Value.SelectItem(foundItem, TreeViewSelectionOptions.RevealAndFrame | TreeViewSelectionOptions.FireSelectionChanged);
                return true;
            }

            return false;
        }
    }

    public abstract class GenericDataDefinitionGroupPage<Group, Member> : DataDefinitionPage
        where Group : GroupDefinition<Group, Member>
        where Member : GroupMemberDefinition<Member, Group>
    {
        private readonly GUILayoutOption[] _treeViewLayoutOptions =
        {
            GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true)
        };

        private readonly GUILayoutOption[] _treeWidthLayoutOption =
        {
            GUILayout.Width(225f)
        };
        private readonly Lazy<DataDefinitionGroupTreeView<Group, Member>> _content;
        private readonly SearchField _searchField;

        private const string TreeViewStateKey = "GenericDataDefinitionsPage.TreeViewState";

        protected GenericDataDefinitionGroupPage()
        {
            string stateJson = SessionState.GetString(TreeViewStateKey + DisplayName, null);
            var treeViewState = !string.IsNullOrEmpty(stateJson)
                ? JsonUtility.FromJson<TreeViewState>(stateJson)
                : new TreeViewState();

            _content = new Lazy<DataDefinitionGroupTreeView<Group, Member>>(() =>
                new DataDefinitionGroupTreeView<Group, Member>(treeViewState));
            _searchField = new SearchField();
        }

        public override void DrawContent()
        {
            var treeView = _content.Value;

            using (new GUILayout.HorizontalScope())
            {
                using (new GUILayout.VerticalScope(_treeWidthLayoutOption))
                {
                    GUILayout.Space(2f);
                    GUILayout.Label(DisplayName, GUIStyles.Title);

                    using (new GUILayout.VerticalScope(EditorStyles.helpBox, _treeViewLayoutOptions))
                    {
                        // Search field
                        GUILayout.Space(2f);

                        GUILayout.BeginHorizontal();
                        GUILayout.Space(2f);
                        treeView.searchString = _searchField.OnToolbarGUI(treeView.searchString);
                        GUILayout.EndHorizontal();

                        // Draw tree view
                        treeView.OnGUI(GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                            _treeViewLayoutOptions));
                    }
                }

                // Main content view
                GUILayout.BeginVertical();
                treeView.DrawObjectInspector();
                GUILayout.EndVertical();
            }
        }

        public override void Dispose()
        {
            if (_content.IsValueCreated)
            {
                SessionState.SetString(TreeViewStateKey + DisplayName, JsonUtility.ToJson(_content.Value.state));
            }
        }

        public override bool HasFocus()
        {
            return _content.Value.HasFocus();
        }

        public override void SetFocus()
        {
            _content.Value.SetFocus();
            Event.current.Use();
        }

        public override void Refresh()
        {
            _content.Value.Reload();
        }

        public override bool IsCompatibleWithObject(UnityEngine.Object unityObject)
        {
            if (unityObject is Group or Member)
            {
                var definition = (DataDefinition)unityObject;
                var foundItem = _content.Value.SearchAll(item => item is ObjectTreeViewItem castedItem && castedItem.UnityObject == definition);
                _content.Value.SelectItem(foundItem, TreeViewSelectionOptions.RevealAndFrame | TreeViewSelectionOptions.FireSelectionChanged);
                return true;
            }

            return false;
        }
    }
}