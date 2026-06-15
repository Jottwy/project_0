using UnityEditor.SceneManagement;
using JetBrains.Annotations;
using Toolbox.Editor;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    using MessageType = MessageType;

    [UsedImplicitly]
    public sealed class ProjectPage : RootToolPage
    {
        private static readonly GUILayoutOption[] _imageLayoutOptions =
        {
            GUILayout.Width(ImageSize), GUILayout.Height(ImageSize)
        };

        private static readonly GUILayoutOption[] _assetInfoLayoutOptions =
        {
            GUILayout.Height(ImageSize + 12f)
        };

        private static readonly string[] _renderPipelineNames = 
        {
            RenderPipelineUtility.GetCleanPipelineName(RenderPipelineType.BIRP),
            RenderPipelineUtility.GetCleanPipelineName(RenderPipelineType.HDRP),
            RenderPipelineUtility.GetCleanPipelineName(RenderPipelineType.URP),
        };
        
        private const float ImageSize = 256f;

        private PolymindAssetInfo[] _assetsInfo;
        private Vector2 _assetsScrollPosition;
        private int _selectedInfo;

        public override string DisplayName => "Project";
        public override int Order => -1;
        public override bool IsCompatibleWithObject(Object unityObject) => unityObject == null;

        public override void DrawContent()
        {
            DrawAssetsInfo();
            GUILayout.FlexibleSpace();
            ToolboxEditorGui.DrawLine();
            DrawRenderPipelines();
            DrawValidateFiles();
        }

        private void DrawAssetsInfo()
        {
            _assetsInfo ??= PolymindAssetInfo.GetAll();

            GUILayout.Label(_assetsInfo.Length > 0
                ? $"Imported Templates ({_assetsInfo.Length})"
                : "No imported templates found, this should not happen.", GUIStyles.Title);

            _assetsScrollPosition = EditorGUILayout.BeginScrollView(_assetsScrollPosition);

            foreach (var assetInfo in _assetsInfo)
            {
                GUILayout.BeginVertical(EditorStyles.helpBox, _assetInfoLayoutOptions);
                DrawAssetInfo(assetInfo);
                GUILayout.EndVertical();
            }

            EditorGUILayout.EndScrollView();
        }

        private static void DrawRenderPipelines()
        {
            EditorGUILayout.HelpBox("Here's the best place to change the active render pipeline for any Polymind Games template.", MessageType.None);

            EditorGUILayout.BeginHorizontal();

            int current = (int)RenderPipelineUtility.GetRenderingPipeline();
            int selected = GUILayout.Toolbar(current, _renderPipelineNames);

            if (selected != current)
            {
                if (EditorUtility.DisplayDialog("Change Render Pipeline",
                    $"Are you sure you want to change the render pipeline to {_renderPipelineNames[selected]}? You might want to back up your project.",
                    "Ok", "Cancel"))
                {
                    RenderPipelineUtility.SetActiveRenderingPipeline((RenderPipelineType)selected);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawValidateFiles()
        {
            GUILayout.BeginHorizontal();
            {
                bool requiresValidation = RequiresValidation();

                const string Prefix = "Action Required: ";
                const string Message =
                    "Click after importing/removing a Polymind template to ensure compatibility between templates like HQ FPS and STP.";
                
                EditorGUILayout.HelpBox(requiresValidation ? Prefix + Message : Message, requiresValidation ? MessageType.Error : MessageType.None);

                if (GUILayout.Button("Validate Files", GUILayout.Width(300f)))
                {
                    if (EditorUtility.DisplayDialog("Validate Files",
                            $"Are you sure you want to validate the files? This includes updating the player prefabs, data definitions (items, surfaces etc.) and others.",
                            "Ok", "Cancel"))
                    {
                        AssetDatabaseUtility.ValidateAllAssets();

                        foreach (var assetInfo in _assetsInfo)
                        {
                            assetInfo.RequiresValidation = false;
                            EditorUtility.SetDirty(assetInfo);
                        }
                    }
                }
            }
            GUILayout.EndHorizontal();
        }

        private bool RequiresValidation()
        {
            _assetsInfo ??= PolymindAssetInfo.GetAll();
            return _assetsInfo.Any(info => info.RequiresValidation);
        }

        private static void DrawAssetInfo(PolymindAssetInfo assetInfo)
        {
            GUILayout.BeginHorizontal();
            {
                GUILayout.Box(assetInfo.Icon, _imageLayoutOptions);

                GUILayout.BeginVertical();
                {
                    EditorGUILayout.Space();
                    GUILayout.Label($"{assetInfo.AssetName} | v{assetInfo.VersionStr}", GUIStyles.LargeTitleLabel);
                    ToolboxEditorGui.DrawLine();
                    GUILayout.Label(assetInfo.Description, GUIStyles.BoldMiniGreyLabel);

                    GUILayout.Space(20f);

                    GUILayout.Label("Getting Started:");
                    GUILayout.Label("- To quickly create your own scene that works perfectly\nwith this asset, you can duplicate the Prototype scene.\n- In the meantime, you can try out one of these scenes:", GUIStyles.BoldMiniGreyLabel);
                    ToolboxEditorGui.DrawLine();

                    GUILayout.BeginHorizontal();
                    {
                        foreach (var scene in assetInfo.Scenes)
                        {
                            if (scene.CanBeLoaded && EditorGUILayout.LinkButton(scene.SceneName))
                                EditorSceneManager.OpenScene(scene.ScenePath);
                        }
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.Space(6f);
                    ToolboxEditorGui.DrawLine();

                    GUILayout.FlexibleSpace();
                    ToolboxEditorGui.DrawLine();

                    GUILayout.BeginHorizontal();
                    {
                        if (EditorGUILayout.LinkButton("Discord"))
                            Application.OpenURL(PolymindAssetInfo.DiscordURL);

                        if (EditorGUILayout.LinkButton("Support"))
                            Application.OpenURL(PolymindAssetInfo.SupportURL);

                        if (EditorGUILayout.LinkButton("Youtube"))
                            Application.OpenURL(PolymindAssetInfo.YoutubeURL);
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    {
                        if (EditorGUILayout.LinkButton("Documentation"))
                            Application.OpenURL(assetInfo.DocsUrl);

                        if (EditorGUILayout.LinkButton("Store Page"))
                            Application.OpenURL(assetInfo.StoreUrl);

                        foreach (var extraSite in assetInfo.ExtraSites)
                        {
                            if (EditorGUILayout.LinkButton(extraSite.Name))
                                Application.OpenURL(extraSite.Url);
                        }
                    }
                    GUILayout.EndHorizontal();

                    EditorGUILayout.Space();
                }
                GUILayout.EndVertical();
            }
            GUILayout.EndHorizontal();
        }
    }
}