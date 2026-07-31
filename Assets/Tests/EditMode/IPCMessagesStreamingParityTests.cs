using System.Collections.Generic;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Ruta IPC sin boxing (C2) — cada mensaje "simple" gana una sobrecarga
    /// <c>Parse(MsgPackReader)</c> que camina el wire sin materializar un
    /// Dictionary/object[]/box intermedio. La vieja <c>Parse(object)</c> sigue viva
    /// (Dispatch no se conmuta hasta C4) — este archivo prueba que las dos rutas
    /// producen exactamente el mismo resultado sobre el MISMO frame real: se
    /// codifica una vez con MsgPackWriter, se decodifica dos veces (ReadValue→
    /// Parse(object) de referencia; Parse(MsgPackReader) nuevo) y se comparan
    /// campo a campo. Root-tagged (chunk_data/delta_update) se prueba con el
    /// envelope {"type":..., ...} real, igual que lo vería IPCClient.Dispatch.
    /// </summary>
    [TestFixture]
    public class IPCMessagesStreamingParityTests
    {
        [Test]
        public void StatsMsgParityAcrossBothParsers()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(5);
            w.WriteString("health"); w.WriteFloat(55.5f);
            w.WriteString("hunger"); w.WriteFloat(40f);
            w.WriteString("thirst"); w.WriteFloat(30f);
            w.WriteString("sanity"); w.WriteFloat(70f);
            w.WriteString("stamina"); w.WriteFloat(90f);
            byte[] frame = w.ToArray();

            var legacy = StatsMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = StatsMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.health, fresh.health);
            Assert.AreEqual(legacy.hunger, fresh.hunger);
            Assert.AreEqual(legacy.thirst, fresh.thirst);
            Assert.AreEqual(legacy.sanity, fresh.sanity);
            Assert.AreEqual(legacy.stamina, fresh.stamina);
        }

        [Test]
        public void StatsMsgParityWithMissingKeys()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(1);
            w.WriteString("health"); w.WriteFloat(12f);
            byte[] frame = w.ToArray();

            var legacy = StatsMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = StatsMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.health, fresh.health);
            Assert.AreEqual(0f, fresh.stamina, "clave ausente ⇒ default, igual que la ruta legacy");
        }

        [Test]
        public void LocalPlayerMsgParityIncludingNestedStats()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(6);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(2f); w.WriteFloat(3f);
            w.WriteString("rotation"); w.WriteFloat(45f);
            w.WriteString("stats"); w.WriteMapHeader(5);
            w.WriteString("health"); w.WriteFloat(80f);
            w.WriteString("hunger"); w.WriteFloat(20f);
            w.WriteString("thirst"); w.WriteFloat(10f);
            w.WriteString("sanity"); w.WriteFloat(60f);
            w.WriteString("stamina"); w.WriteFloat(50f);
            w.WriteString("speed_modifier"); w.WriteFloat(1.5f);
            w.WriteString("inventory_changed"); w.WriteBool(true);
            w.WriteString("ack_input_seq"); w.WriteInt(77);
            byte[] frame = w.ToArray();

            var legacy = LocalPlayerMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = LocalPlayerMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.position, fresh.position);
            Assert.AreEqual(legacy.rotation, fresh.rotation);
            Assert.AreEqual(legacy.stats.health, fresh.stats.health);
            Assert.AreEqual(legacy.stats.stamina, fresh.stats.stamina);
            Assert.AreEqual(legacy.speedModifier, fresh.speedModifier);
            Assert.AreEqual(legacy.inventoryChanged, fresh.inventoryChanged);
            Assert.AreEqual(legacy.ackInputSeq, fresh.ackInputSeq);
        }

        [Test]
        public void RoomZoneMsgParity()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(5);
            w.WriteString("x0"); w.WriteInt(2);
            w.WriteString("z0"); w.WriteInt(4);
            w.WriteString("x1"); w.WriteInt(10);
            w.WriteString("z1"); w.WriteInt(12);
            w.WriteString("kind"); w.WriteInt((byte)RoomZoneKind.SealedRoom);
            byte[] frame = w.ToArray();

            var legacy = RoomZoneMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = RoomZoneMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.x0, fresh.x0);
            Assert.AreEqual(legacy.z0, fresh.z0);
            Assert.AreEqual(legacy.x1, fresh.x1);
            Assert.AreEqual(legacy.z1, fresh.z1);
            Assert.AreEqual(legacy.kindByte, fresh.kindByte);
            Assert.AreEqual(legacy.Kind, fresh.Kind);
        }

        [Test]
        public void RemotePlayerMsgParityWithFullPoseRelaySurface()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(10);
            w.WriteString("id"); w.WriteInt(7);
            w.WriteString("name"); w.WriteString("Joel");
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(2f); w.WriteFloat(3f);
            w.WriteString("rotation"); w.WriteFloat(90f);
            w.WriteString("animation"); w.WriteString("walk");
            w.WriteString("crouch"); w.WriteBool(true);
            w.WriteString("pitch"); w.WriteInt(-15);
            w.WriteString("equipment"); w.WriteArrayHeader(4);
            w.WriteInt(101); w.WriteInt(102); w.WriteInt(0); w.WriteInt(104);
            w.WriteString("held_item"); w.WriteInt(55);
            w.WriteString("hit_seq"); w.WriteInt(3);
            byte[] frame = w.ToArray();

            var legacy = RemotePlayerMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = RemotePlayerMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.id, fresh.id);
            Assert.AreEqual(legacy.name, fresh.name);
            Assert.AreEqual(legacy.position, fresh.position);
            Assert.AreEqual(legacy.rotation, fresh.rotation);
            Assert.AreEqual(legacy.animation, fresh.animation);
            Assert.AreEqual(legacy.crouch, fresh.crouch);
            Assert.AreEqual(legacy.pitch, fresh.pitch);
            CollectionAssert.AreEqual(legacy.equipment, fresh.equipment);
            Assert.AreEqual(legacy.heldItem, fresh.heldItem);
            Assert.AreEqual(legacy.hitSeq, fresh.hitSeq);
            Assert.AreEqual(false, fresh.dead, "dead ausente en el fixture ⇒ default false, igual en ambas rutas");
        }

        [Test]
        public void EntityViewMsgParity()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(6);
            w.WriteString("id"); w.WriteInt(42);
            w.WriteString("entity_type"); w.WriteString("lurker");
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(5f); w.WriteFloat(0f); w.WriteFloat(-5f);
            w.WriteString("rotation"); w.WriteFloat(180f);
            w.WriteString("state"); w.WriteString("chasing");
            w.WriteString("health_pct"); w.WriteFloat(0.5f);
            byte[] frame = w.ToArray();

            var legacy = EntityViewMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = EntityViewMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.id, fresh.id);
            Assert.AreEqual(legacy.entityType, fresh.entityType);
            Assert.AreEqual(legacy.position, fresh.position);
            Assert.AreEqual(legacy.rotation, fresh.rotation);
            Assert.AreEqual(legacy.state, fresh.state);
            Assert.AreEqual(legacy.healthPct, fresh.healthPct);
        }

        [Test]
        public void ItemViewMsgParity()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(4);
            w.WriteString("id"); w.WriteInt(9);
            w.WriteString("item_type"); w.WriteString("scrap");
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(1f); w.WriteFloat(1f);
            w.WriteString("quantity"); w.WriteInt(3);
            byte[] frame = w.ToArray();

            var legacy = ItemViewMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = ItemViewMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.id, fresh.id);
            Assert.AreEqual(legacy.itemType, fresh.itemType);
            Assert.AreEqual(legacy.position, fresh.position);
            Assert.AreEqual(legacy.quantity, fresh.quantity);
        }

        [Test]
        public void StpItemMsgParity()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(5);
            w.WriteString("id"); w.WriteInt(1);
            w.WriteString("def_id"); w.WriteInt(200);
            w.WriteString("count"); w.WriteInt(2);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(0f); w.WriteFloat(0f); w.WriteFloat(0f);
            w.WriteString("rotation"); w.WriteFloat(30f);
            byte[] frame = w.ToArray();

            var legacy = StpItemMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = StpItemMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.id, fresh.id);
            Assert.AreEqual(legacy.defId, fresh.defId);
            Assert.AreEqual(legacy.count, fresh.count);
            Assert.AreEqual(legacy.position, fresh.position);
            Assert.AreEqual(legacy.rotation, fresh.rotation);
        }

        [Test]
        public void StpBuildingMsgParityIncludingNestedAdded()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(6);
            w.WriteString("id"); w.WriteInt(5);
            w.WriteString("def_id"); w.WriteInt(300);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(2f); w.WriteFloat(3f);
            w.WriteString("rotation"); w.WriteFloat(0f);
            w.WriteString("group_id"); w.WriteInt(9);
            w.WriteString("added"); w.WriteArrayHeader(2);
            w.WriteMapHeader(2); w.WriteString("material_id"); w.WriteInt(1); w.WriteString("count"); w.WriteInt(4);
            w.WriteMapHeader(2); w.WriteString("material_id"); w.WriteInt(2); w.WriteString("count"); w.WriteInt(1);
            byte[] frame = w.ToArray();

            var legacy = StpBuildingMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = StpBuildingMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.id, fresh.id);
            Assert.AreEqual(legacy.defId, fresh.defId);
            Assert.AreEqual(legacy.position, fresh.position);
            Assert.AreEqual(legacy.groupId, fresh.groupId);
            Assert.AreEqual(legacy.added.Count, fresh.added.Count);
            for (int i = 0; i < legacy.added.Count; i++)
            {
                Assert.AreEqual(legacy.added[i].materialId, fresh.added[i].materialId);
                Assert.AreEqual(legacy.added[i].count, fresh.added[i].count);
            }
        }

        [Test]
        public void StpCarryableMsgParity()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(4);
            w.WriteString("id"); w.WriteInt(3);
            w.WriteString("def_id"); w.WriteInt(400);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(0f); w.WriteFloat(0f); w.WriteFloat(0f);
            w.WriteString("rotation"); w.WriteFloat(0f);
            byte[] frame = w.ToArray();

            var legacy = StpCarryableMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = StpCarryableMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.id, fresh.id);
            Assert.AreEqual(legacy.defId, fresh.defId);
            Assert.AreEqual(legacy.position, fresh.position);
            Assert.AreEqual(legacy.rotation, fresh.rotation);
        }

        [Test]
        public void StpHarvestableMsgParity()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("id"); w.WriteInt(8);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(0f); w.WriteFloat(1f);
            w.WriteString("remaining"); w.WriteFloat(0.75f);
            byte[] frame = w.ToArray();

            var legacy = StpHarvestableMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = StpHarvestableMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.id, fresh.id);
            Assert.AreEqual(legacy.position, fresh.position);
            Assert.AreEqual(legacy.remaining, fresh.remaining);
        }

        [Test]
        public void VerticalDebugMarkerMsgParity()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(4);
            w.WriteString("id"); w.WriteInt(6);
            w.WriteString("kind"); w.WriteString("shaft");
            w.WriteString("world_min"); w.WriteArrayHeader(3);
            w.WriteFloat(0f); w.WriteFloat(0f); w.WriteFloat(0f);
            w.WriteString("world_max"); w.WriteArrayHeader(3);
            w.WriteFloat(5f); w.WriteFloat(5f); w.WriteFloat(5f);
            byte[] frame = w.ToArray();

            var legacy = VerticalDebugMarkerMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = VerticalDebugMarkerMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.id, fresh.id);
            Assert.AreEqual(legacy.kind, fresh.kind);
            Assert.AreEqual(legacy.worldMin, fresh.worldMin);
            Assert.AreEqual(legacy.worldMax, fresh.worldMax);
        }

        [Test]
        public void CorpseViewMsgParityIncludingNestedItems()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(7);
            w.WriteString("id"); w.WriteInt(11);
            w.WriteString("owner_id"); w.WriteInt(2);
            w.WriteString("owner_name"); w.WriteString("Bob");
            w.WriteString("is_chest"); w.WriteBool(false);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(0f); w.WriteFloat(2f);
            w.WriteString("equipment"); w.WriteArrayHeader(4);
            w.WriteInt(1); w.WriteInt(0); w.WriteInt(3); w.WriteInt(0);
            w.WriteString("held_item"); w.WriteInt(0);
            byte[] frame = w.ToArray();

            var legacy = CorpseViewMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = CorpseViewMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.id, fresh.id);
            Assert.AreEqual(legacy.ownerId, fresh.ownerId);
            Assert.AreEqual(legacy.ownerName, fresh.ownerName);
            Assert.AreEqual(legacy.isChest, fresh.isChest);
            Assert.AreEqual(legacy.position, fresh.position);
            CollectionAssert.AreEqual(legacy.equipment, fresh.equipment);
            Assert.AreEqual(legacy.heldItem, fresh.heldItem);

            var w2 = new MsgPackWriter();
            w2.WriteMapHeader(1);
            w2.WriteString("items"); w2.WriteArrayHeader(2);
            w2.WriteMapHeader(2); w2.WriteString("item_id"); w2.WriteInt(-500); w2.WriteString("quantity"); w2.WriteInt(3);
            w2.WriteMapHeader(2); w2.WriteString("item_id"); w2.WriteInt(600); w2.WriteString("quantity"); w2.WriteInt(1);
            byte[] frame2 = w2.ToArray();

            var legacy2 = CorpseViewMsg.Parse(new MsgPackReader(frame2).ReadValue());
            var fresh2 = CorpseViewMsg.Parse(new MsgPackReader(frame2));

            Assert.AreEqual(legacy2.items.Count, fresh2.items.Count);
            for (int i = 0; i < legacy2.items.Count; i++)
            {
                Assert.AreEqual(legacy2.items[i].itemId, fresh2.items[i].itemId);
                Assert.AreEqual(legacy2.items[i].quantity, fresh2.items[i].quantity);
            }
        }

        // ── Root-tagged: leen el envelope {"type":..., ...} real, como Dispatch ──

        [Test]
        public void MovementDeltaMsgParityThroughTaggedEnvelope()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(5);
            w.WriteString("type"); w.WriteString("delta_update");
            w.WriteString("tick"); w.WriteInt(123);
            w.WriteString("ack_input_seq"); w.WriteInt(45);
            w.WriteString("position"); w.WriteArrayHeader(3);
            w.WriteFloat(1f); w.WriteFloat(2f); w.WriteFloat(3f);
            w.WriteString("velocity"); w.WriteArrayHeader(3);
            w.WriteFloat(0.1f); w.WriteFloat(0f); w.WriteFloat(-0.2f);
            byte[] frame = w.ToArray();

            var legacyRoot = new MsgPackReader(frame).ReadValue() as Dictionary<string, object>;
            var legacy = MovementDeltaMsg.Parse(legacyRoot);

            var freshReader = new MsgPackReader(frame);
            int n = freshReader.ReadMapHeader();
            var typeKey = freshReader.ReadKey();
            Assert.IsTrue(MsgPackReader.Is(typeKey, "type"));
            Assert.AreEqual("delta_update", freshReader.ReadString());
            var fresh = MovementDeltaMsg.Parse(freshReader, n - 1);

            Assert.AreEqual(legacy.tick, fresh.tick);
            Assert.AreEqual(legacy.ackInputSeq, fresh.ackInputSeq);
            Assert.AreEqual(legacy.position, fresh.position);
            Assert.AreEqual(legacy.velocity, fresh.velocity);
        }

        [Test]
        public void GridChunkDataMsgParityThroughTaggedEnvelopeIncludingWallsAndRoomZones()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(5);
            w.WriteString("type"); w.WriteString("chunk_data");
            w.WriteString("cx"); w.WriteInt(3);
            w.WriteString("cz"); w.WriteInt(-7);
            w.WriteString("layer"); w.WriteInt(0);
            w.WriteString("walls"); w.WriteArrayHeader(GridChunkDataMsg.Tiles);
            for (int x = 0; x < GridChunkDataMsg.Tiles; x++)
            {
                w.WriteArrayHeader(GridChunkDataMsg.Tiles);
                for (int z = 0; z < GridChunkDataMsg.Tiles; z++)
                    w.WriteInt(x == 0 && z == 0 ? 0x8F : 0x00); // ejercita la rama 0xcc (SE pillar)
            }
            w.WriteString("room_zones"); w.WriteArrayHeader(2);
            w.WriteMapHeader(5);
            w.WriteString("x0"); w.WriteInt(0); w.WriteString("z0"); w.WriteInt(0);
            w.WriteString("x1"); w.WriteInt(8); w.WriteString("z1"); w.WriteInt(8);
            w.WriteString("kind"); w.WriteInt((byte)RoomZoneKind.SealedRoom);
            w.WriteMapHeader(5);
            w.WriteString("x0"); w.WriteInt(8); w.WriteString("z0"); w.WriteInt(8);
            w.WriteString("x1"); w.WriteInt(16); w.WriteString("z1"); w.WriteInt(16);
            w.WriteString("kind"); w.WriteInt((byte)RoomZoneKind.CorridorSpine);
            byte[] frame = w.ToArray();

            var legacyRoot = new MsgPackReader(frame).ReadValue() as Dictionary<string, object>;
            var legacy = GridChunkDataMsg.Parse(legacyRoot);

            var freshReader = new MsgPackReader(frame);
            int n = freshReader.ReadMapHeader();
            freshReader.ReadKey(); // "type"
            freshReader.ReadString(); // "chunk_data"
            var fresh = GridChunkDataMsg.Parse(freshReader, n - 1);

            Assert.AreEqual(legacy.cx, fresh.cx);
            Assert.AreEqual(legacy.cz, fresh.cz);
            Assert.AreEqual(legacy.layer, fresh.layer);
            for (int x = 0; x < GridChunkDataMsg.Tiles; x++)
                for (int z = 0; z < GridChunkDataMsg.Tiles; z++)
                    Assert.AreEqual(legacy.walls[x, z], fresh.walls[x, z], $"walls[{x},{z}]");

            Assert.AreEqual(legacy.roomZones.Length, fresh.roomZones.Length);
            for (int i = 0; i < legacy.roomZones.Length; i++)
            {
                Assert.AreEqual(legacy.roomZones[i].x0, fresh.roomZones[i].x0);
                Assert.AreEqual(legacy.roomZones[i].kindByte, fresh.roomZones[i].kindByte);
            }
        }

        [Test]
        public void GridChunkDataMsgParityWithoutRoomZonesKeyStaysEmptyNotNull()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(4);
            w.WriteString("type"); w.WriteString("chunk_data");
            w.WriteString("cx"); w.WriteInt(1);
            w.WriteString("cz"); w.WriteInt(1);
            w.WriteString("layer"); w.WriteInt(0);
            byte[] frame = w.ToArray();

            var freshReader = new MsgPackReader(frame);
            int n = freshReader.ReadMapHeader();
            freshReader.ReadKey();
            freshReader.ReadString();
            var fresh = GridChunkDataMsg.Parse(freshReader, n - 1);

            Assert.IsNotNull(fresh.roomZones);
            Assert.AreEqual(0, fresh.roomZones.Length);
        }

        // ── C3: ChunkViewMsg + los anidados volumétricos/layer ──

        [Test]
        public void InterLayerVolumeMsgParityWithArrayFields()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(9);
            w.WriteString("volume_id"); w.WriteInt(12);
            w.WriteString("kind"); w.WriteString("shaft");
            w.WriteString("base_chunk"); w.WriteArrayHeader(2); w.WriteInt(1); w.WriteInt(2);
            w.WriteString("involved_layers"); w.WriteArrayHeader(3); w.WriteInt(0); w.WriteInt(1); w.WriteInt(2);
            w.WriteString("footprint_cell_min"); w.WriteArrayHeader(2); w.WriteInt(3); w.WriteInt(4);
            w.WriteString("footprint_cell_max"); w.WriteArrayHeader(2); w.WriteInt(5); w.WriteInt(6);
            w.WriteString("safety_type"); w.WriteString("railed");
            w.WriteString("future_audio_hint"); w.WriteString("hum");
            w.WriteString("visual_flags"); w.WriteInt(7);
            byte[] frame = w.ToArray();

            var legacy = InterLayerVolumeMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = InterLayerVolumeMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.volumeId, fresh.volumeId);
            Assert.AreEqual(legacy.kind, fresh.kind);
            CollectionAssert.AreEqual(legacy.baseChunk, fresh.baseChunk);
            CollectionAssert.AreEqual(legacy.involvedLayers, fresh.involvedLayers);
            CollectionAssert.AreEqual(legacy.footprintCellMin, fresh.footprintCellMin);
            CollectionAssert.AreEqual(legacy.footprintCellMax, fresh.footprintCellMax);
            Assert.AreEqual(legacy.safetyType, fresh.safetyType);
            Assert.AreEqual(legacy.futureAudioHint, fresh.futureAudioHint);
            Assert.AreEqual(legacy.visualFlags, fresh.visualFlags);
            CollectionAssert.AreEqual(legacy.visualHints, fresh.visualHints, "visual_hints ausente ⇒ array vacío en ambas rutas");
        }

        [Test]
        public void VolumetricFaceMsgParity()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("cell"); w.WriteArrayHeader(3); w.WriteInt(1); w.WriteInt(2); w.WriteInt(3);
            w.WriteString("dir"); w.WriteInt(VolumetricGridMsg.DirUp);
            w.WriteString("kind"); w.WriteInt(VolumetricGridMsg.FaceFloor);
            byte[] frame = w.ToArray();

            var legacy = VolumetricFaceMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = VolumetricFaceMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.x, fresh.x);
            Assert.AreEqual(legacy.y, fresh.y);
            Assert.AreEqual(legacy.z, fresh.z);
            Assert.AreEqual(legacy.dir, fresh.dir);
            Assert.AreEqual(legacy.kind, fresh.kind);
        }

        [Test]
        public void LayerBandMsgParity()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(8);
            w.WriteString("band_id"); w.WriteInt(1);
            w.WriteString("layer"); w.WriteInt(2);
            w.WriteString("profile"); w.WriteString("mixed");
            w.WriteString("profile_code"); w.WriteInt(3);
            w.WriteString("accessible"); w.WriteBool(true);
            w.WriteString("danger_profile"); w.WriteString("low");
            w.WriteString("resource_profile"); w.WriteString("scarce");
            w.WriteString("anomaly_profile"); w.WriteString("none");
            byte[] frame = w.ToArray();

            var legacy = LayerBandMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = LayerBandMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.bandId, fresh.bandId);
            Assert.AreEqual(legacy.layer, fresh.layer);
            Assert.AreEqual(legacy.profile, fresh.profile);
            Assert.AreEqual(legacy.profileCode, fresh.profileCode);
            Assert.AreEqual(legacy.accessible, fresh.accessible);
            Assert.AreEqual(legacy.dangerProfile, fresh.dangerProfile);
            Assert.AreEqual(legacy.resourceProfile, fresh.resourceProfile);
            Assert.AreEqual(legacy.anomalyProfile, fresh.anomalyProfile);
        }

        [Test]
        public void VerticalAccessNodeMsgParity()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(7);
            w.WriteString("access_id"); w.WriteInt(4);
            w.WriteString("access_type"); w.WriteString("stair");
            w.WriteString("access_type_code"); w.WriteInt(1);
            w.WriteString("from_layer"); w.WriteInt(0);
            w.WriteString("to_layer"); w.WriteInt(1);
            w.WriteString("footprint_cell_min"); w.WriteArrayHeader(2); w.WriteInt(1); w.WriteInt(1);
            w.WriteString("footprint_cell_max"); w.WriteArrayHeader(2); w.WriteInt(3); w.WriteInt(3);
            byte[] frame = w.ToArray();

            var legacy = VerticalAccessNodeMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = VerticalAccessNodeMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.accessId, fresh.accessId);
            Assert.AreEqual(legacy.accessType, fresh.accessType);
            Assert.AreEqual(legacy.accessTypeCode, fresh.accessTypeCode);
            Assert.AreEqual(legacy.fromLayer, fresh.fromLayer);
            Assert.AreEqual(legacy.toLayer, fresh.toLayer);
            CollectionAssert.AreEqual(legacy.footprintCellMin, fresh.footprintCellMin);
            CollectionAssert.AreEqual(legacy.footprintCellMax, fresh.footprintCellMax);
            Assert.AreEqual(false, fresh.explicitAccess, "\"explicit\" ausente ⇒ default false en ambas rutas");
        }

        [Test]
        public void BandHeightSpecMsgParity()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(5);
            w.WriteString("band_index"); w.WriteInt(0);
            w.WriteString("layer"); w.WriteInt(1);
            w.WriteString("room_height"); w.WriteFloat(3.5f);
            w.WriteString("total_height"); w.WriteFloat(4f);
            w.WriteString("neighbor_max_room_height"); w.WriteFloat(3.8f);
            byte[] frame = w.ToArray();

            var legacy = BandHeightSpecMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = BandHeightSpecMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.bandIndex, fresh.bandIndex);
            Assert.AreEqual(legacy.layer, fresh.layer);
            Assert.AreEqual(legacy.roomHeight, fresh.roomHeight);
            Assert.AreEqual(legacy.totalHeight, fresh.totalHeight);
            Assert.AreEqual(legacy.neighborMaxRoomHeight, fresh.neighborMaxRoomHeight);
        }

        [Test]
        public void VolumetricGridMsgParityNilReturnsNullOnBothPaths()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(1);
            w.WriteString("volumetric_grid"); w.WriteNil();
            byte[] frame = w.ToArray();

            var reader = new MsgPackReader(frame);
            reader.ReadMapHeader();
            reader.ReadKey();
            var fresh = VolumetricGridMsg.Parse(reader);

            var legacyReader = new MsgPackReader(frame);
            var legacyRoot = legacyReader.ReadValue() as Dictionary<string, object>;
            var legacy = IPCParse.Get(legacyRoot, "volumetric_grid") != null
                ? VolumetricGridMsg.Parse(IPCParse.Get(legacyRoot, "volumetric_grid"))
                : null;

            Assert.IsNull(legacy);
            Assert.IsNull(fresh, "un volumetric_grid nil debe seguir devolviendo null en la ruta streaming");
        }

        [Test]
        public void VolumetricGridMsgParityWithNestedFacesLayerBandsAccessAndHeightBands()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(15);
            w.WriteString("active"); w.WriteBool(true);
            w.WriteString("column_id"); w.WriteInt(999);
            w.WriteString("column_coord"); w.WriteArrayHeader(2); w.WriteInt(1); w.WriteInt(1);
            w.WriteString("source"); w.WriteString("showcase");
            w.WriteString("dims"); w.WriteArrayHeader(3); w.WriteInt(4); w.WriteInt(3); w.WriteInt(4);
            w.WriteString("cell_size_xz"); w.WriteFloat(5f);
            w.WriteString("layer_height"); w.WriteFloat(7f);
            w.WriteString("origin_world"); w.WriteArrayHeader(3); w.WriteFloat(0f); w.WriteFloat(0f); w.WriteFloat(0f);
            w.WriteString("base_layer"); w.WriteInt(0);
            w.WriteString("cells"); w.WriteArrayHeader(3); w.WriteInt(0); w.WriteInt(1); w.WriteInt(2);
            w.WriteString("faces"); w.WriteArrayHeader(1);
            w.WriteMapHeader(3);
            w.WriteString("cell"); w.WriteArrayHeader(3); w.WriteInt(1); w.WriteInt(0); w.WriteInt(1);
            w.WriteString("dir"); w.WriteInt(VolumetricGridMsg.DirNorth);
            w.WriteString("kind"); w.WriteInt(VolumetricGridMsg.FaceWall);
            w.WriteString("open_cell_count"); w.WriteInt(10);
            w.WriteString("solid_cell_count"); w.WriteInt(5);
            w.WriteString("vertical_connection_count"); w.WriteInt(2);
            w.WriteString("valid_vertical_opening_count"); w.WriteInt(1);
            w.WriteString("atrium_span"); w.WriteBool(false);
            w.WriteString("layer_bands"); w.WriteArrayHeader(1);
            w.WriteMapHeader(2); w.WriteString("band_id"); w.WriteInt(1); w.WriteString("layer"); w.WriteInt(0);
            w.WriteString("vertical_access"); w.WriteArrayHeader(1);
            w.WriteMapHeader(2); w.WriteString("access_id"); w.WriteInt(2); w.WriteString("access_type"); w.WriteString("ramp");
            w.WriteString("height_bands"); w.WriteArrayHeader(1);
            w.WriteMapHeader(2); w.WriteString("band_index"); w.WriteInt(0); w.WriteString("room_height"); w.WriteFloat(3f);
            byte[] frame = w.ToArray();

            var legacyReader = new MsgPackReader(frame);
            var legacyRoot = legacyReader.ReadValue();
            var legacy = VolumetricGridMsg.Parse(legacyRoot);

            var freshReader = new MsgPackReader(frame);
            var fresh = VolumetricGridMsg.Parse(freshReader);

            Assert.IsNotNull(fresh);
            Assert.AreEqual(legacy.active, fresh.active);
            Assert.AreEqual(legacy.columnId, fresh.columnId);
            CollectionAssert.AreEqual(legacy.columnCoord, fresh.columnCoord);
            Assert.AreEqual(legacy.source, fresh.source);
            Assert.AreEqual(legacy.nx, fresh.nx);
            Assert.AreEqual(legacy.ny, fresh.ny);
            Assert.AreEqual(legacy.nz, fresh.nz);
            Assert.AreEqual(legacy.cellSizeXZ, fresh.cellSizeXZ);
            Assert.AreEqual(legacy.layerHeight, fresh.layerHeight);
            Assert.AreEqual(legacy.originWorld, fresh.originWorld);
            Assert.AreEqual(legacy.baseLayer, fresh.baseLayer);
            CollectionAssert.AreEqual(legacy.cells, fresh.cells);
            Assert.AreEqual(legacy.openCellCount, fresh.openCellCount);
            Assert.AreEqual(legacy.solidCellCount, fresh.solidCellCount);
            Assert.AreEqual(legacy.verticalConnectionCount, fresh.verticalConnectionCount);
            Assert.AreEqual(legacy.validVerticalOpeningCount, fresh.validVerticalOpeningCount);
            Assert.AreEqual(legacy.atriumSpan, fresh.atriumSpan);

            Assert.AreEqual(legacy.faces.Count, fresh.faces.Count);
            Assert.AreEqual(legacy.faces[0].x, fresh.faces[0].x);
            Assert.AreEqual(legacy.faces[0].dir, fresh.faces[0].dir);
            Assert.AreEqual(legacy.faces[0].kind, fresh.faces[0].kind);

            Assert.AreEqual(legacy.layerBands.Count, fresh.layerBands.Count);
            Assert.AreEqual(legacy.layerBands[0].bandId, fresh.layerBands[0].bandId);

            Assert.AreEqual(legacy.verticalAccess.Count, fresh.verticalAccess.Count);
            Assert.AreEqual(legacy.verticalAccess[0].accessType, fresh.verticalAccess[0].accessType);

            Assert.AreEqual(legacy.heightBands.Count, fresh.heightBands.Count);
            Assert.AreEqual(legacy.heightBands[0].roomHeight, fresh.heightBands[0].roomHeight);
        }

        [Test]
        public void ChunkViewMsgParityWithFullPackedLayoutAndVolumetricGrid()
        {
            const int g = 3; // grid pequeño a propósito: exhaustivo, legible a mano
            int cellCount = g * g;
            int vEdgeCount = (g + 1) * g;
            int hEdgeCount = g * (g + 1);
            int total = cellCount + vEdgeCount + hEdgeCount;

            var w = new MsgPackWriter();
            w.WriteMapHeader(19);
            w.WriteString("chunk_schema"); w.WriteInt(1);
            w.WriteString("pos"); w.WriteArrayHeader(2); w.WriteInt(3); w.WriteInt(-2);
            w.WriteString("layer"); w.WriteInt(0);
            w.WriteString("layer_y"); w.WriteFloat(0f);
            w.WriteString("template_id"); w.WriteInt(1);
            w.WriteString("rotation"); w.WriteInt(90);
            w.WriteString("mirrored"); w.WriteBool(true);
            w.WriteString("state"); w.WriteString("stabilized");
            w.WriteString("has_workbench"); w.WriteBool(true);
            w.WriteString("layout_grid_size"); w.WriteInt(g);
            w.WriteString("layout_cell_size"); w.WriteFloat(5f);
            w.WriteString("layout_cells"); w.WriteArrayHeader(total);
            for (int i = 0; i < total; i++) w.WriteInt(i + 1); // valores distintos ⇒ el split no puede confundir secciones
            w.WriteString("edge_openings"); w.WriteInt(2);
            w.WriteString("macro_id"); w.WriteInt(55);
            w.WriteString("zone_kind"); w.WriteInt(5);
            w.WriteString("macro_local"); w.WriteArrayHeader(2); w.WriteInt(1); w.WriteInt(1);
            w.WriteString("macro_size"); w.WriteArrayHeader(2); w.WriteInt(2); w.WriteInt(2);
            w.WriteString("floor_level"); w.WriteInt(0);
            w.WriteString("inter_layer_volumes"); w.WriteArrayHeader(1);
            w.WriteMapHeader(2); w.WriteString("volume_id"); w.WriteInt(1); w.WriteString("kind"); w.WriteString("atrium");
            byte[] frame = w.ToArray();

            var legacy = ChunkViewMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = ChunkViewMsg.Parse(new MsgPackReader(frame));

            Assert.AreEqual(legacy.chunkSchema, fresh.chunkSchema);
            CollectionAssert.AreEqual(legacy.pos, fresh.pos);
            Assert.AreEqual(legacy.layer, fresh.layer);
            Assert.AreEqual(legacy.templateId, fresh.templateId);
            Assert.AreEqual(legacy.rotation, fresh.rotation);
            Assert.AreEqual(legacy.mirrored, fresh.mirrored);
            Assert.AreEqual(legacy.state, fresh.state);
            Assert.AreEqual(legacy.hasWorkbench, fresh.hasWorkbench);
            Assert.AreEqual(legacy.layoutGridSize, fresh.layoutGridSize);
            Assert.AreEqual(legacy.layoutCellSize, fresh.layoutCellSize);
            CollectionAssert.AreEqual(legacy.layoutCells, fresh.layoutCells);
            Assert.AreEqual(legacy.edgeOpenings, fresh.edgeOpenings);
            Assert.AreEqual(legacy.macroId, fresh.macroId);
            Assert.AreEqual(legacy.zoneKind, fresh.zoneKind);
            CollectionAssert.AreEqual(legacy.macroLocal, fresh.macroLocal);
            CollectionAssert.AreEqual(legacy.macroSize, fresh.macroSize);
            Assert.AreEqual(legacy.floorLevel, fresh.floorLevel);

            // SplitPackedLayout() corrió en ambas rutas — mismo split, mismos flags.
            Assert.AreEqual(legacy.hasBackendLayout, fresh.hasBackendLayout);
            Assert.IsTrue(fresh.hasBackendLayout);
            Assert.AreEqual(legacy.hasEdgeLayout, fresh.hasEdgeLayout);
            Assert.IsTrue(fresh.hasEdgeLayout);
            CollectionAssert.AreEqual(legacy.cellFlags, fresh.cellFlags);
            CollectionAssert.AreEqual(legacy.verticalEdges, fresh.verticalEdges);
            CollectionAssert.AreEqual(legacy.horizontalEdges, fresh.horizontalEdges);
            for (int x = 0; x < g; x++)
                for (int z = 0; z < g; z++)
                    Assert.AreEqual(legacy.GetCell(x, z), fresh.GetCell(x, z), $"GetCell({x},{z})");
            for (int x = 0; x <= g; x++)
                for (int z = 0; z < g; z++)
                    Assert.AreEqual(legacy.GetVEdge(x, z), fresh.GetVEdge(x, z), $"GetVEdge({x},{z})");
            for (int x = 0; x < g; x++)
                for (int z = 0; z <= g; z++)
                    Assert.AreEqual(legacy.GetHEdge(x, z), fresh.GetHEdge(x, z), $"GetHEdge({x},{z})");

            Assert.AreEqual(legacy.interLayerVolumes.Count, fresh.interLayerVolumes.Count);
            Assert.AreEqual(legacy.interLayerVolumes[0].volumeId, fresh.interLayerVolumes[0].volumeId);
            Assert.AreEqual(legacy.interLayerVolumes[0].kind, fresh.interLayerVolumes[0].kind);

            Assert.IsNull(legacy.volumetricGrid, "volumetric_grid ausente en el fixture ⇒ null en ambas rutas");
            Assert.IsNull(fresh.volumetricGrid);
            Assert.IsFalse(fresh.HasVolumetricGrid);
        }

        [Test]
        public void ChunkViewMsgParityWithVolumetricGridPresentSetsHasVolumetricGrid()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("pos"); w.WriteArrayHeader(2); w.WriteInt(0); w.WriteInt(0);
            w.WriteString("layer"); w.WriteInt(0);
            w.WriteString("volumetric_grid"); w.WriteMapHeader(2);
            w.WriteString("active"); w.WriteBool(true);
            w.WriteString("source"); w.WriteString("showcase");
            byte[] frame = w.ToArray();

            var legacy = ChunkViewMsg.Parse(new MsgPackReader(frame).ReadValue());
            var fresh = ChunkViewMsg.Parse(new MsgPackReader(frame));

            Assert.IsTrue(legacy.HasVolumetricGrid);
            Assert.IsTrue(fresh.HasVolumetricGrid);
            Assert.AreEqual(legacy.volumetricGrid.source, fresh.volumetricGrid.source);
        }
    }
}
