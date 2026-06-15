using System.Collections.Generic;
using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Handles motion data and notifies listeners when motion data changes. It manages profiles, state types, 
    /// and overrides for motion data. 
    /// </summary>
    [RequireCharacterComponent(typeof(IMovementControllerCC))]
    public sealed class MotionDataHandler : MonoBehaviour, IMotionDataHandler
    {
        [SerializeField]
        private MotionProfile _defaultProfile;

        private readonly Dictionary<Type, List<IMotionDataListener>> _listenersLookup = new();
        private readonly Dictionary<Type, MotionData> _motionDataOverrides = new();
        private readonly List<MotionProfile> _profilesStack = new();
        private MovementStateType _stateType;

        /// <inheritdoc/>
        public void PushProfile(MotionProfile profile)
        {
            if (profile == null)
                return;

            _profilesStack.Add(profile);
            UpdateListeners(_stateType);
        }

        /// <inheritdoc/>
        public void PopProfile(MotionProfile profile)
        {
            if (_profilesStack.Remove(profile))
                UpdateListeners(_stateType);
        }

        /// <inheritdoc/>
        public void AddChangedListener(IMotionDataListener listener)
        {
            if (listener == null)
                return;

            if (_listenersLookup.TryGetValue(listener.MotionType, out var list))
            {
                list.Add(listener);
            }
            else
            {
                _listenersLookup.Add(listener.MotionType, new List<IMotionDataListener>
                {
                    listener
                });
            }
        }

        /// <inheritdoc/>
        public void RemoveChangedListener(IMotionDataListener listener)
        {
            if (listener == null)
                return;

            if (_listenersLookup.TryGetValue(listener.MotionType, out var list))
            {
                list.Remove(listener);
            }
        }

        /// <inheritdoc/>
        public void SetDataOverride<T>(T data) where T : MotionData
        {
            if (data != null)
                _motionDataOverrides[typeof(T)] = data;
            else
                _motionDataOverrides.Remove(typeof(T));
            
            UpdateListeners(typeof(T), data);
        }

        /// <inheritdoc/>
        public void SetStateType(MovementStateType stateType)
        {
            _stateType = stateType;
            UpdateListeners(stateType);
        }

        /// <summary>
        /// Called at the start of the script to push the default profile if set.
        /// </summary>
        private void Start() => PushProfile(_defaultProfile);

        /// <summary>
        /// Updates all listeners for the current state type and motion data.
        /// </summary>
        private void UpdateListeners(MovementStateType stateType)
        {
            foreach (var pair in _listenersLookup)
            {
                var motionData = GetMotionData(stateType, pair.Key);
                UpdateListeners(pair.Key, motionData);
            }
        }
        
        /// <summary>
        /// Notifies all listeners of the updated motion data for the specified motion type.
        /// </summary>
        private void UpdateListeners(Type motionType, MotionData motionData)
        {
            if (_listenersLookup.TryGetValue(motionType, out var listeners))
            {
                foreach (var listener in listeners)
                    listener.UpdateData(motionData);
            }
        }

        /// <summary>
        /// Retrieves the motion data for a given state type and motion type.
        /// </summary>
        /// <param name="stateType">The current state type.</param>
        /// <param name="motionType">The motion data type to retrieve.</param>
        /// <returns>The corresponding motion data, or null if not found.</returns>
        private MotionData GetMotionData(MovementStateType stateType, Type motionType)
        {
            if (_motionDataOverrides.TryGetValue(motionType, out var motionData))
                return motionData;

            // Search through the profiles stack in reverse order (LIFO)
            for (int i = _profilesStack.Count - 1; i >= 0; i--)
            {
                var data = _profilesStack[i].GetData(stateType, motionType);
                if (data != null)
                    return data;
            }

            return null;
        }
    }
}