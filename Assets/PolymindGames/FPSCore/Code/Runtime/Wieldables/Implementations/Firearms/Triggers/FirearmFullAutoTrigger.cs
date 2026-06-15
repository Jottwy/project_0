using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Full-Auto Trigger")]
    public class FirearmFullAutoTrigger : FirearmTriggerBehaviour
    {
        [SerializeField, Range(0, 10000f), Title("Settings")]
        [Tooltip("The maximum number of shots that can be executed per minute.")]
        private int _roundsPerMinute = 450;

        private float _nextAllowedShotTime;

        /// <summary>
        /// Fires continuously while the trigger is held, respecting the rate of fire.
        /// </summary>
        public override void HoldTrigger()
        {
            base.HoldTrigger();

            if (Time.time >= _nextAllowedShotTime)
            {
                FireShot();
            }
        }

        /// <summary>
        /// Fires a shot and calculates the time until the next shot is allowed.
        /// </summary>
        private void FireShot()
        {
            RaiseShootEvent();
            _nextAllowedShotTime = Time.time + 60f / _roundsPerMinute;
        }
    }
}