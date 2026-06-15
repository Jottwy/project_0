using System;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    [CustomEditor(typeof(Character), true)]
    public class CharacterEditor : ToolboxEditor
    {
        private readonly GUILayoutOption[] _pingOptions = { GUILayout.Height(20f), GUILayout.Width(45f) };
        private ICharacterComponent[] _components;
        private bool _componentsEnabled;

        private const string FoldoutLabel = "<b>Components</b>";
        private const string ComponentsFoldoutKey = "Character.ComponentsEnabled";

        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            EditorGUILayout.Space();
            _componentsEnabled = EditorGUILayout.Foldout(_componentsEnabled, FoldoutLabel, true, GUIStyles.Foldout);

            if (!_componentsEnabled)
                return;
            
            GUILayout.BeginVertical(EditorStyles.helpBox);

            ToolboxEditorGui.DrawLine();
            _components ??= GetComponents();

            if (_components.Length == 0)
            {
                GUILayout.Label("This character has no components", EditorStyles.miniLabel);
                return;
            }

            foreach (var comp in _components)
            {
                DrawComponentLabel(comp);
                ToolboxEditorGui.DrawLine();
            }

            GUILayout.EndVertical();
        }

        private void OnEnable()
        {
            _componentsEnabled = SessionState.GetBool(ComponentsFoldoutKey, true);
        }

        private void OnDisable()
        {
            SessionState.SetBool(ComponentsFoldoutKey, _componentsEnabled);
        }

        private void DrawComponentLabel(ICharacterComponent component)
        {
            GUILayout.BeginHorizontal();
            {
                string componentName = ObjectNames.NicifyVariableName(component.GetType().Name);

                GUILayout.Label(componentName, EditorStyles.miniLabel);

                using (new BackgroundColorScope(GUIStyles.YellowColor))
                {
                    if (component is Component unityComponent &&
                        GUILayout.Button("Ping", GUIStyles.Button, _pingOptions))
                    {
                        EditorGUIUtility.PingObject(unityComponent);
                    }
                }
            }
            GUILayout.EndHorizontal();
        }

        private ICharacterComponent[] GetComponents()
        {
            var components = ((MonoBehaviour)target).GetComponentsInChildren<ICharacterComponent>();
            components ??= Array.Empty<ICharacterComponent>();
            return components;
        }
    }
}