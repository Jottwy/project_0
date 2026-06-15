using UnityEngine.Rendering;
using System.Linq;
using UnityEngine;
using System;

namespace PolymindGames
{
    /// <summary>
    /// Manages the application of material effects to renderers.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MaterialEffect : MonoBehaviour
    {
        [Tooltip("The default material effect to apply.")]
        [SerializeField]
        private MaterialEffectConfig _defaultEffect;

        [Tooltip("The renderers to apply material effects to.")]
        [EditorButton(nameof(GetAllRenderers)), SpaceArea]
        [SerializeField, ReorderableList(HasLabels = false)]
        private Renderer[] _renderers;

        private MaterialEffectConfig _activeEffect;
        private Material[][] _baseMaterials;


        /// <summary>
        /// Enables the specified material effect.
        /// </summary>
        /// <param name="effect">The material effect to enable.</param>
        public void EnableEffect(MaterialEffectConfig effect = null)
        {
            if (effect == null)
            {
                if (_defaultEffect == null)
                {
                    DisableEffect();
                    return;
                }

                effect = _defaultEffect;
            }

            SetEffect(effect);
        }

        /// <summary>
        /// Disables the currently active material effect.
        /// </summary>
        public void DisableEffect()
        {
            if (_activeEffect == null)
                return;

            for (int i = 0; i < _renderers.Length; i++)
            {
                _renderers[i].sharedMaterials = _baseMaterials[i];
                _renderers[i].shadowCastingMode = ShadowCastingMode.On;
            }

            _activeEffect = null;
        }

        /// <summary>
        /// Sets the specified material effect.
        /// </summary>
        /// <param name="effect">The material effect to set.</param>
        private void SetEffect(MaterialEffectConfig effect)
        {
            if (_activeEffect == effect)
                return;

            _activeEffect = effect;
            _baseMaterials ??= GetBaseMaterials();

            switch (effect.AddMode)
            {
                case MaterialEffectConfig.AddType.AddToExistingMaterials:
                    ApplyStackWithBaseMaterials(effect.ReplacementMaterial, effect.EnableShadows);
                    return;

                case MaterialEffectConfig.AddType.OverrideExistingMaterials:
                    ApplyReplaceBaseMaterials(effect.ReplacementMaterial, effect.EnableShadows);
                    return;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        // private void ApplyParameters(MaterialEffectConfig.FloatShaderProperty[] floatParams, MaterialEffectConfig.ColorShaderProperty[] colorParams)
        // {
        //     for (int i = 0; i < _baseMaterials.Length; i++)
        //     {
        //         for (int j = 0; j < _baseMaterials[i].Length; j++)
        //         {
        //             var material = _baseMaterials[i][j];
        //
        //             foreach (var floatParam in floatParams)
        //                 material.SetFloat(floatParam.Name, floatParam.Value);
        //
        //             foreach (var colorParam in colorParams)
        //                 material.SetColor(colorParam.Name, colorParam.Value);
        //         }
        //     }
        // }

        /// <summary>
        /// Applies the stack-with-base-materials effect mode.
        /// </summary>
        /// <param name="material">The material apply.</param>
        /// <param name="enableShadows"></param>
        private void ApplyStackWithBaseMaterials(Material material, bool enableShadows)
        {
            for (int i = 0; i < _renderers.Length; i++)
            {
                var baseMaterials = _baseMaterials[i];
                var newMaterials = new Material[baseMaterials.Length + 1];

                newMaterials[baseMaterials.Length] = material;
                for (int j = 0; j < baseMaterials.Length; j++)
                    newMaterials[j] = baseMaterials[j];

                _renderers[i].sharedMaterials = newMaterials;
                _renderers[i].shadowCastingMode = enableShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            }
        }

        /// <summary>
        /// Applies the replace-base-materials effect mode.
        /// </summary>
        /// <param name="material">The material to apply.</param>
        /// <param name="enableShadows"></param>
        private void ApplyReplaceBaseMaterials(Material material, bool enableShadows)
        {
            foreach (var rend in _renderers)
            {
                var effectMaterials = new Material[rend.sharedMaterials.Length];

                for (int j = 0; j < effectMaterials.Length; j++)
                    effectMaterials[j] = material;

                rend.sharedMaterials = effectMaterials;
                rend.shadowCastingMode = enableShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            }
        }

        /// <summary>
        /// Retrieves the base materials of the renderers.
        /// </summary>
        /// <returns>The base materials of the renderers.</returns>
        private Material[][] GetBaseMaterials()
        {
            var allMaterials = new Material[_renderers.Length][];

            for (int i = 0; i < allMaterials.Length; i++)
            {
                var sharedMaterials = _renderers[i].sharedMaterials;
                var materials = new Material[sharedMaterials.Length];

                for (int j = 0; j < sharedMaterials.Length; j++)
                    materials[j] = sharedMaterials[j];

                allMaterials[i] = materials;
            }

            return allMaterials;
        }

        #region Editor
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private void GetAllRenderers()
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(this, "GetAllRenderers");
            _renderers = GetRenderers();
#endif
        }

#if UNITY_EDITOR
        private void Reset() => _renderers = GetRenderers();

        private Renderer[] GetRenderers()
        {
            var skinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var meshRenderers = TryGetComponent<LODGroup>(out var lodGroup)
                ? lodGroup.GetLODs()[0].renderers.Cast<MeshRenderer>().ToArray()
                : GetComponentsInChildren<MeshRenderer>(true);

            var renderers = new Renderer[meshRenderers.Length + skinnedRenderers.Length];

            meshRenderers.CopyTo(renderers, 0);
            skinnedRenderers.CopyTo(renderers, meshRenderers.Length);

            return renderers;
        }
#endif
        #endregion
    }
}

/*
namespace PolymindGames
{
    public class MaterialChanger : MonoBehaviour
    {
        [SerializeField]
        private MaterialEffectConfig _info;

        [Tooltip("The renderers to apply material effects to.")]
        [SerializeField, ReorderableList(HasLabels = false)]
        private Renderer[] _renderers;

        private int[] _renderersIds;

        private static readonly Dictionary<int, MaterialSetup> Materials = new();


        public void SetDefaultMaterial() => SetMaterials(false);

        public void SetMaterialWithEffects() => SetMaterials(true);

        public void SetOverrideMaterial(Material material)
        {
            for (int i = 0; i < _renderers.Length; ++i)
            {
                var materialArray = new Material[_renderers[i].sharedMaterials.Length];
                for (int j = 0; j < materialArray.Length; ++j)
                    materialArray[j] = material;
                _renderers[i].materials = materialArray;
            }
        }

        protected virtual void Awake()
        {
            SetupMaterials(_info);
            SetMaterials(false);
        }

        protected virtual void SetMaterials(bool withEffects)
        {
            if (_renderersIds == null)
                return;

            for (int index = 0; index < _renderersIds.Length; ++index)
            {
                if (Materials.TryGetValue(_renderersIds[index], out var materialSetup))
                    _renderers[index].sharedMaterials = withEffects ? materialSetup.MaterialsWithEffects : materialSetup.DefaultMaterials;
            }
        }

        private void SetupMaterials(MaterialEffectConfig config)
        {
            if (_renderers.Length == 0 || config == null)
                return;

            _renderersIds = new int[_renderers.Length];
            for (int i = 0; i < _renderers.Length; i++)
            {
                var sharedMaterials = _renderers[i].sharedMaterials;
                int materialsId = CalculateRendererId(sharedMaterials);

                if (!Materials.ContainsKey(materialsId))
                    Materials.Add(materialsId, CreateSetupForMaterials(sharedMaterials, config.ShaderParameters));

                _renderersIds[i] = materialsId;
            }
        }

        private static MaterialSetup CreateSetupForMaterials(Material[] sharedMaterials, MaterialEffectConfig.ShaderParameterSettings shaderParams)
        {
            var defaultMaterials = new Material[sharedMaterials.Length];
            var materialsWithEffects = new Material[defaultMaterials.Length];

            for (int i = 0; i < sharedMaterials.Length; i++)
            {
                defaultMaterials[i] = sharedMaterials[i];

                Material material = new Material(sharedMaterials[i]);
                material.name += "_WithEffects";

                foreach (var property in shaderParams.ColorParameters)
                    material.SetColor(property.Name, property.Value);

                foreach (var property in shaderParams.FloatParameters)
                    material.SetFloat(property.Name, property.Value);

                materialsWithEffects[i] = material;
            }

            return new MaterialSetup(defaultMaterials, materialsWithEffects);
        }

        private int CalculateRendererId(Material[] materials)
        {
            int rendererId = 0;
            foreach (Material sharedMaterial in materials)
            {
                if (sharedMaterial != null)
                    rendererId += sharedMaterial.GetHashCode() / 2;
                else
                    Debug.LogError("Material is NULL", gameObject);
            }
            return rendererId;
        }

        private readonly struct MaterialSetup
        {
            public readonly Material[] DefaultMaterials;
            public readonly Material[] MaterialsWithEffects;

            public MaterialSetup(Material[] defaultMaterials, Material[] materialsWithEffects)
            {
                DefaultMaterials = defaultMaterials;
                MaterialsWithEffects = materialsWithEffects;
            }
        }
    }
}*/