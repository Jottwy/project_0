using System.Collections.Generic;
using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Represents the wieldable representation of carrying an object.
    /// </summary>
    [AddComponentMenu("Polymind Games/Wieldables/Tools/Carryable")]
    public sealed class WieldableCarryable : MonoBehaviour
    {
        [SerializeField, Range(0f, 0.1f)]
        private float _speedDecreasePerWeightUnit = 0.01f;
        
        [SerializeField, NotNull, Title("References")]
        [Tooltip("Motion component for controlling movement.")]
        private WieldableMotion _wieldableMotion;
        
        [SerializeField, NotNull]
        [Tooltip("Animator component for controlling animations.")]
        private WieldableAnimator _wieldableAnimator;

        [SerializeField, NotNull]
        [Tooltip("The socket for the left hand.")]
        private Transform _leftHandSocket;

        [SerializeField, NotNull]
        [Tooltip("The socket for the right hand.")]
        private Transform _rightHandSocket;

        private const float MinMovementSpeed = 0.5f;
        
        private readonly List<CarryablePickup> _pickups = new();
        private WieldableCarrySettings _settings;
        private IWieldable _wieldable;
        private float _weight;

        public IWieldable Wieldable => _wieldable ??= GetComponent<IWieldable>();
        public int CarryCount => _pickups.Count;
        
        /// <summary>
        /// Adds a carryable item to this wieldable item.
        /// </summary>
        /// <param name="pickup">The carryable pickup item to add.</param>
        public void AddCarryable(CarryablePickup pickup)
        {
            _pickups.Add(pickup);
            _weight = pickup.Rigidbody.mass;
            
            var settings = pickup.Definition.WieldableSettings;
            
            if (_settings != settings)
                SetSettings(settings);

            Transform parent = GetTransformForSocket(settings.TargetSocket);
            var offset = settings.Offsets[_pickups.Count - 1];
            Vector3 position = offset.PositionOffset;
            Quaternion rotation = parent.localRotation * Quaternion.Euler(offset.RotationOffset);
            
            var pickupTransform = pickup.transform;
            pickupTransform.SetParent(parent);
            pickupTransform.SetLocalPositionAndRotation(position, rotation);
            pickupTransform.localScale = Vector3.one;
        }

        /// <summary>
        /// Removes a carryable item from this wieldable item.
        /// </summary>
        /// <param name="pickup">The carryable pickup item to remove.</param>
        public void RemoveCarryable(CarryablePickup pickup)
        {
            if (_pickups.Remove(pickup))
            {
                if (pickup != null)
                {
                    var pickupTransform = pickup.transform;
                    pickupTransform.SetParent(null);
                    pickupTransform.localScale = Vector3.one;
                }
            }
        }

        /// <summary>
        /// Retrieves the position and rotation offsets of a pickup at the specified index.
        /// </summary>
        /// <param name="index">The index of the pickup.</param>
        /// <returns>A tuple containing the position and rotation offsets.</returns>
        public (Vector3 position, Vector3 rotation) GetOffsetsAtIndex(int index)
        {
            if (_pickups.Count <= index)
                return default((Vector3 position, Vector3 rotation));

            var pickupTrs = _pickups[index].transform;
            return (pickupTrs.localPosition, pickupTrs.localEulerAngles);
        }

        /// <summary>
        /// Refreshes the position and rotation of the pickups.
        /// </summary>
        public void RefreshVisuals()
        {
            if (_settings == null)
                return;
            
            SetSettings(_settings);
            
            for (int i = 0; i < _pickups.Count; i++)
            {
                // Retrieve the parent transform for the target socket.
                var parent = GetTransformForSocket(_settings.TargetSocket);

                // Retrieve the offset settings for the pickup.
                var offset = _settings.Offsets[i];

                // Calculate the final position and rotation of the pickup based on the parent's transform and offset settings.
                var position = parent.TransformPoint(offset.PositionOffset);
                var rotation = parent.rotation * Quaternion.Euler(offset.RotationOffset);

                // Apply the calculated position and rotation to the pickup.
                _pickups[i].transform.SetPositionAndRotation(position, rotation);
            }
        }

        /// <summary>
        /// Sets the wieldable carry settings.
        /// </summary>
        /// <param name="settings">The settings to apply.</param>
        private void SetSettings(WieldableCarrySettings settings)
        {
            _settings = settings;
            _wieldableAnimator.Animator.runtimeAnimatorController = settings.Animator;
            _wieldableMotion.SetProfile(settings.Motion);
            _wieldableMotion.PositionOffset = settings.PositionOffset;
            _wieldableMotion.RotationOffset = settings.RotationOffset;
        }

        /// <summary>
        /// Gets the transform associated with the specified socket.
        /// </summary>
        /// <param name="socket">The socket to get the transform for.</param>
        /// <returns>The transform associated with the specified socket.</returns>
        private Transform GetTransformForSocket(WieldableCarrySettings.Socket socket) => socket switch
        {
            WieldableCarrySettings.Socket.LeftHand => _leftHandSocket,
            WieldableCarrySettings.Socket.RightHand => _rightHandSocket,
            _ => throw new ArgumentOutOfRangeException(nameof(socket), socket, null)
        };

        private void Awake()
        {
            if (Wieldable is IMovementSpeedHandler speedHandler)
                speedHandler.SpeedModifier.AddModifier(GetSpeed);
        }

        private float GetSpeed()
        {
            return Mathf.Max(1 - _weight * _speedDecreasePerWeightUnit * _pickups.Count, MinMovementSpeed);
        }
    }
}