using Toolbox.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.WieldableSystem.Editor
{
    [CustomEditor(typeof(WieldablesController))]
    public sealed class WieldableControllerEditor : ToolboxEditor
    {
        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            var controller = (WieldablesController)target;
            var activeWieldable = controller.ActiveWieldable != null && controller.ActiveWieldable is Object ? (MonoBehaviour)controller.ActiveWieldable : null;

            using (new EditorGUI.DisabledScope(true))
            {
                ToolboxEditorGui.DrawLine();
                EditorGUILayout.EnumPopup("State", controller.State);
                EditorGUILayout.ObjectField("Active Wieldable", activeWieldable, typeof(MonoBehaviour), activeWieldable);
            }
        }
    }
}
