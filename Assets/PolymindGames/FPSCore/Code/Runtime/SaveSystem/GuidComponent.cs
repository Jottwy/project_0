using UnityEngine;
using System;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PolymindGames.SaveSystem
{
    // This component gives a GameObject a stable, non-replicable Globally Unique Identifier.
    // It can be used to reference a specific instance of an object no matter where it is.
    // This can also be used for other systems, such as Save/Load game
    [ExecuteAlways, DisallowMultipleComponent]
    public abstract class GuidComponent : MonoBehaviour, ISerializationCallbackReceiver
    {
        [SerializeField]
        [SerializedGuidDetails(true, false)]
        private SerializedGuid _instanceGuid = new(Guid.NewGuid());

        public SerializedGuid InstanceGuid
        {
            get => _instanceGuid;
            protected set => _instanceGuid = value;
        }

        public void ClearGuid()
        {
            if (_instanceGuid != SerializedGuid.Empty)
                GuidManager.Remove(_instanceGuid.Guid);

            _instanceGuid = SerializedGuid.Empty;
        }

        // When de-serializing or creating this component, we want to either restore our serialized GUID
        // or create a new one.
        private void CreateGuid()
        {
            // if our serialized data is invalid, then we are a new object and need a new GUID
            if (_instanceGuid == SerializedGuid.Empty)
            {
                _instanceGuid = new SerializedGuid(Guid.NewGuid());

#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    // If we are creating a new GUID for a prefab instance, but we have somehow lost our prefab connection
                    // force a save of the modified prefab instance properties
                    if (PrefabUtility.IsPartOfNonAssetPrefabInstance(this))
                        PrefabUtility.RecordPrefabInstancePropertyModifications(this);
                }
#endif
                return;
            }

            // register with the GUID Manager so that other components can access this
            // if registration fails, we probably have a duplicate or invalid GUID, get us a new one.
            while (!GuidManager.Add(this))
                _instanceGuid = new SerializedGuid(Guid.NewGuid());
        }

        // We cannot allow a GUID to be saved into a prefab.
        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
#if UNITY_EDITOR
            if (this == null)
                return;

            // This lets us detect if we are a prefab instance or a prefab asset.
            // A prefab asset cannot contain a GUID since it would then be duplicated when instanced.
            if (UnityUtility.IsAssetOnDisk(this))
                _instanceGuid = SerializedGuid.Empty;
#endif
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize() { }

        protected virtual void Awake()
        {
#if UNITY_EDITOR
            // Similar to on Serialize, but gets called on Copying a Component or Applying a Prefab.
            // At a time that lets us detect what we are.
            if (UnityUtility.IsAssetOnDisk(this))
                _instanceGuid = SerializedGuid.Empty;
            else
#endif
            {
                CreateGuid();
            }
        }

        // Let the manager know we are gone, so other objects no longer find this.
        protected virtual void OnDestroy()
        {
            if (_instanceGuid != SerializedGuid.Empty)
                GuidManager.Remove(_instanceGuid.Guid);
        }

        protected virtual void OnValidate()
        {
#if UNITY_EDITOR
            // Similar to on Serialize, but gets called on Copying a Component or Applying a Prefab.
            // At a time that lets us detect what we are.
            if (UnityUtility.IsAssetOnDisk(this))
                _instanceGuid = SerializedGuid.Empty;
            else
#endif
            {
                CreateGuid();
            }
        }
    }
}