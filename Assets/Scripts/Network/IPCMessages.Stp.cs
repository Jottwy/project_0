using System;
using System.Collections.Generic;
using UnityEngine;

// Parte del espejo C# de los tipos de wire del IPC (backend/src/ipc/mod.rs).
// EL CONTRATO DE DECODIFICACION (cabecera de mapa propia, `else r.Skip()` obligatorio,
// defaults por omision, post-fixups fuera del bucle) esta enunciado UNA sola vez, en
// IPCMessages.cs. Leelo alli antes de tocar cualquier Parse de este fichero.

namespace BackroomsSurvival.Net
{
    // ──────────── STP objects replicated by the host (items, buildings, carryables, ...) ────────────
    // Each `*Msg` is what comes DOWN in world_state; each `*Spec` is what the host sends UP.

    /// <summary>
    /// Phase 1 — one host-authoritative STP world item replicated in world_state.stp_items.
    /// id = network instance id (host-assigned); defId = STP ItemDefinition id (stable).
    /// </summary>
    public class StpItemMsg
    {
        public uint id;
        public int defId;
        public int count;
        public Vector3 position;
        public float rotation;
        // ADR-070 — the item is still falling: `position` MOVES between relays, so follow it
        // instead of pinning the transform. Absent (older backend, or anything born settled)
        // decodes to false and behaves exactly as before.
        public bool settling;

        public static StpItemMsg Parse(MsgPackReader r)
        {
            var m = new StpItemMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "id")) m.id = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "def_id")) m.defId = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "count")) m.count = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "position")) m.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "rotation")) m.rotation = r.ReadFloat();
                else if (MsgPackReader.Is(k, "settling")) m.settling = r.ReadBool();
                else r.Skip();
            }
            return m;
        }
    }

    /// <summary>Outbound spec the host sends via IPCClient.SendSetStpItems (Phase 1).</summary>
    public struct StpItemSpec
    {
        public uint id;
        public int defId;
        public int count;
        public Vector3 position;
        public float rotation;
    }

    /// <summary>
    /// Phase B1 — one host-authoritative STP building piece replicated in
    /// world_state.stp_buildings. id = network instance id (host-assigned); defId =
    /// STP BuildingPieceDefinition id (stable across instances).
    /// </summary>
    public class StpBuildingMsg
    {
        public uint id;
        public int defId;
        public Vector3 position;
        public float rotation;
        // Phase B3 — host-assigned group identity (0 = standalone). Pieces sharing a groupId
        // are bucketed into one BuildingPieceGroup so sockets cohere across all clients.
        public uint groupId;
        // Phase B2 — host-authoritative construction progress (units of each material accepted).
        public List<StpBuildProgressMsg> added = new List<StpBuildProgressMsg>();

        public static StpBuildingMsg Parse(MsgPackReader r)
        {
            var m = new StpBuildingMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "id")) m.id = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "def_id")) m.defId = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "position")) m.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "rotation")) m.rotation = r.ReadFloat();
                else if (MsgPackReader.Is(k, "group_id")) m.groupId = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "added"))
                {
                    int ac = r.ReadArrayHeader();
                    if (ac > 0)
                    {
                        m.added.Capacity = ac;
                        for (int ai = 0; ai < ac; ai++) m.added.Add(StpBuildProgressMsg.Parse(r));
                    }
                }
                else r.Skip();
            }
            return m;
        }
    }

    /// <summary>Phase B2 — one (material → accepted count) entry of a piece's progress.</summary>
    public class StpBuildProgressMsg
    {
        public int materialId;
        public int count;

        public static StpBuildProgressMsg Parse(MsgPackReader r)
        {
            var p = new StpBuildProgressMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "material_id")) p.materialId = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "count")) p.count = (int)r.ReadInt();
                else r.Skip();
            }
            return p;
        }
    }

    /// <summary>
    /// Phase B2.5 — one host-authoritative STP world carryable replicated in
    /// world_state.stp_carryables. id = network instance id (host-assigned); defId =
    /// STP CarryableDefinition id (stable).
    /// </summary>
    public class StpCarryableMsg
    {
        public uint id;
        public int defId;
        public Vector3 position;
        public float rotation;

        public static StpCarryableMsg Parse(MsgPackReader r)
        {
            var m = new StpCarryableMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "id")) m.id = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "def_id")) m.defId = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "position")) m.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "rotation")) m.rotation = r.ReadFloat();
                else r.Skip();
            }
            return m;
        }
    }

    /// <summary>Outbound spec the host sends via IPCClient.SendSetStpCarryables (Phase B2.5).</summary>
    public struct StpCarryableSpec
    {
        public uint id;
        public int defId;
        public Vector3 position;
        public float rotation;
    }

    /// <summary>
    /// Phase B2.6 — one host-authoritative STP scene harvestable (tree/rock) replicated in
    /// world_state.stp_harvestables. id = network instance id; remaining = harvest health
    /// (1.0 full → 0.0 depleted); position lets clients map id → local harvestable.
    /// </summary>
    public class StpHarvestableMsg
    {
        public uint id;
        public Vector3 position;
        public float remaining;

        public static StpHarvestableMsg Parse(MsgPackReader r)
        {
            var m = new StpHarvestableMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "id")) m.id = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "position")) m.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "remaining")) m.remaining = r.ReadFloat();
                else r.Skip();
            }
            return m;
        }
    }

    /// <summary>Outbound spec the host sends via IPCClient.SendSetStpHarvestables (Phase B2.6).</summary>
    public struct StpHarvestableSpec
    {
        public uint id;
        public Vector3 position;
    }
}
