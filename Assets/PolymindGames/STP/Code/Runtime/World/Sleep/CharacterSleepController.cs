using PolymindGames.ProceduralMotion;
using PolymindGames.WorldManagement;
using PolymindGames.PostProcessing;
using PolymindGames.InputSystem;
using System.Collections;
using UnityEngine.Events;
using UnityEngine;
using System;
using PolymindGames.SaveSystem;

namespace PolymindGames
{
    [OptionalCharacterComponent(typeof(IHungerManagerCC), typeof(IThirstManagerCC), typeof(IWieldablesControllerCC))]
    public sealed class CharacterSleepController : CharacterBehaviour, ISleepControllerCC, ISaveableComponent
    {
        [SerializeField]
        private InputContext _sleepContext;

        [SerializeField, SpaceArea, SubGroup]
        private AnimationSettings _animationSettings;

        [SerializeField, SubGroup]
        private ConditionSettings _conditionSettings;

        [SerializeField, SubGroup]
        private TimeSettings _timeSettings = TimeSettings.Default;

        [SerializeField, SubGroup]
        private EffectSettings _effectSettings;

        [SerializeField, SubGroup]
        private EventSettings _eventSettings;
        
        public Vector3 LastSleepPosition { get; private set; } = Vector3.zero;
        public Quaternion LastSleepRotation { get; private set; } = Quaternion.identity;
        public bool SleepActive { get; private set; }

        public event UnityAction<int> SleepStart
        {
            add => _eventSettings.OnSleepStart.AddListener(value);
            remove => _eventSettings.OnSleepStart.RemoveListener(value);
        }

        public event UnityAction<int> SleepEnd
        {
            add => _eventSettings.OnSleepEnd.AddListener(value);
            remove => _eventSettings.OnSleepEnd.RemoveListener(value);
        }

        public bool TrySleep(ISleepingSpot sleepingSpot)
        {
            if (SleepActive)
                return false;

            if (_conditionSettings.OnlySleepAtNight && World.Instance.Time.IsDayTime())
            {
                MessageDispatcher.Instance.Dispatch(Character, MsgType.Error, "You can only sleep at night!");
                Character.Audio.PlayClip(_effectSettings.FailedAudio, BodyPoint.Torso);
                return false;
            }

            if (_conditionSettings.CheckForEnemies && HasEnemiesNearby(sleepingSpot.gameObject.transform))
            {
                MessageDispatcher.Instance.Dispatch(Character, MsgType.Error, "Can't sleep with enemies nearby!");
                Character.Audio.PlayClip(_effectSettings.FailedAudio, BodyPoint.Torso);
                return false;
            }

            StartCoroutine(C_Sleep(sleepingSpot));
            return true;
        }

        private bool HasEnemiesNearby(Transform checkPoint)
        {
            float radius = _conditionSettings.CheckForEnemiesRadius;
            int size = PhysicsUtility.OverlapSphereOptimized(checkPoint.position, radius, out var cols, LayerConstants.CharacterMask);

            var ignoredRoot = Character.transform;
            for (int i = 0; i < size; i++)
            {
                if (cols[i].transform.IsChildOf(ignoredRoot) || !cols[i].gameObject.HasComponent<IDamageHandler>())
                    continue;

                return true;
            }

            return false;
        }

        private IEnumerator C_Sleep(ISleepingSpot sleepingSpot)
        {
            // Holster active wieldable
            Character.GetCC<IWieldablesControllerCC>().TryEquipWieldable(null, 1.3f);

            var originalRotation = _animationSettings.Camera.eulerAngles;
            var targetPosition = _animationSettings.Camera.InverseTransformPoint(sleepingSpot.SleepPosition);
            var targetRotation = sleepingSpot.SleepOrientation;

            // Play the sleep animation
            PlayCameraAnimation(targetPosition, targetRotation, _timeSettings.EaseInDuration);

            InputManager.Instance.PushContext(_sleepContext);

            yield return new WaitForTime(_timeSettings.EaseInDuration + _timeSettings.FallAsleepDuration);

            DoSleepEffects();
            
            int hoursToSleep = GetHoursToSleep();

            SleepActive = true;
            _eventSettings.OnSleepStart.Invoke(hoursToSleep);
            
            var time = World.Instance.Time;
            float defaultDayIncrement = time.DayTimeIncrementPerSecond;
            time.SetDayDurationToRealSeconds(_timeSettings.SleepDuration * (24f / hoursToSleep));

            yield return new WaitForTime(_timeSettings.SleepDuration);

            time.DayTimeIncrementPerSecond = defaultDayIncrement;

            LastSleepPosition = Character.transform.position;
            
            DoWakeUpEffects(hoursToSleep);

            yield return new WaitForTime(_timeSettings.EaseOutDelay);

            SleepActive = false;
            _eventSettings.OnSleepEnd?.Invoke(hoursToSleep);

            // Play the wake up animation
            PlayCameraAnimation(Vector3.zero, originalRotation, _timeSettings.EaseOutDuration);

            yield return new WaitForTime(_timeSettings.EaseOutDuration);
            
            InputManager.Instance.PopContext(_sleepContext);
        }

