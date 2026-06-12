#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using BackroomsSurvival.Gameplay.GridWorld;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Creates the 6 primitive grid prefabs (§6 of BACKROOMS_GRID_SYSTEM.md)
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

            Material wall = CreateMaterial("GridWall", WallYellow);
            Material floor = CreateMaterial("GridFloor", FloorCarpet);
            Material ceiling = CreateMaterial("GridCeiling", CeilingPanel);
            Material pillar = CreateMaterial("GridPillar", PillarTone);
            Material voidEdge = CreateMaterial("GridVoidEdge", VoidDark);

            SavePrefab(BuildFloorCeiling(floor, ceiling), "FloorCeiling");
            SavePrefab(BuildWall(wall), "Wall");
            SavePrefab(BuildPillar(pillar), "Pillar");
            SavePrefab(BuildStair(floor), "Stair");
            SavePrefab(BuildVoidEdge(voidEdge), "VoidEdge");
            SavePrefab(BuildCeilingStep(ceiling), "CeilingStep");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[GridPrefabCreator] 6 grid prefabs saved to {PrefabFolder}");
        }

        // ---- prefab builders -------------------------------------------------

        /// <summary>
        /// Floor + ceiling tile. Child "Ceiling" sits at the default corridor
        /// height (2 units); GridChunkBuilder repositions it per cell to
        /// ceilingHeight × CellSize.
        /// </summary>
        private static GameObject BuildFloorCeiling(Material floorMat, Material ceilingMat)
        {
            float cs = GridConstants.CellSize;
            var root = new GameObject("FloorCeiling");

            var floor = Box("Floor", root, floorMat,
                new Vector3(0, -0.04f, 0), new Vector3(cs, 0.08f, cs));
            floor.isStatic = true;

            var ceiling = Box("Ceiling", root, ceilingMat,
                new Vector3(0, 2 * cs + 0.04f, 0), new Vector3(cs, 0.08f, cs));
            ceiling.isStatic = true;

            return root;
        }

        /// <summary>
        /// Reference wall cube (material/visual reference only — real walls
        /// come out of WallGreedyMesher as one mesh per chunk).
        /// </summary>
        private static GameObject BuildWall(Material mat)
        {
            float cs = GridConstants.CellSize;
            var root = new GameObject("Wall");
            Box("Cube", root, mat, new Vector3(0, cs / 2f, 0), new Vector3(cs, cs, cs));
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
        /// Stair climbing one full layer (LayerHeight) inside one cell —
        /// steep placeholder; final art may span 2 cells.
        /// </summary>
        private static GameObject BuildStair(Material mat)
        {
            float cs = GridConstants.CellSize;
            float rise = GridConstants.LayerHeight;
            const int steps = 6;
            float stepDepth = cs / steps;
            float stepRise = rise / steps;

            var root = new GameObject("Stair");
            for (int i = 0; i < steps; i++)
            {
                float h = (i + 1) * stepRise;
                Box($"Step{i}", root, mat,
                    new Vector3(0, h / 2f, -cs / 2f + (i + 0.5f) * stepDepth),
                    new Vector3(cs, h, stepDepth));
            }
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

        /// <summary>
        /// Fascia closing the gap between two ceiling heights. Authored 1 unit
        /// tall on the +z edge; the builder scales it to the actual gap and
        /// rotates it to the boundary side.
        /// </summary>
        private static GameObject BuildCeilingStep(Material mat)
        {
            float cs = GridConstants.CellSize;
            var root = new GameObject("CeilingStep");
            Box("Fascia", root, mat,
                new Vector3(0, -cs / 2f, cs / 2f - 0.075f), new Vector3(cs, cs, 0.15f));
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

        private static Material CreateMaterial(string name, Color color)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                ApplyColor(existing, color);
                EditorUtility.SetDirty(existing);
                return existing;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                shader = Shader.Find("Standard");

            var mat = new Material(shader) { name = name };
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
