using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.Serialization;

namespace PolymindGames.SurfaceSystem
{
    [Flags]
    public enum SurfaceEffectPlayFlags : byte
    {
        None = 0,
        Audio = 1,
        Visual = 2,
        Decal = 4,
        AudioVisual = Audio | Visual,
        All = Audio | Visual | Decal
    }

    /// <summary>
    /// A pair of audio and visual effects to play when an action occurs on the surface.
    /// </summary>
    [Serializable]
    public sealed class SurfaceEffectData
    {
        [Tooltip("Visual Effect")]
        public SurfaceEffect VisualEffect;

        [Tooltip("Decal Effect")]
        public SurfaceEffect DecalEffect;

        [Tooltip("Audio Effect")]
        public AudioData AudioEffect = new(null);
    }
    
    /// <summary>
    /// Definition of a surface type for use in a physics-based character controller.
    /// </summary>
    [CreateAssetMenu(menuName = "Polymind Games/Surfaces/Surface Definition", fileName = "Surface_")]
    public sealed class SurfaceDefinition : DataDefinition<SurfaceDefinition>
    {
        [Serializable]
        private struct EffectEntry
        {
            [BeginGroup]
            public SurfaceEffectType EffectType;

            [FormerlySerializedAs("EffectPair")]
            [IgnoreParent, EndGroup]
            public SurfaceEffectData EffectData;
            
            public static bool operator ==(EffectEntry x, EffectEntry y) => x.EffectType == y.EffectType;
            public static bool operator !=(EffectEntry x, EffectEntry y) => x.EffectType != y.EffectType;
            public readonly bool Equals(EffectEntry other) => EffectType == other.EffectType;
            public override int GetHashCode() => (int)EffectType;
            public override readonly bool Equals(object obj) => obj is EffectEntry entry && entry.EffectType == EffectType;
        }

        [FormerlySerializedAs("VelocityModifier")]
        [SerializeField, Range(0.01f, 2f), SpaceArea]
        [Tooltip("Multiplier applied to the character's velocity when stepping on this surface.")]
        private float _velocityModifier = 1f;

        [FormerlySerializedAs("SurfaceFriction")]
        [SerializeField, Range(0.01f, 1f)]
        [Tooltip("Determines how slippery or rough the surface is, with higher values indicating rougher surfaces.")]
        private float _surfaceFriction = 1f;

        [FormerlySerializedAs("PenetrationResistance")]
        [SerializeField, Range(0f, 1f)]
        [Tooltip("Indicates how resistant the surface is to penetration, with higher values being more resistant.")]
        private float _penetrationResistance = 0.3f;

        [FormerlySerializedAs("Materials")]
        [SerializeField]
        [ReorderableList(HasLabels = false), SpaceArea]
        [Tooltip("The physical materials assigned to this surface, defining its physical properties.")]
        private PhysicsMaterial[] _materials = Array.Empty<PhysicsMaterial>();

        [FormerlySerializedAs("Effects")]
        [SerializeField]
        [ReorderableListExposed(HasLabels = false), IgnoreParent]
        private EffectEntry[] _effects = Array.Empty<EffectEntry>();

        private Dictionary<SurfaceEffectType, SurfaceEffectData> _effectsMap;

        public float VelocityModifier => _velocityModifier;
        public float SurfaceFriction => _surfaceFriction;
        public float PenetrationResistance => _penetrationResistance;
        public PhysicsMaterial[] Materials => _materials;
        
        public bool TryGetEffectPair(SurfaceEffectType effectType, out SurfaceEffectData effectData)
        {
            _effectsMap ??= CreateEffectsDictionary();
            if (_effectsMap.TryGetValue(effectType, out effectData))
                return true;

            return false;
        }

        private Dictionary<SurfaceEffectType, SurfaceEffectData> CreateEffectsDictionary()
        {
            var dict = new Dictionary<SurfaceEffectType, SurfaceEffectData>(_effects.Length);
            foreach (var entry in _effects)
                dict.Add(entry.EffectType, entry.EffectData);

            return dict;
        }

        #region Editor
#if UNITY_EDITOR
        public override void Validate_EditorOnly(in ValidationContext validationContext)
        {
            base.Validate_EditorOnly(in validationContext);

            if (validationContext.Trigger is ValidationTrigger.Refresh)
                CollectionExtensions.RemoveDuplicates(ref _materials);
            
            foreach (var material in _materials)
            {
                if (material == null)
                {
                    Debug.LogWarning($"One of the physic materials on ''{Name}'' is null.", this);
                    return;
                }
            }
        }
#endif
        #endregion
    }
}