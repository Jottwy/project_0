using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Represents a throwable weapon that can be used for both melee and ranged attacks.
    /// </summary>
    [AddComponentMenu("Polymind Games/Wieldables/Melee/Throwable")]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault2)]
    public sealed class Throwable : Wieldable, IUseInputHandler, IAimInputHandler
    {
        [SerializeField]
        [Tooltip("A display image that represents this throwable.")]
        private Sprite _displayIcon;

        [SerializeField]
        [Tooltip("The primary melee attack behavior.")]
        private MeleeAttackBehaviour _primaryAttack;

        [SerializeField]
        [Tooltip("The alternate melee attack behavior.")]
        private MeleeAttackBehaviour _alternateAttack;
        
        private MeleeAttackBehaviour _currentAttack;
        private float _nextThrowTime;
        private bool _isThrowing;

        /// <summary>
        /// Gets the display icon for this throwable.
        /// </summary>
        public Sprite DisplayIcon => _displayIcon;
        
        /// <inheritdoc/>
        public ActionBlockHandler UseBlocker { get; } = new();

        /// <inheritdoc/>
        public ActionBlockHandler AimBlocker { get; } = new();

        /// <inheritdoc/>
        public bool IsAiming => false;

        /// <inheritdoc/>
        public bool IsUsing => false;

        /// <inheritdoc/>
        public override bool IsCrosshairActive() => CanThrow();

        /// <inheritdoc/>
        public bool Use(WieldableInputPhase inputPhase)
        {
            return inputPhase == WieldableInputPhase.Start && TryThrow(isAlternate: false);
        }

        /// <inheritdoc/>
        public bool Aim(WieldableInputPhase inputPhase) => inputPhase switch
        {
            WieldableInputPhase.Start => TryThrow(isAlternate: true),
            WieldableInputPhase.End => true,
            _ => false
        };

        /// <summary>
        /// Attempts to perform a throw.
        /// </summary>
        /// <param name="isAlternate">Indicates whether to use the alternate attack.</param>
        /// <returns>True if the throw was successful; otherwise, false.</returns>
        private bool TryThrow(bool isAlternate)
        {
            if (!CanThrow())
                return false;

            if (ExecuteAttack(isAlternate ? _alternateAttack : _primaryAttack))
            {
                _nextThrowTime = Time.time + _currentAttack.AttackCooldown;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks whether the throwable is ready to be used.
        /// </summary>
        /// <returns>True if the throwable can be used; otherwise, false.</returns>
        private bool CanThrow() => Time.time > _nextThrowTime;

        private bool ExecuteAttack(MeleeAttackBehaviour attack)
        {
            if (attack.TryAttack(Accuracy))
            {
                _currentAttack = attack;
                return true;
            }
            return false;
        }

        private void OnEnable()
        {
            _isThrowing = false;
        }

        private void OnDisable()
        {
            if (_currentAttack != null)
                _currentAttack.CancelAttack();
            _nextThrowTime = 0f;
        }

        #region Editor
#if UNITY_EDITOR
        protected override void DrawDebugGUI()
        {
            GUILayout.Label($"Is Use Input Blocked: {UseBlocker.IsBlocked}");
            GUILayout.Label($"Is Throwing: {_isThrowing}");
            GUILayout.Label($"Current Speed Multiplier: {Math.Round(SpeedModifier.EvaluateValue(), 2)}");
        }
#endif
        #endregion
    }
}