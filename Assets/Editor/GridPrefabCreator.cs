#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using BackroomsSurvival.Gameplay.GridWorld;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Creates the 5 primitive grid prefabs (§6 of BACKROOMS_GRID_SYSTEM.md)
    /// as placeholder boxes, plus their material assets. Saved under
    /// Resources/GridPrefabs so GridChunkBuilder can load them without scene
    /// wiring. All placeholders are art-swappable later; only the names and
    /// child layout are API.
    ///
    /// No colliders: Rust is the collision authority, Unity only renders.
    /// </summary>
    public static class GridPrefabCreator
    {
        private const string PrefabFolder = "Assets/Resources/GridPrefabs";
        private const string MaterialFolder = "Assets/Resources/GridMaterials";

        // Backrooms palette: sickly yellow wallpaper, damp carpet, dull panels.
        private static readonly Color WallYellow = new Color(0.78f, 0.70f, 0.38f);
        private static readonly Color FloorCarpet = new Color(0.55f, 0.48f, 0.26f);
        private static readonly Color CeilingPanel = new Color(0.82f, 0.78f, 0.62f);
        private static readonly Color PillarTone = new Color(0.70f, 0.64f, 0.40f);
        private static readonly Color VoidDark = new Color(0.05f, 0.05f, 0.06f);

        [MenuItem("Backrooms/Create Grid Prefabs")]
        public static void CreateAll()
        {
            EnsureFolder("Assets/Resources");
            EnsureFolder(PrefabFolder);
            EnsureFolder(MaterialFolder);

            // Wall uses a custom shader with polygon Offset so its double-sided
            // side faces lose depth ties to the coplanar floor/ceiling panel edges
            // (URP/Lit has no offset property). Other surfaces stay on URP/Lit.
            Material wall = CreateMaterial("GridWall", WallYellow, "Backrooms/GridWallOffset");
            Material floor = CreateMaterial("GridFloor", FloorCarpet);
            Material ceiling = CreateMaterial("GridCeiling", CeilingPanel);
            Material pillar = CreateMaterial("GridPillar", PillarTone);
            Material voidEdge = CreateMaterial("GridVoidEdge", VoidDark);

            SavePrefab(BuildFloor(floor), "Floor");
            SavePrefab(BuildCeiling(ceiling), "Ceiling");
            SavePrefab(BuildWall(wall), "Wall");
            SavePrefab(BuildPillar(pillar), "Pillar");
            SavePrefab(BuildVoidEdge(voidEdge), "VoidEdge");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[GridPrefabCreator] 5 grid prefabs saved to {PrefabFolder}");
        }

        // ---- prefab builders -------------------------------------------------

        /// <summary>Floor panel covering one cell, top face at y = 0.</summary>
        private static GameObject BuildFloor(Material mat)
        {
            float cs = GridConstants.CellSize;
            var root = new GameObject("Floor");
            var floor = Box("Floor", root, mat,
                new Vector3(0, -0.04f, 0), new Vector3(cs, 0.08f, cs));
            floor.isStatic = true;
            return root;
        }

        /// <summary>Ceiling panel covering one cell at the fixed 4 m room height.</summary>
        private static GameObject BuildCeiling(Material mat)
        {
            float cs = GridConstants.CellSize;
            var root = new GameObject("Ceiling");
            var ceiling = Box("Ceiling", root, mat,
                new Vector3(0, 2f * GridVisualConstants.CellHeight + 0.04f, 0),
                new Vector3(cs, 0.08f, cs));
            ceiling.isStatic = true;
            return root;
        }

        /// <summary>
        /// Reference wall cube (material/visual reference only — real walls
        /// come out of WallGreedyMesher as one mesh per chunk).
        /// </summary>
        private static GameObject BuildWall(Material mat)
        {
            var root = new GameObject("Wall");
            Box("Cube", root, mat,
                new Vector3(0, GridVisualConstants.CellHeight, 0),
                new Vector3(GridVisualConstants.TileSize, 2f * GridVisualConstants.CellHeight, GridVisualConstants.WallThickness));
            return root;
        }

        /// <summary>Column. Child "Shaft" is scaled by the builder to reach the cell ceiling.</summary>
        private static GameObject BuildPillar(Material mat)
        {
            float cs = GridConstants.CellSize;
            var root = new GameObject("Pillar");
            Box("Shaft", root, mat,
                new Vector3(0, cs, 0), new Vector3(0.7f, 2 * cs, 0.7f));
            return root;
        }

        /// <summary>
        /// Lip along one cell edge bordering a Void. Authored on the +z edge;
        /// the builder rotates it to the actual border side.
        /// </summary>
        private static GameObject BuildVoidEdge(Material mat)
        {
            float cs = GridConstants.CellSize;
            var root = new GameObject("VoidEdge");
            Box("Lip", root, mat,
                new Vector3(0, 0.1f, cs / 2f - 0.125f), new Vector3(cs, 0.2f, 0.25f));
            return root;
        }

        // ---- helpers ---------------------------------------------------------

        private static GameObject Box(string name, GameObject parent, Material mat,
            Vector3 localPos, Vector3 localScale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;

            // Render only — collision stays in Rust.
            var collider = go.GetComponent<Collider>();
            if (collider != null)
                Object.DestroyImmediate(collider);

            return go;
        }

        private static Material CreateMaterial(string name, Color color,
            string shaderName = "Universal Render Pipeline/Lit")
        {
            string path = $"{MaterialFolder}/{name}.mat";
            Shader shader = Shader.Find(shaderName)
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard");

            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null)
            {
                if (shader != null && mat.shader != shader)
                    mat.shader = shader; // re-run upgrades the wall to the offset shader
                ApplyColor(mat, color);
                EditorUtility.SetDirty(mat);
                return mat;
            }

            mat = new Material(shader) { name = name };
            ApplyColor(mat, color);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        private static void ApplyColor(Material mat, Color color)
        {
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);
            // Matte surfaces — fluorescent-lit office, nothing glossy.
            if (mat.HasProperty("_Smoothness"))
                mat.SetFloat("_Smoothness", 0.08f);
            if (mat.HasProperty("_Glossiness"))
                mat.SetFloat("_Glossiness", 0.08f);
            // Render both faces: grid surfaces (ceilings, wall tops, void edges)
            // are viewed from both sides; single-sided culling makes them
            // transparent from the reverse side.
            if (mat.HasProperty("_Cull"))
                mat.SetFloat("_Cull", 0f); // CullMode.Off
            mat.doubleSidedGI = true;
        }

        private static void SavePrefab(GameObject root, string name)
        {
            string path = $"{PrefabFolder}/{name}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            int slash = path.LastIndexOf('/');
            AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
        }
    }
}
#endif
