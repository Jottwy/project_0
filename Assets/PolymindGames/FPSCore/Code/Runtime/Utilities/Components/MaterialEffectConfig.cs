using UnityEngine;
using System;

namespace PolymindGames
{
    [CreateAssetMenu(menuName = "Polymind Games/Utilities/Material Effect Config", fileName = "MaterialEffect_")]
    public sealed class MaterialEffectConfig : ScriptableObject
    {
        /// <summary>
        /// Specifies the type of effect to apply to the material.
        /// </summary>
        public enum AddType : byte
        {
            [Tooltip("Add the new material effects on top of existing materials.")]
            AddToExistingMaterials = 0,

            [Tooltip("Replace the base materials with new ones.")]
            OverrideExistingMaterials = 1
        }

        /// <summary>
        /// Specifies the type of material handling.
        /// </summary>
        public enum MaterialHandlingType : byte
        {
            [Tooltip("Use a new material.")]
            UseNewMaterial,

            [Tooltip("Modify shader parameters of the material.")]
            ModifyShaderParameters
        }

        [Serializable]
        public sealed class ShaderParameterSettings
        {
            [ReorderableList(elementLabel: "Color Parameter")]
            [Tooltip("Array of shader parameters for colors.")]
            public ColorShaderProperty[] ColorParameters;

            [ReorderableList(elementLabel: "Float Parameter")]
            [Tooltip("Array of shader parameters for floats.")]
            public FloatShaderProperty[] FloatParameters;
        }

        [Serializable]
        public struct ColorShaderProperty
        {
            [Tooltip("The name of the shader parameter.")]
            public string Name;

            [ColorUsage(true, true)]
            [Tooltip("The value of the shader parameter.")]
            public Color Value;
        }
        
        [Serializable]
        public struct FloatShaderProperty
        {
            [Tooltip("The name of the shader parameter.")]
            public string Name;

            [Tooltip("The value of the shader parameter.")]
            public float Value;
        }

        [SerializeField]
        [Tooltip("Enable or disable shadows.")]
        private bool _enableShadows = true;

        [SerializeField]
        [Tooltip("The selected effect type.")]
        private AddType _addMode;

        [SerializeField]
        [Tooltip("The type of material handling.")]
        private MaterialHandlingType _materialHandlingType;

        [SerializeField]
        [Tooltip("The new material to apply.")]
        [ShowIf(nameof(_materialHandlingType), MaterialHandlingType.UseNewMaterial)]
        public Material ReplacementMaterial;

        [SerializeField, IgnoreParent, SpaceArea]
        [Tooltip("Settings for modifying shader parameters.")]
        [ShowIf(nameof(_materialHandlingType), MaterialHandlingType.ModifyShaderParameters)]
        private ShaderParameterSettings _shaderParameters;

        
        /// <summary>
        /// Gets the enable shadows flag.
        /// </summary>
        public bool EnableShadows => _enableShadows;
        
        /// <summary>
        /// Gets the current effect mode.
        /// </summary>
        public AddType AddMode => _addMode;

        /// <summary>
        /// Gets the shader parameter settings.
        /// </summary>
        public ShaderParameterSettings ShaderParameters => _shaderParameters;
    }

}