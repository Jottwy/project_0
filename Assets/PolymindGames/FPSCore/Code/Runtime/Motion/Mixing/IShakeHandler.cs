namespace PolymindGames.ProceduralMotion
{
    public interface IShakeHandler : IMonoBehaviour
    {
        /// <summary>
        /// Plays a shake effect with the given shake data and intensity.
        /// </summary>
        /// <param name="data">The shake data defining the shake characteristics.</param>
        /// <param name="intensity">The intensity of the shake (default is 1f).</param>
        void AddShake(in ShakeData data, float intensity = 1f);
    }
}
