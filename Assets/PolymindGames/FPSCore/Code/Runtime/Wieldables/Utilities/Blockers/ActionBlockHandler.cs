using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    public sealed class ActionBlockHandler
    {
        private List<Object> _blockers;
        private float _blockTimer;
        private bool _isBlocked;
        
        public bool IsBlocked => _isBlocked || _blockTimer > Time.time;

        public event UnityAction OnBlocked;

        /// <summary>
        /// Blocks this action for a set duration.
        /// </summary>
        /// <param name="duration"></param>
        public void AddDurationBlocker(float duration)
        {
            float value = Time.time + duration;

            if (_blockTimer < value)
            {
                if (_blockTimer < Time.time)
                    OnBlocked?.Invoke();

                _blockTimer = value;
            }
        }

        public void ClearDurationBlockers() => _blockTimer = 0f;

        public void AddBlocker(Object blocker)
        {
            _blockers ??= new List<Object>(1);
            if (_blockers.Contains(blocker))
                return;

            bool wasBlocked = _isBlocked;

            _blockers.Add(blocker);
            _isBlocked = true;

            if (!wasBlocked && _isBlocked)
                OnBlocked?.Invoke();
        }

        public void RemoveBlocker(Object blocker)
        {
            if (_blockers != null && _blockers.Remove(blocker))
                _isBlocked = _blockers.Count > 0;
        }
    }
}