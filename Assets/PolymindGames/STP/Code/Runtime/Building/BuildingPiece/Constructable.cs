using System.Collections.Generic;
using PolymindGames.SaveSystem;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.BuildingSystem
{
    /// <summary>
    /// Simple constructable object in the game world.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BuildingPiece))]
    public class Constructable : MonoBehaviour, ISaveableComponent, IConstructable
    {
        [DisableInPlayMode]
        [SerializeField, ReorderableList(ListStyle = ListStyle.Boxed), IgnoreParent]
        [Tooltip("The build requirements for constructing this object.")]
        private List<BuildRequirement> _requirements;

        private BuildingPiece _buildingPiece;
        
        /// <summary>
        /// Gets a value indicating whether this constructable is fully constructed.
        /// </summary>
        public bool IsConstructed => _requirements.Count == 0;

        /// <summary>
        /// Gets the associated building piece of this constructable.
        /// </summary>
        public BuildingPiece BuildingPiece => _buildingPiece;

        /// <summary>
        /// Event triggered when this constructable is fully constructed.
        /// </summary>
        public event UnityAction Constructed;

        /// <summary>
        /// Retrieves the build requirements for constructing this object.
        /// </summary>
        /// <returns>An array of build requirements.</returns>
        public IReadOnlyList<BuildRequirement> GetBuildRequirements() => _requirements;

        /// <summary>
        /// Attempts to add the specified material to this constructable.
        /// </summary>
        /// <param name="material">The build material to add.</param>
        /// <returns>True if the material was successfully added, false otherwise.</returns>
        public bool TryAddMaterial(BuildMaterialDefinition material)
        {
            if (IsConstructed || material == null)
                return false;

            if (!BuildingPiece.IsCollidingWithCharacters() && TryAddToRequirements(material))
            {
                OnMaterialAdded(material);
                if (_requirements.Count == 0)
                    OnConstructed();

                return true;
            }

            return false;
        }

        /// <summary>
        /// Invoked when a material is successfully added to this constructable.
        /// </summary>
        /// <param name="material">The material that was added.</param>
        protected virtual void OnMaterialAdded(BuildMaterialDefinition material)
        {
            AudioManager.Instance.PlayClip3D(material.UseAudio, transform.position);
        }

        /// <summary>
        /// Invoked when this constructable is fully constructed.
        /// </summary>
        protected virtual void OnConstructed() => Constructed?.Invoke();
        
        /// <summary>
        /// Initializes the constructable component by retrieving the associated building piece.
        /// </summary>
        protected virtual void Awake()
        {
            _buildingPiece = GetComponent<BuildingPiece>();
        }
        
        private bool TryAddToRequirements(BuildMaterialDefinition material)
        {
            for (int i = 0; i < _requirements.Count; i++)
            {
                var requirement = _requirements[i];

                if (!requirement.IsCompleted() && requirement.BuildMaterialId == material.Id)
                {
                    int newCount = Mathf.Min(requirement.CurrentAmount + 1, requirement.RequiredAmount);
                    _requirements[i] = new BuildRequirement(requirement.BuildMaterialId, requirement.RequiredAmount, newCount);

                    CheckForCompletion();
                    return true;
                }
            }

            return false;
        }

        // private int TryAddToRequirements(BuildMaterialDefinition material, int count)
        // {
        //     for (int i = 0; i < _requirements.Count; i++)
        //     {
        //         var requirement = _requirements[i];
        //
        //         if (!requirement.IsCompleted() && requirement.BuildMaterialId == material.Id)
        //         {
        //             int newCount = Mathf.Min(requirement.CurrentAmount + Mathf.Abs(count), requirement.RequiredAmount);
        //             _requirements[i] = new BuildRequirement(requirement.BuildMaterialId, requirement.RequiredAmount, newCount);
        //             CheckForCompletion();
        //             return newCount + requirement.CurrentAmount;
        //         }
        //     }
        //
        //     return 0;
        // }

        private void CheckForCompletion()
        {
            for (int i = 0; i < _requirements.Count; i++)
            {
                if (!_requirements[i].IsCompleted())
                    return;
            }

            // On complete
            _requirements.Clear();
            _requirements.TrimExcess();
        }

    #if UNITY_EDITOR
        /// <summary>
        /// Resets the constructable component by adding a FreeBuildingPiece if no BuildingPiece is found.
        /// </summary>
        protected virtual void Reset()
        {
            if (!gameObject.HasComponent<BuildingPiece>())
                gameObject.AddComponent<FreeBuildingPiece>();
        }
    #endif

        #region Save & Load
        void ISaveableComponent.LoadMembers(object data) => _requirements = (List<BuildRequirement>)data;
        object ISaveableComponent.SaveMembers() => _requirements;
        #endregion
    }
}