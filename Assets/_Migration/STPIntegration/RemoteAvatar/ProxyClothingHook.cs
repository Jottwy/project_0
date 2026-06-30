using BackroomsSurvival.Net;
using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-022: drives this proxy's STP <see cref="CharacterClothing"/> from the networked equipment
    /// IDs, so a remote player's worn clothing (Head/Torso/Legs/Feet) shows on their 3P avatar. The
    /// proxy's MTP_PlayerViewer already ships a CharacterClothing with the full pre-placed wardrobe;
    /// we only feed it the 4 item IDs and it toggles the right SkinnedMeshRenderers (+ body opacity
    /// masks). Items outside the wardrobe degrade gracefully to an empty slot (no crash).
    ///
    /// Change-detected per slot (SetClothing only fires when an ID changes), so per-frame cost is zero
    /// once dressed. Reads the equipment array from the RemotePlayerManager view whose root is this
    /// GameObject — same lookup as <see cref="ProxyCrouchHook"/>. Attach to the avatar root.
    ///
    /// IMPORTANT: the builder (RemoteAvatarPrefabBuilder.WireClothingHook) sets the CharacterClothing's
    /// <c>_attachToCharacter = false</c> so it never tries to bind to the proxy's disabled inventory —
    /// a null equipment container in AttachToCharacter would NRE silently. We drive
    /// <c>SetClothing(BodyPoint, id)</c> directly; it does the visual swap without touching any
    /// inventory container.
    ///
    /// Removable: delete the file and the proxy wears the prefab's default clothing; nothing else breaks.
    /// </summary>
    public sealed class ProxyClothingHook : MonoBehaviour
    {
        private const int SlotCount = 4;

        // Protocol order of the networked equipment array → STP BodyPoint.
        private static readonly BodyPoint[] SlotBodyPoints =
        {
            BodyPoint.Head, BodyPoint.Torso, BodyPoint.Legs, BodyPoint.Feet
        };

        // Sentinel that never equals a real item ID (0 = empty is a valid value), so the first
        // observed value always applies — including a peer who joins wearing nothing (all zeros).
        private const int Unset = int.MinValue;

        private CharacterClothing _clothing;
        private RemotePlayerManager _manager;
        private readonly int[] _applied = new int[SlotCount];

        private void Awake()
        {
            _clothing = GetComponentInChildren<CharacterClothing>(true);
            ResetApplied();
        }

        // Re-arm for pool reuse: force a re-apply against the fresh peer's equipment.
        private void OnEnable() => ResetApplied();

        private void Update()
        {
            if (_clothing == null)
                return;

            var equipment = ResolveEquipment();
            if (equipment == null)
                return;

            for (int i = 0; i < SlotCount; i++)
            {
                int id = i < equipment.Length ? equipment[i] : 0;
                if (id == _applied[i])
                    continue;

                _clothing.SetClothing(SlotBodyPoints[i], id);
                _applied[i] = id;
            }
        }

        private void ResetApplied()
        {
            for (int i = 0; i < SlotCount; i++)
                _applied[i] = Unset;
        }

        /// <summary>This proxy's networked equipment array, via the RemotePlayerManager view whose root is us.</summary>
        private int[] ResolveEquipment()
        {
            if (_manager == null)
                _manager = GetComponentInParent<RemotePlayerManager>();
            if (_manager == null)
                return null;

            foreach (var kvp in _manager.ActivePlayers)
            {
                var view = kvp.Value;
                if (view != null && view.root == transform)
                    return view.equipment;
            }
            return null;
        }
    }
}
