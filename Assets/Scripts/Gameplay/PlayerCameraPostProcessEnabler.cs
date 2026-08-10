using UnityEngine;
#if UNITY_POST_PROCESSING_STACK_V2
using UnityEngine.Rendering.PostProcessing;
#endif

namespace BackroomsSurvival.Gameplay
{
#if UNITY_POST_PROCESSING_STACK_V2
    /// <summary>
    /// Fase 5D — attaches a PPv2 <see cref="PostProcessLayer"/> to the player camera at
    /// runtime (no vendor prefab edit). Waits for <c>Camera.main</c> (the STP player camera is
    /// tagged MainCamera), inits the layer with the PostProcessResources copied into
    /// Resources, and scans every layer for the global volume created by
    /// <see cref="BackroomsPostProcess"/>.
    /// </summary>
    public sealed class PlayerCameraPostProcessEnabler : MonoBehaviour
    {
        private PostProcessResources _resources;
        private bool _attached;

        private void Start()
        {
            _resources = Resources.Load<PostProcessResources>("PostProcessResources");
            if (_resources == null)
                Debug.LogError("[PostProcess] Resources/PostProcessResources.asset not found — " +
                               "PPv2 layer disabled. Copy it from the postprocessing package.");
        }

        private void Update()
        {
            if (_attached || _resources == null) return;

            var cam = Camera.main; // player camera spawns late; null until then
            if (cam == null) return;

            if (cam.GetComponent<PostProcessLayer>() == null)
            {
                var layer = cam.gameObject.AddComponent<PostProcessLayer>();
                layer.Init(_resources);
                layer.volumeTrigger    = cam.transform;
                layer.volumeLayer      = ~0; // Everything (the global volume sits on Default)
                layer.antialiasingMode = PostProcessLayer.Antialiasing.FastApproximateAntialiasing;
            }
            _attached = true;
        }
    }
#else
    /// <summary>
    /// No-op stub compiled while PPv2 (UNITY_POST_PROCESSING_STACK_V2) is absent — keeps
    /// GridTestWorld.InitializeWorld compiling during the URP migration. The URP version
    /// enables post-processing on UniversalAdditionalCameraData instead.
    /// </summary>
    public sealed class PlayerCameraPostProcessEnabler : MonoBehaviour
    {
    }
#endif
}
