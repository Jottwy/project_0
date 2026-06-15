using PolymindGames.ResourceHarvesting;
using PolymindGames.SurfaceSystem;
using PolymindGames.UserInterface;
using UnityEngine.Serialization;
using System.Collections;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Handles melee attacks with harvesting functionality. This class manages the delay, radius, distance, and strength of the harvesting attack.
    /// </summary>
    [AddComponentMenu("Polymind Games/Wieldables/Melee/Attacks/Melee Harvest Attack ")]
    public sealed class MeleeHarvestAttack : MeleeAttackBehaviour
    {
        [FormerlySerializedAs("_harvestDelay")]
        [SerializeField, Range(0f, 5f), Title("Attack Settings")]
        [Tooltip("The delay before the harvesting action is performed, in seconds.")]
        private float _attackDelay = 0.2f;

        [FormerlySerializedAs("_harvestDistance"),SerializeField, Range(0f, 5f)]
        [Tooltip("The maximum distance at which resources can be harvested.")]
        private float _attackDistance = 0.55f;
        
        [SerializeField, SpaceArea(3f)]
        [Tooltip("Audio data for the attack sound played during the harvesting action.")]
        private DelayedAudioData _attackAudio = new(null);
        
        [Title("Hit Settings")]
        [SerializeField, Range(0f, 1000f)]
        [Tooltip("The force applied when harvesting, affecting the impact on the resource.")]
        private float _hitForce = 50f;

        [SerializeField]
        [Tooltip("The type of damage inflicted by the attack.")]
        private DamageType _hitDamageType = DamageType.Slash;
        
        [SerializeField]
        [Tooltip("Audio data for the sound played when the harvesting is successful.")]
        private AudioData _hitAudio = new(null);

        [Title("Harvest Settings")]
        [SerializeField, ReorderableList]
        [LabelByChild(nameof(ResourceHarvestProfile.ResourceType))]
        private ResourceHarvestProfile[] _resourceHarvestProfiles;

        private Coroutine _attackRoutine;

        /// <summary>
        /// Attempts to perform a harvest attack with the specified accuracy.
        /// </summary>
        /// <param name="accuracy">The accuracy of the attack.</param>
        /// <param name="hitCallback">Optional callback invoked when a successful hit occurs.</param>
        /// <returns>True if the attack was successfully initiated; otherwise, false.</returns>
        public override bool TryAttack(float accuracy, UnityAction hitCallback = null)
        {
            Ray ray = GetUseRay(accuracy);
            if (TryFindHarvestableResource(ray, out _, out _, out _))
            {
                // Play the attack animation and audio
                PlayAttackAnimation();
                Wieldable.Audio.PlayClip(_attackAudio, BodyPoint.Hands);

                // Start the coroutine to handle the actual harvesting
                _attackRoutine = StartCoroutine(HitCoroutine(accuracy, hitCallback));
                return true;
            }
            
            // return false if no valid harvestable found.
            return false;
        }

        /// <summary>
        /// Cancels the current harvest attack and stops any running attack coroutine.
        /// </summary>
        public override void CancelAttack() => CoroutineUtility.StopCoroutine(this, ref _attackRoutine);

        /// <summary>
        /// Coroutine to handle the harvesting process after the attack delay.
        /// </summary>
        /// <param name="accuracy">The accuracy of the attack.</param>
        /// <param name="hitCallback">Optional callback invoked when a successful harvest occurs.</param>
        /// <returns>An enumerator for the coroutine.</returns>
        private IEnumerator HitCoroutine(float accuracy, UnityAction hitCallback)
        {
            yield return new WaitForTime(_attackDelay);

            Ray ray = GetUseRay(accuracy);
            if (TryFindHarvestableResource(ray, out var harvestable, out var hit, out var profile))
            {
                DamageArgs args = new(_hitDamageType, Wieldable.Character, hit.point, ray.direction * _hitForce);
                harvestable.TryHarvest(profile.HarvestPower, profile.YieldPerHit, in args);
            }
            else if (hit.collider != null)
            {
                HandleDamageAndImpact(hit.collider, hit.rigidbody, hit.point, ray.direction * _hitForce); 
            }

            if (hit.collider != null)
            {
                // Spawn visual effect and play harvest audio
                SurfaceManager.Instance.PlayEffectFromHit(in hit, _hitDamageType.GetSurfaceEffectType(), SurfaceEffectPlayFlags.AudioVisual);
                Wieldable.Audio.PlayClip(_hitAudio, BodyPoint.Hands);
            
                // Play the hit animation and invoke the callback
                PlayHitAnimation();
                hitCallback?.Invoke();
            }

            // Clear the attack routine reference
            _attackRoutine = null;
        }

        /// <summary>
        /// Tries to find a harvestable resource within the range of the given ray.
        /// </summary>
        private bool TryFindHarvestableResource(Ray ray, out HarvestableResourceReference reference, out RaycastHit hit, out ResourceHarvestProfile profile)
        {
            // Perform a sphere cast to detect potential harvestable resources
            if (PhysicsUtility.SphereCastOptimized(ray, 0.1f, _attackDistance, out hit, LayerConstants.SimpleSolidObjectsMask, Wieldable.Character.transform))
            {
                reference = HarvestableResourceReference.Create(hit.collider.gameObject, hit.point, 0.5f);

                if (!reference.IsValid || !TryFindProfileWithResourceType(reference.ResourceType, out profile))
                {
                    profile = null;
                    return false;
                }

                // Check if the resource can be harvested and adjust the hit point if applicable
                if (reference.CanHarvestAt(profile.HarvestPower, ray, hit.point, out var adjustedHitPoint))
                {
                    hit.point = adjustedHitPoint;
                    return true;
                }
            }

            reference = default(HarvestableResourceReference);
            profile = null;
            return false;
        }

        private bool TryFindProfileWithResourceType(HarvestableResourceType resourceType, out ResourceHarvestProfile profile)
        {
            foreach (var harvestProfile in _resourceHarvestProfiles)
            {
                if (harvestProfile.ResourceType == resourceType)
                {
                    profile = harvestProfile;
                    return true;
                }
            }
            
            profile = null;
            return false;
        }

        /// <summary>
        /// Handles the application of damage and impact effects on the hit object.
        /// </summary>
        private void HandleDamageAndImpact(Collider col, Rigidbody rigidB, Vector3 hitPoint, Vector3 hitForce)
        {
            // Apply damage if the object can receive damage.
            if (col.TryGetComponent(out IDamageHandler handler))
            {
                DamageArgs args = new(_hitDamageType, Wieldable.Character, hitPoint, hitForce);
                handler.HandleDamage(1f, args);
            }

            if (col.TryGetComponent(out IDamageImpactHandler impactHandler))
            {
                impactHandler.HandleImpact(hitPoint, hitForce);
            }
            else if (rigidB != null)
            {
                rigidB.AddForceAtPosition(hitForce, hitPoint, ForceMode.Impulse);
            }
        }

        private void OnEnable() => HarvestableResourceHealthUI.ShowIndicator(_resourceHarvestProfiles);
        private void OnDisable() => HarvestableResourceHealthUI.HideIndicator();
    }
}