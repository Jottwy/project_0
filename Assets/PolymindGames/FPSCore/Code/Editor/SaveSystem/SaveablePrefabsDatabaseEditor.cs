using Toolbox.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.SaveSystem.Editor
{
    [CustomEditor(typeof(SaveableDatabase), true)]
    public class SaveablePrefabsDatabaseEditor : ToolboxEditor
    {
        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();
            
            EditorGUILayout.Space();
            if (GUILayout.Button("Reload Database"))
                ((SaveableDatabase)target).SetPrefabs_Editor(SaveableDatabase.FindAllSaveableObjectPrefabs());
        }
    }
}