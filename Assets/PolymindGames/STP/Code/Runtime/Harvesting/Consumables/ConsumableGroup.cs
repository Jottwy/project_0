using PolymindGames.WorldManagement;
using PolymindGames.PoolingSystem;
using System.Collections.Generic;
using UnityEngine;
using System;
using PolymindGames.SaveSystem;

namespace PolymindGames.ResourceHarvesting
{
    public sealed class ConsumableGroup : MonoBehaviour, ISaveableComponent, IPool<Poolable>
    {
        [SerializeField, MinMaxSlider(0f, 1000f)]
        private Vector2Int _respawnHours = new(16, 24);

        private List<RespawnData> _inactivePoolables = new();
        private Poolable[] _allPoolables;

        public Poolable Get()
        {
            if (_inactivePoolables.Count > 0)
            {
                int index = _inactivePoolables.Count - 1;
                var poolable = _inactivePoolables[index].Poolable;
                _inactivePoolables.RemoveAt(index);
                
                poolable.OnAcquired();
                poolable.gameObject.SetActive(true);

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
            instance.gameObject.SetActive(false);
            int respawnHour = World.Instance.Time.TotalHours + _respawnHours.GetRandomFromRange();

            _inactivePoolables ??= new List<RespawnData>();
            _inactivePoolables.Add(new RespawnData(instance, respawnHour));

            if (_inactivePoolables.Count == 1)
                SubscribeToTimeChanges();
        }

        public void Dispose() => Clear();
        public void Clear() => _inactivePoolables.Clear();
        public int InactiveCount => _inactivePoolables.Count;
        public void Prewarm(int count) { }

        private void SubscribeToTimeChanges() => World.Instance.Time.HourChanged += OnHourChanged;
        private void UnsubscribeFromTimeChanges() => World.Instance.Time.HourChanged -= OnHourChanged;

        private void OnHourChanged(int totalHours, int passedHours)
        {
            if (passedHours < 0)
                return;

            for (int i = _inactivePoolables.Count - 1; i >= 0; i--)
            {
                var respawnData = _inactivePoolables[i];
                if (totalHours >= respawnData.RespawnHour)
                {
                    respawnData.Poolable.gameObject.SetActive(true);
                    _inactivePoolables.RemoveAt(i);

                    if (_inactivePoolables.Count == 0)
                        UnsubscribeFromTimeChanges();
                }
            }
        }

        private void Awake()
        {
            _allPoolables = GetComponentsInChildren<Poolable>();
            foreach (var poolableObject in _allPoolables)
                poolableObject.SetParentPool(this, float.MaxValue);
        }

        private void OnDestroy()
        {
            if (_inactivePoolables != null && _inactivePoolables.Count > 0)
                UnsubscribeFromTimeChanges();
        }

        #region Internal Types
        private readonly struct RespawnData
        {
            public readonly Poolable Poolable;
            public readonly int RespawnHour;

            public RespawnData(Poolable poolable, int respawnHour)
            {
                Poolable = poolable;
                RespawnHour = respawnHour;
            }
        }
        #endregion

        #region Save & Load
        [Serializable]
        private sealed class RespawnSaveData
        {
            public int PoolableIndex;
            public int RemainingHours;
        }

        void ISaveableComponent.LoadMembers(object data)
        {
            if (data == null)
                return;

            var saveData = (RespawnSaveData[])data;

            if (saveData.Length > 0)
            {
                _inactivePoolables = new List<RespawnData>(saveData.Length);
                SubscribeToTimeChanges();
            }

            foreach (var respawnData in saveData)
            {
                var consumable = _allPoolables[respawnData.PoolableIndex];
                consumable.gameObject.SetActive(false);
                int targetHour = World.Instance.Time.TotalHours + respawnData.RemainingHours;
                _inactivePoolables.Add(new RespawnData(consumable, targetHour));
            }

            enabled = saveData.Length > 0;
        }

        object ISaveableComponent.SaveMembers()
        {
            if (_inactivePoolables == null || _inactivePoolables.Count == 0)
                return null;

            var respawnData = new RespawnSaveData[_inactivePoolables.Count];
            for (int i = 0; i < _inactivePoolables.Count; i++)
            {
                respawnData[i] = new RespawnSaveData
                {
                    PoolableIndex = Array.IndexOf(_allPoolables, _inactivePoolables[i].Poolable),
                    RemainingHours = _inactivePoolables[i].RespawnHour - World.Instance.Time.TotalHours
                };
            }

            return respawnData;
        }
        #endregion
    }
}