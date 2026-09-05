using System.IO;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-129 — copia los prefabs del pack de oficina a <c>Resources/Wg3Props/&lt;Kind&gt;.prefab</c>,
    /// que es lo que carga <c>Wg3PropCatalog</c> en runtime. Copias y no referencias: el streamer se
    /// crea en runtime y un campo serializado suyo no lo puede rellenar nadie. Se vuelve a ejecutar
    /// cuando cambie el pack o la tabla de aquí.
    /// </summary>
    public static class Wg3PropCatalogBuilder
    {
        private const string Out = "Assets/Resources/Wg3Props";
        private const string Office = "Assets/AK Studio Art/Business Office/Prefabs/Office/";
        private const string Boxes = "Assets/AK Studio Art/Business Office/Prefabs/Cardboard Boxes/";
        private const string Grocery = "Assets/GroceryStorePropsCollection/Prefabs/URP/";

        private static readonly (string kind, string source)[] Table =
        {
            ("Desk", Office + "Desk 1.prefab"),
            ("Chair", Office + "Chair 1.prefab"),
            ("Cabinet", Office + "Cupboard.prefab"),
            ("Shelf", Office + "Shelf 1.prefab"),
            ("Whiteboard", Office + "Whiteboard.prefab"),
            ("Trash", Office + "Trash Can.prefab"),
            ("Box", Boxes + "Carton Box 1.prefab"),
            ("Paper", Grocery + "SM_Paper1.prefab"),
            ("Monitor", Office + "Monitor Pc.prefab"),
        };

        [MenuItem("Backrooms/WG3/Build Prop Catalog")]
        public static void Build()
        {
            if (!AssetDatabase.IsValidFolder(Out))
            {
                Directory.CreateDirectory(Out);
                AssetDatabase.Refresh();
            }
            int ok = 0;
            foreach (var (kind, source) in Table)
            {
                var src = AssetDatabase.LoadAssetAtPath<GameObject>(source);
                if (src == null)
                {
                    Debug.LogError($"[wg3-props] no existe {source}");
                    continue;
                }
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(src);
                // Copia independiente: se desconecta del original para que el catalogo no dependa
                // de la ruta del pack.
                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                string path = $"{Out}/{kind}.prefab";
                PrefabUtility.SaveAsPrefabAsset(instance, path);
                Object.DestroyImmediate(instance);
                var b = Bounds(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                Debug.Log($"[wg3-props] {kind} <- {Path.GetFileName(source)} · bounds centro {b.center} tamaño {b.size}");
                ok++;
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[wg3-props] catalogo: {ok}/{Table.Length}");
        }

        private static Bounds Bounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }
    }
}
