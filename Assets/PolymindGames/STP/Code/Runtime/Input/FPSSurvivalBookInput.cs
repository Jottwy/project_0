using PolymindGames.WieldableSystem;
using UnityEngine.InputSystem;
using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    [AddComponentMenu("Input/Survival Book Input")]
    [RequireCharacterComponent(typeof(IWieldablesControllerCC))]
    public class FPSSurvivalBookInput : PlayerInputBehaviour
    {
        [SerializeField, SceneObjectOnly, NotNull, Title("Reference")]
        [Tooltip("The prefab object representing the survival book.")]
        private Wieldable _survivalBookWieldable;

        [SerializeField, Title("Actions")]
        [Tooltip("The input action reference for accessing the survival book.")]
        private InputActionReference _survivalBookAction;

        private IWieldablesControllerCC _controller;

        #region Initialization
        protected override void OnBehaviourStart(ICharacter character)
        {
            _controller = character.GetCC<IWieldablesControllerCC>();
            _controller.RegisterWieldable(_survivalBookWieldable);
        }

        protected override void OnBehaviourEnable(ICharacter character) => _survivalBookAction.RegisterStarted(OnSurvivalBookAction);

        protected override void OnBehaviourDisable(ICharacter character)
        {
            _survivalBookAction.UnregisterStarted(OnSurvivalBookAction);
            _controller.TryHolsterWieldable(_survivalBookWieldable);
        }
        #endregion

        #region Input Handling
        private void OnSurvivalBookAction(InputAction.CallbackContext obj)
        {
            var activeWieldable = _controller.ActiveWieldable;
            if (!ReferenceEquals(activeWieldable, _survivalBookWieldable))
            {
                _controller.TryEquipWieldable(_survivalBookWieldable, 1.35f);
            }
            else
            {
                _controller.TryHolsterWieldable(_survivalBookWieldable);
            }
        }
        #endregion
    }
}
