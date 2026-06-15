using System.Runtime.CompilerServices;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    public abstract class WieldableActionBlocker : CharacterBehaviour
    {
        [SerializeField, Range(0.01f, 10f)]
        [Tooltip("For how much time should the corresponding action be put to 'sleep' (unable to start) after it's been blocked.")]
        private float _cooldown = 0.35f;
        
        private ActionBlockHandler _blockHandler;
        private bool _isBlocked;
        
        protected bool IsBlocked => _blockHandler != null && _blockHandler.IsBlocked;

        protected override void OnBehaviourEnable(ICharacter character)
        {
            var controller = character.GetCC<IWieldablesControllerCC>();
            controller.HolsteringStarted += OnWieldableHolstered;
            controller.EquippingStarted += OnWieldableEquipped;
            OnWieldableEquipped(controller.ActiveWieldable);
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            var controller = character.GetCC<IWieldablesControllerCC>();
            controller.HolsteringStarted -= OnWieldableHolstered;
            controller.EquippingStarted -= OnWieldableEquipped;
            OnWieldableHolstered(null);
        }

        protected abstract ActionBlockHandler GetBlockHandler(IWieldable wieldable);
        protected abstract bool IsActionValid();

        private void OnWieldableHolstered(IWieldable wieldable)
        {
            if (_blockHandler == null)
                return;

            StopAllCoroutines();

            _blockHandler.RemoveBlocker(this);
            _blockHandler = null;
            _isBlocked = false;

            IsActionValid();
        }

        private void OnWieldableEquipped(IWieldable wieldable)
        {
            if (wieldable == null)
                return;

            _blockHandler = GetBlockHandler(wieldable);
            _isBlocked = false;
        }

        private void FixedUpdate()
        {
            if (_blockHandler == null)
                return;

            SetBlockState(!IsActionValid());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetBlockState(bool block)
        {
            if (_isBlocked == block)
                return;

            if (block)
                _blockHandler.AddBlocker(this);
            else
            {
                _blockHandler.AddDurationBlocker(_cooldown);
                _blockHandler.RemoveBlocker(this);
            }

            _isBlocked = block;
        }
    }
}