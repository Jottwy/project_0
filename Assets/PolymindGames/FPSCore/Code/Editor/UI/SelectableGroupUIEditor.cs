using PolymindGames.UserInterface;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.UISystem.Editor
{
    [CustomEditor(typeof(SelectableGroup), true)]
    public class SelectableGroupUIEditor : ToolboxEditor
    {
        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            EditorGUILayout.Space();
            GUILayout.BeginVertical(EditorStyles.helpBox);
            {
                var group = (SelectableGroupBase)serializedObject.targetObject;
                GUILayout.Label("Info", EditorStyles.boldLabel);

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("Selected", group.Selected, typeof(SelectableButton), group.Selected);
                }
            }
            GUILayout.EndVertical();
        }
    }
}
