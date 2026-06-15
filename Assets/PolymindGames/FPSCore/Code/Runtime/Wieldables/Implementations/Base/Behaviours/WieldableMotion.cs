using PolymindGames.ProceduralMotion;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Controls the motion behavior of a wieldable object.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault1)]
    [AddComponentMenu("Polymind Games/Wieldables/Behaviours/Wieldable Motion")]
    public sealed class WieldableMotion : MonoBehaviour, IWieldableMotion
    {
        [SerializeField, InLineEditor]
        [Tooltip("The target transform for the motion.")]
        private Transform _targetTransform;

        [SerializeField, Title("Offset")]
        [Tooltip("The offset of the pivot.")]
        private Vector3 _pivotOffset;

        [SerializeField]
        [Tooltip("The offset of the position.")]
        private Vector3 _positionOffset;

        [SerializeField]
        [Tooltip("The offset of the rotation.")]
        private Vector3 _rotationOffset;

        [SerializeField, Title("Data")]
        [Tooltip("The motion profile data.")]
        private MotionProfile _motionProfile;

        private IFPSCharacter _character;

        /// <inheritdoc/>>
        public MotionComponents HeadComponents => _character.HeadComponents;

        /// <inheritdoc/>>
        public MotionComponents HandsComponents => _character.HandsComponents;

        /// <summary>
        /// Gets or sets the position offset of the wieldable motion.
        /// </summary>
        public Vector3 PositionOffset
        {
            get => _positionOffset;
            set
            {
                _positionOffset = value;
                
                if (gameObject.activeInHierarchy)
                    HandsComponents.Mixer.ResetMixer(_targetTransform, _pivotOffset, value, _rotationOffset);
            }
        }
        
        /// <summary>
        /// Gets or sets the rotation offset of the wieldable motion.
        /// </summary>
        public Vector3 RotationOffset
        {
            get => _rotationOffset;
            set
            {
                _rotationOffset = value;
                
                if (gameObject.activeInHierarchy)
                    HandsComponents.Mixer.ResetMixer(_targetTransform, _pivotOffset, _positionOffset, value);
            }
        }

        /// <summary>
        /// Sets the motion profile of the wieldable motion.
        /// </summary>
        /// <param name="profile">The motion profile to set.</param>
        public void SetProfile(MotionProfile profile)
        {
            if (_motionProfile == profile)
                return;
            
            if (gameObject.activeInHierarchy)
            {
                HandsComponents.Data.PopProfile(_motionProfile);
                HandsComponents.Data.PushProfile(profile);
            }
            
            _motionProfile = profile;
        }

        private void Awake()
        {
            var wieldable = GetComponentInParent<IWieldable>();
            _character = wieldable.Character as IFPSCharacter;

            if (_character == null)
                Debug.LogError("This behaviour requires n wieldable with an FPS-Character parent.", gameObject);
        }

        private void OnEnable()
        {
            HandsComponents.Mixer.ResetMixer(_targetTransform, _pivotOffset, _positionOffset, _rotationOffset);
            HandsComponents.Data.PushProfile(_motionProfile);
        }

        private void OnDisable()
        {
            HandsComponents.Data.PopProfile(_motionProfile);
        }

        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_targetTransform == null)
                _targetTransform = transform;

            if (_character != null)
                OnEnable();
        }
        
        private void OnDrawGizmosSelected()
        {
            Color pivotColor = new(0.1f, 1f, 0.1f, 0.5f);
            const float PivotRadius = 0.08f;

            var prevColor = UnityEditor.Handles.color;
            UnityEditor.Handles.color = pivotColor;
            UnityEditor.Handles.SphereHandleCap(0, transform.TransformPoint(_pivotOffset), Quaternion.identity, PivotRadius, EventType.Repaint);
            UnityEditor.Handles.color = prevColor;
        }
#endif
        #endregion
    }
}