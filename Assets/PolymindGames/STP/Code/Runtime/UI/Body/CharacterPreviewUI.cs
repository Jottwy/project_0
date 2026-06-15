using UnityEngine;

namespace PolymindGames.UserInterface
{
    public sealed class CharacterPreviewUI : CharacterUIBehaviour
    {
        [SerializeField, NotNull]
        private Camera _camera;
        
        [SerializeField, NotNull]
        private GameObject _characterVisuals;
        
        [SerializeField, NotNull]
        private CharacterClothing _characterClothing;
        
        protected override void Awake()
        {
            base.Awake();

            _camera.forceIntoRenderTexture = true;
            _characterVisuals.SetActive(false);
        }

        protected override void OnCharacterAttached(ICharacter character)
        {
            if (character.TryGetCC(out IInventoryInspectionManagerCC inspection))
            {
                inspection.InspectionStarted += OnInspectionStarted;
                inspection.InspectionEnded += OnInspectionEnded;
            }
            
            _characterClothing.AttachToCharacter(character);
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            if (character.TryGetCC(out IInventoryInspectionManagerCC inspection))
            {
                inspection.InspectionStarted -= OnInspectionStarted;
                inspection.InspectionEnded -= OnInspectionEnded;
            }
            
            _characterClothing.DetachFromCharacter();
        }

        private void OnInspectionStarted() => _characterVisuals.SetActive(true);
        private void OnInspectionEnded() => _characterVisuals.SetActive(false);
    }
}