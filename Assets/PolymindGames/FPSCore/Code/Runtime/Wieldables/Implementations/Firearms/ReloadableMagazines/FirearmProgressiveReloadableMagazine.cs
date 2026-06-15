using System.Collections;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Progressive Reloadable-Magazine")]
    public class FirearmProgressiveReloadableMagazine : FirearmReloadableMagazineBehaviour
    {
        [Title("Tactical Reload")]
        [SerializeField, Range(0f, 10f)]
        private float _reloadBeginDuration = 0.5f;

        [SerializeField, Range(0.1f, 2f)]
        private float _reloadBeginAnimSpeed = 1f;

        [SerializeField]
        private AudioSequence _reloadBeginAudio;

        [SerializeField, Range(0f, 10f), Line]
        private float _reloadLoopDuration = 0.35f;

        [SerializeField, Range(0.1f, 2f)]
        private float _reloadLoopAnimSpeed = 1f;

        [SerializeField]
        private AudioSequence _reloadLoopAudio;

        [SerializeField, Range(0f, 10f), Line]
        private float _reloadEndDuration = 0.5f;

        [SerializeField, Range(0.1f, 2f)]
        private float _reloadEndAnimSpeed = 1f;

        [SerializeField]
        private AudioSequence _reloadEndAudio;

        [SerializeField, Title("Empty Reload")]
        private ReloadType _emptyReloadType = ReloadType.Standard;

        [SerializeField, Range(0f, 15f)]
        [HideIf(nameof(_emptyReloadType), ReloadType.None)]
        private float _emptyReloadDuration = 3f;

        [SerializeField, Range(0.1f, 2f)]
        [HideIf(nameof(_emptyReloadType), ReloadType.None)]
        private float _emptyReloadAnimSpeed = 1f;

        [SerializeField]
        [HideIf(nameof(_emptyReloadType), ReloadType.None)]
        private AudioData _emptyReloadAudio = new(null);

        public override bool TryBeginReload(IFirearmAmmoProvider ammoProvider)
        {
            if (IsReloading || IsMagazineFull)
                return false;

            AmmoToLoad = Capacity - CurrentAmmoCount;
            int currentInStorage = ammoProvider.GetAmmoCount();

            if (currentInStorage < AmmoToLoad)
                AmmoToLoad = currentInStorage;

            if (!IsMagazineFull && AmmoToLoad > 0)
            {
                IsReloading = true;

                if (IsMagazineEmpty && _emptyReloadType != ReloadType.None)
                {
                    OnEmptyReloadStart(ammoProvider);
                }
                else
                {
                    OnTacticalReloadStart(ammoProvider);
                }

                return true;
            }

            return false;
        }
        
        public override bool TryCancelReload(IFirearmAmmoProvider ammoProvider, out float endDuration)
        {
            endDuration = _reloadEndDuration;

            if (!IsReloading)
                return false;

            AmmoToLoad = 0;
            IsReloading = false;

            return true;
        }

        /// <summary>
        /// Starts the tactical reload process as a coroutine.
        /// </summary>
        protected virtual void OnTacticalReloadStart(IFirearmAmmoProvider ammoProvider)
            => StartCoroutine(ReloadSequence(ammoProvider));

        /// <summary>
        /// Starts the empty reload process as a coroutine.
        /// </summary>
        protected virtual void OnEmptyReloadStart(IFirearmAmmoProvider ammoProvider)
            => StartCoroutine(EmptyReloadSequence(ammoProvider));

        /// <summary>
        /// Handles the full reload process when there is remaining ammo to load.
        /// </summary>
        private IEnumerator ReloadSequence(IFirearmAmmoProvider ammoProvider)
        {
            yield return BeginReloadSequence();

            // Reload loop for inserting shells
            while (AmmoToLoad > 0 && IsReloading)
            {
                ConfigureAnimator(_reloadLoopAnimSpeed, false, true);
                var audioSource = Wieldable.Audio.PlayClips(_reloadLoopAudio, BodyPoint.Hands);
                
                yield return WaitForReloadPhase(_reloadLoopDuration, audioSource);

                // Stop if the reload process is interrupted
                if (!IsReloading) 
                    break;

                ammoProvider.RemoveAmmo(1);
                CurrentAmmoCount++;
                AmmoToLoad--;
            }

            yield return EndReloadSequence();
        }

        /// <summary>
        /// Begins the reload animation sequence and waits for its completion.
        /// </summary>
        private IEnumerator BeginReloadSequence()
        {
            IsReloading = true;
            
            ConfigureAnimator(_reloadBeginAnimSpeed, false, true);
            var audioSource = Wieldable.Audio.PlayClips(_reloadBeginAudio, BodyPoint.Hands);

            yield return WaitForReloadPhase(_reloadBeginDuration, audioSource);
        }

        /// <summary>
        /// Ends the reload animation sequence and waits for its completion.
        /// </summary>
        private IEnumerator EndReloadSequence()
        {
            ConfigureAnimator(_reloadEndAnimSpeed, false, false);
            var audioSource = Wieldable.Audio.PlayClips(_reloadEndAudio, BodyPoint.Hands);
            
            yield return WaitForReloadPhase(_reloadEndDuration, audioSource);
            
            IsReloading = false;
        }

        /// <summary>
        /// Handles the process for an empty reload, transitioning to a full reload if necessary.
        /// </summary>
        private IEnumerator EmptyReloadSequence(IFirearmAmmoProvider ammoProvider)
        {
            ConfigureAnimator(_emptyReloadAnimSpeed, true, true);
            var audioSource = Wieldable.Audio.PlayClip(_emptyReloadAudio, BodyPoint.Hands);

            yield return WaitForReloadPhase(_emptyReloadDuration, audioSource);

            if (_emptyReloadType == ReloadType.Progressive)
            {
                ammoProvider.RemoveAmmo(1);
                CurrentAmmoCount++;
                AmmoToLoad--;

                yield return ReloadSequence(ammoProvider);
            }
            else
            {
                // Completes the full reload process when the firearm is fully reloaded.
                ConfigureAnimator(_reloadEndAnimSpeed, false, false);
                ammoProvider.RemoveAmmo(AmmoToLoad);
                CurrentAmmoCount += AmmoToLoad;
                AmmoToLoad = 0;
                IsReloading = false;
            }
        }

        /// <summary>
        /// Configures the animator parameters for the reload process.
        /// </summary>
        private void ConfigureAnimator(float animSpeed, bool isEmpty, bool isReloading)
        {
            var animator = Wieldable.Animator;
            animator.SetFloat(AnimationConstants.ReloadSpeed, animSpeed);
            animator.SetBool(AnimationConstants.IsEmpty, isEmpty);
            animator.SetBool(AnimationConstants.IsReloading, isReloading);
        }

        /// <summary>
        /// Waits for a specified duration and ensures the reloading process is uninterrupted.
        /// </summary>
        private IEnumerator WaitForReloadPhase(float duration, AudioSource audioSource)
        {
            float endTime = Time.time + duration;
            while (IsReloading && Time.time < endTime)
            {
                if (!IsReloading)
                {
                    audioSource.Stop();
                    yield break;
                }

                yield return null;
            }
        }

        #region Internal Types
        protected enum ReloadType
        {
            None,
            Standard,
            Progressive
        }
        #endregion
    }
}