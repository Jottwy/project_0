using PolymindGames.UserInterface;
using Toolbox.Editor;
using UnityEditor;

namespace PolymindGames.UISystem.Editor
{
    [CustomEditor(typeof(SelectableButton), true)]
    public sealed class SelectableUIEditor : ToolboxEditor
    {
        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            var selectableUI = (SelectableButton)target;
            var parent = selectableUI.transform.parent;

            if (parent == null || !parent.gameObject.TryGetComponent(out SelectableGroupBase _))
            {
                EditorGUILayout.HelpBox("No selectable group found on the parent of this object", MessageType.Info);
            }
        }
    }
}