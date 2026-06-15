using UnityEngine.Events;
using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu("Polymind Games/Wieldables/Tools/Wieldable Tool")]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault2)]
    public sealed class WieldableTool : Wieldable, IUseInputHandler
    {
        [SerializeField, Title("Equip Settings")]
        [Tooltip("The trigger type for the equip event.")]
        private TriggerType _equipEventTrigger = TriggerType.None;

        [SerializeField, DisableIf(nameof(_equipEventTrigger), TriggerType.None)]
        [Tooltip("The UnityEvent invoked when the item is equipped.")]
        private UnityEvent _equipEvent;

        [SerializeField, Title("Holster Settings")]
        [Tooltip("The trigger type for the holster event.")]
        private TriggerType _holsterEventTrigger = TriggerType.None;

        [SerializeField, DisableIf(nameof(_holsterEventTrigger), TriggerType.None)]
        [Tooltip("The UnityEvent invoked when the item is holstered.")]
        private UnityEvent _holsterEvent;

        [SerializeField, Range(0f, 10f), Title("Use Settings")]
        [Tooltip("The cooldown duration between consecutive uses of the item.")]
        private float _useCooldown = 0.3f;

        [SerializeField, DisableIf(nameof(_useCooldown), MinUseTime, Comparison = UnityComparisonMethod.Less)]
        [Tooltip("The UnityEvent invoked when the item is used.")]
        private UnityEvent _useEvent;

        private const float MinUseTime = 0.001f;
        private float _useTimer;

        public ActionBlockHandler UseBlocker { get; } = new();

        public bool IsUsing => false;

        public event UnityAction EquippingStarted
        {
            add => _equipEvent.AddListener(value);
            remove => _equipEvent.RemoveListener(value);
        }

        public event UnityAction HolsteringStarted
        {
            add => _holsterEvent.AddListener(value);
            remove => _holsterEvent.RemoveListener(value);
        }

        public bool Use(WieldableInputPhase inputPhase)
        {
            if (inputPhase != WieldableInputPhase.Start || !IsCrosshairActive() || _useCooldown < MinUseTime)
                return false;

            _useTimer = Time.time + _useCooldown;
            _useEvent.Invoke();
            return true;
        }
        
        public override bool IsCrosshairActive() => !UseBlocker.IsBlocked && _useTimer < Time.time;

        protected override void OnStateChanged(WieldableStateType state)
        {
            switch (state)
            {
                case WieldableStateType.Hidden:
                    if (_holsterEventTrigger.HasFlag(TriggerType.AtTheEnd))
                        _holsterEvent.Invoke();
                    break;
                case WieldableStateType.Equipping:
                    if (_equipEventTrigger.HasFlag(TriggerType.AtTheBeginning))
                        _equipEvent.Invoke();
                    break;
                case WieldableStateType.Equipped:
                    if (_equipEventTrigger.HasFlag(TriggerType.AtTheEnd))
                        _equipEvent.Invoke();
                    break;
                case WieldableStateType.Holstering:
                    if (_holsterEventTrigger.HasFlag(TriggerType.AtTheBeginning))
                        _holsterEvent.Invoke();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }
        }

        #region Editor
#if UNITY_EDITOR
        protected override void DrawDebugGUI()
        {
            GUILayout.Label($"Is Use Input Blocked: {UseBlocker.IsBlocked}");
            GUILayout.Label($"Use Cooldown: {Math.Round(Mathf.Max(0f, _useTimer - Time.time), 2)}");
            GUILayout.Label($"Current Speed Multiplier: {Math.Round(SpeedModifier.EvaluateValue(), 2)}");
        }
#endif
        #endregion
        
        #region Internal Types
        [Flags]
        private enum TriggerType : byte
        {
            None = 0,
            AtTheBeginning = 1,
            AtTheEnd = 2
        }
        #endregion
    }
}