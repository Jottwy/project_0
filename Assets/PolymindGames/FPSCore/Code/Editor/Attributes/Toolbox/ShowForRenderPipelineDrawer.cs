using JetBrains.Annotations;
using UnityEditor;
using UnityEngine;

namespace Toolbox.Editor.Drawers
{
    using UnityGraphicsSettings = UnityEngine.Rendering.GraphicsSettings;
    
    [UsedImplicitly]
    public sealed class ShowForRenderPipelineDrawer : ToolboxConditionDrawer<ShowForRenderPipelineAttribute>
    {
        protected override PropertyCondition OnGuiValidateSafe(SerializedProperty property, ShowForRenderPipelineAttribute attribute)
        {
            return FindActiveRenderingPipeline() == attribute.PipelineType ? PropertyCondition.Valid : PropertyCondition.NonValid;
        }
        
        private static ShowForRenderPipelineAttribute.Type FindActiveRenderingPipeline()
        {
            if (UnityGraphicsSettings.defaultRenderPipeline != null)
            {
                var srpType = UnityGraphicsSettings.defaultRenderPipeline.GetType().ToString();
                if (srpType.Contains("HD"))
                    return ShowForRenderPipelineAttribute.Type.Hdrp;
        
                if (srpType.Contains("Universal"))
                    return ShowForRenderPipelineAttribute.Type.Urp;
            }
            
            return ShowForRenderPipelineAttribute.Type.BuiltIn;
        }
    }
}