using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Abstract base class for managing recoil behavior in firearms.
    /// </summary>
    public abstract class FirearmRecoilManagerBehaviour : FirearmComponentBehaviour, IFirearmRecoilManager
    {
        [SerializeField, Range(0f, 1f)]
        [Tooltip("The amount by which hipfire accuracy is reduced with each shot.")]
        private float _hipfireAccuracyKick = 0.15f;

        [SerializeField, Range(0f, 5f)]
        [Tooltip("The rate at which hipfire accuracy recovers over time.")]
        private float _hipfireAccuracyRecoveryRate = 0.4f;

        [SerializeField, Range(0f, 1f)]
        [Tooltip("The amount by which aim-down-sights (ADS) accuracy is reduced with each shot.")]
        private float _aimAccuracyKick = 0.1f;

        [SerializeField, Range(0f, 5f)]
        [Tooltip("The rate at which ADS accuracy recovers over time.")]
        private float _aimAccuracyRecoveryRate = 0.5f;

        [SerializeField, Range(0f, 1f)]
        [Tooltip("The rate at which accumulated recoil (recoil heat) recovers over time.")]
        private float _recoilRecoveryRate = 0.35f;

        [SerializeField, Range(0f, 1f)]
        [Tooltip("The delay before recoil recovery starts after shooting stops.")]
        private float _recoilRecoverDelay = 0.2f;

        protected const string AddMenuPath = "Polymind Games/Wieldables/Firearms/Recoil Managers/";

        /// <inheritdoc/>
        public float RecoilRecoveryRate => _recoilRecoveryRate;

        /// <inheritdoc/>
        public float RecoilRecoveryDelay => _recoilRecoverDelay;

        /// <inheritdoc/>
        public float HipfireAccuracyKick => _hipfireAccuracyKick;

        /// <inheritdoc/>
        public float HipfireAccuracyRecoveryRate => _hipfireAccuracyRecoveryRate;

        /// <inheritdoc/>
        public float AimAccuracyKick => _aimAccuracyKick;

        /// <inheritdoc/>
        public float AimAccuracyRecoveryRate => _aimAccuracyRecoveryRate;

        /// <summary>
        /// Gets or sets the recoil multiplier applied to the recoil behavior.
        /// </summary>
        protected float RecoilMultiplier { get; private set; } = 1f;

        /// <inheritdoc/>
        public abstract void ApplyRecoil(float accuracy, float recoilProgress, bool isAiming);

        /// <inheritdoc/>
        public virtual void SetRecoilMultiplier(float multiplier) => RecoilMultiplier = multiplier;

        /// <summary>
        /// Sets the recoil manager for the associated firearm.
        /// </summary>
        protected virtual void OnEnable()
        {
            if (Firearm != null)
                Firearm.RecoilManager = this;
        }
    }
}