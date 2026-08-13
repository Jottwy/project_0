using System.Collections.Generic;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.MovementSystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-032 amendment — applies the backend's persisted REAL STP inventory to the local
    /// player after a world-save hydration. Counterpart of <see cref="InventoryReporter"/>.
    ///
    /// Trigger: the one-shot <c>inventory_restored {items:[{item_id,quantity}]}</c> event, which
    /// the backend emits in the SAME deferred first-input window as <c>session_restored</c>
    /// (Tarea 1's position snap). The two are independent consumers of independent events — the
    /// applier snaps the motor, this component fills containers — so ordering between them is
    /// cosmetic and they cannot step on each other.
    ///
    /// Apply = CLEAR first, then add: every container is cleared with the same STP-native
    /// <c>IItemContainer.Clear()</c> DeathLootReporter.OnRespawn uses (idempotence — a duplicate
    /// event or a pre-populated fresh-session inventory can never double up), then each persisted
    /// stack is added via the STP-native <c>IInventory.AddItemsById</c> (its own routing/stacking
    /// decides the slots). Anything that doesn't fit is logged and dropped (the container total
    /// is the same one the stacks came from, so overflow only happens on a config change).
    ///
    /// The event can arrive before the local character exists (first-input window fires during
    /// scene warm-up) — the payload is stashed and applied as soon as the character resolves.
    /// Self-bootstraps; removable.
    /// </summary>
    public sealed class InventoryRestorer : MonoBehaviour
    {
        private const string RestoredEvent = "inventory_restored";

        private static InventoryRestorer _instance;

        private IPCClient _ipc;
        private CharacterControllerMotor _motor;
        private ICharacter _character;

        // Stacks waiting for the character to exist. Null = nothing pending. Mutually
        // exclusive with `_pendingV2` — the backend sends ONE homogeneous shape per event
        // (ADR-045 Fase 3: v2 when inventory_v2 is populated server-side, legacy flat
        // otherwise), never both for the same restore.
        private List<(int itemId, int quantity)> _pending;
        private List<InventoryStackV2> _pendingV2;

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

            var go = new GameObject("[InventoryRestorer]");
            _instance = go.AddComponent<InventoryRestorer>();
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

        private void Update()
        {
            // Subscribe once the IPC client exists (same pattern/known-limit as
            // AuthoritativePoseApplier: the client is a long-lived singleton).
            if (_ipc == null && IPCClient.TryGetInstance(out var ipc))
            {
                _ipc = ipc;
                _ipc.AddEventListener(OnGameEvent);
            }

            if (_pending == null && _pendingV2 == null)
                return;

            ResolveCharacter();
            if (_character?.Inventory == null)
                return; // keep waiting — scene still warming up

            if (_pendingV2 != null)
            {
                ApplyV2(_pendingV2);
                _pendingV2 = null;
            }
            else
            {
                Apply(_pending);
                _pending = null;
            }
        }

        private void OnGameEvent(GameEventMsg ev)
        {
            if (ev == null || ev.eventType != RestoredEvent)
                return;

            // ADR-045 Fase 3: v2 (container/slot/props) takes priority when the payload has it —
            // the backend only ever sends ONE homogeneous shape per event (see `_pendingV2` doc).
            var stacksV2 = ParseStacksV2(ev.data);
            if (stacksV2 != null)
            {
                _pendingV2 = stacksV2; // applied in Update once the character exists
                Debug.Log($"[InventoryRestorer] inventory_restored v2 received: {stacksV2.Count} stacks (pending apply)");
                return;
            }

            var stacks = ParseStacks(ev.data);
            if (stacks == null)
            {
                Debug.LogWarning("[InventoryRestorer] inventory_restored with unparsable payload — ignored");
                return;
            }

            _pending = stacks; // applied in Update once the character exists
            Debug.Log($"[InventoryRestorer] inventory_restored received: {stacks.Count} stacks (pending apply)");
        }

        /// ADR-045 fix: `MsgPackReader.ReadValue()` materializes a msgpack array as `object[]`
        /// (see its doc comment + `ReadArray`), NEVER `List<object>` — every other IPC event
        /// consumer in the project (`IPCParse.Vec3`/`IntArray`/`IntArray2`/`StringArray`) checks
        /// `is object[]` for exactly this reason. This method checked `List<object>`, a type the
        /// reader never produces, so the cast failed on every single payload and
        /// `inventory_restored` was unparsable 100% of the time — confirmed in a real playtest
        /// log, not intermittent. Rewritten to match the established `object[]` + `IPCParse`
        /// convention; `ToLong` never throws on a type mismatch (defaults to 0), same as every
        /// other `IPCParse` accessor, so the old try/catch is no longer needed.
        ///
        /// Public rather than internal so the EditMode suite can reach it directly — same reason
        /// documented on `IpcStreamReader`: `tools/dev/CompileCheckClient.sh` builds each
        /// assembly under a `_check` suffix, so an `InternalsVisibleTo` friend name never matches
        /// under the project's own compile-check gate.
        public static List<(int itemId, int quantity)> ParseStacks(object data)
        {
            if (data is not Dictionary<string, object> d ||
                !d.TryGetValue("items", out var rawItems) ||
                rawItems is not object[] list)
                return null;

            var stacks = new List<(int, int)>(list.Length);
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] is not Dictionary<string, object> m)
                    continue;

                int id = (int)IPCParse.ToLong(IPCParse.Get(m, "item_id"));
                int qty = (int)IPCParse.ToLong(IPCParse.Get(m, "quantity"));
                if (id != 0 && qty > 0)
                    stacks.Add((id, qty));
            }
            return stacks;
        }

        /// ADR-045 Fase 3: companion of <see cref="ParseStacks"/> for the instance-fidelity
        /// shape (container/slot/props). Returns null when the payload isn't v2-shaped at
        /// all — malformed, or every entry is a legacy `{item_id, quantity}` pair — so the
        /// caller falls back to <see cref="ParseStacks"/>; the backend only ever sends ONE
        /// homogeneous shape per event (never a mix), so checking the FIRST entry for
        /// "container"/"slot" is enough to classify the whole payload.
        public static List<InventoryStackV2> ParseStacksV2(object data)
        {
            if (data is not Dictionary<string, object> d ||
                !d.TryGetValue("items", out var rawItems) ||
                rawItems is not object[] list ||
                list.Length == 0 ||
                list[0] is not Dictionary<string, object> first ||
                !first.ContainsKey("container") ||
                !first.ContainsKey("slot"))
                return null;

            var stacks = new List<InventoryStackV2>(list.Length);
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] is not Dictionary<string, object> m)
                    continue;

                int id = (int)IPCParse.ToLong(IPCParse.Get(m, "item_id"));
                int qty = (int)IPCParse.ToLong(IPCParse.Get(m, "quantity"));
                if (id == 0 || qty <= 0)
                    continue;

                byte container = (byte)IPCParse.ToLong(IPCParse.Get(m, "container"));
                byte slot = (byte)IPCParse.ToLong(IPCParse.Get(m, "slot"));

                List<ItemPropertyValue> props = null;
                if (IPCParse.Get(m, "props") is object[] rawProps && rawProps.Length > 0)
                {
                    props = new List<ItemPropertyValue>(rawProps.Length);
                    for (int p = 0; p < rawProps.Length; p++)
                    {
                        if (rawProps[p] is not Dictionary<string, object> pm)
                            continue;
                        props.Add(new ItemPropertyValue
                        {
                            id = (int)IPCParse.ToLong(IPCParse.Get(pm, "id")),
                            value = IPCParse.ToFloat(IPCParse.Get(pm, "value")),
                        });
                    }
                }

                stacks.Add(new InventoryStackV2
                {
                    itemId = id,
                    quantity = qty,
                    container = container,
                    slot = slot,
                    props = props,
                });
            }
            return stacks;
        }

        private void Apply(List<(int itemId, int quantity)> stacks)
        {
            var inventory = _character.Inventory;
            var containers = inventory.Containers;

            // Clear first (STP-native, mirrors DeathLootReporter.OnRespawn) — idempotent apply.
            for (int i = 0; i < containers.Count; i++)
                containers[i]?.Clear();

            int added = 0, rejected = 0;
            for (int i = 0; i < stacks.Count; i++)
            {
                var (addedCount, rejectReason) = inventory.AddItemsById(stacks[i].itemId, stacks[i].quantity);
                if (addedCount >= stacks[i].quantity)
                {
                    added++;
                }
                else
                {
                    rejected++;
                    Debug.LogWarning($"[InventoryRestorer] stack item_id={stacks[i].itemId} x{stacks[i].quantity} only added {addedCount} ({rejectReason ?? "no reason"})");
                }
            }

            Debug.Log($"[InventoryRestorer] inventory restored: {added} stacks added, {rejected} partial/rejected");
        }

        /// <summary>
        /// Última red: lo que no cabe en ningún hueco se suelta a los pies por el mismo camino que
        /// un drop nativo, replicado y recogible, en vez de dejar de existir.
        ///
        /// LIMITACIÓN DECLARADA: `stp_drop` viaja como `def_id` + cantidad, así que las propiedades
        /// de instancia (el desgaste de una antorcha) NO sobreviven al vuelco — el item vuelve al
        /// mundo a valor de fábrica. Es la misma carencia de esquema que hace que el botín de un
        /// cadáver pierda el desgaste, y se arregla en el mismo sitio (pide ADR). Perder el desgaste
        /// es estrictamente mejor que perder el item.
        /// </summary>
        private void SpillToWorld(int itemId, int count)
        {
            if (count <= 0)
                return;

            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
            {
                Debug.LogError($"[InventoryRestorer] IPC caído: {count}×item_id={itemId} no caben y no se pueden " +
                    "soltar al mundo. ESO se ha perdido.");
                return;
            }

            var t = _character.transform;
            Vector3 at = t.position + t.forward * 0.5f;
            // ADR-070: con impulso, como un drop de mano — el host decide dónde reposa. Sin él, todo
            // lo que no cupo al restaurar aparecería apilado y quieto sobre los pies del jugador.
            Vector3 velocity = StpNativeDropWatcher.SynthesizeTossVelocity(_character);
            long dropId = StpNativeDropWatcher.MintDropId();
            ipc.SendStpDrop(dropId, itemId, count, at, t.eulerAngles.y, velocity);
            Debug.LogWarning($"[InventoryRestorer] {count}×item_id={itemId} no cabían — soltados al mundo " +
                $"drop_id={dropId} en {at:F2} vel={velocity:F2} (sin propiedades de instancia).");
        }

        /// ADR-045 Fase 3: places each stack in its EXACT saved container/slot via
        /// <c>IItemContainer.SetItemAtIndex</c> (public STP API — no vendor edit) instead of
        /// letting <c>AddItemsById</c> pick a slot, then restores instance properties via the
        /// public <c>ItemProperty.Double</c> setter. Same clear-first idempotence as
        /// <see cref="Apply"/>. An out-of-range container/slot (a save from before the
        /// inventory layout changed) or an unknown item_id degrades gracefully — the stack is
        /// dropped and logged, never a hard failure; likewise a stack the container truncates or
        /// refuses (weight limit, slot restriction) is counted as rejected, not as placed. An
        /// unknown property id on an otherwise valid stack is silently skipped (same contract the
        /// backend documents for its own restore path).
        private void ApplyV2(List<InventoryStackV2> stacks)
        {
            var containers = _character.Inventory.Containers;

            for (int i = 0; i < containers.Count; i++)
                containers[i]?.Clear();

            int placed = 0, rejected = 0;
            for (int i = 0; i < stacks.Count; i++)
            {
                var s = stacks[i];

                // La definición se resuelve ANTES que el hueco: es lo único de aquí que no tiene
                // recuperación posible. Con def conocida, un contenedor o slot que ya no existen
                // solo significan "aquí no", no "esto se pierde".
                if (!ItemDefinition.TryGetWithId(s.itemId, out var def))
                {
                    rejected++;
                    Debug.LogError($"[InventoryRestorer] v2 stack item_id={s.itemId} x{s.quantity}: definición desconocida — " +
                        "IRRECUPERABLE, no hay con qué reconstruirlo.");
                    continue;
                }

                IItemContainer container = null;
                if (s.container >= containers.Count || containers[s.container] == null)
                    Debug.LogWarning($"[InventoryRestorer] v2 stack item_id={s.itemId} container={s.container} out of range ({containers.Count} containers) — se recoloca");
                else if (s.slot >= containers[s.container].SlotsCount)
                    Debug.LogWarning($"[InventoryRestorer] v2 stack item_id={s.itemId} slot={s.slot} out of range ({containers[s.container].SlotsCount} slots) — se recoloca");
                else
                    container = containers[s.container];

                var item = new Item(def);
                if (s.props != null)
                {
                    for (int p = 0; p < s.props.Count; p++)
                    {
                        if (item.TryGetProperty(s.props[p].id, out var prop))
                            prop.Double = s.props[p].value;
                        // Unknown property id: ignored, not an error (def changed between versions).
                    }
                }

                // SetItemAtIndex silently truncates (or drops) via GetAllowedCount — weight limit
                // and container restrictions both apply here, same as the AddItemsById path in
                // Apply. Check what actually landed instead of assuming the full stack fit.
                var stack = new ItemStack(item, s.quantity);
                string rejectReason = null;
                int placedCount = 0;
                if (container != null)
                {
                    var allowed = container.GetAllowedCount(stack);
                    rejectReason = allowed.rejectReason;
                    placedCount = allowed.allowedCount > 0 ? container.SetItemAtIndex(s.slot, stack) : 0;
                }

                if (placedCount >= s.quantity)
                {
                    placed++;
                    continue;
                }

                rejected++;
                Debug.LogWarning($"[InventoryRestorer] v2 stack item_id={s.itemId} x{s.quantity} container={s.container} slot={s.slot} only placed {placedCount} ({(string.IsNullOrEmpty(rejectReason) ? "no reason" : rejectReason)})");

                // Lo que no cupo en SU sitio NO se descarta. Antes se contaba como rechazado y ahí
                // moría: volvías a la partida y te faltaba lo que no encajó, con un warning que
                // nadie lee a media sesión. Segundo intento en cualquier hueco del inventario —con
                // el item YA construido, así que conserva sus propiedades de instancia— y lo que
                // aun así sobre se suelta al mundo.
                int leftover = s.quantity - Mathf.Max(0, placedCount);
                var (elsewhere, _) = _character.Inventory.AddItem(new ItemStack(item, leftover));
                leftover -= Mathf.Max(0, elsewhere);
                if (elsewhere > 0)
                    Debug.Log($"[InventoryRestorer] item_id={s.itemId}: {elsewhere} recolocados en otro hueco.");

                SpillToWorld(s.itemId, leftover);
            }

            Debug.Log($"[InventoryRestorer] inventory v2 restored: {placed} stacks placed, {rejected} partial/dropped");
        }

        // Same local-character resolve as DeathLootReporter/InventoryReporter (skip remote
        // avatars; destroyed motor reads as null → re-resolve).
        private void ResolveCharacter()
        {
            if (_motor != null)
                return;

            var m = LocalPlayerLocator.Find<CharacterControllerMotor>();
            if (m == null)
            {
                _motor = null;
                _character = null;
                return;
            }

            _motor = m;
            _character = m.GetComponentInParent<ICharacter>();
        }

        private void OnDestroy()
        {
            if (_instance != this)
                return;

            if (_ipc != null)
                _ipc.RemoveEventListener(OnGameEvent);
            _instance = null;
        }
    }
}
