using System;
using System.Collections.Generic;
using UnityEngine;

// Parte del espejo C# de los tipos de wire del IPC (backend/src/ipc/mod.rs).
// EL CONTRATO DE DECODIFICACION (cabecera de mapa propia, `else r.Skip()` obligatorio,
// defaults por omision, post-fixups fuera del bucle) esta enunciado UNA sola vez, en
// IPCMessages.cs. Leelo alli antes de tocar cualquier Parse de este fichero.

namespace BackroomsSurvival.Net
{
    // ───────────────────────── Local player state ─────────────────────────

    public class StatsMsg
    {
        public float health, hunger, thirst, sanity;
        // ADR-009: server-authoritative stamina, interpolated client-side at 5 Hz.
        public float stamina;

        /// <summary>Reads its own map header — zero intermediate Dictionary/box per field.</summary>
        public static StatsMsg Parse(MsgPackReader r)
        {
            var s = new StatsMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "health")) s.health = r.ReadFloat();
                else if (MsgPackReader.Is(k, "hunger")) s.hunger = r.ReadFloat();
                else if (MsgPackReader.Is(k, "thirst")) s.thirst = r.ReadFloat();
                else if (MsgPackReader.Is(k, "sanity")) s.sanity = r.ReadFloat();
                else if (MsgPackReader.Is(k, "stamina")) s.stamina = r.ReadFloat();
                else r.Skip();
            }
            return s;
        }
    }

    public class LocalPlayerMsg
    {
        public Vector3 position;
        public float rotation;
        public StatsMsg stats = new StatsMsg();
        public float speedModifier = 1f;
        public bool inventoryChanged;
        // ADR-009: echo of the last client input_seq the server has applied.
        public uint ackInputSeq;

        public static LocalPlayerMsg Parse(MsgPackReader r)
        {
            var p = new LocalPlayerMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "position")) p.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "rotation")) p.rotation = r.ReadFloat();
                else if (MsgPackReader.Is(k, "stats")) p.stats = StatsMsg.Parse(r);
                else if (MsgPackReader.Is(k, "speed_modifier")) p.speedModifier = r.ReadFloat();
                else if (MsgPackReader.Is(k, "inventory_changed")) p.inventoryChanged = r.ReadBool();
                else if (MsgPackReader.Is(k, "ack_input_seq")) p.ackInputSeq = (uint)r.ReadInt();
                else r.Skip();
            }
            return p;
        }
    }

    /// <summary>
    /// ADR-009 §2 DeltaUpdate: the 20 Hz authoritative movement delta consumed by
    /// the MovementReconciler — pose to detect desync, velocity to snap to, and
    /// ackInputSeq to align with the client's input ring buffer.
    /// </summary>
    public class MovementDeltaMsg
    {
        public uint tick;
        public uint ackInputSeq;
        public Vector3 position;
        public Vector3 velocity;

        /// <summary>
        /// Root-tagged message (ServerMessage::DeltaUpdate, "type":"delta_update") —
        /// IPCClient.Dispatch already consumed the map header AND the "type" pair, so this reads
        /// only the REMAINING <paramref name="remainingPairs"/> key/value pairs, not a header of
        /// its own.
        /// </summary>
        public static MovementDeltaMsg Parse(MsgPackReader r, int remainingPairs)
        {
            var m = new MovementDeltaMsg();
            for (int i = 0; i < remainingPairs; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "tick")) m.tick = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "ack_input_seq")) m.ackInputSeq = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "position")) m.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "velocity")) m.velocity = r.ReadVec3();
                else r.Skip();
            }
            return m;
        }
    }
}
