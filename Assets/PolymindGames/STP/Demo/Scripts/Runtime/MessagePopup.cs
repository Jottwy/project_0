using PolymindGames.ProceduralMotion;
using UnityEngine;

namespace PolymindGames.Demo
{
    public sealed class MessagePopup : MonoBehaviour
    {
        [SerializeField]
        private Color _messageSeenIconColor;

        [SerializeField, NotNull]
        private Transform _textRoot;
        
        [SerializeField, NotNull]
        private SpriteRenderer _messageSprite;

        [SerializeField, NotNull]
        private SpriteRenderer _backgroundSprite;

        [SerializeField, Range(0f, 5f)]
        private float _animationDuration = 0.35f;

        [SerializeField]
        private EaseType _animationEaseType = EaseType.SineInOut;

        [SerializeField, Title("Audio")]
        private AudioData _proximityEnterAudio = new(null);

        [SerializeField]
        private AudioData _proximityExitAudio = new(null);

        private bool _isSeen;

        private void OnTriggerEnter(Collider other)
        {
            if (other.gameObject.CompareTag(TagConstants.Player))
            {
                _messageSprite.TweenSpriteRendererAlpha(0f, 0.5f)
                    .AutoReleaseWithParent(true)
                    .SetEasing(_animationEaseType);
                
                _backgroundSprite.TweenSpriteRendererAlpha(1f, 0.5f)
                    .AutoReleaseWithParent(true)
                    .SetEasing(_animationEaseType);
                
                _textRoot.TweenLocalScale(Vector3.one, _animationDuration)
                    .AutoReleaseWithParent(true)
                    .SetEasing(_animationEaseType);
                
                AudioManager.Instance.PlayClip3D(_proximityEnterAudio, _textRoot.position);
                
                _isSeen = true;
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (other.gameObject.CompareTag(TagConstants.Player))
            {
                if (_isSeen)
                    _messageSprite.color = new Color(_messageSeenIconColor.r, _messageSeenIconColor.g, _messageSeenIconColor.b, _messageSprite.color.a);

                _messageSprite.TweenSpriteRendererAlpha(1f, 0.5f)
                    .AutoReleaseWithParent(true)
                    .SetEasing(_animationEaseType);
                
                _backgroundSprite.TweenSpriteRendererAlpha(0f, 0.5f)
                    .AutoReleaseWithParent(true)
                    .SetEasing(_animationEaseType);

                _textRoot.TweenLocalScale(Vector3.zero, _animationDuration)
                    .AutoReleaseWithParent(true)
                    .SetEasing(_animationEaseType);
                
                AudioManager.Instance.PlayClip3D(_proximityExitAudio, _textRoot.position);
                
                _isSeen = true;
            }
        }
        
        private void Awake()
        {
            _backgroundSprite.color = _backgroundSprite.color.WithAlpha(0f);
            _messageSprite.color = _messageSprite.color.WithAlpha(1f);
            _textRoot.localScale = Vector3.zero;
        }

        private void OnDestroy()
        {
            if (_isSeen)
            {
                _messageSprite.ClearTweens();
                _backgroundSprite.ClearTweens();
                _textRoot.ClearTweens();
            }
        }
    }
}