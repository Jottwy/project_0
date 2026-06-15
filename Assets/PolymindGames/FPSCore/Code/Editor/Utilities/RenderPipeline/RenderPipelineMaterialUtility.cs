using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using System;

#if POLYMIND_GAMES_FPS_URP
using UnityEditor.Rendering.Universal;
#endif

namespace PolymindGames.Editor
{
    /// <summary>
    /// Utility class for managing materials across different render pipelines.
    /// </summary>
    public static class RenderPipelineMaterialUtility
    {
        private const string ConvertMaterialsToHdrpMenu = "Edit/Rendering/Materials/Convert All Built-in Materials to HDRP";

        /// <summary>
        /// Converts all materials in the project to the specified render pipeline type.
        /// </summary>
        /// <param name="targetPipelineType">The render pipeline type to convert materials to.</param>
        /// <param name="path"></param>
        public static void ConvertAllMaterialsAtPath(RenderPipelineType targetPipelineType, string path = "Assets/PolymindGames")
        {
            switch (targetPipelineType)
            {
                case RenderPipelineType.BIRP: ConvertMaterialsToBuiltIn(path); break;
                case RenderPipelineType.HDRP: ConvertMaterialsToHdrp(); break;
                case RenderPipelineType.URP: ConvertMaterialsToUrp(); break;
                default: throw new ArgumentOutOfRangeException(nameof(targetPipelineType), targetPipelineType, null);
            }
        }

        /// <summary>
        /// Converts all materials in the project to HDRP.
        /// </summary>
        private static void ConvertMaterialsToHdrp()
        {
            EditorApplication.ExecuteMenuItem(ConvertMaterialsToHdrpMenu);
        }
        
        /// <summary>
        /// Converts all materials in the project to URP.
        /// </summary>
        private static void ConvertMaterialsToUrp()
        {
#if POLYMIND_GAMES_FPS_URP
            Converters.RunInBatchMode(
                ConverterContainerId.BuiltInToURP
                , new List<ConverterId> {
                    ConverterId.Material,
                    ConverterId.ReadonlyMaterial
                }
                , ConverterFilter.Inclusive
            );
#endif
        }
        
        /// <summary>
        /// Converts all materials in the project to the Built-in Render Pipeline.
        /// </summary>
        private static void ConvertMaterialsToBuiltIn(string path)
        {
            var settings = Resources.Load<MaterialConversionSettings>("Editor/MaterialConversionSettings");
            if (settings == null)
            {
                Debug.LogError("No convert settings found.");
                return;
            }
            
            ConvertAllMaterials(settings, path);
            Resources.UnloadAsset(settings);
        }

        private static void ConvertAllMaterials(MaterialConversionSettings settings, string path)
        {
            var materialGuids = AssetDatabase.FindAssets($"t:{nameof(Material)}", new[] { path });
            var lookup = settings.GetLookup();
            
            // Get all materials in the project
            foreach (var materialGuid in materialGuids)
            {
                var materialPath = AssetDatabase.GUIDToAssetPath(materialGuid);
                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);

                if (material == null)
                    continue;
                
                if (lookup.TryGetValue(material.shader, out var convertInfo))
                    MaterialConvertUtility.ConvertMaterial(material, convertInfo, materialPath);
            }

            // Refresh the AssetDatabase to apply changes
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }
}