using Toolbox.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    [CustomEditor(typeof(Manager), true)]
    public class ManagerEditor : ToolboxEditor
    {
        public override void DrawCustomInspector()
        {
            if (EditorWindow.HasOpenInstances<ToolsWindow>())
                DrawObjectField();
            
            base.DrawCustomInspector();
        }

        protected void DrawObjectField()
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Asset", target, target.GetType(), target);
        }

        protected override void OnHeaderGUI()
        {
            base.OnHeaderGUI();

            if (!EditorWindow.HasOpenInstances<ToolsWindow>())
            {
                Rect headerRect = EditorGUILayout.GetControlRect(false, 35);
                Rect buttonRect = new Rect(headerRect.x + headerRect.width - 100, headerRect.y, 100, 20);

                using (new BackgroundColorScope(GUIStyles.BlueColor))
                {
                    if (GUI.Button(buttonRect, "Open In Tools"))
                    {
                        ToolsWindow.SelectPageForObject(target);
                        Selection.activeObject = null;
                    }
                }
            }
        }
    }
}