using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// ADR-129 D1 — de <c>kind</c> a prefab. Los prefabs viven en <c>Resources/Wg3Props</c> como
    /// copias del pack de oficina (las hace <c>Wg3PropCatalogBuilder</c> en el editor) para que el
    /// build los lleve sin referencias serializadas en un componente que se crea en runtime. Uno
    /// que falte se avisa UNA vez y se salta: una sala sin sillas es peor que un mundo que no
    /// arranca.
    /// </summary>
    public static class Wg3PropCatalog
    {
        public static string NameOf(byte kind)
        {
            switch (kind)
            {
                case 1: return "Desk";
                case 2: return "Chair";
                case 3: return "Cabinet";
                case 4: return "Shelf";
                case 5: return "Whiteboard";
                case 6: return "Trash";
                case 7: return "Box";
                case 8: return "Paper";
                case 9: return "Monitor";
                case 10: return "Clock";
                case 11: return "Phone";
                case 12: return "Keyboard";
                case 13: return "Tray";
                case 14: return "Chair";
                // Variantes de sala (ADR-129 enm. 2). `kind` es un byte: valores nuevos no cambian
                // el formato del cable, y un cliente viejo cae en el `default` y se salta el mueble.
                case 16: return "TableLong";
                case 17: return "Counter";
                case 18: return "Microwave";
                case 19: return "Fridge";
                case 20: return "Rack";
                default: return null;
            }
        }

        private static readonly Dictionary<byte, GameObject[]> Cache = new Dictionary<byte, GameObject[]>();
        private static readonly HashSet<byte> Warned = new HashSet<byte>();

        /// <summary>Todas las variantes de un tipo: <c>Name</c>, <c>Name_2</c>, <c>Name_3</c>… hasta
        /// la primera que falte. Se cargan una vez por sesión.</summary>
        private static GameObject[] Variants(byte kind)
        {
            if (Cache.TryGetValue(kind, out GameObject[] cached) && cached != null && cached.Length > 0 && cached[0] != null)
                return cached;
            string name = NameOf(kind);
            var list = new List<GameObject>();
            if (name != null)
            {
                var first = Resources.Load<GameObject>("Wg3Props/" + name);
                if (first != null) list.Add(first);
                for (int i = 2; i < 16; i++)
                {
                    var v = Resources.Load<GameObject>($"Wg3Props/{name}_{i}");
                    if (v == null) break;
                    list.Add(v);
                }
            }
            if (list.Count == 0 && Warned.Add(kind))
                Debug.LogWarning($"[wg3] sin prefab para el atrezo {kind} ({name}): ejecuta Backrooms/WG3/Build Prop Catalog");
            var arr = list.ToArray();
            Cache[kind] = arr;
            return arr;
        }

        /// <summary>Una variante elegida por la POSICIÓN del ancla: dos jugadores ven la misma mesa
        /// y la misma mesa es la misma al volver.</summary>
        public static GameObject Prefab(byte kind, int xCm, int zCm)
        {
            var vs = Variants(kind);
            if (vs.Length == 0) return null;
            unchecked
            {
                uint h = (uint)xCm * 2654435761u ^ (uint)zCm * 2246822519u ^ kind;
                h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;
                return vs[(int)(h % (uint)vs.Length)];
            }
        }
    }
}
