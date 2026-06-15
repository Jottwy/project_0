using System.Collections.Generic;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine;
using System;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// A class that represents a selectable UI component. It handles user interactions
    /// and manages various states, such as pressed, selected, highlighted, and normal.
    /// </summary>
    [SelectionBase, DisallowMultipleComponent]
    [AddComponentMenu("Polymind Games/User Interface/Selectables/Selectable Button")]
    public sealed class SelectableButton : UIBehaviour, IMoveHandler, IPointerDownHandler, IPointerUpHandler, IPointerEnterHandler, IPointerExitHandler, ISelectHandler
    {
        [SerializeField]
        [Tooltip("Can this object be selected?")]
        private bool _isInteractable = true;

        [SerializeField]
        private bool _isSelectable = true;

        [SerializeField]
        private SelectableNavigation _navigation;
        
        [SpaceArea]
        [SerializeReference, ReorderableList(elementLabel: "Feedback")]
        [ReferencePicker(typeof(SelectableButtonFeedback), TypeGrouping.ByFlatName)]
        private SelectableButtonFeedback[] _feedbacks = Array.Empty<SelectableButtonFeedback>();

        private readonly List<CanvasGroup> _canvasGroupCache = new();
        private SelectableGroupBase _parentGroup;
        private int _currentIndex = -1;
        private bool _canvasAllowsInteraction = true;
        private bool _enableCalled;
        private bool _isPointerInside;
        private bool _isPointerDown;
        private bool _isSelected;

        private static SelectableButton[] _selectables = new SelectableButton[16];
        private static int _selectableCount;

        /// <summary>
        /// Gets or sets a value indicating whether this object is selectable.
        /// </summary>
        public bool IsInteractable
        {
            get => _isInteractable && _canvasAllowsInteraction;
            set
            {
                if (_isInteractable == value)
                    return;
                
                _isInteractable = value;

                if (!_isInteractable && _isSelected)
                    _parentGroup.SelectSelectable(null);

                TransitionToState(CurrentSelectionState);
            }
        }

        public bool IsSelectable
        {
            get => _isSelectable && _parentGroup != null;
            set
            {
                if (_isSelectable == value)
                    return;

                _isSelectable = value;
                _isSelected = false;
                
                if (_parentGroup == null)
                    return;

                if (value)
                {
                    _parentGroup.RegisterSelectable(this);
                }
                else
                {
                    _parentGroup.UnregisterSelectable(this);
                }

                TransitionToState(CurrentSelectionState);
            }
        }

        /// <summary>
        /// Gets the current selection state of the UI element.
        /// </summary>
        private SelectionState CurrentSelectionState
        {
            get
            {
                if (_isPointerDown) return SelectionState.Pressed;
                if (IsSelectable && _parentGroup.Selected == this) return SelectionState.Selected;
                return _isPointerInside ? SelectionState.Highlighted : SelectionState.Normal;
            }
        }

        /// <summary>
        /// Event triggered when the UI element is clicked.
        /// </summary>
        public event UnityAction<SelectableButton> Clicked;

        /// <summary>
        /// Selects this UI element, setting it as the current selected GameObject in the EventSystem.
        /// </summary>
        public void Select()
        {
            if (!IsInteractable || EventSystem.current == null || EventSystem.current.alreadySelecting)
                return;

            EventSystem.current.SetSelectedGameObject(gameObject);
        }

        /// <summary>
        /// Deselects this UI element and triggers the relevant feedback.
        /// </summary>
        public void Deselect()
        {
            _isSelected = false;
            TriggerFeedback(FeedbackState.Deselected);
            TransitionToState(CurrentSelectionState);
        }

        /// <summary>
        /// Handles the event when the UI element is selected.
        /// </summary>
        /// <param name="eventData">The data related to the event.</param>
        public void OnSelect(BaseEventData eventData)
        {
            if (!_canvasAllowsInteraction || !IsInteractable || _isSelected)
                return;

            if (IsSelectable)
            {
                _isSelected = true;
                _parentGroup.SelectSelectable(this);
                TransitionToState(CurrentSelectionState);
            }
        }

        /// <summary>
        /// Handles the pointer down event, transitioning the UI element to the pressed state.
        /// </summary>
        /// <param name="eventData">The data related to the pointer event.</param>
        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;

            _isPointerDown = true;
            TransitionToState(CurrentSelectionState);
        }

        /// <summary>
        /// Handles the pointer up event, selecting the UI element if necessary and triggering feedback.
        /// </summary>
        /// <param name="eventData">The data related to the pointer event.</param>
        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;

            _isPointerDown = false;
            if (_isPointerInside && IsInteractable)
            {
                Select();
                TriggerFeedback(FeedbackState.Clicked, true);
                Clicked?.Invoke(this);
            }
            TransitionToState(CurrentSelectionState);
        }

        /// <summary>
        /// Handles the pointer enter event, transitioning the UI element to the highlighted state.
        /// </summary>
        /// <param name="eventData">The data related to the pointer event.</param>
        public void OnPointerEnter(PointerEventData eventData)
        {
            _isPointerInside = true;
            
            if (!_isSelected)
                TransitionToState(CurrentSelectionState);

            if (IsSelectable)
                _parentGroup.HighlightSelectable(this);
        }

        /// <summary>
        /// Handles the pointer exit event, transitioning the UI element to the normal state.
        /// </summary>
        /// <param name="eventData">The data related to the pointer event.</param>
        public void OnPointerExit(PointerEventData eventData)
        {
            _isPointerInside = false;
            
            if (!_isSelected)
                TransitionToState(CurrentSelectionState);

            if (IsSelectable)
                _parentGroup.HighlightSelectable(null);
        }

        /// <summary>
        /// Handles navigation events, determining which selectable object should be focused next.
        /// </summary>
        /// <param name="eventData">The data related to the axis event.</param>
        public void OnMove(AxisEventData eventData)
        {
            SelectableButton nextButtonSelectable = GetSelectableInDirection(eventData.moveDir);

            if (nextButtonSelectable != null && nextButtonSelectable.IsActive())
            {
                eventData.selectedObject = nextButtonSelectable.gameObject;
            }
        }

        /// <summary>
        /// Retrieves the selectable object in the specified direction based on the current navigation mode.
        /// </summary>
        /// <param name="direction">The direction in which to navigate (Right, Left, Up, Down).</param>
        private SelectableButton GetSelectableInDirection(MoveDirection direction)
        {
            return direction switch
            {
                MoveDirection.Right => GetSelectableInDirectionInternal(Vector3.right, SelectableNavigation.NavMode.Horizontal, _navigation.SelectOnRight),
                MoveDirection.Up => GetSelectableInDirectionInternal(Vector3.up, SelectableNavigation.NavMode.Vertical, _navigation.SelectOnUp),
                MoveDirection.Left => GetSelectableInDirectionInternal(Vector3.left, SelectableNavigation.NavMode.Horizontal, _navigation.SelectOnLeft),
                MoveDirection.Down => GetSelectableInDirectionInternal(Vector3.down, SelectableNavigation.NavMode.Vertical, _navigation.SelectOnDown),
                _ => null
            };
        }

        /// <summary>
        /// Determines the selectable object in a given direction, factoring in navigation mode and explicit selection.
        /// </summary>
        /// <param name="direction">The direction vector (e.g., Vector3.right, Vector3.left, etc.).</param>
        /// <param name="mode">The navigation mode (horizontal or vertical).</param>
        /// <param name="explicitSelection">The explicitly selected object in that direction (if available).</param>
        private SelectableButton GetSelectableInDirectionInternal(Vector3 direction, SelectableNavigation.NavMode mode, SelectableButton explicitSelection)
        {
            // If the navigation mode is explicit, use the provided explicit selection
            if (_navigation.Mode == SelectableNavigation.NavMode.Explicit)
            {
                return explicitSelection;
            }

            // If the mode supports the given direction (horizontal or vertical), perform the search
            if ((_navigation.Mode & mode) != 0)
            {
                return FindSelectable(transform.rotation * direction);
            }

            return null;
        }

        /// <summary>
        /// Finds the selectable object next to this one.
        /// </summary>
        /// <remarks>
        /// The direction is determined by a Vector3 variable.
        /// </remarks>
        /// <param name="dir">The direction in which to search for a neighbouring Selectable object.</param>
        /// <returns>The neighbouring Selectable object. Null if none found.</returns>
        private SelectableButton FindSelectable(Vector3 dir)
        {
            dir = dir.normalized;
            Vector3 localDir = Quaternion.Inverse(transform.rotation) * dir;
            Vector3 pos = transform.TransformPoint(GetPointOnRectEdge(transform as RectTransform, localDir));
            float maxScore = Mathf.NegativeInfinity;
            float maxFurthestScore = Mathf.NegativeInfinity;

            bool wantsWrapAround = _navigation.WrapAround && (_navigation.Mode == SelectableNavigation.NavMode.Vertical || _navigation.Mode == SelectableNavigation.NavMode.Horizontal);

            SelectableButton bestPick = null;
            SelectableButton bestFurthestPick = null;

            for (int i = 0; i < _selectableCount; ++i)
            {
                SelectableButton sel = _selectables[i];

                if (sel == this)
                    continue;

                if (!sel.IsInteractable || sel._navigation.Mode == SelectableNavigation.NavMode.None)
                    continue;

                if (sel._parentGroup != _parentGroup)
                {
                    if (sel.IsSelectable && IsSelectable)
                    {
                        if (!Equals(sel._parentGroup.RegisteredSelectables, _parentGroup.RegisteredSelectables))
                            continue;
                    }
                    else
                        continue;
                }

#if UNITY_EDITOR

                // Apart from runtime use, FindSelectable is used by custom editors to
                // draw arrows between different selectables. For scene view cameras,
                // only selectables in the same stage should be considered.
                if (Camera.current != null && !UnityEditor.SceneManagement.StageUtility.IsGameObjectRenderedByCamera(sel.gameObject, Camera.current))
                    continue;
#endif

                var selRect = sel.transform as RectTransform;
                Vector3 selCenter = selRect != null ? selRect.rect.center : Vector3.zero;
                Vector3 myVector = sel.transform.TransformPoint(selCenter) - pos;

                // Value that is the distance out along the direction.
                float dot = Vector3.Dot(dir, myVector);

                // If element is in wrong direction and we have wrapAround enabled check and cache it if furthest away.
                float score;
                if (wantsWrapAround && dot < 0)
                {
                    score = -dot * myVector.sqrMagnitude;

                    if (score > maxFurthestScore)
                    {
                        maxFurthestScore = score;
                        bestFurthestPick = sel;
                    }

                    continue;
                }

                // Skip elements that are in the wrong direction or which have zero distance.
                // This also ensures that the scoring formula below will not have a division by zero error.
                if (dot <= 0)
                    continue;

                // This scoring function has two priorities:
                // - Score higher for positions that are closer.
                // - Score higher for positions that are located in the right direction.
                // This scoring function combines both of these criteria.
                // It can be seen as this:
                //   Dot (dir, myVector.normalized) / myVector.magnitude
                // The first part equals 1 if the direction of myVector is the same as dir, and 0 if it's orthogonal.
                // The second part scores lower the greater the distance is by dividing by the distance.
                // The formula below is equivalent but more optimized.
                //
                // If a given score is chosen, the positions that evaluate to that score will form a circle
                // that touches pos and whose center is located along dir. A way to visualize the resulting functionality is this:
                // From the position pos, blow up a circular balloon so it grows in the direction of dir.
                // The first Selectable whose center the circular balloon touches is the one that's chosen.
                score = dot / myVector.sqrMagnitude;

                if (score > maxScore)
                {
                    maxScore = score;
                    bestPick = sel;
                }
            }

            if (wantsWrapAround && null == bestPick)
                return bestFurthestPick;

            return bestPick;
        }

        /// <summary>
        /// Whether the current selectable is being pressed.
        /// </summary>
        private bool IsPressed()
        {
            if (!isActiveAndEnabled || !_canvasAllowsInteraction)
                return false;

            return _isPointerDown;
        }

        /// <summary>
        /// Transitions the Selectable to the specified state with optional instant feedback.
        /// </summary>
        /// <param name="state">The state to transition to.</param>
        /// <param name="instant">Indicates if the transition should occur instantly.</param>
        private void TransitionToState(SelectionState state, bool instant = false)
        {
            bool disabled = !_isInteractable || !_canvasAllowsInteraction || !isActiveAndEnabled;
            var feedbackState = disabled ? FeedbackState.Disabled : MatchSelectionStateWithFeedback(state);
            TriggerFeedback(feedbackState, instant);
        }

        private FeedbackState MatchSelectionStateWithFeedback(SelectionState state) => state switch
        {
            SelectionState.Normal => FeedbackState.Normal,
            SelectionState.Highlighted => FeedbackState.Highlighted,
            SelectionState.Pressed => FeedbackState.Pressed,
            SelectionState.Selected => FeedbackState.Selected,
            _ => FeedbackState.Disabled
        };

        /// <summary>
        /// Triggers feedback for a specified type with optional instant behavior.
        /// </summary>
        /// <param name="type">The type of feedback to trigger.</param>
        /// <param name="instant">Indicates if the feedback should be triggered instantly.</param>
        private void TriggerFeedback(FeedbackState type, bool instant = false)
        {
            if (UnityUtility.IsQuitting)
                return;
            
            // Iterate feedbacks and invoke corresponding method
            foreach (var feedback in _feedbacks)
            {
                switch (type)
                {
                    case FeedbackState.Normal:
                        feedback.OnNormal(instant);
                        break;
                    case FeedbackState.Highlighted:
                        feedback.OnHighlighted(instant);
                        break;
                    case FeedbackState.Pressed:
                        feedback.OnPressed(instant);
                        break;
                    case FeedbackState.Selected:
                        feedback.OnSelected(instant);
                        break;
                    case FeedbackState.Deselected:
                        feedback.OnDeselected(instant);
                        break;
                    case FeedbackState.Disabled:
                        feedback.OnDisabled(instant);
                        break;
                    case FeedbackState.Clicked:
                        feedback.OnClicked(instant);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(type), type, null);
                }
            }
        }

        /// <summary>
        /// Clears any internal state from the Selectable (used when disabling).
        /// </summary>
        private void InstantClearState()
        {
            _isPointerInside = false;
            _isPointerDown = false;
            _isSelected = false;

            // Transition instantly to the normal state
            TransitionToState(SelectionState.Normal, true);
        }

        protected override void OnCanvasGroupChanged()
        {
            // Figure out if parent groups allow interaction
            // If no interaction is allowed... then we need
            // to not do that :)
            var groupAllowInteraction = true;
            Transform t = transform;
            while (t != null)
            {
                t.GetComponents(_canvasGroupCache);
                bool shouldBreak = false;
                foreach (var group in _canvasGroupCache)
                {
                    // if the parent group does not allow interaction
                    // we need to break
                    if (group.enabled && !group.interactable)
                    {
                        groupAllowInteraction = false;
                        shouldBreak = true;
                    }

                    // if this is a 'fresh' group, then break
                    // as we should not consider parents
                    if (group.ignoreParentGroups)
                        shouldBreak = true;
                }
                if (shouldBreak)
                    break;

                t = t.parent;
            }

            if (groupAllowInteraction != _canvasAllowsInteraction)
            {
                _canvasAllowsInteraction = groupAllowInteraction;
                TransitionToState(CurrentSelectionState, true);
            }
        }

        // Call from unity if animation properties have changed
        protected override void OnDidApplyAnimationProperties() => TransitionToState(CurrentSelectionState, true);

        protected override void Awake()
        {
            if (!Application.isPlaying)
                return;

            foreach (var feedback in _feedbacks)
                feedback.Initialize(this);
            
            var parent = transform.parent;
            if (parent != null && parent.TryGetComponent(out _parentGroup))
            {
                if (_isSelectable)
                    _parentGroup.RegisterSelectable(this);
            }
        }

        protected override void OnDestroy()
        {
            if (!Application.isPlaying)
                return;

            if (IsSelectable)
                _parentGroup.UnregisterSelectable(this);
        }

        // Select on enable and add to the list.
        protected override void OnEnable()
        {
            // Check to avoid multiple OnEnable() calls for each selectable
            if (_enableCalled)
                return;

            if (_selectableCount == _selectables.Length)
            {
                var temp = new SelectableButton[_selectables.Length * 2];
                Array.Copy(_selectables, temp, _selectables.Length);
                _selectables = temp;
            }

            _currentIndex = _selectableCount;
            _selectables[_currentIndex] = this;
            _selectableCount++;
            _isPointerDown = false;

            if (IsSelectable && _parentGroup.Selected == this)
                OnSelect(null);

            TransitionToState(CurrentSelectionState, true);

            _enableCalled = true;
        }

        // Remove from the list.
        protected override void OnDisable()
        {
            if (!Application.isPlaying)
                return;

            // Check to avoid multiple OnDisable() calls for each selectable
            if (!_enableCalled)
                return;

            _selectableCount--;

            // Update the last elements index to be this index
            _selectables[_selectableCount]._currentIndex = _currentIndex;

            // Swap the last element and this element
            _selectables[_currentIndex] = _selectables[_selectableCount];

            // null out last element.
            _selectables[_selectableCount] = null;

            Deselect();
            InstantClearState();
            base.OnDisable();

            _enableCalled = false;
        }

        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();

            // If our parenting changes figure out if we are under a new CanvasGroup.
            OnCanvasGroupChanged();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus && IsPressed())
                InstantClearState();
        }

        private static Vector3 GetPointOnRectEdge(RectTransform rectTrs, Vector2 dir)
        {
            if (rectTrs == null)
                return Vector3.zero;
            if (dir != Vector2.zero)
                dir /= Mathf.Max(Mathf.Abs(dir.x), Mathf.Abs(dir.y));
            Rect rect = rectTrs.rect;
            dir = rect.center + Vector2.Scale(rect.size, dir * 0.5f);
            return dir;
        }

        #region Initialization
