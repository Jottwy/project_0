using System.Collections;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu("Polymind Games/Wieldables/Healing Wieldable")]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault2)]
    public sealed class HealingWieldable : Wieldable, IUseInputHandler
    {
        [SerializeField, Range(0.1f, 5f)]
        [Tooltip("The duration it takes to complete the healing action.")]
        private float _healDuration = 2f;

        [SerializeField, Range(0f, 100f)]
        [Tooltip("The minimum amount of health that can be restored.")]
        private float _minHealAmount = 40f;

        [SerializeField, Range(0f, 100f)]
        [Tooltip("The maximum amount of health that can be restored.")]
        private float _maxHealAmount = 50f;

        [SerializeField, Range(0f, 10f)]
        [Tooltip("The movement speed modifier applied while performing the healing action.")]
        private float _healMovementSpeedMod = 0.75f;

        [SerializeField, SpaceArea]
        [Tooltip("Audio clips played during the healing action.")]
        private AudioSequence _healingAudio;

        private Coroutine _healRoutine;

        public ActionBlockHandler UseBlocker { get; } = new();
        public bool IsHealing => _healRoutine != null;
        public bool IsUsing => false;

        /// <summary>
        /// Starts healing
        /// </summary>
        public void Heal(UnityAction healCallback)
        {
            if (IsCrosshairActive())
            {
                _healRoutine = StartCoroutine(HealDelayed(healCallback, _healDuration));
                Audio.PlayClips(_healingAudio, BodyPoint.Hands);
            }
        }

        /// <summary>
        /// Cancels healing
        /// </summary>
        public bool Use(WieldableInputPhase inputPhase)
        {
            if (inputPhase == WieldableInputPhase.Start && IsHealing)
            {
                CoroutineUtility.StopCoroutine(this, ref _healRoutine);
                return true;
            }

            return false;
        }

        public override bool IsCrosshairActive() => !IsHealing;

        private void Start() => SpeedModifier.AddModifier(() => IsHealing ? _healMovementSpeedMod : 1f);
        private void OnDisable() => Use(WieldableInputPhase.Start);

        private IEnumerator HealDelayed(UnityAction healCallback, float delay)
        {
            yield return new WaitForTime(delay);

            float healingAmount = Random.Range(_minHealAmount, _maxHealAmount);
            Character.HealthManager.RestoreHealth(healingAmount);

            _healRoutine = null;
            healCallback?.Invoke();
        }
    }
}