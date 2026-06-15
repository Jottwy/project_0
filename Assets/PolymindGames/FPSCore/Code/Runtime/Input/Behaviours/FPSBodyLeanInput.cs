using UnityEngine.InputSystem;
using PolymindGames.Options;
using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    [AddComponentMenu("Input/Lean Input")]
    [RequireCharacterComponent(typeof(IBodyLeanHandlerCC))]
    public sealed class FPSBodyLeanInput : PlayerInputBehaviour
    {
        [SerializeField, Title("Actions")]
        private InputActionReference _leanAction;

        private IBodyLeanHandlerCC _leanHandler;

        #region Initialization
        protected override void OnBehaviourStart(ICharacter character)
        {
            _leanHandler = character.GetCC<IBodyLeanHandlerCC>();
        }

        protected override void OnBehaviourEnable(ICharacter character)
        {
            _leanHandler.SetLeanState(BodyLeanState.Center);
            _leanAction.EnableAction();
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            _leanHandler.SetLeanState(BodyLeanState.Center);
            _leanAction.DisableAction();
        }
        #endregion

        private void Update()
        {
            if (InputOptions.Instance.LeanToggle)
            {
                if (_leanAction.action.WasPressedThisFrame())
                {
                    var leanState = _leanHandler.LeanState;
                    var targetLeanState = (BodyLeanState)Mathf.CeilToInt(_leanAction.action.ReadValue<float>());
                    
                    if (leanState == targetLeanState)
                        _leanHandler.SetLeanState(BodyLeanState.Center);
                    else
                        _leanHandler.SetLeanState(leanState != BodyLeanState.Center ? BodyLeanState.Center : targetLeanState);
                }
            }
            else
            {
                var targetLeanState = (BodyLeanState)Mathf.CeilToInt(_leanAction.action.ReadValue<float>());
                _leanHandler.SetLeanState(targetLeanState);
            }
        }
    }
}
