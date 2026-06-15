using PolymindGames.WieldableSystem;
using PolymindGames.Options;
using UnityEngine.UI;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/user-interface/behaviours/ui_wieldables#ammo")]
    public sealed class FirearmAmmoUI : CharacterUIBehaviour
    {
        [SerializeField, NotNull, Title("Magazine")]
        [Tooltip("A UI text component that's used for displaying the current ammo in the magazine.")]
        private TextMeshProUGUI _magazineText;

        [SerializeField]
        [Tooltip("The color gradient for visualizing the current ammo in the magazine.")]
        private Gradient _magazineColor;

        [SerializeField, Title("Storage")]
        [Tooltip("An image component that represents infinite storage of ammo.")]
        private Image _infiniteStorageImage;

        [SerializeField, NotNull]
        [Tooltip("A UI text component that's used for displaying the current ammo in the storage.")]
        private TextMeshProUGUI _storageText;

        [SerializeField, Title("Animation")]
        private Animation _animation;
        
        [SerializeField]
        [Tooltip("The animation clip for showing the ammo UI.")]
        private AnimationClip _showAnimation;

        [SerializeField]
        [Tooltip("The animation clip for updating the ammo UI.")]
        private AnimationClip _updateAnimation;

        [SerializeField]
        [Tooltip("The animation clip for hiding the ammo UI.")]
        private AnimationClip _hideAnimation;

        private IFirearmReloadableMagazine _magazine;
        private IFirearmAmmoProvider _ammoProvider;
        private IFirearm _firearm;
        private bool _isVisible;

        protected override void OnCharacterAttached(ICharacter character)
        {
            _infiniteStorageImage.enabled = false;
            _isVisible = false;
            character.GetCC<IWieldablesControllerCC>().EquippingStopped += OnWieldableEquipped;
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            character.GetCC<IWieldablesControllerCC>().EquippingStopped -= OnWieldableEquipped;

            if (_firearm != null)
            {
                _firearm.RemoveChangedListener(FirearmComponentType.AmmoProvider, OnAmmoChanged);
                _firearm.RemoveChangedListener(FirearmComponentType.ReloadableMagazine, OnMagazineChanged);
                _firearm = null;
                
                if (_magazine != null)
                    _magazine.AmmoCountChanged -= UpdateMagazineText;

                if (_ammoProvider != null)
                    _ammoProvider.AmmoCountChanged -= UpdateText;
            }
        }

        private void OnWieldableEquipped(IWieldable wieldable)
        {
            if (wieldable == _firearm)
                return;
            
            // Unsubscribe from previous firearm
            if (_firearm != null)
            {
                _firearm.RemoveChangedListener(FirearmComponentType.AmmoProvider, OnAmmoChanged);
                _firearm.RemoveChangedListener(FirearmComponentType.ReloadableMagazine, OnMagazineChanged);
                _firearm = null;

                OnMagazineChanged();
                OnAmmoChanged();
            }

            if (wieldable is IFirearm firearm)
            {
                // Subscribe to current firearm
                _firearm = firearm;
                _firearm.AddChangedListener(FirearmComponentType.AmmoProvider, OnAmmoChanged);
                _firearm.AddChangedListener(FirearmComponentType.ReloadableMagazine, OnMagazineChanged);

                OnMagazineChanged();
                OnAmmoChanged();
                
                if (!_isVisible)
                {
                    _animation.PlayClip(_showAnimation);
                    _isVisible = true;
                }
            }
            else if (_isVisible)
            {
                _animation.PlayClip(_hideAnimation);
                _isVisible = false;
            }
        }
        
        private void OnMagazineChanged()
        {
            if (_magazine != null)
                _magazine.AmmoCountChanged -= UpdateMagazineText;

            _magazine = _firearm?.ReloadableMagazine;

            if (_magazine != null)
            {
                _magazine.AmmoCountChanged += UpdateMagazineText;
                UpdateMagazineText(_magazine.CurrentAmmoCount, _magazine.CurrentAmmoCount);
            }
        }

        private void OnAmmoChanged()
        {
            // Prev ammo
            if (_ammoProvider != null)
                _ammoProvider.AmmoCountChanged -= UpdateText;

            _ammoProvider = _firearm?.AmmoProvider;

            // Current ammo
            if (_ammoProvider != null)
            {
                _ammoProvider.AmmoCountChanged += UpdateText;
                UpdateText(_ammoProvider.GetAmmoCount());
            }
        }

        private void UpdateMagazineText(int prevAmmo, int currentAmmo)
        {
            _magazineText.text = currentAmmo.ToString();
            
            float t = (_magazine.Capacity > 0) ? currentAmmo / (float)_magazine.Capacity : 0f;
            _magazineText.color = _magazineColor.Evaluate(t);

            if (prevAmmo > currentAmmo)
                _animation.PlayClip(_updateAnimation);
        }

        private void UpdateText(int currentAmmo)
        {
            if (GameplayOptions.Instance.InfiniteStorageAmmo || currentAmmo > 100000)
            {
                _storageText.text = string.Empty;
                _infiniteStorageImage.enabled = true;
            }
            else
            {
                _storageText.text = currentAmmo.ToString();
                _infiniteStorageImage.enabled = false;
            }
        }
    }
}