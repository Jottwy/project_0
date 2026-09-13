using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Cinta de un slot de equipo que enfoca su zona en la vista previa (<see cref="BackroomsPreviewZoom"/>). Clic en
    /// la CINTA, no en el objeto: el hueco sigue siendo del vendor. La zona activa lleva la cinta roja.
    /// </summary>
    [RequireComponent(typeof(Image))]
    public sealed class BackroomsZoneHeader : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField]
        private string _zone;

        [SerializeField]
        private BackroomsPreviewZoom _zoom;

        [SerializeField]
        private Sprite _idleSprite;

        [SerializeField]
        private Sprite _activeSprite;

        private Image _image;
        private bool _active;
        private bool _hover;

        public string Zone => _zone;

        private void Awake() => _image = GetComponent<Image>();

        private void OnEnable()
        {
            if (_zoom != null) _zoom.Register(this);
            Refresh();
        }

        private void OnDisable()
        {
            if (_zoom != null) _zoom.Unregister(this);
            _hover = false;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left && _zoom != null) _zoom.Toggle(_zone);
        }

        public void OnPointerEnter(PointerEventData eventData) { _hover = true; Refresh(); }

        public void OnPointerExit(PointerEventData eventData) { _hover = false; Refresh(); }

        public void SetActive(bool active)
        {
            _active = active;
            Refresh();
        }

        private void Refresh()
        {
            if (_image == null) return;
            if (_active && _activeSprite != null) _image.sprite = _activeSprite;
            else if (_idleSprite != null) _image.sprite = _idleSprite;
            // Al pasar por encima, un aviso leve de que la cinta se puede pulsar.
            transform.localScale = _hover && !_active ? new Vector3(1.06f, 1.06f, 1f) : Vector3.one;
        }
    }
}
