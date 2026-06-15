#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration.EditorTools
{
    /// <summary>
    /// Builds <c>Resources/RemotePlayerAvatar.prefab</c> as a VARIANT of the vendor
    /// MTP_PlayerViewer, with gameplay-only components disabled so it is safe to spawn as a
    /// remote avatar. The vendor prefab is never modified — a variant only stores overrides.
    /// Idempotent / re-runnable.
    ///
    /// KEEP enabled: SkinnedMeshRenderer, Animator, CapsuleCollider, CharacterAnimator,
    /// CharacterClothing (the visible/animated mesh).
    /// DISABLE (not delete): Player, HealthManager, Inventory, CharacterDeathHandler,
    /// CharacterDamageHandler, CharacterAudioPlayer (local gameplay logic).
    ///
    /// Output lives under Resources so RemoteAvatarProvider can Resources.Load it at runtime
    /// (the vendor prefab is not under a Resources folder, and a self-bootstrapped component
    /// cannot hold an inspector reference).
    /// </summary>
    public static class RemoteAvatarPrefabBuilder
    {
        private const string BasePrefabPath =
            "Assets/PolymindGames/STP/Prefabs/Characters/MTP_PlayerViewer.prefab";
        private const string OutputDir = "Assets/_Migration/STPIntegration/Resources";
        private const string OutputPath = OutputDir + "/RemotePlayerAvatar.prefab";

        // Matched by Component.GetType().Name, so no compile-time dependency on STP types.
        private static readonly HashSet<string> DisableByTypeName = new HashSet<string>
        {
            "Player",
            "HealthManager",
            "Inventory",
            "CharacterDeathHandler",
            "CharacterDamageHandler",
            "CharacterAudioPlayer",
        };

        [MenuItem("Backrooms/Build Remote Avatar Prefab")]
        public static void Build()
        {
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BasePrefabPath);
            if (basePrefab == null)
            {
                Debug.LogError($"[RemoteAvatarPrefabBuilder] Base prefab not found: {BasePrefabPath}");
                return;
            }

            if (!Directory.Exists(OutputDir))
            {
                Directory.CreateDirectory(OutputDir);
                AssetDatabase.Refresh();
            }

            // Instantiating the base prefab and saving the instance produces a VARIANT.
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            try
            {
                var disabled = new List<string>();
                var kept = new List<string>();
                foreach (var comp in instance.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null)
                        continue;
                    string typeName = comp.GetType().Name;
                    if (comp is Behaviour b && DisableByTypeName.Contains(typeName))
                    {
                        b.enabled = false;
                        disabled.Add(typeName);
                    }
                }

                PrefabUtility.SaveAsPrefabAsset(instance, OutputPath, out bool ok);
                if (ok)
                {
                    AssetDatabase.SaveAssets();
                    Debug.Log($"[RemoteAvatarPrefabBuilder] Saved variant '{OutputPath}'. Disabled: " +
                              (disabled.Count > 0 ? string.Join(", ", disabled) : "<none of the listed types found>") +
                              ". RemoteAvatarProvider will Resources.Load it at runtime.");
                }
                else
                {
                    Debug.LogError("[RemoteAvatarPrefabBuilder] SaveAsPrefabAsset failed.");
                }
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }
    }
}
#endif
