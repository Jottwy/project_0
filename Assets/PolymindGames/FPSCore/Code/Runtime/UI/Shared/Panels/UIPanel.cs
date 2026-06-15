using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Represents a basic UI panel that can be enabled/disabled and shown/hidden.
    /// </summary>
    public abstract class UIPanel : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Determines whether this panel will automatically be shown when the scene starts.")]
        private bool _showOnStart;

        [SerializeField]
        [Tooltip("Allows the panel to be hidden when the user presses the Escape key.")]
        private bool _canEscape;

        [SerializeField, Range(-1, 12)]
        [Tooltip("Specifies the layer this panel belongs to. Panels on the same layer cannot be visible simultaneously. Set to -1 to bypass this restriction.")]
        private int _panelLayer = -1;

        private bool _isDestroyed = false;
        private bool _isVisible = false;
        private bool _isActive = false;

        /// <summary>
        /// Gets a value indicating whether the panel is currently active (enabled).
        /// </summary>
        public bool IsActive => _isActive;

        /// <summary>
        /// Gets a value indicating whether the panel is currently visible (shown).
        /// </summary>
        public bool IsVisible => _isVisible;

        /// <summary>
        /// Gets the layer of the panel used for layer-based visibility management.
        /// </summary>
        public int PanelLayer => _panelLayer;

        /// <summary>
        /// Gets a value indicating whether the panel can be hidden with the escape key.
        /// </summary>
        public bool CanEscape => _canEscape;

        /// <summary>
        /// Event triggered when the panel's active state changes (enabled/disabled).
        /// </summary>
        public event UnityAction<bool> PanelStateChanged;

        /// <summary>
        /// Enables and shows the panel.
        /// </summary>
        public void Show() => SetPanelState(true);

        /// <summary>
        /// Disables and hides the panel.
        /// </summary>
        public void Hide() => SetPanelState(false);

        /// <summary>
        /// Changes the visibility of the panel.
        /// </summary>
        /// <param name="show">A boolean indicating whether to show or hide the panel.</param>
        public void ChangeVisibility(bool show)
        {
            // Ensure visibility state change is valid
            if (_isVisible == show || _isDestroyed || UnityUtility.IsQuitting)
                return;

            OnVisibilityChanged(show);
            _isVisible = show;
        }

        /// <summary>
        /// Called when the visibility of the panel changes.
        /// Derived classes must implement this method to handle visibility changes.
        /// </summary>
        /// <param name="show">A boolean indicating whether the panel is being shown or hidden.</param>
        protected abstract void OnVisibilityChanged(bool show);

        protected virtual void OnDestroy()
        {
            _isDestroyed = true;

            // Ensure the panel is hidden before destruction
            if (_isActive)
                SetPanelState(false);
        }

        /// <summary>
        /// Enables or disables the panel and triggers the visibility event.
        /// </summary>
        /// <param name="enable">A boolean indicating whether to enable or disable the panel.</param>
        private void SetPanelState(bool enable)
        {
            // Only proceed if the panel's state needs to change
            if (_isActive == enable && !_isVisible)
                return;
            
            bool wasActive = _isActive;
            _isActive = enable;

            if (enable) UIPanelManager.ShowPanel(this);
            else UIPanelManager.HidePanel(this);

            // Trigger the PanelToggled event if the active state has changed
            if (_isActive != wasActive)
                PanelStateChanged?.Invoke(_isActive);
        }

        private void Start()
        {
            if (_showOnStart)
                SetPanelState(true);
        }
    }
}