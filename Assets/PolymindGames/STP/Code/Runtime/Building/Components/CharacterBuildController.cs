using PolymindGames.ProceduralMotion;
using PolymindGames.PostProcessing;
using PolymindGames.InputSystem;
using UnityEngine.Events;
using UnityEngine;
using System;

namespace PolymindGames.BuildingSystem
{
    /// <summary>
    /// Handles placing and building objects.
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrderConstants.AfterDefault1)]
    public sealed class CharacterBuildController : CharacterBehaviour, IBuildControllerCC
    {
        [SerializeField]
        private InputContext _buildingContext;

        [SerializeField, SubGroup, SpaceArea]
        private BuildSettings _buildSettings;

        [SerializeField, SubGroup]
        private EffectSettings _effectSettings;

        [SerializeField, SubGroup]
        private EventSettings _eventSettings;

        private BuildingPiece _buildingPiece;
        private bool _isPlacementAllowed;

        /// <summary>
        /// The currently selected building piece.
        /// </summary>
        public BuildingPiece BuildingPiece
        {
            get => _buildingPiece;
            private set
            {
                _buildingPiece = value;
                BuildingPieceChanged?.Invoke(value);
            }
        }

        /// <summary>
        /// The rotation offset for the building piece.
        /// </summary>
        public float RotationOffset { get; set; }

        /// <summary>
        /// Event invoked when building starts.
        /// </summary>
        public event UnityAction BuildingStarted
        {
            add => _eventSettings.OnBuildingStart.AddListener(value);
            remove => _eventSettings.OnBuildingStart.RemoveListener(value);
        }

        /// <summary>
        /// Event invoked when building stops.
        /// </summary>
        public event UnityAction BuildingStopped
        {
            add => _eventSettings.OnBuildingStop.AddListener(value);
            remove => _eventSettings.OnBuildingStop.RemoveListener(value);
        }

        /// <summary>
        /// Event invoked when an object is placed.
        /// </summary>
        public event UnityAction<BuildingPiece> ObjectPlaced
        {
            add => _eventSettings.ObjectPlaced.AddListener(value);
            remove => _eventSettings.ObjectPlaced.RemoveListener(value);
        }

        /// <summary>
        /// Event invoked when the current building piece changes.
        /// </summary>
        public event UnityAction<BuildingPiece> BuildingPieceChanged;

        /// <summary>
        /// Sets the currently selected building piece.
        /// </summary>
        /// <param name="buildingPiece">The building piece to set.</param>
        public void SetBuildingPiece(BuildingPiece buildingPiece)
        {
            // If there's a current building piece and it's not placed, destroy it
            if (_buildingPiece != null)
                Destroy(_buildingPiece.gameObject);

            // Set the new building piece
            BuildingPiece = buildingPiece;
            
            // If a building piece is set and the component is not enabled, invoke the building started event
            if (buildingPiece != null && !enabled)
            {
                StartBuilding();
            }
            // If no building piece is set and the component is enabled, invoke the building stopped event
            else if (buildingPiece == null && enabled)
            {
                StopBuilding();
            }

            // Function to start building
            void StartBuilding()
            {
                InputManager.Instance.PushContext(_buildingContext);
                InputManager.Instance.PushEscapeCallback(ForceEndBuilding);
                _eventSettings.OnBuildingStart.Invoke();
                enabled = true;
            }
            
            // Function to stop building
            void StopBuilding()
            {
                InputManager.Instance.PopContext(_buildingContext);
                InputManager.Instance.PopEscapeCallback(ForceEndBuilding);
                _eventSettings.OnBuildingStop.Invoke();
                enabled = false;
            }
            
            // Function to force end building
            void ForceEndBuilding() => SetBuildingPiece(null);
        }

        /// <summary>
        /// Tries to place the currently selected building piece.
        /// </summary>
        /// <returns>True if the building piece was successfully placed, otherwise false.</returns>
        public bool TryPlaceBuildingPiece()
        {
            // Check if there's a selected building piece and if it can be placed at a valid socket
            if (_buildingPiece != null && _buildingPiece.TryPlace(FindValidSocket()))
            {
                HandleSuccessfulPlacement(_buildingPiece is GroupBuildingPiece);
                return true;
            }
    
            // If placement fails, play invalid place audio and return false
            Character.Audio.PlayClip(_effectSettings.InvalidPlaceAudio, BodyPoint.Torso);
            return false;
        }
        
        // Method to handle successful placement
        private void HandleSuccessfulPlacement(bool createNew)
        {
            var nextPiece = createNew ? Instantiate(_buildingPiece.Definition.Prefab, new Vector3(0f, -1000f, 0f), Quaternion.identity) : null;
            _buildingPiece = null;
            SetBuildingPiece(nextPiece);

            // Play place effects
            if (Character is IFPSCharacter character)
                character.HeadComponents.Shake.AddShake(_effectSettings.CameraShake);
            
            PostProcessingManager.Instance.TryPlayAnimation(this, _effectSettings.CameraEffect);
            _eventSettings.ObjectPlaced.Invoke(_buildingPiece);
        }

        private void Awake() => enabled = false;
        
        #region Placement
        private void LateUpdate()
        {
            var socket = FindValidSocket();
            UpdatePlacement(socket);
        }

        /// <summary>
        /// Updates the placement of the building piece.
        /// </summary>
        /// <param name="socket">The valid socket to snap the building piece to, if available.</param>
        private void UpdatePlacement(Socket socket)
        {
            bool hasPlacementSurface = false;
            var (position, rotation) = socket != null ? GetSocketPlacement(socket) : GetFreePlacement(out hasPlacementSurface);
            _buildingPiece.UpdatePlacement(position, rotation, socket, hasPlacementSurface);
        }

        /// <summary>
        /// Calculates the position and rotation for snapping to a socket.
        /// </summary>
        /// <param name="socket">The socket to snap to.</param>
        /// <returns>A tuple containing the position and rotation for snapping to the socket.</returns>
        private (Vector3, Quaternion) GetSocketPlacement(Socket socket)
        {
            var offset = socket.GetBuildingPieceOffset(_buildingPiece.Definition.ParentGroup.Id);
            Vector3 position = socket.WorldPosition + socket.ParentTransform.TransformVector(offset.PositionOffset);
            Quaternion rotation = socket.ParentTransform.rotation * Quaternion.Euler(offset.RotationOffset);
    
            return (position, rotation);
        }
        
        /// <summary>
        /// Calculates the position and rotation for free placement.
        /// </summary>
        /// <returns>A tuple containing the position and rotation for free placement.</returns>
        private (Vector3, Quaternion) GetFreePlacement(out bool hasPlacementSurface)
        {
            hasPlacementSurface = false;
            
            // Check if there's a valid surface within the build range for free placement
            // If a surface is found, return the hit point and a rotation based on settings
            // Otherwise, return a position based on character's forward direction and a rotation based on settings
            Ray ray = UnityUtility.CachedMainCamera.ViewportPointToRay(Vector3.one * 0.5f);
            int placementMask = BuildingManager.Instance.FreePlacementMask;
            if (Physics.Raycast(ray, out RaycastHit hit, _buildSettings.BuildRange, placementMask, QueryTriggerInteraction.Ignore))
            {
                hasPlacementSurface = true;
                return (hit.point, GetFreeRotation());
            }
            
            Transform characterTrs = Character.transform;
            Vector3 currentPos = characterTrs.position + characterTrs.forward * _buildSettings.BuildRange;
            Vector3 startPos = _buildingPiece.transform.position + new Vector3(0, 0.25f, 0);

            if (Physics.Raycast(startPos, Vector3.down, out hit, 1f, placementMask, QueryTriggerInteraction.Ignore))
            {
                currentPos.y = hit.point.y;
                hasPlacementSurface = true;
            }

            return (currentPos, GetFreeRotation());
        }
        
        /// <summary>
        /// Calculates the rotation for free placement.
        /// </summary>
        /// <returns>The rotation for free placement.</returns>
        private Quaternion GetFreeRotation()
        {
            // Calculate rotation based on rotation offset and character's rotation, if required
            var rotation = Quaternion.Euler(0f, RotationOffset * _buildSettings.RotationSpeed, 0f);
            if (_buildSettings.FollowCharacterRotation)
                rotation *= Character.transform.rotation;

            return rotation;
        }
        
        /// <summary>
        /// Finds the closest valid socket for snapping the building piece to.
        /// </summary>
        /// <returns>The closest valid socket, if found; otherwise, null.</returns>
        private Socket FindValidSocket()
        {
            var headTransform = Character.GetTransformOfBodyPoint(BodyPoint.Head);
            var headPosition = headTransform.position;

            int size = PhysicsUtility.OverlapSphereOptimized(headPosition, _buildSettings.BuildRange, out var colliders, LayerConstants.BuildingMask);

            float closestSocketAngle = float.PositiveInfinity;
            Socket closestSocket = null;

            // Loop through all the building pieces in proximity and calculate which socket is the closest in terms of distance & angle
            for (int i = 0; i < size; i++)
            {
                if (colliders[i].TryGetComponent(out BuildingPiece proximityPiece))
                {
                    Ray viewRay = new Ray(headPosition, headTransform.forward);
                    CheckSockets(proximityPiece.GetSockets(), viewRay, ref closestSocketAngle, ref closestSocket);
                }
            }

            return closestSocket;
        }

        /// <summary>
        /// Checks if a socket is valid for snapping the building piece.
        /// </summary>
        /// <param name="sockets">The sockets to check.</param>
        /// <param name="viewRay">The ray representing the character's view direction.</param>
        /// <param name="bestMatchAngle">The angle to the closest valid socket.</param>
        /// <param name="bestMatchSocket">The closest valid socket.</param>
        private void CheckSockets(ReadOnlySpan<Socket> sockets, Ray viewRay, ref float bestMatchAngle, ref Socket bestMatchSocket)
        {
            // Loop through all sockets, comparing them to the last one that was checked,
            // and find the one with the best match angle
            foreach (var socket in sockets)
            {
                float buildRangeSqr = _buildSettings.BuildRange * _buildSettings.BuildRange;
                if ((socket.WorldPosition - viewRay.origin).sqrMagnitude > buildRangeSqr || !socket.SupportsBuildingPiece(_buildingPiece))
                    continue;

                float angleToSocket = Vector3.Angle(viewRay.direction, socket.WorldPosition - viewRay.origin);
                if (angleToSocket < bestMatchAngle && angleToSocket < _buildSettings.ViewAngleThreshold)
                {
                    bestMatchAngle = angleToSocket;
                    bestMatchSocket = socket;
                }
            }
        }
        #endregion

        #region Internal Types
        [Serializable]
        private struct BuildSettings
        {
            [Tooltip("Should the building piece follow the rotation of the character?")]
            public bool FollowCharacterRotation;

            [Range(0f, 70f)]
            [Tooltip("Max angle for detecting nearby sockets.")]
            public float ViewAngleThreshold;

            [Range(0f, 360)]
            [Tooltip("How fast should the building piece be rotated.")]
            public float RotationSpeed;

            [Range(0f, 10f)]
            [Tooltip("Max building range.")]
            public float BuildRange;
        }
        
        [Serializable]
        private struct EffectSettings
        {
            [Tooltip("The camera effect to be played.")]
            public VolumeAnimationProfile CameraEffect;
            
            [Tooltip("The camera shake effect.")]
            public ShakeData CameraShake;

            [Tooltip("The audio played when placing an object is invalid.")]
            public AudioData InvalidPlaceAudio;
        }

        [Serializable]
        private struct EventSettings
        {
            [SpaceArea]
            [Tooltip("Event invoked when building starts.")]
            public UnityEvent OnBuildingStart;

            [Tooltip("Event invoked when building stops.")]
            public UnityEvent OnBuildingStop;

            [Tooltip("Event invoked when an object is placed.")]
            public UnityEvent<BuildingPiece> ObjectPlaced;
        }
        #endregion
    }
}