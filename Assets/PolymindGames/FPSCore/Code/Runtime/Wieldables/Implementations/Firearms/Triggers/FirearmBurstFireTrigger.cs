using System.Collections;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Burst-Fire Trigger")]
    public class FirearmBurstFireTrigger : FirearmTriggerBehaviour
    {
        [SerializeField, Range(0, 100), Title("Settings")]
        [Tooltip("How many times in succession will the trigger be pressed.")]
        private int _burstLength = 3;

        [SerializeField, Range(0f, 10f)]
        [Tooltip("How much time it takes to complete the burst.")]
        private float _burstDuration = 0.5f;

        [SerializeField, Range(0f, 10f)]
        [Tooltip("How much extra time to wait before being able to shoot again.")]
        private float _burstPause = 0.3f;

        private float _shootTimer;

        protected override void TapTrigger()
        {
            if (Time.time < _shootTimer)
                return;

            if (Firearm.ReloadableMagazine.IsReloading || Firearm.ReloadableMagazine.IsMagazineEmpty())
                RaiseShootEvent();
            else
                StartCoroutine(DoBurst());

            _shootTimer = Time.time + _burstDuration + _burstPause;
        }

        private IEnumerator DoBurst()
        {
            for (int i = 0; i < _burstLength; i++)
            {
                RaiseShootEvent();
                yield return new WaitForTime(_burstDuration / _burstLength);
            }
        }
    }
}