#if UNITY_EDITOR
using System.IO;
using BackroomsSurvival.Gameplay.Chunks;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>Serialises a <see cref="ChunkTemplate"/> to a JSON file under Assets/Chunks/.</summary>
    public static class ChunkJsonExporter
    {
        private const string OutputDir = "Assets/Chunks";

        public static void ExportToJson(ChunkTemplate template)
        {
            if (template == null) return;

            string fileName = string.IsNullOrEmpty(template.name) ? "UnnamedChunk" : template.name;
            string json = JsonUtility.ToJson(template, true);

            Directory.CreateDirectory(OutputDir);
            string path = $"{OutputDir}/{fileName}.json";
            File.WriteAllText(path, json);

            AssetDatabase.Refresh();
            Debug.Log($"[ChunkJsonExporter] Exported to {path}");
        }
    }
}
#endif
