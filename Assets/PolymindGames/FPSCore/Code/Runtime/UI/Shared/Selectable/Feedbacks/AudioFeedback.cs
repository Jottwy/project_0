using JetBrains.Annotations;
using UnityEngine;
using System;

namespace PolymindGames.UserInterface
{
    [Serializable]
    [UsedImplicitly]
    public sealed class AudioFeedback : SelectableButtonFeedback
    {
        [SerializeField]
        private AudioData _highlightAudio = new(null);

        [SerializeField]
        private AudioData _selectedAudio = new(null);

        private SelectableButton _selectable;
        
        private static float _highlightPlayTimer = 0f;
        private static float _selectedPlayTimer = 0f;
        private const float PlayCooldown = 0.1f;

        public override void OnHighlighted(bool instant)
        {
            if (!instant && Mathf.Abs(_highlightPlayTimer - Time.time) > PlayCooldown)
            {
                AudioManager.Instance.PlayClip2D(_highlightAudio, AudioChannel.UI);
                _highlightPlayTimer = Time.time;
            }
        }

        public override void OnSelected(bool instant)
        {
            if (!instant && Mathf.Abs(_selectedPlayTimer - Time.time) > PlayCooldown)
            {
                AudioManager.Instance.PlayClip2D(_selectedAudio, AudioChannel.UI);
                _selectedPlayTimer = Time.time;
                _highlightPlayTimer = Time.time;
            }
        }

        public override void OnClicked(bool instant)
        {
            if (!_selectable.IsSelectable)
                OnSelected(false);
        }

        public override void Initialize(SelectableButton selectable)
        {
            _selectable = selectable;
        }
    }
}