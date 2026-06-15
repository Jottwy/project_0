using System.Collections;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    public sealed class DamageIndicatorUI : CharacterUIBehaviour
    {
        [SerializeField, Range(0f, 1024)]
        [Tooltip("Damage indicator distance (in pixels) from the screen center.")]
        private int _indicatorDistance = 128;
        
        [SerializeField, Range(0f, 100f)]
        [Tooltip("How much damage does the player have to take for the damage indicator effect to show. ")]
        private float _damageThreshold = 1f;
        
        [SerializeField, SpaceArea, SubGroup]
        [Tooltip("Image fading settings for the directional damage indicator.")]
        private ImageFaderUI _fadeSettings;
        
        private RectTransform _damageIndicatorRT;
        private Coroutine _indicatorRoutine;
        private Vector3 _lastHitPoint;

        protected override void Awake()
        {
            base.Awake();
            _damageIndicatorRT = _fadeSettings.Image.rectTransform;
        }

        protected override void OnCharacterAttached(ICharacter character)
        {
            character.HealthManager.DamageReceived += OnTakeDamage;
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            character.HealthManager.DamageReceived -= OnTakeDamage;
        }

        private void OnTakeDamage(float damage, in DamageArgs args)
        {
            if (damage < _damageThreshold || args.HitPoint == Vector3.zero)
                return;
            
            _lastHitPoint = args.HitPoint;
            float targetAlpha = damage / Character.HealthManager.MaxHealth;
                    
            if (_fadeSettings.IsCurrentlyFading)
            {
                _fadeSettings.StartFadeCycle(this, _fadeSettings.CurrentAlpha + targetAlpha);
            }
            else
            {
                _fadeSettings.StartFadeCycle(this, targetAlpha);
                CoroutineUtility.StartOrReplaceCoroutine(this, UpdateDirectionalSpriteDirection(), ref _indicatorRoutine);
            }
        }

        private IEnumerator UpdateDirectionalSpriteDirection()
        {
            var headTransform = Character.GetTransformOfBodyPoint(BodyPoint.Head);
            while (_fadeSettings.IsCurrentlyFading)
            {
                Vector3 lookDir = Vector3.ProjectOnPlane(headTransform.forward, Vector3.up).normalized;
                Vector3 dirToPoint = Vector3.ProjectOnPlane(_lastHitPoint - Character.transform.position, Vector3.up).normalized;

                Vector3 rightDir = Vector3.Cross(lookDir, Vector3.up);

                float angle = Vector3.Angle(lookDir, dirToPoint) * Mathf.Sign(Vector3.Dot(rightDir, dirToPoint));

                _damageIndicatorRT.localEulerAngles = Vector3.forward * angle;
                _damageIndicatorRT.localPosition = _damageIndicatorRT.up * _indicatorDistance;

                yield return null;
            }

            _indicatorRoutine = null;
        }
    }
}
