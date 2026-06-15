using UnityEngine;

namespace PolymindGames.WorldManagement
{
    /// <summary>
    /// Interface for managing game weather.
    /// NOTE: This is just a proof of concept, it's not currently implemented. 
    /// </summary>
    public interface IWeatherManager
    {
        float GlobalTemperature { get; }
        float GetTemperatureAtPoint(Vector3 point);
        
        public const float DefaultTemperatureInCelsius = 20f;
    }
    
    public sealed class DefaultWeatherManager : IWeatherManager
    {
        private const float DefaultTemperatureCelsius = 20;


        public float GlobalTemperature => DefaultTemperatureCelsius;
        public float GetTemperatureAtPoint(Vector3 point) => DefaultTemperatureCelsius;
    }
}