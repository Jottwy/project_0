using System.Collections.Generic;
using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Represents a motion behavior that applies additive shaking effects to position and rotation.
    /// This behavior is adjustable with configurable spring settings for smooth or responsive animations.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault3)]
    [AddComponentMenu("Polymind Games/Motion/Additive Shake Motion")]
    public sealed class AdditiveShakeMotion : MotionBehaviour, IShakeHandler
    {
        private readonly List<Shake> _positionShakes = new();
        private readonly List<Shake> _rotationShakes = new();
        private readonly List<ShakeZone> _zones = new();

        private static readonly Stack<Shake> _shakesPool = CreateShakesPool(16);

        /// <inheritdoc/>
        public void AddShake(in ShakeData shake, float intensity = 1f)
        {
            if (!shake.IsPlayable)
                return;
            
            var profile = shake.Profile;
            AddPositionShake(profile.PositionShake, shake.Duration, shake.Multiplier * intensity);
            AddRotationShake(profile.RotationShake, shake.Duration, shake.Multiplier * intensity);
        }

        public void AddPositionShake(in ShakeSettings3D settings, float duration, float intensity = 1f)
        {
            if (duration < 0.01f)
                return;

            if (_shakesPool.TryPop(out var shake))
            {
                shake.Init(in settings, duration, intensity);
                _positionShakes.Add(shake);

                PositionSpring.Adjust(settings.Spring);
                SetTargetPosition(EvaluateShakes(_positionShakes));
            }
        }

        public void AddRotationShake(in ShakeSettings3D settings, float duration, float intensity = 1f)
        {
            if (duration < 0.01f)
                return;

            if (_shakesPool.TryPop(out var shake))
            {
                shake.Init(in settings, duration, intensity);
                _rotationShakes.Add(shake);

                RotationSpring.Adjust(settings.Spring);
                SetTargetRotation(EvaluateShakes(_rotationShakes));
            }
        }

        public override void UpdateMotion(float deltaTime)
        {
            if (PositionSpring.IsIdle && RotationSpring.IsIdle)
                return;

            SetTargetPosition(EvaluateShakes(_positionShakes));
            SetTargetRotation(EvaluateShakes(_rotationShakes));
        }

        protected override void OnBehaviourStart(ICharacter character)
            => IgnoreParentMultiplier = true;

        private static Vector3 EvaluateShakes(List<Shake> shakes)
        {
            int i = 0;
            Vector3 value = default(Vector3);

            while (i < shakes.Count)
            {
                var shake = shakes[i];
                value += shake.Evaluate();

                if (shake.IsDone)
                {
                    shakes.RemoveAt(i);
                    _shakesPool.Push(shake);
                }
                else
                    i++;
            }

            return value;
        }
        
        private static Stack<Shake> CreateShakesPool(int capacity)
        {
            var shakesPool = new Stack<Shake>(capacity);
            for (int i = 0; i < capacity; i++)
                shakesPool.Push(new Shake());

            return shakesPool;
        }

        #region Internal Types
        private sealed class Shake
        {
            private float _duration;
            private float _endTime;
            private float _speed;
            private float _xAmplitude;
            private float _yAmplitude;
            private float _zAmplitude;

            private static readonly AnimationCurve _decayCurve =
                AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);

            public void Init(in ShakeSettings3D settings, float duration, float amplitude = 1f)
            {
                float xSign = Random.Range(0, 100) > 50 ? 1f : -1f;
                float ySign = Random.Range(0, 100) > 50 ? 1f : -1f;
                float zSign = Random.Range(0, 100) > 50 ? 1f : -1f;

                _xAmplitude = xSign * amplitude * settings.XAmplitude;
                _yAmplitude = ySign * amplitude * settings.YAmplitude;
                _zAmplitude = zSign * amplitude * settings.ZAmplitude;

                _duration = duration;
                _speed = settings.Speed;
                _endTime = Time.fixedTime + _duration;
            }

            public bool IsDone => Time.time > _endTime;

            public Vector3 Evaluate()
            {
                float time = Time.fixedTime;
                float timer = (_endTime - time) * _speed;
                float decay = _decayCurve.Evaluate(1f - (_endTime - time) / _duration);

                return new Vector3(Mathf.Sin(timer) * _xAmplitude * decay,
                    Mathf.Cos(timer) * _yAmplitude * decay,
                    Mathf.Sin(timer) * _zAmplitude * decay);
            }
        }
        #endregion
    }
}