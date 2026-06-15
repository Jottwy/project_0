using System.Collections.Generic;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    public abstract class FoldoutBaseTypeEditor<T> : ToolboxEditor where T : Object
    {
        private List<string> _baseTypeFieldNames;
        private List<string> _subTypeFieldNames;

        private bool _baseTypeEnabled;
        private bool _subTypeEnabled;

        private GUIContent _baseTypeContent;
        private GUIContent _subTypeContent;

        public override void DrawCustomInspector()
        {
            // If the target type is the base type, draw the default inspector.
            if (!IsDerivedType())
            {
                base.DrawCustomInspector();
                return;
            }

            GUILayout.BeginVertical(EditorStyles.helpBox);
            {
                // Draw the base type foldout.
                _baseTypeEnabled = EditorGUILayout.Foldout(_baseTypeEnabled, _baseTypeContent, true, GUIStyles.Foldout);
                if (_baseTypeEnabled)
                {
                    EditorGUI.indentLevel++;
                    DrawBaseTypeInspector();
                    EditorGUI.indentLevel--;
                }
            }
            GUILayout.EndVertical();

            GUILayout.BeginVertical(EditorStyles.helpBox);
            {
                // Draw the sub type foldout.
                _subTypeEnabled = EditorGUILayout.Foldout(_subTypeEnabled, _subTypeContent, true, GUIStyles.Foldout);
                
                if (_subTypeEnabled)
                {
                    EditorGUI.indentLevel++;
                    DrawSubTypeInspector();
                    EditorGUI.indentLevel--;
                }
            }
            GUILayout.EndVertical();
        }

        protected virtual void DrawBaseTypeInspector() => DrawInspectorFields(_baseTypeFieldNames);
        protected virtual void DrawSubTypeInspector() => DrawInspectorFields(_subTypeFieldNames);

        protected virtual void OnEnable()
        {
            if (!IsDerivedType())
                return;

            _baseTypeContent = new GUIContent($"Settings ({ObjectNames.NicifyVariableName(typeof(T).Name)})");
            _subTypeContent = new GUIContent($"Settings ({ObjectNames.NicifyVariableName(target.GetType().Name)})");

            // Get serialized field names for the base and sub types.
            _baseTypeFieldNames = typeof(T).GetSerializedFieldNames();
            _subTypeFieldNames = target.GetType().GetSerializedFieldNames();

            // Remove base type fields from the sub type list to avoid duplication.
            _subTypeFieldNames.RemoveAll(field => _baseTypeFieldNames.Contains(field));

            _baseTypeEnabled = SessionState.GetBool(_baseTypeContent.text, false);
            _subTypeEnabled = SessionState.GetBool(_subTypeContent.text, true);
        }

        protected virtual void OnDisable()
        {
            SessionState.SetBool(_baseTypeContent.text, _baseTypeEnabled);
            SessionState.SetBool(_subTypeContent.text, _subTypeEnabled);
        }

        /// <summary>
        /// Draws serialized properties based on the provided field names.
        /// </summary>
        /// <param name="fieldNames">A list of field names to draw.</param>
        private void DrawInspectorFields(List<string> fieldNames)
        {
            serializedObject.Update();
            foreach (var fieldName in fieldNames)
            {
                var property = serializedObject.FindProperty(fieldName);
                ToolboxEditorGui.DrawToolboxProperty(property);
            }
            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Checks if the current type is derived from the base type.
        /// </summary>
        private bool IsDerivedType() => target.GetType() != typeof(T);
    }
}