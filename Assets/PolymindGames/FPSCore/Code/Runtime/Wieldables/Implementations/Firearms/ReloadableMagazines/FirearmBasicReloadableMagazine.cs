using PolymindGames.Options;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Represents a basic reloadable magazine for a firearm with configurable reload behavior.
    /// Handles ammo usage, reloading, and canceling reload actions.
    /// </summary>
    [AddComponentMenu(AddMenuPath + "Basic Reloadable Magazine")]
    public class FirearmBasicReloadableMagazine : FirearmReloadableMagazineBehaviour
    {
        [SerializeField, Range(0.1f, 15f), Title("Tactical Reload")]
        [Tooltip("Duration of the reload process in seconds.")]
        private float _reloadDuration = 1f;

        [SerializeField, Range(0.1f, 2f)]
        [Tooltip("Speed modifier for the reload animation.")]
        private float _reloadAnimSpeed = 1f;

        [SerializeField]
        [Tooltip("Audio clip to play during the reload.")]
        private AudioSequence _reloadAudio;
        
        [SerializeField, Range(0f, 15f), Title("Empty Reload")]
        [Help("Enable empty reload by setting a duration greater than 0.", UnityMessageType.None, Order = 2000)]
        private float _emptyReloadDuration = 0f;

        [SerializeField, Range(0.1f, 2f)]
        [ShowIf(nameof(_emptyReloadDuration), 0.01f, Comparison = UnityComparisonMethod.GreaterEqual)]
        private float _emptyReloadAnimSpeed = 1f;

        [SerializeField]
        [ShowIf(nameof(_emptyReloadDuration), 0.01f, Comparison = UnityComparisonMethod.GreaterEqual)]
        private AudioSequence _emptyReloadAudio;

        private AudioSource _audioSource;
        private Coroutine _reloadCoroutine;
        private float _reloadEndTime;

        public sealed override bool TryBeginReload(IFirearmAmmoProvider ammoProvider)
        {
            bool emptyReload = _emptyReloadDuration > 0.01f && IsMagazineEmpty;
            if (TryStartReloadProcess(ammoProvider, emptyReload ? _emptyReloadDuration : _reloadDuration))
            {
                if (emptyReload) OnEmptyReload();
                else OnTacticalReload();
                
                return true;
            }

            return false;
        }
        
        /// <summary>
        /// Attempts to cancel the ongoing reload process.
        /// </summary>
        /// <param name="ammoProvider">The ammo provider involved in the reload.</param>
        /// <param name="endDuration">The delay duration for ending the reload.</param>
        /// <returns>True if the reload was successfully canceled; otherwise, false.</returns>
        public override bool TryCancelReload(IFirearmAmmoProvider ammoProvider, out float endDuration)
        {
            endDuration = 0f;
            if (!IsReloading || !GameplayOptions.Instance.CancelReloadOnShoot)
                return false;

            ammoProvider.AddAmmo(AmmoToLoad);
            AmmoToLoad = 0;
            
            if (_audioSource != null)
                _audioSource.Stop();
            
            StopCoroutine(_reloadCoroutine);
            EndReload();

            return true;
        }
        
        protected virtual void OnTacticalReload()
        {
            var animator = Wieldable.Animator;
            animator.SetFloat(AnimationConstants.ReloadSpeed, _reloadAnimSpeed);
            animator.SetBool(AnimationConstants.IsEmpty, false);
            animator.SetBool(AnimationConstants.IsReloading, true);
            _audioSource = Wieldable.Audio.PlayClips(_reloadAudio, BodyPoint.Torso);
        }

        protected virtual void OnEmptyReload()
        {
            var animator = Wieldable.Animator;
            animator.SetFloat(AnimationConstants.ReloadSpeed, _emptyReloadAnimSpeed);
            animator.SetBool(AnimationConstants.IsEmpty, true);
            animator.SetBool(AnimationConstants.IsReloading, true);
            _audioSource = Wieldable.Audio.PlayClips(_emptyReloadAudio, BodyPoint.Hands);
        }
        
        /// <summary>
        /// Starts the reload process, handling ammo transfer and animations.
        /// </summary>
        /// <returns>True if the reload process started; otherwise, false.</returns>
        private bool TryStartReloadProcess(IFirearmAmmoProvider ammoProvider, float duration)
        {
            if (IsReloading || IsMagazineFull || _reloadCoroutine != null)
                return false;

            AmmoToLoad = ammoProvider.RemoveAmmo(Capacity - CurrentAmmoCount);

            if (AmmoToLoad > 0)
            {
                IsReloading = true;
                _reloadCoroutine = CoroutineUtility.InvokeDelayed(this, EndReload, duration);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Completes the reload process, updating ammo count and animation states.
        /// </summary>
        private void EndReload()
        {
            Wieldable.Animator.SetBool(AnimationConstants.IsReloading, false);
            CurrentAmmoCount += AmmoToLoad;
            IsReloading = false;
            _reloadCoroutine = null;
        }
    }
}