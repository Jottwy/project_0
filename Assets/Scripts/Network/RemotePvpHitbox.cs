using System.Threading;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-029 Fase 1+2: damage-handler shim for remote player proxies.
    /// STP weapons hit IDamageHandler on the struck collider; this handler reports a PvP
    /// candidate to the local backend and deliberately does not mutate health.
    /// </summary>
    /// <remarks>
    /// CRITICAL invariant (verified in <see cref="Install"/>): the same collider GameObjects
    /// on <c>RemotePlayerAvatar</c> (e.g. "UpperArm.R" and 10 others, inherited unmodified from
    /// the base <c>MTP_PlayerViewer.prefab</c>) ALREADY carry a prefab-authored
    /// <c>PolymindGames.CharacterHitbox</c> — STP's own <c>IDamageHandler</c> that forwards
    /// straight to <c>Character.HealthManager.ReceiveDamage</c> (REAL damage, can fire
    /// <c>HealthManager.Death</c> -> the proxy's dormant <c>CharacterDeathHandler</c> ->
    /// an unvalidated ragdoll). Since <c>Collider.TryGetComponent&lt;IDamageHandler&gt;()</c>
    /// resolves to whichever matching component exists FIRST on that GameObject, and
    /// <c>CharacterHitbox</c> is prefab-authored (present before this component is
    /// runtime-<c>AddComponent</c>'d), it would win the resolution and this shim would never
    /// run at all. <see cref="Install"/> destroys any other <c>IDamageHandler</c> on the same
    /// GameObject so this shim is the ONLY one STP can ever resolve there.
    /// </remarks>
    public sealed class RemotePvpHitbox : MonoBehaviour, IDamageHandler
    {
        private static long _nextRequestId;

        private int _victimId;
        private Transform _proxyRoot;

        public ICharacter Character => null;

        public void Configure(int victimId, Transform proxyRoot)
        {
            _victimId = victimId;
            _proxyRoot = proxyRoot;
        }

        public DamageResult HandleDamage(float damage, in DamageArgs args)
        {
            bool hasIpc = IPCClient.TryGetInstance(out var ipc);
            bool connected = hasIpc && ipc.IsConnected;
            int weaponId = ResolveHeldWeaponId();
            Debug.Log(
                $"MPTRACE step=PVP event=remote_pvp_hitbox_handle_damage victim_id={_victimId} " +
                $"damage={damage:F1} weapon_id={weaponId} connected={connected}");

            if (_victimId <= 0 || !connected)
                return DamageResult.Ignored;

            int attackerId = NetworkInitializer.Instance != null
                ? NetworkInitializer.Instance.LastSelectedNetId
                : 0;
            if (attackerId <= 0 || attackerId == _victimId)
                return DamageResult.Ignored;

            Vector3 hitPoint = args.HitPoint != default ? args.HitPoint : transform.position;
            Vector3 direction = ResolveDirection(args, hitPoint);
            if (direction.sqrMagnitude < 0.0001f)
                return DamageResult.Ignored;
            direction.Normalize();

            Vector3 origin = ResolveOrigin(args, hitPoint, direction);
            float candidateDamage = Mathf.Abs(damage);
            if (float.IsNaN(candidateDamage) || float.IsInfinity(candidateDamage) || candidateDamage <= 0f)
                return DamageResult.Ignored;

            long requestId = Interlocked.Increment(ref _nextRequestId);
            uint clientTick = unchecked((uint)Time.frameCount);

            bool sent = ipc.SendPvpHitCandidate(
                requestId,
                attackerId,
                _victimId,
                weaponId,
                candidateDamage,
                origin,
                direction,
                clientTick,
                hitPoint);

            if (sent)
            {
                Debug.Log(
                    $"MPTRACE step=PVP event=unity_pvp_hit_candidate_sent request_id={requestId} " +
                    $"attacker_id={attackerId} victim_id={_victimId} weapon_id={weaponId} " +
                    $"damage={candidateDamage:F1} hit=({hitPoint.x:F2},{hitPoint.y:F2},{hitPoint.z:F2})");
            }

            return DamageResult.Ignored;
        }

        public static void Install(GameObject proxyRoot, int victimId)
        {
            if (proxyRoot == null || victimId <= 0)
            {
                Debug.LogWarning(
                    $"MPTRACE step=PVP event=remote_pvp_hitbox_install_skipped victim_id={victimId} " +
                    $"root={(proxyRoot != null ? proxyRoot.name : "<null>")}");
                return;
            }

            var colliders = proxyRoot.GetComponentsInChildren<Collider>(true);
            int handlersAdded = 0;
            Debug.Log(
                $"MPTRACE step=PVP event=remote_pvp_hitbox_install_begin victim_id={victimId} " +
                $"root={proxyRoot.name} colliders_found={colliders.Length}");

            foreach (var collider in colliders)
            {
                if (collider == null)
                    continue;

                // CRITICAL (see class remarks): remove any OTHER IDamageHandler already on this
                // GameObject (STP's own CharacterHitbox is prefab-authored on these same limb
                // colliders) so this shim is the only one Collider.TryGetComponent can resolve.
                foreach (var existingHandler in collider.GetComponents<IDamageHandler>())
                {
                    if (existingHandler is RemotePvpHitbox)
                        continue;

                    var behaviour = existingHandler as MonoBehaviour;
                    string handlerType = behaviour != null ? behaviour.GetType().Name : existingHandler.GetType().Name;
                    Debug.Log(
                        $"MPTRACE step=PVP event=remote_pvp_hitbox_removed_competing_handler victim_id={victimId} " +
                        $"root={proxyRoot.name} collider={collider.gameObject.name} handler_type={handlerType}");
                    if (behaviour != null)
                        Object.Destroy(behaviour);
                }

                var hitbox = collider.GetComponent<RemotePvpHitbox>();
                bool added = false;
                if (hitbox == null)
                {
                    hitbox = collider.gameObject.AddComponent<RemotePvpHitbox>();
                    handlersAdded++;
                    added = true;
                }

                if (hitbox != null)
                    hitbox.Configure(victimId, proxyRoot.transform);

                string layerName = LayerMask.LayerToName(collider.gameObject.layer);
                if (string.IsNullOrEmpty(layerName))
                    layerName = collider.gameObject.layer.ToString();
                Debug.Log(
                    $"MPTRACE step=PVP event=remote_pvp_hitbox_collider victim_id={victimId} " +
                    $"root={proxyRoot.name} collider={collider.gameObject.name} layer={collider.gameObject.layer} " +
                    $"layer_name={layerName} trigger={collider.isTrigger} handler_present={hitbox != null} " +
                    $"handler_added={added}");
            }

            Debug.Log(
                $"MPTRACE step=PVP event=remote_pvp_hitbox_installed RemotePvpHitbox installed victim_id={victimId} " +
                $"root={proxyRoot.name} colliders_found={colliders.Length} handlers_added={handlersAdded}");
        }

        private static Vector3 ResolveDirection(in DamageArgs args, Vector3 hitPoint)
        {
            if (args.HitForce.sqrMagnitude > 0.0001f)
                return args.HitForce.normalized;

            var cam = Camera.main;
            if (cam != null)
                return cam.transform.forward;

            if (args.Source != null)
                return hitPoint - args.Source.transform.position;

            return Vector3.zero;
        }

        private static Vector3 ResolveOrigin(in DamageArgs args, Vector3 hitPoint, Vector3 direction)
        {
            var cam = Camera.main;
            if (cam != null)
                return cam.transform.position;

            if (args.Source != null)
                return args.Source.transform.position;

            return hitPoint - direction;
        }

        private static int ResolveHeldWeaponId()
        {
            if (!GameMode.HasInstance || GameMode.Instance.LocalPlayer == null)
                return 0;

            var character = GameMode.Instance.LocalPlayer;
            var wieldableInv = character.GetCC<IWieldableInventoryCC>();
            var holster = character.Inventory?.FindContainer(
                ItemContainerFilters.WithTag(ItemConstants.WieldableTag));

            if (wieldableInv == null || holster == null)
                return 0;

            int index = wieldableInv.SelectedIndex;
            if (index < 0 || index >= holster.SlotsCount)
                return 0;

            return holster.GetItemAtIndex(index).Item?.Id ?? 0;
        }
    }
}
