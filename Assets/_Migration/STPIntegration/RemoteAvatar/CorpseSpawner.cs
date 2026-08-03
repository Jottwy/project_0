using System.Collections.Generic;
using System.Reflection;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-028 Fase C/D — spawns/destroys a local, cosmetic corpse ragdoll to match
    /// world_state.visible_corpses, keyed by corpse id. Mirrors the reconcile pattern of
    /// StpItemReplicator (presence-based spawn/destroy, no interpolation — a corpse never moves
    /// on the wire) rather than RemotePlayerManager (which pools/interpolates a LIVE pose stream).
    ///
    /// Lives here (Assembly-CSharp, not BackroomsSurvival) because it depends on GripPoseSet
    /// (same assembly as ProxyHeldItemHook) — a script under Scripts/Network cannot reference it
    /// (BackroomsSurvival is an explicit asmdef; it cannot see the implicit Assembly-CSharp, the
    /// same one-way constraint documented for PolymindGames — see STATE.md "NO tocar").
    ///
    /// The spawned instance is PURELY PRESENTATION: the loot interaction point is the
    /// server-frozen corpse.position (ADR-028 decision 1), which this component never re-reads or
    /// feeds back — dressing + EnableRagdoll() happen ONCE at spawn, then Unity's local physics
    /// (against THIS client's rendered geometry) settles the ragdoll independently per client. No
    /// determinism is required or attempted between clients; only the (fixed, replicated) loot
    /// anchor matters for gameplay.
    ///
    /// Fase D (loot UI) — REUSES StorageStationUI, the SAME dual-inventory widget already used for
    /// storage crates, per ADR-028 scope ("no construir UI nueva"). Discovered while implementing:
    /// StorageStationUI (WorkstationInspectorBaseUI&lt;StorageStation&gt;) is generically hard-bound
    /// to the CONCRETE, SEALED StorageStation class — the inspection dispatcher matches on that
    /// exact type, so a hand-rolled IWorkstation implementation (as originally sketched in the ADR
    /// text) would never be routed to it; sealed rules out subclassing too. The only way to
    /// genuinely reuse the widget is to attach a REAL StorageStation component (the same one chests
    /// use) and feed it the corpse's container via reflection on its private `_containers` field —
    /// same vendor-field idiom already used elsewhere in this project (RespawnRequester/
    /// SilentHealthUIBridge read private DeathUI/PlayerHealthUI fields the same way). `Workstation`
    /// itself carries NO crafting semantics (audio + an interact-to-inspect hookup only) — the
    /// crafting-specific logic lives in a sibling class, not here — so this does not violate the
    /// "no crafting baggage" intent, it just uses the literal existing storage widget instead of
    /// duplicating its wiring.
    ///
    /// Self-bootstraps; fully removable (delete the file — corpses simply never render; the
    /// backend/loot state is unaffected, this component never mutates game state).
    /// </summary>
    public sealed class CorpseSpawner : MonoBehaviour
    {
        private static CorpseSpawner _instance;

        private const string ResourcePath = "CorpseAvatar";
        private const string HandBoneName = "Hand.R";

        // Protocol order of the networked equipment array → STP BodyPoint (mirrors ProxyClothingHook).
        private static readonly BodyPoint[] SlotBodyPoints =
        {
            BodyPoint.Head, BodyPoint.Torso, BodyPoint.Legs, BodyPoint.Feet
        };

        private readonly Dictionary<uint, GameObject> _spawned = new Dictionary<uint, GameObject>();
        private GameObject _template;
        private bool _templateResolved;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[CorpseSpawner]");
            _instance = go.AddComponent<CorpseSpawner>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
        }

        private void LateUpdate()
        {
            if (!IPCClient.TryGetInstance(out var ipc))
                return;

            var state = ipc.LatestState;
            if (state == null)
                return;

            var alive = new HashSet<uint>();
            foreach (var corpse in state.visibleCorpses)
            {
                alive.Add(corpse.id);
                // Re-spawn if missing (first sight) or destroyed (e.g. scene reload) — mirrors
                // StpItemReplicator. Position never changes after spawn; CONTENTS can (ADR-028
                // Fase E2: another peer looting this corpse arrives only through this roster
                // view), so existing corpses get a content reconcile pass instead.
                if (!_spawned.TryGetValue(corpse.id, out var existing) || existing == null)
                {
                    var spawned = SpawnCorpse(corpse);
                    if (spawned != null)
                        _spawned[corpse.id] = spawned;
                }
                else
                {
                    // E2: fold remote peers' loot into the local container (and refresh the
                    // slot→server-index mirror). CorpseLootSync internally guards slots with
                    // our own optimistic takes in flight and suppresses echo reporting.
                    var sync = existing.GetComponent<CorpseLootSync>();
                    if (sync != null)
                        sync.ReconcileFromServer(corpse.items);
                }
            }

            var stale = new List<uint>();
            foreach (var kv in _spawned)
            {
                if (!alive.Contains(kv.Key))
                {
                    if (kv.Value != null)
                    {
                        CloseInspectionIfOpen(kv.Value);
                        // Bug found 2026-07-03 (log of the final Fase D pass): destroying the
                        // corpse the SAME frame leaves InteractionHandler's cached hoverable
                        // pointing at a destroyed Interactable — FPSInteractionInput re-enables
                        // interaction one frame after StopInspection and the vendor prompt UI
                        // dereferences it (MissingReferenceException, self-healing but noisy).
                        // Neutralize the interaction surface NOW (disabled hoverables are skipped
                        // by the spherecast, disabled colliders by both cast modes → the hover
                        // clears itself cleanly on the next FixedUpdate while the object is still
                        // alive) and destroy after a short grace period.
                        foreach (var inter in kv.Value.GetComponentsInChildren<Interactable>(true))
                            inter.enabled = false;
                        foreach (var col in kv.Value.GetComponentsInChildren<Collider>(true))
                            col.enabled = false;
                        Destroy(kv.Value, DespawnGraceSeconds);
                    }
                    stale.Add(kv.Key);
                }
            }
            foreach (uint k in stale)
                _spawned.Remove(k);
        }

        // Long enough for FPSInteractionInput's next-frame re-enable + one FixedUpdate hover
        // refresh; short enough to be invisible (the interaction surface is already dead).
        private const float DespawnGraceSeconds = 0.25f;

        // Contract-integrity fix (2026-07-03, found in Fase D play-test): destroying a corpse
        // whose loot panel is STILL OPEN (emptied via "Take All", or walked out of the visibility
        // radius mid-loot) left InventoryInspectionManager holding a reference to the now-destroyed
        // StorageStation — the next Escape/Tab threw MissingReferenceException from
        // Workstation.EndInspection() (StorageStation has no null-guard of its own; not our class to
        // fix, this is the external-hook side of the fix per the "never edit STP" rule). Force-close
        // first so the manager's Workstation reference is cleared before the object dies.
        private static void CloseInspectionIfOpen(GameObject corpseGo)
        {
            // StorageStation lives on the Hips bone (child), not the root — see WireLoot.
            var station = corpseGo.GetComponentInChildren<StorageStation>(true);
            if (station == null)
                return;

            foreach (var manager in FindObjectsByType<InventoryInspectionManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (ReferenceEquals(manager.Workstation, station))
                    manager.StopInspection();
            }
        }

        private GameObject SpawnCorpse(CorpseViewMsg corpse)
        {
            // ADR-028 amendment (world chests): a chest entry reuses the whole corpse pipeline
            // (roster reconcile, take/despawn, CorpseLootSync) but renders as a static crate.
            if (corpse.isChest)
                return SpawnChest(corpse);

            var template = ResolveTemplate();
            if (template == null)
                return null;

            var go = Instantiate(template, corpse.position, Quaternion.identity);
            go.name = $"Corpse_{corpse.id}_{corpse.ownerName}";

            ApplyClothing(go, corpse.equipment);
            GameObject heldItemInstance = ApplyHeldItem(go, corpse.heldItem);

            var ragdoll = go.GetComponentInChildren<CharacterRagdoll>(true);
            if (ragdoll != null)
            {
                ragdoll.EnableRagdoll();
                // The robapieles THROWS you. Only the client whose own player was just killed by a
                // revealed creature has a pending impulse, and only for the body at that spot within
                // a few seconds (PhantomAttackHandler matches on position AND time), so every other
                // corpse — and every other client's view of this one — settles exactly as before.
                //
                // Local by design, not an oversight: ADR-028 already has each client run its own
                // ragdoll physics against its own rendered geometry, so corpses never agreed pose
                // for pose. An impulse adds no divergence that was not already accepted.
                if (BackroomsSurvival.Gameplay.PhantomAttackHandler.TryTakeCorpseThrow(
                        go.transform.position, out var impulse))
                {
                    ThrowRagdoll(ragdoll, impulse);
                }
                // Fase D fix #2b: bound how far physics can carry the visual body from death_pos.
                go.AddComponent<CorpseRagdollFreezer>().Initialize(ragdoll.Bones);
            }
            else
                Debug.LogWarning($"[CorpseSpawner] corpse {corpse.id}: no CharacterRagdoll on the template — spawned as a static pose instead.");

            WireLoot(go, corpse, heldItemInstance);

            Debug.Log($"[CorpseSpawner] spawned corpse_id={corpse.id} owner={corpse.ownerName} pos={corpse.position:F2} items={corpse.items.Count}");
            return go;
        }

        // ADR-028 amendment (world chests): the STP "Storage Crate" building piece definition —
        // definitions live under Resources, so the crate PREFAB is reachable at runtime through
        // it without any new serialized reference (same resolution idiom as CarryableDefinition
        // in StpCarryableSpawner). Id read from STP_Storage Crate.asset.
        private const int StorageCrateBuildingDefId = 5498433;

        // Chest visual + loot: crate mesh instead of a ragdoll, and the SAME interaction/loot
        // stack a corpse gets — except it mounts on a dedicated child (no bones to track; the
        // vendor crate colliders stay on the root for physical presence) and skips everything
        // death-specific (clothing, held item, ragdoll, freezer, appearance updates). Nothing
        // here touches the death flow: that only ever starts at report_death_loot.
        private GameObject SpawnChest(CorpseViewMsg corpse)
        {
            var go = ResolveChestVisual(corpse.position);
            go.name = $"Chest_{corpse.id}";

            // Interaction child, not the root: LayerConstants.InteractableNoCollision on the
            // root would strip the crate's physical presence (the vendor colliders kept above).
            var interact = new GameObject("LootInteract");
            interact.transform.SetParent(go.transform, false);
            interact.transform.localPosition = Vector3.zero;
            interact.layer = LayerConstants.InteractableNoCollision;

            var box = interact.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(1.2f, 1.0f, 1.2f);
            box.center = new Vector3(0f, 0.5f, 0f);

            var interactable = interact.AddComponent<Interactable>();
            interactable.Title = "Loot supply crate";

            var container = BuildLootContainer(corpse, out var restriction);

            var station = interact.AddComponent<StorageStation>();
            if (StorageStationContainersField != null)
                StorageStationContainersField.SetValue(station, new IItemContainer[] { container });
            else
                Debug.LogError($"[CorpseSpawner] chest {corpse.id}: StorageStation._containers field not found via reflection " +
                    "(vendor internals changed?) — the loot panel will show empty. Falling back gracefully, no exception.");
            InitializeInspectionSettings(station, WorkstationOpenSettingsField, corpse.id);
            InitializeInspectionSettings(station, WorkstationCloseSettingsField, corpse.id);

            // Same placement as the corpse's sync: the ROOT, where LateUpdate's reconcile
            // (GetComponent<CorpseLootSync>) finds it.
            var sync = go.AddComponent<CorpseLootSync>();
            sync.Initialize(corpse.id, container, restriction);

            Debug.Log($"[CorpseSpawner] spawned chest_id={corpse.id} pos={corpse.position:F2} items={corpse.items.Count}");
            return go;
        }

        // The crate mesh, neutralized: vendor MonoBehaviours (BuildingPiece/saveables) and
        // Rigidbodies destroyed, COLLIDERS kept (unlike NeutralizeToVisualOnly) so the crate has
        // physical presence. Missing definition/prefab → primitive placeholder cube ("no bloquees
        // por arte"), never a null return: a chest must always be lootable once replicated.
        private static GameObject ResolveChestVisual(Vector3 position)
        {
            var def = DataDefinition<PolymindGames.BuildingSystem.BuildingPieceDefinition>.GetWithId(StorageCrateBuildingDefId);
            var prefab = def != null && def.Prefab != null ? def.Prefab.gameObject : null;
            if (prefab == null)
            {
                Debug.LogWarning("[CorpseSpawner] Storage Crate building definition/prefab not found — using a placeholder cube.");
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.position = position + Vector3.up * 0.5f;
                return cube;
            }

            var go = Instantiate(prefab, position, Quaternion.identity);
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
                Destroy(rb);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                Destroy(mb);
            return go;
        }

        // Design change (2026-07-03, Fase D play-test): the interaction collider was originally
        // anchored to the ROOT (the frozen death position, static). Play-test showed this visibly
        // desyncs from the ragdoll once physics settles it — the player had to interact with a
        // phantom point instead of the body they could see. Per the human's redesign: the collider
        // now lives on the ragdoll's Hips bone (already dynamic after EnableRagdoll()), so the
        // player interacts exactly where the body visually is. STP's own InteractionHandler is
        // confirmed camera-raycast/spherecast driven (not proximity — see InteractionHandler.cs
        // DetectionMode.SphereCast/_view.forward), matching how chests already work, so this is a
        // like-for-like swap of WHERE the same detection mechanism finds its target.
        //
        // take_corpse_item's server-side 5m check (Fase A) still validates against the FROZEN
        // death_pos, unchanged — this collider only decides when the client shows the prompt and
        // sends the request; the server remains the real authority (ADR-028 decision 1 intact). A
        // ragdoll that slides >5m from death_pos down a slope would show the prompt but have its
        // request rejected — flagged as a known edge case, not fixed here (see WireLoot doc).
        private static readonly Vector3 InteractBoxSize = new Vector3(1.0f, 0.8f, 1.0f);
        private static readonly Vector3 InteractBoxCenter = Vector3.zero;

        private static readonly FieldInfo StorageStationContainersField =
            typeof(StorageStation).GetField("_containers", BindingFlags.Instance | BindingFlags.NonPublic);

        // Workstation._openStationSettings/_closeStationSettings (InspectionSettings structs) are
        // only ever populated by Unity's normal serialization (prefab/inspector) — a component
        // added purely via AddComponent() at runtime leaves their `Event: UnityEvent` field at the
        // C# default (null, NOT an empty instantiated UnityEvent), and Workstation.BeginInspection/
        // EndInspection call `.Event.Invoke()` unconditionally → NullReferenceException the moment
        // the corpse is interacted with (found in Fase D play-test). Same reflection idiom as the
        // _containers fix above: reach into the private struct fields and give each Event a real
        // instance. The nested InspectionSettings type is `protected`, hence GetNestedType(NonPublic).
        private static readonly FieldInfo WorkstationOpenSettingsField =
            typeof(Workstation).GetField("_openStationSettings", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo WorkstationCloseSettingsField =
            typeof(Workstation).GetField("_closeStationSettings", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo InspectionSettingsEventField =
            typeof(Workstation).GetNestedType("InspectionSettings", BindingFlags.NonPublic)
                ?.GetField("Event", BindingFlags.Instance | BindingFlags.Public);

        // Fase D: wires the corpse into the SAME dual-inventory widget storage crates use (see
        // class doc for why this needs a real StorageStation instance, reflection-fed). The
        // interaction stack (collider/Interactable/StorageStation) lives on the ragdoll's pelvis
        // bone (see InteractBoxSize doc), not the root — it must physically track the fallen body.
        //
        // Bone name is "Pelvis", NOT "Hips" — verified directly in MTP_PlayerViewer.prefab (the
        // base of RemotePlayerAvatar/CorpseAvatar); "Hips" doesn't exist anywhere in that rig.
        // Found the hard way: ProxyGroundingHook.cs ([D] grounding, ADR-025-adjacent) also searches
        // for "Hips" and would have silently no-op'd the same way (FindBone returns null →
        // `_hasRig=false` → the whole hook goes inert, no warning) — its calibration play-test was
        // marked PENDING in STATE.md and may never have actually run. Flagged, NOT fixed here (out
        // of Fase D scope — that hook is unrelated to corpses); worth a follow-up check.
        /// <summary>
        /// Push a freshly-enabled ragdoll, so a kill launches the body instead of dropping it.
        ///
        /// Every bone gets the velocity rather than one AddForce on the hips: a ragdoll whose limbs
        /// start at rest while its pelvis leaves at 6 m/s snaps its own joints and reads as a glitch.
        /// Uniform velocity throws the whole body and lets the joints sort out the tumble.
        ///
        /// `CorpseRagdollFreezer` (added right after) still bounds how far this can carry the body,
        /// so the throw cannot put a lootable corpse somewhere the server never said it was.
        /// </summary>
        private static void ThrowRagdoll(CharacterRagdoll ragdoll, Vector3 impulse)
        {
            var bones = ragdoll.Bones;
            if (bones == null)
                return;

            for (int i = 0; i < bones.Length; i++)
            {
                var rb = bones[i] != null ? bones[i].GetComponent<Rigidbody>() : null;
                if (rb != null && !rb.isKinematic)
                    rb.linearVelocity = impulse;
            }
        }

        private void WireLoot(GameObject go, CorpseViewMsg corpse, GameObject heldItemInstance)
        {
            Transform hips = FindBoneByName(go.transform, "Pelvis");
            if (hips == null)
            {
                Debug.LogWarning($"[CorpseSpawner] corpse {corpse.id}: no 'Pelvis' bone found — loot interaction disabled for this corpse.");
                return;
            }
            GameObject target = hips.gameObject;

            var box = target.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = InteractBoxSize;
            box.center = InteractBoxCenter;
            target.layer = LayerConstants.InteractableNoCollision;

            var interactable = target.AddComponent<Interactable>();
            interactable.Title = $"Loot {corpse.ownerName}'s corpse";

            var container = BuildLootContainer(corpse, out var restriction);

            var station = target.AddComponent<StorageStation>();
            if (StorageStationContainersField != null)
            {
                StorageStationContainersField.SetValue(station, new IItemContainer[] { container });
            }
            else
            {
                Debug.LogError($"[CorpseSpawner] corpse {corpse.id}: StorageStation._containers field not found via reflection " +
                    "(vendor internals changed?) — the loot panel will show empty. Falling back gracefully, no exception.");
            }
            InitializeInspectionSettings(station, WorkstationOpenSettingsField, corpse.id);
            InitializeInspectionSettings(station, WorkstationCloseSettingsField, corpse.id);

            // CorpseLootSync stays on the root — it only needs the container reference and the
            // corpse id, no interaction/collider dependency. It receives the restriction so a
            // rejection ROLLBACK can unseal → re-insert → reseal (a sealed container would refuse
            // the restore write too).
            var sync = go.AddComponent<CorpseLootSync>();
            sync.Initialize(corpse.id, container, restriction);

            WireAppearanceUpdates(go, corpse, sync, heldItemInstance);
        }

        // Fix #2 (2026-07-03, Fase D play-test): CorpseSpawner only dressed the ragdoll ONCE at
        // spawn — looting a worn item (e.g. a shirt) left it visually still worn until the whole
        // corpse despawned. Subscribes to CorpseLootSync's CONFIRMED-take event (never the
        // optimistic local one — undressing before the server agrees would be the same
        // trust-inversion bug as fix #1) and clears the matching equipment slot / destroys the
        // held-item instance the moment the real take lands. Mutable local state captured by the
        // closure (equipmentState/heldItemState/heldInstance) — one closure per corpse, lives as
        // long as the corpse GameObject (the subscription is never explicitly removed; it dies
        // with the corpse, same as every other one-shot hook in this file).
        private void WireAppearanceUpdates(GameObject go, CorpseViewMsg corpse, CorpseLootSync sync, GameObject heldItemInstance)
        {
            var clothing = go.GetComponentInChildren<CharacterClothing>(true);
            int[] equipmentState = corpse.equipment != null ? (int[])corpse.equipment.Clone() : new int[SlotBodyPoints.Length];
            int heldItemState = corpse.heldItem;
            GameObject heldInstance = heldItemInstance;

            sync.ItemTakenConfirmed += (itemId, quantity) =>
            {
                for (int i = 0; i < equipmentState.Length; i++)
                {
                    if (equipmentState[i] == 0 || equipmentState[i] != itemId)
                        continue;

                    equipmentState[i] = 0;
                    if (clothing != null)
                        clothing.SetClothing(SlotBodyPoints[i], 0);
                }

                if (heldItemState != 0 && heldItemState == itemId)
                {
                    heldItemState = 0;
                    if (heldInstance != null)
                    {
                        Destroy(heldInstance);
                        heldInstance = null;
                    }
                }
            };
        }

        // Gives a runtime-added Workstation's InspectionSettings struct a real UnityEvent instance
        // (see field doc above for why it's otherwise null). Boxes the struct, sets the field on
        // the box, writes it back — the only way to mutate a struct field through reflection.
        private static void InitializeInspectionSettings(StorageStation station, FieldInfo settingsField, uint corpseId)
        {
            if (settingsField == null || InspectionSettingsEventField == null)
            {
                Debug.LogError($"[CorpseSpawner] corpse {corpseId}: Workstation InspectionSettings field(s) not found via " +
                    "reflection (vendor internals changed?) — BeginInspection/EndInspection may throw. Not fixed this spawn.");
                return;
            }

            object boxedSettings = settingsField.GetValue(station);
            InspectionSettingsEventField.SetValue(boxedSettings, new UnityEngine.Events.UnityEvent());
            settingsField.SetValue(station, boxedSettings);
        }

        // Builds a real STP ItemContainer (not a hand-rolled IItemContainer — see class doc)
        // populated once from the corpse's reported loot stacks. item_id is the raw STP scheme
        // (DataIdReference, may be negative) — same resolution as ApplyHeldItem/ProxyHeldItemHook.
        private static ItemContainer BuildLootContainer(CorpseViewMsg corpse, out RejectAllRestriction restriction)
        {
            int size = Mathf.Max(corpse.items.Count, 1);
            // Loot-only by scope decision — starts UNSEALED so the initial population below can
            // land (ItemContainer gates its own SetItemAtIndex through GetAllowedCount; sealing
            // first left the panel empty — see RejectAllRestriction doc), sealed right after.
            restriction = RejectAllRestriction.CreateUnsealed();
            var container = new ItemContainer.Builder()
                .WithName(corpse.isChest ? corpse.ownerName : $"{corpse.ownerName}'s corpse")
                .WithSize(size)
                .WithMaxWeight(ItemContainer.Builder.MaxWeightLimit)
                .WithAllowStacking(true)
                .WithRestriction(restriction)
                .Build();

            for (int i = 0; i < corpse.items.Count; i++)
            {
                var stack = corpse.items[i];
                var def = DataDefinition<ItemDefinition>.GetWithId(stack.itemId);
                if (def == null)
                {
                    Debug.LogWarning($"[CorpseSpawner] corpse {corpse.id}: unknown item_id={stack.itemId} at slot {i}, skipped.");
                    continue;
                }
                container.SetItemAtIndex(i, new ItemStack(new Item(def), stack.quantity));
            }

            restriction.Sealed = true;
            return container;
        }

        // ADR-022 mechanism reused directly (not the live ProxyClothingHook — see
        // CorpseAvatarPrefabBuilder class doc for why): one-shot SetClothing per slot.
        private static void ApplyClothing(GameObject go, int[] equipment)
        {
            var clothing = go.GetComponentInChildren<CharacterClothing>(true);
            if (clothing == null || equipment == null)
                return;

            for (int i = 0; i < SlotBodyPoints.Length; i++)
            {
                int id = i < equipment.Length ? equipment[i] : 0;
                clothing.SetClothing(SlotBodyPoints[i], id);
            }
        }

        // ADR-023 mechanism reused directly (not the live ProxyHeldItemHook): resolve the item's
        // world pickup mesh, neutralize it to visual-only, and parent it under Hand.R with the
        // category's placement from the SAME GripPoseSet asset the live proxies use. One-shot —
        // no per-frame re-application (the corpse's held item never changes), and no finger curl
        // (the ragdoll's hand bone is not driven by a bind pose once physics takes over).
        // Returns the spawned instance (or null if none) so WireLoot can destroy it later if the
        // held item is looted (fix #2, real-time undress on confirmed take).
        private static GameObject ApplyHeldItem(GameObject go, int heldItem)
        {
            if (heldItem == 0)
                return null;

            var def = DataDefinition<ItemDefinition>.GetWithId(heldItem);
            var pickup = def != null ? def.Pickup : null;
            if (pickup == null)
                return null; // unknown item / no world model → empty hands (graceful, same as ProxyHeldItemHook)

            Transform hand = FindBoneByName(go.transform, HandBoneName);
            if (hand == null)
                return null;

            var instance = Instantiate(pickup.gameObject, hand, false);
            NeutralizeToVisualOnly(instance);
            SetLayerRecursive(instance, hand.gameObject.layer);

            string category = def.ParentGroup != null ? def.ParentGroup.Name : "";
            var grip = ResolveGripPoseSet()?.Resolve(category);
            var t = instance.transform;
            t.localPosition = grip != null ? grip.modelLocalPosition : Vector3.zero;
            t.localRotation = Quaternion.Euler(grip != null ? grip.modelLocalEuler : Vector3.zero);
            t.localScale = grip != null && grip.modelLocalScale != Vector3.zero ? grip.modelLocalScale : Vector3.one;
            return instance;
        }

        private static GripPoseSet _gripPoses;
        private static bool _gripPosesResolved;

        // The SAME asset RemoteAvatarPrefabBuilder bakes for the live proxies (Resources, so a
        // corpse instance — not itself under Resources — can still load it by path at runtime).
        private static GripPoseSet ResolveGripPoseSet()
        {
            if (!_gripPosesResolved)
            {
                _gripPoses = Resources.Load<GripPoseSet>("GripPoseSet");
                _gripPosesResolved = true;
            }
            return _gripPoses;
        }

        private static Transform FindBoneByName(Transform root, string name)
        {
            foreach (var tr in root.GetComponentsInChildren<Transform>(true))
            {
                if (tr.name == name)
                    return tr;
            }
            return null;
        }

        private static void NeutralizeToVisualOnly(GameObject go)
        {
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
                Destroy(rb);
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                Destroy(col);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                Destroy(mb);
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursive(child.gameObject, layer);
        }

        private GameObject ResolveTemplate()
        {
            if (_templateResolved)
                return _template;

            _templateResolved = true;
            _template = Resources.Load<GameObject>(ResourcePath);
            if (_template == null)
                Debug.LogError($"[CorpseSpawner] Resources/{ResourcePath} not found. Run " +
                    "'Backrooms ▸ Build Corpse Avatar Prefab' — corpses will not render until then.");
            return _template;
        }

        private void OnDestroy()
        {
            if (_instance != this)
                return;

            foreach (var kv in _spawned)
            {
                if (kv.Value != null)
                    Destroy(kv.Value);
            }
            _spawned.Clear();
            _instance = null;
        }
    }
}
