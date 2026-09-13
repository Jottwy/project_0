using System.Collections.Generic;
using System.Reflection;
using PolymindGames.UserInterface;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Zoom por zona del cuerpo en la vista previa del personaje (INVENTORY-ROADMAP, idea de Joel del 2026-09-13):
    /// clic en la cinta de un slot de equipo y la cámara del preview del vendor gira y cierra el campo de visión
    /// hacia ese hueso con una curva corta. Las zonas de espalda dan además la vuelta al muñeco. Clic otra vez en la
    /// misma cinta, o un clic sin arrastrar sobre el muñeco, vuelve al cuerpo entero y de frente; al cerrar el
    /// inventario vuelve de golpe. Base para Heridas y sastrería.
    ///
    /// La cámara (rotación y fov) es nuestra. El giro del muñeco es del <c>CharacterPreviewRotationHandlerUI</c> del
    /// vendor, que guarda sus ángulos en privado: se escriben los dos por reflexión para que arrastrar siga desde
    /// donde quedó, y si el jugador arrastra a mitad de la vuelta, la vuelta se cancela y manda él.
    /// </summary>
    public sealed class BackroomsPreviewZoom : MonoBehaviour, IPointerClickHandler
    {
        public readonly struct Zone
        {
            public readonly string Id;
            public readonly string[] Bones;
            public readonly float Facing, Lift, Span;

            public Zone(string id, float facing, float lift, float span, params string[] bones)
            {
                Id = id; Facing = facing; Lift = lift; Span = span; Bones = bones;
            }
        }

        // Span = metros de alto que se ven; el render llena la caja Personaje, que es aproximadamente la mitad de ancha
        // que de alta. Facing = grados que gira el muñeco respecto a su frente.
        public static readonly Zone[] Zones =
        {
            new Zone("Head", 0f, 0.06f, 0.6f, "Head"),
            new Zone("Torso", 0f, -0.05f, 0.9f, "UpperSpine"),
            new Zone("Back", 180f, 0f, 0.9f, "MiddleSpine"),
            new Zone("Waist", 0f, 0f, 0.8f, "Pelvis"),
            new Zone("Legs", 0f, 0.05f, 1.1f, "LowerLeg.L", "LowerLeg.R"),
            new Zone("Feet", 0f, 0.1f, 0.65f, "Foot.L", "Foot.R"),
            new Zone("Hands", 0f, 0f, 1.2f, "Hand.L", "Hand.R"),
        };

        private static readonly FieldInfo EulerField =
            typeof(CharacterPreviewRotationHandlerUI).GetField("_rootEulerAngles", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RotationRootField =
            typeof(CharacterPreviewRotationHandlerUI).GetField("_rotationRoot", BindingFlags.NonPublic | BindingFlags.Instance);

        [SerializeField]
        private Camera _camera;

        [SerializeField]
        private GameObject _characterVisuals;

        [SerializeField]
        private CharacterPreviewRotationHandlerUI _rotationHandler;

        [SerializeField, Range(1f, 30f)]
        private float _sharpness = 8f;

        private readonly Dictionary<string, Transform> _bones = new();
        private readonly List<BackroomsZoneHeader> _headers = new();
        private Quaternion _defaultRotation;
        private float _defaultFov;
        private int _active = -1;

        private Transform _rotationRoot;
        private float _frontYaw;
        private bool _yawDrive;
        private float _yawTarget;
        private Vector3? _written;

        public string ActiveZone => _active >= 0 ? Zones[_active].Id : null;

        public static int IndexOf(string zoneId)
        {
            for (int i = 0; i < Zones.Length; i++)
                if (Zones[i].Id == zoneId) return i;
            return -1;
        }

        /// <summary>Campo de visión vertical que encuadra <paramref name="span"/> metros a <paramref name="distance"/>.</summary>
        public static float FovForSpan(float span, float distance)
            => 2f * Mathf.Atan(span * 0.5f / Mathf.Max(distance, 0.0001f)) * Mathf.Rad2Deg;

        public void Register(BackroomsZoneHeader header)
        {
            if (!_headers.Contains(header)) _headers.Add(header);
            header.SetActive(header.Zone == ActiveZone);
        }

        public void Unregister(BackroomsZoneHeader header) => _headers.Remove(header);

        public void Toggle(string zoneId)
        {
            int i = IndexOf(zoneId);
            Select(i == _active ? -1 : i);
        }

        public void Clear() => Select(-1);

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left && !eventData.dragging) Clear();
        }

        private void Awake()
        {
            if (_camera != null)
            {
                _defaultRotation = _camera.transform.localRotation;
                _defaultFov = _camera.fieldOfView;
            }
            if (_rotationHandler != null && EulerField != null && RotationRootField != null)
            {
                _rotationRoot = RotationRootField.GetValue(_rotationHandler) as Transform;
                if (_rotationRoot != null) _frontYaw = _rotationRoot.localEulerAngles.y;
            }
        }

        private void OnDisable() => Snap();

        private void Select(int index)
        {
            _active = index;
            foreach (var header in _headers)
                if (header != null) header.SetActive(header.Zone == ActiveZone);

            if (_rotationRoot == null) return;
            _yawTarget = _frontYaw + (index >= 0 ? Zones[index].Facing : 0f);
            _yawDrive = true;
            _written = null;
        }

        private void Snap()
        {
            if (_camera == null) return;
            if (_active >= 0) Select(-1);
            _camera.transform.localRotation = _defaultRotation;
            _camera.fieldOfView = _defaultFov;
            _yawDrive = false;
            if (_rotationRoot != null)
            {
                var euler = (Vector3)EulerField.GetValue(_rotationHandler);
                if (Mathf.Abs(Mathf.DeltaAngle(euler.y, _frontYaw)) > 0.01f)
                {
                    euler.y = _frontYaw;
                    WriteEuler(euler);
                }
            }
        }

        private void LateUpdate()
        {
            if (_camera == null) return;
            if (_characterVisuals != null && !_characterVisuals.activeInHierarchy)
            {
                if (_active >= 0 || _yawDrive || _camera.fieldOfView != _defaultFov) Snap();
                return;
            }

            float t = 1f - Mathf.Exp(-_sharpness * Time.unscaledDeltaTime);
            DriveYaw(t);

            var cam = _camera.transform;
            var targetRotation = _defaultRotation;
            float targetFov = _defaultFov;
            if (_active >= 0 && TryGetAim(Zones[_active], out var point))
            {
                float scale = Mathf.Max(cam.lossyScale.y, 0.0001f);
                point += Vector3.up * (Zones[_active].Lift * scale);
                var direction = point - cam.position;
                var parent = cam.parent;
                var world = Quaternion.LookRotation(direction, parent != null ? parent.up : Vector3.up);
                targetRotation = parent != null ? Quaternion.Inverse(parent.rotation) * world : world;
                targetFov = Mathf.Min(_defaultFov, FovForSpan(Zones[_active].Span * scale, direction.magnitude));
            }

            cam.localRotation = Quaternion.Slerp(cam.localRotation, targetRotation, t);
            _camera.fieldOfView = Mathf.Lerp(_camera.fieldOfView, targetFov, t);
        }

        private void DriveYaw(float t)
        {
            if (!_yawDrive || _rotationRoot == null) return;
            var euler = (Vector3)EulerField.GetValue(_rotationHandler);
            // El jugador ha arrastrado a mitad de la vuelta: manda él.
            if (_written.HasValue && euler != _written.Value)
            {
                _yawDrive = false;
                return;
            }
            float yaw = Mathf.LerpAngle(euler.y, _yawTarget, t);
            if (Mathf.Abs(Mathf.DeltaAngle(yaw, _yawTarget)) < 0.2f)
            {
                yaw = _yawTarget;
                _yawDrive = false;
            }
            euler.y = yaw;
            WriteEuler(euler);
        }

        private void WriteEuler(Vector3 euler)
        {
            EulerField.SetValue(_rotationHandler, euler);
            _rotationRoot.localRotation = Quaternion.Euler(euler);
            _written = euler;
        }

        private bool TryGetAim(Zone zone, out Vector3 point)
        {
            point = Vector3.zero;
            if (_bones.Count == 0 && _characterVisuals != null)
                foreach (var bone in _characterVisuals.GetComponentsInChildren<Transform>(true))
                    _bones.TryAdd(bone.name, bone);

            int found = 0;
            foreach (var name in zone.Bones)
            {
                if (!_bones.TryGetValue(name, out var bone) || bone == null) continue;
                point += bone.position;
                found++;
            }
            if (found == 0) return false;
            point /= found;
            return true;
        }
    }
}
