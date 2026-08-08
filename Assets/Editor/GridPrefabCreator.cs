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
            BackroomsEditorFolders.EnsureFolder("Assets/Resources");
            BackroomsEditorFolders.EnsureFolder(PrefabFolder);
            BackroomsEditorFolders.EnsureFolder(MaterialFolder);

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
            SavePrefab(BuildFloorSlab(floor), "FloorSlab");
            SavePrefab(BuildWall(wall), "Wall");
            SavePrefab(BuildPillar(pillar), "Pillar");
            SavePrefab(BuildVoidEdge(voidEdge), "VoidEdge");
            SavePrefab(BuildOfficeWall(wall, pillar), "OfficeWall");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[GridPrefabCreator] 7 grid prefabs saved to {PrefabFolder}");
        }

        // ---- prefab builders -------------------------------------------------

        /// <summary>Floor panel covering one 5 m tile, top face at y = 0.</summary>
        private static GameObject BuildFloor(Material mat)
        {
            var root = new GameObject("Floor");
            var floor = Box("Floor", root, mat,
                new Vector3(0, -0.04f, 0),
                new Vector3(GridVisualConstants.TileSize, 0.08f, GridVisualConstants.TileSize));
            floor.isStatic = true;
            return root;
        }

        /// <summary>
        /// Shared horizontal slab covering one 5 m tile, CENTRED on its plane
        /// (top face +0.04, bottom face -0.04). One slab is the floor of layer N
        /// and the ceiling of layer N-1 at the same time — the builder positions
        /// it at the right Y so adjacent layers stop double-stacking floor+ceiling.
        /// Double-sided GridFloor material: reads as floor from above, ceiling
        /// from below.
        /// </summary>
        private static GameObject BuildFloorSlab(Material mat)
        {
            var root = new GameObject("FloorSlab");
            var slab = Box("Slab", root, mat,
                Vector3.zero,
                new Vector3(GridVisualConstants.TileSize, 0.08f, GridVisualConstants.TileSize));
            slab.isStatic = true;
            return root;
        }

        /// <summary>Ceiling panel covering one 5 m tile at the fixed 4 m room height.</summary>
        private static GameObject BuildCeiling(Material mat)
        {
            var root = new GameObject("Ceiling");
            var ceiling = Box("Ceiling", root, mat,
                new Vector3(0, 2f * GridVisualConstants.CellHeight + 0.04f, 0),
                new Vector3(GridVisualConstants.TileSize, 0.08f, GridVisualConstants.TileSize));
            ceiling.isStatic = true;
            return root;
        }

        /// <summary>
        /// Wall tile piece (5×4×0.2 m). GridChunkBuilder instantiates one per
        /// tile edge that needs a wall — no procedural mesh.
        /// </summary>
        private static GameObject BuildWall(Material mat)
        {
            var root = new GameObject("Wall");
            Box("Cube", root, mat,
                new Vector3(0, GridVisualConstants.CellHeight, 0),
                new Vector3(GridVisualConstants.TileSize, 2f * GridVisualConstants.CellHeight, GridVisualConstants.WallThickness));
            return root;
        }

        /// <summary>
        /// ADR-035 — variante de pared de ZONE_OFFICE: tabique de despacho (antepecho macizo,
        /// banda acristalada, cabecero). Se selecciona por (zone_kind, RoomType) desde
        /// <c>LayerVisualConfig.wallVariantSets</c>, no reemplaza al panel de siempre.
        ///
        /// CONTRATO (doc-comment de <c>WallVariant</c>): 5 m × 4 m × 0.2 m con el PIVOTE EN EL
        /// SUELO, igual que Wall.prefab. Se respeta pieza a pieza — el conjunto ocupa
        /// exactamente 0..4 m en Y y ±TileSize/2 en X. Romperlo no lanza ningún error: escala
        /// mal los knee walls y los dinteles, que dividen por esa altura de 4 m hardcodeada.
        ///
        /// La banda acristalada es un HUECO REAL en la malla, no un material transparente: el
        /// panel lleva collider de caja por bounds (<c>AddColliderIfMissing</c>), así que un
        /// cristal no cambiaría la colisión y sí costaría una pasada de transparencias.
        /// Dejar el hueco da la lectura de tabique de oficina y mantiene el panel opaco al
        /// paso, que es lo que el backend dice de esa arista.
        /// </summary>
        private static GameObject BuildOfficeWall(Material panelMat, Material trimMat)
        {
            const float H = 2f * GridVisualConstants.CellHeight;   // 4 m
            const float W = GridVisualConstants.TileSize;          // 5 m
            const float T = GridVisualConstants.WallThickness;     // 0.2 m

            const float SillTop = 1.1f;   // antepecho: por debajo de la vista
            const float HeadFrom = 2.3f;  // cabecero: arranca por encima de la cabeza (1.8)

            var root = new GameObject("OfficeWall");
            // Antepecho macizo, del suelo a SillTop.
            Box("Sill", root, panelMat, new Vector3(0f, SillTop * 0.5f, 0f), new Vector3(W, SillTop, T));
            // Cabecero, de HeadFrom al techo.
            Box("Head", root, panelMat,
                new Vector3(0f, (HeadFrom + H) * 0.5f, 0f), new Vector3(W, H - HeadFrom, T));
            // Montantes: los dos extremos y uno central, que cierran la banda por los lados y
            // son lo que de verdad lee como "tabique modular" y no como ventana.
            float bandY = (SillTop + HeadFrom) * 0.5f;
            float bandH = HeadFrom - SillTop;
            foreach (float x in new[] { -W * 0.5f + 0.08f, 0f, W * 0.5f - 0.08f })
                Box("Mullion", root, trimMat, new Vector3(x, bandY, 0f), new Vector3(0.16f, bandH, T));
            return root;
        }

        /// <summary>Column 0.7 m thick, authored at the uniform 4 m room height.</summary>
        private static GameObject BuildPillar(Material mat)
        {
            var root = new GameObject("Pillar");
            Box("Shaft", root, mat,
                new Vector3(0, GridVisualConstants.CellHeight, 0),
                new Vector3(0.7f, 2f * GridVisualConstants.CellHeight, 0.7f));
            return root;
        }

        /// <summary>
        /// Lip along one tile edge bordering a hole (5 m long). Authored on the
        /// +z edge; the builder rotates it to the actual border side.
        /// </summary>
        private static GameObject BuildVoidEdge(Material mat)
        {
            var root = new GameObject("VoidEdge");
            Box("Lip", root, mat,
                new Vector3(0, 0.1f, GridVisualConstants.TileSize / 2f - 0.125f),
                new Vector3(GridVisualConstants.TileSize, 0.2f, 0.25f));
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
    }
}
#endif
