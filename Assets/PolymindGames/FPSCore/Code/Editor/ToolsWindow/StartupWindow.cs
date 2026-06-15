using Toolbox.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    using MessageType = MessageType;
    
    public sealed class StartupWindow : EditorWindow
    {
        private PolymindAssetInfo[] _assetsInfo;
        private Texture2D _polymindIcon;
        private bool _showOnStartup;
        private bool _showActionRequired;

        private const string ShowOnStartupKey = "Show_On_Startup";
        
        [MenuItem("Tools/Polymind Games/Startup", priority = 1000)]
        private static void Init() => GetStartupWindow();

        [InitializeOnLoadMethod]
        private static void Init_Startup()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private static void OnEditorUpdate()
        {
            EditorApplication.update -= OnEditorUpdate;

            // Open your editor window when Unity starts
            if (ShowOnStartup())
            {
                SessionState.SetBool(ShowOnStartupKey, false);
                GetStartupWindow();
            }
        }

        private static bool ShowOnStartup()
        {
            return EditorPrefs.GetBool(ShowOnStartupKey, true) && SessionState.GetBool(ShowOnStartupKey, true);
        }

        private static bool RequiresValidation()
        {
            var assetsInfo = PolymindAssetInfo.GetAll();
            foreach (var assetInfo in assetsInfo)
            {
                if (!assetInfo.AreRequirementsMet())
                    return true;
            }

            return false;
        }

        private static StartupWindow GetStartupWindow()
        {
            var window = GetWindow<StartupWindow>(true);

            const float WindowWidth = 600f;
            const float WindowHeight = 400f;
            
            float x = (Screen.currentResolution.width - WindowWidth) / 2f;
            float y = (Screen.currentResolution.height - WindowHeight) / 2f;
            
            window.position = new Rect(x, y, WindowWidth, WindowHeight);
            window.minSize = new Vector2(WindowWidth, WindowHeight);
            window.maxSize = new Vector2(WindowWidth, WindowHeight);
            
            window.titleContent = new GUIContent("Polymind Startup", Resources.Load<Texture2D>("Icons/Editor_PolymindLogoSmall"));

            return window;
        }
        
        private void OnEnable()
        {
            _polymindIcon = Resources.Load<Texture2D>("Icons/Editor_PolymindLogo");
            _showActionRequired = RequiresValidation();
        }

        private void OnGUI()
        {
            GUILayout.FlexibleSpace();
            
            GUILayout.BeginHorizontal();
            {
                GUILayout.FlexibleSpace(); 
                GUILayout.Box(_polymindIcon, GUIStyle.none, GUILayout.Width(400), GUILayout.Height(200f));
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();
            
            GUILayout.FlexibleSpace();
            ToolboxEditorGui.DrawLine();
            
            GUILayout.BeginHorizontal();
            {
                GUILayout.FlexibleSpace();
                
                if (EditorGUILayout.LinkButton("Discord"))
                    Application.OpenURL(PolymindAssetInfo.DiscordURL);

                if (EditorGUILayout.LinkButton("Support"))
                    Application.OpenURL(PolymindAssetInfo.SupportURL);
                
                if (EditorGUILayout.LinkButton("Youtube"))
                    Application.OpenURL(PolymindAssetInfo.YoutubeURL);
            }
            GUILayout.EndHorizontal();
            
            ToolboxEditorGui.DrawLine();
            
            if (_showActionRequired)
                EditorGUILayout.HelpBox("Action Required!", MessageType.Error);
            
            if (GUILayout.Button("Open Tools Window"))
            {
                ToolsWindow.GetOrCreateToolsWindow();
                Close();
            }

            ToolboxEditorGui.DrawLine();
            
            GUILayout.BeginHorizontal();
            {
                GUILayout.FlexibleSpace();
                EditorPrefs.SetBool(ShowOnStartupKey, EditorGUILayout.Toggle("Show On Startup", EditorPrefs.GetBool(ShowOnStartupKey, true)));
            }
            GUILayout.EndHorizontal();
        }
    }
}
