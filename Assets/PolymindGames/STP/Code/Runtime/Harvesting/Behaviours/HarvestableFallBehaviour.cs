using PolymindGames.ProceduralMotion;
using PolymindGames.SaveSystem;
using System.Collections;
using UnityEngine;

namespace PolymindGames.ResourceHarvesting
{
    /// <summary>
    /// Handles the behavior of a harvestable resource falling upon being fully harvested.
    /// This component manages the visual, audio, and impact effects, as well as spawning items
    /// when the resource impacts the ground.
    /// </summary>
    public class HarvestableFallBehaviour : MonoBehaviour, ISaveableComponent
    {
        [SerializeField, NotNull]
        [Tooltip("The Rigidbody component representing the falling resource.")]
        private Rigidbody _fallingResource;

        [SerializeField, NotNull]
        [Tooltip("Handler for detecting impact triggers during the resource fall.")]
        private TriggerEventHandler _impactTriggerEvent;

        [Title("Spawned Items")]
        [SerializeField, NotNull, PrefabObjectOnly]
        [Tooltip("Prefab used to instantiate items when the resource impacts the ground.")]
        private Rigidbody _itemPrefab;

        [SerializeField, Range(1, 100)]
        [Tooltip("Number of items to spawn when the resource impacts the ground.")]
        private int _itemCount = 6;

        [SerializeField]
        [Tooltip("Offset between each spawned item.")]
        private Vector3 _itemOffset = new(0f, 2f, 0f);

        [SerializeField, Title("Effects")]
        [Tooltip("Particle effect to play when the resource impacts the ground.")]
        private ParticleSystem _impactFX;

        [SerializeField, Range(1f, 100f)]
        private float _impactShakeRadius = 30f;
        
        [SerializeField, IgnoreParent]
        [Tooltip("Settings for the camera shake effect triggered by the resource impact.")]
        private ShakeData _impactShake;

        [SerializeField]
        [Tooltip("Audio played when the resource falls.")]
        private AudioData _fallAudio = new(null);

        [SerializeField]
        [Tooltip("Audio played when the resource impacts the ground.")]
        private AudioData _impactAudio = new(null);

        private IHarvestableResource _harvestable;
        private Vector3 _defaultPosition;
        private bool _isFalling;

        private const float MinImpactTriggerTime = 1f;
        private const float MaxFallTime = 8f;
        private const float ItemForce = 50f;

        /// <summary>
        /// Initializes the default position of the falling resource.
        /// </summary>
        private void Awake()
        {
            if (_harvestable != null)
                return;
            
            _harvestable = GetComponentInParent<IHarvestableResource>();
            if (_harvestable == null)
            {
                Debug.LogError("No parent harvestable found.", gameObject);
                return;
            }

            _harvestable.FullyHarvested += StartFall;
            _harvestable.Respawned += _ => ResetFallingResource();
            _defaultPosition = _fallingResource.transform.localPosition;
        }

        /// <summary>
        /// Triggers the falling behavior when the resource is fully harvested.
        /// </summary>
        /// <param name="amount">Amount of resource harvested.</param>
        /// <param name="args">Details about the damage event.</param>
        private void StartFall(float amount, in DamageArgs args)
        {
            ActivateFallingResource();
            _fallingResource.AddForce(new Vector3(args.HitForce.x, 0, args.HitForce.z), ForceMode.Impulse);
            StartCoroutine(HandleFalling());
        }
        
        /// <summary>
        /// Activates the resource's collider and disables the harvestable's collision.
        /// </summary>
        private void ActivateFallingResource()
        {
            _fallingResource.GetComponent<Collider>().enabled = true;
            _fallingResource.isKinematic = false;
        }

        /// <summary>
        /// Deactivates the resource, resetting its position and disabling its collider.
        /// </summary>
        private void ResetFallingResource()
        {
            _fallingResource.GetComponent<Collider>().enabled = false;
            _fallingResource.isKinematic = true;
            _fallingResource.transform.SetLocalPositionAndRotation(_defaultPosition, Quaternion.identity);
            _fallingResource.gameObject.SetActive(true);
        }

        /// <summary>
        /// Coroutine handling the resource's falling process, including audio and impact detection.
        /// </summary>
        private IEnumerator HandleFalling()
        {
            AudioManager.Instance.PlayClip3D(_fallAudio, transform.position);
            _isFalling = true;
            
            yield return new WaitForTime(0.1f);

            _impactTriggerEvent.TriggerEnter += OnImpact;
            float elapsedTime = 0f;

            while (_isFalling)
            {
                if (elapsedTime > MinImpactTriggerTime &&
                    (elapsedTime > MaxFallTime || _fallingResource.angularVelocity.sqrMagnitude < 0.001f))
                {
                    _isFalling = false;
                }

                elapsedTime += Time.deltaTime;
                yield return null;
            }

            _impactTriggerEvent.TriggerEnter -= OnImpact;
            yield return new WaitForTime(0.1f);

            HandleImpact();
        }

        /// <summary>
        /// Handles the impact logic, spawning items and triggering visual and audio effects.
        /// </summary>
        private void HandleImpact()
        {
            _fallingResource.gameObject.SetActive(false);

            SpawnItems(_fallingResource.transform);
            
            Vector3 impactPosition = _impactTriggerEvent.transform.position;
            AudioManager.Instance.PlayClip3D(_impactAudio, impactPosition);
            ShakeZone.PlayOneShotAtPosition(_impactShake, impactPosition, _impactShakeRadius);
        }
        
        /// <summary>
        /// Spawns items upon resource impact, applying forces and effects to each item.
        /// </summary>
        /// <param name="resourceTransform">Transform of the falling resource.</param>
        private void SpawnItems(Transform resourceTransform)
        {
            for (int i = 0; i < _itemCount; i++)
            {
                var itemInstance = Instantiate(_itemPrefab);
                var itemTransform = itemInstance.transform;

                Vector3 offset = i * _itemOffset + 0.5f * _itemOffset;
                Vector3 itemPosition = resourceTransform.TransformPoint(offset);

                Quaternion itemRotation = Quaternion.LookRotation(resourceTransform.forward, resourceTransform.up);
                itemTransform.SetPositionAndRotation(itemPosition, itemRotation);

                itemInstance.GetComponent<Rigidbody>().AddForce(
                    ItemForce * Mathf.Sign(Random.Range(-100f, 100f)) * itemTransform.right,
                    ForceMode.Impulse);

                if (_impactFX is not null)
                    Instantiate(_impactFX, itemTransform.position, itemTransform.rotation);
            }
        }

        /// <summary>
        /// Event handler to stop the falling process when the resource impacts another collider.
        /// </summary>
        /// <param name="other">The collider that was impacted.</param>
        private void OnImpact(Collider other) => _isFalling = false;

        #region Save & Load
        void ISaveableComponent.LoadMembers(object data)
        {
            Awake();
            if (_harvestable.HarvestableState != HarvestableState.FullyHarvested)
                return;
            
            // If the tree is still falling.
            if (data is SerializedRigidbodyData saveData)
            {
                ActivateFallingResource();
                SerializedRigidbodyData.ApplyToRigidbody(_fallingResource, saveData);
                StartCoroutine(HandleFalling());
            }
            else
            {
                _fallingResource.gameObject.SetActive(false);
            }
        }

        object ISaveableComponent.SaveMembers()
        {
            return _harvestable.HarvestableState == HarvestableState.FullyHarvested && _isFalling
                ? new SerializedRigidbodyData(_fallingResource)
                : null;
        }
        #endregion
    }
}