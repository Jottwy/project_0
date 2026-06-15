#if UNITY_EDITOR
using BackroomsSurvival.Gameplay.Chunks;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>Menu entries for the Chunk Editor tooling.</summary>
    public static class BackroomsEditorMenu
    {
        // Note: "Backrooms/Chunk Editor" is owned by BackroomsChunkEditor.OpenWindow
        // (a single [MenuItem] — duplicating it here would be a Unity error).

        [MenuItem("Backrooms/Create Chunk Template")]
        public static void CreateNewTemplate()
        {
            var template = ScriptableObject.CreateInstance<ChunkTemplate>();
            template.InitializeDefaultLayers();
            template.InitializeDefaultWalls();

            string path = EditorUtility.SaveFilePanelInProject("Save Chunk Template", "NewChunk", "asset", "");
            if (string.IsNullOrEmpty(path))
            {
                Object.DestroyImmediate(template);
                return;
            }

            AssetDatabase.CreateAsset(template, path);
            AssetDatabase.SaveAssets();
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = template;
        }
    }
}
#endif
