using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Abstract base class for handling aiming functionality in firearms.
    /// </summary>
    public abstract class FirearmAimHandlerBehaviour : FirearmComponentBehaviour, IFirearmAimHandler
    {
        [SerializeField, Range(-1, 100)]
        private int _aimCrosshairIndex;

        [SerializeField, Range(0f, 1f)]
        private float _aimMovementSpeed = 1f;

        [SerializeField, Range(0f, 1f)]
        private float _aimAccuracy = 1f;

        [SerializeField, Range(0f, 1f)]
        private float _aimRecoil = 1f;

        protected const string AddMenuPath = "Polymind Games/Wieldables/Firearms/Aim Handlers/";

        /// <inheritdoc/>
        public bool IsAiming { get; private set; }

        /// <inheritdoc/>
        public float FireAccuracyModifier => _aimAccuracy;

        /// <inheritdoc/>
        public virtual bool StartAiming()
        {
            if (IsAiming)
                return false;

            Firearm?.RecoilManager.SetRecoilMultiplier(_aimRecoil);

            if (Wieldable is ICrosshairHandler crosshair)
                crosshair.CrosshairIndex = _aimCrosshairIndex;

            if (Wieldable is IMovementSpeedHandler speed)
                speed.SpeedModifier.AddModifier(GetMovementSpeedMultiplier);

            IsAiming = true;

            return true;
        }

        /// <inheritdoc/>
        public virtual bool StopAiming()
        {
            if (!IsAiming)
                return false;
            
            Firearm?.RecoilManager.SetRecoilMultiplier(1f);

            if (Wieldable is ICrosshairHandler crosshair)
                crosshair.ResetCrosshair();

            if (Wieldable is IMovementSpeedHandler speed)
                speed.SpeedModifier.RemoveModifier(GetMovementSpeedMultiplier);

            IsAiming = false;

            return true;
        }

        /// <summary>
        /// Sets the aim handler for the associated firearm.
        /// </summary>
        protected virtual void OnEnable()
        {
            if (Firearm != null)
            {
                Firearm.AimHandler = this;
                Firearm.RecoilManager.SetRecoilMultiplier(1f);
                Firearm.AddChangedListener(FirearmComponentType.RecoilManager, RecoilChanged);
            }
        }

        /// <summary>
        /// Removes listeners and stops aiming.
        /// </summary>
        protected virtual void OnDisable()
        {
            Firearm?.RemoveChangedListener(FirearmComponentType.RecoilManager, RecoilChanged);
            StopAiming();
        }

        private void RecoilChanged() => Firearm.RecoilManager.SetRecoilMultiplier(_aimRecoil);
        private float GetMovementSpeedMultiplier() => _aimMovementSpeed;
    }
}