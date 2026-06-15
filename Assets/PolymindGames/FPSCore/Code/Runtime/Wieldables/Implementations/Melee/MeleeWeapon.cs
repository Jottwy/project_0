using PolymindGames.InventorySystem;
using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu("Polymind Games/Wieldables/Melee/Melee Weapon")]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault2)]
    public class MeleeWeapon : Wieldable, IUseInputHandler
    {
        [SerializeField, SpaceArea]
        [Tooltip("Determines if the weapon can attack continuously while the use input is held down.")]
        private bool _attackContinuously = true;

        [SerializeField]
        private bool _hideAfterAttack = false;
        
        [SerializeField, Range(0f, 60f)]
        [ShowIf(nameof(_hideAfterAttack), true)]
        [Tooltip("The delay after attacking before the weapon is automatically hidden.")]
        private float _hideDelayAfterAttack;

        [SerializeField, Range(0f, 1f), SpaceArea]
        [Tooltip("The amount of accuracy kick when attacking.")]
        private float _accuracyKick = 0.3f;

        [SerializeField, Range(0f, 1f)]
        [Tooltip("The rate at which accuracy recovers after attacking.")]
        private float _accuracyRecover = 0.5f;

        [SerializeField, SpaceArea]
        private AttackCombo _priorityAttackCombo;
        
        [SerializeField]
        private AttackCombo _attackCombo;

        private IAccuracyHandlerCC _characterAccuracy;
        private MeleeAttackBehaviour _currentAttack;
        private IStaminaManagerCC _staminaManager;
        private ItemProperty _durability;
        private float _accuracyPenalty;
        private float _nextAttackTime;
        private float _hideTimer = float.MaxValue;
        private bool _isUsing;

        private const float MinStamina = 0.05f;

        public bool IsUsing => _isUsing;
        public ActionBlockHandler UseBlocker { get; } = new();

        public bool Use(WieldableInputPhase inputPhase)
        {
            if (inputPhase is WieldableInputPhase.Start || inputPhase is WieldableInputPhase.Hold && _attackContinuously)
            {
                ToggleHideTimer();
                if (TryStartAttack())
                {
                    _isUsing = true;
                    return true;
                }
            }
            _isUsing = false;
            return false;
        }

        public sealed override bool IsCrosshairActive() => CanAttack();

        protected override void OnCharacterChanged(ICharacter character)
        {
            _characterAccuracy = character?.TryGetCC(out IAccuracyHandlerCC accuracy) == true ? accuracy : new NullAccuracyHandler();
            _staminaManager = character?.GetCC<IStaminaManagerCC>();
        }

        private bool TryStartAttack()
        {
            if (!CanAttack())
                return false;

            if (PerformAttack())
            {
                _accuracyPenalty += _accuracyKick;
                _nextAttackTime = Time.time + _currentAttack.AttackCooldown;
                if (_staminaManager != null)
                    _staminaManager.Stamina -= _currentAttack.AttackStaminaUsage;
                return true;
            }
            
            return false;
        }

        protected virtual bool PerformAttack()
        {
            if (ExecuteAttack(_priorityAttackCombo))
                return true;

            if (ExecuteAttack(_attackCombo))
                return true;

            return false;
        }

        protected bool ExecuteAttack(AttackCombo combo)
        {
            var attack = combo.GetNextAttack();
            if (attack != null && attack.TryAttack(Accuracy, OnHit))
            {
                _currentAttack = attack;
                return true;
            }
            return false;
        }

        protected virtual bool CanAttack()
        {
            return !UseBlocker.IsBlocked
                   && Time.time >= _nextAttackTime
                   && (_staminaManager == null || _staminaManager.Stamina > MinStamina);
        }

        protected virtual void OnHit()
        {
            if (_durability != null)
            {
                _durability.Float = Mathf.Max(_durability.Float - _currentAttack.HitDurabilityUsage, 0f);
            }
        }
        
        private void Start()
        {
            InitializeDurability();
        }
        
        private void InitializeDurability()
        {
            if (!TryGetComponent(out IWieldableItem wieldableItem))
                return;
            
            wieldableItem.AttachedSlotChanged += slot => _durability = slot.GetItem()?.GetProperty(ItemConstants.Durability);
            _durability = wieldableItem.Slot.GetItem()?.GetProperty(ItemConstants.Durability);
        }

        private void OnEnable()
        {
            if (_hideAfterAttack)
                _hideTimer = 0f;
        }

        private void OnDisable()
        {
            if (_currentAttack != null)
                _currentAttack.CancelAttack();
            _nextAttackTime = 0f;
        }

        private void FixedUpdate()
        {
            UpdateHideTimer();
            UpdateAccuracy();
        }

        private void UpdateHideTimer()
        {
            if (_hideTimer < Time.fixedTime)
            {
                _hideTimer = float.MaxValue;
                Animator.SetBool(AnimationConstants.IsVisible, false);
            }
        }

        private void ToggleHideTimer()
        {
            if (_hideAfterAttack)
            {
                _hideTimer = Time.fixedTime + _hideDelayAfterAttack;
                Animator.SetBool(AnimationConstants.IsVisible, true);
            }
        }

        private void UpdateAccuracy()
        {
            float targetAccuracy = Mathf.Clamp01(_characterAccuracy.GetAccuracyMod() - _accuracyPenalty);
            _accuracyPenalty = Mathf.Clamp01(_accuracyPenalty - _accuracyRecover * Time.fixedDeltaTime);
            Accuracy = targetAccuracy;
        }

        #region Internal Types
        [Serializable]
        protected sealed class AttackCombo
        {
            [SerializeField, Range(0.5f, 10f)]
            [Tooltip("The delay before the combo reset.")]
            private float _resetDelay = 1f;

            [SerializeField]
            private SelectionType _selectionType = SelectionType.Sequence;

            [SerializeField]
            [ReorderableList(ListStyle.Lined, HasLabels = false)]
            private MeleeAttackBehaviour[] _attacks;

            private float _resetComboTimer = float.MaxValue;
            private int _lastAttackIndex = -1;

            public MeleeAttackBehaviour GetNextAttack()
            {
                if (_resetComboTimer < Time.time)
                    _lastAttackIndex = -1;

                _resetComboTimer = Time.time + _resetDelay;
                return _attacks.Select(ref _lastAttackIndex, _selectionType);
            }
        }
        #endregion

        #region Editor
#if UNITY_EDITOR
        protected override void DrawDebugGUI()
        {
            GUILayout.Label($"Is Use Input Blocked: {UseBlocker.IsBlocked}");
            GUILayout.Label($"Current Speed Multiplier: {Math.Round(SpeedModifier.EvaluateValue(), 2)}");
        }
#endif
        #endregion
    }
}