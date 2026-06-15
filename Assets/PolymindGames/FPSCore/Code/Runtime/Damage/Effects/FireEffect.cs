using System.Collections.Generic;
using System.Collections;
using UnityEngine;

namespace PolymindGames
{
    [RequireComponent(typeof(SphereCollider))]
    [AddComponentMenu("Polymind Games/Damage/FireEffect")]
    public sealed class FireEffect : MonoBehaviour
    {
        [SerializeField]
        private bool _igniteOnStart = false;
        
        [SerializeField, Range(0f, 1000f)]
        [Tooltip("The duration of the fire effect in seconds.")]
        private float _duration = 15f;

        [SerializeField, Range(0f, 1000f)]
        [Tooltip("The damage applied per tick of the fire effect.")]
        private float _damagePerTick = 15f;

        [SerializeField, Range(0.01f, 10f)]
        [Tooltip("Time interval between each damage tick in seconds.")]
        private float _timePerTick = 1f;

        [SerializeField]
        [Tooltip("The audio effect to play when the fire is active.")]
        private AudioEffect _audioEffect;

        [SerializeField]
        [Tooltip("The light effect associated with the fire.")]
        private LightEffect _lightEffect;

        [SerializeField]
        [Tooltip("The particle system representing the fire.")]
        private ParticleSystem _particles;

        private List<IDamageHandler> _damageHandlers;
        private SphereCollider _sphereCollider;

        /// <summary>
        /// Activates the fire effect at its current position.
        /// </summary>
        public void Ignite() => Ignite(null);

        /// <summary>
        /// Activates the fire effect and optionally assigns a damage source.
        /// </summary>
        /// <param name="source">The damage source responsible for this fire effect.</param>
        public void Ignite(IDamageSource source)
        {
            _damageHandlers ??= new List<IDamageHandler>();
            _damageHandlers.Clear();

            _sphereCollider.enabled = true;

            if (Physics.Raycast(transform.position, Vector3.down, out RaycastHit hit, 10f))
                transform.SetPositionAndRotation(hit.point, Quaternion.Euler(hit.normal));

            StartCoroutine(HandleFireEffect(source));
        }

        private void OnTriggerEnter(Collider other)
        {
            if (other.gameObject.IsLayerInMask(LayerConstants.DamageableMask) &&
                other.TryGetComponent<IDamageHandler>(out var damageHandler))
            {
                _damageHandlers.Add(damageHandler);
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (other.gameObject.IsLayerInMask(LayerConstants.DamageableMask) &&
                other.TryGetComponent<IDamageHandler>(out var damageHandler))
            {
                _damageHandlers.Remove(damageHandler);
            }
        }

        private void OnEnable()
        {
            _sphereCollider.enabled = false;
            StopEffects();
        }

        private void Awake()
        {
            _sphereCollider = GetComponent<SphereCollider>();
            if (_igniteOnStart)
                Ignite();
        }

        private IEnumerator HandleFireEffect(IDamageSource source)
        {
            StartEffects();

            DamageArgs damageArgs = new(DamageType.Fire, source, transform.position, Vector3.zero);
            float damage = _damagePerTick;

            float endTime = Time.time + _duration;
            while (Time.time < endTime)
            {
                foreach (var damageHandler in _damageHandlers)
                {
                    damageHandler.HandleDamage(damage, damageArgs);
                }

                yield return new WaitForTime(_timePerTick);
            }

            _sphereCollider.enabled = false;
            StopEffects();
        }

        private void StartEffects()
        {
            _audioEffect.Play();
            _lightEffect.Play();
            _particles.Play(true);
        }

        private void StopEffects()
        {
            _audioEffect.Stop();
            _lightEffect.Stop();
            _particles.Stop(true);
        }

        #region Editor
#if UNITY_EDITOR
        private void Reset()
        {
            gameObject.layer = LayerConstants.TriggerZone;
        }

        private void OnValidate()
        {
            if (_sphereCollider == null)
                _sphereCollider = GetComponent<SphereCollider>();

            _sphereCollider.isTrigger = true;
        }
#endif
        #endregion
    }
}