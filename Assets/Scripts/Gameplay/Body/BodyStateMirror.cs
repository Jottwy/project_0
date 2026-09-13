using System.Collections.Generic;
using BackroomsSurvival.Net;

namespace BackroomsSurvival.Gameplay.Body
{
    /// <summary>
    /// ADR-149 R2a: lee el evento <c>body_state</c> del backend (los 15 bytes de zona, si sangra y lo que frenan las piernas).
    /// Con backend el cuerpo del cliente es un espejo: nunca decide lesiones, solo copia lo que manda el servidor.
    /// </summary>
    public static class BodyStateMirror
    {
        public const string EventType = "body_state";

        /// <summary>Rellena <paramref name="zones"/> si el evento es <c>body_state</c>; las zonas que no vengan quedan a 0.</summary>
        public static bool TryRead(GameEventMsg ev, byte[] zones, out bool bleeding, out float legSpeed)
        {
            bleeding = false;
            legSpeed = 1f;
            if (ev == null || ev.eventType != EventType || zones == null) return false;
            var data = ev.data as Dictionary<string, object>;
            var raw = IPCParse.Get(data, "zones") as object[];
            if (raw == null) return false;
            for (int i = 0; i < zones.Length; i++)
                zones[i] = i < raw.Length ? (byte)(IPCParse.ToLong(raw[i]) & 0xFF) : (byte)0;
            bleeding = IPCParse.B(data, "bleeding");
            legSpeed = data != null && data.ContainsKey("leg_speed") ? IPCParse.F(data, "leg_speed") : 1f;
            return true;
        }
    }
}
