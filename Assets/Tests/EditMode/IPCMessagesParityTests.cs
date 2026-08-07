using System.Collections.Generic;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// C2/C3 of the IPC decoder-streaming barrido (docs/STATE.md's "mayor coste real de la ruta
    /// de parseo"): every *Msg type gained a Parse(MsgPackReader) streaming twin that walks the
    /// wire without materializing a Dictionary/object[]/box per field. C4 (Dispatch switch) has
    /// already deleted the old Parse(object) reference implementation, so this file's job
    /// changed from "old vs new agree" to what it always ultimately needed to prove: real
    /// msgpack bytes, encoded with MsgPackWriter, decode to the EXACT literal values through
    /// Parse(MsgPackReader) — same round-trip discipline as RoomZoneWireTests/PillarNibbleTests.
    ///
    /// Root-tagged types (MovementDeltaMsg, GridChunkDataMsg, GameEventMsg, WorldStateMsg) take
    /// the reader ALREADY past the map header and the "type" pair — that's
    /// IPCClient.Dispatch's job. Here each test plays Dispatch's part by hand: read the header,
    /// match "type", pass n-1 as remainingPairs.
    /// </summary>
    [TestFixture]
    public class IPCMessagesParityTests
    {
        // ── StatsMsg ─────────────────────────────────────────────────────────

        [Test]
        public void StatsMsg_AllFieldsDecodeToLiteralValues()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(5);
            w.WriteString("health"); w.WriteFloat(55.5f);
            w.WriteString("hunger"); w.WriteFloat(40f);
            w.WriteString("thirst"); w.WriteFloat(30f);
            w.WriteString("sanity"); w.WriteFloat(90f);
            w.WriteString("stamina"); w.WriteFloat(70f);

            var msg = StatsMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(55.5f, msg.health);
            Assert.AreEqual(40f, msg.hunger);
            Assert.AreEqual(30f, msg.thirst);
            Assert.AreEqual(90f, msg.sanity);
            Assert.AreEqual(70f, msg.stamina);
        }

        [Test]
        public void StatsMsg_MissingKeysDefaultToZero()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(1);
            w.WriteString("health"); w.WriteFloat(1f);

            var msg = StatsMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(1f, msg.health);
            Assert.AreEqual(0f, msg.hunger);
            Assert.AreEqual(0f, msg.thirst);
            Assert.AreEqual(0f, msg.sanity);
            Assert.AreEqual(0f, msg.stamina);
        }

        // ── LocalPlayerMsg (nested StatsMsg) ─────────────────────────────────

        [Test]
        public void LocalPlayerMsg_AllFieldsIncludingNestedStatsDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(6);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(2f); w.WriteFloat(3f);
            w.WriteString("rotation"); w.WriteFloat(180f);
            w.WriteString("stats"); w.WriteMapHeader(5);
            w.WriteString("health"); w.WriteFloat(50f);
            w.WriteString("hunger"); w.WriteFloat(51f);
            w.WriteString("thirst"); w.WriteFloat(52f);
            w.WriteString("sanity"); w.WriteFloat(53f);
            w.WriteString("stamina"); w.WriteFloat(54f);
            w.WriteString("speed_modifier"); w.WriteFloat(1.5f);
            w.WriteString("inventory_changed"); w.WriteBool(true);
            w.WriteString("ack_input_seq"); w.WriteInt(42);

            var msg = LocalPlayerMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(new Vector3(1, 2, 3), msg.position);
            Assert.AreEqual(180f, msg.rotation);
            Assert.AreEqual(50f, msg.stats.health);
            Assert.AreEqual(51f, msg.stats.hunger);
            Assert.AreEqual(52f, msg.stats.thirst);
            Assert.AreEqual(53f, msg.stats.sanity);
            Assert.AreEqual(54f, msg.stats.stamina);
            Assert.AreEqual(1.5f, msg.speedModifier);
            Assert.IsTrue(msg.inventoryChanged);
            Assert.AreEqual(42u, msg.ackInputSeq);
        }

        // ── MovementDeltaMsg (root-tagged: "type" consumed by the caller) ───────

        [Test]
        public void MovementDeltaMsg_AllFieldsDecodeThroughTaggedEnvelope()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(5);
            w.WriteString("type"); w.WriteString("delta_update");
            w.WriteString("tick"); w.WriteInt(1000);
            w.WriteString("ack_input_seq"); w.WriteInt(7);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(4f); w.WriteFloat(5f); w.WriteFloat(6f);
            w.WriteString("velocity"); w.WriteArrayHeader(3);
            w.WriteFloat(0.1f); w.WriteFloat(0.2f); w.WriteFloat(0.3f);

            var r = new MsgPackReader(w.ToArray());
            int n = r.ReadMapHeader();
            Assert.IsTrue(MsgPackReader.Is(r.ReadKey(), "type"));
            r.ReadString(); // "delta_update", mirrors Dispatch reading the tag
            var msg = MovementDeltaMsg.Parse(r, n - 1);

            Assert.AreEqual(1000u, msg.tick);
            Assert.AreEqual(7u, msg.ackInputSeq);
            Assert.AreEqual(new Vector3(4, 5, 6), msg.position);
            Assert.AreEqual(new Vector3(0.1f, 0.2f, 0.3f), msg.velocity);
        }

        // ── RoomZoneMsg ──────────────────────────────────────────────────────

        [Test]
        public void RoomZoneMsg_AllFieldsDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(5);
            w.WriteString("x0"); w.WriteInt(2);
            w.WriteString("z0"); w.WriteInt(4);
            w.WriteString("x1"); w.WriteInt(10);
            w.WriteString("z1"); w.WriteInt(12);
            w.WriteString("kind"); w.WriteInt((byte)RoomZoneKind.SealedRoom);

            var msg = RoomZoneMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(2, msg.x0);
            Assert.AreEqual(4, msg.z0);
            Assert.AreEqual(10, msg.x1);
            Assert.AreEqual(12, msg.z1);
            Assert.AreEqual((byte)RoomZoneKind.SealedRoom, msg.kindByte);
            Assert.AreEqual(RoomZoneKind.SealedRoom, msg.Kind);
        }

        // ── GridChunkDataMsg (root-tagged; walls 10×10 + room_zones) ────────────

        private static byte[] BuildChunkData(bool withZones)
        {
            var w = new MsgPackWriter();
            // type,cx,cz,layer,walls = 5 unconditional; +room_zones = 6. An undercount here
            // would make the reader stop early and silently skip "walls"/"room_zones" without
            // erroring — see the fieldCount comment on BuildChunkView below for the same trap.
            w.WriteMapHeader(withZones ? 6 : 5);
            w.WriteString("type"); w.WriteString("chunk_data");
            w.WriteString("cx"); w.WriteInt(3);
            w.WriteString("cz"); w.WriteInt(-7);
            w.WriteString("layer"); w.WriteInt(1);
            w.WriteString("walls"); w.WriteArrayHeader(GridChunkDataMsg.Tiles);
            for (int x = 0; x < GridChunkDataMsg.Tiles; x++)
            {
                w.WriteArrayHeader(GridChunkDataMsg.Tiles);
                for (int z = 0; z < GridChunkDataMsg.Tiles; z++)
                    w.WriteInt((x + z) % 16); // varied low-nibble bits; 0x80/0xcc boundary is PillarNibbleTests' job
            }
            if (withZones)
            {
                w.WriteString("room_zones");
                w.WriteArrayHeader(2);
                w.WriteMapHeader(5);
                w.WriteString("x0"); w.WriteInt(0); w.WriteString("z0"); w.WriteInt(0);
                w.WriteString("x1"); w.WriteInt(4); w.WriteString("z1"); w.WriteInt(4);
                w.WriteString("kind"); w.WriteInt((byte)RoomZoneKind.Open);
                w.WriteMapHeader(5);
                w.WriteString("x0"); w.WriteInt(4); w.WriteString("z0"); w.WriteInt(4);
                w.WriteString("x1"); w.WriteInt(8); w.WriteString("z1"); w.WriteInt(8);
                w.WriteString("kind"); w.WriteInt((byte)RoomZoneKind.CorridorSpine);
            }
            return w.ToArray();
        }

        private static GridChunkDataMsg ParseChunkData(byte[] bytes)
        {
            var r = new MsgPackReader(bytes);
            int n = r.ReadMapHeader();
            r.ReadKey(); r.ReadString(); // "type" / "chunk_data"
            return GridChunkDataMsg.Parse(r, n - 1);
        }

        [Test]
        public void GridChunkDataMsg_WithRoomZones_DecodesWallsAndZonesExactly()
        {
            var msg = ParseChunkData(BuildChunkData(withZones: true));

            Assert.AreEqual(3, msg.cx);
            Assert.AreEqual(-7, msg.cz);
            Assert.AreEqual(1, msg.layer);
            for (int x = 0; x < GridChunkDataMsg.Tiles; x++)
                for (int z = 0; z < GridChunkDataMsg.Tiles; z++)
                    Assert.AreEqual((x + z) % 16, msg.walls[x, z], $"walls[{x},{z}]");

            Assert.AreEqual(2, msg.roomZones.Length);
            Assert.AreEqual(RoomZoneKind.Open, msg.roomZones[0].Kind);
            Assert.AreEqual(4, msg.roomZones[0].x1);
            Assert.AreEqual(RoomZoneKind.CorridorSpine, msg.roomZones[1].Kind);
            Assert.AreEqual(8, msg.roomZones[1].z1);
        }

        [Test]
        public void GridChunkDataMsg_WithoutRoomZonesKey_YieldsEmptyNeverNull()
        {
            var msg = ParseChunkData(BuildChunkData(withZones: false));
            Assert.IsNotNull(msg.roomZones, "roomZones nunca debe ser null");
            Assert.AreEqual(0, msg.roomZones.Length);
        }

        // ── RemotePlayerMsg ──────────────────────────────────────────────────

        [Test]
        public void RemotePlayerMsg_AllFieldsDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(12);
            w.WriteString("id"); w.WriteInt(5);
            w.WriteString("name"); w.WriteString("Joel");
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(0f); w.WriteFloat(2f);
            w.WriteString("rotation"); w.WriteFloat(90f);
            w.WriteString("animation"); w.WriteString("walk");
            w.WriteString("crouch"); w.WriteBool(true);
            w.WriteString("pitch"); w.WriteInt(-30);
            w.WriteString("equipment"); w.WriteArrayHeader(4);
            w.WriteInt(11); w.WriteInt(22); w.WriteInt(33); w.WriteInt(44);
            w.WriteString("held_item"); w.WriteInt(77);
            w.WriteString("hit_seq"); w.WriteInt(3);
            w.WriteString("light_on"); w.WriteBool(true); // ADR-042
            w.WriteString("fire_seq"); w.WriteInt(9);     // ADR-042

            var msg = RemotePlayerMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(5, msg.id);
            Assert.AreEqual("Joel", msg.name);
            Assert.AreEqual(new Vector3(1, 0, 2), msg.position);
            Assert.AreEqual(90f, msg.rotation);
            Assert.AreEqual("walk", msg.animation);
            Assert.IsTrue(msg.crouch);
            Assert.AreEqual(-30, msg.pitch);
            CollectionAssert.AreEqual(new[] { 11, 22, 33, 44 }, msg.equipment);
            Assert.AreEqual(77, msg.heldItem);
            Assert.AreEqual(3, msg.hitSeq);
            Assert.IsTrue(msg.lightOn); // ADR-042
            Assert.AreEqual(9, msg.fireSeq); // ADR-042
            Assert.IsFalse(msg.dead);
        }

        [Test]
        public void RemotePlayerMsg_MissingOptionalFieldsDefaultCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(2);
            w.WriteString("id"); w.WriteInt(1);
            w.WriteString("name"); w.WriteString("X");

            var msg = RemotePlayerMsg.Parse(new MsgPackReader(w.ToArray()));
            CollectionAssert.AreEqual(new[] { 0, 0, 0, 0 }, msg.equipment);
            Assert.AreEqual(0, msg.heldItem);
            Assert.AreEqual(0, msg.hitSeq);
            // ADR-042: the v13→v14 compat leg. A backend that never sends these must leave the peer
            // dark and silent, not throw and not carry a stale value.
            Assert.IsFalse(msg.lightOn);
            Assert.AreEqual(0, msg.fireSeq);
            Assert.IsFalse(msg.dead);
            Assert.IsFalse(msg.crouch);
        }

        // ── EntityViewMsg / ItemViewMsg ──────────────────────────────────────

        [Test]
        public void EntityViewMsg_AllFieldsDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(6);
            w.WriteString("id"); w.WriteInt(9);
            w.WriteString("entity_type"); w.WriteString("lurker");
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(2f); w.WriteFloat(3f);
            w.WriteString("rotation"); w.WriteFloat(45f);
            w.WriteString("state"); w.WriteString("chase");
            w.WriteString("health_pct"); w.WriteFloat(0.6f);

            var msg = EntityViewMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(9u, msg.id);
            Assert.AreEqual("lurker", msg.entityType);
            Assert.AreEqual(new Vector3(1, 2, 3), msg.position);
            Assert.AreEqual(45f, msg.rotation);
            Assert.AreEqual("chase", msg.state);
            Assert.AreEqual(0.6f, msg.healthPct);
        }

        [Test]
        public void ItemViewMsg_AllFieldsDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(4);
            w.WriteString("id"); w.WriteInt(12);
            w.WriteString("item_type"); w.WriteString("bandage");
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(0f); w.WriteFloat(1f);
            w.WriteString("quantity"); w.WriteInt(3);

            var msg = ItemViewMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(12u, msg.id);
            Assert.AreEqual("bandage", msg.itemType);
            Assert.AreEqual(new Vector3(1, 0, 1), msg.position);
            Assert.AreEqual(3, msg.quantity);
        }

        // ── StpItemMsg / StpBuildingMsg (+ nested StpBuildProgressMsg) ──────────

        [Test]
        public void StpItemMsg_AllFieldsDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(5);
            w.WriteString("id"); w.WriteInt(1);
            w.WriteString("def_id"); w.WriteInt(2);
            w.WriteString("count"); w.WriteInt(3);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(1f); w.WriteFloat(1f);
            w.WriteString("rotation"); w.WriteFloat(45f);

            var msg = StpItemMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(1u, msg.id);
            Assert.AreEqual(2, msg.defId);
            Assert.AreEqual(3, msg.count);
            Assert.AreEqual(new Vector3(1, 1, 1), msg.position);
            Assert.AreEqual(45f, msg.rotation);
        }

        [Test]
        public void StpBuildingMsg_WithAddedProgress_DecodesNestedListCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(6);
            w.WriteString("id"); w.WriteInt(1);
            w.WriteString("def_id"); w.WriteInt(2);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(0f); w.WriteFloat(1f);
            w.WriteString("rotation"); w.WriteFloat(0f);
            w.WriteString("group_id"); w.WriteInt(9);
            w.WriteString("added"); w.WriteArrayHeader(2);
            w.WriteMapHeader(2);
            w.WriteString("material_id"); w.WriteInt(1);
            w.WriteString("count"); w.WriteInt(5);
            w.WriteMapHeader(2);
            w.WriteString("material_id"); w.WriteInt(2);
            w.WriteString("count"); w.WriteInt(3);

            var msg = StpBuildingMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(1u, msg.id);
            Assert.AreEqual(2, msg.defId);
            Assert.AreEqual(9u, msg.groupId);
            Assert.AreEqual(2, msg.added.Count);
            Assert.AreEqual(1, msg.added[0].materialId);
            Assert.AreEqual(5, msg.added[0].count);
            Assert.AreEqual(2, msg.added[1].materialId);
            Assert.AreEqual(3, msg.added[1].count);
        }

        // ── StpCarryableMsg / StpHarvestableMsg / VerticalDebugMarkerMsg ────────

        [Test]
        public void StpCarryableMsg_AllFieldsDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(4);
            w.WriteString("id"); w.WriteInt(1);
            w.WriteString("def_id"); w.WriteInt(2);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(2f); w.WriteFloat(3f);
            w.WriteString("rotation"); w.WriteFloat(10f);

            var msg = StpCarryableMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(1u, msg.id);
            Assert.AreEqual(2, msg.defId);
            Assert.AreEqual(new Vector3(1, 2, 3), msg.position);
            Assert.AreEqual(10f, msg.rotation);
        }

        [Test]
        public void StpHarvestableMsg_AllFieldsDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("id"); w.WriteInt(1);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(2f); w.WriteFloat(3f);
            w.WriteString("remaining"); w.WriteFloat(0.4f);

            var msg = StpHarvestableMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(1u, msg.id);
            Assert.AreEqual(new Vector3(1, 2, 3), msg.position);
            Assert.AreEqual(0.4f, msg.remaining);
        }

        [Test]
        public void VerticalDebugMarkerMsg_AllFieldsDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(4);
            w.WriteString("id"); w.WriteInt(1);
            w.WriteString("kind"); w.WriteString("stair");
            w.WriteString("world_min"); w.WriteArrayHeader(3);
            w.WriteFloat(-1f); w.WriteFloat(0f); w.WriteFloat(-1f);
            w.WriteString("world_max"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(2f); w.WriteFloat(1f);

            var msg = VerticalDebugMarkerMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(1u, msg.id);
            Assert.AreEqual("stair", msg.kind);
            Assert.AreEqual(new Vector3(-1, 0, -1), msg.worldMin);
            Assert.AreEqual(new Vector3(1, 2, 1), msg.worldMax);
        }

        // ── CorpseViewMsg (nested anonymous item_id/quantity maps) ──────────────

        [Test]
        public void CorpseViewMsg_WithoutItemsKey_YieldsEmptyList()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(7);
            w.WriteString("id"); w.WriteInt(1);
            w.WriteString("owner_id"); w.WriteInt(2);
            w.WriteString("owner_name"); w.WriteString("Fulano");
            w.WriteString("is_chest"); w.WriteBool(false);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(0f); w.WriteFloat(1f);
            w.WriteString("equipment"); w.WriteArrayHeader(4);
            w.WriteInt(1); w.WriteInt(2); w.WriteInt(3); w.WriteInt(4);
            w.WriteString("held_item"); w.WriteInt(9);
            // "items" omitted on purpose — covers the ADR-028 v7-backend-omits-items case.

            var msg = CorpseViewMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(1u, msg.id);
            Assert.AreEqual(2u, msg.ownerId);
            Assert.AreEqual("Fulano", msg.ownerName);
            Assert.IsFalse(msg.isChest);
            Assert.AreEqual(new Vector3(1, 0, 1), msg.position);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, msg.equipment);
            Assert.AreEqual(9, msg.heldItem);
            Assert.AreEqual(0, msg.items.Count);
        }

        [Test]
        public void CorpseViewMsg_WithLootItems_DecodesNestedStacksIncludingNegativeItemId()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(8);
            w.WriteString("id"); w.WriteInt(1);
            w.WriteString("owner_id"); w.WriteInt(2);
            w.WriteString("owner_name"); w.WriteString("Fulano");
            w.WriteString("is_chest"); w.WriteBool(true);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(0f); w.WriteFloat(1f);
            w.WriteString("equipment"); w.WriteArrayHeader(4);
            w.WriteInt(1); w.WriteInt(2); w.WriteInt(3); w.WriteInt(4);
            w.WriteString("held_item"); w.WriteInt(9);
            w.WriteString("items"); w.WriteArrayHeader(2);
            w.WriteMapHeader(2);
            w.WriteString("item_id"); w.WriteInt(-5); // DataIdReference can be negative
            w.WriteString("quantity"); w.WriteInt(3);
            w.WriteMapHeader(2);
            w.WriteString("item_id"); w.WriteInt(7);
            w.WriteString("quantity"); w.WriteInt(1);

            var msg = CorpseViewMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.IsTrue(msg.isChest);
            Assert.AreEqual(2, msg.items.Count);
            Assert.AreEqual(-5, msg.items[0].itemId);
            Assert.AreEqual(3, msg.items[0].quantity);
            Assert.AreEqual(7, msg.items[1].itemId);
            Assert.AreEqual(1, msg.items[1].quantity);
        }

        // ── InterLayerVolumeMsg / VolumetricFaceMsg / LayerBandMsg /
        //    VerticalAccessNodeMsg / BandHeightSpecMsg ────────────────────────

        [Test]
        public void InterLayerVolumeMsg_AllFieldsDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(9);
            w.WriteString("volume_id"); w.WriteInt(1);
            w.WriteString("kind"); w.WriteString("shaft");
            w.WriteString("base_chunk"); w.WriteArrayHeader(2); w.WriteInt(1); w.WriteInt(2);
            w.WriteString("involved_layers"); w.WriteArrayHeader(3); w.WriteInt(0); w.WriteInt(1); w.WriteInt(2);
            w.WriteString("footprint_cell_min"); w.WriteArrayHeader(2); w.WriteInt(0); w.WriteInt(0);
            w.WriteString("footprint_cell_max"); w.WriteArrayHeader(2); w.WriteInt(4); w.WriteInt(4);
            w.WriteString("safety_type"); w.WriteString("safe");
            w.WriteString("future_audio_hint"); w.WriteString("echo");
            w.WriteString("visual_flags"); w.WriteInt(3);

            var msg = InterLayerVolumeMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(1u, msg.volumeId);
            Assert.AreEqual("shaft", msg.kind);
            CollectionAssert.AreEqual(new[] { 1, 2 }, msg.baseChunk);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, msg.involvedLayers);
            CollectionAssert.AreEqual(new[] { 0, 0 }, msg.footprintCellMin);
            CollectionAssert.AreEqual(new[] { 4, 4 }, msg.footprintCellMax);
            Assert.AreEqual("safe", msg.safetyType);
            Assert.AreEqual("echo", msg.futureAudioHint);
            Assert.AreEqual(3, msg.visualFlags);
            CollectionAssert.AreEqual(new string[0], msg.visualHints); // omitted key ⇒ empty, never null
        }

        [Test]
        public void VolumetricFaceMsg_AllFieldsDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("cell"); w.WriteArrayHeader(3); w.WriteInt(1); w.WriteInt(2); w.WriteInt(3);
            w.WriteString("dir"); w.WriteInt(VolumetricGridMsg.DirUp);
            w.WriteString("kind"); w.WriteInt(VolumetricGridMsg.FaceFloor);

            var msg = VolumetricFaceMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(1, msg.x);
            Assert.AreEqual(2, msg.y);
            Assert.AreEqual(3, msg.z);
            Assert.AreEqual(VolumetricGridMsg.DirUp, msg.dir);
            Assert.AreEqual(VolumetricGridMsg.FaceFloor, msg.kind);
        }

        [Test]
        public void LayerBandMsg_AllFieldsDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(8);
            w.WriteString("band_id"); w.WriteInt(1);
            w.WriteString("layer"); w.WriteInt(2);
            w.WriteString("profile"); w.WriteString("standard");
            w.WriteString("profile_code"); w.WriteInt(4);
            w.WriteString("accessible"); w.WriteBool(true);
            w.WriteString("danger_profile"); w.WriteString("low");
            w.WriteString("resource_profile"); w.WriteString("rich");
            w.WriteString("anomaly_profile"); w.WriteString("none");

            var msg = LayerBandMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(1u, msg.bandId);
            Assert.AreEqual(2, msg.layer);
            Assert.AreEqual("standard", msg.profile);
            Assert.AreEqual(4, msg.profileCode);
            Assert.IsTrue(msg.accessible);
            Assert.AreEqual("low", msg.dangerProfile);
            Assert.AreEqual("rich", msg.resourceProfile);
            Assert.AreEqual("none", msg.anomalyProfile);
        }

        [Test]
        public void VerticalAccessNodeMsg_AllFieldsDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(8);
            w.WriteString("access_id"); w.WriteInt(1);
            w.WriteString("access_type"); w.WriteString("stair");
            w.WriteString("access_type_code"); w.WriteInt(2);
            w.WriteString("from_layer"); w.WriteInt(0);
            w.WriteString("to_layer"); w.WriteInt(1);
            w.WriteString("footprint_cell_min"); w.WriteArrayHeader(2); w.WriteInt(1); w.WriteInt(1);
            w.WriteString("footprint_cell_max"); w.WriteArrayHeader(2); w.WriteInt(3); w.WriteInt(3);
            w.WriteString("explicit"); w.WriteBool(true);

            var msg = VerticalAccessNodeMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(1u, msg.accessId);
            Assert.AreEqual("stair", msg.accessType);
            Assert.AreEqual(2, msg.accessTypeCode);
            Assert.AreEqual(0, msg.fromLayer);
            Assert.AreEqual(1, msg.toLayer);
            CollectionAssert.AreEqual(new[] { 1, 1 }, msg.footprintCellMin);
            CollectionAssert.AreEqual(new[] { 3, 3 }, msg.footprintCellMax);
            Assert.IsTrue(msg.explicitAccess);
        }

        [Test]
        public void BandHeightSpecMsg_AllFieldsDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(5);
            w.WriteString("band_index"); w.WriteInt(0);
            w.WriteString("layer"); w.WriteInt(1);
            w.WriteString("room_height"); w.WriteFloat(4f);
            w.WriteString("total_height"); w.WriteFloat(7f);
            w.WriteString("neighbor_max_room_height"); w.WriteFloat(5f);

            var msg = BandHeightSpecMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(0, msg.bandIndex);
            Assert.AreEqual(1, msg.layer);
            Assert.AreEqual(4f, msg.roomHeight);
            Assert.AreEqual(7f, msg.totalHeight);
            Assert.AreEqual(5f, msg.neighborMaxRoomHeight);
        }

        // ── VolumetricGridMsg (nested faces/layerBands/verticalAccess/heightBands) ──

        [Test]
        public void VolumetricGridMsg_AllFieldsIncludingNestedFacesDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            // active,column_id,column_coord,source,dims,cell_size_xz,layer_height,origin_world,
            // base_layer,cells,faces,open_cell_count,solid_cell_count,vertical_connection_count,
            // valid_vertical_opening_count,atrium_span = 16.
            w.WriteMapHeader(16);
            w.WriteString("active"); w.WriteBool(true);
            w.WriteString("column_id"); w.WriteInt(123456);
            w.WriteString("column_coord"); w.WriteArrayHeader(2); w.WriteInt(1); w.WriteInt(2);
            w.WriteString("source"); w.WriteString("showcase");
            w.WriteString("dims"); w.WriteArrayHeader(3); w.WriteInt(4); w.WriteInt(3); w.WriteInt(4);
            w.WriteString("cell_size_xz"); w.WriteFloat(5f);
            w.WriteString("layer_height"); w.WriteFloat(7f);
            w.WriteString("origin_world"); w.WriteArrayHeader(3);
            w.WriteFloat(0f); w.WriteFloat(0f); w.WriteFloat(0f);
            w.WriteString("base_layer"); w.WriteInt(0);
            w.WriteString("cells"); w.WriteArrayHeader(4);
            w.WriteInt(0); w.WriteInt(1); w.WriteInt(2); w.WriteInt(8);
            w.WriteString("faces"); w.WriteArrayHeader(2);
            w.WriteMapHeader(3);
            w.WriteString("cell"); w.WriteArrayHeader(3); w.WriteInt(0); w.WriteInt(0); w.WriteInt(0);
            w.WriteString("dir"); w.WriteInt(VolumetricGridMsg.DirNorth);
            w.WriteString("kind"); w.WriteInt(VolumetricGridMsg.FaceWall);
            w.WriteMapHeader(3);
            w.WriteString("cell"); w.WriteArrayHeader(3); w.WriteInt(1); w.WriteInt(0); w.WriteInt(1);
            w.WriteString("dir"); w.WriteInt(VolumetricGridMsg.DirUp);
            w.WriteString("kind"); w.WriteInt(VolumetricGridMsg.FaceCeiling);
            w.WriteString("open_cell_count"); w.WriteInt(30);
            w.WriteString("solid_cell_count"); w.WriteInt(18);
            w.WriteString("vertical_connection_count"); w.WriteInt(2);
            w.WriteString("valid_vertical_opening_count"); w.WriteInt(1);
            w.WriteString("atrium_span"); w.WriteBool(false);

            var msg = VolumetricGridMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.IsNotNull(msg);
            Assert.IsTrue(msg.active);
            Assert.AreEqual(123456ul, msg.columnId);
            CollectionAssert.AreEqual(new[] { 1, 2 }, msg.columnCoord);
            Assert.AreEqual("showcase", msg.source);
            Assert.AreEqual(4, msg.nx); Assert.AreEqual(3, msg.ny); Assert.AreEqual(4, msg.nz);
            Assert.AreEqual(5f, msg.cellSizeXZ);
            Assert.AreEqual(7f, msg.layerHeight);
            Assert.AreEqual(Vector3.zero, msg.originWorld);
            Assert.AreEqual(0, msg.baseLayer);
            CollectionAssert.AreEqual(new byte[] { 0, 1, 2, 8 }, msg.cells);
            Assert.AreEqual(2, msg.faces.Count);
            Assert.AreEqual(0, msg.faces[0].x); Assert.AreEqual(VolumetricGridMsg.DirNorth, msg.faces[0].dir);
            Assert.AreEqual(1, msg.faces[1].x); Assert.AreEqual(VolumetricGridMsg.FaceCeiling, msg.faces[1].kind);
            Assert.AreEqual(30, msg.openCellCount);
            Assert.AreEqual(18, msg.solidCellCount);
            Assert.AreEqual(2, msg.verticalConnectionCount);
            Assert.AreEqual(1, msg.validVerticalOpeningCount);
            Assert.IsFalse(msg.atriumSpan);
            Assert.AreEqual(0, msg.layerBands.Count);
            Assert.AreEqual(0, msg.verticalAccess.Count);
            Assert.AreEqual(0, msg.heightBands.Count);
        }

        [Test]
        public void VolumetricGridMsg_NilValueReturnsNull()
        {
            var w = new MsgPackWriter();
            w.WriteNil();
            Assert.IsNull(VolumetricGridMsg.Parse(new MsgPackReader(w.ToArray())));
        }

        [Test]
        public void VolumetricGridMsg_ZeroOrNegativeCellSizeAndLayerHeightFallBackToDefaults()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(2);
            w.WriteString("cell_size_xz"); w.WriteFloat(0f);
            w.WriteString("layer_height"); w.WriteFloat(-1f);

            var msg = VolumetricGridMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(5f, msg.cellSizeXZ);
            Assert.AreEqual(7f, msg.layerHeight);
        }

        // ── ChunkViewMsg (the big one: nested interLayerVolumes + volumetricGrid,
        //    post-fixups, SplitPackedLayout) ──────────────────────────────────

        private static byte[] BuildChunkView(bool withVolumes, bool withVolumetricGrid, int layoutGridSize)
        {
            int cellCount = layoutGridSize * layoutGridSize;
            int vEdgeCount = (layoutGridSize + 1) * layoutGridSize;
            int hEdgeCount = layoutGridSize * (layoutGridSize + 1);
            int totalCells = cellCount + vEdgeCount + hEdgeCount;

            var w = new MsgPackWriter();
            // 23 unconditional fields (chunk_schema .. vertical_flags), + 1 for inter_layer_volumes
            // if present, + 1 for volumetric_grid if present. Miscounting this would make the
            // reader stop early and silently skip the volumes/grid fields without erroring — keep
            // in sync with the WriteString calls below.
            int fieldCount = 23 + (withVolumes ? 1 : 0) + (withVolumetricGrid ? 1 : 0);
            w.WriteMapHeader(fieldCount);
            w.WriteString("chunk_schema"); w.WriteInt(1);
            w.WriteString("pos"); w.WriteArrayHeader(2); w.WriteInt(3); w.WriteInt(-2);
            w.WriteString("layer"); w.WriteInt(0);
            w.WriteString("layer_y"); w.WriteFloat(0f);
            w.WriteString("template_id"); w.WriteInt(5);
            w.WriteString("rotation"); w.WriteInt(90);
            w.WriteString("mirrored"); w.WriteBool(false);
            w.WriteString("state"); w.WriteString("stabilized");
            w.WriteString("has_workbench"); w.WriteBool(true);
            w.WriteString("layout_grid_size"); w.WriteInt(layoutGridSize);
            w.WriteString("layout_cell_size"); w.WriteFloat(5f);
            w.WriteString("layout_cells"); w.WriteArrayHeader(totalCells);
            for (int i = 0; i < totalCells; i++) w.WriteInt(i % 5);
            w.WriteString("edge_openings"); w.WriteInt(3);
            w.WriteString("macro_id"); w.WriteInt(42);
            w.WriteString("zone_kind"); w.WriteInt(2);
            w.WriteString("macro_local"); w.WriteArrayHeader(2); w.WriteInt(1); w.WriteInt(1);
            w.WriteString("macro_size"); w.WriteArrayHeader(2); w.WriteInt(2); w.WriteInt(2);
            w.WriteString("floor_level"); w.WriteInt(0);
            w.WriteString("floor_profile"); w.WriteInt(1);
            w.WriteString("ceiling_profile"); w.WriteInt(2);
            w.WriteString("light_profile"); w.WriteInt(1);
            w.WriteString("anomaly_flags"); w.WriteInt(0);
            w.WriteString("vertical_flags"); w.WriteInt(0);
            if (withVolumes)
            {
                w.WriteString("inter_layer_volumes"); w.WriteArrayHeader(1);
                w.WriteMapHeader(9);
                w.WriteString("volume_id"); w.WriteInt(1);
                w.WriteString("kind"); w.WriteString("shaft");
                w.WriteString("base_chunk"); w.WriteArrayHeader(2); w.WriteInt(3); w.WriteInt(-2);
                w.WriteString("involved_layers"); w.WriteArrayHeader(1); w.WriteInt(0);
                w.WriteString("footprint_cell_min"); w.WriteArrayHeader(2); w.WriteInt(0); w.WriteInt(0);
                w.WriteString("footprint_cell_max"); w.WriteArrayHeader(2); w.WriteInt(2); w.WriteInt(2);
                w.WriteString("safety_type"); w.WriteString("safe");
                w.WriteString("future_audio_hint"); w.WriteString("");
                w.WriteString("visual_flags"); w.WriteInt(0);
            }
            if (withVolumetricGrid)
            {
                w.WriteString("volumetric_grid"); w.WriteMapHeader(5);
                w.WriteString("active"); w.WriteBool(true);
                w.WriteString("column_id"); w.WriteInt(9);
                w.WriteString("dims"); w.WriteArrayHeader(3); w.WriteInt(2); w.WriteInt(2); w.WriteInt(2);
                w.WriteString("cell_size_xz"); w.WriteFloat(5f);
                w.WriteString("layer_height"); w.WriteFloat(7f);
            }
            return w.ToArray();
        }

        [Test]
        public void ChunkViewMsg_FullFieldsWithVolumesAndVolumetricGrid_DecodeCorrectly()
        {
            var bytes = BuildChunkView(withVolumes: true, withVolumetricGrid: true, layoutGridSize: 10);
            var msg = ChunkViewMsg.Parse(new MsgPackReader(bytes));

            Assert.AreEqual(1, msg.chunkSchema);
            CollectionAssert.AreEqual(new[] { 3, -2 }, msg.pos);
            Assert.AreEqual(0, msg.layer);
            Assert.AreEqual(5, msg.templateId);
            Assert.AreEqual(90, msg.rotation);
            Assert.IsFalse(msg.mirrored);
            Assert.AreEqual("stabilized", msg.state);
            Assert.IsTrue(msg.hasWorkbench);
            Assert.AreEqual(10, msg.layoutGridSize);
            Assert.AreEqual(5f, msg.layoutCellSize);
            Assert.AreEqual(320, msg.layoutCells.Length); // cellCount+vEdge+hEdge for g=10
            Assert.AreEqual(3, msg.edgeOpenings);
            Assert.AreEqual(42u, msg.macroId);
            Assert.AreEqual(2, msg.zoneKind);
            CollectionAssert.AreEqual(new[] { 1, 1 }, msg.macroLocal);
            CollectionAssert.AreEqual(new[] { 2, 2 }, msg.macroSize);
            Assert.AreEqual(1, msg.interLayerVolumes.Count);
            Assert.AreEqual(1u, msg.interLayerVolumes[0].volumeId);
            Assert.IsTrue(msg.HasVolumetricGrid);
            Assert.AreEqual(9ul, msg.volumetricGrid.columnId);

            // SplitPackedLayout() derived state — g=10, full pack (cells+edges) present.
            Assert.IsTrue(msg.hasBackendLayout);
            Assert.IsTrue(msg.hasEdgeLayout);
            Assert.AreEqual(100, msg.cellFlags.Length);
            Assert.AreEqual(110, msg.verticalEdges.Length);
            Assert.AreEqual(110, msg.horizontalEdges.Length);
        }

        [Test]
        public void ChunkViewMsg_WithoutVolumesOrVolumetricGrid_DefaultsStayEmpty()
        {
            var bytes = BuildChunkView(withVolumes: false, withVolumetricGrid: false, layoutGridSize: 10);
            var msg = ChunkViewMsg.Parse(new MsgPackReader(bytes));

            Assert.IsFalse(msg.HasVolumetricGrid);
            Assert.IsNull(msg.volumetricGrid);
            Assert.AreEqual(0, msg.interLayerVolumes.Count);
        }

        [Test]
        public void ChunkViewMsg_ZeroOrNegativePostFixupFieldsFallBackToDefaults()
        {
            // chunk_schema<=0→1, layout_cell_size<=0→5f, macro_size[i]<=0→1 — same fixups
            // Parse(object) used to apply after the loop; the streaming Parse must too.
            var w = new MsgPackWriter();
            w.WriteMapHeader(6);
            w.WriteString("chunk_schema"); w.WriteInt(0);
            w.WriteString("layout_cell_size"); w.WriteFloat(-1f);
            w.WriteString("layout_grid_size"); w.WriteInt(0); // Mathf.Max(1, ...) inline fixup
            w.WriteString("macro_size"); w.WriteArrayHeader(2); w.WriteInt(0); w.WriteInt(-3);
            w.WriteString("pos"); w.WriteArrayHeader(2); w.WriteInt(0); w.WriteInt(0);
            w.WriteString("layer"); w.WriteInt(0);

            var msg = ChunkViewMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(1, msg.chunkSchema);
            Assert.AreEqual(5f, msg.layoutCellSize);
            Assert.AreEqual(1, msg.layoutGridSize);
            CollectionAssert.AreEqual(new[] { 1, 1 }, msg.macroSize);
        }

        [Test]
        public void ChunkViewMsg_CellsOnlyLegacyLayout_SetsBackendLayoutWithoutEdges()
        {
            // layout_cells shorter than cells+edges (no edge tail) — the "old layout" branch of
            // SplitPackedLayout. Header claims cellCount only, not the full pack.
            const int g = 10;
            int cellCount = g * g;
            var w = new MsgPackWriter();
            w.WriteMapHeader(11);
            w.WriteString("chunk_schema"); w.WriteInt(1);
            w.WriteString("pos"); w.WriteArrayHeader(2); w.WriteInt(0); w.WriteInt(0);
            w.WriteString("layer"); w.WriteInt(0);
            w.WriteString("layer_y"); w.WriteFloat(0f);
            w.WriteString("template_id"); w.WriteInt(1);
            w.WriteString("rotation"); w.WriteInt(0);
            w.WriteString("mirrored"); w.WriteBool(false);
            w.WriteString("state"); w.WriteString("random");
            w.WriteString("has_workbench"); w.WriteBool(false);
            w.WriteString("layout_grid_size"); w.WriteInt(g);
            w.WriteString("layout_cells"); w.WriteArrayHeader(cellCount);
            for (int i = 0; i < cellCount; i++) w.WriteInt(1);

            var msg = ChunkViewMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.IsTrue(msg.hasBackendLayout);
            Assert.IsFalse(msg.hasEdgeLayout);
            Assert.AreEqual(cellCount, msg.cellFlags.Length);
            Assert.AreEqual(0, msg.verticalEdges.Length);
            Assert.AreEqual(0, msg.horizontalEdges.Length);
        }

        // ── WorldStateMsg / GameEventMsg (the two remaining root-tagged types) ──

        /// <summary>Reads the {"type":expectedType, ...} envelope exactly like
        /// IPCClient.Dispatch does, and returns the reader positioned for the message's own
        /// Parse(reader, remainingPairs).</summary>
        private static (MsgPackReader reader, int remaining) OpenTaggedFrame(byte[] frame, string expectedType)
        {
            var reader = new MsgPackReader(frame);
            int n = reader.ReadMapHeader();
            Assert.IsTrue(MsgPackReader.Is(reader.ReadKey(), "type"), "\"type\" debe ser la primera clave (serde-tag first)");
            Assert.AreEqual(expectedType, reader.ReadString());
            return (reader, n - 1);
        }

        [Test]
        public void WorldStateMsg_ScalarsAndEveryListDecodeThroughTaggedEnvelope()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(6);
            w.WriteString("type"); w.WriteString("world_state");
            w.WriteString("tick"); w.WriteInt(1000);
            w.WriteString("world_seed"); w.WriteInt(42);
            w.WriteString("world_revision"); w.WriteInt(3);
            w.WriteString("local_player"); w.WriteMapHeader(1);
            w.WriteString("rotation"); w.WriteFloat(180f);
            w.WriteString("remote_players"); w.WriteArrayHeader(2);
            w.WriteMapHeader(1); w.WriteString("id"); w.WriteInt(1);
            w.WriteMapHeader(1); w.WriteString("id"); w.WriteInt(2);

            var (reader, remaining) = OpenTaggedFrame(w.ToArray(), "world_state");
            var ws = WorldStateMsg.Parse(reader, remaining);

            Assert.AreEqual(1000, ws.tick);
            Assert.AreEqual(42, ws.worldSeed);
            Assert.AreEqual(3, ws.worldRevision);
            Assert.AreEqual(180f, ws.localPlayer.rotation);
            Assert.AreEqual(2, ws.remotePlayers.Count);
            Assert.AreEqual(1, ws.remotePlayers[0].id);
            Assert.AreEqual(2, ws.remotePlayers[1].id);

            // Every optional list omitted from the wire stays empty, never null.
            Assert.IsNotNull(ws.visibleChunks); Assert.AreEqual(0, ws.visibleChunks.Count);
            Assert.IsNotNull(ws.visibleEntities); Assert.AreEqual(0, ws.visibleEntities.Count);
            Assert.IsNotNull(ws.visibleItems); Assert.AreEqual(0, ws.visibleItems.Count);
            Assert.IsNotNull(ws.verticalDebugMarkers); Assert.AreEqual(0, ws.verticalDebugMarkers.Count);
            Assert.IsNotNull(ws.stpItems); Assert.AreEqual(0, ws.stpItems.Count);
            Assert.IsNotNull(ws.stpBuildings); Assert.AreEqual(0, ws.stpBuildings.Count);
            Assert.IsNotNull(ws.stpCarryables); Assert.AreEqual(0, ws.stpCarryables.Count);
            Assert.IsNotNull(ws.stpHarvestables); Assert.AreEqual(0, ws.stpHarvestables.Count);
            Assert.IsNotNull(ws.visibleCorpses); Assert.AreEqual(0, ws.visibleCorpses.Count);
        }

        [Test]
        public void WorldStateMsg_VisibleChunksAndCorpsesListsDecodeCorrectly()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("world_state");
            w.WriteString("visible_chunks"); w.WriteArrayHeader(2);
            w.WriteMapHeader(2); w.WriteString("pos"); w.WriteArrayHeader(2); w.WriteInt(1); w.WriteInt(1);
            w.WriteString("layer"); w.WriteInt(0);
            w.WriteMapHeader(2); w.WriteString("pos"); w.WriteArrayHeader(2); w.WriteInt(2); w.WriteInt(2);
            w.WriteString("layer"); w.WriteInt(1);
            w.WriteString("visible_corpses"); w.WriteArrayHeader(1);
            w.WriteMapHeader(1); w.WriteString("id"); w.WriteInt(77);

            var (reader, remaining) = OpenTaggedFrame(w.ToArray(), "world_state");
            var ws = WorldStateMsg.Parse(reader, remaining);

            Assert.AreEqual(2, ws.visibleChunks.Count);
            CollectionAssert.AreEqual(new[] { 1, 1 }, ws.visibleChunks[0].pos);
            Assert.AreEqual(1, ws.visibleChunks[1].layer);
            Assert.AreEqual(1, ws.visibleCorpses.Count);
            Assert.AreEqual(77u, ws.visibleCorpses[0].id);
        }

        [Test]
        public void GameEventMsg_EventTypeAndDataMaterializeAsObjectTree()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("event");
            w.WriteString("event_type"); w.WriteString("player_respawned");
            w.WriteString("data"); w.WriteMapHeader(1);
            w.WriteString("reason"); w.WriteString("timeout");

            var (reader, remaining) = OpenTaggedFrame(w.ToArray(), "event");
            var e = GameEventMsg.Parse(reader, remaining);

            Assert.AreEqual("player_respawned", e.eventType);
            var data = e.data as Dictionary<string, object>;
            Assert.IsNotNull(data,
                "\"data\" sigue materializándose como Dictionary<string,object> — todo consumidor de " +
                "GameEventMsg.data (PvpFeedbackController, StpPickupController, ...) usa IPCParse sobre este árbol");
            Assert.AreEqual("timeout", IPCParse.S(data, "reason"));
        }

        [Test]
        public void GameEventMsg_NilDataLeavesDataNull()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("event");
            w.WriteString("event_type"); w.WriteString("chunk_teleported");
            w.WriteString("data"); w.WriteNil();

            var (reader, remaining) = OpenTaggedFrame(w.ToArray(), "event");
            var e = GameEventMsg.Parse(reader, remaining);

            Assert.AreEqual("chunk_teleported", e.eventType);
            Assert.IsNull(e.data);
        }

        // ── InventoryRestorer.ParseStacks (ADR-045) ──────────────────────────
        //
        // Fija el bug real de playtest: MsgPackReader.ReadValue() materializa un array msgpack
        // como object[] (ver su doc-comment + ReadArray), nunca List<object>. ParseStacks
        // comprobaba List<object> — un tipo que el reader jamas produce — asi que el cast fallaba
        // siempre y "inventory_restored" llegaba "unparsable" el 100% de las veces, no de forma
        // intermitente. Estos tests decodifican bytes msgpack REALES (MsgPackWriter -> el mismo
        // shape exacto que emite game_loop.rs -> MsgPackReader), no un arbol de objetos fabricado
        // a mano, para que la prueba dependa del decoder de verdad, igual que el resto del fichero.

        [Test]
        public void InventoryRestorer_ParseStacks_DecodesRealBackendPayload()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("event");
            w.WriteString("event_type"); w.WriteString("inventory_restored");
            w.WriteString("data"); w.WriteMapHeader(1);
            w.WriteString("items"); w.WriteArrayHeader(2);
            w.WriteMapHeader(2);
            w.WriteString("item_id"); w.WriteInt(-52379);
            w.WriteString("quantity"); w.WriteInt(2);
            w.WriteMapHeader(2);
            w.WriteString("item_id"); w.WriteInt(3621376);
            w.WriteString("quantity"); w.WriteInt(30);

            var (reader, remaining) = OpenTaggedFrame(w.ToArray(), "event");
            var e = GameEventMsg.Parse(reader, remaining);

            var stacks = InventoryRestorer.ParseStacks(e.data);
            Assert.IsNotNull(stacks, "un payload bien formado nunca debe leerse como unparsable");
            Assert.AreEqual(2, stacks.Count);
            Assert.AreEqual((-52379, 2), stacks[0]);
            Assert.AreEqual((3621376, 30), stacks[1]);
        }

        [Test]
        public void InventoryRestorer_ParseStacks_DropsZeroQuantityAndZeroIdEntries()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("event");
            w.WriteString("event_type"); w.WriteString("inventory_restored");
            w.WriteString("data"); w.WriteMapHeader(1);
            w.WriteString("items"); w.WriteArrayHeader(3);
            w.WriteMapHeader(2);
            w.WriteString("item_id"); w.WriteInt(111);
            w.WriteString("quantity"); w.WriteInt(0); // filtered: qty must be > 0
            w.WriteMapHeader(2);
            w.WriteString("item_id"); w.WriteInt(0); // filtered: id must be != 0
            w.WriteString("quantity"); w.WriteInt(5);
            w.WriteMapHeader(2);
            w.WriteString("item_id"); w.WriteInt(222);
            w.WriteString("quantity"); w.WriteInt(7);

            var (reader, remaining) = OpenTaggedFrame(w.ToArray(), "event");
            var e = GameEventMsg.Parse(reader, remaining);

            var stacks = InventoryRestorer.ParseStacks(e.data);
            Assert.IsNotNull(stacks);
            Assert.AreEqual(1, stacks.Count, "solo la entrada valida debe sobrevivir el filtro");
            Assert.AreEqual((222, 7), stacks[0]);
        }

        [Test]
        public void InventoryRestorer_ParseStacks_EmptyItemsArrayIsAnEmptyListNotUnparsable()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("event");
            w.WriteString("event_type"); w.WriteString("inventory_restored");
            w.WriteString("data"); w.WriteMapHeader(1);
            w.WriteString("items"); w.WriteArrayHeader(0);

            var (reader, remaining) = OpenTaggedFrame(w.ToArray(), "event");
            var e = GameEventMsg.Parse(reader, remaining);

            var stacks = InventoryRestorer.ParseStacks(e.data);
            Assert.IsNotNull(stacks, "un array vacio es un payload valido, no unparsable");
            Assert.AreEqual(0, stacks.Count);
        }

        /// Control negativo: si "items" NO es un array (p.ej. viene de un evento con otro shape,
        /// o de un cliente/backend desincronizado), ParseStacks debe devolver null explicitamente
        /// — el contrato que InventoryRestorer.OnGameEvent usa para decidir "unparsable, ignorar"
        /// en vez de aplicar un pending vacio por accidente.
        [Test]
        public void InventoryRestorer_ParseStacks_NonArrayItemsReturnsNull()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("event");
            w.WriteString("event_type"); w.WriteString("inventory_restored");
            w.WriteString("data"); w.WriteMapHeader(1);
            w.WriteString("items"); w.WriteString("not an array");

            var (reader, remaining) = OpenTaggedFrame(w.ToArray(), "event");
            var e = GameEventMsg.Parse(reader, remaining);

            var stacks = InventoryRestorer.ParseStacks(e.data);
            Assert.IsNull(stacks);
        }

        // ── InventoryRestorer.ParseStacksV2 (ADR-045 Fase 3) ─────────────────
        //
        // Mismos bytes msgpack reales (MsgPackWriter -> el shape exacto que game_loop.rs emite
        // cuando inventory_v2 no esta vacio -> MsgPackReader), no un arbol fabricado a mano.

        [Test]
        public void InventoryRestorer_ParseStacksV2_DecodesRealBackendPayloadWithProps()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("event");
            w.WriteString("event_type"); w.WriteString("inventory_restored");
            w.WriteString("data"); w.WriteMapHeader(1);
            w.WriteString("items"); w.WriteArrayHeader(1);
            w.WriteMapHeader(5);
            w.WriteString("item_id"); w.WriteInt(-52379);
            w.WriteString("quantity"); w.WriteInt(2);
            w.WriteString("container"); w.WriteInt(1);
            w.WriteString("slot"); w.WriteInt(5);
            w.WriteString("props"); w.WriteArrayHeader(1);
            w.WriteMapHeader(2);
            w.WriteString("id"); w.WriteInt(10);
            w.WriteString("value"); w.WriteFloat(0.75f);

            var (reader, remaining) = OpenTaggedFrame(w.ToArray(), "event");
            var e = GameEventMsg.Parse(reader, remaining);

            var stacks = InventoryRestorer.ParseStacksV2(e.data);
            Assert.IsNotNull(stacks, "un payload v2 bien formado no debe caer al parse legado");
            Assert.AreEqual(1, stacks.Count);
            Assert.AreEqual(-52379, stacks[0].itemId);
            Assert.AreEqual(2, stacks[0].quantity);
            Assert.AreEqual(1, stacks[0].container);
            Assert.AreEqual(5, stacks[0].slot);
            Assert.IsNotNull(stacks[0].props);
            Assert.AreEqual(1, stacks[0].props.Count);
            Assert.AreEqual(10, stacks[0].props[0].id);
            Assert.AreEqual(0.75, stacks[0].props[0].value, 1e-4);
        }

        /// Un payload legado (sin container/slot en ninguna entrada) NO es v2 — ParseStacksV2
        /// debe devolver null para que OnGameEvent caiga al ParseStacks de siempre, no aplicar
        /// container=0/slot=0 por defecto (colisionaria con un item real en ese slot).
        [Test]
        public void InventoryRestorer_ParseStacksV2_LegacyPayloadReturnsNullForFallback()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("event");
            w.WriteString("event_type"); w.WriteString("inventory_restored");
            w.WriteString("data"); w.WriteMapHeader(1);
            w.WriteString("items"); w.WriteArrayHeader(1);
            w.WriteMapHeader(2);
            w.WriteString("item_id"); w.WriteInt(42);
            w.WriteString("quantity"); w.WriteInt(3);

            var (reader, remaining) = OpenTaggedFrame(w.ToArray(), "event");
            var e = GameEventMsg.Parse(reader, remaining);

            Assert.IsNull(InventoryRestorer.ParseStacksV2(e.data));
            var legacy = InventoryRestorer.ParseStacks(e.data);
            Assert.IsNotNull(legacy);
            Assert.AreEqual((42, 3), legacy[0]);
        }

        /// Un item v2 sin props (item sin propiedades de instancia) es valido — props queda null,
        /// no una lista vacia forzada ni un fallo de parseo.
        [Test]
        public void InventoryRestorer_ParseStacksV2_ItemWithoutPropsParsesWithNullProps()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("type"); w.WriteString("event");
            w.WriteString("event_type"); w.WriteString("inventory_restored");
            w.WriteString("data"); w.WriteMapHeader(1);
            w.WriteString("items"); w.WriteArrayHeader(1);
            w.WriteMapHeader(5);
            w.WriteString("item_id"); w.WriteInt(999);
            w.WriteString("quantity"); w.WriteInt(1);
            w.WriteString("container"); w.WriteInt(0);
            w.WriteString("slot"); w.WriteInt(0);
            w.WriteString("props"); w.WriteArrayHeader(0);

            var (reader, remaining) = OpenTaggedFrame(w.ToArray(), "event");
            var e = GameEventMsg.Parse(reader, remaining);

            var stacks = InventoryRestorer.ParseStacksV2(e.data);
            Assert.IsNotNull(stacks);
            Assert.AreEqual(1, stacks.Count);
            Assert.IsNull(stacks[0].props, "sin propiedades, props debe quedar null, no lista vacia");
        }

        // ── PeerVoiceMsg (ADR-046) ───────────────────────────────────────────

        [Test]
        public void PeerVoiceMsg_DecodesSpeakerSeqAndAudioBytes()
        {
            var audio = new byte[120];
            for (int i = 0; i < audio.Length; i++) audio[i] = (byte)((i * 31 + 7) & 0xff);

            var w = new MsgPackWriter();
            w.WriteMapHeader(4);
            w.WriteString("type"); w.WriteString("peer_voice");
            w.WriteString("peer_id"); w.WriteInt(4097);
            w.WriteString("seq"); w.WriteInt(65535);
            w.WriteString("data"); w.WriteBin(audio);

            var (reader, remaining) = OpenTaggedFrame(w.ToArray(), "peer_voice");
            var v = PeerVoiceMsg.Parse(reader, remaining);

            Assert.AreEqual(4097, v.peerId);
            Assert.AreEqual(65535, v.seq, "seq debe llegar entero hasta el borde del u16");
            Assert.AreEqual(audio, v.data, "el audio debe sobrevivir byte a byte");
        }

        /// <summary>
        /// Un frame sin `data` (o con audio vacío) deja el array VACÍO, nunca null: el
        /// reproductor solo tiene que tratar "esta trama no traía audio", no un caso de nulo
        /// aparte. Y una clave desconocida de un backend más nuevo no puede romper el parseo.
        /// </summary>
        [Test]
        public void PeerVoiceMsg_MissingOrUnknownKeysNeverProduceNullAudio()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(4);
            w.WriteString("type"); w.WriteString("peer_voice");
            w.WriteString("peer_id"); w.WriteInt(7);
            w.WriteString("codec"); w.WriteString("un campo que este cliente no conoce");
            w.WriteString("seq"); w.WriteInt(3);

            var (reader, remaining) = OpenTaggedFrame(w.ToArray(), "peer_voice");
            var v = PeerVoiceMsg.Parse(reader, remaining);

            Assert.AreEqual(7, v.peerId);
            Assert.AreEqual(3, v.seq, "la clave desconocida debe consumirse ENTERA o esto sale mal");
            Assert.IsNotNull(v.data);
            Assert.IsEmpty(v.data);
        }
    }
}
