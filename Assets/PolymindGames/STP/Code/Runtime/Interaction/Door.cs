using PolymindGames.ProceduralMotion;
using PolymindGames.SaveSystem;
using System.Collections;
using UnityEngine;
using System;
using UnityEngine.Serialization;

namespace PolymindGames.BuildingSystem
{
    [RequireComponent(typeof(BoxCollider), typeof(IHoverableInteractable))]
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/interaction/interactable/demo-interactables")]
    public sealed class Door : MonoBehaviour, IDamageHandler, ISaveableComponent
    {
        private enum DoorState
        {
            Closed = 0,
            OpenedFront = 1,
            OpenedBack = 2,
        }

        [Flags]
        private enum DoorStateFlags
        {
            Nothing = 0,
            OpenedFront = 1,
            OpenedBack = 2
        }

        [Title("Interaction Settings")]
        [SerializeField, Range(0f, 1000f)]
        [Tooltip("Amount of damage required to open the door. Set to 0 to ignore damage interaction.")]
        private float _damageRequiredToOpen = 30f;

        [SerializeField]
        [Tooltip("If true, the door will attempt to push away obstructing objects when opening.")]
        private bool _pushObstructions = true;

        [SerializeField, Range(0f, 3f)]
        [Tooltip("Cooldown time between door interactions.")]
        private float _interactCooldown = 0.5f;

        [SerializeField, SpaceArea]
        [Tooltip("Displayed title when door is in closed state.")]
        private string _openTitle = "Open";

        [SerializeField]
        [Tooltip("Displayed title when door is in opened state.")]
        private string _closeTitle = "Close";

        [Title("State Settings")]
        [SerializeField]
        [Tooltip("Defines which directions the door is allowed to open.")]
        private DoorStateFlags _allowedDoorStates = DoorStateFlags.OpenedFront | DoorStateFlags.OpenedBack;

        [Title("Animation")]
        [SerializeField]
        [Tooltip("Rotation offset to apply when the door opens.")]
        private Vector3 _openRotation;

        [SerializeField, Range(0.1f, 30f)]
        [Tooltip("Speed of the door opening/closing animation.")]
        private float _animationDuration = 1f;

        [SerializeField]
        private EaseType _animationType = EaseType.SineIn;

        [Title("Audio")]
        [SerializeField]
        [Tooltip("Audio played when the door opens.")]
        private AudioData _openAudio = new(null);

        [SerializeField]
        [Tooltip("Audio played when the door closes.")]
        private AudioData _closeAudio = new(null);

        private IHoverableInteractable _interactable;
        private Quaternion _closedRotation;
        private BoxCollider _collider;
        private float _interactTimer;
        private Coroutine _animationRoutine;
        private DoorState _state;

        private void Awake()
        {
            _closedRotation = transform.localRotation;
            _collider = GetComponent<BoxCollider>();
            _interactable = GetComponent<IHoverableInteractable>();

            _interactable.Interacted += TryToggleDoor;
            _interactable.Title = _openTitle;
        }

        /// <summary>
        /// Attempts to toggle the door when interacted with by the character.
        /// </summary>
        /// <param name="interactable">The interactable component.</param>
        /// <param name="character">The character interacting with the door.</param>
        private void TryToggleDoor(IInteractable interactable, ICharacter character)
        {
            if (Time.time <= _interactTimer)
                return;

            ToggleDoor(character.transform);
        }

        /// <summary>
        /// Toggles the door state based on the character's position and updates interaction cooldown.
        /// </summary>
        /// <param name="source">The transform of the character interacting with the door.</param>
        private void ToggleDoor(Transform source)
        {
            SetDoorState(CalculateTargetState(source));
            _interactTimer = Time.time + _interactCooldown;
        }

        /// <summary>
        /// Calculates the target door state based on the character's relative position.
        /// </summary>
        /// <param name="source">The transform of the character interacting with the door.</param>
        /// <returns>The desired DoorState to transition to.</returns>
        private DoorState CalculateTargetState(Transform source)
        {
            if (_state != DoorState.Closed)
                return DoorState.Closed;

            bool isInFront = Vector3.Dot(source.forward, transform.forward) > 0f;
            DoorState preferredState = isInFront ? DoorState.OpenedFront : DoorState.OpenedBack;

            if (_allowedDoorStates.HasFlag((DoorStateFlags)preferredState))
                return preferredState;

            DoorState alternateState = preferredState == DoorState.OpenedBack ? DoorState.OpenedFront : DoorState.OpenedBack;
            return _allowedDoorStates.HasFlag((DoorStateFlags)alternateState) ? alternateState : DoorState.Closed;
        }

