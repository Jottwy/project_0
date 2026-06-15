using UnityEngine;

namespace PolymindGames
{
    [RequireComponent(typeof(Rigidbody))]
    public sealed class RigidbodyImpactHandler : MonoBehaviour, IDamageImpactHandler
    {
        [SerializeField, Range(0f, 100f)]
        private float _forceMultiplier = 1f;
        
        private Rigidbody _rigidbody;
        
        public void HandleImpact(Vector3 hitPoint, Vector3 hitForce)
        {
            _rigidbody.AddForceAtPosition(hitForce * _forceMultiplier, hitPoint, ForceMode.Impulse);
        }

        private void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();
        }
    }
}
