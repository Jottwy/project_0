using UnityEngine.Rendering;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.Editor
{
    using UnityObject = UnityEngine.Object;
    
    /// <summary>
    /// TODO: Delete
    /// Utility class for converting materials between different shaders.
    /// </summary>
    public static class MaterialConvertUtility
    {
        /// <summary>
        /// Converts the properties of a material to match those of a target shader specified in the convert info.
        /// </summary>
        /// <param name="material">The material to be converted.</param>
        /// <param name="conversionInfo">The conversion information containing the target shader and property mappings.</param>
        /// <param name="materialPath"></param>
        /// <exception cref="ArgumentNullException">Thrown if convertInfo is null.</exception>
        public static void ConvertMaterial(Material material, MaterialConversionInfo conversionInfo, string materialPath)
        {
            if (conversionInfo == null)
                throw new ArgumentNullException(nameof(conversionInfo));

            // Create a copy of the original material to read previous properties
            Material prevMaterial = new Material(material);

            // Assign the new shader to the material
            material.shader = conversionInfo.TargetShader;
            ConvertShaderProperties(material, conversionInfo, prevMaterial);
            HandleTextureConversion(material, conversionInfo, materialPath);

            // Mark the material as dirty to ensure changes are saved
            EditorUtility.SetDirty(material);
            
            // Destroy the temporary copy of the original material
            UnityObject.DestroyImmediate(prevMaterial);
        }

        private static void ConvertShaderProperties(Material material, MaterialConversionInfo conversionInfo, Material prevMaterial)
        {
            // Loop through each property and convert it to the target shader
            foreach (var property in conversionInfo.ShaderProperties)
            {
                switch (property.PropertyType)
                {
                    case ShaderPropertyType.Color:
                        material.SetColor(property.TargetProperty, prevMaterial.GetColor(property.OriginalProperty));
                        break;
                    case ShaderPropertyType.Vector:
                        material.SetVector(property.TargetProperty, prevMaterial.GetVector(property.OriginalProperty));
                        break;
                    case ShaderPropertyType.Float:
                        material.SetFloat(property.TargetProperty, prevMaterial.GetFloat(property.OriginalProperty));
                        break;
                    case ShaderPropertyType.Texture:
                        material.SetTexture(property.TargetProperty, prevMaterial.GetTexture(property.OriginalProperty));
                        break;
                    case ShaderPropertyType.Int:
                        material.SetInt(property.TargetProperty, prevMaterial.GetInt(property.OriginalProperty));
                        break;
                    case ShaderPropertyType.Range:
                        material.SetFloat(property.TargetProperty, prevMaterial.GetFloat(property.OriginalProperty));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        private static void HandleTextureConversion(Material material, MaterialConversionInfo conversionInfo, string materialPath)
        {
            foreach (var textureConversion in conversionInfo.TextureConversionInfo)
            {
                if (string.IsNullOrEmpty(textureConversion.TargetTextureSuffix))
                    continue;

                // Hack
                if (materialPath.Contains("Wieldables") || materialPath.Contains("Ammunition"))
                    continue;
                
                materialPath = materialPath.Substring(0, materialPath.Length - 4);
                
                var targetTexturePath = $"{materialPath}{textureConversion.TargetTextureSuffix}.png";
                var targetTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(targetTexturePath);
                material.SetTexture(textureConversion.TargetProperty, targetTexture);
                
                if (!string.IsNullOrEmpty(textureConversion.TextureToDeleteSuffix))
                {
                    var textureToDeletePath = $"{materialPath}{textureConversion.TextureToDeleteSuffix}.png";
                    AssetDatabase.DeleteAsset(textureToDeletePath);
                }
            }
        }
    }

    /// <summary>
    /// Represents a shader property with its type and name mappings between original and target shaders.
    /// </summary>
    public readonly struct ShaderProperty
    {
        public readonly ShaderPropertyType PropertyType;
        public readonly int OriginalProperty;
        public readonly int TargetProperty;

        /// <summary>
        /// Initializes a new instance of the ShaderProperty struct.
        /// </summary>
        /// <param name="propertyType">The type of the shader property.</param>
        /// <param name="originalName">The name of the original property.</param>
        /// <param name="targetName">The name of the target property.</param>
        public ShaderProperty(ShaderPropertyType propertyType, string originalName, string targetName)
        {
            PropertyType = propertyType;
            OriginalProperty = Shader.PropertyToID(originalName);
            TargetProperty = Shader.PropertyToID(targetName);
        }
    }

    public readonly struct TextureConversionInfo
    {
        public readonly int TargetProperty;
        public readonly string TargetTextureSuffix;
        public readonly string TextureToDeleteSuffix;

        public TextureConversionInfo(string propertyName, string targetTextureSuffix, string textureToDeleteSuffix)
        {
            TargetProperty = Shader.PropertyToID(propertyName);
            TargetTextureSuffix = targetTextureSuffix;
            TextureToDeleteSuffix = textureToDeleteSuffix;
        }
    }

    /// <summary>
    /// Contains information required for converting a material to a different shader.
    /// </summary>
    public sealed class MaterialConversionInfo
    {
        public readonly TextureConversionInfo[] TextureConversionInfo;
        public readonly ShaderProperty[] ShaderProperties;
        public readonly Shader TargetShader;

        /// <summary>
        /// Initializes a new instance of the MaterialConvertInfo class.
        /// </summary>
        /// <param name="targetShader">The target shader to which the material will be converted.</param>
        /// <param name="shaderProperties">Array of shader properties that need to be converted.</param>
        /// <param name="textureConversionInfo"></param>
        public MaterialConversionInfo(Shader targetShader, ShaderProperty[] shaderProperties, TextureConversionInfo[] textureConversionInfo)
        {
            TextureConversionInfo = textureConversionInfo ?? Array.Empty<TextureConversionInfo>();
            ShaderProperties = shaderProperties ?? Array.Empty<ShaderProperty>();
            TargetShader = targetShader;
        }
    }
}