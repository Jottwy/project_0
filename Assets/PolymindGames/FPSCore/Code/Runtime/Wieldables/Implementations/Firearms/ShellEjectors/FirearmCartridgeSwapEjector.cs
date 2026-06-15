using System.Collections;
using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Cartridge-Swap Ejector")]
    public sealed class FirearmCartridgeSwapEjector : FirearmShellEjectorBehaviour
    {
        [Serializable]
        private sealed class CartridgeInfo
        {
            [SerializeField, ReorderableList(ListStyle.Lined, HasLabels = false)]
            private Renderer[] _fullCartridgeRenderers;
            
            [SerializeField, ReorderableList(ListStyle.Lined, HasLabels = false)]
            private Renderer[] _emptyCartridgeRenderers;

            public void ToggleCartridge(bool isFull)
            {
                foreach (var fullRenderer in _fullCartridgeRenderers)
                    fullRenderer.enabled = isFull;
                
                foreach (var emptyRenderer in _emptyCartridgeRenderers)
                    emptyRenderer.enabled = !isFull;
            }
        }
        
        [SerializeField, Range(0, 100), Title("Settings")]
        private int _cartridgesCount;

        [SerializeField, Range(0f, 10f)]
        private float _reloadUpdateDelay = 0.5f;
        
        [SerializeField]
        private DelayedAudioData _swapCartridgeAudio = new(null);

        [SpaceArea]
        [SerializeField, ReorderableList(elementLabel: "Cartridge")]
        private CartridgeInfo[] _cartridges;

        private IFirearmReloadableMagazine _magazine;

        public override void Eject()
        {
            Wieldable.Audio.PlayClip(_swapCartridgeAudio, BodyPoint.Hands);
            CoroutineUtility.InvokeDelayed(this, ResetShells, EjectionDuration);
        }

        public override void ResetShells()
        {
            int magSize = _magazine.Capacity;
            int currentInMag = _magazine.CurrentAmmoCount;

            int index = magSize - (magSize - currentInMag);
            if (index < _cartridgesCount)
                _cartridges[index].ToggleCartridge(false);
        }

        protected override void OnEnable()
        {
            base.OnEnable();

            _magazine = Firearm.ReloadableMagazine;
            _magazine.ReloadStarted += OnReloadStart;

            Firearm.AddChangedListener(FirearmComponentType.ReloadableMagazine, OnMagazineChanged);
        }

        private void OnDisable()
        {
            Firearm.RemoveChangedListener(FirearmComponentType.ReloadableMagazine, OnMagazineChanged);

            if (_magazine != null)
            {
                _magazine.ReloadStarted -= OnReloadStart;
                _magazine = null;
            }
        }

        private void OnMagazineChanged()
        {
            _magazine.ReloadStarted -= OnReloadStart;
            _magazine = Firearm.ReloadableMagazine;
            _magazine.ReloadStarted += OnReloadStart;
        }

        private void OnReloadStart(int loadCount)
        {
            StartCoroutine(ReloadUpdateCartridges(_magazine.Capacity, loadCount));
        }

        private IEnumerator ReloadUpdateCartridges(int currentInMag, int reloadingAmount)
        {
            yield return new WaitForTime(_reloadUpdateDelay);

            if (!_magazine.IsReloading)
                yield break;

            int numberOfCartridgesToEnable = Mathf.Clamp(currentInMag + reloadingAmount, 0, _cartridgesCount);

            for (int i = 0; i < numberOfCartridgesToEnable; i++)
                _cartridges[i].ToggleCartridge(true);
        }
    }
}