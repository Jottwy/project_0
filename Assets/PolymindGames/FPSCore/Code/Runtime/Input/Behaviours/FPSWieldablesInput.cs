using PolymindGames.WieldableSystem;
using UnityEngine.InputSystem;
using PolymindGames.Options;
using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    [AddComponentMenu("Input/Wieldables Input")]
    [RequireCharacterComponent(typeof(IWieldablesControllerCC), typeof(IWieldableInventoryCC))]
    [OptionalCharacterComponent(typeof(IWieldableHealingHandlerCC), typeof(IWieldableThrowableHandlerCC))]
    public class FPSWieldablesInput : PlayerInputBehaviour
    {
        [SerializeField, Title("Actions")]
        private InputActionReference _useAction;

        [SerializeField]
        private InputActionReference _reloadAction;

        [SerializeField]
        private InputActionReference _dropAction;

        [SerializeField]
        private InputActionReference _aimAction;

        [SerializeField]
        private InputActionReference _selectAction;

        [SerializeField]
        private InputActionReference _holsterAction;

        [SerializeField]
        private InputActionReference _healAction;

        [SerializeField]
        private InputActionReference _throwAction;

        [SerializeField]
        private InputActionReference _firemodeAction;

        [SerializeField]
        private InputActionReference _throwableScrollAction;

        private IWieldableThrowableHandlerCC _throwableHandler;
        private IWieldableHealingHandlerCC _healingHandler;
        private IWieldableInventoryCC _selection;
        private IWieldablesControllerCC _controller;
        private IReloadInputHandler _reloadInputHandler;
        private IUseInputHandler _useInputHandler;
        private IAimInputHandler _aimInputHandler;
        private IWieldable _activeWieldable;

        #region Initialization
        protected override void OnBehaviourStart(ICharacter character)
        {
            _controller = character.GetCC<IWieldablesControllerCC>();
            _selection = character.GetCC<IWieldableInventoryCC>();
            _healingHandler = character.GetCC<IWieldableHealingHandlerCC>();
            _throwableHandler = character.GetCC<IWieldableThrowableHandlerCC>();
            
            _controller.EquippingStopped += OnEquip;
            _controller.HolsteringStarted += OnHolster;
        }

        protected override void OnBehaviourDestroy(ICharacter character)
        {
            if (_controller != null)
            {
                _controller.EquippingStopped -= OnEquip;
                _controller.HolsteringStarted -= OnHolster;
            }
        }

        protected override void OnBehaviourEnable(ICharacter character)
        {
            _holsterAction.RegisterStarted(OnHolsterAction);
            _selectAction.RegisterStarted(OnSelectAction);
            _dropAction.RegisterStarted(OnDropAction);

            if (_healingHandler != null)
                _healAction.RegisterStarted(OnHealAction);

            if (_throwableHandler != null)
            {
                _throwAction.RegisterStarted(OnThrowAction);
                _throwableScrollAction.RegisterPerformed(OnThrowableScrollAction);
            }

            _useAction.EnableAction();
            _aimAction.EnableAction();

            _firemodeAction.RegisterStarted(OnFiremodeAction);
            _reloadAction.RegisterStarted(OnReloadAction);
            
            if (_controller.State == WieldableControllerState.None)
                OnEquip(_controller.ActiveWieldable);
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            _holsterAction.UnregisterStarted(OnHolsterAction);
            _selectAction.UnregisterStarted(OnSelectAction);
            _dropAction.UnregisterStarted(OnDropAction);

            if (_healingHandler != null)
                _healAction.UnregisterStarted(OnHealAction);

            if (_throwableHandler != null)
            {
                _throwAction.UnregisterStarted(OnThrowAction);
                _throwableScrollAction.UnregisterPerformed(OnThrowableScrollAction);
            }

            _firemodeAction.UnregisterStarted(OnFiremodeAction);
            _reloadAction.UnregisterStarted(OnReloadAction);
            
            _useAction.DisableAction();
            _aimAction.DisableAction();

            OnHolster(_activeWieldable);
        }

        private void OnHolster(IWieldable wieldable)
        {
            _useInputHandler?.Use(WieldableInputPhase.End);
            _useInputHandler = null;
            
            _aimInputHandler?.Aim(WieldableInputPhase.End);
            _aimInputHandler = null;
            
            _reloadInputHandler?.Reload(WieldableInputPhase.End);
            _reloadInputHandler = null;
        }

        private void OnEquip(IWieldable wieldable)
        {
            _activeWieldable = wieldable;
            _useInputHandler = wieldable as IUseInputHandler;
            _aimInputHandler = wieldable as IAimInputHandler;
            _reloadInputHandler = wieldable as IReloadInputHandler;
        }
        #endregion

        #region Input Handling
        private void Update()
        {
            if (_useInputHandler != null)
            {
                if (_useAction.action.triggered)
                    _useInputHandler.Use(WieldableInputPhase.Start);
                else if (_useAction.action.ReadValue<float>() > 0.001f)
                    _useInputHandler.Use(WieldableInputPhase.Hold);
                else if (_useAction.action.WasReleasedThisFrame() || !_useAction.action.enabled)
                    _useInputHandler.Use(WieldableInputPhase.End);
            }

            if (_aimInputHandler != null)
            {
                if (InputOptions.Instance.AimToggle)
                {
                    if (_aimAction.action.WasPressedThisFrame())
                    {
                        _aimInputHandler.Aim(_aimInputHandler.IsAiming ? WieldableInputPhase.End : WieldableInputPhase.Start);
                    }
                }
                else
                {
                    if (_aimAction.action.ReadValue<float>() > 0.001f)
                    {
                        if (!_aimInputHandler.IsAiming)
                            _aimInputHandler.Aim(WieldableInputPhase.Start);
                    }
                    else if (_aimInputHandler.IsAiming)
                        _aimInputHandler.Aim(WieldableInputPhase.End);
                }
            }
        }

        private void OnSelectAction(InputAction.CallbackContext context)
        {
            int index = (int)context.ReadValue<float>() - 1;
            _selection.SelectAtIndex(index);
        }

        private void OnFiremodeAction(InputAction.CallbackContext context)
        {
            if (_activeWieldable is IFirearm && _activeWieldable.gameObject.TryGetComponent<IFirearmIndexModeHandler>(out var modeHandler))
                modeHandler.ToggleNextMode();
        }

        private void OnReloadAction(InputAction.CallbackContext context) => _reloadInputHandler?.Reload(WieldableInputPhase.Start);
        private void OnDropAction(InputAction.CallbackContext context) => _selection.DropWieldable();
        private void OnHealAction(InputAction.CallbackContext context) => _healingHandler?.TryHeal();
        private void OnThrowAction(InputAction.CallbackContext context) => _throwableHandler?.TryThrow();
        private void OnThrowableScrollAction(InputAction.CallbackContext context) => _throwableHandler.SelectNext(context.ReadValue<float>() > 0);
        private void OnHolsterAction(InputAction.CallbackContext context) => _selection.SelectAtIndex(_selection.SelectedIndex != -1 ? -1 : _selection.PreviousIndex);
        #endregion
    }
}