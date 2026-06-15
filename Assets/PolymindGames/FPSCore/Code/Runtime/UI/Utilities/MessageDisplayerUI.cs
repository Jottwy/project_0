using PolymindGames.ProceduralMotion;
using UnityEngine.UI;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    public sealed class MessageDisplayerUI : MonoBehaviour, IMessageListener
    {
        [SerializeField]
        private GameObject _messageTemplate;

        [SerializeField, Range(1, 30)]
        private int _templatesCount = 8;

        [SerializeField, Range(0f, 10f), Title("Fading")]
        private float _fadeDelay = 2f;

        [SerializeField, Range(0f, 10f)]
        private float _fadeDuration = 1f;

        [SerializeField, Title("Colors")]
        private Color _infoColor;

        [SerializeField]
        private Color _warningColor;

        [SerializeField]
        private Color _errorColor;

        private MessageTemplateData[] _messageTemplates;
        private int _currentIndex = -1;

        void IMessageListener.OnMessageReceived(ICharacter character, in MessageArgs args)
        {
            Color color = GetColorForMessageType(args.Type);
            PushMessage(args.Message, color, args.Sprite);
        }

        private void Awake()
        {
            _messageTemplates = new MessageTemplateData[_templatesCount];
            for (int i = 0; i < _templatesCount; i++)
                _messageTemplates[i] = new MessageTemplateData(_messageTemplate, transform);
        }

        private void OnEnable() => MessageDispatcher.Instance.AddListener(this);
        private void OnDisable() => MessageDispatcher.Instance.RemoveListener(this);

        private void PushMessage(string message, Color color, Sprite sprite)
        {
            var template = GetMessageTemplate();

            template.Root.SetActive(true);
            template.Root.transform.SetAsLastSibling();

            template.Text.text = message.ToUpper();
            template.Text.color = new Color(color.r, color.g, color.b, 1f);

            template.IconImg.gameObject.SetActive(sprite != null);
            template.IconImg.sprite = sprite;

            template.CanvasGroup.alpha = color.a;
            template.CanvasGroup.TweenCanvasGroupAlpha(0f, _fadeDuration)
                .SetDelay(_fadeDelay).AutoReleaseWithParent(true);
        }

        private MessageTemplateData GetMessageTemplate() =>
            _messageTemplates[(int)Mathf.Repeat(++_currentIndex, _templatesCount)];
        
        private Color GetColorForMessageType(MsgType type) => type switch
        {
            MsgType.Info => _infoColor,
            MsgType.Warning => _warningColor,
            MsgType.Error => _errorColor,
            _ => Color.black
        };

		#region Internal Types
        private sealed class MessageTemplateData
        {
            public readonly CanvasGroup CanvasGroup;
            public readonly Image IconImg;
            public readonly GameObject Root;
            public readonly TextMeshProUGUI Text;

            public MessageTemplateData(GameObject objectTemplate, Transform spawnRoot)
            {
                GameObject instance = Instantiate(objectTemplate, spawnRoot);
                Root = instance;
                Text = instance.GetComponentInChildren<TextMeshProUGUI>();
                IconImg = instance.transform.Find("Icon").GetComponent<Image>();
                CanvasGroup = instance.GetComponentInChildren<CanvasGroup>();
                CanvasGroup.alpha = 0f;
            }
        }
        #endregion
    }
}