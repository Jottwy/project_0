using System;
using UnityEngine;

#if POLYMIND_GAMES_FPS_HDRP
using VolumeComponent = UnityEngine.Rendering.HighDefinition.DepthOfField;
#elif POLYMIND_GAMES_FPS_URP
using VolumeComponent = UnityEngine.Rendering.Universal.DepthOfField;
#else
using VolumeComponent = UnityEngine.Rendering.PostProcessing.DepthOfField;
#endif

namespace PolymindGames.PostProcessing
{
    [Serializable]
    public sealed class DepthOfFieldAnimation : VolumeAnimation<VolumeComponent>
    {
        [SerializeField, ShowForRenderPipeline(ShowForRenderPipelineAttribute.Type.BuiltIn)]
        private VolumeParameterAnimation<float> _aperture = new(20f, 0f);
        
        [SerializeField, ShowForRenderPipeline(ShowForRenderPipelineAttribute.Type.BuiltIn)]
        private VolumeParameterAnimation<float> _focusDistance = new(20f, 0f);
        
        [SerializeField, ShowForRenderPipeline(ShowForRenderPipelineAttribute.Type.Hdrp)]
        private VolumeParameterAnimation<float> _nearFocusStart = new(0f, 0f);

        [SerializeField, ShowForRenderPipeline(ShowForRenderPipelineAttribute.Type.Hdrp)]
        private VolumeParameterAnimation<float> _nearFocusEnd = new(4f, 4f);

        [SerializeField, ShowForRenderPipeline(ShowForRenderPipelineAttribute.Type.Hdrp)]
        private VolumeParameterAnimation<float> _farFocusStart = new(10f, 10f);

        [SerializeField, ShowForRenderPipeline(ShowForRenderPipelineAttribute.Type.Hdrp)]
        private VolumeParameterAnimation<float> _farFocusEnd = new(20f, 0f);

        protected override void AddAnimations(VolumeParameterAnimationCollection list, VolumeComponent component)
        {
#if POLYMIND_GAMES_FPS_HDRP
            list.Add(_nearFocusStart.SetParameter(component.nearFocusStart));
            list.Add(_nearFocusEnd.SetParameter(component.nearFocusEnd));
            list.Add(_farFocusStart.SetParameter(component.farFocusStart));
            list.Add(_farFocusEnd.SetParameter(component.farFocusEnd));
#elif UNITY_POST_PROCESSING_STACK_V2
            list.Add(_aperture.SetParameter(component.aperture));
            list.Add(_focusDistance.SetParameter(component.focusDistance));
#endif
        }
    }
}
