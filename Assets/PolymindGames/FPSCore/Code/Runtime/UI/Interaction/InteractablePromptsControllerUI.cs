using System.Linq;

namespace PolymindGames.UserInterface
{
    public interface IInteractablePrompt
    {
        bool TrySetHoverable(IHoverable hoverable);
        void SetInteractionProgress(float interactProgress);
    }

    public sealed class InteractablePromptsControllerUI : CharacterUIBehaviour
    {
        private IInteractablePrompt _activePrompt;
        private IInteractablePrompt _defaultPrompt;
        private IInteractablePrompt[] _prompts;

        protected override void OnCharacterAttached(ICharacter character)
        {
            InitializePrompts();

            var interaction = character.GetCC<IInteractionHandlerCC>();
            interaction.HoverableInViewChanged += OnHoverableInViewChanged;
            interaction.InteractProgressChanged += SetInteractionProgress;
            interaction.InteractionEnabledChanged += OnEnabledStateChanged;

            if (interaction.Hoverable != null && interaction.InteractionEnabled)
                OnHoverableInViewChanged(interaction.Hoverable);
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            var interaction = character.GetCC<IInteractionHandlerCC>();
            interaction.HoverableInViewChanged -= OnHoverableInViewChanged;
            interaction.InteractProgressChanged -= SetInteractionProgress;
            interaction.InteractionEnabledChanged -= OnEnabledStateChanged;
        }

        private void InitializePrompts()
        {
            var prompts = gameObject.GetComponentsInFirstChildren<IInteractablePrompt>();
            
            foreach (var prompt in prompts)
                prompt.SetInteractionProgress(0f);

            _defaultPrompt = prompts.FirstOrDefault();
            _activePrompt = _defaultPrompt;

            prompts.Remove(_defaultPrompt);
            _prompts = prompts.ToArray();
        }

        private void OnEnabledStateChanged(bool enable)
        {
            if (enable)
            {
                var hoverable = Character.GetCC<IInteractionHandlerCC>().Hoverable;
                OnHoverableInViewChanged(hoverable);
            }
            else
                OnHoverableInViewChanged(null);
        }

        private void SetInteractionProgress(float progress) => _activePrompt?.SetInteractionProgress(progress);

        private void OnHoverableInViewChanged(IHoverable hoverable)
        {
            if (hoverable != null)
            {
                var prompt = GetPromptForHoverable(hoverable);
                prompt.SetInteractionProgress(0f);

                if (prompt != _activePrompt)
                {
                    _activePrompt?.TrySetHoverable(null);
                    _activePrompt = prompt;
                }
            }
            else
            {
                _activePrompt?.TrySetHoverable(null);
                _activePrompt = null;
            }
        }

        private IInteractablePrompt GetPromptForHoverable(IHoverable hoverable)
        {
            foreach (var prompt in _prompts)
            {
                if (prompt.TrySetHoverable(hoverable))
                    return prompt;
            }

            _defaultPrompt.TrySetHoverable(hoverable);
            return _defaultPrompt;
        }
    }
}