using System.Collections;
using UnityEngine;

namespace PolymindGames.InventorySystem
{
    public abstract class ItemAction : ScriptableObject
    {
        [SerializeField, Tooltip("The name of the action, displayed in the UI.")]
        private string _actionName;

        [SerializeField, Tooltip("The verb representing the action (e.g., 'Use', 'Equip').")]
        private string _actionVerb;

        [SerializeField, Tooltip("The icon representing the action, displayed in the UI.")]
        private Sprite _actionIcon;

        /// <summary>
        /// The name of the action, displayed in the UI.
        /// </summary>
        public string ActionName => _actionName;

        /// <summary>
        /// The verb representing the action (e.g., "Use", "Equip").
        /// </summary>
        public string ActionVerb => _actionVerb;

        /// <summary>
        /// The icon representing the action, displayed in the UI.
        /// </summary>
        public Sprite ActionIcon => _actionIcon;

        /// <summary>
        /// Determines the duration of the action when performed by a character with a specific item.
        /// </summary>
        /// <param name="character">The character performing the action.</param>
        /// <param name="stack">The item associated with the action.</param>
        /// <returns>The duration of the action in seconds.</returns>
        public abstract float GetDuration(ICharacter character, ItemStack stack);

        /// <summary>
        /// Determines whether the action can be performed by a character with a specific item.
        /// </summary>
        /// <param name="character">The character performing the action.</param>
        /// <param name="stack">The item associated with the action.</param>
        /// <returns>True if the action can be performed; otherwise, false.</returns>
        public abstract bool CanPerform(ICharacter character, ItemStack stack);

        /// <summary>
        /// Executes the logic for performing the action.
        /// </summary>
        /// <param name="character">The character performing the action.</param>
        /// <param name="parentSlot">The parent slot holding the item.</param>
        /// <param name="stack">The item associated with the action.</param>
        /// <param name="duration">The duration of the action.</param>
        /// <returns>An enumerator for coroutine execution.</returns>
        protected abstract IEnumerator Execute(ICharacter character, SlotReference parentSlot, ItemStack stack, float duration);

        /// <summary>
        /// Performs the action, optionally applying a duration multiplier.
        /// </summary>
        /// <param name="character">The character performing the action.</param>
        /// <param name="slot">The container holding the item.</param>
        /// <param name="stack">The item associated with the action.</param>
        /// <param name="durationMultiplier">A multiplier to adjust the action's duration.</param>
        /// <returns>A tuple containing the started coroutine and the action's duration.</returns>
        public (Coroutine coroutine, float duration) Perform(ICharacter character, in SlotReference slot, ItemStack stack, float durationMultiplier = 1f)
        {
#if DEBUG
            if (character == null)
                throw new System.ArgumentNullException(nameof(character));
#endif
            
            if (!CanPerform(character, stack))
                return (null, 0f);

            float actionDuration = GetDuration(character, stack) * durationMultiplier;
            IEnumerator routine = Execute(character, slot, stack, actionDuration);
            return (character.StartCoroutine(routine), actionDuration);
        }

        /// <summary>
        /// Cancels a currently running action.
        /// </summary>
        /// <param name="actionCoroutine">The coroutine associated with the action.</param>
        /// <param name="character">The character performing the action.</param>
        public void CancelAction(ref Coroutine actionCoroutine, ICharacter character)
        {
#if DEBUG
            if (character == null)
                throw new System.ArgumentNullException(nameof(character));
#endif

            if (character is MonoBehaviour monoBehaviour)
            {
                CoroutineUtility.StopCoroutine(monoBehaviour, ref actionCoroutine);
            }
            else
            {
                CoroutineUtility.StopGlobalCoroutine(ref actionCoroutine);
            }
        }

        #region Editor
#if UNITY_EDITOR
        private void Reset()
        {
            _actionName = name;
        }

        private void OnValidate()
        {
            if (string.IsNullOrEmpty(_actionName))
                _actionName = name;
        }
#endif
        #endregion
    }
}