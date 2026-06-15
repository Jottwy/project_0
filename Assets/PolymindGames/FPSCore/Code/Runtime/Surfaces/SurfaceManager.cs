using PolymindGames.PoolingSystem;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using UnityEngine;

namespace PolymindGames.SurfaceSystem
{
    /// <summary>
    /// Global surface effects system
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrderConstants.Manager)]
    [CreateAssetMenu(menuName = CreateMenuPath + "Surface Manager", fileName = nameof(SurfaceManager))]
    public sealed partial class SurfaceManager : Manager<SurfaceManager>
    {
        [SerializeField, InLineEditor, NotNull]
        [Tooltip("Default surface definition.")]
        private SurfaceDefinition _defaultSurface;

        [SerializeField, Range(2, 128), Title("Effects")]
        [Tooltip("Size of the effect pool.")]
        private int _effectPoolSize = 4;

        [SerializeField, Range(2, 128)]
        [Tooltip("Capacity of the effect pool.")]
        private int _effectPoolCapacity = 8;

        [SerializeField, Range(2, 128), Title("Decals")]
        [Tooltip("Size of the decal pool.")]
        private int _decalPoolSize = 4;

        [SerializeField, Range(2, 128)]
        [Tooltip("Capacity of the decal pool.")]
        private int _decalPoolCapacity = 16;

        private readonly Dictionary<PhysicsMaterial, SurfaceDefinition> _materialSurfacePairs = new(12);
        private readonly Dictionary<int, SurfaceEffectData> _surfaceEffects = new(32);

        #region Initialization
#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
#else
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#endif
        private static void Init() => LoadOrCreateInstance();

        protected override void OnInitialized()
        {
#if UNITY_EDITOR
            _materialSurfacePairs.Clear();
            _surfaceEffects.Clear();
#endif
            CacheSurfaceDefinitions();

            // Hack: Cheating a bit for now.
            SceneManager.sceneUnloaded += _ => _surfaceEffects.Clear();
        }

        private void CacheSurfaceDefinitions()
        {
            var surfaces = SurfaceDefinition.Definitions;
            foreach (var surface in surfaces)
            {
                foreach (var material in surface.Materials)
                {
                    if (!_materialSurfacePairs.TryAdd(material, surface))
                    {
                        Debug.LogError($"The physic material ''{material.name}'' on {surface.Name} is used by a different surface definition: ''{_materialSurfacePairs[material]}''", surface);
                        return;
                    }
                }
            }
        }
		#endregion

        /// <summary>
        /// Gets the surface definition based on a raycast hit.
        /// </summary>
        /// <param name="hit">The raycast hit information.</param>
        /// <returns>The corresponding surface definition.</returns>
        public SurfaceDefinition GetSurfaceFromHit(in RaycastHit hit)
        {
            var collider = hit.collider;
            var material = collider.sharedMaterial;
            if (material == null && collider.TryGetComponent(out SurfaceIdentity identity))
                return identity.GetSurfaceFromHit(in hit);

            return GetSurfaceFromCollider(collider);
        }

        /// <summary>
        /// Gets the surface definition based on a collision event.
        /// </summary>
        /// <param name="collision">The collision data.</param>
        /// <returns>The corresponding surface definition.</returns>
        public SurfaceDefinition GetSurfaceFromCollision(Collision collision)
        {
            var collider = collision.collider;
            var material = collider.sharedMaterial;
            if (material == null && collider.TryGetComponent(out SurfaceIdentity identity))
                return identity.GetSurfaceFromCollision(collision);

            return GetSurfaceFromCollider(collider);
        }

        /// <summary>
        /// Gets the surface definition associated with a collider.
        /// </summary>
        /// <param name="collider">The collider to check.</param>
        /// <returns>The corresponding surface definition.</returns>
        public SurfaceDefinition GetSurfaceFromCollider(Collider collider)
        {
            return GetSurfaceFromMaterial(collider.sharedMaterial) ?? _defaultSurface;
        }

        /// <summary>
        /// Gets the surface definition associated with a physics material.
        /// </summary>
        /// <param name="material">The physics material to check.</param>
        /// <returns>The corresponding surface definition.</returns>
        public SurfaceDefinition GetSurfaceFromMaterial(PhysicsMaterial material)
        {
            return material != null && _materialSurfacePairs.TryGetValue(material, out var surface) ? surface : null;
        }

        /// <summary>
        /// Spawns an effect at the location of a raycast hit.
        /// </summary>
        /// <param name="hit">The raycast hit data.</param>
        /// <param name="effectType">The type of effect to spawn.</param>
        /// <param name="flags"></param>
        /// <param name="audioVolume">The volume of the audio effect.</param>
        /// <param name="parentEffects">Whether to parent the decal to the hit object.</param>
        /// <returns>The surface definition associated with the hit.</returns>
        public SurfaceDefinition PlayEffectFromHit(in RaycastHit hit, SurfaceEffectType effectType,
            SurfaceEffectPlayFlags flags = SurfaceEffectPlayFlags.All, float audioVolume = 1f, bool parentEffects = false)
        {
            var surface = GetSurfaceFromHit(hit);
            if (TryGetEffect(surface, effectType, out var effectData))
            {
                PlayEffect(effectData, hit.point, Quaternion.LookRotation(hit.normal, Vector3.up), flags, audioVolume, parentEffects ? hit.transform : null);
            }

            return surface;
        }

        /// <summary>
        /// Spawns an effect at the location of a collision.
        /// </summary>
        /// <param name="collision">The collision data.</param>
        /// <param name="effectType">The type of effect to spawn.</param>
        /// <param name="flags"></param>
        /// <param name="audioVolume">The volume of the audio effect.</param>
        /// <param name="parentEffects">Whether to parent the decal to the collision object.</param>
        /// <returns>The surface definition associated with the collision.</returns>
        public SurfaceDefinition PlayEffectFromCollision(Collision collision, SurfaceEffectType effectType,
            SurfaceEffectPlayFlags flags = SurfaceEffectPlayFlags.All, float audioVolume = 1f, bool parentEffects = false)
        {
            var surface = GetSurfaceFromCollision(collision);
            var contact = collision.GetContact(0);
            if (TryGetEffect(surface, effectType, out var effectData))
            {
                PlayEffect(effectData, contact.point, Quaternion.LookRotation(contact.normal), flags, audioVolume, parentEffects ? collision.collider.transform : null);
            }

            return surface;
        }

        /// <summary>
        /// Spawns an effect at a specific position and rotation using a given surface definition.
        /// </summary>
        /// <param name="surface">The surface definition.</param>
        /// <param name="position">The world position of the effect.</param>
        /// <param name="rotation">The rotation of the effect.</param>
        /// <param name="effectType">The type of effect to spawn.</param>
        /// <param name="flags"></param>
        /// <param name="audioVolume">The volume of the audio effect.</param>
        public void PlayEffectFromSurface(SurfaceDefinition surface, in Vector3 position, in Quaternion rotation, SurfaceEffectType effectType,
            SurfaceEffectPlayFlags flags = SurfaceEffectPlayFlags.All, float audioVolume = 1f)
        {
            if (TryGetEffect(surface, effectType, out var effectData))
            {
                PlayEffect(effectData, position, rotation, flags, audioVolume);
            }
        }

        /// <summary>
        /// Plays a surface effect, including audio, visual, and decal effects.
        /// </summary>
        /// <param name="effectData">The effect data to play.</param>
        /// <param name="flags"></param>
        /// <param name="position">The position of the effect.</param>
        /// <param name="rotation">The rotation of the effect.</param>
        /// <param name="volumeMultiplier">The volume multiplier for audio effects.</param>
        /// <param name="parent">An optional parent transform for the effect.</param>
        public void PlayEffect(SurfaceEffectData effectData, in Vector3 position, in Quaternion rotation, SurfaceEffectPlayFlags flags, float volumeMultiplier = 1f, Transform parent = null)
        {
            // Play the audio effect
            if ((flags & SurfaceEffectPlayFlags.Audio) != 0)
                AudioManager.Instance.PlayClip3D(effectData.AudioEffect, position, volumeMultiplier);

            // Play the visual effect
            if ((flags & SurfaceEffectPlayFlags.Visual) != 0 && PoolManager.Instance.TryGet(effectData.VisualEffect, out var visualEffectInstance))
                visualEffectInstance.Play(position, rotation);

            // Play the decal effect
            if ((flags & SurfaceEffectPlayFlags.Decal) != 0 && PoolManager.Instance.TryGet(effectData.DecalEffect, out var decalEffectInstance))
                decalEffectInstance.Play(position, rotation, parent);
        }

        /// <summary>
        /// Tries to retrieve the effect data associated with a surface and effect type.
        /// </summary>
        /// <param name="surface">The surface definition.</param>
        /// <param name="effectType">The type of effect to retrieve.</param>
        /// <param name="effectData">The retrieved effect data, if found.</param>
        /// <returns>True if the effect data was found, false otherwise.</returns>
        private bool TryGetEffect(SurfaceDefinition surface, SurfaceEffectType effectType, out SurfaceEffectData effectData)
        {
            int effectDataId = surface.Id + (int)effectType;
            if (_surfaceEffects.TryGetValue(effectDataId, out effectData))
                return true;

            if (surface.TryGetEffectPair(effectType, out effectData))
            {
                _surfaceEffects.Add(effectDataId, effectData);
                CreateEffectPools(effectData);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Creates object pools for visual and decal effects associated with a surface effect.
        /// </summary>
        /// <param name="effectData">The effect data containing visual and decal effects.</param>
        private void CreateEffectPools(SurfaceEffectData effectData)
        {
            Scene? activeScene = null;
            var poolManager = PoolManager.Instance;

            if (effectData.VisualEffect != null && !poolManager.HasPool(effectData.VisualEffect))
            {
                activeScene = SceneManager.GetActiveScene();
                var pool = new SceneObjectPool<SurfaceEffect>(effectData.VisualEffect, activeScene.Value, PoolCategory.SurfaceEffects, _effectPoolSize, _effectPoolCapacity);
                poolManager.RegisterPool(effectData.VisualEffect, pool);
            }

            if (effectData.DecalEffect != null && !poolManager.HasPool(effectData.DecalEffect))
            {
                activeScene ??= SceneManager.GetActiveScene();
                var pool = new SceneObjectPool<SurfaceEffect>(effectData.DecalEffect, activeScene.Value, PoolCategory.SurfaceDecals, _decalPoolSize, _decalPoolCapacity);
                poolManager.RegisterPool(effectData.DecalEffect, pool);
            }
        }
    }
}