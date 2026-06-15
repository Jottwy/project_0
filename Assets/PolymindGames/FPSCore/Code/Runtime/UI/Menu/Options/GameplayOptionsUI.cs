using PolymindGames.Options;
using UnityEngine.UI;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    public partial class GameplayOptionsUI : UserOptionsUI<GameplayOptions>
    {
        [SerializeField]
        private Toggle _autoSave;
        
        [SerializeField]
        private Slider _autoSaveInterval;
        
        [SerializeField]
        private Toggle _cancelReloadOnShoot;
        
        [SerializeField]
        private Toggle _autoReloadOnDry;
        
        [SerializeField]
        private Toggle _canAimWhileReloading; 
        
        [SerializeField]
        private Toggle _manualShellEjection;
        
        [SerializeField]
        private Toggle _infiniteMagazineAmmo;

        [SerializeField]
        private Toggle _infiniteStorageAmmo;

        protected override void Start()
        {
            base.Start();
            
            _autoSave.onValueChanged.AddListener(UserOptions.AutosaveEnabled.SetValue);
            _cancelReloadOnShoot.onValueChanged.AddListener(UserOptions.CancelReloadOnShoot.SetValue);
            _autoReloadOnDry.onValueChanged.AddListener(UserOptions.AutoReloadOnDry.SetValue);
            _canAimWhileReloading.onValueChanged.AddListener(UserOptions.CanAimWhileReloading.SetValue);
            _manualShellEjection.onValueChanged.AddListener(UserOptions.ManualShellEjection.SetValue);
            _autoSaveInterval.onValueChanged.AddListener(UserOptions.AutosaveInterval.SetValue);
            
#if DEBUG
            if (_infiniteMagazineAmmo != null)
                _infiniteMagazineAmmo.onValueChanged.AddListener(UserOptions.InfiniteMagazineAmmo.SetValue);
            
            if (_infiniteStorageAmmo != null)
                _infiniteStorageAmmo.onValueChanged.AddListener(UserOptions.InfiniteStorageAmmo.SetValue);
#else
            _infiniteStorageAmmo.transform.parent.gameObject.SetActive(false);
            _infiniteMagazineAmmo.transform.parent.gameObject.SetActive(false);
#endif
        }

        protected override void ResetUIState()
        {
            _autoSave.isOn = UserOptions.AutosaveEnabled;
            _cancelReloadOnShoot.isOn = UserOptions.CancelReloadOnShoot;
            _autoReloadOnDry.isOn = UserOptions.AutoReloadOnDry;
            _canAimWhileReloading.isOn = UserOptions.CanAimWhileReloading;
            _manualShellEjection.isOn = UserOptions.ManualShellEjection;

            _autoSaveInterval.minValue = 1f;
            _autoSaveInterval.maxValue = GameplayOptions.MaxAutoSaveInterval;
            _autoSaveInterval.wholeNumbers = true;
            _autoSaveInterval.value = UserOptions.AutosaveInterval;
            
#if DEBUG
            if (_infiniteMagazineAmmo != null)
                _infiniteMagazineAmmo.isOn = UserOptions.InfiniteMagazineAmmo;
            
            if (_infiniteStorageAmmo != null)
                _infiniteStorageAmmo.isOn = UserOptions.InfiniteStorageAmmo;
#endif
        }
    }
}