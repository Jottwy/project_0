using UnityEngine;

#if POLYMIND_GAMES_FPS_HDRP || POLYMIND_GAMES_FPS_URP
using Volume = UnityEngine.Rendering.Volume;
#else
using Volume = UnityEngine.Rendering.PostProcessing.PostProcessVolume;
#endif

namespace PolymindGames.PostProcessing
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Volume))]
    [AddComponentMenu("Polymind Games/Miscellaneous/Global Volume")]
    public sealed class GlobalVolume : MonoBehaviour
    {
        private Volume _volume;

        private void Awake() => _volume = GetComponent<Volume>();
        private void OnEnable() => PostProcessingManager.Instance.ActiveVolume = _volume;

        private void OnDisable()
        {
            if (PostProcessingManager.Instance.ActiveVolume == _volume)
                PostProcessingManager.Instance.ActiveVolume = null;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            UnityUtility.SafeOnValidate(this, () => gameObject.GetOrAddComponent<Volume>().isGlobal = true);
        }
#endif
    }
}

