#if UNITY_EDITOR
using BackroomsSurvival.Gameplay.Building;
using BackroomsSurvival.Gameplay.GridWorld;
using PolymindGames;
using PolymindGames.BuildingSystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Authors the player-buildable Backrooms wall: a 5 × 4 × 0.2 prefab driven by
    /// <see cref="GridWallBuildingPiece"/> plus the <see cref="BuildingPieceDefinition"/> that puts
    /// it in the survival book. Run via "Backrooms ▸ Create Building Pieces".
    ///
    /// crear-si-falta, like <c>ZoneLootTableCreator</c> and deliberately NOT like
    /// <c>GridPrefabCreator</c>: re-running this menu never overwrites an existing prefab or
    /// definition. That is not a style preference — the definition's auto-generated <c>_id</c> is
    /// the <c>def_id</c> that travels over the wire on every placement, so regenerating it would
    /// silently break replication for every wall already standing in a save (and for any client
    /// running the old id). Same trap the inventory-swap audit flagged for
    /// "Backrooms ▸ Create Grid Prefabs".
    ///
    /// COMMIT BOTH GENERATED ASSETS. A fresh clone that re-runs this menu would mint a DIFFERENT
    /// random id and two players would then disagree about what a wall is. Same lesson as
    /// ZoneLootTable.asset.
    ///
    /// Everything it writes lives outside vendor folders. The single vendor asset it touches is the
    /// STP_Building category, whose member list has to gain the wall for the book page to show it —
    /// and it is touched through the vendor's own <c>SetParentGroup_EditorOnly</c> API, which keeps
    /// both sides of that relation in step.
    /// </summary>
    public static class BackroomsBuildingPieceCreator
    {
        private const string PrefabFolder = "Assets/Prefabs/Building";
        private const string PrefabPath = PrefabFolder + "/BR_BuildingPiece_GridWall.prefab";

        // Must stay under "<a Resources folder>/Definitions/BuildingPiece" — that literal path is
        // what DataDefinition.LoadDefinitionsFromResources scans. Resources.LoadAll merges every
        // Resources folder in the project, so this one is picked up alongside the vendor's.
        private const string DefinitionFolder = "Assets/Resources/Definitions/BuildingPiece";
        private const string DefinitionPath = DefinitionFolder + "/BR_Backrooms Wall.asset";

        private const string PanelPrefabPath = PrefabFolder + "/BR_BuildingPiece_GridPanel_Drywall.prefab";
        private const string PanelDefinitionPath = DefinitionFolder + "/BR_Backrooms Drywall.asset";
        private const string PanelMeshPath =
            "Assets/MeshyImports/Back of a Flight Case_20260803_121714/Meshy_AI_Back_of_a_Flight_Case_0803101651_texture.fbx";
        private const string PanelMaterialPath =
            "Assets/MeshyImports/Back of a Flight Case_20260803_121714/Material.001.mat";

        private const string WallMaterialPath = "Assets/Resources/GridMaterials/GridWall.mat";
        private const string PlaceEffectsPath =
            "Assets/PolymindGames/STP/Data/Resources/Definitions/BuildingPiece/Effects/STP_Effect_PlaceBuildingPiece.asset";
        private const string ConstructEffectsPath =
            "Assets/PolymindGames/STP/Data/Resources/Definitions/BuildingPiece/Effects/STP_Effect_ConstructFreePiece.asset";
        private const string IconDonorPath =
            "Assets/PolymindGames/STP/Data/Resources/Definitions/BuildingPiece/Structure/STP_Log_Wall.asset";

        // The book page that already exists for free-standing pieces. Matched by name (the "STP_"
        // prefix is stripped by DataDefinition.Name).
        private const string CategoryName = "Building";
        private const string MetalMaterialName = "Metal";

        // Gypsum board is a mineral sheet, so Stone rather than the frame's Metal — and needing two
        // different materials for one finished wall is the point, not an accident.
        // TODO(balance): first pass, never played, same standing as MetalCost.
        private const string StoneMaterialName = "Stone";
        private const int StoneCost = 2;

        // TODO(balance): first pass, never played. A 5 m panel for 4 metal is a guess anchored on
        // the Metal Crate (6 metal + 3 stone) — a wall should cost clearly less than a container.
        private const int MetalCost = 4;

        private const float Length = GridVisualConstants.TileSize;              // 5
        private const float Height = 2f * GridVisualConstants.CellHeight;       // 4
        private const float Thickness = GridVisualConstants.WallThickness;      // 0.2

        // The drywall sheet is MEASURED at author time, not remembered. The previous version carried
        // its size as constants read off "Backrooms ▸ Diagnostics ▸ Measure Building Meshes" — but that
        // menu reports the size WITH the model's root node applied, and this code then overwrote that
        // node's scale instead of multiplying by it. The Meshy imports carry ×100 on that node, so the
        // panel was authored a hundred times too small and simply did not appear in play.

        // MUST match the authored _gridColumns / _gridRows on GridWallBuildingPiece. The panel prefab
        // is sized for one cell of THIS subdivision, and its scale is baked into the asset — the
        // replicated copy is spawned from def_id + pose with no scale on the wire, so a panel cannot
        // resize itself at runtime to whatever frame it lands on. Retune the frame's grid and this
        // prefab has to be regenerated by hand.
        private const int PanelColumns = 5;
        private const int PanelRows = 4;

        // How many of those cells one drywall sheet covers. 2 × 2 gives a 2 × 2 m sheet — its real
        // proportions, undistorted — and tiles the frame four times, leaving a 1 × 4 m strip on one
        // side. That strip is deliberate: it is the slot a different piece goes in, which is where the
        // variety in a built wall comes from. Sizing the GRID to the sheet instead would leave the same
        // remainder with nothing able to fill it, because 5 is not a multiple of 2.
        private const int PanelFootprintColumns = 2;
        private const int PanelFootprintRows = 2;

        [MenuItem("Backrooms/Create Building Pieces")]
        public static void CreateIfMissing()
        {
            CreateWallIfMissing();
            CreatePanelIfMissing();
        }

        private static void CreateWallIfMissing()
        {
            var existingDefinition = AssetDatabase.LoadAssetAtPath<BuildingPieceDefinition>(DefinitionPath);
            var existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (existingDefinition != null && existingPrefab != null)
            {
                Debug.Log($"[BackroomsBuildingPieceCreator] '{DefinitionPath}' and '{PrefabPath}' already exist — " +
                          "left untouched (the definition id is on the wire; regenerating it would break replication).");
                Selection.activeObject = existingDefinition;
                return;
            }

            if (existingDefinition != null || existingPrefab != null)
            {
                Debug.LogError("[BackroomsBuildingPieceCreator] Half the pair exists " +
                               $"(definition={existingDefinition != null}, prefab={existingPrefab != null}). " +
                               "Refusing to regenerate: recreating the definition would mint a new def_id, " +
                               "recreating the prefab would orphan the existing one. Delete the survivor by hand " +
                               "and re-run, or restore the missing file from git.");
                return;
            }

            var metal = ResolveBuildMaterial(MetalMaterialName);
            if (metal == null)
                return;

            if (!TryResolveShared(out var category, out var placeEffects, out var constructEffects))
                return;

            EnsureFolders();

            // The definition and the prefab reference each other, so the definition is created first
            // with an empty prefab slot and wired up once the prefab exists.
            var definition = CreateDefinition(DefinitionPath, category, placeEffects, constructEffects,
                "A bare 5 × 4 metal panel. Snaps flush to the floor grid — no frame, no foundation.");
            var prefab = CreatePrefab(definition, metal);
            AssignPrefabToDefinition(definition, prefab);

            // Make the new definition visible to the already-cached Definitions array, so the book
            // lists it without a domain reload.
            BuildingPieceDefinition.ReloadDefinitions_EditorOnly();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = definition;
            Debug.Log($"[BackroomsBuildingPieceCreator] Created '{DefinitionPath}' (def_id={definition.Id}) and " +
                      $"'{PrefabPath}' — {MetalCost}× {MetalMaterialName}, category '{category.Name}'. " +
                      "COMMIT BOTH: the def_id travels over the wire.");
        }

        /// <summary>
        /// Authors the drywall sheet that clads one cell of a grid wall. Same crear-si-falta contract
        /// and the same def_id-is-on-the-wire hazard as the wall above — COMMIT BOTH ASSETS.
        /// </summary>
        private static void CreatePanelIfMissing()
        {
            var existingDefinition = AssetDatabase.LoadAssetAtPath<BuildingPieceDefinition>(PanelDefinitionPath);
            var existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PanelPrefabPath);
            if (existingDefinition != null && existingPrefab != null)
            {
                Debug.Log($"[BackroomsBuildingPieceCreator] '{PanelDefinitionPath}' and '{PanelPrefabPath}' already " +
                          "exist — left untouched (the definition id is on the wire; regenerating it would break " +
                          "replication).");
                return;
            }

            if (existingDefinition != null || existingPrefab != null)
            {
                Debug.LogError("[BackroomsBuildingPieceCreator] Half the drywall pair exists " +
                               $"(definition={existingDefinition != null}, prefab={existingPrefab != null}). " +
                               "Refusing to regenerate: recreating the definition would mint a new def_id, " +
                               "recreating the prefab would orphan the existing one. Delete the survivor by hand " +
                               "and re-run, or restore the missing file from git.");
                return;
            }

            var stone = ResolveBuildMaterial(StoneMaterialName);
            if (stone == null)
                return;

            // Resolved BEFORE the definition is written to disk: CreateDefinition calls
            // AssetDatabase.CreateAsset immediately, so failing after it would leave exactly the half
            // pair the guard above refuses to repair.
            var mesh = AssetDatabase.LoadAssetAtPath<GameObject>(PanelMeshPath);
            if (mesh == null)
            {
                Debug.LogError($"[BackroomsBuildingPieceCreator] Drywall mesh not found at '{PanelMeshPath}'. " +
                               "Nothing created.");
                return;
            }

            if (!TryResolveShared(out var category, out var placeEffects, out var constructEffects))
                return;

            EnsureFolders();

            var definition = CreateDefinition(PanelDefinitionPath, category, placeEffects, constructEffects,
                "A 2 × 2 gypsum sheet. Clads a metal frame — aim at a placed frame to fit it.");
            var prefab = CreatePanelPrefab(definition, stone, mesh);
            AssignPrefabToDefinition(definition, prefab);

            BuildingPieceDefinition.ReloadDefinitions_EditorOnly();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[BackroomsBuildingPieceCreator] Created '{PanelDefinitionPath}' (def_id={definition.Id}) and " +
                      $"'{PanelPrefabPath}' — {StoneCost}× {StoneMaterialName}, spanning " +
                      $"{PanelFootprintColumns} × {PanelFootprintRows} cells of a {PanelColumns} × {PanelRows} " +
                      "frame grid. COMMIT BOTH: the def_id travels over the wire.");
        }

        private static GameObject CreatePanelPrefab(BuildingPieceDefinition definition,
            BuildMaterialDefinition stone, GameObject mesh)
        {
            // The prefab is sized to the FOOTPRINT, not to one cell: a 2 × 2 sheet is 2 × 2 m of mesh
            // and collider, and GridPanelSnap centres it over the cells it spans.
            float cellWidth = GridPanelSnap.SlotLength / PanelColumns * PanelFootprintColumns;
            float cellHeight = GridPanelSnap.SlotHeight / PanelRows * PanelFootprintRows;

            var root = new GameObject("BR_BuildingPiece_GridPanel_Drywall")
            {
                layer = LayerConstants.Building
            };

            // The sizing wrapper, and the reason it exists: the model's root node carries BOTH its unit
            // conversion (a ×100 scale) and its axis conversion (a quarter turn on X that stands the
            // sheet upright). Writing either of those directly is what broke this prefab twice —
            // replacing the scale authored it a hundred times too small, and replacing the rotation
            // left it lying flat, so the "height" being measured was really its thickness and got
            // stretched to a full metre. So the model instance is left exactly as imported, and the
            // scale goes on this holder, which IS axis-aligned with the piece: x/y/z here mean
            // width/height/thickness there, whatever the model's own axes happen to be.
            var holder = new GameObject("Sheet") { layer = LayerConstants.Building };
            holder.transform.SetParent(root.transform, false);

            // Turned to face out. GridPanelSnap's contract is that a panel's local +Z points away from
            // the frame, and this sheet imports with its finished side on the other one — without this
            // the good face points INTO the bars on both sides of the wall, so every panel reads as its
            // own back. Corrected here rather than in the snapper: which way a mesh faces is a property
            // of the asset, and baking it into a pure function would make the next panel's art decide
            // how the grid maths works.
            holder.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

            // Nested as a prefab instance, like the wall carries the Steel Frame Grid: a re-import of
            // the FBX then flows through instead of being baked into a copy. Only the position is
            // reset — the FBX is centred on its own origin, so this is a no-op today and a guard
            // against a re-export that is not.
            var sheet = (GameObject)PrefabUtility.InstantiatePrefab(mesh, holder.transform);
            sheet.transform.localPosition = Vector3.zero;
            SetLayerRecursively(holder, LayerConstants.Building);

            var renderer = holder.GetComponentInChildren<MeshRenderer>();

            // Measured with the holder still at scale 1, so this is the model's authored world size.
            var authored = MeasureAuthoredSize(holder);
            if (authored.x < 0.0001f || authored.y < 0.0001f || authored.z < 0.0001f)
            {
                Debug.LogError($"[BackroomsBuildingPieceCreator] Could not measure the drywall sheet at " +
                               $"'{PanelMeshPath}' (got {authored}). Nothing created.");
                Object.DestroyImmediate(root);
                return null;
            }

            // Thickness follows the width factor rather than getting its own: the sheet's thickness is
            // a physical property, and scaling it independently would make a 1 m panel and a 2 m panel
            // sit at different depths off the frame face.
            float factorX = cellWidth / authored.x;
            float factorY = cellHeight / authored.y;
            holder.transform.localScale = new Vector3(factorX, factorY, factorX);
            float thickness = authored.z * factorX;

            // A sheet is a sheet. If the thinnest axis came out anywhere near the size of a cell, the
            // model was measured on the wrong axis — which is exactly what happened when this code
            // reset the model's rotation and then stretched its thickness to a full metre. Loud here
            // beats a 2 × 2 × 2 block of gypsum that nobody can explain in play.
            if (thickness > cellWidth * 0.5f)
            {
                Debug.LogError($"[BackroomsBuildingPieceCreator] Drywall sheet came out {thickness:F3} m thick " +
                               $"for a {cellWidth:F2} × {cellHeight:F2} m panel (authored size {authored}). That is " +
                               "a slab, not a sheet — the model's axes are not what this code assumes. " +
                               "Nothing created.");
                Object.DestroyImmediate(root);
                return null;
            }
            var material = AssetDatabase.LoadAssetAtPath<Material>(PanelMaterialPath);
            if (renderer != null && material != null)
                renderer.sharedMaterial = material;
            else
                Debug.LogWarning($"[BackroomsBuildingPieceCreator] Drywall renderer={renderer != null}, " +
                                 $"material at '{PanelMaterialPath}'={material != null}. The panel keeps whatever " +
                                 "the import assigned.");

            // On the ROOT, same load-bearing reason as the wall: both vendor detectors resolve their
            // component from the GameObject of the collider they hit, so a collider on the mesh child
            // makes the piece invisible to them and no build material could ever be added to it.
            var collider = root.AddComponent<BoxCollider>();
            collider.center = Vector3.zero;
            collider.size = new Vector3(cellWidth, cellHeight, thickness);

            // RequireComponent pulls in MaterialEffect (the ghost tint) with this call.
            var piece = root.AddComponent<GridPanelBuildingPiece>();
            var constructable = root.AddComponent<Constructable>();

            ConfigurePiece(piece, definition, new Bounds(Vector3.zero, new Vector3(cellWidth, cellHeight, thickness)));
            ConfigurePanel(piece, thickness * 0.5f);
            ConfigureConstructable(constructable, stone, StoneCost);
            if (renderer != null)
                ConfigureMaterialEffect(root.GetComponent<MaterialEffect>(), renderer);

            var saved = PrefabUtility.SaveAsPrefabAsset(root, PanelPrefabPath);
            Object.DestroyImmediate(root);
            return saved;
        }

        /// <summary>
        /// Writes the two things the snapper needs that cannot be derived from the prefab at runtime:
        /// the sheet's own half-thickness (the pivot is pushed off the frame face by this much, so a
        /// stale value sinks it into the bars) and the footprint the mesh was actually sized for. Both
        /// are set here rather than left at their field defaults so the asset can never disagree with
        /// the geometry this method just built.
        /// </summary>
        private static void ConfigurePanel(GridPanelBuildingPiece piece, float halfThickness)
        {
            var serialized = new SerializedObject(piece);
            serialized.FindProperty("_faceOffset").floatValue = halfThickness;
            serialized.FindProperty("_footprintColumns").intValue = PanelFootprintColumns;
            serialized.FindProperty("_footprintRows").intValue = PanelFootprintRows;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Size of a freshly instantiated model, WITH its own root node applied — the same quantity
        /// "Backrooms ▸ Diagnostics ▸ Measure Building Meshes" reports, computed the same way on
        /// purpose so the menu and this code can never disagree about what a mesh measures.
        ///
        /// Rotating the extents rather than building a true AABB is exact here and only here: these
        /// imports carry axis-aligned quarter-turns on their root node, so the components merely swap.
        /// A model with an arbitrary tilt would need a real bounds encapsulation.
        /// </summary>
        private static Vector3 MeasureAuthoredSize(GameObject instance)
        {
            var filter = instance.GetComponentInChildren<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
                return Vector3.zero;

            var extents = filter.transform.localToWorldMatrix.MultiplyVector(filter.sharedMesh.bounds.extents);
            return new Vector3(Mathf.Abs(extents.x), Mathf.Abs(extents.y), Mathf.Abs(extents.z)) * 2f;
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursively(child.gameObject, layer);
        }

        private static BuildMaterialDefinition ResolveBuildMaterial(string name)
        {
            var material = BuildMaterialDefinition.GetWithName(name);
            if (material != null)
                return material;

            Debug.LogError($"[BackroomsBuildingPieceCreator] No BuildMaterialDefinition named " +
                           $"'{name}'. Expected the STP_{name} asset under " +
                           "Definitions/BuildMaterial; without it the piece would have no build cost.");
            return null;
        }

        /// <summary>
        /// The category and the two effect configs every piece here needs. Both effect slots are
        /// [NotNull] on the vendor definition and are dereferenced unguarded by
        /// BuildingPiece.SetPlacedState / SetConstructedState — resolving them up front turns a
        /// missing asset into an error here instead of a NullReferenceException the first time a
        /// player places one, and stops us writing half a pair to disk.
        /// </summary>
        private static bool TryResolveShared(out BuildingPieceCategoryDefinition category,
            out EffectPairConfig placeEffects, out EffectPairConfig constructEffects)
        {
            placeEffects = null;
            constructEffects = null;

            category = BuildingPieceCategoryDefinition.GetWithName(CategoryName);
            if (category == null)
            {
                Debug.LogError($"[BackroomsBuildingPieceCreator] No BuildingPieceCategoryDefinition named " +
                               $"'{CategoryName}'. Without a category the piece never appears in the survival book.");
                return false;
            }

            placeEffects = AssetDatabase.LoadAssetAtPath<EffectPairConfig>(PlaceEffectsPath);
            constructEffects = AssetDatabase.LoadAssetAtPath<EffectPairConfig>(ConstructEffectsPath);
            if (placeEffects == null || constructEffects == null)
            {
                Debug.LogError("[BackroomsBuildingPieceCreator] Missing effect config " +
                               $"(place={placeEffects != null} at '{PlaceEffectsPath}', " +
                               $"construct={constructEffects != null} at '{ConstructEffectsPath}'). " +
                               "Nothing created.");
                return false;
            }

            return true;
        }

        private static void EnsureFolders()
        {
            EnsureFolder("Assets/Prefabs");
            EnsureFolder(PrefabFolder);
            EnsureFolder("Assets/Resources");
            EnsureFolder("Assets/Resources/Definitions");
            EnsureFolder(DefinitionFolder);
        }

        private static BuildingPieceDefinition CreateDefinition(string path,
            BuildingPieceCategoryDefinition category, EffectPairConfig placeEffects,
            EffectPairConfig constructEffects, string description)
        {
            var definition = ScriptableObject.CreateInstance<BuildingPieceDefinition>();
            AssetDatabase.CreateAsset(definition, path);

            // Assigns the unique auto-generated _id (AssignID is private; this is the vendor's own
            // entry point to it).
            definition.Validate_EditorOnly(new DataDefinition.ValidationContext(false, DataDefinition.ValidationTrigger.Created));

            // Sets _parentGroup AND adds the wall to the category's member list — the book page
            // enumerates that list, so both halves are required.
            definition.SetParentGroup_EditorOnly(category);

            var serialized = new SerializedObject(definition);
            serialized.FindProperty("_description").stringValue = description;
            serialized.FindProperty("_placeEffects").objectReferenceValue = placeEffects;
            serialized.FindProperty("_constructEffects").objectReferenceValue = constructEffects;

            var iconDonor = AssetDatabase.LoadAssetAtPath<BuildingPieceDefinition>(IconDonorPath);
            if (iconDonor != null)
                SetObjectReference(serialized, "_icon", iconDonor.Icon);

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
            return definition;
        }

        private static GameObject CreatePrefab(BuildingPieceDefinition definition, BuildMaterialDefinition metal)
        {
            var root = new GameObject("BR_BuildingPiece_GridWall")
            {
                layer = LayerConstants.Building
            };

            // The visible panel, authored exactly like the procedural wall prefab: pivot on the
            // floor, box centred at half-height. RENDER ONLY — its primitive collider is discarded,
            // see below.
            var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            panel.name = "Panel";
            panel.layer = LayerConstants.Building;
            panel.transform.SetParent(root.transform, false);
            panel.transform.localPosition = new Vector3(0f, Height * 0.5f, 0f);
            panel.transform.localScale = new Vector3(Length, Height, Thickness);
            Object.DestroyImmediate(panel.GetComponent<Collider>());

            // The collider belongs on the ROOT, not on the mesh child, and this is load-bearing:
            // both vendor detectors resolve their component from the GameObject of the collider
            // they hit — CharacterConstructableBuilder.GetClosestConstructable does
            // col.TryGetComponent(out IConstructable), CharacterBuildController.FindValidSocket does
            // the same for BuildingPiece. With the collider on a child, the wall would be invisible
            // to both: no build material could ever be added to it. Every vendor piece is authored
            // this way (STP_BuildingPIece_MetalCrate: BoxCollider on the root, mesh children bare).
            var collider = root.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, Height * 0.5f, 0f);
            collider.size = new Vector3(Length, Height, Thickness);

            var material = AssetDatabase.LoadAssetAtPath<Material>(WallMaterialPath);
            if (material != null)
                panel.GetComponent<MeshRenderer>().sharedMaterial = material;
            else
                Debug.LogWarning($"[BackroomsBuildingPieceCreator] '{WallMaterialPath}' not found; the wall keeps " +
                                 "the default primitive material. Run \"Backrooms ▸ Create Grid Prefabs\" to author it.");

            // RequireComponent pulls in MaterialEffect (the ghost tint) with this call.
            var piece = root.AddComponent<GridWallBuildingPiece>();
            var constructable = root.AddComponent<Constructable>();

            ConfigurePiece(piece, definition,
                new Bounds(new Vector3(0f, Height * 0.5f, 0f), new Vector3(Length, Height, Thickness)));
            ConfigureConstructable(constructable, metal, MetalCost);
            ConfigureMaterialEffect(root.GetComponent<MaterialEffect>(), panel.GetComponent<MeshRenderer>());

            var saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static void ConfigurePiece(BuildingPiece piece, BuildingPieceDefinition definition, Bounds localBounds)
        {
            var serialized = new SerializedObject(piece);
            serialized.FindProperty("_definition").objectReferenceValue = definition;

            // Local bounds must describe the mesh, not the (empty) root: GetWorldBounds derives the
            // character-overlap box from them. Size is intentionally left unrotated — the vendor
            // passes transform.rotation to the overlap query separately, so the pair is a correct OBB.
            serialized.FindProperty("_localBounds").boundsValue = localBounds;

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureConstructable(Constructable constructable,
            BuildMaterialDefinition material, int amount)
        {
            var serialized = new SerializedObject(constructable);
            var requirements = serialized.FindProperty("_requirements");
            requirements.arraySize = 1;

            var requirement = requirements.GetArrayElementAtIndex(0);
            requirement.FindPropertyRelative("BuildMaterialId").intValue = material.Id;
            requirement.FindPropertyRelative("CurrentAmount").intValue = 0;
            requirement.FindPropertyRelative("RequiredAmount").intValue = amount;

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureMaterialEffect(MaterialEffect effect, MeshRenderer renderer)
        {
            var serialized = new SerializedObject(effect);
            var renderers = serialized.FindProperty("_renderers");
            renderers.arraySize = 1;
            renderers.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssignPrefabToDefinition(BuildingPieceDefinition definition, GameObject prefab)
        {
            var serialized = new SerializedObject(definition);
            serialized.FindProperty("_prefab").objectReferenceValue = prefab.GetComponent<BuildingPiece>();
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
        }

        private static void SetObjectReference(SerializedObject serialized, string path, Object value)
        {
            if (value != null)
                serialized.FindProperty(path).objectReferenceValue = value;
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
