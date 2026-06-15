using PolymindGames.UserInterface;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.UISystem.Editor
{
    [CustomEditor(typeof(WorkstationInspectControllerUI))]
    public sealed class WorkstationInspectControllerUIEditor : ToolboxEditor
    {
        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            ToolboxEditorGui.DrawLine();

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Show All Inspectable Panels"))
            {
                ShowAllChildPanels(true);
                return;
            }

            if (GUILayout.Button("Hide All Inspectable Panels"))
                ShowAllChildPanels(false);

            EditorGUILayout.EndHorizontal();
        }

        private void ShowAllChildPanels(bool show)
        {
            var obj = ((Component)target).gameObject;

            if (obj == null)
                return;

            var panels = obj.GetComponentsInChildren<UIPanel>();

            foreach (var panel in panels)
            {
                panel.GetComponentInChildren<CanvasGroup>().alpha = show ? 1f : 0f;
                panel.GetComponentInChildren<CanvasGroup>().blocksRaycasts = show;
            }
        }
    }
}