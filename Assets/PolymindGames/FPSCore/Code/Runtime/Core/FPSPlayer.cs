using PolymindGames.ProceduralMotion;
using UnityEngine;

namespace PolymindGames
{
    [SelectionBase]
    [AddComponentMenu("Polymind Games/Characters/FPS Player")]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault2)]
    public sealed class FPSPlayer : Player, IFPSCharacter
    {
        [SerializeField, NotNull, Title("Motion Handlers")]
        private MotionMixer _headMotionMixer;

        [SerializeField, NotNull]
        private MotionMixer _handsMotionMixer;
        
        public MotionComponents HeadComponents { get; private set; }
        public MotionComponents HandsComponents { get; private set; }
        
        private void OnEnable()
        {
            if (TryGetCC(out IMovementControllerCC movement))
                movement.AddStateTransitionListener(MovementStateType.None, OnStateTypeChanged);
        }

        private void OnDisable()
        {
            if (TryGetCC(out IMovementControllerCC movement))
                movement.RemoveStateTransitionListener(MovementStateType.None, OnStateTypeChanged);
        }

        private void Start()
        {
            HeadComponents = new MotionComponents(_headMotionMixer.GetMotion<AdditiveShakeMotion>(),
                _headMotionMixer.GetComponent<IMotionDataHandler>(),
                _headMotionMixer);
            
            HandsComponents = new MotionComponents(_handsMotionMixer.GetMotion<AdditiveShakeMotion>(),
                _handsMotionMixer.GetComponent<IMotionDataHandler>(),
                _handsMotionMixer);
        }

        private void OnStateTypeChanged(MovementStateType stateType)
        {
            HeadComponents.Data.SetStateType(stateType);
            HandsComponents.Data.SetStateType(stateType);
        }
    }
}