using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Shared "find the LOCAL player's component" scan, extracted verbatim from the nine copies
    /// that lived in PlayerPoseTransmitter / AuthoritativePoseApplier / DeathLootReporter /
    /// InventoryReporter / InventoryRestorer / RespawnRequester / SilentHealthUIBridge /
    /// PhantomAttackHandler (x2) / CorpseLootSync.
    ///
    /// Semantics (unchanged — do NOT "improve"): scan ACTIVE objects only
    /// (<see cref="FindObjectsInactive.Exclude"/>, unsorted), skip anything living under a
    /// <see cref="RemotePlayerManager"/> hierarchy (a remote avatar) and return the FIRST
    /// survivor, or null when there is none.
    ///
    /// Caching stays with the caller on purpose: Unity's overloaded == reports a destroyed
    /// component as null, so a rig rebuild makes the caller's cached value read null and the
    /// caller re-runs this scan. This helper never caches — it would hide that revalidation.
    ///
    /// Static helpers on a plain (non-MonoBehaviour) class on purpose: every caller is a
    /// MonoBehaviour wired into prefabs/scenes by GUID and must not change type.
    /// </summary>
    public static class LocalPlayerLocator
    {
        /// <summary>
        /// First ACTIVE <typeparamref name="T"/> that is NOT under a RemotePlayerManager, or null.
        /// </summary>
        public static T Find<T>() where T : Component
        {
            var candidates = UnityEngine.Object.FindObjectsByType<T>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i].GetComponentInParent<RemotePlayerManager>() != null)
                    continue; // remote avatar, not the local player

                return candidates[i];
            }

            return null;
        }
    }
}
