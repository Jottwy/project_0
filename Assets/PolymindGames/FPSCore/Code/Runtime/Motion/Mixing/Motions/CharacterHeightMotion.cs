using PolymindGames.ProceduralMotion;
using UnityEngine;

namespace PolymindGames
{
    [RequireComponent(typeof(IMotionMixer))]
    [RequireCharacterComponent(typeof(IMotorCC))]
    public sealed class CharacterHeightMotion : CharacterBehaviour, IMixedMotion
    {
        [SerializeField]
        private SpringSettings _springSettings = SpringSettings.Default;

        private float _defaultHeight;
        private Spring1D _spring;
        private IMotorCC _motor;

        public float Multiplier { get => 1f; set { } }

        public void UpdateMotion(float deltaTime) { }

        public Vector3 GetPosition(float deltaTime)
            => new(0f, _spring.Evaluate(deltaTime), 0f);

        public Quaternion GetRotation(float deltaTime)
            => Quaternion.identity;

        protected override void OnBehaviourStart(ICharacter character)
        {
            _motor = character.GetCC<IMotorCC>();
            _spring = new Spring1D(_springSettings);
            _defaultHeight = _motor.DefaultHeight;
        }

        protected override void OnBehaviourEnable(ICharacter character)
        {
            _motor.HeightChanged += SetSpringTarget;
            _motor.Teleported += _spring.Reset;
            GetComponent<IMotionMixer>().AddMotion(this);
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            _motor.HeightChanged -= SetSpringTarget;
            _motor.Teleported -= _spring.Reset;
            GetComponent<IMotionMixer>().RemoveMotion(this);
        }

        private void SetSpringTarget(float height)
            => _spring.SetTargetValue(height - _defaultHeight);
    }
}