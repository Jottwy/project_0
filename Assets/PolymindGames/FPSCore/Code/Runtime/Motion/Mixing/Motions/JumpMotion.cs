using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Polymind Games/Motion/Jump Motion")]
    [RequireCharacterComponent(typeof(IMovementControllerCC))]
    public sealed class JumpMotion : DataMotionBehaviour<GeneralMotionData>
    {
        private float _currentJumpTime;
        private bool _playJumpAnim;
        private float _randomFactor = -1f;

        protected override void OnBehaviourEnable(ICharacter character)
        {
            character.GetCC<IMovementControllerCC>()
                .AddStateTransitionListener(MovementStateType.Jump, OnJump);
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            character.GetCC<IMovementControllerCC>()
                .RemoveStateTransitionListener(MovementStateType.Jump, OnJump);
        }

        protected override void OnDataChanged(GeneralMotionData data)
        {
            if (data != null)
            {
                var jumpData = Data.Jump;
                PositionSpring.Adjust(jumpData.PositionSpring);
                RotationSpring.Adjust(jumpData.RotationSpring);
            }
        }

        public override void UpdateMotion(float deltaTime)
        {
            if (Data == null || !_playJumpAnim)
                return;

            var jumpData = Data.Jump;
            
            bool playPosJump = jumpData.PositionCurves.Duration > _currentJumpTime;
            if (playPosJump)
            {
                // Evaluate position jumping curves.
                Vector3 posJump = jumpData.PositionCurves.Evaluate(_currentJumpTime);
                posJump = MotionMixer.TargetTransform.InverseTransformVector(posJump);
                posJump.x *= _randomFactor;

                SetTargetPosition(posJump);
            }

            bool playRotJump = jumpData.RotationCurves.Duration > _currentJumpTime;
            if (playRotJump)
            {
                // Evaluate rotation jumping curves.
                Vector3 rotJump = jumpData.RotationCurves.Evaluate(_currentJumpTime);
                rotJump.y *= _randomFactor;
                rotJump.z *= _randomFactor;

                SetTargetRotation(rotJump);
            }
            _currentJumpTime += deltaTime;

            if (!playPosJump && !playRotJump)
            {
                _playJumpAnim = false;
                SetTargetPosition(Vector3.zero);
                SetTargetRotation(Vector3.zero);
            }
        }

        private void OnJump(MovementStateType state)
        {
            _currentJumpTime = 0f;
            _playJumpAnim = true;
            _randomFactor = _randomFactor > 0f ? -1f : 1f;

            UpdateMotion(Time.deltaTime);
        }
    }
}