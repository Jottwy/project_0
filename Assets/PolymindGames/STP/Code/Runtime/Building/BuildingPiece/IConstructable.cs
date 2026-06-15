using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine;
using System;
using System.Collections;

namespace PolymindGames.BuildingSystem
{
    /// <summary>
    /// Interface for constructable objects in the game world.
    /// </summary>
    public interface IConstructable : IMonoBehaviour
    {
        /// <summary>
        /// Gets the associated building piece of this constructable.
        /// </summary>
        BuildingPiece BuildingPiece { get; }

        /// <summary>
        /// Gets a value indicating whether this constructable is fully constructed.
        /// </summary>
        bool IsConstructed { get; }

        /// <summary>
        /// Event triggered when this constructable is fully constructed.
        /// </summary>
        event UnityAction Constructed;

        /// <summary>
        /// Retrieves the build requirements for constructing this object.
        /// </summary>
        /// <returns>An array of build requirements.</returns>
        IReadOnlyList<BuildRequirement> GetBuildRequirements();

        /// <summary>
        /// Attempts to add the specified material to this constructable.
        /// </summary>
        /// <param name="material">The build material to add.</param>
        /// <returns>True if the material was successfully added, false otherwise.</returns>
        bool TryAddMaterial(BuildMaterialDefinition material);
    }

    public sealed class NullConstructable : IConstructable
    {
        private readonly BuildingPiece _buildingPiece;
        
        public GameObject gameObject => _buildingPiece.gameObject;
        public Transform transform => _buildingPiece.transform;
        
        public bool enabled
        {
            get => _buildingPiece.enabled;
            set => _buildingPiece.enabled = value;
        }
        
        public BuildingPiece BuildingPiece => _buildingPiece;
        public bool IsConstructed => true;
        
        public event UnityAction Constructed
        {
            add { }
            remove { } 
        }

        public IReadOnlyList<BuildRequirement> GetBuildRequirements() => Array.Empty<BuildRequirement>();
        public bool TryAddMaterial(BuildMaterialDefinition material) => false;
        public Coroutine StartCoroutine(IEnumerator routine) => _buildingPiece.StartCoroutine(routine);

        public NullConstructable(BuildingPiece buildingPiece)
        {
            _buildingPiece = buildingPiece;
        }
    }
}
