using PolymindGames.UserInterface;
using PolymindGames.Editor;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.UISystem.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(UIPanel), true)]
    public class PanelUIEditor : ToolboxEditor
    {
        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            ToolboxEditorGui.DrawLine();

            if (serializedObject.targetObjects.Length <= 1)
            {
                GUILayout.BeginVertical(GUIStyles.Box);
                {
                    var panel = (UIPanel)serializedObject.targetObject;
                    GUILayout.Label("Info", EditorStyles.boldLabel);
                    GUILayout.Label($"Is Active: {panel.IsActive}", GUIStyles.BoldMiniGreyLabel);
                    GUILayout.Label($"Is Visible: {panel.IsVisible}", GUIStyles.BoldMiniGreyLabel);

                    GUILayout.BeginHorizontal();

                    bool hasEvents = panel.gameObject.HasComponent<UIPanelEvents>();
                    GUILayout.Label($"Has Events: {hasEvents}", GUIStyles.BoldMiniGreyLabel);

                    var btnStr = !hasEvents ? "Add Events Component" : "Remove Events Component";
                    if (GUILayout.Button(btnStr))
                        AddEventsComponent(panel, !hasEvents);

                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();
            }

            GUILayout.BeginHorizontal(GUIStyles.Box);
            if (GUILayout.Button("Show"))
            {
                foreach (var panels in serializedObject.targetObjects)
                    ShowPanel((UIPanel)panels, true);
            }

            if (GUILayout.Button("Hide"))
            {
                foreach (var panels in serializedObject.targetObjects)
                    ShowPanel((UIPanel)panels, false);
            }
            GUILayout.EndHorizontal();
        }

        private static void AddEventsComponent(UIPanel panel, bool add)
        {
            if (add)
            {
                Undo.AddComponent<UIPanelEvents>(panel.gameObject);
            }
            else
            {
                var events = panel.GetComponent<UIPanelEvents>();
                Undo.DestroyObjectImmediate(events);
            }
        }

        private static void ShowPanel(UIPanel panel, bool show)
        {
            if (Application.isPlaying)
            {
                if (show) panel.Show();
                else panel.Hide();
            }
            else if (panel.TryGetComponent<CanvasGroup>(out var canvasGroup))
            {
                canvasGroup.alpha = show ? 1f : 0f;
                canvasGroup.blocksRaycasts = show;
                canvasGroup.interactable = show;
                EditorUtility.SetDirty(canvasGroup);
            }
        }
    }
}