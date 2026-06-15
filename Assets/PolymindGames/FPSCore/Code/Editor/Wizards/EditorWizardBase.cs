using Toolbox.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    public abstract class EditorWizardBase : ScriptableObject
    {
        public abstract string ValidateSettings();
        public abstract void CreateAsset();

        public virtual void Reset()
        {
        }

        public virtual void Update()
        {
        }
    }

    [CustomEditor(typeof(EditorWizardBase), true)]
    public class EditorWizardBaseEditor : ToolboxEditor
    {
        private EditorWizardBase _wizard;

        public override void DrawCustomInspector()
        {
            DrawBaseInspector();
            GUILayout.FlexibleSpace();
            DrawResetAndCreateButtons();
        }

        protected void DrawBaseInspector()
        {
            base.DrawCustomInspector();
            if (Event.current.type == EventType.Repaint)
                _wizard.Update();
        }

        protected void DrawResetAndCreateButtons()
        {
            string validationError = _wizard.ValidateSettings();
            bool hasValidationError = !string.IsNullOrEmpty(validationError);

            if (hasValidationError)
                EditorGUILayout.HelpBox(validationError, MessageType.Error);

            using (new EditorGUI.DisabledScope(hasValidationError))
            {
                GUILayout.BeginHorizontal();
                
                if (GUILayout.Button("Reset"))
                {
                    _wizard.Reset();
                }

                if (GUILayout.Button("Create"))
                {
                    _wizard.CreateAsset();
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                }

                GUILayout.EndHorizontal();
            }
        }

        private void OnEnable()
        {
            _wizard = target as EditorWizardBase;
        }
    }
}