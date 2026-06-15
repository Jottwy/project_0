using JetBrains.Annotations;
using PolymindGames.Editor;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;
using System.IO;

namespace PolymindGames.Options.Editor
{
    [UsedImplicitly]
    [CustomEditor(typeof(UserOptions), true)]
    public class UserOptionsEditor : ToolboxEditor
    {
        public sealed override void DrawCustomInspector()
        {
            GUILayout.Label("Defaults", GUIStyles.LargeTitleLabel);

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (EditorWindow.HasOpenInstances<ToolsWindow>())
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField("Asset", target, target.GetType(), target);
                }
                
                DrawInspector();
            }

            EditorGUILayout.Space();
            DrawDeleteSettingsButton();
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

        private void DrawDeleteSettingsButton()
        {
            EditorGUILayout.HelpBox("Used as default values if no option save file is found on the user's machine.", MessageType.Info);
            string savePath = UserOptionsPersistence.GetSavePath(target.GetType());

            using (new EditorGUI.DisabledScope(!File.Exists(savePath)))
            {
                if (GUILayout.Button("Delete save file"))
                    File.Delete(savePath);
            }
        }

        protected virtual void DrawInspector() => base.DrawCustomInspector();
    }
}