#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reload()
        {
            Array.Clear(_selectables, 0, _selectables.Length);
            _selectableCount = 0;
        }
#endif

        private SelectableButton()
        { }
        #endregion

        #region Editor
#if UNITY_EDITOR
        protected override void OnValidate()
        {
            foreach (var feedback in _feedbacks)
                feedback?.Validate_EditorOnly(this);
            
            // OnValidate can be called before OnEnable, this makes it unsafe to access other components
            // since they might not have been initialized yet.
            if (isActiveAndEnabled)
            {
                // And now go to the right state.
                TransitionToState(CurrentSelectionState, true);
            }
        }

        protected override void Reset()
        {
            base.Reset();
            _navigation.Mode = SelectableNavigation.NavMode.Automatic;
        }
#endif
        #endregion

        #region Internal Types
        /// <summary>
        /// An enumeration of selected states of objects
        /// </summary>
        private enum SelectionState
        {
            /// <summary>
            /// The UI object can be selected.
            /// </summary>
            Normal,

            /// <summary>
            /// The UI object is highlighted.
            /// </summary>
            Highlighted,

            /// <summary>
            /// The UI object is pressed.
            /// </summary>
            Pressed,

            /// <summary>
            /// The UI object is selected
            /// </summary>
            Selected
        }
        #endregion
    }
}