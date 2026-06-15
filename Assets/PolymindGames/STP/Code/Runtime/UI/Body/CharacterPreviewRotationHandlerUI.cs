using UnityEngine.EventSystems;
using UnityEngine;
using System.Linq;

namespace PolymindGames.UserInterface
{
    public sealed class CharacterPreviewRotationHandlerUI : MonoBehaviour
    {
        [SerializeField]
        private EventTrigger _input;
        
        [SerializeField, NotNull]
        [Tooltip("The root transform for position adjustments.")]
        private Transform _positionRoot;

        [SerializeField, NotNull]
        [Tooltip("The root transform for rotation adjustments.")]
        private Transform _rotationRoot;

        [SerializeField, Range(0.01f, 10f), SpaceArea]
        [Tooltip("The rotation speed around the Y axis.")]
        private float _yRotationSpeed = 0.2f;

        [SerializeField]
        [Tooltip("Whether to invert the Y rotation direction.")]
        private bool _invertYDirection = true;

        [SerializeField, Range(0.01f, 10f), SpaceArea]
        [Tooltip("The rotation speed around the X axis.")]
        private float _xRotationSpeed = 0.01f;

        [SerializeField]
        [Tooltip("Whether to invert the X rotation direction.")]
        private bool _invertXDirection = true;

        [SerializeField, Range(0f, 180f)]
        [Tooltip("The maximum rotation around the X axis.")]
        private float _maxXRotation = 1f;

        [SerializeField, Range(-1f, 1f), SpaceArea]
        [Tooltip("The speed at which the camera moves.")]
        private float _cameraMoveInterval = -0.25f;

        [SerializeField, Range(-100f, 100f)]
        [Tooltip("The minimum distance of the camera.")]
        private float _minCameraDistance = 0.5f;

        [SerializeField, Range(-100f, 100f)]
        [Tooltip("The maximum distance of the camera.")]
        private float _maxCameraDistance = 1.75f;

        private Vector3 _rootEulerAngles;
        private float _cameraOffset;

        private void Start()
        {
            _rootEulerAngles = _rotationRoot.localEulerAngles;
            _cameraOffset = _minCameraDistance;
            Vector3 localPosition = _positionRoot.localPosition;
            _positionRoot.localPosition = new Vector3(localPosition.x, localPosition.y, -_cameraOffset);
            
            InitializeEvents();
        }
        
        private void InitializeEvents()
        {
            var onDragEvent = _input.triggers.FirstOrDefault(entry => entry.eventID == EventTriggerType.Drag);
            var onScrollEvent = _input.triggers.FirstOrDefault(entry => entry.eventID == EventTriggerType.Scroll);
            onDragEvent?.callback.AddListener(OnDrag);
            onScrollEvent?.callback.AddListener(OnScroll);
        }

        private void OnDrag(BaseEventData eventData)
        {
            if (eventData is PointerEventData pointerData)
            {
                _rootEulerAngles.y += pointerData.delta.x * _yRotationSpeed * (_invertYDirection ? -1f : 1f);
                _rootEulerAngles.x += pointerData.delta.y * _xRotationSpeed * (_invertXDirection ? -1f : 1f);
                _rootEulerAngles.x = Mathf.Clamp(_rootEulerAngles.x, -_maxXRotation, _maxXRotation);
                _rotationRoot.localRotation = Quaternion.Euler(_rootEulerAngles);
            }
        }

        private void OnScroll(BaseEventData eventData)
        {
            if (eventData is PointerEventData pointerData)
            {
                float mod = _maxCameraDistance - _minCameraDistance;
                
                _cameraOffset += Mathf.Clamp(pointerData.scrollDelta.y, -1f, 1f) * _cameraMoveInterval / mod;
                _cameraOffset = Mathf.Clamp(_cameraOffset, _minCameraDistance, _maxCameraDistance);
                _positionRoot.localPosition = _positionRoot.localPosition.WithZ(-_cameraOffset);
            }
        }
    }
}