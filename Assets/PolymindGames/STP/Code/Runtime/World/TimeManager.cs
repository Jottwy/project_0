using PolymindGames.SaveSystem;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WorldManagement
{
    [DisallowMultipleComponent, ExecuteAlways]
    [DefaultExecutionOrder(ExecutionOrderConstants.MonoSingleton)]
    public sealed class TimeManager : MonoBehaviour, ITimeManager, ISaveableComponent
    {
        private enum UpdateType
        {
            FixedUpdate,
            Update
        }

        [SerializeField]
        [Tooltip("The type of update method used for time management.")]
        private UpdateType _updateType;
    
        [SerializeField, Range(0, 1000), Title("Time Settings")]
        [Tooltip("The current day in the game.")]
        private int _day;

        [SerializeField, Range(0, 1f), Delayed]
        [Tooltip("The current time of day, represented as a value between 0 and 1.")]
        private float _dayTime = 0.5f;

        [SerializeField, Range(0f, 0.1f)]
        [Tooltip("The amount of time increment per second.")]
        private float _timeIncrementPerSec = 0.001f;
        
        private TimeOfDay _timeOfDay;
        private int _totalMinutes;
        private int _totalHours;
        private int _minute;
        private int _hour;


        public int Day
        {
            get => _day;
            set
            {
                value = Mathf.Max(value, 0);
                int passedDays = value - _day;
                _day = value;
                DayChanged?.Invoke(_day, passedDays);
            }
        }

        public int Hour
        {
            get => _hour;
            private set
            {
                int totalHours = _day * 24 + value;
                int passedHours = totalHours - _totalHours;
                
                if (passedHours == 0)
                    return;

                _hour = value;
                _totalHours = totalHours;

                HourChanged?.Invoke(totalHours, passedHours);
            }
        }

        public int Minute
        {
            get => _minute;
            private set
            {
                int totalMinutes = _day * 24 + _totalHours * 60 + value;
                int passedMinutes = totalMinutes - _totalMinutes;
                
                if (passedMinutes == 0)
                    return;

                _minute = value;
                _totalMinutes = totalMinutes;
                
                MinuteChanged?.Invoke(totalMinutes, passedMinutes);
            }
        }
        
        public int Second => WorldExtensions.GetSecond(_dayTime);
        public int TotalMinutes => _totalMinutes;
        public int TotalHours => _totalHours;

        public float DayTime
        {
            get => _dayTime;
            private set
            {
                _dayTime = value;
                DayTimeChanged?.Invoke(value);
            }
        }

        public TimeOfDay TimeOfDay
        {
            get => _timeOfDay;
            private set
            {
                if (_timeOfDay == value)
                    return;

                _timeOfDay = value;
                TimeOfDayChanged?.Invoke(value);
            }
        }
        
        public float DayTimeIncrementPerSecond
        {
            get => _timeIncrementPerSec;
            set => _timeIncrementPerSec = value;
        }

        public event TimeChangedEventHandler MinuteChanged;
        public event TimeChangedEventHandler HourChanged;
        public event TimeChangedEventHandler DayChanged;
        public event UnityAction<TimeOfDay> TimeOfDayChanged;
        public event UnityAction<float> DayTimeChanged;

        private void OnEnable() => World.Instance.Time = this;
        private void OnDisable() => World.Instance.Time = null;

        private void Start() => SetDayTime(_dayTime);

        private void Update()
        {
            if (_updateType == UpdateType.Update)
                UpdateTime(Time.deltaTime);
        }

        private void FixedUpdate()
        {
            if (_updateType == UpdateType.FixedUpdate)
                UpdateTime(Time.fixedDeltaTime);
        }

        private void UpdateTime(float deltaTime)
        {
            if (_timeIncrementPerSec > Mathf.Epsilon)
                SetDayTime(_dayTime + _timeIncrementPerSec * deltaTime);
        }

        private void SetDayTime(float dayTime)
        {
            bool newDay = dayTime > 1f;
            dayTime = newDay ? dayTime - 1f : dayTime;

            DayTime = dayTime;
            TimeOfDay = WorldExtensions.GetTimeOfDay(dayTime);
            Hour = WorldExtensions.GetHour(dayTime);
            Minute = WorldExtensions.GetMinute(dayTime);

            if (newDay)
                Day++;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying)
                UnityUtility.SafeOnValidate(this, Validate);
            else
                Validate();

            void Validate()
            {
                SetDayTime(_dayTime);
                Day = _day;
            }
        }
#endif

        #region Save & Load
        void ISaveableComponent.LoadMembers(object data)
        {
            var saveData = (GameTime)data;
            SetDayTime(saveData.DayTime);
            _day = saveData.Day;
        }

        object ISaveableComponent.SaveMembers() => new GameTime(_day, _dayTime);
        #endregion
    }
}
