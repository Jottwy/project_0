using UnityEngine;
using UnityEngine.Serialization;

namespace PolymindGames.SurfaceSystem
{
	[DisallowMultipleComponent]
	[RequireComponent(typeof(Collider))]
    public sealed class SurfaceImpactHandler : MonoBehaviour
    {
		[SerializeField]
		private SurfaceEffectPlayFlags _impactFlags = SurfaceEffectPlayFlags.Audio;

		[FormerlySerializedAs("_volumeMultiplier")]
		[SerializeField, Range(0f, 1f)]
		private float _audioVolume = 1f;

		private SurfaceEffectData _impactEffect;
		private float _playEffectsTimer;

		private const float PlayCooldown = 0.4f;
		private const float MinSpeedThreshold = 2.5f;
		
		private void OnCollisionEnter(Collision collision)
		{
			if (Time.time < _playEffectsTimer)
				return;
			
			float relativeVelocityMagnitude = collision.relativeVelocity.magnitude;
			if (relativeVelocityMagnitude < MinSpeedThreshold)
				return;

			var contact = collision.GetContact(0);
			float audioVolume = _audioVolume * Mathf.Clamp(relativeVelocityMagnitude / MinSpeedThreshold / 10, 0.2f, 1f);
			SurfaceManager.Instance.PlayEffect(_impactEffect, contact.point, Quaternion.LookRotation(contact.normal), _impactFlags, audioVolume);
			
			_playEffectsTimer = Time.time + PlayCooldown;
		}

        private void Awake()
        {
			if (!TryGetImpactEffect(out _impactEffect))
			{
				Debug.LogWarning("No corresponding surface definition found for this object, removing this component.", gameObject);
				Destroy(this);
			}
        }

        private bool TryGetImpactEffect(out SurfaceEffectData effectData)
        {
			var surface = SurfaceManager.Instance.GetSurfaceFromCollider(GetComponent<Collider>());
			if (surface != null && surface.TryGetEffectPair(SurfaceEffectType.Impact, out effectData))
				return true;

			effectData = null;
			return false;
        }
	}
}