#if UNITY_EDITOR
using PolymindGames;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration.EditorTools
{
    /// <summary>
    /// ADR-028 Fase C — builds <c>Resources/CorpseAvatar.prefab</c> as a VARIANT of the already-built
    /// <c>RemotePlayerAvatar.prefab</c> (itself a variant of the vendor MTP_PlayerViewer), trimmed for a
    /// STATIC corpse instead of a live networked proxy. Idempotent / re-runnable, same idiom as
    /// <see cref="RemoteAvatarPrefabBuilder"/> (disable, never delete, so the base's wiring/bone refs
    /// stay intact and inspectable).
    ///
    /// KEPT (inherited from RemotePlayerAvatar, untouched): Animator, SkinnedMeshRenderer,
    /// CharacterClothing (dresses the ragdoll — CorpseSpawner drives it once via SetClothing, ADR-022),
    /// CharacterRagdoll (already fully rigged with 11 bone Rigidbody+CapsuleCollider pairs on the
    /// MaleSurvivor skeleton — see ADR-028 Contexto; CorpseSpawner calls EnableRagdoll() once),
    /// ProxyControllerBinder (harmless — CharacterRagdoll.EnableRagdoll() disables the Animator anyway).
    ///
    /// DISABLED (pose-following / live-network hooks — a corpse has no RemotePlayerView, so these
    /// would either do nothing useful or actively fight the ragdoll):
    ///   - ProxyLocomotionFeeder, ProxyJumpFeeder: derive MovementSpeed/Jump from a moving root: a
    ///     corpse's root never moves.
    ///   - ProxyPickupHook, ProxyCrouchHook, ProxyPitchHook, ProxyHitReactionHook: all drive Animator
    ///     params/bone rotations that the ragdoll's disabled Animator no longer consumes.
    ///   - ProxyGroundingHook: offsets Hips to floor-snap a WALKING avatar; a settled ragdoll must not
    ///     be nudged by an unrelated raycast every LateUpdate.
    ///   - ProxyClothingHook, ProxyHeldItemHook: BOTH resolve their data via
    ///     <c>GetComponentInParent&lt;RemotePlayerManager&gt;()</c> + a live <c>RemotePlayerView</c>
    ///     (see their class docs) — a corpse is never parented under RemotePlayerManager and has no
    ///     view, so they would stay permanently inert even if left enabled. CorpseSpawner reuses their
    ///     UNDERLYING mechanism directly instead (CharacterClothing.SetClothing; a one-shot neutralized
    ///     held-item mesh under Hand.R) — "reuso ADR-022/023" means the technique, not the live hook.
    /// </summary>
    public static class CorpseAvatarPrefabBuilder
    {
        private const string BasePrefabPath = "Assets/_Migration/STPIntegration/Resources/RemotePlayerAvatar.prefab";
        private const string OutputDir = "Assets/_Migration/STPIntegration/Resources";
        private const string OutputPath = OutputDir + "/CorpseAvatar.prefab";

        [MenuItem("Backrooms/Build Corpse Avatar Prefab")]
        public static void Build()
        {
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BasePrefabPath);
            if (basePrefab == null)
            {
                Debug.LogError($"[CorpseAvatarPrefabBuilder] Base prefab not found: {BasePrefabPath}. " +
                    "Run 'Backrooms ▸ Build Remote Avatar Prefab' first.");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            try
            {
                DisableHook<ProxyLocomotionFeeder>(instance);
                DisableHook<ProxyJumpFeeder>(instance);
                DisableHook<ProxyPickupHook>(instance);
                DisableHook<ProxyCrouchHook>(instance);
                DisableHook<ProxyPitchHook>(instance);
                DisableHook<ProxyGroundingHook>(instance);
                DisableHook<ProxyHitReactionHook>(instance);
                DisableHook<ProxyClothingHook>(instance);
                DisableHook<ProxyHeldItemHook>(instance);

                var ragdoll = instance.GetComponentInChildren<CharacterRagdoll>(true);
                if (ragdoll == null)
                    Debug.LogError("[CorpseAvatarPrefabBuilder] No CharacterRagdoll found under the base — " +
                        "CorpseSpawner will not be able to EnableRagdoll(). Check RemotePlayerAvatar/MTP_PlayerViewer.");

                var clothing = instance.GetComponentInChildren<CharacterClothing>(true);
                if (clothing == null)
                    Debug.LogWarning("[CorpseAvatarPrefabBuilder] No CharacterClothing found under the base — " +
                        "the corpse will not be able to wear the death-loot equipment snapshot.");

                PrefabUtility.SaveAsPrefabAsset(instance, OutputPath, out bool ok);
                if (ok)
                {
                    AssetDatabase.SaveAssets();
                    Debug.Log($"[CorpseAvatarPrefabBuilder] Saved variant '{OutputPath}'. " +
                        "CorpseSpawner will Resources.Load it at runtime.");
                }
                else
                {
                    Debug.LogError("[CorpseAvatarPrefabBuilder] SaveAsPrefabAsset failed.");
                }
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private static void DisableHook<T>(GameObject root) where T : Behaviour
        {
            var hook = root.GetComponent<T>();
            if (hook != null)
                hook.enabled = false;
        }
    }
}
#endif
