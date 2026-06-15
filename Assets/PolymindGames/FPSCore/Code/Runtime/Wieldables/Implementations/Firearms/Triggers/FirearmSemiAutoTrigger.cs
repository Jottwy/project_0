using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Semi-Auto Trigger")]
    public class FirearmSemiAutoTrigger : FirearmTriggerBehaviour
    {
        [SerializeField, Range(0f, 10f)]
        [Tooltip("The minimum time that can pass between consecutive shots.")]
        private float _shotCooldown;

        [SerializeField, Range(0f, 1f)]
        [Tooltip("The duration for which an input can be buffered.")]
        private float _inputBufferDuration = 0f;

        private float _nextAllowedShotTime;
        private float _bufferExpiryTime;

        /// <summary>
        /// Handles trigger input for shooting.
        /// </summary>
        protected override void TapTrigger()
        {
            if (Time.time < _nextAllowedShotTime)
            {
                // Buffer the input if the shot cannot be fired yet.
                if (_inputBufferDuration > 0.001f)
                    _bufferExpiryTime = Time.time + _inputBufferDuration;
                
                return;
            }

            FireShot();
        }

        /// <summary>
        /// Updates the input buffer logic and checks if a buffered shot should be triggered.
        /// </summary>
        private void Update()
        {
            // Check if buffered input is enabled and a buffered shot should be fired.
            if (_bufferExpiryTime > Time.time && Time.time >= _nextAllowedShotTime)
            {
                FireShot();
            }
        }

        /// <summary>
        /// Fires a shot and updates the cooldown timer.
        /// </summary>
        private void FireShot()
        {
            RaiseShootEvent();
            _nextAllowedShotTime = Time.time + _shotCooldown;
            _bufferExpiryTime = 0f;
        }
    }
}