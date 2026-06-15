using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    public interface IHoverable : IMonoBehaviour
    {
        string Title { get; set; }
        string Description { get; set; }
        Vector3 CenterOffset { get; }

        event UnityAction DescriptionChanged;
        event HoverEventHandler HoverStarted;
        event HoverEventHandler HoverEnded;

        /// <summary>
        /// Called when a character starts looking at this object.
        /// </summary>
        void OnHoverStart(ICharacter character);

        /// <summary>
        /// Called when a character stops looking at this object.
        /// </summary>
        void OnHoverEnd(ICharacter character);
    }
    
    public delegate void HoverEventHandler(IHoverable interactable, ICharacter character);
}