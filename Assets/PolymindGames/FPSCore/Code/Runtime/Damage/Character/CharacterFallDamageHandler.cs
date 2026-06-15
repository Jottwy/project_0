using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Handles dealing fall damage to the character based on the impact velocity.
    /// </summary>
    [RequireCharacterComponent(typeof(IMotorCC))]
    [AddComponentMenu("Polymind Games/Damage/Fall Damage Handler")]
    public sealed class FallDamageHandler : CharacterBehaviour
    {
        [SerializeField, Range(1f, 30f)]
        [Help("At which landing speed, the character will start taking damage.")]
        private float _minFallSpeed = 15f;

        [SerializeField, Range(1f, 50f)]
        [Help("At which landing speed, the character will take maximum damage (die).")]
        private float _fatalFallSpeed = 30f;

        protected override void OnBehaviourEnable(ICharacter character)
            => character.GetCC<IMotorCC>().FallImpact += OnFallImpact;
        
        protected override void OnBehaviourDisable(ICharacter character)
            => character.GetCC<IMotorCC>().FallImpact -= OnFallImpact;

        private void OnFallImpact(float impactSpeed)
        {
            if (impactSpeed > _minFallSpeed)
            {
                var health = Character.HealthManager;
                health.ReceiveDamage(-health.MaxHealth * (impactSpeed / _fatalFallSpeed), new DamageArgs(DamageType.Fall));
            }
        }
    }
}