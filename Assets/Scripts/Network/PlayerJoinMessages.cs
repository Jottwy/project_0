namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Qué se le dice al jugador cuando entra alguien. Puro a propósito —sin UnityEngine— para
    /// que la regla de «a quién se anuncia» corra en el arnés headless; el MonoBehaviour que
    /// escucha el bus (<see cref="PlayerJoinNotifier"/>) sólo traduce y despacha.
    ///
    /// El evento es el `player_joined` que el backend ya emitía para el host y que desde
    /// 2026-09-09 emite también el joiner al descubrir a un compañero por el roster
    /// (`NetworkEvent::PeerDiscovered`).
    /// </summary>
    public static class PlayerJoinMessages
    {
        /// Un nombre vacío no es un caso raro: el backend estampa `""` en más de un sitio.
        public const string Unknown = "Alguien";

        /// Un nombre viene de OTRA máquina (`NET_NAME` del otro lado). Se acota para que un
        /// nombre kilométrico no se coma el cartel; el resto de la limpieza ya la hizo
        /// `SteamLobbyManager.SanitizePlayerName` en origen, pero no se puede contar con ella
        /// cuando el origen es un build manual.
        public const int MaxNameLength = 32;

        /// <summary>
        /// En un joiner el evento también llega al registrar AL ANFITRIÓN, y el anfitrión no «se
        /// ha unido» a nada: es la partida en la que se entra. Es el `is_host` que pone el
        /// backend, no una adivinanza por el nombre literal "Host".
        /// </summary>
        public static bool ShouldAnnounce(bool isHost) => !isHost;

        public static string Joined(string name) => $"{Clean(name)} se ha unido a la partida";

        public static string Clean(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Unknown;
            string trimmed = name.Trim();
            return trimmed.Length <= MaxNameLength ? trimmed : trimmed.Substring(0, MaxNameLength);
        }
    }
}
