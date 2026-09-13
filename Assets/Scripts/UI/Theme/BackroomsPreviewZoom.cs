using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Zoom por zona del cuerpo en la vista previa del personaje (INVENTORY-ROADMAP, idea de Joel del 2026-09-13):
    /// clic en la cinta de un slot de equipo y la cámara del preview del vendor gira y cierra el campo de visión
    /// hacia ese hueso con una curva corta. Clic otra vez en la misma cinta, o un clic sin arrastrar sobre el
    /// muñeco, vuelve al cuerpo entero; al cerrar el inventario vuelve de golpe. Base para Heridas y sastrería.
    ///
    /// Solo mueve la CÁMARA (rotación y fov): posición y giro del muñeco siguen siendo del
    /// <c>CharacterPreviewRotationHandlerUI</c> del vendor, así que arrastrar y la rueda conviven con el zoom.
    /// </summary>
    public sealed class BackroomsPreviewZoom : MonoBehaviour, IPointerClickHandler
    {
        public readonly struct Zone
        {
            public readonly string Id;
            public readonly string[] Bones;
            public readonly float Lift, Span;

            public Zone(string id, float lift, float span, params string[] bones)
            {
                Id = id; Lift = lift; Span = span; Bones = bones;
            }
        }

        // Span = metros de alto que se ven. El hueco del preview enseña un tercio del ancho del render, así que el
        // ancho visible es Span / 3: por eso el torso no se cierra tanto como la cabeza.
        public static readonly Zone[] Zones =
        {
            new Zone("Head", 0.08f, 0.7f, "Head"),
            new Zone("Torso", -0.05f, 1.1f, "UpperSpine"),
            new Zone("Back", 0f, 1.1f, "MiddleSpine"),
            new Zone("Waist", 0.02f, 0.9f, "Pelvis"),
            new Zone("Legs", 0.05f, 1.1f, "LowerLeg.L", "LowerLeg.R"),
            new Zone("Feet", -0.04f, 0.7f, "Foot.L", "Foot.R"),
            new Zone("Hands", 0f, 1.2f, "Hand.L", "Hand.R"),
        };

        [SerializeField]
        private Camera _camera;

        [SerializeField]
        private GameObject _characterVisuals;

        [SerializeField, Range(1f, 30f)]
        private float _sharpness = 8f;

        private readonly Dictionary<string, Transform> _bones = new();
        private readonly List<BackroomsZoneHeader> _headers = new();
        private Quaternion _defaultRotation;
        private float _defaultFov;
        private int _active = -1;

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
            if (_camera == null) return;
            _defaultRotation = _camera.transform.localRotation;
            _defaultFov = _camera.fieldOfView;
        }

        private void OnDisable() => Snap();

        private void Select(int index)
        {
            _active = index;
            foreach (var header in _headers)
                if (header != null) header.SetActive(header.Zone == ActiveZone);
        }

        private void Snap()
        {
            if (_camera == null) return;
            if (_active >= 0) Select(-1);
            _camera.transform.localRotation = _defaultRotation;
            _camera.fieldOfView = _defaultFov;
        }

        private void LateUpdate()
        {
            if (_camera == null) return;
            if (_characterVisuals != null && !_characterVisuals.activeInHierarchy)
            {
                if (_active >= 0 || _camera.fieldOfView != _defaultFov) Snap();
                return;
            }

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

            float t = 1f - Mathf.Exp(-_sharpness * Time.unscaledDeltaTime);
            cam.localRotation = Quaternion.Slerp(cam.localRotation, targetRotation, t);
            _camera.fieldOfView = Mathf.Lerp(_camera.fieldOfView, targetFov, t);
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
