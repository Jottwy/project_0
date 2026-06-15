using PolymindGames.ProceduralMotion;
using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Manages the movement and animation of weapon or wieldable item parts, applying smooth position
    /// and rotation offsets with configurable delays and easing effects.
    /// </summary>
    [Serializable]
    public sealed class WieldableMovingPart
    {
        [Serializable]
        private struct PartTransform
        {
            public Transform Part;
            public Vector3 PositionOffset;
            public Vector3 RotationOffset;
        }

        [Title("Timing Settings")]
        [SerializeField, Range(0f, 10f)]
        [Tooltip("Delay before movement starts.")]
        private float _movementStartDelay = 0.1f;

        [SerializeField, Range(0f, 10f)]
        [Tooltip("Duration of the movement's easing effect.")]
        private float _easingDuration = 0.1f;

        [SerializeField, Range(0f, 10f)]
        [Tooltip("Duration over which movement stops.")]
        private float _stopDuration;

        [SerializeField, Title("Easing Settings")]
        [Tooltip("Type of easing to apply during movement.")]
        private EaseType _easingType = EaseType.SineIn;
        
        [SerializeField, IgnoreParent]
        [ReorderableList(ListStyle.Lined)]
        [Tooltip("List of parts that will move and rotate.")]
        private PartTransform[] _partsToAnimate = Array.Empty<PartTransform>();

        private float _movementTimer = -1f;
        private float _currentEasingTime;
        private bool _isAnimating;

        /// <summary>
        /// Starts the movement of the parts after the configured delay.
        /// </summary>
        public void BeginMovement()
        {
            _movementTimer = Time.time + _movementStartDelay;
            _isAnimating = true;
        }

        /// <summary>
        /// Stops the movement of the parts and transitions to a stopping phase.
        /// </summary>
        public void StopMovement()
        {
            _movementTimer = Time.time;
            _isAnimating = false;
        }

        /// <summary>
        /// Updates the state of the part movements, applying easing as necessary.
        /// Should be called in an update loop.
        /// </summary>
        public void UpdateMovement()
        {
            if (_isAnimating)
            {
                if (_movementTimer < Time.time)
                {
                    _currentEasingTime = Mathf.Clamp01(1 - (_easingDuration - (Time.time - _movementTimer)) / _easingDuration);
                    ApplyTransformation(Mathf.Clamp01(Easer.Apply(_easingType, _currentEasingTime)));
                }
            }
            else
            {
                if (_currentEasingTime > 0f)
                {
                    _currentEasingTime -= Time.deltaTime / _stopDuration;
                    ApplyTransformation(Mathf.Clamp01(Easer.Apply(_easingType, _currentEasingTime)));
                }
            }
        }

        /// <summary>
        /// Applies positional and rotational transformations to each part based on the easing factor.
        /// </summary>
        /// <param name="easingFactor">The current easing factor (0 to 1).</param>
        private void ApplyTransformation(float easingFactor)
        {
            if (easingFactor < 0.01f)
                return;

            foreach (var part in _partsToAnimate)
            {
                if (part.Part == null) continue;

                part.Part.position += part.Part.TransformDirection(part.PositionOffset * easingFactor);
                part.Part.rotation *= Quaternion.Euler(part.RotationOffset * easingFactor);
            }
        }
    }
}