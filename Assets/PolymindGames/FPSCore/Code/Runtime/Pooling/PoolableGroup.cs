using System.Collections.Generic;
using PolymindGames.SaveSystem;
using UnityEngine;
using System;

namespace PolymindGames.PoolingSystem
{
    public sealed class PoolableGroup : MonoBehaviour, IPool<Poolable>, ISaveableComponent
    {
        [SerializeField, DisableInPlayMode]
        private bool _resetTransforms;

        [SerializeField, DisableInPlayMode]
        [EditorButton(nameof(ResetPoolables))]
        [EditorButton(nameof(ResetInactivePoolables))]
        [ShowIf(nameof(_resetTransforms), true)]
        private bool _resetRigidbodies;

        private readonly Stack<Poolable> _inactivePoolables = new();
        private SerializedTransformData[] _initialPoolablePositions;
        private Poolable[] _allPoolables;

        public int InactiveCount => _inactivePoolables.Count;

        public void ResetInactivePoolables()
        {
            while (_inactivePoolables.Count > 0)
                Get();
        }

        public void ResetPoolables()
        {
            _inactivePoolables.Clear();
            foreach (var poolable in _allPoolables)
            {
                poolable.gameObject.SetActive(true);
                poolable.OnAcquired();
            }
        }

        public Poolable Get()
        {
            if (_inactivePoolables.TryPop(out var poolable))
            {
                poolable.gameObject.SetActive(true);
                poolable.OnAcquired();
                return poolable;
            }

            return null;
        }

        public Poolable Get(Vector3 position, Quaternion rotation, Transform parent = null)
        {
            var instance = Get();
            if (instance != null)
            {
                var instanceTrs = instance.transform;
                instanceTrs.SetPositionAndRotation(position, rotation);
                if (!ReferenceEquals(parent, null))
                    instanceTrs.SetParent(parent, true);
            }

            return instance;
        }

        public void Release(Poolable instance)
        {
            _inactivePoolables.Push(instance);

            if (_resetTransforms)
            {
                int allObjectsIndex = _allPoolables.IndexOf(instance);
                instance.transform.SetPositionAndRotation(_initialPoolablePositions[allObjectsIndex].Position,
                    _initialPoolablePositions[allObjectsIndex].Rotation);

                if (_resetRigidbodies)
                {
                    var rigidB = instance.gameObject.GetComponent<Rigidbody>();
                    rigidB.linearVelocity = Vector3.zero;
                    rigidB.angularVelocity = Vector3.zero;
                }
            }

            instance.gameObject.SetActive(false);
        }

        public void Prewarm(int count) { }
        public void Clear() => _inactivePoolables.Clear();
        public void Dispose() => Clear();

        private void Awake()
        {
            _allPoolables = GetComponentsInChildren<Poolable>();
            foreach (var poolable in _allPoolables)
            {
                poolable.SetParentPool(this);
            }

            _initialPoolablePositions = _resetTransforms ? GetTransformData() : Array.Empty<SerializedTransformData>();
        }

        private SerializedTransformData[] GetTransformData()
        {
            var transformData = new SerializedTransformData[_allPoolables.Length];

            for (int i = 0; i < _allPoolables.Length; i++)
                transformData[i] = new SerializedTransformData(_allPoolables[i].transform);

            return transformData;
        }

        #region Save & Load
        private sealed class SaveData
        {
            public SerializedTransformData[] ChildrenTransforms;
            public bool[] ActiveList;
        }

        void ISaveableComponent.LoadMembers(object data)
        {
            var saveData = (SaveData)data;
            var transformData = saveData.ChildrenTransforms;
            bool[] activeList = saveData.ActiveList;

            for (int i = 0; i < _allPoolables.Length; i++)
            {
                var poolableObject = _allPoolables[i];

                if (!activeList[i])
                    poolableObject.Release();

                SerializedTransformData.ApplyToTransform(poolableObject.transform, in transformData[i]);
            }
        }

        object ISaveableComponent.SaveMembers()
        {
            bool[] activeList = new bool[_allPoolables.Length];

            for (int i = 0; i < _allPoolables.Length; i++)
                activeList[i] = _allPoolables[i].gameObject.activeSelf;

            return new SaveData
            {
                ChildrenTransforms = GetTransformData(),
                ActiveList = activeList
            };
        }
        #endregion
    }
}