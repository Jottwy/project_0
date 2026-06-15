using System;
using JetBrains.Annotations;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Defines the different states of feedback that can be triggered.
    /// </summary>
    public enum FeedbackState
    {
        /// <summary>Triggered when the selectable is in its normal state.</summary>
        Normal,

        /// <summary>Triggered when the selectable is highlighted (e.g., hovered or focused).</summary>
        Highlighted,

        /// <summary>Triggered when the selectable is selected (e.g., clicked or chosen).</summary>
        Selected,

        /// <summary>Triggered when the selectable is deselected (e.g., lost focus or unchosen).</summary>
        Deselected,

        /// <summary>Triggered when the selectable is pressed (e.g., mouse button down).</summary>
        Pressed,

        /// <summary>Triggered when the selectable is clicked (e.g., mouse button released after press).</summary>
        Clicked,

        /// <summary>Triggered when the selectable is disabled (e.g., cannot be interacted with).</summary>
        Disabled
    }

    /// <summary>
    /// Interface for selectable UI feedback, providing methods to trigger feedback for different selectable states.
    /// </summary>
    [Serializable]
    [UsedImplicitly]
    public abstract class SelectableButtonFeedback
    {
        /// <summary>
        /// Trigger feedback when the selectable is in its normal state.
        /// </summary>
        /// <param name="instant">If true, the feedback will be triggered instantly.</param>
        public virtual void OnNormal(bool instant) { }

        /// <summary>
        /// Trigger feedback when the selectable is highlighted (hovered or focused).
        /// </summary>
        /// <param name="instant">If true, the feedback will be triggered instantly.</param>
        public virtual void OnHighlighted(bool instant) { }

        /// <summary>
        /// Trigger feedback when the selectable is selected (clicked or chosen).
        /// </summary>
        /// <param name="instant">If true, the feedback will be triggered instantly.</param>
        public virtual void OnSelected(bool instant) { }

        /// <summary>
        /// Trigger feedback when the selectable is deselected (unfocused or unchosen).
        /// </summary>
        /// <param name="instant">If true, the feedback will be triggered instantly.</param>
        public virtual void OnDeselected(bool instant) { }

        /// <summary>
        /// Trigger feedback when the selectable is pressed (mouse button down).
        /// </summary>
        /// <param name="instant">If true, the feedback will be triggered instantly.</param>
        public virtual void OnPressed(bool instant) { }

        /// <summary>
        /// Trigger feedback when the selectable is clicked (mouse button released after press).
        /// </summary>
        /// <param name="instant">If true, the feedback will be triggered instantly.</param>
        public virtual void OnClicked(bool instant) { }

        /// <summary>
        /// Trigger feedback when the selectable is disabled (cannot be interacted with).
        /// </summary>
        /// <param name="instant">If true, the feedback will be triggered instantly.</param>
        public virtual void OnDisabled(bool instant) { }
        
        public virtual void Initialize(SelectableButton selectable) { }

#if UNITY_EDITOR
        /// <summary>
        /// Validate method used in the editor.
        /// </summary>
        public virtual void Validate_EditorOnly(SelectableButton selectable) { }
#endif
    }
}