using System.Text;
using UnityEngine;

namespace PolymindGames.SaveSystem
{
    /// <summary>
    /// Represents a saveable object in the game, which can be serialized and registered for saving purposes.
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class SaveableObject : GuidComponent, IPoolableListener
    {
        [SerializeField] 
        [SerializedGuidDetails(false, false)]
        private SerializedGuid _prefabGuid;
    
        [SerializeField]
#if UNITY_EDITOR
        [OnValueChanged(nameof(IsSaveableChanged))]
#endif
        private bool _isSaveable = true;
    
        [SerializeField, SpaceArea(3f)]
        private SerializedTransformData.SaveFlags _rootSaveFlags =
            SerializedTransformData.SaveFlags.Position | SerializedTransformData.SaveFlags.Rotation;
    
        [SerializeField, NewLabel("Transforms To Save"), ReorderableList(HasLabels = false)]
        [Tooltip("Here you can specify which transforms to save. The corresponding transform of this component will always be implicitly added to the list.")]
        private Transform[] _childrenToSave;
    
        [SerializeField, ReorderableList(HasLabels = false)]
        private Rigidbody[] _rigidbodiesToSave;

        private bool _isRegistered;

        /// <summary>
        /// Gets or sets whether the object is saveable. Automatically registers or unregisters as necessary.
        /// </summary>
        public bool IsSaveable
        {
            get => _isSaveable;
            set
            {
                if (_isSaveable == value)
                    return;
                
                _isSaveable = value;
                if (_isSaveable)
                {
                    RegisterSaveableIfNeeded();
                }
                else
                {
                    UnregisterSaveable();
                }
            }
        }
    
        /// <summary>
        /// Gets or sets the prefab GUID associated with this saveable object.
        /// </summary>
        public SerializedGuid PrefabGuid
        {
            get => _prefabGuid;
            set => _prefabGuid = value;
        }
    
        /// <summary>
        /// Generates the save data for this object, including its prefab GUID, instance GUID, transform, components, and associated data.
        /// </summary>
        /// <param name="pathBuilder">The StringBuilder to use for constructing the path.</param>
        /// <returns>An instance of <see cref="ObjectSaveData"/> containing the serialized data.</returns>
        public ObjectSaveData GenerateSaveData(StringBuilder pathBuilder)
        {
            var cachedTransform = transform;
            return new ObjectSaveData
            {
                PrefabGuid = _prefabGuid,
                InstanceGuid = InstanceGuid,
                Transform = new SerializedTransformData(cachedTransform),
                ComponentData = SerializedComponentData.ExtractFromObject(cachedTransform, pathBuilder),
                AdditionalTransforms = SerializedTransformData.ExtractFromTransforms(_childrenToSave),
                RigidbodyData = SerializedRigidbodyData.ExtractFromRigidbodies(_rigidbodiesToSave)
            };
        }
    
        /// <summary>
        /// Applies data into this object from the provided save data.
        /// </summary>
        /// <param name="saveData">The save data to load from.</param>
        public void ApplySaveData(ObjectSaveData saveData)
        {
            InstanceGuid = saveData.InstanceGuid;
            var cachedTransform = transform;
    
            SerializedTransformData.ApplyToTransform(cachedTransform, saveData.Transform, _rootSaveFlags);
            SerializedTransformData.ApplyToTransforms(_childrenToSave, saveData.AdditionalTransforms);
            SerializedComponentData.ApplyToObject(cachedTransform, saveData.ComponentData);
            SerializedRigidbodyData.ApplyToRigidbodies(_rigidbodiesToSave, saveData.RigidbodyData);
        }
        
        /// <summary>
        /// Registers the object if it is saveable.
        /// </summary>
        void IPoolableListener.OnAcquired() => RegisterSaveableIfNeeded();

        /// <summary>
        /// Unregisters the object from saving.
        /// </summary>
        void IPoolableListener.OnReleased() => UnregisterSaveable();

        /// <summary>
        /// Initializes the object and registers it as saveable if applicable.
        /// </summary>
        private void Start() => RegisterSaveableIfNeeded();
    
        /// <summary>
        /// Unregisters the object from saving when it is destroyed.
        /// </summary>
        protected override void OnDestroy()
        {
            UnregisterSaveable();
            base.OnDestroy();
        }
    
        /// <summary>
        /// Registers this object as saveable if it meets the criteria.
        /// </summary>
        private void RegisterSaveableIfNeeded()
        {
            if (_isRegistered || !Application.isPlaying || !_isSaveable)
                return;
    
            _isRegistered = true;
            if (SceneSaveHandler.TryGetHandler(gameObject.scene, out var handler))
                handler.RegisterSaveable(this);
        }
    
        /// <summary>
        /// Unregisters this object from saving.
        /// </summary>
        private void UnregisterSaveable()
        {
            if (!_isRegistered)
                return;
    
            _isRegistered = false;
            if (SceneSaveHandler.TryGetHandler(gameObject.scene, out var handler))
                handler.UnregisterSaveable(this);
        }
    
#if UNITY_EDITOR
        /// <summary>
        /// Toggles the saveable state in the editor.
        /// </summary>
        private void IsSaveableChanged() => IsSaveable = !_isSaveable;
#endif
    }
}