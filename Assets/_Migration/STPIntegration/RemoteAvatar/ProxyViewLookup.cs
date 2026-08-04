using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// The one lookup every Proxy*Hook in this folder does: find the <see cref="RemotePlayerView"/>
    /// that <see cref="RemotePlayerManager"/> has bound to THIS proxy root.
    ///
    /// Extracted verbatim from the sixteen hand-copied versions of it (crouch, pitch, stance, reveal,
    /// light, held item, carry, clothing, pickup, hit, melee, footstep, the two audio hooks and the
    /// two lookups in the vocal hook). Same three steps, same order:
    ///   1. resolve the manager lazily and CACHE it in the CALLER'S OWN field — the cache stays with
    ///      the hook, so a hook that gets deleted takes its cache with it and the others are unaffected
    ///      (the hooks are removable by design);
    ///   2. no manager at all (a bare test scene) =&gt; false, never an exception;
    ///   3. linear scan of ActivePlayers for the view whose <c>root</c> is this proxy.
    ///
    /// Deliberately a plain static class and NOT a shared base MonoBehaviour: the hooks are wired into
    /// the avatar prefab by GUID, so their types must not change. Each hook keeps its own resolver
    /// method, its own return type and its own fallback value — only the lookup itself lives here.
    /// </summary>
    internal static class ProxyViewLookup
    {
        /// <summary>This proxy's networked view, via the RemotePlayerManager whose child we are.</summary>
        /// <param name="proxyRoot">The hook's own <c>transform</c>; the view's <c>root</c> must match it.</param>
        /// <param name="manager">The caller's cached manager field, filled in on first use.</param>
        /// <param name="view">The matching view, or null when there is none.</param>
        /// <returns>True if a view for this proxy is currently active.</returns>
        public static bool TryResolve(Transform proxyRoot, ref RemotePlayerManager manager,
            out RemotePlayerView view)
        {
            view = null;
            if (manager == null)
                manager = proxyRoot.GetComponentInParent<RemotePlayerManager>();
            if (manager == null)
                return false;

            foreach (var kvp in manager.ActivePlayers)
            {
                var v = kvp.Value;
                if (v != null && v.root == proxyRoot)
                {
                    view = v;
                    return true;
                }
            }
            return false;
        }
    }
}
