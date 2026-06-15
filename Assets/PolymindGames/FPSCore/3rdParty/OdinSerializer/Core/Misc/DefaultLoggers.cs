namespace PolymindGames.OdinSerializer
{
    /// <summary>
    /// Defines default loggers for serialization and deserialization. This class and all of its loggers are thread safe.
    /// </summary>
    public static class DefaultLoggers
    {
        private static readonly object s_Lock = new();
        private static volatile ILogger s_UnityLogger;

        /// <summary>
        /// The default logger - usually this is <see cref="UnityLogger"/>.
        /// </summary>
        public static ILogger DefaultLogger => UnityLogger;

        /// <summary>
        /// Logs messages using Unity's <see cref="UnityEngine.Debug"/> class.
        /// </summary>
        public static ILogger UnityLogger
        {
            get
            {
                if (s_UnityLogger == null)
                {
                    lock (s_Lock)
                    {
                        if (s_UnityLogger == null)
                        {
                            s_UnityLogger = new CustomLogger(UnityEngine.Debug.LogWarning, UnityEngine.Debug.LogError, UnityEngine.Debug.LogException);
                        }
                    }
                }

                return s_UnityLogger;
            }
        }
    }
}