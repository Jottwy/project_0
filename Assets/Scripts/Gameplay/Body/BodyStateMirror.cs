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
        public const string HitEventType = "body_hit";

        /// <summary>
        /// ADR-149 R2b: <c>body_hit {zone, cause, damage}</c>, un golpe ya aplicado por el backend con el daño BRUTO. El cliente
        /// rompe con él la prenda que lo recibió. <paramref name="cause"/> es el nombre del <c>DamageType</c> del vendor.
        /// </summary>
        public static bool TryReadHit(GameEventMsg ev, out BodyZone zone, out string cause, out float damage)
        {
            zone = BodyZone.Chest;
            cause = string.Empty;
            damage = 0f;
            if (ev == null || ev.eventType != HitEventType) return false;
            var data = ev.data as Dictionary<string, object>;
            if (data == null || !data.ContainsKey("zone")) return false;
            long raw = IPCParse.L(data, "zone");
            if (raw < 0 || raw >= BodyZones.Count) return false;
            zone = (BodyZone)raw;
            cause = IPCParse.S(data, "cause");
            damage = IPCParse.F(data, "damage");
            return true;
        }

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
