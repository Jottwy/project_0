using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    public class StatsMsg
    {
        public float health, hunger, thirst, sanity;

        public static StatsMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var s = new StatsMsg();
            if (d == null) return s;
            s.health = IPCParse.F(d, "health");
            s.hunger = IPCParse.F(d, "hunger");
            s.thirst = IPCParse.F(d, "thirst");
            s.sanity = IPCParse.F(d, "sanity");
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

        public static LocalPlayerMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var p = new LocalPlayerMsg();
            if (d == null) return p;
            p.position = IPCParse.Vec3(IPCParse.Get(d, "position"));
            p.rotation = IPCParse.F(d, "rotation");
            p.stats = StatsMsg.Parse(IPCParse.Get(d, "stats"));
            p.speedModifier = IPCParse.F(d, "speed_modifier");
            p.inventoryChanged = IPCParse.B(d, "inventory_changed");
            return p;
        }
    }

    public class RemotePlayerMsg
    {
        public int id;
        public string name = "";
        public Vector3 position;
        public float rotation;
        public string animation = "idle";

        public static RemotePlayerMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var r = new RemotePlayerMsg();
            if (d == null) return r;
            r.id = (int)IPCParse.L(d, "id");
            r.name = IPCParse.S(d, "name");
            r.position = IPCParse.Vec3(IPCParse.Get(d, "position"));
            r.rotation = IPCParse.F(d, "rotation");
            r.animation = IPCParse.S(d, "animation");
            return r;
        }
    }

    public class ChunkViewMsg
    {
        public int[] pos = new int[2];
        public int templateId;
        public int rotation;
        public bool mirrored;
        public string state = "random";
        public bool hasWorkbench;

        public static ChunkViewMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var c = new ChunkViewMsg();
            if (d == null) return c;
            c.pos = IPCParse.IntArray2(IPCParse.Get(d, "pos"));
            c.templateId = (int)IPCParse.L(d, "template_id");
            c.rotation = (int)IPCParse.L(d, "rotation");
            c.mirrored = IPCParse.B(d, "mirrored");
            c.state = IPCParse.S(d, "state");
            c.hasWorkbench = IPCParse.B(d, "has_workbench");
            return c;
        }
    }

    public class EntityViewMsg
    {
        public uint id;
        public string entityType = "lurker";
        public Vector3 position;
        public float rotation;
        public string state = "idle";
        public float healthPct = 1f;

        public static EntityViewMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var e = new EntityViewMsg();
            if (d == null) return e;
            e.id = (uint)IPCParse.L(d, "id");
            e.entityType = IPCParse.S(d, "entity_type");
            e.position = IPCParse.Vec3(IPCParse.Get(d, "position"));
            e.rotation = IPCParse.F(d, "rotation");
            e.state = IPCParse.S(d, "state");
            e.healthPct = IPCParse.F(d, "health_pct");
            return e;
        }
    }

    public class ItemViewMsg
    {
        public uint id;
        public string itemType = "";
        public Vector3 position;
        public int quantity;

        public static ItemViewMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var i = new ItemViewMsg();
            if (d == null) return i;
            i.id = (uint)IPCParse.L(d, "id");
            i.itemType = IPCParse.S(d, "item_type");
            i.position = IPCParse.Vec3(IPCParse.Get(d, "position"));
            i.quantity = (int)IPCParse.L(d, "quantity");
            return i;
        }
    }

    public class WorldStateMsg
    {
        public long tick;
        public LocalPlayerMsg localPlayer = new LocalPlayerMsg();
        public List<RemotePlayerMsg> remotePlayers = new List<RemotePlayerMsg>();
        public List<ChunkViewMsg> visibleChunks = new List<ChunkViewMsg>();
        public List<EntityViewMsg> visibleEntities = new List<EntityViewMsg>();
        public List<ItemViewMsg> visibleItems = new List<ItemViewMsg>();

        public static WorldStateMsg Parse(Dictionary<string, object> d)
        {
            var ws = new WorldStateMsg();
            if (d == null) return ws;
            ws.tick = IPCParse.L(d, "tick");
            ws.localPlayer = LocalPlayerMsg.Parse(IPCParse.Get(d, "local_player"));

            if (IPCParse.Get(d, "remote_players") is object[] rp)
                foreach (var item in rp) ws.remotePlayers.Add(RemotePlayerMsg.Parse(item));

            if (IPCParse.Get(d, "visible_chunks") is object[] vc)
                foreach (var item in vc) ws.visibleChunks.Add(ChunkViewMsg.Parse(item));

            if (IPCParse.Get(d, "visible_entities") is object[] ve)
                foreach (var item in ve) ws.visibleEntities.Add(EntityViewMsg.Parse(item));

            if (IPCParse.Get(d, "visible_items") is object[] vi)
                foreach (var item in vi) ws.visibleItems.Add(ItemViewMsg.Parse(item));

            return ws;
        }
    }

    public class GameEventMsg
    {
        public string eventType = "";
        public object data;

        public static GameEventMsg Parse(Dictionary<string, object> d)
        {
            var e = new GameEventMsg();
            if (d == null) return e;
            e.eventType = IPCParse.S(d, "event_type");
            e.data = IPCParse.Get(d, "data");
            return e;
        }
    }

    public static class IPCParse
    {
        public static object Get(Dictionary<string, object> d, string key)
            => (d != null && d.TryGetValue(key, out var v)) ? v : null;

        public static float ToFloat(object v)
        {
            if (v is double dd) return (float)dd;
            if (v is long ll) return ll;
            return 0f;
        }

        public static long ToLong(object v)
        {
            if (v is long ll) return ll;
            if (v is double dd) return (long)dd;
            return 0L;
        }

        public static float F(Dictionary<string, object> d, string key) => ToFloat(Get(d, key));
        public static long L(Dictionary<string, object> d, string key) => ToLong(Get(d, key));
        public static bool B(Dictionary<string, object> d, string key) => Get(d, key) is bool b && b;
        public static string S(Dictionary<string, object> d, string key) => Get(d, key) as string ?? "";

        public static int Len(object v) => v is object[] a ? a.Length : 0;

        public static Vector3 Vec3(object v)
        {
            if (v is object[] a && a.Length >= 3)
                return new Vector3(ToFloat(a[0]), ToFloat(a[1]), ToFloat(a[2]));
            return Vector3.zero;
        }

        public static int[] IntArray2(object v)
        {
            if (v is object[] a && a.Length >= 2)
                return new[] { (int)ToLong(a[0]), (int)ToLong(a[1]) };
            return new[] { 0, 0 };
        }
    }
}
