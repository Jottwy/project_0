using System.Collections.Generic;
using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Represents a motion behaviour that applies additive forces to an object's position and rotation
    /// using configurable spring settings. Supports both immediate and delayed forces, as well as force curves.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault3)]
    [AddComponentMenu("Polymind Games/Motion/Additive Force Motion")]
    public sealed class AdditiveForceMotion : MotionBehaviour
    {
        [SerializeField, Title("Interpolation")]
        private SpringSettings _smoothPositionSpring = new(10, 100, 1, 1);

        [SerializeField]
        private SpringSettings _smoothRotationSpring = new(15, 95, 1, 1);

        [SerializeField]
        private SpringSettings _responsivePositionSpring = new(12, 140, 1, 1.1f);

        [SerializeField]
        private SpringSettings _responsiveRotationSpring = new(12, 140, 1, 1.1f);

        private readonly List<CurveForce> _positionCurves = new(2);
        private readonly List<DistributedForce> _positionForces = new(2);
        private readonly List<CurveForce> _rotationCurves = new(2);
        private readonly List<DistributedForce> _rotationForces = new(2);

        private SpringSettings _customPositionSpring;
        private SpringSettings _customRotationSpring;
        private bool _requiresUpdate;

        /// <summary>
        /// Sets custom spring settings for position and rotation.
        /// </summary>
        /// <param name="positionSpring">The custom spring settings for position.</param>
        /// <param name="rotationSpring">The custom spring settings for rotation.</param>
        public void SetCustomSpringSettings(SpringSettings positionSpring, SpringSettings rotationSpring)
        {
            _customPositionSpring = positionSpring;
            _customRotationSpring = rotationSpring;
        }
        
        /// <summary>
        /// Adds a position force that affects the object's position over time.
        /// </summary>
        /// <param name="springForce">The force to apply to the position.</param>
        /// <param name="scale">The scale factor for the force.</param>
        /// <param name="springType">The type of spring to use (smooth or responsive).</param>
        public void AddPositionForce(SpringForce3D springForce, float scale = 1f, SpringType springType = SpringType.Smooth)
        {
            if (springForce.IsEmpty())
                return;

            float time = Time.time;
            Vector3 force = springForce.Force * scale;
            _positionForces.Add(new DistributedForce(force, time + springForce.Duration));
            _requiresUpdate = true;

            PositionSpring.Adjust(GetPositionSpringSettings(springType));
        }

        /// <summary>
        /// Adds an animation curve to modify the position over time.
        /// </summary>
        /// <param name="animCurves">The animation curves defining the position modification.</param>
        /// <param name="springType">The type of spring to use (smooth or responsive).</param>
        public void AddPositionCurve(AnimCurves3D animCurves, SpringType springType = SpringType.Smooth)
        {
            if (animCurves.Duration < 0.01f)
                return;

            float time = Time.time;
            _positionCurves.Add(new CurveForce(animCurves, time));
            _requiresUpdate = true;

            PositionSpring.Adjust(GetPositionSpringSettings(springType));
        }

        /// <summary>
        /// Adds a rotation force that affects the object's rotation over time.
        /// </summary>
        /// <param name="springForce">The force to apply to the rotation.</param>
        /// <param name="scale">The scale factor for the force.</param>
        /// <param name="springType">The type of spring to use (smooth or responsive).</param>
        public void AddRotationForce(SpringForce3D springForce, float scale = 1f, SpringType springType = SpringType.Smooth)
        {
            if (springForce.IsEmpty())
                return;

            float time = Time.time;
            Vector3 force = springForce.Force * scale;
            _rotationForces.Add(new DistributedForce(force, time + springForce.Duration));
            _requiresUpdate = true;

            RotationSpring.Adjust(GetRotationSpringSettings(springType));
        }

        /// <summary>
        /// Adds an animation curve to modify the rotation over time.
        /// </summary>
        /// <param name="animCurves">The animation curves defining the rotation modification.</param>
        /// <param name="springType">The type of spring to use (smooth or responsive).</param>
        public void AddRotationCurve(AnimCurves3D animCurves, SpringType springType = SpringType.Smooth)
        {
            if (animCurves.Duration < 0.01f)
                return;

            float time = Time.time;
            _rotationCurves.Add(new CurveForce(animCurves, time));
            _requiresUpdate = true;

            RotationSpring.Adjust(GetRotationSpringSettings(springType));
        }

        /// <summary>
        /// Adds a delayed position force that affects the object's position after a specified delay.
        /// </summary>
        /// <param name="force">The delayed force to apply to the position.</param>
        /// <param name="scale">The scale factor for the force.</param>
        /// <param name="springType">The type of spring to use (smooth or responsive).</param>
        public void AddDelayedPositionForce(DelayedSpringForce3D force, float scale = 1f, SpringType springType = SpringType.Smooth)
        {
            CoroutineUtility.InvokeDelayed(this, () => AddPositionForce(force.SpringForce, scale, springType), force.Delay);
        }

        /// <summary>
        /// Adds a delayed rotation force that affects the object's rotation after a specified delay.
        /// </summary>
        /// <param name="force">The delayed force to apply to the rotation.</param>
        /// <param name="scale">The scale factor for the force.</param>
        /// <param name="springType">The type of spring to use (smooth or responsive).</param>
        public void AddDelayedRotationForce(DelayedSpringForce3D force, float scale = 1f, SpringType springType = SpringType.Smooth)
        {
            CoroutineUtility.InvokeDelayed(this, () => AddRotationForce(force.SpringForce, scale, springType), force.Delay);
        }

        public override void UpdateMotion(float deltaTime)
        {
            if (!_requiresUpdate)
                return;

            float time = Time.time;

            Vector3 targetPosition = EvaluatePositionForces(time);
            Vector3 targetRotation = EvaluateRotationForces(time);

            SetTargetPosition(targetPosition);
            SetTargetRotation(targetRotation);

            if (targetPosition == Vector3.zero && targetPosition == Vector3.zero && PositionSpring.IsIdle && RotationSpring.IsIdle)
                _requiresUpdate = false;
        }
        
        protected override void OnBehaviourStart(ICharacter character) => IgnoreParentMultiplier = true;
        protected override SpringSettings GetDefaultPositionSpringSettings() => _smoothPositionSpring;
        protected override SpringSettings GetDefaultRotationSpringSettings() => _smoothRotationSpring;

        private SpringSettings GetPositionSpringSettings(SpringType type) => type switch
        {
            SpringType.Smooth => _smoothPositionSpring,
            SpringType.Responsive => _responsivePositionSpring,
            SpringType.Custom => _customPositionSpring,
            _ => SpringSettings.Default
        };

        private SpringSettings GetRotationSpringSettings(SpringType type) => type switch
        {
            SpringType.Smooth => _smoothRotationSpring,
            SpringType.Responsive => _responsiveRotationSpring,
            SpringType.Custom => _customRotationSpring,
            _ => SpringSettings.Default
        };

        private Vector3 EvaluatePositionForces(float time)
        {
            Vector3 force = Vector3.zero;
            for (int i = _positionForces.Count - 1; i >= 0; i--)
            {
                force += _positionForces[i].Force;

                if (time > _positionForces[i].EndTime)
                    _positionForces.RemoveAt(i);
            }

            for (int i = _positionCurves.Count - 1; i >= 0; i--)
            {
                var animCurve = _positionCurves[i].AnimCurve;
                float startTime = _positionCurves[i].StartTime;

                force += animCurve.Evaluate(time - startTime);

                if (time > startTime + animCurve.Duration)
                    _positionCurves.RemoveAt(i);
            }

            return force;
        }

        private Vector3 EvaluateRotationForces(float time)
        {
            Vector3 force = Vector3.zero;
            for (int i = _rotationForces.Count - 1; i >= 0; i--)
            {
                force += _rotationForces[i].Force;

                if (time > _rotationForces[i].EndTime)
                    _rotationForces.RemoveAt(i);
            }

            for (int i = _rotationCurves.Count - 1; i >= 0; i--)
            {
                var animCurve = _rotationCurves[i].AnimCurve;
                float startTime = _rotationCurves[i].StartTime;

                force += animCurve.Evaluate(time - startTime);

                if (time > startTime + animCurve.Duration)
                    _rotationCurves.RemoveAt(i);
            }

            return force;
        }

        #region Internal Types
        private readonly struct DistributedForce
        {
            public readonly Vector3 Force;
            public readonly float EndTime;

            public DistributedForce(Vector3 force, float endTime)
            {
                Force = force;
                EndTime = endTime;
            }
        }

        private readonly struct CurveForce
        {
            public readonly AnimCurves3D AnimCurve;
            public readonly float StartTime;

            public CurveForce(AnimCurves3D animCurve, float startTime)
            {
                AnimCurve = animCurve;
                StartTime = startTime;
            }
        }
        #endregion
    }
}