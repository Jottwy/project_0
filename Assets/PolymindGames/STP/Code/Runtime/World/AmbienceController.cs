using UnityEngine;

namespace PolymindGames.WorldManagement
{
    public sealed class AmbienceController : MonoBehaviour 
	{
		[SerializeField, Title("Day")]
		private AudioSource _dayAudioSrc;

		[SerializeField, Range(0f, 1f)]
		private float _peakDayVolume = 0.7f;

		[SerializeField, Title("Night")]
		private AudioSource _nightAudioSrc;

		[SerializeField, Range(0f, 1f)]
		private float _peakNightVolume = 0.7f;

		private void OnEnable()
		{
			World.Instance.Time.DayTimeChanged += UpdateVolume;
			UpdateVolume(0);
		}

		private void OnDisable() => World.Instance.Time.DayTimeChanged -= UpdateVolume;

		private void UpdateVolume(float dayTime)
		{
	        _dayAudioSrc.volume = Mathf.PingPong(dayTime, 0.5f) * 2 * _peakDayVolume;
	        _nightAudioSrc.volume = GetVolumeAtTime(dayTime, 0.25f, 0.75f) * _peakNightVolume;
        }

		private static float GetVolumeAtTime(float time, float minTime, float maxTime)
		{
			if (time <= minTime)
				return 1f - time * (1 / minTime);
			
			if (time >= maxTime)
				return (time - maxTime) * (1 / minTime);

			return 0f;
		}
	}
}
