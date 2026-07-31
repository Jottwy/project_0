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

        // TODO(balance): first pass, never played. A 5 m panel for 4 metal is a guess anchored on
        // the Metal Crate (6 metal + 3 stone) — a wall should cost clearly less than a container.
        private const int MetalCost = 4;

        private const float Length = GridVisualConstants.TileSize;              // 5
        private const float Height = 2f * GridVisualConstants.CellHeight;       // 4
        private const float Thickness = GridVisualConstants.WallThickness;      // 0.2

        [MenuItem("Backrooms/Create Building Pieces")]
        public static void CreateIfMissing()
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

            var metal = ResolveBuildMaterial();
            if (metal == null)
                return;

            var category = BuildingPieceCategoryDefinition.GetWithName(CategoryName);
            if (category == null)
            {
                Debug.LogError($"[BackroomsBuildingPieceCreator] No BuildingPieceCategoryDefinition named " +
                               $"'{CategoryName}'. Without a category the wall never appears in the survival book.");
                return;
            }

            // Both effect slots are [NotNull] on the vendor definition and are dereferenced
            // unguarded by BuildingPiece.SetPlacedState / SetConstructedState. Resolving them up
            // front turns a missing asset into an error here instead of a NullReferenceException
            // the first time a player places a wall — and stops us writing half a pair to disk.
            var placeEffects = AssetDatabase.LoadAssetAtPath<EffectPairConfig>(PlaceEffectsPath);
            var constructEffects = AssetDatabase.LoadAssetAtPath<EffectPairConfig>(ConstructEffectsPath);
            if (placeEffects == null || constructEffects == null)
            {
                Debug.LogError("[BackroomsBuildingPieceCreator] Missing effect config " +
                               $"(place={placeEffects != null} at '{PlaceEffectsPath}', " +
                               $"construct={constructEffects != null} at '{ConstructEffectsPath}'). " +
                               "Nothing created.");
                return;
            }

            EnsureFolder("Assets/Prefabs");
            EnsureFolder(PrefabFolder);
            EnsureFolder("Assets/Resources");
            EnsureFolder("Assets/Resources/Definitions");
            EnsureFolder(DefinitionFolder);

            // The definition and the prefab reference each other, so the definition is created first
            // with an empty prefab slot and wired up once the prefab exists.
            var definition = CreateDefinition(category, placeEffects, constructEffects);
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

        private static BuildMaterialDefinition ResolveBuildMaterial()
        {
            var metal = BuildMaterialDefinition.GetWithName(MetalMaterialName);
            if (metal != null)
                return metal;

            Debug.LogError($"[BackroomsBuildingPieceCreator] No BuildMaterialDefinition named " +
                           $"'{MetalMaterialName}'. Expected the STP_Metal asset under " +
                           "Definitions/BuildMaterial; without it the wall would have no build cost.");
            return null;
        }

        private static BuildingPieceDefinition CreateDefinition(BuildingPieceCategoryDefinition category,
            EffectPairConfig placeEffects, EffectPairConfig constructEffects)
        {
            var definition = ScriptableObject.CreateInstance<BuildingPieceDefinition>();
            AssetDatabase.CreateAsset(definition, DefinitionPath);

            // Assigns the unique auto-generated _id (AssignID is private; this is the vendor's own
            // entry point to it).
            definition.Validate_EditorOnly(new DataDefinition.ValidationContext(false, DataDefinition.ValidationTrigger.Created));

            // Sets _parentGroup AND adds the wall to the category's member list — the book page
            // enumerates that list, so both halves are required.
            definition.SetParentGroup_EditorOnly(category);

            var serialized = new SerializedObject(definition);
            serialized.FindProperty("_description").stringValue =
                "A bare 5 × 4 metal panel. Snaps flush to the floor grid — no frame, no foundation.";
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

            ConfigurePiece(piece, definition);
            ConfigureConstructable(constructable, metal);
            ConfigureMaterialEffect(root.GetComponent<MaterialEffect>(), panel.GetComponent<MeshRenderer>());

            var saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static void ConfigurePiece(GridWallBuildingPiece piece, BuildingPieceDefinition definition)
        {
            var serialized = new SerializedObject(piece);
            serialized.FindProperty("_definition").objectReferenceValue = definition;

            // Local bounds must describe the panel, not the (empty) root: GetWorldBounds derives the
            // character-overlap box from them. Size is intentionally left unrotated — the vendor
            // passes transform.rotation to the overlap query separately, so the pair is a correct OBB.
            serialized.FindProperty("_localBounds").boundsValue =
                new Bounds(new Vector3(0f, Height * 0.5f, 0f), new Vector3(Length, Height, Thickness));

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureConstructable(Constructable constructable, BuildMaterialDefinition metal)
        {
            var serialized = new SerializedObject(constructable);
            var requirements = serialized.FindProperty("_requirements");
            requirements.arraySize = 1;

            var requirement = requirements.GetArrayElementAtIndex(0);
            requirement.FindPropertyRelative("BuildMaterialId").intValue = metal.Id;
            requirement.FindPropertyRelative("CurrentAmount").intValue = 0;
            requirement.FindPropertyRelative("RequiredAmount").intValue = MetalCost;

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
            serialized.FindProperty("_prefab").objectReferenceValue = prefab.GetComponent<GridWallBuildingPiece>();
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
