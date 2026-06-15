using UnityEngine;

namespace PolymindGames
{
    [RequireCharacterComponent(typeof(IMotorCC))]
    public sealed class PlayerImpactHandler : CharacterBehaviour, IDamageImpactHandler
    {
        [SerializeField, Range(0f, 100f)]
        private float _forceMultiplier = 1f;
        
        private IMotorCC _motor;

        protected override void OnBehaviourStart(ICharacter character)
        {
            _motor = character.GetCC<IMotorCC>();
        }

        public void HandleImpact(Vector3 hitPoint, Vector3 hitForce)
        {
            _motor.AddForce(hitForce * _forceMultiplier, ForceMode.Impulse);
        }
    }
}
