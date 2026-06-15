using UnityEngine.UI;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/user-interface/behaviours/ui_interaction")]
    public sealed class GenericInteractionPromptUI : MonoBehaviour, IInteractablePrompt
    {
        [SerializeField, NotNull]
        private UIPanel _panel;

        [SerializeField]
        private WorldToUIScreenPositioner _screenPositioner;

        [SerializeField, Title("Interaction Info")]
        [Tooltip("A UI text component that's used for displaying the current interactable's name.")]
        private TextMeshProUGUI _nameText;

        [SerializeField]
        [Tooltip("A UI text component that's used for displaying the current interactable's description.")]
        private TextMeshProUGUI _descriptionText;

        [SerializeField]
        [Tooltip("An image that used in showing the time the current interactable has been interacted with.")]
        private Image _interactProgress;

        [SerializeField, NotNull]
        private GameObject _inputRoot;

        private IHoverable _hoverable;

        public bool TrySetHoverable(IHoverable hoverable)
        {
            if (_hoverable != null)
                _hoverable.DescriptionChanged -= OnDescriptionChanged;
            
            if (hoverable != null && !string.IsNullOrEmpty(hoverable.Title))
            {
                _hoverable = hoverable; 
                
                hoverable.DescriptionChanged += OnDescriptionChanged;
                OnDescriptionChanged();
                
                if (_screenPositioner != null)
                    _screenPositioner.SetTargetTransform(hoverable.transform, hoverable.CenterOffset);
                
                _inputRoot.gameObject.SetActive(hoverable is IInteractable { InteractionEnabled: true });
                
                _panel.Show();
            }
            else
            {
                _hoverable = null;
                _panel.Hide();
                
                if (_screenPositioner != null)
                    _screenPositioner.SetTargetTransform(null);
            }

            return true;
        }

        public void SetInteractionProgress(float progress) => _interactProgress.fillAmount = progress;

        private void OnDescriptionChanged()
        {
            _nameText.text = _hoverable.Title;
            _descriptionText.text = _hoverable.Description;
        }
    }
}