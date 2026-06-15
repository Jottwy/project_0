using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    public abstract class PlayerInputBehaviour : CharacterBehaviour, IInputBehaviour
    {
        [field: SerializeField]
        public InputEnableMode EnableMode { get; private set; } = InputEnableMode.BasedOnContext;
        
        public bool Enabled
        {
            get => enabled;
            set => enabled = value;
        }

        protected virtual void Awake()
        {
            enabled = false;
            InputManager.Instance.RegisterBehaviour(this);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            InputManager.Instance.UnregisterBehaviour(this);
        }
    }
}