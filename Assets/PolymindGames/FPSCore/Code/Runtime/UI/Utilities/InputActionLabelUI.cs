using UnityEngine.InputSystem;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    [RequireComponent(typeof(TextMeshProUGUI))]
    public sealed class InputActionLabelUI : MonoBehaviour
    {
        [SerializeField, NotNull]
        private InputActionReference _inputAction;

        [SerializeField, Range(0, 4)]
        private int _bindingIndex;

        private void Start()
        {
            if (_inputAction == null)
            {
                Debug.LogError($"ACTION NULL", gameObject);
                return;
            }

            UpdateUIText();

#if !UNITY_EDITOR
            Destroy(this);
#endif
        }

        private void UpdateUIText()
        {
            if (!TryGetComponent<TextMeshProUGUI>(out var text))
                return;

            // Get the display string for the input action's binding
            string bindingDisplayString = _inputAction.action.GetBindingDisplayString();

            // Remove any modifier keys (before the '+') and any alternate bindings (after the '/')
            string sanitizedBinding = SanitizeBindingString(bindingDisplayString);

            // Set the text in the UI
            text.text = sanitizedBinding;
        }

        /// <summary>
        /// Removes modifier keys (before '+') and selects the appropriate binding in case of alternates (after '/').
        /// </summary>
        /// <param name="bindingDisplayString">The original binding display string.</param>
        /// <returns>The sanitized binding display string.</returns>
        private string SanitizeBindingString(string bindingDisplayString)
        {
            // Remove any modifier keys before the '+'
            int plusIndex = bindingDisplayString.IndexOf('+');
            if (plusIndex != -1)
            {
                bindingDisplayString = bindingDisplayString.Substring(0, plusIndex);
            }

            // Handle alternate bindings separated by '/'
            int slashIndex = bindingDisplayString.IndexOf('/');
            if (slashIndex != -1)
            {
                var alternates = bindingDisplayString.Split('/');
                return alternates.Length > _bindingIndex ? alternates[_bindingIndex] : alternates[0];
            }

            return bindingDisplayString;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_inputAction != null)
                UpdateUIText();
        }
#endif
    }
}