using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Polymind Games/Motion/Lean Motion")]
    public sealed class LeanMotion : MotionBehaviour
    {
        [SerializeField, NotNull]
        [Title("References")]
        private AdditiveForceMotion _additiveForce;

        [SerializeField, Title("Interpolation")]
        [Tooltip("Settings for rotation spring.")]
        private SpringSettings _rotationSpring = SpringSettings.Default;

        [SerializeField]
        [Tooltip("Settings for position spring.")]
        private SpringSettings _positionSpring = SpringSettings.Default;

        [SerializeField, Title("Forces")]
        [Tooltip("Force applied on position when entering lean motion.")]
        private SpringForce3D _positionEnterForce = SpringForce3D.Default;

        [SerializeField]
        [Tooltip("Force applied on rotation when entering lean motion.")]
        private SpringForce3D _rotationEnterForce = SpringForce3D.Default;

        [SerializeField, Range(-90, 90f), Title("Offsets")]
        [Tooltip("Angle at which the object will lean.")]
        private float _leanAngle = 13f;

        [SerializeField, Range(-5f, 5f)]
        [Tooltip("Side offset applied during leaning.")]
        private float _leanSideOffset = 0.35f;

        [SerializeField, Range(0f, 5f)]
        [Tooltip("Height offset applied during leaning.")]
        private float _leanHeightOffset = 0.2f;

        private BodyLeanState _leanState;
        private float _maxLeanPercent = 1f;
        
        public float LeanAngle => _leanAngle;
        public float LeanSideOffset => _leanSideOffset;
        public float LeanHeightOffset => _leanHeightOffset;

        public float MaxLeanPercent
        {
            get => _maxLeanPercent;
            set
            {
                float leanPercent = Mathf.Clamp01(value);
                _maxLeanPercent = leanPercent;
            }
        }

        public void SetLeanState(BodyLeanState leanState)
        {
            float forceFactor = (leanState == BodyLeanState.Center ? 0.5f : 1f) * _maxLeanPercent;

            _additiveForce.SetCustomSpringSettings(_rotationSpring, _positionSpring);
            _additiveForce.AddPositionForce(_positionEnterForce, forceFactor, SpringType.Custom);
            _additiveForce.AddRotationForce(_rotationEnterForce, forceFactor, SpringType.Custom);

            _leanState = leanState;
            UpdateTargetOffsets();
        }

        protected override void Awake()
        {
            base.Awake();
            IgnoreParentMultiplier = true;
        }
        
        protected override SpringSettings GetDefaultPositionSpringSettings() => _positionSpring;
        protected override SpringSettings GetDefaultRotationSpringSettings() => _rotationSpring;

        public override void UpdateMotion(float deltaTime)
        {
            if (_leanState != BodyLeanState.Center)
                UpdateTargetOffsets();
        }

        private void UpdateTargetOffsets()
        {
            Vector3 targetPos;
            Vector3 targetRot;
            
            switch (_leanState)
            {
                case BodyLeanState.Left:
                    targetPos = new Vector3(-_leanSideOffset * _maxLeanPercent, -_leanHeightOffset * _maxLeanPercent, 0f);
                    targetRot = new Vector3(0f, 0f, _leanAngle * _maxLeanPercent);
                    break;
                case BodyLeanState.Right:
                    targetPos = new Vector3(_leanSideOffset * _maxLeanPercent, -_leanHeightOffset * _maxLeanPercent, 0f);
                    targetRot = new Vector3(0f, 0f, -_leanAngle * _maxLeanPercent);
                    break;
                case BodyLeanState.Center:
                    targetPos = Vector3.zero;
                    targetRot = Vector3.zero;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            
            SetTargetPosition(targetPos);
            SetTargetRotation(targetRot);
        }

        #region Editor
#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            if (_additiveForce == null)
                _additiveForce = GetComponent<AdditiveForceMotion>();
        }
#endif
        #endregion
    }
}