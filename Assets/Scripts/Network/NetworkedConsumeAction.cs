using System.Collections;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-030: drop-in replacement for STP's vendor <c>ConsumeAction</c> (identical local
    /// effect: same <see cref="ConsumeData"/> fields, same audio/removal behavior) that
    /// ADDITIONALLY reports the consumed item to the authoritative backend via
    /// <see cref="IPCClient.SendConsumeItem"/>. The vendor class is sealed and exposes no
    /// "item consumed" event to hook externally (rule: never edit STP code) — this asset is
    /// wired into the `_actions` array of the affected item definitions INSTEAD of the vendor
    /// one (a data reference swap, not a vendor code edit). Local Hunger/Thirst writes below
    /// are harmless-but-redundant while connected (StatInterpolator, ADR-009 L2, overwrites the
    /// displayed value from the server every snapshot) and are the real behavior when offline/
    /// disconnected (STP's managers are only disabled while IPCClient.IsConnected).
    /// </summary>
    [CreateAssetMenu(menuName = "Polymind Games/Items/Actions/Networked Consume Action", fileName = "ItemAction_NetworkedConsume")]
    public sealed class NetworkedConsumeAction : ItemAction
    {
        // No [Title(...)] here (unlike the vendor original) — Toolbox lives in a separate
        // assembly that BackroomsSurvival.asmdef doesn't reference; purely a cosmetic Inspector
        // header, not worth adding a new assembly reference for.
        [SerializeField, Range(0f, 10f)]
        private float _duration;

        [SerializeField]
        private AudioData _consumeAudio;

        [SerializeField]
        private bool _removeFromInventory = true;

        /// <inheritdoc/>
        public override float GetDuration(ICharacter character, ItemStack stack) => _duration;

        /// <inheritdoc/>
        public override bool CanPerform(ICharacter character, ItemStack stack)
            => stack.Item.Definition.GetDataOfType<ConsumeData>() != null;

        /// <inheritdoc/>
        protected override IEnumerator Execute(ICharacter character, SlotReference parentSlot, ItemStack stack, float duration)
        {
            AudioManager.Instance.PlayClip2D(_consumeAudio);

            yield return new WaitForTime(duration);

            var data = stack.Item.Definition.GetDataOfType<ConsumeData>();

            // Restore or reduce health based on the health change value (single read — the
            // vendor properties roll a new random value on every access).
            float healthChange = data.HealthChange;
            if (!Mathf.Approximately(healthChange, 0f))
            {
                if (healthChange > 0f)
                    character.HealthManager.RestoreHealth(healthChange);
                else
                    character.HealthManager.ReceiveDamage(-healthChange);
            }

            // Change hunger if hunger data is available.
            if (!Mathf.Approximately(data.HungerChange, 0f) && character.TryGetCC<IHungerManagerCC>(out var hunger))
                hunger.Hunger += data.HungerChange;

            // Change thirst if thirst data is available.
            if (!Mathf.Approximately(data.ThirstChange, 0f) && character.TryGetCC<IThirstManagerCC>(out var thirst))
                thirst.Thirst += data.ThirstChange;

            // ADR-030: the actual authoritative restore — the backend owns the fixed amount for
            // this item_id (consumable_spec, game_loop.rs), ignoring whatever local roll happened
            // above. Reported regardless of connection state; a no-op server-side if disconnected.
            if (IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected)
                ipc.SendConsumeItem(stack.Item.Id);

            // Decrease the stack count of the consumed item.
            if (_removeFromInventory && parentSlot.IsValid())
                parentSlot.AdjustStack(-1);
        }
    }
}
