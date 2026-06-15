using UnityEngine.Events;
using System.Text;
using System;

namespace PolymindGames.WorldManagement
{
    /// <summary>
    /// Interface for managing game time.
    /// </summary>
    public interface ITimeManager
    {
        /// <summary>
        /// Gets the current day of the month in the game.
        /// </summary>
        int Day { get; }

        /// <summary>
        /// Gets the current hour of the day (24-hour format) in the game.
        /// </summary>
        int Hour { get; }

        /// <summary>
        /// Gets the current minute of the hour in the game.
        /// </summary>
        int Minute { get; }

        /// <summary>
        /// Gets the current second of the minute in the game.
        /// </summary>
        int Second { get; }

        /// <summary>
        /// Gets the total number of minutes elapsed since the start of the game.
        /// </summary>
        int TotalMinutes { get; }

        /// <summary>
        /// Gets the total number of hours elapsed since the start of the game.
        /// </summary>
        int TotalHours { get; }

        /// <summary>
        /// Gets the current day time, ranging from 0 (start of the day) to 1 (end of the day).
        /// </summary>
        float DayTime { get; }

        /// <summary>
        /// Gets the current time of day in the game.
        /// </summary>
        TimeOfDay TimeOfDay { get; }

        /// <summary>
        /// Gets or sets the increment of normalized day time per second.
        /// </summary>
        float DayTimeIncrementPerSecond { get; set; }

        /// <summary>
        /// Event triggered when the minute changes in the game.
        /// </summary>
        event TimeChangedEventHandler MinuteChanged;

        /// <summary>
        /// Event triggered when the hour changes in the game.
        /// </summary>
        event TimeChangedEventHandler HourChanged;

        /// <summary>
        /// Event triggered when the day changes in the game.
        /// </summary>
        event TimeChangedEventHandler DayChanged;

        /// <summary>
        /// Event triggered when the day time changes in the game.
        /// </summary>
        event UnityAction<float> DayTimeChanged;

        /// <summary>
        /// Event triggered when the time of day changes in the game.
        /// </summary>
        event UnityAction<TimeOfDay> TimeOfDayChanged;
    }

    public delegate void TimeChangedEventHandler(int total, int change);

    public sealed class DefaultTimeManager : ITimeManager
    {
        public int Day => 0;
        public int Hour => 12;
        public int Minute => 0;
        public int Second => 0;
        public int TotalMinutes => 0;
        public int TotalHours => 0;
        public float DayTime => 0.5f;
        public TimeOfDay TimeOfDay => TimeOfDay.Afternoon;
        public float DayTimeIncrementPerSecond { get; set; } = 1f;

        public event TimeChangedEventHandler MinuteChanged { add { } remove { } }
        public event TimeChangedEventHandler HourChanged { add { } remove { } }
        public event TimeChangedEventHandler DayChanged { add { } remove { } }
        public event UnityAction<float> DayTimeChanged { add { } remove { } }
        public event UnityAction<TimeOfDay> TimeOfDayChanged { add { } remove { } }
    }

    public static class WorldExtensions
    {
        /// <summary>
        /// Calculates the duration of an in-game day in real-world minutes.
        /// </summary>
        /// <returns>The duration of an in-game day in real-world minutes.</returns>
        public static float GetDayDurationInRealMinutes(this ITimeManager time) =>
            1 / (time.DayTimeIncrementPerSecond * 60f);

        /// <summary>
        /// Calculates the duration of an in-game day in real-world seconds.
        /// </summary>
        /// <returns>The duration of an in-game day in real-world seconds.</returns>
        public static float GetDayDurationInRealSeconds(this ITimeManager time) =>
            1 / time.DayTimeIncrementPerSecond;

        /// <summary>
        /// Sets the duration of an in-game day to real-world seconds.
        /// </summary>
        public static void SetDayDurationToRealSeconds(this ITimeManager time, float realSeconds) =>
            time.DayTimeIncrementPerSecond = 1f / realSeconds;

        /// <summary>
        /// Sets the duration of an in-game day to real-world minutes.
        /// </summary>
        public static void SetDayDurationToRealMinutes(this ITimeManager time, float realMinutes) =>
            time.DayTimeIncrementPerSecond = 1f / (realMinutes * 60f);

