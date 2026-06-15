using UnityEditor;

namespace PolymindGames.Editor
{
    [CustomEditor(typeof(GroupDefinition<,>), true)]
    public class DataDefinitionGroupEditor : DataDefinitionEditor
    {
        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            if (!EditorWindow.HasOpenInstances<ToolsWindow>())
                DrawCustomPropertySkipIgnore("_members");
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            IgnoreProperty("_members");
        }
    }
}