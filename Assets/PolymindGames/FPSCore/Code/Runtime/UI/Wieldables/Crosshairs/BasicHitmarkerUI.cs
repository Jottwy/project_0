using UnityEngine.UI;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    public sealed class BasicHitmarkerUI : HitmarkerBehaviourUI
    {
        [SerializeField, Range(0f, 1f)]
        [Tooltip("Duration of the hitmarker display.")]
        private float _duration = 0.5f;

        [SerializeField, Range(0f, 180f)]
        [Tooltip("Random rotation applied to the hitmarker.")]
        private float _randomRotation = 5f;

        [SerializeField, Range(0f, 100f)]
        [Tooltip("Rotation update speed for the hitmarker.")]
        private float _rotationUpdateSpeed = 25f;

        [SerializeField, Range(0f, 180f)]
        [Tooltip("Effect of accuracy size on hitmarker.")]
        private float _accuracySizeEffect = 5f;

        [SerializeField, Range(0f, 1000f), Title("Size")]
        [Tooltip("Size of the hitmarker for normal hits.")]
        private float _normalSize = 40f;

        [SerializeField, Range(0f, 1000f)]
        [Tooltip("Size of the hitmarker for critical hits.")]
        private float _criticalSize = 40f;

        [SerializeField, Range(0f, 1000f)]
        [Tooltip("Size of the hitmarker for fatal hits.")]
        private float _fatalSize = 40f;

        [SerializeField, Title("Animation")]
        [Tooltip("Animation curve for hitmarker size.")]
        private AnimationCurve _sizeAnimation;

        [SerializeField]
        [Tooltip("Animation curve for hitmarker alpha.")]
        private AnimationCurve _alphaAnimation;

        [SerializeField, Title("Colors")]
        [Tooltip("Color of the hitmarker for normal hits.")]
        private Color _normalColor;

        [SerializeField]
        [Tooltip("Color of the hitmarker for critical hits.")]
        private Color _criticalColor;

        [SerializeField]
        [Tooltip("Color of the hitmarker for fatal hits.")]
        private Color _fatalColor;

        [SerializeField, Title("Audio")]
        [Tooltip("Audio played for normal hits.")]
        private AudioData _normalAudio;

        [SerializeField]
        [Tooltip("Audio played for critical hits.")]
        private AudioData _criticalAudio;

        [SerializeField]
        [Tooltip("Audio played for fatal hits.")]
        private AudioData _fatalAudio;

        private RectTransform _rectTransform;
        private Image[] _hitmarkImages;
        private float _targetZRotation;
        private Color _targetColor;
        private float _audioTimer;
        private float _targetSize;
        private float _zRotation = 45f;
        private float _endTime;

        private const float AudioCooldown = 0.3f;
        
        public override bool IsActive => Time.time < _endTime;

        public override void StartAnimation(DamageResult damageResult)
        {
            float time = Time.time;

            if (time > _audioTimer)
            {
                PlayAudioFor(damageResult);
                _audioTimer = time + AudioCooldown;
            }

            _targetColor = GetColorFor(damageResult);
            _targetSize = GetSizeFor(damageResult);
            _targetZRotation = Random.Range(-_randomRotation, _randomRotation) + 45f;
            _endTime = time + _duration;

            UpdateAnimation(1f);
        }

        public override void UpdateAnimation(float accuracy)
        {
            float t = 1 - (_endTime - Time.time) / _duration;

            _zRotation = Mathf.MoveTowards(_zRotation, Mathf.Lerp(_targetZRotation, 45f, t), Time.deltaTime * _rotationUpdateSpeed);

            _rectTransform.sizeDelta = Vector2.one * (_sizeAnimation.Evaluate(t) * _targetSize + _accuracySizeEffect * (1 - accuracy));
            _rectTransform.eulerAngles = new Vector3(0f, 0f, _zRotation);

            foreach (var img in _hitmarkImages)
                img.color =  new Color(_targetColor.r, _targetColor.g, _targetColor.b, _alphaAnimation.Evaluate(t));
        }

        private float GetSizeFor(DamageResult damageResult)
        {
            return damageResult switch
            {
                DamageResult.Normal => _normalSize,
                DamageResult.Critical => _criticalSize,
                DamageResult.Fatal => _fatalSize,
                _ => 1f
            };
        }

        private Color GetColorFor(DamageResult damageResult)
        {
            return damageResult switch
            {
                DamageResult.Normal => _normalColor,
                DamageResult.Critical => _criticalColor,
                DamageResult.Fatal => _fatalColor,
                _ => default(Color)
            };
        }

        private void PlayAudioFor(DamageResult damageResult)
        {
            switch (damageResult)
            {
                case DamageResult.Normal:
                    AudioManager.Instance.PlayClip2D(_normalAudio, 1f, AudioChannel.UI);
                    break;
                case DamageResult.Critical:
                    AudioManager.Instance.PlayClip2D(_criticalAudio, 1f ,AudioChannel.UI);
                    break;
                case DamageResult.Fatal:
                    AudioManager.Instance.PlayClip2D(_fatalAudio, 1f, AudioChannel.UI);
                    break;
            }
        }

        private void Awake()
        {
            _rectTransform = GetComponent<RectTransform>();
            _hitmarkImages = GetComponentsInChildren<Image>();
        }
    }
}