        /// <summary>
        /// Gets the current game time based on the provided time manager.
        /// </summary>
        /// <param name="time">The time manager providing the current time.</param>
        /// <returns>The current game time.</returns>
        public static GameTime GetGameTime(this ITimeManager time) =>
            new(time.Day, time.DayTime);

        /// <summary>
        /// Retrieves the current in-game time.
        /// </summary>
        /// <returns>The current in-game time as a DateTime object.</returns>
        public static DateTime GetDateTime(this ITimeManager time) =>
            new(time.Day / 365, time.Day / 30, time.Day, time.Hour, time.Minute, time.Second);

        /// <summary>
        /// Determines whether the current time in the game is during daytime (morning or noon).
        /// </summary>
        /// <param name="time">The time manager providing the current time.</param>
        /// <returns>True if the current time is during daytime; otherwise, false.</returns>
        public static bool IsDayTime(this ITimeManager time) => IsDaytime(time.TimeOfDay);

        /// <summary>
        /// Determines whether the current time in the game is during nighttime (evening or night).
        /// </summary>
        /// <param name="time">The time manager providing the current time.</param>
        /// <returns>True if the current time is during nighttime; otherwise, false.</returns>
        public static bool IsNightTime(this ITimeManager time) => IsDaytime(time.TimeOfDay);

        /// <summary>
        /// Calculates the normalized day time given hours, minutes, and seconds.
        /// </summary>
        /// <param name="hours">The number of hours.</param>
        /// <param name="minutes">The number of minutes.</param>
        /// <param name="seconds">The number of seconds.</param>
        /// <returns>The normalized day time as a float value between 0 and 1.</returns>
        public static float CalculateNormalizedDayTime(int hours, int minutes, int seconds)
        {
            int totalSeconds = hours * 3600 + minutes * 60 + seconds;
            return (float)totalSeconds / (24 * 60 * 60);
        }

        /// <summary>
        /// Gets the hour component from the given day time (0-1).
        /// </summary>
        /// <param name="dayTime">The floating-point representation of the time in a day.</param>
        /// <returns>The hour component from the given day time.</returns>
        public static int GetHour(float dayTime) => (int)(dayTime * 24) % 24;

        /// <summary>
        /// Gets the minute component from the given day time (0-1).
        /// </summary>
        /// <param name="dayTime">The floating-point representation of the time in a day.</param>
        /// <returns>The minute component from the given day time.</returns>
        public static int GetMinute(float dayTime) => (int)(dayTime * 1440) % 60;

        /// <summary>
        /// Gets the second component from the given day time (0-1).
        /// </summary>
        /// <param name="dayTime">The floating-point representation of the time in a day.</param>
        /// <returns>The second component from the given day time.</returns>
        public static int GetSecond(float dayTime) => (int)(dayTime * 86400) % 60;

        /// <summary>
        /// Determines the time of day based on the given day-time value (0-1).
        /// </summary>
        /// <param name="dayTime">The time of day based on dayTime</param>
        /// <returns></returns>
        public static TimeOfDay GetTimeOfDay(float dayTime) => dayTime switch
        {
            < 0.25f => TimeOfDay.Night,
            < 0.5f => TimeOfDay.Morning,
            < 0.7f => TimeOfDay.Afternoon,
            < 0.83f => TimeOfDay.Evening,
            _ => TimeOfDay.Night
        };

        /// <summary>
        /// Determines whether the given day time represents daytime.
        /// </summary>
        /// <param name="dayTime">The day time as a float value between 0 and 1.</param>
        /// <returns>True if the day time represents daytime; otherwise, false.</returns>
        public static bool IsDaytime(float dayTime) => dayTime > 0.25f && dayTime < 0.83f;

        /// <summary>
        /// Determines whether the given day time represents nighttime.
        /// </summary>
        /// <param name="dayTime">The day time as a float value between 0 and 1.</param>
        /// <returns>True if the day time represents nighttime; otherwise, false.</returns>
        public static bool IsNighttime(float dayTime) => dayTime < 0.25f || dayTime > 0.83f;

