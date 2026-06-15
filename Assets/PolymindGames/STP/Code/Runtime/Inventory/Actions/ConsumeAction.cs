using System.Collections;
using UnityEngine;

namespace PolymindGames.InventorySystem
{
    [CreateAssetMenu(menuName = "Polymind Games/Items/Actions/Consume Action", fileName = "ItemAction_Consume")]
    public sealed class ConsumeAction : ItemAction
    {
        [SerializeField, Range(0f, 10f), Title("Consuming")]
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
            
            // Restore or reduce health based on the health change value.
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

            // Decrease the stack count of the consumed item.
            if (_removeFromInventory && parentSlot.IsValid())
                parentSlot.AdjustStack(-1);
        }
    }
}