        private void DoSleepEffects()
        {
            Character.Audio.PlayClip(_effectSettings.SleepingAudio, BodyPoint.Torso);
            
            PostProcessingManager.Instance.PlayAnimation(this, _effectSettings.CameraEffect);
        }

        private void DoWakeUpEffects(int hoursSlept)
        {
            if (hoursSlept > 0)
                Character.HealthManager.RestoreHealth(hoursSlept * _effectSettings.HealthIncreasePerHour);

            Character.Audio.PlayClip(_effectSettings.GetUpAudio, BodyPoint.Torso);
            
            PostProcessingManager.Instance.CancelAnimation(this, _effectSettings.CameraEffect);
        }

        private int GetHoursToSleep()
        {
            int currentHour = World.Instance.Time.Hour;

            int hoursToSleep = currentHour switch
            {
                < 24 and > 12 => 24 - currentHour + _timeSettings.MaxGetUpHour,
                < 12 when currentHour <= _timeSettings.MaxGetUpHour => _timeSettings.MaxGetUpHour - currentHour,
                _ => _timeSettings.HoursToSleep
            };

            return Mathf.Min(hoursToSleep, _timeSettings.HoursToSleep);
        }

        private void PlayCameraAnimation(Vector3 targetPosition, Vector3 targetRotation, float duration)
        {
            _animationSettings.Camera.TweenLocalPosition(targetPosition, duration)
                .SetEasing(_animationSettings.EaseType);

            _animationSettings.Camera.TweenRotation(Quaternion.Euler(targetRotation), duration)
                .SetEasing(_animationSettings.EaseType);
        }

        #region Internal Types
        [Serializable]
        private struct AnimationSettings
        {
            public Transform Camera;
            public EaseType EaseType;
        }
        
        [Serializable]
        private struct TimeSettings
        {
            [Range(0f, 10f)]
            [Tooltip("How much time it takes to transition to sleeping (e.g. moving to bed).")]
            public float EaseInDuration;

            [Range(0f, 10f)]
            [Tooltip("How much time it takes to fall asleep.")]
            public float FallAsleepDuration;

            [Range(0, 60)]
            [Tooltip("Sleep duration in seconds")]
            public float SleepDuration;

            [Range(0f, 10f)]
            [Tooltip("How much time to wait after the sleep is done, before getting up.")]
            public float EaseOutDelay;

            [Range(0f, 10f)]
            [Tooltip("How much time it takes to transition from sleeping to standing up.")]
            public float EaseOutDuration;

            [SpaceArea]
            [Range(0, 24)]
            [Tooltip("Max hours that can pass while sleeping")]
            public int HoursToSleep;

            [Range(0, 24)]
            [Tooltip("Max hour this character can wake up at, we don't want to be lazy :)")]
            public int MaxGetUpHour;

            public static TimeSettings Default =>
                new()
                {
                    EaseInDuration = 2f,
                    FallAsleepDuration = 1.5f,
                    SleepDuration = 8f,
                    EaseOutDelay = 1.5f,
                    EaseOutDuration = 2f,
                    HoursToSleep = 12,
                    MaxGetUpHour = 8
                };
        }

        [Serializable]
        private struct ConditionSettings
        {
            [Tooltip("If Enabled, this character will not be allowed to sleep during the day.")]
            public bool OnlySleepAtNight;

            [Tooltip("Check for enemies before sleeping, if any of the are found, this character will be unable to sleep.")]
            public bool CheckForEnemies;

            [IndentArea]
            [ShowIf(nameof(CheckForEnemies), true)]
            [Tooltip("The enemy check radius.")]
            public float CheckForEnemiesRadius;
        }

        [Serializable]
        private struct EffectSettings
        {
            [Range(0, 100f)]
            public float HealthIncreasePerHour;

            [SpaceArea(3f)]
            public VolumeAnimationProfile CameraEffect;
            public AudioData FailedAudio;
            public AudioData SleepingAudio;
            public AudioData GetUpAudio;
        }

        [Serializable]
        private struct EventSettings
        {
            [SpaceArea]
            public UnityEvent<int> OnSleepStart;
            public UnityEvent<int> OnSleepEnd;
        }
        #endregion

        #region Save & Load
        [Serializable]
        private sealed class SaveData
        {
            public Vector3 Position;
            public Quaternion Rotation;
        }

        void ISaveableComponent.LoadMembers(object data)
        {
            var saveData = (SaveData)data;
            LastSleepPosition = saveData.Position;
            LastSleepRotation = saveData.Rotation;
        }

        object ISaveableComponent.SaveMembers() => new SaveData()
        {
            Position = LastSleepPosition,
            Rotation = LastSleepRotation
        };
        #endregion
    }
}