using UnityEngine;
using System;

namespace PolymindGames.BuildingSystem
{
    /// <summary>
    /// Abstract class representing a building piece in the game.
    /// </summary>
    [DisallowMultipleComponent]
    public abstract class BuildingPiece : MonoBehaviour
    {
        /// <summary>
        /// Enumeration representing the state of a building piece.
        /// </summary>
        protected enum BuildingPieceState
        {
            None = 0,
            InPlacementAllowed = 1, // Non-interactable
            InPlacementDenied = 2,  // Non-interactable
            Placed = 4,             // Non-interactable
            Constructed = 3,        // Interactable
        }

        [SerializeField]
        [Tooltip("The definition of this building piece.")]
        private BuildingPieceDefinition _definition;

        [SerializeField, Title("Settings")]
        [Tooltip("The current state of this building piece.")]
        private BuildingPieceState _state;

        [SerializeField]
        [Tooltip("The local bounds of this building piece.")]
        private Bounds _localBounds;

        private MaterialEffect _materialEffect;
        private IConstructable _constructable;
        private IHoverable _hoverable;
        private Collider[] _colliders;

        /// <summary> Gets a value indicating whether this building piece is placed. </summary>
        public bool IsPlaced => _state == BuildingPieceState.Placed || _state == BuildingPieceState.Constructed;

        /// <summary> Gets a value indicating whether this building piece is fully constructed. </summary>
        public bool IsConstructed => _state == BuildingPieceState.Constructed;
        
        /// <summary> Gets the definition of the building piece. </summary>
        public BuildingPieceDefinition Definition => _definition;

        /// <summary> Gets the constructable component of the building piece if it exists. </summary>
        public IConstructable Constructable
        {
            get
            {
                if (_constructable == null)
                {
                    _constructable = GetComponent<IConstructable>();
                    _constructable ??= new NullConstructable(this);
                }
                return _constructable;
            }
        }

        /// <summary> Gets the parent group to which the building piece belongs. </summary>
        public abstract IBuildingPieceGroup ParentGroup { get; }
        
        /// <summary> Gets the building state. </summary>
        protected BuildingPieceState State
        {
            get => _state;
            set => _state = value;
        }

        /// <summary>
        /// Tries to place the building piece.
        /// </summary>
        /// <returns>True if the piece was successfully placed, otherwise false.</returns>
        public abstract bool TryPlace(Socket socket);

        /// <summary>
        /// Retrieves the center position of this building piece.
        /// </summary>
        /// <returns>The center position of this building piece.</returns>
        public abstract Vector3 GetCenter();

        /// <summary>
        /// 
        /// </summary>
        /// <param name="position"></param>
        /// <param name="rotation"></param>
        /// <param name="socket"></param>
        /// <param name="hasSurface"></param>
        public abstract void UpdatePlacement(Vector3 position, Quaternion rotation, Socket socket, bool hasSurface);

        /// <summary>
        /// Gets the read-only span of sockets associated with the building piece.
        /// </summary>
        public abstract ReadOnlySpan<Socket> GetSockets();
        
        /// <summary>
        /// Checks if the building piece has a collider.
        /// </summary>
        /// <param name="col">The collider to check.</param>
        /// <returns>True if the building piece has the specified collider, otherwise false.</returns>
        public bool HasCollider(Collider col) => Array.IndexOf(_colliders, col) != -1;
        
        /// <summary>
        /// Gets the world-space bounds of the building piece.
        /// </summary>
        /// <returns>The world-space bounds of the building piece.</returns>
        public Bounds GetWorldBounds() =>
            new(transform.position + transform.TransformVector(_localBounds.center), _localBounds.size);

        /// <summary>
        /// Gets the local-space bounds of the building piece.
        /// </summary>
        /// <returns>The local-space bounds of the building piece.</returns>
        public Bounds GetLocalBounds() => _localBounds;

        /// <summary>
        /// Sets the state of a building piece and executes corresponding behavior based on the state.
        /// </summary>
        /// <param name="state">The state to set for the building piece.</param>
        protected void SetState(BuildingPieceState state)
        {
            if (state != _state)
                SetState_Internal(state);
        }

