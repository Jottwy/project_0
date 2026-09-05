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
                default: return null;
            }
        }

        private static readonly Dictionary<byte, GameObject> Cache = new Dictionary<byte, GameObject>();
        private static readonly HashSet<byte> Warned = new HashSet<byte>();

        public static GameObject Prefab(byte kind)
        {
            if (Cache.TryGetValue(kind, out GameObject cached) && cached != null) return cached;
            string name = NameOf(kind);
            GameObject prefab = name != null ? Resources.Load<GameObject>("Wg3Props/" + name) : null;
            if (prefab == null)
            {
                if (Warned.Add(kind))
                    Debug.LogWarning($"[wg3] sin prefab para el atrezo {kind} ({name}): ejecuta Backrooms/WG3/Build Prop Catalog");
                return null;
            }
            Cache[kind] = prefab;
            return prefab;
        }
    }
}
