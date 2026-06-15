using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Generates Rays based on the parent character state.
    /// (e.g. shoot direction ray will be more random when moving)
    /// </summary>
    [RequireCharacterComponent(typeof(IMotorCC))]
    public sealed class WieldableAccuracyHandler : CharacterBehaviour, IAccuracyHandlerCC
    {
        [SerializeField, Range(0.1f, 10f)]
        private float _airborneAccuracyMod = 0.5f;

        [SerializeField, Range(0.1f, 10f)]
        private float _lowerHeightAccuracyMod = 1.1f;

        [SerializeField]
        private AnimationCurve _speedAccuracyCurve;

        [SerializeField, Range(0.1f, 100f)]
        private float _dampSpeed = 6f;

        private IMotorCC _motor;
        private float _accuracy = 1f;
        private float _baseAccuracy = 1f;

        public float GetAccuracyMod()
        {
            float targetAccuracy = _speedAccuracyCurve.Evaluate(_motor.Velocity.magnitude);

            if (!_motor.IsGrounded)
                targetAccuracy *= _airborneAccuracyMod;

            if (_motor.Height < _motor.DefaultHeight)
                targetAccuracy *= _lowerHeightAccuracyMod;

            _accuracy = Mathf.Lerp(_accuracy, targetAccuracy * _baseAccuracy, _dampSpeed * Time.fixedDeltaTime);

            return _accuracy;
        }

        public void SetBaseAccuracy(float accuracy) => _baseAccuracy = accuracy;
        
        protected override void OnBehaviourStart(ICharacter character) => _motor = character.GetCC<IMotorCC>();
    }
}