        /// <summary>
        /// Sets the state of a building piece and executes corresponding behavior based on the state.
        /// </summary>
        /// <param name="state">The state to set for the building piece.</param>
        private void SetState_Internal(BuildingPieceState state)
        {
            // Update the current state
            var prevState = _state;
            _state = state;
            
            // Determine the behavior based on the provided state
            switch (state)
            {
                // No action needed for the None state
                case BuildingPieceState.None:
                    return;
        
                // Invoke method to handle placement allowed state
                case BuildingPieceState.InPlacementAllowed: SetPlacementAllowedState(prevState);
                    return;
        
                // Invoke method to handle placement denied state
                case BuildingPieceState.InPlacementDenied: SetPlacementDeniedState(prevState);
                    return;

                // Invoke method to handle placed state
                case BuildingPieceState.Placed: SetPlacedState();
                    return;
                
                // Invoke method to handle constructed state
                case BuildingPieceState.Constructed: SetConstructedState();
                    return;
                    
                // Throw an exception for unexpected states
                default: throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }
        }

        /// <summary>
        /// Handles the behavior when placement is allowed.
        /// </summary>
        protected virtual void SetPlacementAllowedState(BuildingPieceState prevState)
        {
            // Small optimization.
            if (prevState != BuildingPieceState.InPlacementDenied)
            {
                EnableColliders(false);
                EnableNavMeshObstacle(false);
            }
            
            _materialEffect.EnableEffect(BuildingManager.Instance.PlacementAllowedMaterialEffect);
        }

        /// <summary>
        /// Handles the behavior when placement is denied.
        /// </summary>
        protected virtual void SetPlacementDeniedState(BuildingPieceState prevState)
        {
            // Small optimization.
            if (prevState != BuildingPieceState.InPlacementAllowed)
            {
                EnableColliders(false);
                EnableNavMeshObstacle(false);
            }
            
            _materialEffect.EnableEffect(BuildingManager.Instance.PlacementDeniedMaterialEffect);
        }

        /// <summary>
        /// Handles the behavior when the piece is placed and still in construction.
        /// </summary>
        protected virtual void SetPlacedState()
        {
            ExcludeCollisions(PhysicsUtility.AllLayers);
            EnableColliders(true);
            EnableNavMeshObstacle(false);

            if (_hoverable != null)
                _hoverable.enabled = false;

            _materialEffect.EnableEffect(BuildingManager.Instance.PlacementAllowedMaterialEffect);
            
            // Avoids playing effects when fading the screen.
            if (Time.timeSinceLevelLoad > 1f)
                _definition.PlaceEffects.PlayAtPosition(transform.position, Quaternion.identity);
        }

        /// <summary>
        /// Handles the behavior when the piece is placed constructed.
        /// </summary>
        protected virtual void SetConstructedState()
        {
            ExcludeCollisions(0);
            
            foreach (var col in _colliders)
                col.enabled = !col.isTrigger;

            EnableNavMeshObstacle(true);
            
            if (_hoverable != null)
                _hoverable.enabled = true;

            _materialEffect.DisableEffect();

            // Avoids playing effects when fading the screen.
            if (Time.timeSinceLevelLoad > 1f)
                _definition.ConstructEffects.PlayAtPosition(transform.position, Quaternion.identity);
        }

        /// <summary>
        /// Enables or disables colliders based on the provided boolean value.
        /// </summary>
        /// <param name="enable">A boolean value indicating whether to enable or disable colliders.</param>
        protected void EnableColliders(bool enable)
        {
            foreach (var col in _colliders)
                col.enabled = enable;
        }
        
        /// <summary>
        /// Enables or disables the attached NavMeshObstacle component.
        /// </summary>
        /// <param name="enable">A boolean value indicating whether to enable or disable the NavMeshObstacle component.</param>
        protected void EnableNavMeshObstacle(bool enable)
        {
            if (TryGetComponent<UnityEngine.AI.NavMeshObstacle>(out var navMeshObstacle))
                navMeshObstacle.enabled = enable;
        }

        /// <summary>
        /// Excludes collisions with the specified layers for all attached colliders.
        /// </summary>
        /// <param name="mask">The LayerMask representing the layers to exclude collisions with.</param>
        protected void ExcludeCollisions(LayerMask mask)
        {
            foreach (var col in _colliders)
                col.excludeLayers = mask;
        }
        
        protected virtual void Awake()
        {
            _materialEffect = GetComponent<MaterialEffect>();
            _colliders = GetComponentsInChildren<Collider>(true);
            _hoverable = GetComponentInChildren<IHoverable>();
            Constructable.Constructed += () => SetState(BuildingPieceState.Constructed);
        }

        protected virtual void Start()
        {
            if (_hoverable != null)
            {
                if (string.IsNullOrEmpty(_hoverable.Title))
                    _hoverable.Title = Definition.Name;
                
                if (string.IsNullOrEmpty(_hoverable.Description))
                    _hoverable.Description = Definition.Description;
            }
            
            SetState_Internal(_state);
        }

        protected virtual void Reset()
        {
            gameObject.layer = LayerConstants.Building;
        }
    }
}