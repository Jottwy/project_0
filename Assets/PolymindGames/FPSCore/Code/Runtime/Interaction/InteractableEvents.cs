using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(IHoverableInteractable))]
    public sealed class InteractableEvents : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Unity event that will be called when a character interacts with this object.")]
        private UnityEvent<ICharacter> _onInteract;

        [SerializeField]
        private UnityEvent<ICharacter> _onHoverStart;

        [SerializeField]
        private UnityEvent<ICharacter> _onHoverEnd;

        private void Start()
        {
            var interactable = GetComponent<IHoverableInteractable>();
            interactable.Interacted += OnInteract;
            interactable.HoverStarted += OnHoverStart;
            interactable.HoverEnded += OnHoverEnd;
        }

        private void OnDestroy()
        {
            var interactable = GetComponent<IHoverableInteractable>();
            interactable.Interacted -= OnInteract;
            interactable.HoverStarted -= OnHoverStart;
            interactable.HoverEnded -= OnHoverEnd;
        }

        private void OnInteract(IInteractable interactable, ICharacter character) =>
            _onInteract.Invoke(character);
        
        private void OnHoverStart(IHoverable hoverable, ICharacter character) =>
            _onHoverStart.Invoke(character);
        
        private void OnHoverEnd(IHoverable hoverable, ICharacter character) =>
            _onHoverEnd.Invoke(character);

#if UNITY_EDITOR
        private void Reset()
        {
            if (!gameObject.HasComponent<IInteractable>())
                gameObject.AddComponent<Interactable>();
        }
#endif
    }
}