        /// <summary>
        /// Determines whether the specified time of day is during daytime (morning or noon).
        /// </summary>
        /// <param name="timeOfDay">The time of day to check.</param>
        /// <returns>True if the time of day is during daytime; otherwise, false.</returns>
        public static bool IsDaytime(this TimeOfDay timeOfDay) => timeOfDay is TimeOfDay.Morning or TimeOfDay.Afternoon;

        /// <summary>
        /// Determines whether the specified time of day is during nighttime (evening or night).
        /// </summary>
        /// <param name="timeOfDay">The time of day to check.</param>
        /// <returns>True if the time of day is during nighttime; otherwise, false.</returns>
        public static bool IsNighttime(this TimeOfDay timeOfDay) => timeOfDay is TimeOfDay.Night or TimeOfDay.Evening;

        /// <summary>
        /// Formats the time with prefixes indicating the day, hour, and minute.
        /// </summary>
        /// <returns>A string representing the formatted time with prefixes for day, hour, and minute.</returns>
        public static string FormatTimeWithPrefixes(this ITimeManager time)
        {
            return $"Day: {time.Day} | Hour: {time.Hour} | Minute: {time.Minute}";
        }

        /// <summary>
        /// Formats the time into a string representation based on specified units (hours, minutes, seconds),
        /// appending the result to the provided StringBuilder and returning the formatted time string.
        /// </summary>
        /// <param name="time"></param>
        /// <param name="hours">Whether to include hours in the formatted string.</param>
        /// <param name="minutes">Whether to include minutes in the formatted string.</param>
        /// <param name="seconds">Whether to include seconds in the formatted string.</param>
        /// <param name="builder">The StringBuilder to which the formatted time string will be appended.</param>
        /// <returns>The formatted time string.</returns>
        public static string FormatDayTime(this ITimeManager time, bool hours, bool minutes, bool seconds, StringBuilder builder = null)
        {
            if (builder != null)
                builder.Clear();
            else
                builder = new StringBuilder(8);

            if (hours)
                builder.Append(time.Hour.ToString("00"));

            if (minutes)
            {
                if (builder.Length > 0)
                    builder.Append(':');
                builder.Append(time.Minute.ToString("00"));
            }

            if (seconds)
            {
                if (builder.Length > 0)
                    builder.Append(':');
                builder.Append(time.Second.ToString("00"));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Formats the given minute value with hour and minute suffixes.
        /// </summary>
        /// <param name="minute">The minute value to format.</param>
        /// <returns>A string representation of the minute value with suffixes.</returns>
        public static string FormatMinuteWithSuffixes(int minute)
        {
            return minute switch
            {
                > 60 => $"{minute / 60}h {minute % 60}m",
                < 0 => string.Empty,
                _ => $"{minute}m"
            };
        }

        /// <summary>
        /// Formats the time into a string representation with suffixes based on specified units (hours, minutes, seconds),
        /// appending the result to the provided StringBuilder and returning the formatted time string.
        /// </summary>
        /// <param name="time"></param>
        /// <param name="hours">Whether to include hours in the formatted string.</param>
        /// <param name="minutes">Whether to include minutes in the formatted string.</param>
        /// <param name="seconds">Whether to include seconds in the formatted string.</param>
        /// <param name="builder">The StringBuilder to which the formatted time string with suffixes will be appended.</param>
        /// <returns>The formatted time string with suffixes.</returns>
        public static string FormatDayTimeWithSuffixes(this ITimeManager time, bool hours, bool minutes, bool seconds, StringBuilder builder = null)
        {
            if (builder != null)
                builder.Clear();
            else
                builder = new StringBuilder(8);

            if (hours && time.Hour > 0)
                builder.Append(time.Hour + "h ");

            if (minutes && time.Minute > 0)
                builder.Append(time.Minute + "m ");

            if (seconds && time.Second > 0)
                builder.Append(time.Second + "s ");

            return builder.ToString();
        }

        /// <summary>
        /// Formats the time with prefixes indicating the day, hour, and minute.
        /// </summary>
        /// <returns>A string representing the formatted time with prefixes for day, hour, and minute.</returns>
        public static string FormatGameTimeWithPrefixes(this GameTime time)
        {
            return $"Day: {time.Day} | Hour: {time.Hour} | Minute: {time.Minute}";
        }
    }
}