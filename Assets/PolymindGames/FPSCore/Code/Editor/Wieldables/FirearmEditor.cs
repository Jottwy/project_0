using PolymindGames.Editor;
using Toolbox.Editor;
using UnityEditor;

namespace PolymindGames.WieldableSystem.Editor
{
    [CustomEditor(typeof(Firearm))]
    public sealed class FirearmEditor : WieldableEditor
    {
        private IDrawablePanel[] _componentDrawers;
        private Firearm _firearm;
        private bool _componentsEnabled;
        
        private const string FoldoutLabel = "<b>Components</b>";
        private const string ComponentsFoldoutKey = "Firearm.ComponentsEnabled";
        
        protected override void DrawSubTypeInspector()
        {
            base.DrawSubTypeInspector();

            EditorGUILayout.Space();
            _componentsEnabled = EditorGUILayout.Foldout(_componentsEnabled, FoldoutLabel, true, GUIStyles.Foldout);
            if (_componentsEnabled)
            {
                _componentDrawers ??= CreateComponentDrawers(_firearm);
                foreach (var drawer in _componentDrawers)
                {
                    ToolboxEditorGui.DrawLine();
                    drawer.Draw();
                }
            }
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            _firearm = (Firearm)target;
            _componentsEnabled = SessionState.GetBool(ComponentsFoldoutKey, true);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            SessionState.SetBool(ComponentsFoldoutKey, _componentsEnabled);
        }

        private static IDrawablePanel[] CreateComponentDrawers(Firearm firearm)
        {
            return new IDrawablePanel[]
            {
                new FirearmComponentPanelDrawer<FirearmTriggerBehaviour>(firearm, FirearmComponentType.Trigger),
                new FirearmComponentPanelDrawer<FirearmAimHandlerBehaviour>(firearm, FirearmComponentType.AimHandler),
                new FirearmComponentPanelDrawer<FirearmFireSystemBehaviour>(firearm, FirearmComponentType.FireSystem),
                new FirearmComponentPanelDrawer<FirearmImpactEffectBehaviour>(firearm, FirearmComponentType.ImpactEffect),
                new FirearmComponentPanelDrawer<FirearmRecoilManagerBehaviour>(firearm, FirearmComponentType.RecoilManager),
                new FirearmComponentPanelDrawer<FirearmAmmoProviderBehaviour>(firearm, FirearmComponentType.AmmoProvider),
                new FirearmComponentPanelDrawer<FirearmReloadableMagazineBehaviour>(firearm, FirearmComponentType.ReloadableMagazine),
                new FirearmComponentPanelDrawer<FirearmShellEjectorBehaviour>(firearm, FirearmComponentType.ShellEjector),
                new FirearmComponentPanelDrawer<FirearmBarrelEffectBehaviour>(firearm, FirearmComponentType.BarrelEffect),
                new FirearmComponentPanelDrawer<FirearmDryFireFeedbackBehaviour>(firearm, FirearmComponentType.DryFireFeedback),
            };
        }
    }
}