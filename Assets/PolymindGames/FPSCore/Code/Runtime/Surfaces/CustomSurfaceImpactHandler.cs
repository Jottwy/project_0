using UnityEngine;

namespace PolymindGames.SurfaceSystem
{
    public sealed class CustomSurfaceImpactHandler : MonoBehaviour
    {
        [SerializeField, Range(0f, 2f)]
        private float _volumeMultiplier = 1f;

        [SerializeField]
        private AudioData _impactAudio = new(null);

        private bool _hasCollided;

        private void OnEnable() => _hasCollided = false;

        private void OnCollisionEnter(Collision collision)
        {
            if (_hasCollided)
                return;
            
            AudioManager.Instance.PlayClip3D(_impactAudio, collision.GetContact(0).point, _volumeMultiplier);
            _hasCollided = true;
        }
    }
}