using System.Collections.Generic;
using UnityEngine.Rendering;
using JetBrains.Annotations;
using UnityEngine;
using System;

namespace PolymindGames.Editor
{
    /// <summary>
    /// TODO: Delete
    /// ScriptableObject to store settings for material conversion.
    /// </summary>
    [CreateAssetMenu(menuName = "Polymind Games/Editor/Material Conversion Settings", fileName = "MaterialConversionSettings", order = 0)]
    public sealed class MaterialConversionSettings : ScriptableObject
    {
        /// <summary>
        /// Represents the conversion information for a specific shader.
        /// </summary>
        [Serializable]
        private sealed class ShaderInfo
        {
            [UsedImplicitly]
            public string ConversionName;
            
            [ReorderableList(ListStyle.Lined), IgnoreParent]
            [Tooltip("Array of properties that need to be converted from the original shader to the target shader.")]
            public PropertyInfo[] ShaderProperties;

            [ReorderableList(ListStyle.Lined), IgnoreParent]
            public TexturePropertyInfo[] TextureChangeProperties;

            [Tooltip("Name of the original shader.")]
            public string OriginalShader;

            [Tooltip("Name of the target shader.")]
            public string TargetShader;
        }

        /// <summary>
        /// Represents the conversion information for a specific shader property.
        /// </summary>
        [Serializable]
        private sealed class PropertyInfo
        {
            [Tooltip("Type of the shader property.")]
            public ShaderPropertyType PropertyType;

            [Tooltip("Name of the original shader property.")]
            public string OriginalProperty;

            [Tooltip("Name of the target shader property.")]
            public string TargetProperty;
        }
        
        [Serializable]
        public sealed class TexturePropertyInfo
        {
            public string Property;
            public string TargetTextureSuffix;
            public string TextureToDeleteSuffix;
        }

        [SerializeField, ReorderableList(elementLabel: "Conversion Info")]
        [Tooltip("Array of shader conversion information.")]
        private ShaderInfo[] _convertInfo;

        /// <summary>
        /// Converts the shader conversion information into a dictionary for quick lookups.
        /// </summary>
        /// <returns>Dictionary mapping original shaders to their conversion information.</returns>
        public Dictionary<Shader, MaterialConversionInfo> GetLookup()
        {
            var dict = new Dictionary<Shader, MaterialConversionInfo>(_convertInfo.Length);
            foreach (var convertInfo in _convertInfo)
            {
                var key = Shader.Find(convertInfo.OriginalShader);
                var value = CreateConversionInfo(convertInfo);
                if (key != null && value != null)
                    dict.Add(key, value);
            }

            return dict;
        }

        /// <summary>
        /// Creates a MaterialConvertInfo object from the provided ShaderInfo.
        /// </summary>
        /// <param name="shaderInfo">Shader conversion information.</param>
        /// <returns>A MaterialConvertInfo object containing the target shader and properties.</returns>
        private static MaterialConversionInfo CreateConversionInfo(ShaderInfo shaderInfo)
        {
            var shader = Shader.Find(shaderInfo.TargetShader);
            
            var properties = shaderInfo.ShaderProperties;
            var newProperties = new ShaderProperty[properties.Length];
            for (int i = 0; i < newProperties.Length; i++)
            {
                var property = properties[i]; 
                newProperties[i] = new ShaderProperty(property.PropertyType, property.OriginalProperty, property.TargetProperty);
            }

            var textureProperties = shaderInfo.TextureChangeProperties;
            var newTextureShaderProperties = new TextureConversionInfo[textureProperties.Length];
            for (int i = 0; i < newTextureShaderProperties.Length; i++)
            {
                var property = textureProperties[i]; 
                newTextureShaderProperties[i] = new TextureConversionInfo(property.Property, property.TargetTextureSuffix, property.TextureToDeleteSuffix);
            }

            return new MaterialConversionInfo(shader, newProperties, newTextureShaderProperties);
        }
    }
}