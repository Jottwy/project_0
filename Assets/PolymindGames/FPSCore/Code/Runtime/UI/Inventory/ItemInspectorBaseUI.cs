using UnityEngine;

namespace PolymindGames.UserInterface
{
    public abstract class ItemInspectorBaseUI : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("The panel UI component used for displaying item information.")]
        private UIPanel _panel;
        
        /// <summary>
        /// Sets the inspected slot to display item information.
        /// </summary>
        /// <param name="slot">The item slot to inspect.</param>
        public void SetInspectedSlot(ItemSlotUIBase slot)
        {
            if (slot == null || !slot.HasItem)
            {
                if (_panel.IsActive)
                    _panel.Hide();

                return;
            }

            if (!_panel.IsActive)
                _panel.Show();

            UpdateInspectionUI(slot);
        }
        
        /// <summary>
        /// Updates the UI to reflect the details of the inspected item slot.
        /// This method must be implemented by derived classes.
        /// </summary>
        /// <param name="slot">The item slot to inspect.</param>
        protected abstract void UpdateInspectionUI(ItemSlotUIBase slot);

        protected virtual void OnValidate()
        {
            if (_panel == null)
                _panel = gameObject.GetOrAddComponent<UIPanel>();
        }
    }
}