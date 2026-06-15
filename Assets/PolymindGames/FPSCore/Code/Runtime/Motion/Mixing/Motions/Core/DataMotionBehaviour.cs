using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Abstract base class for motion behaviours that rely on a data type implementing IMotionData.
    /// </summary>
    /// <typeparam name="T">The type of motion data used by this behaviour.</typeparam>
    public abstract class DataMotionBehaviour<T> : MotionBehaviour, IMotionDataListener where T : MotionData
    {
        [SerializeField]
        [DisableIf(nameof(HasDataHandler), true)]
        private T _data;

        /// <summary>
        /// Gets the current motion data.
        /// </summary>
        protected T Data => _data;
        
        /// <inheritdoc/>
        Type IMotionDataListener.MotionType => typeof(T);

        /// <inheritdoc/>
        void IMotionDataListener.UpdateData(MotionData motionData)
        {
            T prevData = _data;
            _data = motionData as T;

            if (prevData == _data)
                return;
            
            if (_data == null)
            {
                SetTargetPosition(Vector3.zero);
                SetTargetRotation(Vector3.zero);
            }

            OnDataChanged(_data);
        }

        /// <summary>
        /// Called when the motion data changes.
        /// </summary>
        /// <param name="data">The new data.</param>
        protected virtual void OnDataChanged(T data) { }

        /// <summary>
        /// Called when the behaviour becomes enabled and active.
        /// Registers the behaviour to the data handler, if present.
        /// </summary>
        protected sealed override void OnEnable()
        {
            base.OnEnable();

            if (TryGetComponent<IMotionDataHandler>(out var dataHandler))
            {
                _data = null;
                dataHandler.AddChangedListener(this);
            }
        }

        /// <summary>
        /// Called when the behaviour becomes disabled or inactive.
        /// Unregisters the behaviour from the data handler.
        /// </summary>
        protected sealed override void OnDisable()
        {
            base.OnDisable();

            if (TryGetComponent<IMotionDataHandler>(out var dataHandler))
            {
                _data = null;
                dataHandler.RemoveChangedListener(this);
            }
        }

        protected bool HasDataHandler() => gameObject.HasComponent<IMotionDataHandler>();
    }
}