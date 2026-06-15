using PolymindGames.Editor;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem.Editor
{
    public sealed class FirearmComponentPanelDrawer<T> : IDrawablePanel where T : FirearmComponentBehaviour
    {
        private readonly GUILayoutOption[] _pingOptions =
        {
            GUILayout.Height(22f), GUILayout.Width(22f * 2f)
        };
        
        private readonly string[] _componentContents;
        private readonly GUIContent _foldoutContent;
        private readonly string _headerName;
        private readonly T[] _components;
        private int _selectedIndex;
        private bool _enabled;

        public FirearmComponentPanelDrawer(Firearm firearm, FirearmComponentType componentType)
        {
            _components = firearm.gameObject.GetComponentsInChildren<T>(true) ?? Array.Empty<T>();
            _headerName = componentType.ToString().AddSpaceBeforeCapitalLetters();
            _foldoutContent = new GUIContent($"{_headerName} ({_components.Length})");

            _componentContents = new string[_components.Length];
            for (int i = 0; i < _components.Length; i++)
            {
                _componentContents[i] =
                    $" {_components[i].gameObject.name} ({_components[i].GetType().Name.Replace("Firearm", "").AddSpaceBeforeCapitalLetters()})";
            }

            _enabled = _components.Length > 1;
            _selectedIndex = GetSelectedIndex();
        }

        public void Draw(Rect rect)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(10f);

            GUILayout.BeginVertical();
            bool newFoldout = EditorGUILayout.Foldout(_enabled, _foldoutContent, true, GUIStyles.SmallFoldout);

            if (_enabled != newFoldout)
                Event.current.Use();

            _enabled = newFoldout || !HasComponents();

            if (_enabled)
            {
                GUILayout.Space(6f);

                if (HasComponents())
                {
                    DrawComponents();
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        $"No {_headerName} found, a null one will be used instead which could cause unexpected issues.",
                        MessageType.Warning);
                }
            }

            GUILayout.EndHorizontal();
            GUILayout.EndHorizontal();
        }

        private void DrawComponents()
        {
            for (int i = 0; i < _componentContents.Length; i++)
            {
                bool selected = DrawComponentContent(_selectedIndex == i, _componentContents[i], _components[i]);

                if (selected && i != _selectedIndex)
                {
                    if (!Application.isPlaying)
                    {
                        DetachComponent(_components[_selectedIndex]);
                        _selectedIndex = i;
                        AttachComponent(_components[i]);
                    }
                    else
                    {
                        _selectedIndex = i;
                        AttachComponent(_components[i]);
                    }

                    break;
                }
            }
        }

        private int GetSelectedIndex()
        {
            if (_components.Length == 0)
                return -1;

            int selectedIndex = -1;

            for (var i = 0; i < _components.Length; i++)
            {
                var component = _components[i];
                if (component.IsAttached)
                {
                    if (selectedIndex == -1)
                        selectedIndex = i;
                    else
                        DetachComponent(component);
                }
            }

            if (selectedIndex != -1)
                return selectedIndex;

            AttachComponent(_components[0]);
            return 0;
        }

        private bool HasComponents() => _components.Length > 0;

        private void AttachComponent(FirearmComponentBehaviour component)
        {
            component.Attach();
            if (!Application.isPlaying)
                EditorUtility.SetDirty(component);
        }

        private void DetachComponent(FirearmComponentBehaviour component)
        {
            component.Detach();
            if (!Application.isPlaying)
                EditorUtility.SetDirty(component);
        }

        public bool DrawComponentContent(bool isSelected, string content, T component)
        {
            GUILayout.BeginHorizontal();
            bool selected = GUILayout.Toggle(isSelected, content, GUIStyles.RadioButton);

            using (new BackgroundColorScope(GUIStyles.YellowColor))
            {
                if (GUILayout.Button("Ping", _pingOptions))
                    EditorGUIUtility.PingObject(component);
            }

            GUILayout.EndHorizontal();
            return selected;
        }
    }
}