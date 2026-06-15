using System.Collections;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    [HelpURL(
        "https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/player/modules-and-behaviours/interaction#interaction-handler-module")]
    public sealed class InteractionHandler : CharacterBehaviour, IInteractionHandlerCC
    {
        private enum DetectionMode
        {
            Raycast,
            SphereCast
        }

        [SerializeField]
        [Tooltip("The transform used in ray-casting.")]
        private Transform _view;

        [SerializeField, SpaceArea]
        private DetectionMode _detectionMode = DetectionMode.SphereCast;

        [SerializeField, Range(0.01f, 25f)]
        [Tooltip("The max detection distance, anything out of range will be ignored.")]
        private float _distance = 2.5f;

        [SerializeField, Range(0f, 25f)]
        [ShowIf(nameof(_detectionMode), DetectionMode.SphereCast)]
        [Tooltip("If set to a value larger than 0, the detection method will be set to SphereCast instead of Raycast.")]
        private float _radius;

        [SerializeField]
        private LayerMask _interactableMask = LayerConstants.InteractableMask;

        [SerializeField]
        [ShowIf(nameof(_detectionMode), DetectionMode.SphereCast)]
        private LayerMask _obstructionMask = LayerConstants.SimpleSolidObjectsMask
            & ~(1 << LayerConstants.Interactable)
            & ~(1 << LayerConstants.Hitbox);

        [SerializeField]
        [Tooltip("The trigger colliders interaction mode.")]
        private QueryTriggerInteraction _triggerInteraction = QueryTriggerInteraction.Collide;

        [SerializeField, SpaceArea]
        private AudioData _failedAudio = new(null);

        private static readonly RaycastHit[] _hits = new RaycastHit[10];
        private IInteractable _interactable;
        private float _interactionProgress;
        private Transform _ignoredRoot;
        private IHoverable _hoverable;
        private bool _hasRaycastHit;

        /// <inheritdoc/>
        public bool InteractionEnabled
        {
            get => enabled;
            set
            {
                if (enabled == value)
                    return;

                enabled = value;
                InteractionEnabledChanged?.Invoke(value);
            }
        }

        /// <inheritdoc/>
        public IHoverable Hoverable => _hoverable;

        public event UnityAction<IHoverable> HoverableInViewChanged;
        public event UnityAction<float> InteractProgressChanged;
        public event UnityAction<bool> InteractionEnabledChanged;

        #region Interaction
        public void StartInteraction()
        {
            if (_interactable != null)
                return;

            if (Hoverable is IInteractable { InteractionEnabled: true } interactable)
            {
                _interactable = interactable;

                if (_interactable.HoldDuration > 0.01f)
                    StartCoroutine(DelayedInteraction());
                else
                    _interactable?.OnInteract(Character);
            }
            else
                Character.Audio.PlayClip(_failedAudio, BodyPoint.Torso);
        }

        public void StopInteraction()
        {
            if (_interactable == null)
                return;

            StopAllCoroutines();

            _interactionProgress = 0f;
            InteractProgressChanged?.Invoke(_interactionProgress);
            _interactable = null;
        }

        private IEnumerator DelayedInteraction()
        {
            float endTime = Time.time + _interactable.HoldDuration;

            while (Time.time <= endTime)
            {
                _interactionProgress = 1 - (endTime - Time.time) / _interactable.HoldDuration;
                InteractProgressChanged?.Invoke(_interactionProgress);
                yield return null;
            }

            _interactable?.OnInteract(Character);
            StopInteraction();
        }
        #endregion

        #region Detection
        private void Awake() => enabled = false;

        protected override void OnBehaviourStart(ICharacter character)
        {
            base.OnBehaviourStart(character);
            _ignoredRoot = character.transform;
        }

        /// <summary>
        /// Detect and handle hoverable interactables in front of the player.
        /// Manages hover state transitions and triggers hover start/end events.
        /// </summary>
        private void FixedUpdate()
        {
            Ray ray = new Ray(_view.position, _view.forward);
            var hoverableInView = _detectionMode == DetectionMode.SphereCast ? SphereCastHoverable(ray) : RaycastHoverable(ray);

            if (hoverableInView == _hoverable)
                return;

            if (hoverableInView != null)
            {
                // Hover End for previous hoverable if any
                if (_hoverable != null)
                {
                    _hoverable.OnHoverEnd(Character);
                    StopInteraction();
                }

                _hoverable = hoverableInView;
                HoverableInViewChanged?.Invoke(hoverableInView);

                // Hover Start for new hoverable
                hoverableInView.OnHoverStart(Character);
            }
            else if (_hoverable != null)
            {
                // Hover End if no hoverable found
                _hoverable.OnHoverEnd(Character);
                StopInteraction();
                _hoverable = null;
                HoverableInViewChanged?.Invoke(null);
            }
        }

        /// <summary>
        /// Finds the best hoverable object within view using a sphere cast and obstruction checks.
        /// Returns the interactable closest to the center of the player's view (lowest angle).
        /// </summary>
        /// <param name="ray">Ray from the player's view forward direction.</param>
        /// <returns>The best <see cref="IHoverable"/> found or null if none.</returns>
        private IHoverable SphereCastHoverable(Ray ray)
        {
            int hitCount =
                Physics.SphereCastNonAlloc(ray, _radius, _hits, _distance, _interactableMask | _obstructionMask, _triggerInteraction);

            IHoverable bestHoverable = null;
            float bestScore = float.MaxValue;

            for (int i = 0; i < hitCount; i++)
            {
                var hit = _hits[i];
                GameObject hitObject = hit.collider.gameObject;

                // Skip if this hit is an obstruction layer
                if (((1 << hitObject.layer) & _obstructionMask) != 0)
                    continue;

                // Skip if line of sight is obstructed
                if (IsObstructed(ray.origin, hit.point))
                    continue;

                // Calculate angle between player's view direction and hit direction
                float angle = Vector3.Angle(ray.direction, (hit.point - ray.origin).normalized);
                if (angle < bestScore)
                {
                    var hoverable = hitObject.GetComponent<IHoverable>();
                    if (hoverable != null && hoverable.enabled)
                    {
                        bestScore = angle;
                        bestHoverable = hoverable;
                    }
                }
            }

            return bestHoverable;
        }

        private IHoverable RaycastHoverable(Ray ray)
        {
            bool hasHit = PhysicsUtility.RaycastOptimized(ray, _distance, out var hit, _interactableMask | _obstructionMask, _ignoredRoot);
            return hasHit ? hit.collider.GetComponent<IHoverable>() : null;
        }

        /// <summary>
        /// Checks if there is an obstruction between two points using a raycast against the obstruction layer mask.
        /// </summary>
        /// <param name="origin">Starting point of the ray.</param>
        /// <param name="target">End point to check obstruction towards.</param>
        /// <returns>True if an obstruction is detected, otherwise false.</returns>
        private bool IsObstructed(Vector3 origin, Vector3 target)
        {
            Vector3 direction = (target - origin);
            float distance = direction.magnitude;
            direction /= distance;

            return Physics.Raycast(origin, direction, distance, _obstructionMask, QueryTriggerInteraction.Ignore);
        }
        #endregion
    }
}