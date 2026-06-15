using UnityEngine.Events;
using UnityEngine;
using System;
using JetBrains.Annotations;

namespace PolymindGames.UserInterface
{
    [Serializable]
    [UsedImplicitly]
    public sealed class EventFeedback : SelectableButtonFeedback
    {   
        [Serializable]
        private struct EventEntry
        {
            public FeedbackState Trigger;
            [SpaceArea(3f)]
            public UnityEvent Event;
        }
        
        [SerializeField, IgnoreParent, ReorderableList]
        private EventEntry[] _events = Array.Empty<EventEntry>();

        public override void OnNormal(bool instant) => InvokeAllEventsOfType(FeedbackState.Normal);
        public override void OnHighlighted(bool instant) => InvokeAllEventsOfType(FeedbackState.Highlighted);
        public override void OnSelected(bool instant) => InvokeAllEventsOfType(FeedbackState.Selected);
        public override void OnDeselected(bool instant) => InvokeAllEventsOfType(FeedbackState.Deselected);
        public override void OnPressed(bool instant) => InvokeAllEventsOfType(FeedbackState.Pressed);
        public override void OnDisabled(bool instant) => InvokeAllEventsOfType(FeedbackState.Disabled);
        public override void OnClicked(bool instant) => InvokeAllEventsOfType(FeedbackState.Clicked);

        private void InvokeAllEventsOfType(FeedbackState type)
        {
            foreach (var eventEntry in _events)
            {
                if (eventEntry.Trigger == type)
                    eventEntry.Event.Invoke();
            }
        }
    }
}