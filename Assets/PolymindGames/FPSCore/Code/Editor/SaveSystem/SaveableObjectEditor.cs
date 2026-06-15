using PolymindGames.Editor;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.SaveSystem.Editor
{
    [CustomEditor(typeof(SaveableObject), true)]
    public sealed class SaveableObjectEditor : ToolboxEditor
    {
        private readonly GUILayoutOption[] _pingOptions = { GUILayout.Height(20f), GUILayout.Width(60f) };
        private ISaveableComponent[] _components;
        private bool _componentsEnabled;

        private const string FoldoutLabel = "<b>Components</b>";
        private const string ComponentsFoldoutKey = "SaveableObject.ComponentsEnabled";

        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            GUILayout.BeginVertical(EditorStyles.helpBox);
            {
                _componentsEnabled = EditorGUILayout.Foldout(_componentsEnabled, FoldoutLabel, true, GUIStyles.Foldout);

                if (_componentsEnabled)
                {
                    GUILayout.Label("Order of loading", EditorStyles.miniLabel);

                    ToolboxEditorGui.DrawLine();

                    _components ??= GetComponents();
                    if (_components.Length > 0)
                    {
                        foreach (var comp in _components)
                        {
                            DrawComponentLabel(comp);
                            ToolboxEditorGui.DrawLine();
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("No children implementing the ISaveableComponent interface found.",
                            MessageType.None);
                    }
                }
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

        private void DrawComponentLabel(ISaveableComponent component)
        {
            GUILayout.BeginHorizontal();

            string componentName = ObjectNames.NicifyVariableName(component.GetType().Name);
            GUILayout.Label(componentName, EditorStyles.miniLabel);

            using (new BackgroundColorScope(GUIStyles.YellowColor))
            {
                if (component is Component unityComponent && GUILayout.Button("Ping", GUIStyles.Button, _pingOptions))
                {
                    EditorGUIUtility.PingObject(unityComponent);
                }
            }

            GUILayout.EndHorizontal();
        }

        private ISaveableComponent[] GetComponents()
        {
            var components = ((MonoBehaviour)target).GetComponentsInChildren<ISaveableComponent>();
            components ??= Array.Empty<ISaveableComponent>();
            return components;
        }
    }
}