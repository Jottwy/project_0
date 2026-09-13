using UnityEngine;
using UnityEngine.EventSystems;

namespace BackroomsSurvival.UI
{
    /// <summary>Franja de cabecera de una sección (D13): clic pliega, arrastrar reordena. Solo reenvía a su dueño.</summary>
    public sealed class BackroomsSectionHeader : MonoBehaviour, IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        [SerializeField] private BackroomsInventorySections _owner;
        [SerializeField] private string _id;

        public void OnPointerClick(PointerEventData eventData)
        {
            // Unity no manda clic si hubo arrastre, así que soltar tras reordenar no pliega.
            if (_owner != null && eventData.button == PointerEventData.InputButton.Left) _owner.Toggle(_id);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (_owner != null) _owner.BeginDrag(_id);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_owner != null) _owner.Drag(eventData);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (_owner != null) _owner.EndDrag();
        }
    }
}
