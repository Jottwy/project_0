using PolymindGames.ProceduralMotion;
using PolymindGames.InventorySystem;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.Events;
using UnityEngine;
using System;

namespace PolymindGames.BuildingSystem
{
    public sealed class CharacterConstructableBuilder : CharacterBehaviour, IConstructableBuilderCC
    {
        [SerializeField, Range(0f, 5f), Title("Detection Settings")]
        [Tooltip("The cooldown time for updating constructable detection.")]
        private float _updateCooldown = 0.2f;

        [SerializeField, Range(0f, 10f)]
        [Tooltip("The maximum distance at which constructables are detected.")]
        private float _maxDetectionDistance = 7f;

        [SerializeField, Range(0f, 120f)]
        [Tooltip("The maximum angle for detecting constructables.")]
        private float _maxDetectionAngle = 20f;

        [SerializeField, Range(0f, 5f), SpaceArea]
        [Tooltip("The duration for canceling the constructable.")]
        private float _cancelPreviewDuration = 0.35f;

        [SerializeField, Title("Effects")]
        private ShakeData _addMaterialShake;

        [SerializeField]
        private AudioData _invalidAddMaterialAudio = new(null);

        private IConstructable _currentConstructable;
        private List<IItemContainer> _containers;
        private Transform _headTransform;
        private float _cancelPreviewProgress;
        private float _invalidAudioTimer;
        private bool _detectionEnabled;
        private float _updateTimer;

        public IConstructable CurrentConstructable
        {
            get => _currentConstructable;
            private set
            {
                if (!ReferenceEquals(_currentConstructable, value))
                {
                    _currentConstructable = value;
                    ConstructableChanged?.Invoke(_currentConstructable);
                }
            }
        }

        public bool DetectionEnabled
        {
            get => _detectionEnabled;
            set
            {
                if (_detectionEnabled != value)
                {
                    _detectionEnabled = value;

                    if (value)
                        enabled = true;
                    else
                    {
                        enabled = false;
                        CurrentConstructable = null;
                    }
                }
            }
        }

        private float CancelPreviewProgress
        {
            get => _cancelPreviewProgress;
            set
            {
                _cancelPreviewProgress = value;
                CancelConstructableProgressChanged?.Invoke(value);
            }
        }

        public event UnityAction<float> CancelConstructableProgressChanged;
        public event UnityAction<IConstructable> ConstructableChanged;
        public event UnityAction<BuildMaterialDefinition, int> BuildMaterialAdded;

        public void StartCancellingPreview()
        {
            if (_currentConstructable == null)
                return;

            StartCoroutine(DestroyConstructable(_currentConstructable));
        }

        public void StopCancellingPreview()
        {
            if (CancelPreviewProgress == 0f)
                return;

            StopAllCoroutines();
            CancelPreviewProgress = 0f;
        }

        public bool TryAddMaterialFromPlayer()
        {
            if (_currentConstructable == null)
                return false;

            _containers ??= Character.Inventory.FindContainers(ItemContainerFilters.WithoutTag);
            foreach (var container in _containers)
            {
                foreach (var slot in container.GetSlots())
                {
                    if (TryGetBuildMaterialFromSlot(slot, out var buildMaterial) && TryAddMaterial(buildMaterial, false))
                        return true;
                }
            }

            HandleFailedAddMaterial();
            return false;
        }

        public bool TryAddMaterial(BuildMaterialDefinition buildMaterial)
            => TryAddMaterial(buildMaterial, true);

        private bool TryAddMaterial(BuildMaterialDefinition buildMaterial, bool playFailedEffects)
        {
            if (_currentConstructable == null)
                return false;
            
            if (_currentConstructable.TryAddMaterial(buildMaterial))
            {
                BuildMaterialAdded?.Invoke(buildMaterial, 1);
                HandleSuccessfulAddMaterial();
                return true;
            }

            if (playFailedEffects)
                HandleFailedAddMaterial();

            return false;
        }

        protected override void OnBehaviourStart(ICharacter character)
        {
            _headTransform = character.GetTransformOfBodyPoint(BodyPoint.Head);
        }

