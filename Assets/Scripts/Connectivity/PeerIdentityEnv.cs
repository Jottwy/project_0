using System.Collections.Generic;
using System.Globalization;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>
    /// ADR-136 D1/D2 — cómo viajan al backend la identidad de plataforma propia y la de quien
    /// invitó: dos variables de entorno, la misma puerta que `NET_NAME` y `CONNECT_STEAM`.
    ///
    /// Puro y sin Unity para que la regla —**un 0 es «ninguna» y NO se pone**— tenga test en el
    /// arnés. El backend trata la variable ausente como 0; ponerla a "0" sería decir lo mismo con
    /// más letras y un sitio más donde equivocarse.
    ///
    /// Para el backend son dos números opacos (ADR-135 D2: Rust no sabe que Steam existe); que
    /// hoy sean `SteamId` lo decide quien llama, no esto.
    /// </summary>
    public static class PeerIdentityEnv
    {
        public const string IdentityKey = "PEER_IDENTITY";

        public const string InvitedByKey = "INVITED_BY";

        public static void Apply(IDictionary<string, string> env, ulong selfIdentity, ulong invitedBy)
        {
            if (env == null) return;

            if (selfIdentity != 0UL)
                env[IdentityKey] = selfIdentity.ToString(CultureInfo.InvariantCulture);
            if (invitedBy != 0UL)
                env[InvitedByKey] = invitedBy.ToString(CultureInfo.InvariantCulture);
        }
    }
}
