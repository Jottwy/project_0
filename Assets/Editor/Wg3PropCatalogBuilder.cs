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

        // Variantes: la primera se guarda como <Kind>, las demás como <Kind>_2, <Kind>_3…; el
        // catálogo de runtime las carga hasta la primera que falte y elige por posición.
        private static readonly (string kind, string[] sources)[] Table =
        {
            // Desk 3 y 4 son mesas en L de 2,5 x 2,8: no caben en la huella de 2,30 x 1,02 del servidor.
            ("Desk", new[] { Office + "Desk 1.prefab", Office + "Desk 2.prefab" }),
            ("Chair", new[] { Office + "Chair 1.prefab", Office + "Chair 2.prefab", Office + "Chair 3.prefab" }),
            ("Cabinet", new[] { Office + "Cupboard.prefab" }),
            ("Shelf", new[] { Office + "Shelf 1.prefab", Office + "Shelf 2.prefab" }),
            ("Whiteboard", new[] { Office + "Whiteboard.prefab" }),
            ("Trash", new[] { Office + "Trash Can.prefab" }),
            ("Box", new[] { Boxes + "Carton Box 1.prefab", Boxes + "Carton Box 2.prefab", Boxes + "Carton Box 3.prefab" }),
            ("Paper", new[] { Grocery + "SM_Paper1.prefab", Grocery + "SM_Paper2.prefab", Grocery + "SM_Paper3.prefab", Grocery + "SM_Papers1.prefab" }),
            ("Monitor", new[] { Office + "Monitor Pc.prefab" }),
            ("Clock", new[] { Office + "Wall Clock.prefab" }),
            ("Phone", new[] { Office + "Office Phone.prefab" }),
            ("Keyboard", new[] { Office + "Keyboard.prefab" }),
            ("Tray", new[] { Office + "Paper Tray.prefab" }),
        };

        [MenuItem("Backrooms/WG3/Build Prop Catalog")]
        public static void Build()
        {
            if (!AssetDatabase.IsValidFolder(Out))
            {
                Directory.CreateDirectory(Out);
                AssetDatabase.Refresh();
            }
            int ok = 0, total = 0;
            foreach (var (kind, sources) in Table)
            {
                for (int i = 0; i < sources.Length; i++)
                {
                    total++;
                    string source = sources[i];
                    var src = AssetDatabase.LoadAssetAtPath<GameObject>(source);
                    if (src == null)
                    {
                        Debug.LogError($"[wg3-props] no existe {source}");
                        continue;
                    }
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(src);
                    // Copia independiente: se desconecta del original para que el catalogo no
                    // dependa de la ruta del pack.
                    PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    string path = i == 0 ? $"{Out}/{kind}.prefab" : $"{Out}/{kind}_{i + 1}.prefab";
                    PrefabUtility.SaveAsPrefabAsset(instance, path);
                    Object.DestroyImmediate(instance);
                    var b = Bounds(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                    Debug.Log($"[wg3-props] {Path.GetFileNameWithoutExtension(path)} <- {Path.GetFileName(source)} · bounds centro {b.center} tamaño {b.size}");
                    ok++;
                }
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[wg3-props] catalogo: {ok}/{total}");
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
