#if UNITY_EDITOR
using PolymindGames;
using PolymindGames.BuildingSystem;
using PolymindGames.SaveSystem;
using PolymindGames.WieldableSystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Authors the drywall sheet the player hauls on their shoulder: a clone of the vendor's metal
    /// carryable, repointed at the drywall mesh and at the build material the infill panel wants.
    /// Run via "Backrooms ▸ Create Carryables".
    ///
    /// Why cloning the metal one is the whole job: the chain from "plank on the shoulder" to "material
    /// consumed by a building piece" is already type-agnostic end to end —
    /// <c>CarryablePickup.TryUse</c> → <c>CarryableBuildAction.TryDoAction</c> →
    /// <c>constructableBuilder.TryAddMaterial(_buildMaterial)</c>. Nothing in it enumerates or
    /// validates which carryable it is, and the network layer is the same: the host relays
    /// <c>def_id</c> without ever checking it (<c>game_loop.rs</c> process_stp_carryable_pickup), so a
    /// new carryable is new VALUES in i32 fields that already exist. No wire change, no ADR, no Rust.
    ///
    /// crear-si-falta, exactly like <see cref="BackroomsBuildingPieceCreator"/> and for the identical
    /// reason: <c>DataDefinition</c> mints <c>_id</c> from <c>Random.Range(int.MinValue,
    /// int.MaxValue)</c>, and a CarryableDefinition's <c>_id</c> IS the <c>def_id</c> on the wire.
    /// Regenerating it would break replication against any client or save that knew the old one.
    /// COMMIT BOTH GENERATED ASSETS, plus SaveableDatabase.asset (see RefreshSaveableDatabase).
    ///
    /// The carry limit comes for free and matches metal exactly: <c>CarryableDefinition.MaxCarryCount</c>
    /// is <c>_wieldableSettings.Offsets.Length</c>, and the whole settings block is copied across. That
    /// array is load-bearing twice over — <c>WieldableCarryable</c> indexes <c>Offsets[Count - 1]</c>
    /// with no bounds check — so the copy is verified below instead of assumed.
    /// </summary>
    public static class BackroomsCarryableCreator
    {
        private const string PrefabFolder = "Assets/Prefabs/Carryables";
        private const string PrefabPath = PrefabFolder + "/BR_Carryable_Drywall.prefab";
        private const string RootName = "BR_Carryable_Drywall";

        // Must stay under "<a Resources folder>/Definitions/Carryable" — that literal path is what
        // DataDefinition.LoadDefinitionsFromResources scans. Resources.LoadAll merges every Resources
        // folder in the project, so this one is picked up alongside the vendor's.
        private const string DefinitionFolder = "Assets/Resources/Definitions/Carryable";
        private const string DefinitionPath = DefinitionFolder + "/BR_Drywall.asset";

        private const string MetalPrefabPath =
            "Assets/PolymindGames/STP/Prefabs/Carryables/STP_Carryable_Metal.prefab";
        private const string MetalDefinitionPath =
            "Assets/PolymindGames/STP/Data/Resources/Definitions/Carryable/STP_Metal.asset";

        // The same import the infill panel uses. One mesh, two pieces: what you carry is visibly the
        // thing you clad the frame with.
        private const string MeshPath =
            "Assets/MeshyImports/Back of a Flight Case_20260803_121714/Meshy_AI_Back_of_a_Flight_Case_0803101651_texture.fbx";
        private const string MaterialPath =
            "Assets/MeshyImports/Back of a Flight Case_20260803_121714/Material.001.mat";

        private const string SaveableDatabasePath =
            "Assets/PolymindGames/FPSCore/Data/Resources/Managers/SaveableDatabase.asset";

        // Stone, deliberately: it is what BR_BuildingPiece_GridPanel_Drywall already requires, so the
        // loop closes with zero new assets. Known consequence, accepted: a drywall sheet also becomes
        // valid fuel for the vendor's stone cabin and campfire, which consume the same id.
        // TODO(balance): revisit alongside MetalCost — a dedicated Drywall BuildMaterialDefinition is
        // a one-asset change if the shared-with-stone behaviour ever reads wrong.
        private const string BuildMaterialName = "Stone";

        // ── The mesh must arrive hand-sized FROM THE IMPORTER ────────────────────
        // There is nowhere left to scale it. WieldableCarryable forces localScale = Vector3.one the
        // moment a sheet is picked up and again when it is released, so a scaled ROOT would read right
        // on the ground and wrong in the hands; and the mesh cannot move to a scaled CHILD either,
        // because CarryablePickup caches GetComponent<MeshRenderer>() off the root.
        //
        // So the size lives in the FBX import settings (Scale Factor 100 on this asset: the Meshy
        // export is authored in centimetres, raw bounds 0.019 m, with the ×100 sitting on the model's
        // own root node where a bare MeshFilter never sees it). These bounds are a fail-loud guard
        // against that setting being reverted, not a tuning knob — a sheet outside this range is a
        // broken import, and authoring the prefab anyway would put a 4 mm speck in the player's hands
        // and be blamed on anything but the importer.
        private const float MinPlausibleSize = 0.5f;
        private const float MaxPlausibleSize = 4f;

        [MenuItem("Backrooms/Create Carryables")]
        public static void CreateIfMissing()
        {
            var existingDefinition = AssetDatabase.LoadAssetAtPath<CarryableDefinition>(DefinitionPath);
            var existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (existingDefinition != null && existingPrefab != null)
            {
                Debug.Log($"[BackroomsCarryableCreator] '{DefinitionPath}' and '{PrefabPath}' already exist — " +
                          "left untouched (the definition id is on the wire; regenerating it would break " +
                          "replication).");
                Selection.activeObject = existingDefinition;
                return;
            }

            if (existingDefinition != null || existingPrefab != null)
            {
                Debug.LogError("[BackroomsCarryableCreator] Half the pair exists " +
                               $"(definition={existingDefinition != null}, prefab={existingPrefab != null}). " +
                               "Refusing to regenerate: recreating the definition would mint a new def_id, " +
                               "recreating the prefab would orphan the existing one. Delete the survivor by hand " +
                               "and re-run, or restore the missing file from git.");
                return;
            }

            // Everything resolved BEFORE the first write. CreateDefinition calls AssetDatabase.CreateAsset
            // immediately, so a failure after it would leave exactly the half pair the guard above
            // refuses to repair.
            var metalPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MetalPrefabPath);
            var metalDefinition = AssetDatabase.LoadAssetAtPath<CarryableDefinition>(MetalDefinitionPath);
            var meshAsset = AssetDatabase.LoadAssetAtPath<GameObject>(MeshPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (metalPrefab == null || metalDefinition == null || meshAsset == null || material == null)
            {
                Debug.LogError("[BackroomsCarryableCreator] Missing source asset " +
                               $"(metal prefab={metalPrefab != null}, metal definition={metalDefinition != null}, " +
                               $"mesh={meshAsset != null} at '{MeshPath}', material={material != null}). " +
                               "Nothing created.");
                return;
            }

            var mesh = ResolveMesh(meshAsset);
            if (mesh == null)
            {
                Debug.LogError($"[BackroomsCarryableCreator] No MeshFilter with a mesh under '{MeshPath}'. " +
                               "Nothing created.");
                return;
            }

            // Checked on the RAW mesh, because that is what ends up on the prefab root. The model
            // asset's own root node carries a ×100 in this project's Meshy imports, so the size you
            // read by selecting the FBX is NOT the size a bare MeshFilter renders.
            var meshSize = mesh.bounds.size;
            float widest = Mathf.Max(meshSize.x, Mathf.Max(meshSize.y, meshSize.z));
            if (widest < MinPlausibleSize || widest > MaxPlausibleSize)
            {
                Debug.LogError($"[BackroomsCarryableCreator] '{MeshPath}' has raw bounds " +
                               $"({meshSize.x:F4}, {meshSize.y:F4}, {meshSize.z:F4}) — widest side {widest:F4} m, " +
                               $"outside the plausible {MinPlausibleSize}–{MaxPlausibleSize} m for something " +
                               "carried by hand. Set the model's Scale Factor so the RAW mesh is hand-sized " +
                               "(100 for this asset) and re-run. Nothing created — a prefab built from this " +
                               "would be invisible in play and the importer would not be the first suspect.");
                return;
            }

            var buildMaterial = BuildMaterialDefinition.GetWithName(BuildMaterialName);
            if (buildMaterial == null)
            {
                Debug.LogError($"[BackroomsCarryableCreator] No BuildMaterialDefinition named " +
                               $"'{BuildMaterialName}'. Without it the sheet would donate nothing when used " +
                               "on a frame. Nothing created.");
                return;
            }

            BackroomsEditorFolders.EnsureFolder("Assets/Prefabs");
            BackroomsEditorFolders.EnsureFolder(PrefabFolder);
            BackroomsEditorFolders.EnsureFolder("Assets/Resources");
            BackroomsEditorFolders.EnsureFolder("Assets/Resources/Definitions");
            BackroomsEditorFolders.EnsureFolder(DefinitionFolder);

            // The definition and the prefab reference each other, so the definition is created first
            // with an empty pickup slot and wired up once the prefab exists.
            var definition = CreateDefinition(metalDefinition);
            var prefab = CreatePrefab(definition, buildMaterial, mesh, material);
            if (prefab == null)
            {
                Debug.LogError($"[BackroomsCarryableCreator] Prefab creation failed; '{DefinitionPath}' is now an " +
                               "orphan half-pair. Delete it by hand before re-running.");
                return;
            }

            AssignPickupToDefinition(definition, prefab);

            // Make the new definition visible to the already-cached Definitions array, so a pickup
            // resolves it without a domain reload.
            CarryableDefinition.ReloadDefinitions_EditorOnly();
            RefreshSaveableDatabase();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = definition;
            Debug.Log($"[BackroomsCarryableCreator] Created '{DefinitionPath}' (def_id={definition.Id}) and " +
                      $"'{PrefabPath}' — carries {definition.MaxCarryCount} at a time, donates 1× " +
                      $"{BuildMaterialName} per sheet. COMMIT BOTH plus SaveableDatabase.asset: the def_id " +
                      "travels over the wire.");
        }

        private static Mesh ResolveMesh(GameObject meshAsset)
        {
            foreach (var filter in meshAsset.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null)
                    return filter.sharedMesh;
            }

            return null;
        }

        /// <summary>
        /// A fresh CarryableDefinition with metal's drop physics and carry settings copied in.
        ///
        /// Built with CreateInstance rather than CopyAsset on purpose: STP_Metal.asset carries a
        /// hidden sub-asset named _CarriableBuildAction pinned to the Metal material id, and
        /// CarryableDefinition declares no field of that type — it is vendor cruft nothing reads.
        /// Copying the asset would drag it along, pointing at the wrong material, to confuse the next
        /// person who opens the file.
        /// </summary>
        private static CarryableDefinition CreateDefinition(CarryableDefinition metal)
        {
            var definition = ScriptableObject.CreateInstance<CarryableDefinition>();
            AssetDatabase.CreateAsset(definition, DefinitionPath);

            // Assigns the unique auto-generated _id (AssignID is private; this is the vendor's own
            // entry point to it).
            definition.Validate_EditorOnly(
                new DataDefinition.ValidationContext(false, DataDefinition.ValidationTrigger.Created));

            var source = new SerializedObject(metal);
            var target = new SerializedObject(definition);

            // _wieldableSettings is the whole nested block: animator override, motion profile, socket,
            // the two base offsets and the per-count Offsets array. The animator and the motion
            // profile are [NotNull] and are DONATED by reference — the sheet animates like a metal
            // plate, which is the correct first pass for an object of the same size and mass class.
            target.CopyFromSerializedProperty(source.FindProperty("_icon"));
            target.CopyFromSerializedProperty(source.FindProperty("_dropForce"));
            target.CopyFromSerializedProperty(source.FindProperty("_dropTorque"));
            target.CopyFromSerializedProperty(source.FindProperty("_wieldableSettings"));
            target.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);

            // Fail loud rather than trust the copy. MaxCarryCount IS Offsets.Length, and
            // WieldableCarryable indexes Offsets[CarryCount - 1] with no bounds check — a short array
            // would not read as "carries fewer", it would throw the moment the player picked up one
            // too many.
            if (definition.MaxCarryCount != metal.MaxCarryCount)
            {
                Debug.LogError($"[BackroomsCarryableCreator] Carry offsets did not copy: drywall has " +
                               $"{definition.MaxCarryCount}, metal has {metal.MaxCarryCount}. Fix the array on " +
                               $"'{DefinitionPath}' by hand before playing — an index past its end throws in " +
                               "WieldableCarryable.");
            }

            return definition;
        }

        /// <summary>
        /// Clones the metal pickup prefab and swaps what makes it metal. Everything else — the
        /// Interactable, the surface impact handler, the rigidbody mass and drag, the material effect —
        /// is inherited unchanged, which is the point of cloning rather than authoring from scratch.
        /// </summary>
        private static GameObject CreatePrefab(CarryableDefinition definition,
            BuildMaterialDefinition buildMaterial, Mesh mesh, Material material)
        {
            if (!AssetDatabase.CopyAsset(MetalPrefabPath, PrefabPath))
            {
                Debug.LogError($"[BackroomsCarryableCreator] Could not copy '{MetalPrefabPath}' to '{PrefabPath}'.");
                return null;
            }

            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                root.name = RootName;

                // Mesh and renderer stay exactly where the vendor put them: on the ROOT.
                // CarryablePickup.Awake caches GetComponent<MeshRenderer>() and OnPickUp/OnDrop
                // dereference it unguarded, so a renderer parked on a child does not degrade — it
                // throws a MissingComponentException out of the drop key and the sheet can never be
                // put down. Leaving it in place also means the LODGroup and the MaterialEffect keep
                // pointing at a live renderer with nothing to rewire.
                var filter = root.GetComponent<MeshFilter>();
                var renderer = root.GetComponent<MeshRenderer>();
                if (filter == null || renderer == null)
                {
                    Debug.LogError("[BackroomsCarryableCreator] The cloned prefab has no MeshFilter/MeshRenderer " +
                                   "on its root. Refusing to author: the vendor pickup would throw on drop.");
                    return null;
                }

                filter.sharedMesh = mesh;
                renderer.sharedMaterial = material;

                // The cloned LODGroup still carries the metal plate's reference size (0.44 m). A 1.9 m
                // sheet would cull as if it were four times smaller and pop out at close range.
                var lodGroup = root.GetComponent<LODGroup>();
                if (lodGroup != null)
                    lodGroup.RecalculateBounds();

                // Collider straight off the mesh, not off remembered numbers. The last version of this
                // file carried hand-copied "native" dimensions that had been read off the model asset
                // WITH its root node applied, then used against the raw mesh, which is a factor of 100
                // in this project's Meshy imports.
                var box = root.GetComponent<BoxCollider>();
                if (box != null)
                {
                    box.center = mesh.bounds.center;
                    box.size = mesh.bounds.size;
                }

                SetIdReference(root.GetComponent<CarryablePickup>(), "_definition", definition.Id);
                SetIdReference(root.GetComponent<CarryableBuildAction>(), "_buildMaterial", buildMaterial.Id);

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        }

        /// <summary>Writes a DataIdReference&lt;T&gt; field, whose serialized shape is a bare int _value.</summary>
        private static void SetIdReference(Component component, string field, int id)
        {
            if (component == null)
            {
                Debug.LogError($"[BackroomsCarryableCreator] The cloned prefab has no component carrying " +
                               $"'{field}'. The drywall sheet will not behave like the metal one.");
                return;
            }

            var serialized = new SerializedObject(component);
            serialized.FindProperty(field).FindPropertyRelative("_value").intValue = id;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssignPickupToDefinition(CarryableDefinition definition, GameObject prefab)
        {
            var serialized = new SerializedObject(definition);
            serialized.FindProperty("_pickup").objectReferenceValue = prefab.GetComponent<CarryablePickup>();
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
        }

        /// <summary>
        /// Re-mints every saveable prefab's guid from its asset guid and rebuilds the lookup.
        ///
        /// Not optional. The clone arrives carrying the metal prefab's _prefabGuid verbatim, and
        /// SaveableDatabase.CreatePrefabsLookup is a Dictionary.Add with no try/catch — a duplicate key
        /// throws. FindAllSaveableObjectPrefabs reassigns any mismatched guid before the lookup is
        /// built, so calling it here closes the window. The database asset changes on disk as a result
        /// and has to be committed with the rest.
        /// </summary>
        private static void RefreshSaveableDatabase()
        {
            var database = AssetDatabase.LoadAssetAtPath<SaveableDatabase>(SaveableDatabasePath);
            if (database == null)
            {
                Debug.LogWarning($"[BackroomsCarryableCreator] No SaveableDatabase at '{SaveableDatabasePath}'. " +
                                 "The cloned prefab still carries metal's _prefabGuid; fix it before building a " +
                                 "player or the saveable lookup will throw on a duplicate key.");
                return;
            }

            database.SetPrefabs_Editor(SaveableDatabase.FindAllSaveableObjectPrefabs());
            EditorUtility.SetDirty(database);
        }
    }
}
#endif
