using System.Runtime.CompilerServices;
using UnityEngine;

namespace PolymindGames.SurfaceSystem
{
    [RequireCharacterComponent(typeof(IMovementControllerCC), typeof(IMotorCC))]
    public sealed class FootstepsController : CharacterBehaviour
    {
        private enum Footstep { Left, Right }

        [SerializeField, Title("Raycast Settings")]
        private LayerMask _layerMask = LayerConstants.SimpleSolidObjectsMask;
        
        [SerializeField, Range(0.01f, 1f)]
        [Tooltip("The maximum distance for spherecasting to detect surfaces underfoot. This determines how far ahead the footstep ray will cast.")]
        private float _footstepRaycastDistance = 0.3f;

        [SerializeField, Range(0.01f, 0.5f)]
        [Tooltip("The radius used for spherecasting to detect surfaces for footsteps. A larger radius increases detection sensitivity.")]
        private float _footstepRaycastRadius = 0.3f;

        [SerializeField, Range(0f, 24f), Title("Footsteps Thresholds")]
        [Tooltip("The minimum speed required for a footstep to be audible at the lowest volume. Below this speed, footsteps won't play.")]
        private float _minSpeedForFootstepVolume = 1f;

        [SerializeField, Range(0f, 24f)]
        [Tooltip("The maximum speed at which footsteps will play at full volume. Speeds above this will not increase the volume further.")]
        private float _maxSpeedForFootstepVolume = 7f;

        [SerializeField, Range(0f, 25f), Title("Fall Impact Thresholds")]
        [Tooltip("The minimum fall impact speed at which an impact effect will be triggered. Falling faster than this will activate fall impact effects.")]
        private float _minFallImpactSpeedForEffect = 4f;

        [SerializeField, Range(0f, 25f)]
        [Tooltip("The fall impact speed at which the full fall impact effect audio will be played at maximum intensity.")]
        private float _maxFallImpactSpeedForFullEffect = 11f;

        private IMovementControllerCC _movement;
        private SurfaceDefinition _lastSurface;
        private Footstep _lastFootDown;
        private float _fallImpactTimer;
        private bool _hasLastSurface;
        private IMotorCC _motor;

        protected override void OnBehaviourStart(ICharacter character)
        {
            _movement = character.GetCC<IMovementControllerCC>();
            _motor = character.GetCC<IMotorCC>();
        }

        protected override void OnBehaviourEnable(ICharacter character)
        {
            _movement.StepCycleEnded += PlayFootstepEffect;
            _motor.FallImpact += PlayFallImpactEffects;

            _movement.SpeedModifier.AddModifier(GetVelocity);
            _movement.AccelerationModifier.AddModifier(GetAcceleration);
            _movement.DecelerationModifier.AddModifier(GetDeceleration);
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            _movement.StepCycleEnded -= PlayFootstepEffect;
            _motor.FallImpact -= PlayFallImpactEffects;

            _movement.SpeedModifier.RemoveModifier(GetVelocity);
            _movement.AccelerationModifier.RemoveModifier(GetAcceleration);
            _movement.DecelerationModifier.RemoveModifier(GetDeceleration);
        }

        private float GetVelocity() => _lastSurface?.VelocityModifier ?? 1f;
        private float GetAcceleration() => _lastSurface?.SurfaceFriction ?? 1f;
        private float GetDeceleration() => _lastSurface?.SurfaceFriction ?? 1f;

        private void PlayFootstepEffect()
        {
            MovementStateType stateType = _movement.ActiveState;

            if (CheckGround(out RaycastHit hit))
            {
                _lastFootDown = _lastFootDown == Footstep.Left ? Footstep.Right : Footstep.Left;
                float audioVolume = Mathf.Clamp(_motor.Velocity.magnitude + _motor.TurnSpeed, _minSpeedForFootstepVolume, _maxSpeedForFootstepVolume) / _maxSpeedForFootstepVolume;
                _lastSurface = SurfaceManager.Instance.PlayEffectFromHit(in hit, GetEffectType(stateType), SurfaceEffectPlayFlags.Audio, audioVolume);
            }
        }

        private void PlayFallImpactEffects(float impactSpeed)
        {
            if (Mathf.Abs(impactSpeed) > _minFallImpactSpeedForEffect && Time.time > _fallImpactTimer)
            {
                _fallImpactTimer = Time.time + 0.3f;
                if (CheckGround(out RaycastHit hit))
                {
                    float audioVolume = Mathf.Min(1f, impactSpeed / (_maxFallImpactSpeedForFullEffect - _minFallImpactSpeedForEffect));
                    _lastSurface = SurfaceManager.Instance.PlayEffectFromHit(in hit, SurfaceEffectType.FallImpact, SurfaceEffectPlayFlags.Audio, audioVolume);
                }
            }
        }

        private bool CheckGround(out RaycastHit hit)
        {
            var ray = new Ray(transform.position + Vector3.up * 0.3f, Vector3.down);
            bool hitSomething = Physics.Raycast(ray, out hit, _footstepRaycastDistance, _layerMask, QueryTriggerInteraction.Ignore);

            if (!hitSomething)
                hitSomething = Physics.SphereCast(ray, _footstepRaycastRadius, out hit, _footstepRaycastDistance, _layerMask, QueryTriggerInteraction.Ignore);

            return hitSomething;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SurfaceEffectType GetEffectType(MovementStateType stateType)
        {
            bool isRunning = stateType == MovementStateType.Run;
            SurfaceEffectType footstepType = isRunning ? SurfaceEffectType.RunFootstep : SurfaceEffectType.WalkFootstep;
            return footstepType;
        }
    }
}