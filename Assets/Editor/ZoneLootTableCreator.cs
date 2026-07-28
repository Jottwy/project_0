#if UNITY_EDITOR
using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.Net;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Pieza 3 — creates Assets/Resources/Loot/ZoneLootTable.asset with the 12 first-pass
    /// zone-&gt;loot profiles (<see cref="ChunkLootRoll.DefaultZoneLootProfiles"/>), one per
    /// zone_kind. Run via "Backrooms ▸ Create Zone Loot Table" — required once before the profile
    /// table has any effect; until the asset exists, ChunkLootManager falls back to
    /// ZoneLootProfile.Default (today's pre-Pieza-3 behaviour) for every zone.
    ///
    /// GripPoseSet-style: crear-si-falta, NEVER re-seeds an existing asset (unlike
    /// BackroomsLayerVisualsCreator, which overwrites every field on re-run) — a hand-tuned
    /// rebalance in the Inspector survives re-running this menu.
    /// </summary>
    public static class ZoneLootTableCreator
    {
        private const string Folder = "Assets/Resources/Loot";
        private const string AssetPath = Folder + "/ZoneLootTable.asset";

        [MenuItem("Backrooms/Create Zone Loot Table")]
        public static void CreateIfMissing()
        {
            var existing = AssetDatabase.LoadAssetAtPath<ZoneLootTable>(AssetPath);
            if (existing != null)
            {
                Debug.Log($"[ZoneLootTableCreator] '{AssetPath}' already exists — left untouched " +
                          "(calibration preserved). Select it in the Inspector to rebalance.");
                Selection.activeObject = existing;
                return;
            }

            EnsureFolder("Assets/Resources");
            EnsureFolder(Folder);

            var table = ScriptableObject.CreateInstance<ZoneLootTable>();
            table.profiles = ChunkLootRoll.DefaultZoneLootProfiles();
            AssetDatabase.CreateAsset(table, AssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = table;
            Debug.Log($"[ZoneLootTableCreator] Created '{AssetPath}' with {table.profiles.Length} default zone profiles.");
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
        }
    }
}
#endif
