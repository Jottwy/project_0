using UnityEngine;

namespace PolymindGames.MovementSystem
{
    public abstract class CharacterMovementState : ICharacterMovementState
    {
        private bool _isInitialized;

        public bool Enabled { get; set; } = true;
        public abstract MovementStateType StateType { get; }
        public abstract float StepCycleLength { get; }
        public abstract bool ApplyGravity { get; }
        public abstract bool SnapToGround { get; }
        
        protected IMovementControllerCC Controller { get; private set; }
        protected IMovementInputProvider Input { get; private set; }
        protected IMotorCC Motor { get; private set; }
        protected ICharacter Character { get; private set; }

        void ICharacterMovementState.InitializeState(IMovementControllerCC controller, IMovementInputProvider input, IMotorCC motor, ICharacter character)
        {
            if (_isInitialized)
                return;

            Controller = controller;
            Input = input;
            Motor = motor;
            Character = character;

            _isInitialized = true;
            OnStateInitialized();
        }

        public virtual bool IsValid() => true;
        public virtual void OnEnter(MovementStateType prevStateType) { }
        public abstract void UpdateLogic();
        public abstract Vector3 UpdateVelocity(Vector3 currentVelocity, float deltaTime);
        public virtual void OnExit() { }

        protected virtual void OnStateInitialized() { }
    }
}