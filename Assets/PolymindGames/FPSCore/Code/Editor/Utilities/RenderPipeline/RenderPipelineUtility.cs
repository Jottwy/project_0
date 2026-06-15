using System.Runtime.CompilerServices;
using Unity.EditorCoroutines.Editor;
using System.Collections.Generic;
using UnityEditor.Compilation;
using JetBrains.Annotations;
using System.Collections;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.Editor
{
    public enum RenderPipelineType
    {
        BIRP = 0,
        HDRP = 1,
        URP = 2,
    }
    
    [InitializeOnLoad]
    public static class RenderPipelineUtility
    {
        private const string RenderPipelineStepPref = "RenderPipelineStep";
        private const string RenderPipelineFromIndexPref = "RenderPipelineFromIndex";
        private const string RenderPipelineTargetIndexPref = "RenderPipelineTargetIndex";

        static RenderPipelineUtility()
        {
            int conversionStep = SessionState.GetInt(RenderPipelineStepPref, -1); 
            if (conversionStep == -1)
                return;

            var fromPipeline = (RenderPipelineType)SessionState.GetInt(RenderPipelineFromIndexPref, (int)GetRenderingPipeline());
            var targetPipeline = (RenderPipelineType)SessionState.GetInt(RenderPipelineTargetIndexPref, 0);
            SetRenderingPipeline(fromPipeline, targetPipeline, conversionStep);
        }

        /// <summary>
        /// Gets the current rendering pipeline type.
        /// </summary>
        /// <returns>The current rendering pipeline type.</returns>
        [Pure, MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RenderPipelineType GetRenderingPipeline()
        {
#if POLYMIND_GAMES_FPS_HDRP
            return RenderPipelineType.HDRP;
#elif POLYMIND_GAMES_FPS_URP
            return RenderPipelineType.URP;
#else
            return RenderPipelineType.BIRP;
#endif
        }
        
        public static string GetCleanPipelineName(RenderPipelineType pipelineType)
        {
            return pipelineType switch
            {
                RenderPipelineType.BIRP => "Built In RP",
                RenderPipelineType.URP => "Universal RP",
                RenderPipelineType.HDRP => "High Definition RP",
                _ => throw new ArgumentOutOfRangeException(nameof(pipelineType), pipelineType, null)
            };
        }

        /// <summary>
        /// Sets the active rendering pipeline for the project, if the specified pipeline is valid.
        /// </summary>
        /// <param name="pipelineType">The desired rendering pipeline type.</param>
        public static void SetActiveRenderingPipeline(RenderPipelineType pipelineType)
        {
            if (CanChangePipeline(pipelineType))
                SetRenderingPipeline(GetRenderingPipeline(), pipelineType);
        }

        /// <summary>
        /// Checks if the specified rendering pipeline is valid and can be set as the active pipeline.
        /// </summary>
        /// <param name="pipelineType">The rendering pipeline type to be validated.</param>
        /// <returns>True if the specified pipeline is valid; otherwise, false.</returns>
        private static bool CanChangePipeline(RenderPipelineType pipelineType)
        {
            // Check if the application is currently playing
            if (Application.isPlaying)
            {
                Debug.LogWarning("You can only change the active rendering pipeline while not playing.");
                return false;
            }
    
            // Check if the desired pipeline is already active
            if (GetRenderingPipeline() == pipelineType)
            {
                Debug.LogWarning($"The target rendering pipeline ({pipelineType}) is already active.");
                return false;
            }

            return true;
        }
        
        /// <summary>
        /// Sets the rendering pipeline for the project and performs necessary updates.
        /// The process is split into multiple steps to handle compilation and asset modifications.
        /// </summary>
        /// <param name="fromPipeline">The current rendering pipeline type.</param>
        /// <param name="targetPipeline">The target rendering pipeline type.</param>
        /// <param name="conversionStep">The current step of the conversion process.</param>
        private static void SetRenderingPipeline(RenderPipelineType fromPipeline, RenderPipelineType targetPipeline, int conversionStep = 0)
        {
            // Reset the render pipeline step preference
            SetNextStepPref(-1);
            SetFromAndTargetPrefs(fromPipeline, targetPipeline);

            switch (conversionStep)
            {
                case 0: Part1(fromPipeline, targetPipeline);
                    break;
                case 1: EditorCoroutineUtility.StartCoroutineOwnerless(Part2(fromPipeline, targetPipeline));
                    break;
                case 2: Part3(fromPipeline, targetPipeline);
                    break;
                default:
                    return;
            }

            static void Part1(RenderPipelineType fromPipeline, RenderPipelineType targetPipeline)
            {
                // Remove dependencies of the old pipeline
                var dependenciesToAdd = GetDependenciesForPipeline(targetPipeline);
                ProjectModificationUtility.ModifyDependencies(dependenciesToAdd, null);
                
                AssetDatabaseUtility.DeleteAssetPathsContainingString("Assets/PolymindGames", "_" + fromPipeline);
                ClearComponentsForPipeline(fromPipeline);
                SaveAssetsAndFreeMemory();

                SetNextStepPref(1);
                CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.None);
            }

            static IEnumerator Part2(RenderPipelineType fromPipeline, RenderPipelineType targetPipeline)
            {
                // Convert all materials to the target rendering pipeline
                yield return null;
                yield return null;
                yield return null;

                string symbolToRemove = GetDefineSymbolForPipeline(fromPipeline);
                if (symbolToRemove != null)
                    ProjectModificationUtility.ModifyDefineSymbol(symbolToRemove, false);
                
                string symbolToAdd = GetDefineSymbolForPipeline(fromPipeline);
                if (symbolToAdd != null)
                    ProjectModificationUtility.ModifyDefineSymbol(symbolToAdd, true);
                
                RenderPipelineMaterialUtility.ConvertAllMaterialsAtPath(targetPipeline);

                SaveAssetsAndFreeMemory();
                ImportPackagesForRenderPipeline(targetPipeline);
                SaveAssetsAndFreeMemory();

                // Remove dependencies of the old pipeline
                var dependenciesToRemove = GetDependenciesForPipeline(fromPipeline);
                ProjectModificationUtility.ModifyDependencies(null, dependenciesToRemove);

                ReimportShaderGraphs();

                // Log success message
                Debug.Log($"The project has been successfully converted to the {targetPipeline} Render Pipeline.");
                
                SetNextStepPref(2);
                CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
            }
            
            static void Part3(RenderPipelineType fromPipeline, RenderPipelineType targetPipeline)
            {
                Debug.Log($"The project has been successfully converted to the {targetPipeline} Render Pipeline. It's recommend to restart the editor.");
                SetNextStepPref(-1);
            }
            
            static void SaveAssetsAndFreeMemory()
            {
                AssetDatabase.SaveAssets();
                GC.Collect();
                EditorUtility.UnloadUnusedAssetsImmediate();
                AssetDatabase.Refresh();
            }

            static void SetNextStepPref(int i)
            {
                SessionState.SetInt(RenderPipelineStepPref, i);
            }
            
            static void SetFromAndTargetPrefs(RenderPipelineType fromPipeline, RenderPipelineType targetPipeline)
            {
                SessionState.SetInt(RenderPipelineFromIndexPref, (int)fromPipeline);
                SessionState.SetInt(RenderPipelineTargetIndexPref, (int)targetPipeline);
            }
        }

        private static void ReimportShaderGraphs()
        {
            var shaderGraphs = AssetDatabaseUtility.FindAllAssetsWithExtension(".shadergraph", new [] { "Assets/PolymindGames" });
            foreach (var shaderGraphPath in shaderGraphs)
            {
                AssetDatabase.ImportAsset(shaderGraphPath, ImportAssetOptions.ForceUpdate);
            }
        }

        /// <summary>
        /// Handles the destruction of specific pipeline-related components.
        /// </summary>
        private static void ClearComponentsForPipeline(RenderPipelineType pipelineType)
        {
            if (pipelineType == RenderPipelineType.BIRP)
                return;
            
            // Find all prefabs in the specified folder
            var guids = AssetDatabase.FindAssets("t:prefab", new[] { "Assets/PolymindGames" });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefabInstance = PrefabUtility.LoadPrefabContents(path);

                if (prefabInstance == null)
                    continue;
                
                bool changed = pipelineType switch
                {
                    RenderPipelineType.HDRP => ClearAllHDRPComponents(prefabInstance),
                    RenderPipelineType.URP => ClearAllURPComponents(prefabInstance),
                    _ => false
                };
                    
                // Save changes if any components were removed
                if (changed)
                {
                    PrefabUtility.SaveAsPrefabAsset(prefabInstance, path);
                }
                    
                PrefabUtility.UnloadPrefabContents(prefabInstance);
            }
        }

        private static bool ClearAllURPComponents(GameObject prefab)
        {
            bool changed = false;
            
#if POLYMIND_GAMES_FPS_URP
            var lightDataComponents = prefab.GetComponentsInChildren<UnityEngine.Rendering.Universal.UniversalAdditionalLightData>();
            foreach (var lightData in lightDataComponents)
            {
                UnityEngine.Object.DestroyImmediate(lightData, true);
                changed = true;
            }
                        
            var cameraDataComponents = prefab.GetComponentsInChildren<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            foreach (var cameraData in cameraDataComponents)
            {
                UnityEngine.Object.DestroyImmediate(cameraData, true);
                changed = true;
            }
                        
            var volumeComponents = prefab.GetComponentsInChildren<Volume>();
            foreach (var volumeComponent in volumeComponents)
            {
                UnityEngine.Object.DestroyImmediate(volumeComponent, true);
                changed = true;
            }
#endif

            return changed;
        }
        
        private static bool ClearAllHDRPComponents(GameObject prefab)
        {
            bool changed = false;

#if POLYMIND_GAMES_FPS_HDRP
            var lightDataComponents = prefab.GetComponentsInChildren<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
            foreach (var lightData in lightDataComponents)
            {
                UnityEngine.Object.DestroyImmediate(lightData, true);
                changed = true;
            }
                        
            var cameraDataComponents = prefab.GetComponentsInChildren<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
            foreach (var cameraData in cameraDataComponents)
            {
                UnityEngine.Object.DestroyImmediate(cameraData, true);
                changed = true;
            }
                        
            var volumeComponents = prefab.GetComponentsInChildren<Volume>();
            foreach (var volumeComponent in volumeComponents)
            {
                UnityEngine.Object.DestroyImmediate(volumeComponent, true);
                changed = true;
            }

            var reflectionDataComponents = prefab.GetComponentsInChildren<UnityEngine.Rendering.HighDefinition.HDAdditionalReflectionData>();
            foreach (var reflectionData in reflectionDataComponents)
            {
                UnityEngine.Object.DestroyImmediate(reflectionData, true);
                changed = true;
            }
#endif

            return changed;
        }

        /// <summary>
        /// Imports packages required for the specified render pipeline type.
        /// </summary>
        /// <param name="pipelineType">The render pipeline type for which packages are to be imported.</param>
        private static void ImportPackagesForRenderPipeline(RenderPipelineType pipelineType)
        {
            // Get packages for the specified render pipeline type and import them
            var packages = GetPackagesForPipeline(pipelineType);
            foreach (var package in packages)
                AssetDatabase.ImportPackage(package, false);
        }

        /// <summary>
        /// Retrieves packages required for the specified render pipeline type.
        /// </summary>
        /// <param name="pipelineType">The render pipeline type for which packages are to be retrieved.</param>
        /// <returns>A list containing paths to packages required for the specified render pipeline type.</returns>
        private static List<string> GetPackagesForPipeline(RenderPipelineType pipelineType)
        {
            // Find all packages under the specified folder
            var packages = AssetDatabaseUtility.FindAllPackages("Assets/PolymindGames");
            
            // Filter packages based on the render pipeline type
            for (int i = packages.Count - 1; i >= 0; i--)
            {
                if (!packages[i].Contains(pipelineType.ToString(), StringComparison.CurrentCultureIgnoreCase))
                {
                    packages.RemoveAt(i);
                }
            }
            
            packages.Reverse();
            return packages;
        }
        
        /// <summary>
        /// Gets the dependencies for the specified rendering pipeline.
        /// </summary>
        /// <param name="pipelineType">The selected rendering pipeline type.</param>
        /// <returns>An array of dependencies for the specified rendering pipeline.</returns>
        private static string[] GetDependenciesForPipeline(RenderPipelineType pipelineType) => pipelineType switch
        {
            RenderPipelineType.HDRP => new [] { "com.unity.render-pipelines.high-definition", "com.unity.render-pipelines.high-definition-config" },
            RenderPipelineType.URP => new [] { "com.unity.render-pipelines.universal", "com.unity.render-pipelines.universal-config" },
            RenderPipelineType.BIRP => new [] { "com.unity.postprocessing" },
            _ => throw new ArgumentOutOfRangeException(nameof(pipelineType), pipelineType, null)
        };

        private static string GetDefineSymbolForPipeline(RenderPipelineType pipelineType) => pipelineType switch
        {
            RenderPipelineType.HDRP => "POLYMIND_GAMES_FPS_HDRP",
            RenderPipelineType.URP => "POLYMIND_GAMES_FPS_URP",
            _ => null
        };
    }
}
