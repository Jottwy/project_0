using PolymindGames.SaveSystem;
using UnityEngine;

namespace PolymindGames.BuildingSystem
{
    public sealed class StandingLight : MonoBehaviour, ISaveableComponent
    {
        [SerializeField, Title("Settings")]
        [Tooltip("Interaction text for when the fire is not ignited.")]
        private string _enableFireText = "Ignite Torch";

        [SerializeField]
        [Tooltip("Interaction text for when the fire is ignited.")]
        private string _disableFireText = "Extinguish Torch";

        [SerializeField, Title("Effects")]
        [Tooltip("Fire light effect component.")]
        private LightEffect _lightEffect;

        [SerializeField]
        [Tooltip("Fire audio effect component.")]
        private AudioEffect _audioEffect;

        [SerializeField, SpaceArea]
        [ReorderableList(ListStyle.Lined, HasLabels = false)]
        [Tooltip("Fire particle effect")]
        private ParticleSystem[] _fireFX;

        private IHoverableInteractable _interactable;
        private bool _fireEnabled;
        

        private void Awake()
        {
            _interactable = GetComponent<IHoverableInteractable>();
            _interactable.Interacted += TryToggleFire;
            _interactable.Description = _enableFireText;
        }
        
        private void TryToggleFire(IInteractable interactable, ICharacter character) => EnableFire(!_fireEnabled);

        private void EnableFire(bool enableFire)
        {
            _fireEnabled = enableFire;

            if (enableFire)
            {
                _lightEffect.Play();
                _audioEffect.Play();

                foreach (var fx in _fireFX)
                    fx.Play(false);
            }
            else
            {
                _lightEffect.Stop();
                _audioEffect.Stop();

                foreach (var fx in _fireFX)
                    fx.Stop();
            }

            _interactable.Description = enableFire ? _disableFireText : _enableFireText;
        }
        
        #region Save & Load
        void ISaveableComponent.LoadMembers(object data)
        {
            _fireEnabled = (bool)data;
            EnableFire(_fireEnabled);
        }

        object ISaveableComponent.SaveMembers() => _fireEnabled;
        #endregion
    }
}