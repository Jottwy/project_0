using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault2)]
    public class AimedMeleeWeapon : MeleeWeapon, IAimInputHandler
    {
        [SerializeField, Range(0f, 5f), SpaceArea]
        [Tooltip("For how long should this melee weapon be unable to be used after aiming.")]
        private float _aimAttackCooldown = 0.3f;

        [SerializeField, NotNull]
        private FirearmAimHandlerBehaviour _aimHandler;
        
        [SerializeField]
        private AttackCombo _aimAttacks;
    
        private float _useTimer;

        /// <inheritdoc/>
        public bool IsAiming => _aimHandler.IsAiming;
        
        /// <inheritdoc/>
        public ActionBlockHandler AimBlocker { get; } = new();

        /// <inheritdoc/>
        public bool Aim(WieldableInputPhase inputPhase) => inputPhase switch
        {
            WieldableInputPhase.Start => StartAiming(),
            WieldableInputPhase.End => EndAiming(),
            _ => false
        };

        /// <inheritdoc/>
        protected override bool PerformAttack()
        {
            if (IsAiming)
                return ExecuteAttack(_aimAttacks);
            else
                return base.PerformAttack();
        }

        /// <inheritdoc/>
        protected override bool CanAttack()
        {
            return base.CanAttack()
                   && _useTimer < Time.time;
        }

        /// <inheritdoc/>
        protected override void OnCharacterChanged(ICharacter character)
        {
            base.OnCharacterChanged(character);
            AimBlocker.OnBlocked += ForceEndAiming;
            
            return;
            void ForceEndAiming() => EndAiming();
        }
    
        private bool StartAiming()
        {
            if (AimBlocker.IsBlocked)
                return false;
    
            if (_aimHandler.StartAiming())
            {
                _useTimer = Time.time + _aimAttackCooldown;
                return true;
            }
    
            return false;
        }
    
        private bool EndAiming()
        {
            if (_aimHandler.StopAiming())
            {
                _useTimer = Time.time + _aimAttackCooldown;
                return true;
            }
    
            return false;
        }
        
        #region Editor
#if UNITY_EDITOR
        protected override void DrawDebugGUI()
        {
            GUILayout.Label($"Is Aiming: {IsAiming}");
        }
#endif
        #endregion
    }
}