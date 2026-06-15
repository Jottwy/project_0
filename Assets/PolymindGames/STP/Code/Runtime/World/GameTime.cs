using UnityEngine;
using System;

namespace PolymindGames.WorldManagement
{
    public enum TimeOfDay : byte
    {
        Morning,
        Afternoon,
        Evening,
        Night
    }
    
    [Serializable]
    public struct GameTime
    {
        [SerializeField, Range(0, 1000)]
        private int _day;
        
        [SerializeField, Range(0f, 1f)]
        private float _dayTime;
        
        public GameTime(int day, float normalizedDayTime)
        {
            _day = day;
            _dayTime = normalizedDayTime;
        }

        public GameTime(int day, int hours, int minutes, int seconds)
        {
            _day = day;
            _dayTime = WorldExtensions.CalculateNormalizedDayTime(hours, minutes, seconds);
        }

        /// <summary>
        /// Gets the second component of the current game time.
        /// </summary>
        public int Second => WorldExtensions.GetSecond(_dayTime);

        /// <summary>
        /// Gets the minute component of the current game time.
        /// </summary>
        public int Minute => WorldExtensions.GetMinute(_dayTime);

        /// <summary>
        /// Gets the hour component of the current game time.
        /// </summary>
        public int Hour => WorldExtensions.GetHour(_dayTime);

        /// <summary>
        /// Gets the current day of the game.
        /// </summary>
        public int Day => _day;

        /// <summary>
        /// Gets the normalized day time, ranging from 0 (start of the day) to 1 (end of the day).
        /// </summary>
        public float DayTime => _dayTime;

        public static bool operator ==(GameTime time1, GameTime time2) =>
            time1._day == time2._day && Mathf.Approximately(time1._dayTime, time2._dayTime);

        public static bool operator !=(GameTime time1, GameTime time2) => 
            !(time1 == time2);

        public override bool Equals(object obj)
        {
            if (obj is GameTime gameTime)
                return gameTime == this;

            return false;
        }

        public override int GetHashCode() => HashCode.Combine(_day, _dayTime);
    }
}