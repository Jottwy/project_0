using Toolbox.Editor.Drawers;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.Editor
{
    public abstract class DataDefinitionEditor<T> : ToolboxEditor where T : DataDefinition
    {
        private IToolboxEditorDrawer _drawer;
        private bool _hasValidPath;

        public sealed override IToolboxEditorDrawer Drawer
            => _drawer ??= new ToolboxEditorDrawer(GetDrawingAction());

        protected T Definition { get; private set; }

        public override void DrawCustomInspector()
        {
            if (EditorWindow.HasOpenInstances<ToolsWindow>())
                DrawObjectField();
            else
                DrawToolsButton();

            var evt = Event.current;
            var validatable = Definition as IEditorValidate;
            if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.F5 && validatable != null)
                validatable.ValidateInEditor();

            base.DrawCustomInspector();
        }

        protected void DrawToolsButton()
        {
            GUILayout.BeginHorizontal();
            
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("  Open In Tools  ", GUIStyles.Button))
            {
                ToolsWindow.SelectPageForObject(target);
                Selection.activeObject = null;
            }

            GUILayout.EndHorizontal();
        }

        protected void DrawObjectField()
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Asset", target, target.GetType(), target);
        }

        protected void DrawCustomPropertySkipIgnore(string propertyPath)
        {
            var property = serializedObject.FindProperty(propertyPath);
            ToolboxEditorGui.DrawToolboxProperty(property);
        }

        protected virtual Action<SerializedProperty> GetDrawingAction()
            => ToolboxEditorGui.DrawToolboxProperty;

        protected virtual void OnEnable()
        {
            Definition = target as T;
        }
    }

    [CustomEditor(typeof(DataDefinition), true, isFallback = true)]
    public class DataDefinitionEditor : DataDefinitionEditor<DataDefinition>
    {
    }
}