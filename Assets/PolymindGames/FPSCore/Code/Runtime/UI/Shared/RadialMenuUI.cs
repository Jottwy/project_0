using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// A UI component that manages a radial menu, allowing users to highlight and select options 
    /// based on directional input.
    /// </summary>
    public sealed class RadialMenuUI : MonoBehaviour
    {
        [SerializeField, NotNull]
        private SelectableGroupBase _group;

        [SerializeField, NotNull]
        private UIPanel _panel;

        [SerializeField, Range(0.1f, 25f), Title("Settings")]
        private float _sensitivity = 3f;

        [SerializeField, Range(-90f, 90f)]
        private float _angleOffset = -30f;

        [SerializeField, Range(0.1f, 25f)]
        private float _range = 3f;

        private Vector2 _cursorDirection;

        /// <summary>
        /// Occurs when the highlighted selectable UI element changes.
        /// </summary>
        public event UnityAction<SelectableButton> HighlightedChanged
        {
            add => _group.HighlightedChanged += value;
            remove => _group.HighlightedChanged -= value;
        }

        /// <summary>
        /// Occurs when the selected selectable UI element changes.
        /// </summary>
        public event UnityAction<SelectableButton> SelectedChanged
        {
            add => _group.SelectedChanged += value;
            remove => _group.SelectedChanged -= value;
        }

        /// <summary>
        /// Gets the currently highlighted selectable UI element.
        /// </summary>
        public SelectableButton Highlighted => _group.Highlighted;

        /// <summary>
        /// Gets the currently selected selectable UI element.
        /// </summary>
        public SelectableButton Selected => _group.Selected;

        /// <summary>
        /// Gets a read-only list of selectable UI elements registered in the group.
        /// </summary>
        public IReadOnlyList<SelectableButton> Selectables => _group.RegisteredSelectables;

        /// <summary>
        /// Displays the radial menu and resets the cursor direction.
        /// </summary>
        public void ShowMenu()
        {
            _cursorDirection = Vector2.zero;
            _panel.Show();
        }

        /// <summary>
        /// Hides the radial menu.
        /// </summary>
        public void HideMenu()
        {
            _panel.Hide();
        }

        /// <summary>
        /// Updates the highlighted selection based on the provided directional input.
        /// </summary>
        /// <param name="input">The directional input for selecting a UI element.</param>
        public void UpdateHighlightedSelection(Vector2 input)
        {
            var highlighted = GetHighlightedSelectable(input);
            if (highlighted == null)
                return;

            if (highlighted != _group.Highlighted)
                HandleSlotHighlighting(highlighted);
        }

        /// <summary>
        /// Selects the UI element at the specified index.
        /// </summary>
        /// <param name="index">The index of the UI element to select.</param>
        public void SelectAtIndex(int index)
        {
            if (index < 0 || index >= _group.RegisteredSelectables.Count)
            {
                Debug.LogError("Index out of bounds");
                return;
            }

            var selectable = _group.RegisteredSelectables[index];
            HandleSlotHighlighting(selectable);
            selectable.Select();
        }

        /// <summary>
        /// Determines the highlighted selectable UI element based on the input direction.
        /// </summary>
        /// <param name="input">The directional input.</param>
        /// <returns>The highlighted selectable UI element.</returns>
        private SelectableButton GetHighlightedSelectable(Vector2 input)
        {
            if (input == Vector2.zero)
                return null;

            Vector2 directionOfSelection = input.normalized * _range;

            // Update _cursorDirection based on input
            _cursorDirection = Vector2.Lerp(_cursorDirection, directionOfSelection, Time.deltaTime * _sensitivity);

            // Calculate the angle between the cursor direction and Vector2.up (0 degrees)
            float angle = -Vector2.SignedAngle(Vector2.up, _cursorDirection);

            // Ensure the angle is within the 0-360 degrees range, adjusting for _angleOffset
            angle = Mathf.Repeat(angle + _angleOffset, 360f);

            // Get the list of selectable UI elements
            var selectables = _group.RegisteredSelectables;

            // Calculate the index of the selectable based on the angle
            int index = Mathf.FloorToInt(angle * selectables.Count / 360f);

            // Ensure the index is within bounds
            index = (index + selectables.Count) % selectables.Count;

            // Return the selected UI element
            return selectables[index];
        }

        /// <summary>
        /// Handles the highlighting of a selectable UI element.
        /// </summary>
        /// <param name="highlighted">The UI element to highlight.</param>
        private void HandleSlotHighlighting(SelectableButton highlighted)
        {
            if (_group.Highlighted != null)
                _group.Highlighted.OnPointerExit(null);

            highlighted.OnPointerEnter(null);
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (_group == null)
                return;

            var selectables = _group.GetComponentsInChildren<SelectableButton>();

            Vector3 center = _group.transform.position;
            float angleIncrement = 360f / selectables.Length;

            for (int i = 0; i < selectables.Length; i++)
            {
                Gizmos.color = i == 0 ? Color.blue : Color.red;

                Gizmos.DrawRay(center, Quaternion.Euler(0, 0, angleIncrement * i + _angleOffset) * Vector3.up * 150);

                if (i < selectables.Length - 1)
                    Gizmos.DrawRay(center, Quaternion.Euler(0, 0, angleIncrement * (i + 1) + _angleOffset) * Vector3.up * 150);
            }
        }
#endif
    }
}