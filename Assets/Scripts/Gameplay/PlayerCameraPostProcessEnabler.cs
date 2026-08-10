using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// URP version (reescrito desde PPv2 en la migración BIRP→URP). The vendor
    /// player camera prefab already enables post-processing (TAA, volumeLayerMask =
    /// PostProcessing). This enabler covers cameras created outside that prefab
    /// (grid test world fallback): waits for <c>Camera.main</c>, forces
    /// renderPostProcessing on, widens the volume mask to include the
    /// PostProcessing layer (where <see cref="BackroomsPostProcess"/> puts its
    /// global volume), and only sets FXAA when antialiasing is off — never
    /// downgrades the vendor's TAA.
    /// </summary>
    public sealed class PlayerCameraPostProcessEnabler : MonoBehaviour
    {
        private bool _attached;

        private void Update()
        {
            if (_attached) return;

            var cam = Camera.main; // player camera spawns late; null until then
            if (cam == null) return;

            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            if (data.antialiasing == AntialiasingMode.None)
                data.antialiasing = AntialiasingMode.FastApproximateAntialiasing;

            int ppLayer = LayerMask.NameToLayer("PostProcessing");
            if (ppLayer >= 0)
                data.volumeLayerMask = data.volumeLayerMask | (1 << ppLayer);

            _attached = true;
        }
    }
}
