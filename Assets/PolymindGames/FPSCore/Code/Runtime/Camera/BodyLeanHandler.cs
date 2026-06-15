using PolymindGames.ProceduralMotion;
using UnityEngine;

namespace PolymindGames
{
    [OptionalCharacterComponent(typeof(IMotorCC))]
    public sealed class BodyLeanHandler : CharacterBehaviour, IBodyLeanHandlerCC
    {
        [SerializeField, NotNull]
        private LeanMotion _bodyLeanMotion;

        [SerializeField, NotNull]
        private LeanMotion _wieldableLeanMotion;
        
        [SpaceArea]
        [SerializeField, Range(0f, 3f)]
        private float _leanCooldown = 0.2f;

        [SerializeField]
        private LayerMask _obstructionMask;

        [SerializeField, Range(0f, 1f)]
        private float _obstructionPadding = 0.15f;

        [SerializeField, Range(0f, 1f)]
        private float _maxLeanObstructionCutoff = 0.35f;

        [SerializeField, Range(1f, 100f)]
        private float _maxAllowedCharacterSpeed = 4f;

        [SpaceArea]
        [SerializeField, Title("Audio")]
        private AudioData _leanAudio = new(null);

        private BodyLeanState _leanState;
        private RaycastHit _raycastHit;
        private float _maxLeanPercent;
        private float _leanAudioTimer;
        private float _leanTimer;
        private IMotorCC _motor;

        private const float LeanAudioCooldown = 0.35f;

        public BodyLeanState LeanState => _leanState;
        
        public void SetLeanState(BodyLeanState leanState)
        {
            if (leanState == _leanState || Time.time < _leanTimer)
                return;

            if (CanLean(leanState))
            {
                SetLeanState_Internal(leanState);
                _leanTimer = Time.time + _leanCooldown;
            }
        }

        protected override void OnBehaviourStart(ICharacter character)
        {
            _motor = character.GetCC<IMotorCC>();
            SetLeanPercent(1f);
        }

        private void Update()
        {
            if (_leanState == BodyLeanState.Center)
                return;

            if (!CanLean(_leanState))
                SetLeanState_Internal(BodyLeanState.Center);
        }

        private void SetLeanState_Internal(BodyLeanState leanState)
        {
            if (leanState == _leanState)
                return;

            _leanState = leanState;

            if (Time.time > _leanAudioTimer)
            {
                _leanAudioTimer = Time.time + LeanAudioCooldown;
                Character.Audio.PlayClip(_leanAudio, BodyPoint.Torso, Mathf.Max(_maxLeanPercent, 0.3f));
            }

            if (leanState == BodyLeanState.Center)
                SetLeanPercent(1f);

            _bodyLeanMotion.SetLeanState(leanState);
            _wieldableLeanMotion.SetLeanState(leanState);
        }

        private bool CanLean(BodyLeanState leanState)
        {
            if (leanState == BodyLeanState.Center)
                return true;

            if (_motor != null && (_motor.Velocity.magnitude > _maxAllowedCharacterSpeed || !_motor.IsGrounded))
                return false;

            var bodyLeadTransform = _bodyLeanMotion.transform;

            Vector3 position = bodyLeadTransform.position;
            Vector3 targetPos = new Vector3(leanState == BodyLeanState.Left ? -_bodyLeanMotion.LeanSideOffset : _bodyLeanMotion.LeanSideOffset,
                -_bodyLeanMotion.LeanHeightOffset, 0f);

            targetPos = bodyLeadTransform.TransformPoint(targetPos);

            Ray ray = new Ray(position, targetPos - position);
            float distance = Vector3.Distance(position, targetPos) + _obstructionPadding;

            if (PhysicsUtility.SphereCastOptimized(ray, 0.2f, distance, out _raycastHit, _obstructionMask, Character.transform))
            {
                // Lower the max lean value.
                SetLeanPercent(_raycastHit.distance / distance);
                return _maxLeanPercent > _maxLeanObstructionCutoff;
            }

            // Reset the max lean value.
            _bodyLeanMotion.MaxLeanPercent = 1f;
            _wieldableLeanMotion.MaxLeanPercent = 1f;
            return true;
        }

        private void SetLeanPercent(float percent)
        {
            _maxLeanPercent = percent;
            _bodyLeanMotion.MaxLeanPercent = percent;
            _wieldableLeanMotion.MaxLeanPercent = percent;
        }
    }
}