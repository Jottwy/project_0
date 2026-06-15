using System.Collections.Generic;
using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    [CreateAssetMenu(menuName = "Polymind Games/Motion/Motion Preset", fileName = "Motion_", order = 100)]
    public sealed class MotionProfile : ScriptableObject
    {
        [SerializeField]
        [Help("Fallback preset. If a certain type of data is not found in this preset, the base profile will be used instead.")]
        private MotionProfile _baseProfile;

        [ReorderableList]
        [SerializeField, SpaceArea, LabelByChild("State")]
        [Help("The data from the 'None' state will be used if no data of the needed type is found in the current state.")]
        private StateData[] _motionData = Array.Empty<StateData>();

        private Dictionary<MotionDataEntry, MotionData> _states;
        private bool _hasBaseProfile;

        /// <summary>
        /// Retrieves motion data of the specified type and state.
        /// Falls back to the base profile or 'None' state if not found.
        /// </summary>
        public MotionData GetData(MovementStateType stateType, Type motionType)
        {
            if (_states == null)
            {
                _states = CreateDataCache();
                _hasBaseProfile = _baseProfile != null;
            }

            var entry = new MotionDataEntry(stateType, motionType);

            // Attempt to retrieve the requested motion data
            if (_states.TryGetValue(entry, out var data))
                return data;

            // Attempt fallback to the 'None' state in this profile
            entry = new MotionDataEntry(MovementStateType.None, motionType);
            if (_states.TryGetValue(entry, out data))
                return data;

            // Fallback to base profile if applicable
            return _hasBaseProfile ? _baseProfile.GetData(stateType, motionType) : null;
        }

        /// <summary>
        /// Retrieves motion data of the specified type and state.
        /// Falls back to the base profile or 'None' state if not found.
        /// </summary>
        public T GetData<T>(MovementStateType stateType) where T : MotionData
        {
            return GetData(stateType, typeof(T)) as T;
        }

        /// <summary>
        /// Creates a cache of motion data for quick lookup by state and type.
        /// </summary>
        /// <returns>A dictionary of motion data entries mapped to their corresponding data.</returns>
        private Dictionary<MotionDataEntry, MotionData> CreateDataCache()
        {
            var dict = new Dictionary<MotionDataEntry, MotionData>();

            foreach (var stateData in _motionData)
            {
                foreach (var motion in stateData.Motions)
                {
                    if (motion == null)
                    {
                        Debug.LogError($"Motion is null for state {stateData.State}.", this);
                        continue;
                    }

                    var entry = new MotionDataEntry(stateData.State, motion.GetType());
                    if (!dict.TryAdd(entry, motion))
                        Debug.LogWarning($"Duplicate motion data found for state {stateData.State} and type {motion.GetType().Name}.", this);
                }
            }

            return dict;
        }

        #region Internal Types
        [Serializable]
        public class StateData
        {
            public MovementStateType State;

            [SerializeReference, SpaceArea]
            [ReorderableList(elementLabel: "Motion")]
            [ReferencePicker(typeof(MotionData), TypeGrouping.ByFlatName)]
            public MotionData[] Motions = Array.Empty<MotionData>();
        }

        private readonly struct MotionDataEntry
        {
            private readonly MovementStateType _stateType;
            private readonly Type _motionType;

            public MotionDataEntry(MovementStateType stateType, Type motionType)
            {
                _stateType = stateType;
                _motionType = motionType;
            }

            public override int GetHashCode()
            {
                return HashCode.Combine((int)_stateType, _motionType.GetHashCode());
            }

            public override bool Equals(object obj)
            {
                return obj is MotionDataEntry other &&
                       _stateType == other._stateType &&
                       _motionType == other._motionType;
            }
        }
        #endregion

        #region Editor Only
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_baseProfile != null)
            {
                if (_baseProfile == this || _baseProfile._baseProfile == this)
                {
                    UnityEditor.EditorUtility.SetDirty(this);
                    _baseProfile = null;
                }
            }

            if (Application.isPlaying)
                _states = CreateDataCache();
        }
#endif
        #endregion
    }
}