        private void SetDoorState(DoorState targetState, bool animate = true)
        {
            if (targetState == _state)
                return;

            bool isClosed = targetState == DoorState.Closed;
            _interactable.Title = isClosed ? _openTitle : _closeTitle;
            AudioManager.Instance.PlayClip3D(isClosed ? _closeAudio : _openAudio, transform.position);

            if (animate)
            {
                CoroutineUtility.StartOrReplaceCoroutine(this, SetDoorStateWithAnimation(targetState), ref _animationRoutine);
            }
            else
            {
                transform.localRotation = GetTargetRotationForState(targetState);
            }

            _state = targetState;
        }

        /// <summary>
        /// Sets the door state with animation.
        /// </summary>
        /// <param name="targetState">The target DoorState to transition to.</param>
        private IEnumerator SetDoorStateWithAnimation(DoorState targetState)
        {
            Quaternion startRotation = transform.localRotation;
            Quaternion targetRotation = GetTargetRotationForState(targetState);
            Vector3 colliderSize = Vector3.Scale(_collider.size, new Vector3(1f, 1f, 2f));
            
            float startTime = Time.time;
            float endTime = startTime + _animationDuration;
            
            while (Time.time < endTime)
            {
                float t = Easer.Apply(_animationType, 1 - ((endTime - Time.time) / _animationDuration));
                transform.localRotation = Quaternion.Lerp(startRotation, targetRotation, t);

                if (_pushObstructions)
                {
                    HandleCollisions(colliderSize);
                }

                yield return null;
            }

            transform.localRotation = targetRotation;
            _animationRoutine = null;
        }

        /// <summary>
        /// Gets the target rotation for the door based on the specified DoorState.
        /// </summary>
        /// <param name="state">The target DoorState.</param>
        /// <returns>The corresponding rotation for the door.</returns>
        private Quaternion GetTargetRotationForState(DoorState state)
        {
            Vector3 modelEulerAngles = transform.localEulerAngles;
            return state switch
            {
                DoorState.OpenedFront => Quaternion.Euler(modelEulerAngles + _openRotation),
                DoorState.OpenedBack => Quaternion.Euler(modelEulerAngles - _openRotation),
                _ => _closedRotation
            };
        }

        /// <summary>
        /// Handles collisions with objects during the door's movement.
        /// </summary>
        /// <param name="size">The size of the collider to use for overlap checks.</param>
        private void HandleCollisions(Vector3 size)
        {
            int count = PhysicsUtility.OverlapBoxOptimized(transform.TransformPoint(_collider.center), size, transform.rotation, out var colliders, LayerConstants.SolidObjectsMask);

            for (int i = 0; i < count; i++)
            {
                Vector3 directionToCollider = (colliders[i].transform.position - transform.position).normalized;
                Vector3 forceDirection = Vector3.Dot(directionToCollider, transform.forward) > 0 ? transform.forward : -transform.forward;

                if (colliders[i].TryGetComponent<IDamageImpactHandler>(out var impactHandler))
                {
                    impactHandler.HandleImpact(colliders[i].transform.position, forceDirection);
                }
                else if (colliders[i].TryGetComponent(out Rigidbody rigidB))
                {
                    rigidB.AddForce(forceDirection, ForceMode.Impulse);
                }
            }
        }

        #region Editor
#if UNITY_EDITOR
        private void Reset()
        {
            if (!gameObject.HasComponent<IHoverableInteractable>())
                gameObject.AddComponent<Interactable>();
        }
#endif
        #endregion

		#region Damage
        ICharacter IDamageHandler.Character => null;

        DamageResult IDamageHandler.HandleDamage(float damage, in DamageArgs args)
        {
            if (_damageRequiredToOpen < 0.01f || damage < _damageRequiredToOpen)
                return DamageResult.Ignored;

            bool isOpen = Quaternion.Angle(_closedRotation, transform.localRotation) > 0.5f;
            if (!isOpen)
            {
                ToggleDoor(args.Source.transform);
            }

            return DamageResult.Normal;
        }
        #endregion

        #region Save & Load
        void ISaveableComponent.LoadMembers(object data)
        {
            var flags = (DoorState)data;
            SetDoorState(flags, false);
        }

        object ISaveableComponent.SaveMembers()
        {
            return _state;
        }
        #endregion
    }
}