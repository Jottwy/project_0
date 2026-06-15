using UnityEngine;

namespace PolymindGames.InputSystem
{
    public abstract class InputBehaviour : MonoBehaviour, IInputBehaviour
    {
        [field: SerializeField]
        public InputEnableMode EnableMode { get; private set; } = InputEnableMode.BasedOnContext;
        
        public bool Enabled
        {
            get => enabled;
            set
            {
                // Check if this object is destroyed
                if (this != null)
                    enabled = value;
            }
        }

        protected virtual void Awake()
        {
            enabled = false;
            InputManager.Instance.RegisterBehaviour(this);
        }

        protected virtual void OnDestroy()
        {
            InputManager.Instance.UnregisterBehaviour(this);
        }
    }
}