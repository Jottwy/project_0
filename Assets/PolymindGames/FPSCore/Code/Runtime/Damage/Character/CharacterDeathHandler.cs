using UnityEngine.Events;
using UnityEngine;
using System;

namespace PolymindGames
{
    public class CharacterDeathHandler : CharacterBehaviour
    {
        private enum DeathType
        {
            Ragdoll,
            Animation
        }

        [SerializeField, Title("Animation")]
        private DeathType _deathType;
        
        [SerializeField, IndentArea]
        [ShowIf(nameof(_deathType), DeathType.Ragdoll)]
        private CharacterRagdoll _ragdoll;

        [SerializeField, Title("Audio")]
        private AudioData _deathAudio = new(null);

        [SerializeField]
        private AudioData _respawnAudio = new(null);

        [Title("Events")]
        [SerializeField, SpaceArea]
        private UnityEvent _deathEvent;

        [SerializeField]
        private UnityEvent _respawnEvent;
        
        private static readonly int _die = Animator.StringToHash("DIE");

        protected override void OnBehaviourEnable(ICharacter character)
        {
            character.HealthManager.Death += HandleDeath;
            character.HealthManager.Respawn += HandleRespawn;
        }
        
        protected override void OnBehaviourDisable(ICharacter character)
        {
            character.HealthManager.Death -= HandleDeath;
            character.HealthManager.Respawn -= HandleRespawn;
        }

        protected virtual void HandleDeath(in DamageArgs args)
        {
            Character.Audio.PlayClip(_deathAudio, BodyPoint.Head);
            PlayDeathAnimation();
            _deathEvent.Invoke();
        }
        
        protected virtual void HandleRespawn()
        {
            Character.Audio.PlayClip(_respawnAudio, BodyPoint.Head);
            PlayRespawnAnimation();
            _respawnEvent.Invoke();
        }

        protected void PlayDeathAnimation()
        {
            switch (_deathType)
            {
                case DeathType.Ragdoll:
                    _ragdoll.EnableRagdoll();
                    break;
                case DeathType.Animation:
                    Character.Animator.SetTrigger(_die);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        protected void PlayRespawnAnimation()
        {
            switch (_deathType)
            {
                case DeathType.Ragdoll:
                    _ragdoll.DisableRagdoll();
                    break;
                case DeathType.Animation:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}
