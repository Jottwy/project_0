using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WorldManagement
{
    /// <summary>
    /// Represents the world in the game.
    /// </summary>
    public sealed partial class World
    {
        private readonly MessageDispatcher _message = new();
        private IWeatherManager _weather = new DefaultWeatherManager();
        private ITimeManager _time = new DefaultTimeManager();

        /// <summary>
        /// Gets the singleton instance of the world.
        /// </summary>
        public static World Instance { get; private set; } = new();
        
        /// <summary>
        /// Gets or sets the time manager responsible for managing in-game time.
        /// </summary>
        public ITimeManager Time
        {
            get => _time;
            set
            {
                if (value == Time)
                {
                    Debug.LogWarning("You're trying to set the time manager to the active one.");
                    return;
                }

                value ??= new DefaultTimeManager();

                var prevTime = Time;
                _time = value;
                TimeManagerChanged?.Invoke(prevTime, _time);
            }
        }

        /// <summary>
        /// Gets or sets the weather manager responsible for managing in-game weather.
        /// </summary>
        public IWeatherManager Weather
        {
            get => _weather;
            set
            {
                if (value == Weather)
                {
                    Debug.LogWarning("You're trying to set the weather manager to the active one.");
                    return;
                }
                
                value ??= new DefaultWeatherManager();

                var prevWeather = Weather;
                _weather = value;
                WeatherManagerChanged?.Invoke(prevWeather, _weather);
            }
        }

        /// <summary>
        /// Event triggered when the time manager changes.
        /// </summary>
        public event UnityAction<ITimeManager, ITimeManager> TimeManagerChanged;

        /// <summary>
        /// Event triggered when the weather manager changes.
        /// </summary>
        public event UnityAction<IWeatherManager, IWeatherManager> WeatherManagerChanged;
    }
}