        private void FixedUpdate()
        {
            if (_updateTimer > Time.fixedTime)
                return;

            int count = PhysicsUtility.OverlapSphereOptimized(_headTransform.position, _maxDetectionDistance, out var cols, LayerConstants.BuildingMask, QueryTriggerInteraction.Collide);

            CurrentConstructable = count > 0 ? GetClosestConstructable(cols.AsSpan(0, count)) : null;
            _updateTimer = Time.fixedTime + _updateCooldown;
        }

        private static bool TryGetBuildMaterialFromSlot(in SlotReference slot, out BuildMaterialDefinition buildMaterial)
        {
            if (slot.GetItem()?.Definition.TryGetDataOfType(out BuildMaterialData buildData) ?? false)
            {
                buildMaterial = buildData.BuildMaterial;
                return true;
            }

            buildMaterial = null;
            return false;
        }

        /// <summary>
        /// Finds the closest constructable object among the provided colliders.
        /// </summary>
        /// <param name="colliders">A span of colliders to search through.</param>
        /// <returns>The closest constructable object, or null if no constructable object is found.</returns>
        private IConstructable GetClosestConstructable(ReadOnlySpan<Collider> colliders)
        {
            // Initialize variables to keep track of the closest constructable object and its rank.
            IConstructable closestConstructable = null;
            float closestRank = float.MaxValue;

            // Loop through each collider in the provided span.
            foreach (var col in colliders)
            {
                if (col.TryGetComponent(out IConstructable constructable))
                {
                    if (constructable.IsConstructed)
                        continue;

                    // Calculate the position and direction from the character to the collider.
                    Vector3 playerPosition = _headTransform.position;
                    Vector3 position = col.transform.position;
                    Vector3 direction = position - playerPosition;

                    // Calculate the squared distance between the character and the collider.
                    float distance = (playerPosition - position).sqrMagnitude;

                    // If the distance is greater than the maximum detection distance squared, skip to the next collider.
                    if (distance > _maxDetectionDistance * _maxDetectionDistance)
                        continue;

                    // Calculate the angle between the direction to the collider and the character's forward direction.
                    float angle = Vector3.Angle(direction, _headTransform.forward);

                    // If the angle is greater than the maximum detection angle, skip to the next collider.
                    if (angle > _maxDetectionAngle)
                        continue;

                    // Calculate the rank of the current collider based on distance and angle.
                    float rank = distance + angle;

                    // If the rank of the current collider is greater than the closest rank found so far, skip to the next collider.
                    if (closestRank < rank)
                        continue;

                    // Update the closest rank and closest constructable object to the current collider.
                    closestRank = rank;
                    closestConstructable = constructable;
                }
            }

            // Return the closest constructable object found among the colliders.
            return closestConstructable;
        }

        private IEnumerator DestroyConstructable(IConstructable constructable)
        {
            float endTime = Time.time + _cancelPreviewDuration;
            while (Time.time < endTime)
            {
                CancelPreviewProgress = 1 - (endTime - Time.time) / _cancelPreviewDuration;
                yield return null;
            }

            CancelPreviewProgress = 0f;
            if (constructable != null)
            {
                var parentGrouyp = constructable.BuildingPiece.ParentGroup;

                if (parentGrouyp != null)
                {
                    foreach (var groupConstructable in constructable.BuildingPiece.ParentGroup.BuildingPieces)
                    {
                        if (!groupConstructable.IsConstructed)
                            Destroy(groupConstructable.gameObject);
                    }
                }
                else
                {
                    Destroy(constructable.gameObject);
                }
            }
        }

        private void HandleSuccessfulAddMaterial()
        {
            if (_addMaterialShake.IsPlayable && Character is IFPSCharacter fpsCharacter)
                fpsCharacter.HeadComponents.Shake?.AddShake(_addMaterialShake);
        }

        private void HandleFailedAddMaterial()
        {
            if (_invalidAudioTimer < Time.time)
            {
                _invalidAudioTimer = Time.time + 0.3f;
                Character.Audio.PlayClip(_invalidAddMaterialAudio, BodyPoint.Torso);
            }
        }
    }
}