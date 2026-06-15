using PolymindGames.ProceduralMotion;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.Demo
{
    public sealed class MovingTarget : MonoBehaviour
    {
        [SerializeField, Title("Settings")]
        private EaseType _easingType;

        [SerializeField]
        [ReorderableList(ListStyle.Lined, elementLabel: "Point")]
        private Target[] _points;

        private Vector3 _initialPosition;
        private Vector3 _initialRotation;
        private float _interpolation;
        private float _pauseTimer;
        private int _currentIndex;
        private int _targetIndex;


        private void Start()
        {
            if (_points == null || _points.Length < 2)
            {
                enabled = false;
                Debug.LogWarning("2 or more points are require, disabling this component.", gameObject);
                return;
            }

            _currentIndex = 0;
            _targetIndex = 1;

            Transform trs = transform;
            _initialPosition = trs.position;
            _initialRotation = trs.eulerAngles;
            trs.SetPositionAndRotation(
                position: _points[0].Position + _initialPosition,
                rotation: Quaternion.Euler(_initialRotation + _points[0].Rotation));
        }

        private void Update()
        {
            if (_pauseTimer > 0f)
            {
                _pauseTimer -= Time.deltaTime;

                if (_pauseTimer <= 0f)
                {
                    _currentIndex = _targetIndex;
                    _targetIndex = _targetIndex == _points.Length - 1 ? 0 : _currentIndex + 1;
                    _interpolation = 0f;
                }
                return;
            }

            if (_interpolation < 1f)
            {
                _interpolation += Time.deltaTime;
                float t = Easer.Apply(_easingType, _interpolation);

                transform.SetPositionAndRotation(
                    position: Vector3.Lerp(GetPositionForTarget(_points[_currentIndex]), GetPositionForTarget(_points[_targetIndex]), t),
                    rotation: Quaternion.Lerp(GetRotationForTarget(_points[_currentIndex]), GetRotationForTarget(_points[_targetIndex]), t));
            }
            else
                _pauseTimer = _points[_currentIndex].Pause;
        }

        private Vector3 GetPositionForTarget(Target target) => target.Position + _initialPosition;
        private Quaternion GetRotationForTarget(Target target) => Quaternion.Euler(_initialRotation + target.Rotation);
        
        #region Internal Types
        [Serializable]
        private class Target
        {
            public Vector3 Position;
            public Vector3 Rotation;
            public float Pause;
            public float Duration;
        }
        #endregion

        #region Editor
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (_points == null || _points.Length == 0)
                return;

            if (!Application.isPlaying)
                _initialPosition = transform.position;

            DrawTargets();
        }

        private void DrawTargets()
        {
            for (int i = 0; i < _points.Length; i++)
            {
                Vector3 pointPosition = _points[i].Position + _initialPosition;

                Handles.color = i == 0 ? Color.blue : Color.green;
                Handles.CubeHandleCap(0, pointPosition, Quaternion.Euler(_points[i].Rotation), 0.1f, EventType.Repaint);

                Vector3 nextTarget = _initialPosition + _points[(int)Mathf.Repeat(i + 1, _points.Length)].Position;

                Gizmos.color = Color.red;
                Gizmos.DrawLine(pointPosition, nextTarget);

                string pointNumber;

                if (i == 0)
                    pointNumber = "1 (First)";
                else if (i == _points.Length - 1)
                    pointNumber = $"{i + 1} (Last)";
                else
                    pointNumber = $"{i + 1}";

                string pointLabel = $"Point: " + pointNumber + "\n" +
                                    $"Pause: {_points[i].Pause} sec\n" +
                                    $"Duration: {_points[i].Duration}";
                Handles.Label(pointPosition, pointLabel, EditorStyles.boldLabel);
            }
        }
#endif
        #endregion
    }
}