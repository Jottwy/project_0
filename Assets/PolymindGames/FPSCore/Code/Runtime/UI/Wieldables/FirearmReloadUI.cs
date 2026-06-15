using PolymindGames.ProceduralMotion;
using PolymindGames.WieldableSystem;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    public sealed class FirearmReloadUI : CharacterUIBehaviour
    {
        [SerializeField, NotNull]
        private TextMeshProUGUI _text;

        [SerializeField, NotNull]
        private CanvasRenderer _canvasRenderer;

        [SerializeField, SpaceArea]
        private Gradient _color;

        [SerializeField, Range(0f, 1f)]
        private float _activatePercent = 0.35f;

        private const string Reload = "Reload";
        private const string LowAmmo = "Low Ammo";
        private const string NoAmmo = "No Ammo";

        private IFirearmReloadableMagazine _magazine;
        private IFirearm _firearm;
        private bool _isVisible;

        protected override void OnCharacterAttached(ICharacter character)
        {
            _canvasRenderer.SetAlpha(0f);
            character.GetCC<IWieldablesControllerCC>().EquippingStopped += OnWieldableEquipped;
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            character.GetCC<IWieldablesControllerCC>().EquippingStopped -= OnWieldableEquipped;

            if (_firearm != null)
            {
                _firearm.RemoveChangedListener(FirearmComponentType.ReloadableMagazine, OnMagazineChanged);
                _firearm = null;
            }

            if (_magazine != null)
            {
                _magazine.AmmoCountChanged -= UpdateMagazineText;
                _magazine = null;
            }

            SetVisibility(false);
        }

        private void OnWieldableEquipped(IWieldable wieldable)
        {
            // Unsubscribe from previous firearm & magazine events
            if (_firearm != null)
            {
                _firearm.RemoveChangedListener(FirearmComponentType.ReloadableMagazine, OnMagazineChanged);
                _firearm = null;
            }

            if (_magazine != null)
            {
                _magazine.AmmoCountChanged -= UpdateMagazineText;
                _magazine = null;
            }

            // Subscribe to new firearm if applicable
            if (wieldable is IFirearm firearm)
            {
                _firearm = firearm;
                _firearm.AddChangedListener(FirearmComponentType.ReloadableMagazine, OnMagazineChanged);
                OnMagazineChanged();
            }
            else
            {
                SetVisibility(false);
            }
        }

        private void OnMagazineChanged()
        {
            if (_magazine != null)
            {
                _magazine.AmmoCountChanged -= UpdateMagazineText;
                _magazine = null;
            }

            _magazine = _firearm?.ReloadableMagazine;

            if (_magazine != null)
            {
                _magazine.AmmoCountChanged += UpdateMagazineText;
                // Initialize UI with current ammo count.
                UpdateMagazineText(_magazine.CurrentAmmoCount, _magazine.CurrentAmmoCount);
            }
            else
            {
                SetVisibility(false);
            }
        }

        private void UpdateMagazineText(int prevAmmo, int currentAmmo)
        {
            if (_magazine == null)
            {
                SetVisibility(false);
                return;
            }

            float ammoPercent = currentAmmo / (float)_magazine.Capacity;
            bool isVisible = ammoPercent < _activatePercent;
            SetVisibility(isVisible);

            if (isVisible)
                UpdateText(ammoPercent);
        }

        private void UpdateText(float percent)
        {
            if (_text == null)
                return;

            _text.color = _color.Evaluate(percent);

            if (_firearm == null || _firearm.AmmoProvider == null)
            {
                _text.text = NoAmmo;
                return;
            }

            if (Mathf.Approximately(percent, 0f))
            {
                _text.text = _firearm.AmmoProvider.HasAmmo() ? Reload : NoAmmo;
            }
            else
            {
                _text.text = LowAmmo;
            }
        }

        private void SetVisibility(bool value)
        {
            if (_isVisible == value)
                return;

            _canvasRenderer.ClearTweens();
            _canvasRenderer.TweenCanvasRendererAlpha(value ? 1f : 0f, 0.3f)
                .SetUnscaledTime(true)
                .AutoReleaseWithParent(true);

            _isVisible = value;
        }
    }
}