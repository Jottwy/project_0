#if UNITY_EDITOR
using System.IO;
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

        // ADR-081 pieza 3 — el marcador de territorio.
        private const string MarkerPrefabPath = PrefabFolder + "/BR_BuildingPiece_ClaimMarker.prefab";
        private const string MarkerDefinitionPath = DefinitionFolder + "/BR_Claim Marker.asset";
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

        // ADR-081 pieza 3: un poste de 1,8 m que se ve desde lejos y no se confunde con nada.
        private const float MarkerHeight = 1.8f;
        private const float MarkerSide = 0.35f;

        // TODO(balance): primera pasada, nunca jugado. Reclamar tiene que costar MÁS que una pared
        // suelta (4 metal) porque abre un territorio entero, y menos que un edificio, porque sin
        // reclamar no se puede construir nada — un marcador impagable cierra el juego, no lo protege.
        private const int MarkerMetalCost = 6;

        // Steel door frame, imported 2026-08-18. A drop-in alternate to the plain wall: same 5 x 4 x
        // 0.2 slot, same GridWallBuildingPiece, but a compound collider leaves its door column open.
        private const string DoorFramePrefabPath = PrefabFolder + "/BR_BuildingPiece_GridDoorFrame.prefab";
        private const string DoorFrameDefinitionPath = DefinitionFolder + "/BR_Door Frame.asset";

        private const string DoorFrameSourceFbxPath =
            "Assets/MeshyImports/steel-door-frame-remesh_20260818_140152/Meshy_AI_steel_door_frame_reme_0818120142_texture.fbx";
        private const string DoorFrameSourceBaseColorPath =
            "Assets/MeshyImports/steel-door-frame-remesh_20260818_140152/meshy_basecolor.png";
        private const string DoorFrameSourceMetallicPath =
            "Assets/MeshyImports/steel-door-frame-remesh_20260818_140152/meshy_metallic_smoothness.png";

        // Baked, versioned copy — Assets/MeshyImports/ is gitignored (line 60: "cientos de MB, se
        // regeneran desde la herramienta"), so a prefab pointing at the FBX directly would render
        // fine here and be invisible on every other machine. See memory meshy-imports-gitignored-bake.
        private const string DoorFrameBakedFolder = "Assets/Art/Building/DoorFrame";
        private const string DoorFrameBakedMeshPath = DoorFrameBakedFolder + "/BR_DoorFrame_Mesh.asset";
        private const string DoorFrameBakedBaseColorPath = DoorFrameBakedFolder + "/BR_DoorFrame_BaseColor.png";
        private const string DoorFrameBakedMetallicPath = DoorFrameBakedFolder + "/BR_DoorFrame_Metallic.png";
        private const string DoorFrameBakedMaterialPath = DoorFrameBakedFolder + "/BR_DoorFrame_Mat.mat";
        private const int DoorFrameBakedTextureSize = 1024;

        // Measured once (Backrooms ▸ Diagnostics ▸ Measure Door Frame) against this specific import:
        // width x height x thickness once its own node rotation (270° on X) and scale (x100, a Meshy
        // unit-conversion leftover) are applied. Re-export from Meshy and these need re-measuring.
        private const float DoorFrameNativeWidth = 1.9033f;
        private const float DoorFrameNativeHeight = 1.3553f;
        private const float DoorFrameNativeThickness = 0.1811f;

        // TODO(balance): first pass, never played. Between the wall (4) and the claim marker (6) —
        // a door disturbs the wall row it sits in more than a bare panel, less than claiming ground.
        private const int DoorFrameMetalCost = 5;

        // RECONCILED 2026-08-18: Joel widened the frame's jamb/header colliders by hand in the editor
        // after the first generation (the original symmetric ±0.45/2.5 read too tight against the
        // visual opening). These edges are read straight off the adjusted prefab on disk
        // (BR_BuildingPiece_GridDoorFrame.prefab), not re-guessed — left/right are no longer
        // mirror-symmetric because a hand-drag in the Inspector isn't. A clean re-run of this creator
        // on a fresh clone now reproduces the same opening Joel left behind, not the original one.
        private const float DoorOpeningLeftEdge = -0.752f;
        private const float DoorOpeningRightEdge = 0.633f;
        private const float DoorOpeningHeight = 2.618f;

        // Door leaf: hangs in the frame's opening above, hinged on the LEFT jamb (DoorOpeningLeftEdge)
        // to match GridDoorFrameOpening's _hingeLocalPosition. Sized to fill the opening exactly.
        private const string DoorLeafPrefabPath = PrefabFolder + "/BR_BuildingPiece_GridDoorLeaf.prefab";
        private const string DoorLeafDefinitionPath = DefinitionFolder + "/BR_Door Leaf.asset";
        private const float DoorLeafWidth = DoorOpeningRightEdge - DoorOpeningLeftEdge;
        private const float DoorLeafHeight = DoorOpeningHeight;
        private const float DoorLeafThickness = 0.05f;

        // TODO(balance): first pass, never played. Less than the frame (5) — hanging a leaf in an
        // already-built frame disturbs less than the frame itself did.
        private const int DoorLeafMetalCost = 2;

        // Reused straight from the vendor's own wood door — same sound, different frame. Not owned
        // by this project, never edited, just referenced.
        private const string DoorOpenAudioPath = "Assets/PolymindGames/STP/Audio/SFX/Interactables/STP_Door_Open.wav";
        private const string DoorCloseAudioPath = "Assets/PolymindGames/STP/Audio/SFX/Interactables/STP_Door_Close.wav";

        // Metal shelf, imported 2026-08-17. Free-standing storage piece — unlike the wall/door frame
        // it does not conform to the 5 x 4 x 0.2 grid slot, so it is a FreeBuildingPiece (same base
        // as the claim marker) rather than GridWallBuildingPiece. FARMING-ROADMAP.md bloque E.
        private const string StorageRackPrefabPath = PrefabFolder + "/BR_BuildingPiece_StorageRack.prefab";
        private const string StorageRackDefinitionPath = DefinitionFolder + "/BR_Storage Rack.asset";

        private const string StorageRackSourceFbxPath =
            "Assets/MeshyImports/metal-shelf-gamemesh_20260817_205927/" +
            "Meshy_AI_metal_shelf_gamemesh_0817185423_texture.fbx";
        private const string StorageRackSourceBaseColorPath =
            "Assets/MeshyImports/metal-shelf-gamemesh_20260817_205927/meshy_basecolor.png";
        private const string StorageRackSourceMetallicPath =
            "Assets/MeshyImports/metal-shelf-gamemesh_20260817_205927/meshy_metallic_smoothness.png";

        // Baked, versioned copy — Assets/MeshyImports/ is gitignored, see meshy-imports-gitignored-bake.
        private const string StorageRackBakedFolder = "Assets/Art/Building/StorageRack";
        private const string StorageRackBakedMeshPath = StorageRackBakedFolder + "/BR_StorageRack_Mesh.asset";
        private const string StorageRackBakedBaseColorPath = StorageRackBakedFolder + "/BR_StorageRack_BaseColor.png";
        private const string StorageRackBakedMetallicPath = StorageRackBakedFolder + "/BR_StorageRack_Metallic.png";
        private const string StorageRackBakedMaterialPath = StorageRackBakedFolder + "/BR_StorageRack_Mat.mat";
        private const int StorageRackBakedTextureSize = 1024;

        // Measured once (Backrooms ▸ Diagnostics ▸ Measure Storage Rack, 2026-08-22) against this
        // specific import: world size once the FBX's own node rotation (270° on X) and scale (x100,
        // the same Meshy unit-conversion leftover as the door frame) are applied. Unlike the door
        // frame, this piece does NOT get re-scaled to fit a fixed slot — a free-standing rack is not
        // bound to the wall grid, and the native size already reads as a real shelving unit — so
        // these doubles as both the "native" and the FINAL baked size (extra scale factor of 1).
        // Re-export from Meshy and these need re-measuring.
        private const float StorageRackWidth = 1.4527f;   // X
        private const float StorageRackHeight = 1.9026f;  // Y
        private const float StorageRackDepth = 0.6049f;   // Z (thin axis)

        // TODO(balance): first pass, never played. Between the wall (4) and the claim marker (6) —
        // FARMING-ROADMAP.md D4.
        private const int StorageRackMetalCost = 4;

        // Read off the front-view probe screenshot (Temp/claude_rack_shot.png, 2026-08-22): an open
        // tube frame, no back/side panels, four evenly spaced open tiers (not three — the top rail
        // and three shelf boards below it bound four compartments). FARMING-ROADMAP.md D3: range is
        // 12-16 with a floor of 12; picking the top of the range (4 per tier) because the rack reads
        // as OPEN wire shelving and Joel wants the "items visibly placed" effect to read as full.
        // Not yet wired to anything — StorageRackDisplay (bloque E, tarea E3) is what will actually
        // use this to size its shelf-anchor array; kept here now so E1's measurement is not re-done.
        private const int StorageRackTierCount = 4;
        private const int StorageRackSlotsPerTier = 4;
        private const int StorageRackSlots = StorageRackTierCount * StorageRackSlotsPerTier;

        [MenuItem("Backrooms/Create Building Pieces")]
        public static void CreateIfMissing()
        {
            CreateWallIfMissing();
            CreatePanelIfMissing();
            CreateClaimMarkerIfMissing();
            CreateDoorFrameIfMissing();
            CreateDoorLeafIfMissing();
            CreateStorageRackIfMissing();
        }

        /// <summary>
        /// ADR-081 pieza 3 — el marcador de territorio: la única pieza colocable en una zona segura
        /// SIN reclamar, y la que acuña el claim a nombre de quien la pone.
        ///
        /// Es un <see cref="FreeBuildingPiece"/> del vendor y no una subclase propia a propósito: no
        /// necesita encajar en la rejilla ni en sockets, y la tubería de colocación/replicación/
        /// persistencia no distingue subclases. Lo que lo hace especial NO vive en el prefab sino en
        /// el `def_id`, que el backend conoce por constante (`CLAIM_MARKER_DEF_ID`, mismo patrón que
        /// el `BED_DEF_ID` de ADR-031) — así que este asset es el que fija ese número.
        ///
        /// Mismo contrato crear-si-falta y la misma trampa que los dos de arriba: el `def_id` viaja
        /// por el wire, regenerarlo rompería todo claim ya plantado en un save. COMMITEAR AMBOS.
        /// </summary>
        private static void CreateClaimMarkerIfMissing()
        {
            var existingDefinition = AssetDatabase.LoadAssetAtPath<BuildingPieceDefinition>(MarkerDefinitionPath);
            var existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MarkerPrefabPath);
            if (existingDefinition != null && existingPrefab != null)
            {
                Debug.Log($"[BackroomsBuildingPieceCreator] '{MarkerDefinitionPath}' and '{MarkerPrefabPath}' " +
                          $"already exist (def_id={existingDefinition.Id}) — left untouched. That id is hardcoded " +
                          "in the backend as CLAIM_MARKER_DEF_ID; regenerating it would orphan every claim.");
                return;
            }

            if (existingDefinition != null || existingPrefab != null)
            {
                Debug.LogError("[BackroomsBuildingPieceCreator] Half the claim-marker pair exists " +
                               $"(definition={existingDefinition != null}, prefab={existingPrefab != null}). " +
                               "Refusing to regenerate: recreating the definition would mint a new def_id and the " +
                               "backend constant would stop matching. Restore the missing file from git.");
                return;
            }

            var metal = ResolveBuildMaterial(MetalMaterialName);
            if (metal == null)
                return;

            if (!TryResolveShared(out var category, out var placeEffects, out var constructEffects))
                return;

            EnsureFolders();

            var definition = CreateDefinition(MarkerDefinitionPath, category, placeEffects, constructEffects,
                "Reclama el terreno a tu alrededor. Solo quien lo planta puede construir dentro.");
            var prefab = CreateMarkerPrefab(definition, metal);
            AssignPrefabToDefinition(definition, prefab);

            BuildingPieceDefinition.ReloadDefinitions_EditorOnly();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[BackroomsBuildingPieceCreator] Created '{MarkerDefinitionPath}' " +
                      $"(def_id={definition.Id}) and '{MarkerPrefabPath}' — {MarkerMetalCost}× " +
                      $"{MetalMaterialName}. PON ESTE def_id EN `CLAIM_MARKER_DEF_ID` (backend/src/game_loop.rs) " +
                      "y commitea los dos assets: sin esa constante el marcador es un poste decorativo.");
        }

        private static GameObject CreateMarkerPrefab(BuildingPieceDefinition definition, BuildMaterialDefinition metal)
        {
            var root = new GameObject("BR_BuildingPiece_ClaimMarker")
            {
                layer = LayerConstants.Building
            };

            var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            post.name = "Post";
            post.layer = LayerConstants.Building;
            post.transform.SetParent(root.transform, false);
            post.transform.localPosition = new Vector3(0f, MarkerHeight * 0.5f, 0f);
            post.transform.localScale = new Vector3(MarkerSide, MarkerHeight, MarkerSide);
            Object.DestroyImmediate(post.GetComponent<Collider>());

            // En la RAÍZ, por lo mismo que la pared y el panel: los dos detectores del vendor resuelven
            // su componente desde el GameObject del collider que golpean, así que un collider en el
            // hijo deja la pieza invisible para ellos y no se le podría añadir material nunca.
            var collider = root.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, MarkerHeight * 0.5f, 0f);
            collider.size = new Vector3(MarkerSide, MarkerHeight, MarkerSide);

            var material = AssetDatabase.LoadAssetAtPath<Material>(WallMaterialPath);
            if (material != null)
                post.GetComponent<MeshRenderer>().sharedMaterial = material;
            else
                Debug.LogWarning($"[BackroomsBuildingPieceCreator] '{WallMaterialPath}' not found; the claim marker " +
                                 "keeps the default primitive material.");

            // RequireComponent trae MaterialEffect (el tinte del fantasma) con esta llamada.
            var piece = root.AddComponent<FreeBuildingPiece>();
            var constructable = root.AddComponent<Constructable>();

            ConfigurePiece(piece, definition,
                new Bounds(new Vector3(0f, MarkerHeight * 0.5f, 0f),
                    new Vector3(MarkerSide, MarkerHeight, MarkerSide)));
            ConfigureConstructable(constructable, metal, MarkerMetalCost);
            ConfigureMaterialEffect(root.GetComponent<MaterialEffect>(), post.GetComponent<MeshRenderer>());

            var saved = PrefabUtility.SaveAsPrefabAsset(root, MarkerPrefabPath);
            Object.DestroyImmediate(root);
            return saved;
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
        /// Authors the door frame: same crear-si-falta contract and def_id-on-the-wire hazard as the
        /// wall and panel — COMMIT BOTH GENERATED ASSETS (the definition AND the baked mesh/textures
        /// under <see cref="DoorFrameBakedFolder"/>, which live outside Assets/MeshyImports/ so a
        /// fresh clone actually renders them).
        /// </summary>
        private static void CreateDoorFrameIfMissing()
        {
            var existingDefinition = AssetDatabase.LoadAssetAtPath<BuildingPieceDefinition>(DoorFrameDefinitionPath);
            var existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DoorFramePrefabPath);
            if (existingDefinition != null && existingPrefab != null)
            {
                Debug.Log($"[BackroomsBuildingPieceCreator] '{DoorFrameDefinitionPath}' and '{DoorFramePrefabPath}' " +
                          "already exist — left untouched (the definition id is on the wire; regenerating it would " +
                          "break replication).");
                EnsureDoorFrameOpeningMarker(existingPrefab);
                return;
            }

            if (existingDefinition != null || existingPrefab != null)
            {
                Debug.LogError("[BackroomsBuildingPieceCreator] Half the door-frame pair exists " +
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
            BackroomsEditorFolders.EnsureFolder("Assets/Art");
            BackroomsEditorFolders.EnsureFolder("Assets/Art/Building");
            BackroomsEditorFolders.EnsureFolder(DoorFrameBakedFolder);

            var mesh = BakeDoorFrameMesh();
            if (mesh == null)
                return;

            var material = BakeDoorFrameMaterial();
            if (material == null)
                return;

            var definition = CreateDefinition(DoorFrameDefinitionPath, category, placeEffects, constructEffects,
                "A steel frame with a door-shaped opening. Snaps to the floor grid like the plain wall — " +
                "build it into a wall row to leave a walkable gap instead of a solid panel.");
            var prefab = CreateDoorFramePrefab(definition, metal, mesh, material);
            AssignPrefabToDefinition(definition, prefab);
            EnsureDoorFrameOpeningMarker(prefab);

            BuildingPieceDefinition.ReloadDefinitions_EditorOnly();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[BackroomsBuildingPieceCreator] Created '{DoorFrameDefinitionPath}' (def_id={definition.Id}) " +
                      $"and '{DoorFramePrefabPath}' — {DoorFrameMetalCost}× {MetalMaterialName}, category " +
                      $"'{category.Name}'. COMMIT BOTH plus everything under '{DoorFrameBakedFolder}': the def_id " +
                      "travels over the wire and the baked art is what makes the piece visible off this machine.");
        }

        /// <summary>
        /// Sets the FBX importer up for baking rather than direct rendering: our own URP material
        /// replaces whatever the FBX carries (imported materials come in Built-in shader and render
        /// magenta since ADR-065), and the mesh needs to be CPU-readable to copy its vertex data out.
        /// Mirrors BackroomsSprayModelSwapper.ConfigureModel.
        /// </summary>
        private static void ConfigureDoorFrameModelImport()
        {
            var importer = AssetImporter.GetAtPath(DoorFrameSourceFbxPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[BackroomsBuildingPieceCreator] '{DoorFrameSourceFbxPath}' has no ModelImporter.");
                return;
            }

            bool dirty = false;
            if (importer.materialImportMode != ModelImporterMaterialImportMode.None)
            {
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
                dirty = true;
            }
            if (importer.importAnimation) { importer.importAnimation = false; dirty = true; }
            if (importer.importCameras) { importer.importCameras = false; dirty = true; }
            if (importer.importLights) { importer.importLights = false; dirty = true; }
            if (!importer.isReadable) { importer.isReadable = true; dirty = true; }
            // Off, not the default: mesh compression quantizes positions RELATIVE TO THE IMPORT
            // BOX, and this bake rewrites the vertices afterward into a different box entirely
            // (same trap documented in BackroomsSprayModelSwapper.ConfigureModel).
            if (importer.meshCompression != ModelImporterMeshCompression.Off)
            {
                importer.meshCompression = ModelImporterMeshCompression.Off;
                dirty = true;
            }
            if (!importer.optimizeMeshPolygons) { importer.optimizeMeshPolygons = true; dirty = true; }
            if (!importer.optimizeMeshVertices) { importer.optimizeMeshVertices = true; dirty = true; }

            if (!dirty) return;
            importer.SaveAndReimport();
        }

        /// <summary>
        /// Bakes the door frame FBX into a versioned <c>.asset</c> mesh, already at its FINAL world
        /// size: the FBX's own node rotation/scale (Meshy's unit + axis correction, measured as
        /// <see cref="DoorFrameNativeWidth"/> etc.) is baked in alongside the extra scale that
        /// stretches it onto the wall's 5 x 4 slot, so the finished piece needs no scale anywhere in
        /// its hierarchy — which matters because <c>MaterialEffect</c>'s ghost tint is object-space
        /// and a scaled root would distort it (the almond water bottle's oversized "highlight" was
        /// exactly this bug).
        ///
        /// Width and height share one pair of factors (the door column and its opening scale up
        /// together with the whole frame); thickness gets its OWN factor rather than following
        /// width's the way the drywall sheet does — following width would balloon a ~0.18 m frame to
        /// ~0.48 m instead of landing near the wall's fixed 0.2 m.
        /// </summary>
        private static Mesh BakeDoorFrameMesh()
        {
            var sourceAsset = AssetDatabase.LoadAssetAtPath<GameObject>(DoorFrameSourceFbxPath);
            if (sourceAsset == null)
            {
                Debug.LogError($"[BackroomsBuildingPieceCreator] Door frame FBX missing at " +
                               $"'{DoorFrameSourceFbxPath}'. Nothing created.");
                return null;
            }

            ConfigureDoorFrameModelImport();

            var extraScale = new Vector3(
                Length / DoorFrameNativeWidth,
                Height / DoorFrameNativeHeight,
                Thickness / DoorFrameNativeThickness);

            var temp = (GameObject)Object.Instantiate(sourceAsset);
            Mesh baked;
            try
            {
                var filter = temp.GetComponentInChildren<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                {
                    Debug.LogError($"[BackroomsBuildingPieceCreator] '{DoorFrameSourceFbxPath}' has no readable " +
                                   "MeshFilter. Nothing created.");
                    return null;
                }

                // The node's own authored rotation/scale (identity parent, so world == local here).
                var rotation = filter.transform.rotation;
                var unitScale = filter.transform.lossyScale;

                baked = Object.Instantiate(filter.sharedMesh);
                baked.name = "BR_DoorFrame_Mesh";

                var vertices = baked.vertices;
                for (int i = 0; i < vertices.Length; i++)
                    vertices[i] = Vector3.Scale(rotation * Vector3.Scale(vertices[i], unitScale), extraScale);
                baked.vertices = vertices;

                // unitScale is authored UNIFORM (Meshy's flat x100), so it drops out of a normal's
                // direction entirely (only ever rescales length, which RecalculateNormals-adjacent
                // renormalizing below undoes) — only extraScale, being non-uniform, needs the
                // inverse-transpose treatment here.
                var normals = baked.normals;
                if (normals != null && normals.Length == vertices.Length)
                {
                    var invExtra = new Vector3(1f / extraScale.x, 1f / extraScale.y, 1f / extraScale.z);
                    for (int i = 0; i < normals.Length; i++)
                        normals[i] = Vector3.Scale(rotation * normals[i], invExtra).normalized;
                    baked.normals = normals;
                }

                var tangents = baked.tangents;
                if (tangents != null && tangents.Length == vertices.Length)
                {
                    for (int i = 0; i < tangents.Length; i++)
                    {
                        var t = tangents[i];
                        var xyz = Vector3.Scale(rotation * Vector3.Scale(new Vector3(t.x, t.y, t.z), unitScale),
                            extraScale).normalized;
                        tangents[i] = new Vector4(xyz.x, xyz.y, xyz.z, t.w);
                    }
                    baked.tangents = tangents;
                }

                baked.RecalculateBounds();
            }
            finally
            {
                Object.DestroyImmediate(temp);
            }

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(DoorFrameBakedMeshPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(baked, DoorFrameBakedMeshPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"[BackroomsBuildingPieceCreator] Door frame mesh baked to '{DoorFrameBakedMeshPath}' " +
                          $"(bounds size {baked.bounds.size}).");
                return baked;
            }

            // Overwritten in place, not deleted and recreated: a fresh GUID would break every
            // reference into this asset on the next run (same rule as the spray can bake).
            EditorUtility.CopySerialized(baked, existing);
            Object.DestroyImmediate(baked);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(DoorFrameBakedMeshPath, ImportAssetOptions.ForceUpdate);
            var reloaded = AssetDatabase.LoadAssetAtPath<Mesh>(DoorFrameBakedMeshPath);
            Debug.Log($"[BackroomsBuildingPieceCreator] Door frame mesh re-baked at '{DoorFrameBakedMeshPath}' " +
                      $"(same GUID), bounds size {reloaded.bounds.size}.");
            return reloaded;
        }

        /// <summary>
        /// Bakes one source texture (read UNCOMPRESSED and linear, which is how Meshy's own import
        /// settings must be temporarily forced — reading pixels off an already-compressed texture
        /// shuffles the channels) down to <see cref="DoorFrameBakedTextureSize"/> px, then restores
        /// the source import settings. Mirrors BackroomsSprayModelSwapper.BakeTexture.
        /// </summary>
        private static void BakeDoorFrameTexture(string sourcePath, string bakedPath, bool sRgb)
        {
            var importer = AssetImporter.GetAtPath(sourcePath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[BackroomsBuildingPieceCreator] No texture at '{sourcePath}' — door frame " +
                                 "baked without it.");
                return;
            }

            var prevType = importer.textureType;
            var prevCompression = importer.textureCompression;
            bool prevReadable = importer.isReadable;
            bool prevSrgb = importer.sRGBTexture;
            int prevMax = importer.maxTextureSize;

            try
            {
                importer.textureType = TextureImporterType.Default;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.isReadable = true;
                importer.sRGBTexture = sRgb;
                importer.maxTextureSize = DoorFrameBakedTextureSize;
                importer.SaveAndReimport();

                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(sourcePath);
                if (tex == null)
                {
                    Debug.LogWarning($"[BackroomsBuildingPieceCreator] '{sourcePath}' did not load as Texture2D.");
                    return;
                }

                File.WriteAllBytes(bakedPath, tex.EncodeToPNG());
                AssetDatabase.ImportAsset(bakedPath, ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                importer.textureType = prevType;
                importer.textureCompression = prevCompression;
                importer.isReadable = prevReadable;
                importer.sRGBTexture = prevSrgb;
                importer.maxTextureSize = prevMax;
                importer.SaveAndReimport();
            }

            var baked = AssetImporter.GetAtPath(bakedPath) as TextureImporter;
            if (baked == null) return;

            baked.textureType = TextureImporterType.Default;
            baked.sRGBTexture = sRgb;
            baked.maxTextureSize = DoorFrameBakedTextureSize;
            baked.textureCompression = TextureImporterCompression.Compressed;
            baked.mipmapEnabled = true;
            baked.SaveAndReimport();
        }

        /// <summary>
        /// Bakes both source textures and builds the URP Lit material. No normal map this import
        /// (unlike the spray can's source) — Meshy simply did not produce one, so the frame reads
        /// slightly flatter than it could; not a bug, just what shipped.
        /// </summary>
        private static Material BakeDoorFrameMaterial()
        {
            BackroomsEditorFolders.EnsureFolder(DoorFrameBakedFolder);
            BakeDoorFrameTexture(DoorFrameSourceBaseColorPath, DoorFrameBakedBaseColorPath, sRgb: true);
            BakeDoorFrameTexture(DoorFrameSourceMetallicPath, DoorFrameBakedMetallicPath, sRgb: false);

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[BackroomsBuildingPieceCreator] No 'Universal Render Pipeline/Lit' shader. " +
                               "Nothing created.");
                return null;
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(DoorFrameBakedMaterialPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, DoorFrameBakedMaterialPath);
            }
            mat.shader = shader;

            var baseColor = AssetDatabase.LoadAssetAtPath<Texture2D>(DoorFrameBakedBaseColorPath);
            var metallic = AssetDatabase.LoadAssetAtPath<Texture2D>(DoorFrameBakedMetallicPath);

            if (baseColor != null) mat.SetTexture("_BaseMap", baseColor);
            if (metallic != null)
            {
                mat.SetTexture("_MetallicGlossMap", metallic);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                mat.SetFloat("_Metallic", 1f);
                mat.SetFloat("_Smoothness", 1f);
                mat.SetFloat("_SmoothnessTextureChannel", 0f); // alpha of the metallic map
            }

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }

        /// <summary>
        /// Two jambs plus a header, all on the ROOT (same load-bearing reason as every other piece
        /// in this file: both vendor detectors resolve their component from the GameObject of the
        /// collider they hit). Left as three separate boxes rather than one box with a hole because
        /// Unity colliders cannot express a hole — this is the standard decomposition for a solid
        /// frame around an opening.
        /// </summary>
        private static void AddDoorFrameColliders(GameObject root)
        {
            float halfLength = Length * 0.5f;

            float leftWidth = DoorOpeningLeftEdge - (-halfLength);
            var left = root.AddComponent<BoxCollider>();
            left.center = new Vector3(-halfLength + leftWidth * 0.5f, Height * 0.5f, 0f);
            left.size = new Vector3(leftWidth, Height, Thickness);

            float rightWidth = halfLength - DoorOpeningRightEdge;
            var right = root.AddComponent<BoxCollider>();
            right.center = new Vector3(DoorOpeningRightEdge + rightWidth * 0.5f, Height * 0.5f, 0f);
            right.size = new Vector3(rightWidth, Height, Thickness);

            float headerHeight = Height - DoorOpeningHeight;
            float headerWidth = DoorOpeningRightEdge - DoorOpeningLeftEdge;
            var header = root.AddComponent<BoxCollider>();
            header.center = new Vector3((DoorOpeningLeftEdge + DoorOpeningRightEdge) * 0.5f,
                DoorOpeningHeight + headerHeight * 0.5f, 0f);
            header.size = new Vector3(headerWidth, headerHeight, Thickness);
        }

        private static GameObject CreateDoorFramePrefab(BuildingPieceDefinition definition,
            BuildMaterialDefinition metal, Mesh mesh, Material material)
        {
            var root = new GameObject("BR_BuildingPiece_GridDoorFrame")
            {
                layer = LayerConstants.Building
            };

            // RENDER ONLY, bare MeshFilter/MeshRenderer — same split as every piece here, the
            // collider belongs on the root, never on this child.
            var model = new GameObject("Model") { layer = LayerConstants.Building };
            model.transform.SetParent(root.transform, false);
            model.transform.localPosition = new Vector3(0f, Height * 0.5f, 0f);
            var filter = model.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = model.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;

            AddDoorFrameColliders(root);

            // RequireComponent pulls in MaterialEffect (the ghost tint) with this call.
            var piece = root.AddComponent<GridWallBuildingPiece>();
            var constructable = root.AddComponent<Constructable>();

            ConfigurePiece(piece, definition,
                new Bounds(new Vector3(0f, Height * 0.5f, 0f), new Vector3(Length, Height, Thickness)));
            ConfigureConstructable(constructable, metal, DoorFrameMetalCost);
            ConfigureMaterialEffect(root.GetComponent<MaterialEffect>(), renderer);

            var saved = PrefabUtility.SaveAsPrefabAsset(root, DoorFramePrefabPath);
            Object.DestroyImmediate(root);
            return saved;
        }

        /// <summary>
        /// Idempotent patch: adds <see cref="GridDoorFrameOpening"/> to the frame prefab if it is not
        /// already there, and nothing else. The frame's definition id must never be regenerated once
        /// minted, so this is how the marker reaches a frame prefab authored before the marker
        /// existed (or repairs one where it was somehow lost) without touching the def_id at all.
        /// </summary>
        private static void EnsureDoorFrameOpeningMarker(GameObject prefabAsset)
        {
            string path = AssetDatabase.GetAssetPath(prefabAsset);
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                // Defensive: this piece's authored pivot is the floor origin like every other piece
                // in this file (GridWallSnap overwrites position/rotation on every placement anyway,
                // so nothing depends on whatever the asset's root happens to say) — reset it here so
                // a stray non-origin root never lingers into a re-save.
                contents.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                // Not add-once: the hinge/leaf size are RE-APPLIED every run, even when the marker
                // already exists, so a reconciliation of DoorOpeningLeftEdge/RightEdge/Height (like
                // 2026-08-18's, after Joel hand-widened the frame's colliders) actually reaches an
                // already-existing frame instead of silently going stale.
                var opening = contents.GetComponent<GridDoorFrameOpening>();
                bool wasMissing = opening == null;
                if (wasMissing)
                    opening = contents.AddComponent<GridDoorFrameOpening>();

                var serialized = new SerializedObject(opening);
                serialized.FindProperty("_hingeLocalPosition").vector3Value =
                    new Vector3(DoorOpeningLeftEdge, 0f, 0f);
                serialized.FindProperty("_leafSize").vector3Value =
                    new Vector3(DoorLeafWidth, DoorLeafHeight, DoorLeafThickness);
                serialized.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(contents, path);
                Debug.Log($"[BackroomsBuildingPieceCreator] " +
                          $"{(wasMissing ? "Added" : "Synced")} GridDoorFrameOpening on '{path}' — hinge=" +
                          $"({DoorOpeningLeftEdge}, 0, 0), leafSize=({DoorLeafWidth}, {DoorLeafHeight}, " +
                          $"{DoorLeafThickness}).");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// Idempotent sync: resizes the leaf's Model/collider/bounds to the CURRENT DoorLeafWidth/
        /// Height/Thickness, without touching its def_id. The leaf's whole footprint is derived from
        /// the frame's opening (<see cref="DoorOpeningLeftEdge"/> etc.) — if that changes (like
        /// 2026-08-18's reconciliation against Joel's hand-widened colliders), the leaf has to follow
        /// or it stops filling the gap it hangs in.
        /// </summary>
        private static void EnsureDoorLeafGeometry(GameObject prefabAsset)
        {
            string path = AssetDatabase.GetAssetPath(prefabAsset);
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var model = contents.transform.Find("Model");
                var collider = contents.GetComponent<BoxCollider>();
                var piece = contents.GetComponent<GridDoorLeafBuildingPiece>();
                if (model == null || collider == null || piece == null)
                {
                    Debug.LogError($"[BackroomsBuildingPieceCreator] '{path}' is missing an expected " +
                                   $"child/component (Model={model != null}, collider={collider != null}, " +
                                   $"piece={piece != null}) — cannot sync geometry. Nothing changed.");
                    return;
                }

                var centre = new Vector3(DoorLeafWidth * 0.5f, DoorLeafHeight * 0.5f, 0f);
                var size = new Vector3(DoorLeafWidth, DoorLeafHeight, DoorLeafThickness);

                model.localPosition = centre;
                model.localScale = size;
                collider.center = centre;
                collider.size = size;
                ConfigurePiece(piece, piece.Definition, new Bounds(centre, size));

                PrefabUtility.SaveAsPrefabAsset(contents, path);
                Debug.Log($"[BackroomsBuildingPieceCreator] Synced '{path}' geometry to size={size}.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// Authors the door leaf: same crear-si-falta contract as every other piece here. Reuses the
        /// door frame's own baked material (a plain metal plank, per the user's call — no separate
        /// leaf mesh was imported) and the vendor's own <c>Door</c> component unmodified for the
        /// actual swing/open/close/damage behaviour, wired the same way its own inspector would.
        /// </summary>
        private static void CreateDoorLeafIfMissing()
        {
            var existingDefinition = AssetDatabase.LoadAssetAtPath<BuildingPieceDefinition>(DoorLeafDefinitionPath);
            var existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DoorLeafPrefabPath);
            if (existingDefinition != null && existingPrefab != null)
            {
                Debug.Log($"[BackroomsBuildingPieceCreator] '{DoorLeafDefinitionPath}' and '{DoorLeafPrefabPath}' " +
                          "already exist (def_id untouched) — syncing geometry to the current opening size.");
                EnsureDoorLeafGeometry(existingPrefab);
                return;
            }

            if (existingDefinition != null || existingPrefab != null)
            {
                Debug.LogError("[BackroomsBuildingPieceCreator] Half the door-leaf pair exists " +
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

            var material = AssetDatabase.LoadAssetAtPath<Material>(DoorFrameBakedMaterialPath);
            if (material == null)
            {
                Debug.LogError($"[BackroomsBuildingPieceCreator] No baked material at '{DoorFrameBakedMaterialPath}'. " +
                               "Run \"Backrooms ▸ Create Building Pieces\" once fully so the door frame bakes its " +
                               "own material first. Nothing created.");
                return;
            }

            var definition = CreateDefinition(DoorLeafDefinitionPath, category, placeEffects, constructEffects,
                "A hinged door leaf. Aim near a built door frame's opening to hang it — opens on interact, " +
                "or with enough of a hit.");
            var prefab = CreateDoorLeafPrefab(definition, metal, material);
            AssignPrefabToDefinition(definition, prefab);

            BuildingPieceDefinition.ReloadDefinitions_EditorOnly();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[BackroomsBuildingPieceCreator] Created '{DoorLeafDefinitionPath}' (def_id={definition.Id}) " +
                      $"and '{DoorLeafPrefabPath}' — {DoorLeafMetalCost}× {MetalMaterialName}, category " +
                      $"'{category.Name}'. COMMIT BOTH: the def_id travels over the wire.");
        }

        private static GameObject CreateDoorLeafPrefab(BuildingPieceDefinition definition,
            BuildMaterialDefinition metal, Material material)
        {
            // Root pivot IS the hinge, unlike every other piece in this file (bottom-centre or
            // cell-centre): the vendor's Door swings by rotating transform.localRotation on whatever
            // GameObject it lives on, so the root has to BE the swing axis. The visual leaf and the
            // collider both sit offset sideways from it instead of centred on it.
            var root = new GameObject("BR_BuildingPiece_GridDoorLeaf")
            {
                layer = LayerConstants.Building
            };

            var centre = new Vector3(DoorLeafWidth * 0.5f, DoorLeafHeight * 0.5f, 0f);
            var size = new Vector3(DoorLeafWidth, DoorLeafHeight, DoorLeafThickness);

            var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            panel.name = "Model";
            panel.layer = LayerConstants.Building;
            panel.transform.SetParent(root.transform, false);
            panel.transform.localPosition = centre;
            panel.transform.localScale = size;
            Object.DestroyImmediate(panel.GetComponent<Collider>());
            panel.GetComponent<MeshRenderer>().sharedMaterial = material;

            // On the ROOT — the usual load-bearing reason (vendor build detectors resolve their
            // component off the collider's own GameObject) AND what Door.cs itself demands
            // ([RequireComponent(typeof(BoxCollider), typeof(IHoverableInteractable))]): both
            // constraints point at the same GameObject, so there is nothing to reconcile.
            var collider = root.AddComponent<BoxCollider>();
            collider.center = centre;
            collider.size = size;

            // RequireComponent pulls in MaterialEffect (the ghost tint) with this call.
            var piece = root.AddComponent<GridDoorLeafBuildingPiece>();
            var constructable = root.AddComponent<Constructable>();

            ConfigurePiece(piece, definition, new Bounds(centre, size));
            ConfigureConstructable(constructable, metal, DoorLeafMetalCost);
            ConfigureMaterialEffect(root.GetComponent<MaterialEffect>(), panel.GetComponent<MeshRenderer>());

            // Vendor components, unmodified — Door.Awake() requires IHoverableInteractable to already
            // be present, so Interactable is added first.
            root.AddComponent<Interactable>();
            var door = root.AddComponent<Door>();
            ConfigureDoor(door);

            var saved = PrefabUtility.SaveAsPrefabAsset(root, DoorLeafPrefabPath);
            Object.DestroyImmediate(root);
            return saved;
        }

        /// <summary>
        /// Sets the vendor Door's private serialized fields the same way its own inspector would: a
        /// 90° swing (vendor default) and its two audio clips reused straight from the vendor's own
        /// wood door — same sound, different frame.
        /// </summary>
        private static void ConfigureDoor(Door door)
        {
            var serialized = new SerializedObject(door);
            serialized.FindProperty("_openRotation").vector3Value = new Vector3(0f, 90f, 0f);
            serialized.FindProperty("_damageRequiredToOpen").floatValue = 30f;
            serialized.FindProperty("_openTitle").stringValue = "Open";
            serialized.FindProperty("_closeTitle").stringValue = "Close";

            SetDoorAudioClip(serialized, "_openAudio", DoorOpenAudioPath);
            SetDoorAudioClip(serialized, "_closeAudio", DoorCloseAudioPath);

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetDoorAudioClip(SerializedObject serialized, string fieldName, string clipPath)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath);
            if (clip == null)
            {
                Debug.LogWarning($"[BackroomsBuildingPieceCreator] No audio clip at '{clipPath}' — door leaf " +
                                 "built without it.");
                return;
            }

            var field = serialized.FindProperty(fieldName);
            field.FindPropertyRelative("Clip").objectReferenceValue = clip;
            field.FindPropertyRelative("Volume").floatValue = 1f;
        }

        /// <summary>
        /// Authors the storage rack: same crear-si-falta contract and def_id-on-the-wire hazard as
        /// every other piece here — COMMIT BOTH GENERATED ASSETS (the definition AND the baked
        /// mesh/textures under <see cref="StorageRackBakedFolder"/>, which live outside
        /// Assets/MeshyImports/ so a fresh clone actually renders them). FreeBuildingPiece, not
        /// GridWallBuildingPiece — this piece stands on its own, it is not a wall-slot alternate.
        /// StorageStation (the actual container) is FARMING-ROADMAP.md tarea E2, added on top of
        /// this prefab later — this menu only gets the piece placeable and costed.
        /// </summary>
        private static void CreateStorageRackIfMissing()
        {
            var existingDefinition = AssetDatabase.LoadAssetAtPath<BuildingPieceDefinition>(StorageRackDefinitionPath);
            var existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(StorageRackPrefabPath);
            if (existingDefinition != null && existingPrefab != null)
            {
                Debug.Log($"[BackroomsBuildingPieceCreator] '{StorageRackDefinitionPath}' and " +
                          $"'{StorageRackPrefabPath}' already exist — left untouched (the definition id is on " +
                          "the wire; regenerating it would break replication).");
                EnsureStorageRackContainer(existingPrefab);
                EnsureStorageRackDisplay(existingPrefab);
                return;
            }

            if (existingDefinition != null || existingPrefab != null)
            {
                Debug.LogError("[BackroomsBuildingPieceCreator] Half the storage-rack pair exists " +
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
            BackroomsEditorFolders.EnsureFolder("Assets/Art");
            BackroomsEditorFolders.EnsureFolder("Assets/Art/Building");
            BackroomsEditorFolders.EnsureFolder(StorageRackBakedFolder);

            var mesh = BakeStorageRackMesh();
            if (mesh == null)
                return;

            var material = BakeStorageRackMaterial();
            if (material == null)
                return;

            var definition = CreateDefinition(StorageRackDefinitionPath, category, placeEffects, constructEffects,
                "A steel shelving unit, four open tiers. Free-standing — does not snap to the wall grid.");
            var prefab = CreateStorageRackPrefab(definition, metal, mesh, material);
            AssignPrefabToDefinition(definition, prefab);
            EnsureStorageRackContainer(prefab);
            EnsureStorageRackDisplay(prefab);

            BuildingPieceDefinition.ReloadDefinitions_EditorOnly();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[BackroomsBuildingPieceCreator] Created '{StorageRackDefinitionPath}' " +
                      $"(def_id={definition.Id}) and '{StorageRackPrefabPath}' — {StorageRackMetalCost}× " +
                      $"{MetalMaterialName}, category '{category.Name}'. COMMIT BOTH plus everything under " +
                      $"'{StorageRackBakedFolder}'.");
        }

        /// <summary>
        /// Idempotent patch: adds <see cref="Interactable"/> and <see cref="StorageStation"/> to the
        /// rack prefab if not already there, and re-syncs <c>_defaultContainer</c>'s slot count /
        /// stacking / name on every run — same contract as
        /// <see cref="EnsureDoorFrameOpeningMarker"/>. Never touches the definition's def_id.
        ///
        /// Explicit AddComponent for both, not relying on <c>Workstation.Reset()</c> /
        /// <c>Interactable.Reset()</c> auto-wiring (both exist, both are editor-only convenience
        /// hooks tied to the Inspector "Add Component" flow) — a batch MenuItem context is not that
        /// flow, and the codebase's own pattern here is to verify by symptom, not assume an editor
        /// callback fired. <c>Interactable</c> is added FIRST so <c>Workstation.Reset()</c>'s own
        /// guard (<c>HasComponent&lt;IInteractable&gt;</c>) sees it already there and no-ops.
        ///
        /// No stacking (<c>AllowStacking = false</c>): reinforces FARMING-ROADMAP.md D1 (items
        /// themselves are already `_stackSize = 1`) specifically for this container, so a future
        /// stackable item never silently combines two rack slots into one. <c>MaxSlotCount</c> is
        /// wired straight to <see cref="StorageRackSlots"/> — the number measured in E1 IS the
        /// number of shelf anchors E3 will author, so the container can never hold more items than
        /// there are shelf positions to show them in. Starts EMPTY (no PredefinedItems/LootTable):
        /// this is a player-built piece, not a world-spawned chest.
        ///
        /// Root layer is re-asserted to <see cref="LayerConstants.Building"/> after both
        /// AddComponent calls: <c>Interactable.Reset()</c> sets the GameObject to
        /// <c>LayerConstants.Interactable</c> by default, but the vendor's own
        /// STP_BuildingPIece_StorageCrate ships with its root on Building (verified by reading that
        /// prefab directly) — safe because <c>LayerConstants.InteractableMask</c> already includes
        /// Building, and Building is what the two vendor build-detectors' raycasts expect on every
        /// other piece in this file.
        /// </summary>
        private static void EnsureStorageRackContainer(GameObject prefabAsset)
        {
            string path = AssetDatabase.GetAssetPath(prefabAsset);
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var interactable = contents.GetComponent<Interactable>();
                bool interactableWasMissing = interactable == null;
                if (interactableWasMissing)
                    interactable = contents.AddComponent<Interactable>();

                var interactableSerialized = new SerializedObject(interactable);
                interactableSerialized.FindProperty("_interactTitle").stringValue = "Storage Rack";
                interactableSerialized.FindProperty("_interactDescription").stringValue =
                    "A steel shelving unit. Hold to open.";
                var materialEffectProp = interactableSerialized.FindProperty("_materialEffect");
                if (materialEffectProp.objectReferenceValue == null)
                    materialEffectProp.objectReferenceValue = contents.GetComponent<MaterialEffect>();
                interactableSerialized.ApplyModifiedPropertiesWithoutUndo();

                var station = contents.GetComponent<StorageStation>();
                bool stationWasMissing = station == null;
                if (stationWasMissing)
                    station = contents.AddComponent<StorageStation>();

                var stationSerialized = new SerializedObject(station);
                var container = stationSerialized.FindProperty("_defaultContainer");
                container.FindPropertyRelative("Name").stringValue = "Storage Rack";
                container.FindPropertyRelative("AllowStacking").boolValue = false;
                container.FindPropertyRelative("MaxSlotCount").intValue = StorageRackSlots;
                stationSerialized.ApplyModifiedPropertiesWithoutUndo();

                contents.layer = LayerConstants.Building;

                PrefabUtility.SaveAsPrefabAsset(contents, path);
                Debug.Log($"[BackroomsBuildingPieceCreator] " +
                          $"{(interactableWasMissing ? "Added" : "Synced")} Interactable and " +
                          $"{(stationWasMissing ? "added" : "synced")} StorageStation on '{path}' — " +
                          $"{StorageRackSlots} slots, no stacking, starts empty.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// Idempotent patch, tarea E3: adds <see cref="StorageRackDisplay"/> (needs no configuration
        /// — its shelf positions are computed from constants, see that class' doc) if not already
        /// there. Separate from <see cref="EnsureStorageRackContainer"/> because this one has
        /// nothing to re-sync on a second run; a plain existence check is enough.
        /// </summary>
        // Mirrors StorageRackDisplay.ShelfClearance — same estimate, same reason (most pickup
        // meshes are not authored with their pivot at their own base).
        private const float StorageRackShelfClearance = 0.06f;

        // Seeded at IDENTITY on purpose: rotation is now per-ITEM, not per-slot (see
        // StorageRackDisplay.UprightCorrectionFor's doc — a slot has no fixed item type, so a fixed
        // per-slot rotation can't be right for everything that might land in it). Identity on an
        // anchor means "Joel hasn't overridden this slot" and lets the runtime per-item logic decide;
        // any OTHER rotation means he deliberately set one for that specific slot, and StorageRackDisplay
        // trusts it as-is.
        private static readonly Quaternion StorageRackAnchorSeedRotation = Quaternion.identity;

        // One-time migration (2026-08-22): the FIRST version of this seeded every anchor at this
        // blanket rotation, tuned for the bottle — which stood the bottle up correctly but tipped the
        // already-upright spray can onto its side the moment either one occupied a slot. Caught in
        // Joel's own playtest screenshot. Revert any anchor still exactly at that value back to
        // identity so the per-item logic in StorageRackDisplay takes back over. Never touches a
        // rotation Joel set to anything else on purpose.
        private static readonly Quaternion StorageRackAnchorMigrateFromRotation = Quaternion.Euler(-90f, 0f, 0f);

        /// <summary>
        /// Idempotent patch, tarea E3 (revisada tras el playtest de Joel): adds
        /// <see cref="StorageRackDisplay"/> if missing, and seeds ONE anchor child Transform per
        /// slot the FIRST time — <c>ShelfAnchor_00</c>.. at the same position the component's own
        /// formula fallback would compute, so seeding changes nothing visually. Never re-seeds an
        /// anchor that already exists: <c>_shelfAnchors.arraySize &gt;= StorageRackSlots</c> means
        /// every slot already has one, and a second run of this menu must not clobber whatever Joel
        /// dragged those to by hand — same "never regenerate what a human already touched" contract
        /// as the door frame's hinge/leaf reconciliation, just for a Transform instead of a float.
        /// </summary>
        private static void EnsureStorageRackDisplay(GameObject prefabAsset)
        {
            string path = AssetDatabase.GetAssetPath(prefabAsset);
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var display = contents.GetComponent<StorageRackDisplay>();
                bool displayWasMissing = display == null;
                if (displayWasMissing)
                    display = contents.AddComponent<StorageRackDisplay>();

                var serialized = new SerializedObject(display);
                var anchors = serialized.FindProperty("_shelfAnchors");

                int previousSize = anchors.arraySize;
                bool needsSeed = previousSize < StorageRackSlots;
                if (needsSeed)
                {
                    anchors.arraySize = StorageRackSlots;
                    for (int i = previousSize; i < StorageRackSlots; i++)
                    {
                        var anchorGo = new GameObject($"ShelfAnchor_{i:00}") { layer = LayerConstants.Building };
                        anchorGo.transform.SetParent(contents.transform, false);
                        anchorGo.transform.localPosition = StorageRackAnchorLocalPosition(i);
                        anchorGo.transform.localRotation = StorageRackAnchorSeedRotation;
                        anchors.GetArrayElementAtIndex(i).objectReferenceValue = anchorGo.transform;
                    }
                }

                int migrated = 0;
                for (int i = 0; i < previousSize; i++)
                {
                    var anchorProp = anchors.GetArrayElementAtIndex(i).objectReferenceValue as Transform;
                    if (anchorProp != null && anchorProp.localRotation == StorageRackAnchorMigrateFromRotation)
                    {
                        anchorProp.localRotation = Quaternion.identity;
                        migrated++;
                    }
                }

                serialized.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(contents, path);
                Debug.Log($"[BackroomsBuildingPieceCreator] " +
                          $"{(displayWasMissing ? "Added" : "Synced")} StorageRackDisplay on '{path}'" +
                          (needsSeed
                              ? $" — seeded {StorageRackSlots - previousSize} anchor(s), {previousSize} pre-existing."
                              : " — every slot already has an anchor.") +
                          (migrated > 0
                              ? $" Migrated {migrated} anchor(s) off the old blanket rotation back to identity."
                              : "") +
                          " Drag ShelfAnchor_NN children in the Inspector to nudge position/rotation.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // Same formula as StorageRackDisplay.AnchorLocalPosition — this is the SEED value, that one
        // is the runtime FALLBACK for an anchor this menu hasn't gotten to yet. Kept in sync by
        // comment, not by a shared reference: different assemblies (see that class' own doc).
        private static Vector3 StorageRackAnchorLocalPosition(int index)
        {
            int tier = index / StorageRackSlotsPerTier;
            int column = index % StorageRackSlotsPerTier;
            float y = tier * (StorageRackHeight / StorageRackTierCount) + StorageRackShelfClearance;
            float x = StorageRackWidth * ((column + 0.5f) / StorageRackSlotsPerTier - 0.5f);
            return new Vector3(x, y, 0f);
        }

        private static void ConfigureStorageRackModelImport()
        {
            var importer = AssetImporter.GetAtPath(StorageRackSourceFbxPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[BackroomsBuildingPieceCreator] '{StorageRackSourceFbxPath}' has no ModelImporter.");
                return;
            }

            bool dirty = false;
            if (importer.materialImportMode != ModelImporterMaterialImportMode.None)
            {
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
                dirty = true;
            }
            if (importer.importAnimation) { importer.importAnimation = false; dirty = true; }
            if (importer.importCameras) { importer.importCameras = false; dirty = true; }
            if (importer.importLights) { importer.importLights = false; dirty = true; }
            if (!importer.isReadable) { importer.isReadable = true; dirty = true; }
            // Off, not the default: mesh compression quantizes positions RELATIVE TO THE IMPORT BOX,
            // and this bake rewrites the vertices afterward — same trap as the door frame's bake.
            if (importer.meshCompression != ModelImporterMeshCompression.Off)
            {
                importer.meshCompression = ModelImporterMeshCompression.Off;
                dirty = true;
            }
            if (!importer.optimizeMeshPolygons) { importer.optimizeMeshPolygons = true; dirty = true; }
            if (!importer.optimizeMeshVertices) { importer.optimizeMeshVertices = true; dirty = true; }

            if (!dirty) return;
            importer.SaveAndReimport();
        }

        /// <summary>
        /// Bakes the rack FBX into a versioned <c>.asset</c> mesh at its FINAL world size. Unlike
        /// <see cref="BakeDoorFrameMesh"/> the extra scale factor here is 1 on every axis — see the
        /// comment on <see cref="StorageRackWidth"/>: this piece keeps the size Meshy exported once
        /// the node's own rotation/scale correction is applied, it is not stretched onto a grid slot.
        /// The mesh is baked CENTRED on its own origin (matching how the raw import measured); the
        /// prefab builder below lifts it onto the floor with a child transform offset, same trick as
        /// the door frame's "Model" child.
        /// </summary>
        private static Mesh BakeStorageRackMesh()
        {
            var sourceAsset = AssetDatabase.LoadAssetAtPath<GameObject>(StorageRackSourceFbxPath);
            if (sourceAsset == null)
            {
                Debug.LogError($"[BackroomsBuildingPieceCreator] Storage rack FBX missing at " +
                               $"'{StorageRackSourceFbxPath}'. Nothing created.");
                return null;
            }

            ConfigureStorageRackModelImport();

            var temp = (GameObject)Object.Instantiate(sourceAsset);
            Mesh baked;
            try
            {
                var filter = temp.GetComponentInChildren<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                {
                    Debug.LogError($"[BackroomsBuildingPieceCreator] '{StorageRackSourceFbxPath}' has no readable " +
                                   "MeshFilter. Nothing created.");
                    return null;
                }

                // The node's own authored rotation/scale (identity parent, so world == local here).
                var rotation = filter.transform.rotation;
                var unitScale = filter.transform.lossyScale;

                baked = Object.Instantiate(filter.sharedMesh);
                baked.name = "BR_StorageRack_Mesh";

                var vertices = baked.vertices;
                for (int i = 0; i < vertices.Length; i++)
                    vertices[i] = rotation * Vector3.Scale(vertices[i], unitScale);
                baked.vertices = vertices;

                var normals = baked.normals;
                if (normals != null && normals.Length == vertices.Length)
                {
                    for (int i = 0; i < normals.Length; i++)
                        normals[i] = (rotation * normals[i]).normalized;
                    baked.normals = normals;
                }

                var tangents = baked.tangents;
                if (tangents != null && tangents.Length == vertices.Length)
                {
                    for (int i = 0; i < tangents.Length; i++)
                    {
                        var t = tangents[i];
                        var xyz = (rotation * Vector3.Scale(new Vector3(t.x, t.y, t.z), unitScale)).normalized;
                        tangents[i] = new Vector4(xyz.x, xyz.y, xyz.z, t.w);
                    }
                    baked.tangents = tangents;
                }

                baked.RecalculateBounds();
            }
            finally
            {
                Object.DestroyImmediate(temp);
            }

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(StorageRackBakedMeshPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(baked, StorageRackBakedMeshPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"[BackroomsBuildingPieceCreator] Storage rack mesh baked to " +
                          $"'{StorageRackBakedMeshPath}' (bounds size {baked.bounds.size}).");
                return baked;
            }

            // Overwritten in place, not deleted and recreated: a fresh GUID would break every
            // reference into this asset on the next run.
            EditorUtility.CopySerialized(baked, existing);
            Object.DestroyImmediate(baked);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(StorageRackBakedMeshPath, ImportAssetOptions.ForceUpdate);
            var reloaded = AssetDatabase.LoadAssetAtPath<Mesh>(StorageRackBakedMeshPath);
            Debug.Log($"[BackroomsBuildingPieceCreator] Storage rack mesh re-baked at " +
                      $"'{StorageRackBakedMeshPath}' (same GUID), bounds size {reloaded.bounds.size}.");
            return reloaded;
        }

        /// <summary>
        /// Bakes one source texture (read UNCOMPRESSED and linear) down to
        /// <see cref="StorageRackBakedTextureSize"/> px, then restores the source import settings.
        /// Mirrors <see cref="BakeDoorFrameTexture"/>.
        /// </summary>
        private static void BakeStorageRackTexture(string sourcePath, string bakedPath, bool sRgb)
        {
            var importer = AssetImporter.GetAtPath(sourcePath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[BackroomsBuildingPieceCreator] No texture at '{sourcePath}' — storage rack " +
                                 "baked without it.");
                return;
            }

            var prevType = importer.textureType;
            var prevCompression = importer.textureCompression;
            bool prevReadable = importer.isReadable;
            bool prevSrgb = importer.sRGBTexture;
            int prevMax = importer.maxTextureSize;

            try
            {
                importer.textureType = TextureImporterType.Default;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.isReadable = true;
                importer.sRGBTexture = sRgb;
                importer.maxTextureSize = StorageRackBakedTextureSize;
                importer.SaveAndReimport();

                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(sourcePath);
                if (tex == null)
                {
                    Debug.LogWarning($"[BackroomsBuildingPieceCreator] '{sourcePath}' did not load as Texture2D.");
                    return;
                }

                File.WriteAllBytes(bakedPath, tex.EncodeToPNG());
                AssetDatabase.ImportAsset(bakedPath, ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                importer.textureType = prevType;
                importer.textureCompression = prevCompression;
                importer.isReadable = prevReadable;
                importer.sRGBTexture = prevSrgb;
                importer.maxTextureSize = prevMax;
                importer.SaveAndReimport();
            }

            var baked = AssetImporter.GetAtPath(bakedPath) as TextureImporter;
            if (baked == null) return;

            baked.textureType = TextureImporterType.Default;
            baked.sRGBTexture = sRgb;
            baked.maxTextureSize = StorageRackBakedTextureSize;
            baked.textureCompression = TextureImporterCompression.Compressed;
            baked.mipmapEnabled = true;
            baked.SaveAndReimport();
        }

        /// <summary>
        /// Bakes both source textures and builds the URP Lit material. No normal map this import,
        /// same as the door frame — Meshy did not produce one.
        /// </summary>
        private static Material BakeStorageRackMaterial()
        {
            BackroomsEditorFolders.EnsureFolder(StorageRackBakedFolder);
            BakeStorageRackTexture(StorageRackSourceBaseColorPath, StorageRackBakedBaseColorPath, sRgb: true);
            BakeStorageRackTexture(StorageRackSourceMetallicPath, StorageRackBakedMetallicPath, sRgb: false);

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[BackroomsBuildingPieceCreator] No 'Universal Render Pipeline/Lit' shader. " +
                               "Nothing created.");
                return null;
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(StorageRackBakedMaterialPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, StorageRackBakedMaterialPath);
            }
            mat.shader = shader;

            var baseColor = AssetDatabase.LoadAssetAtPath<Texture2D>(StorageRackBakedBaseColorPath);
            var metallic = AssetDatabase.LoadAssetAtPath<Texture2D>(StorageRackBakedMetallicPath);

            if (baseColor != null) mat.SetTexture("_BaseMap", baseColor);
            if (metallic != null)
            {
                mat.SetTexture("_MetallicGlossMap", metallic);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                mat.SetFloat("_Metallic", 1f);
                mat.SetFloat("_Smoothness", 1f);
                mat.SetFloat("_SmoothnessTextureChannel", 0f); // alpha of the metallic map
            }

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }

        /// <summary>
        /// FreeBuildingPiece, not GridWallBuildingPiece: this piece is not a wall-slot alternate, it
        /// stands on its own footprint. Single bounding-box collider on the ROOT (same load-bearing
        /// reason as every other piece in this file) rather than one collider per tube of the open
        /// frame — the vendor detectors only need ONE collider to resolve the component from, and a
        /// full-bounds box is what the claim marker and every primitive piece here already do.
        /// </summary>
        private static GameObject CreateStorageRackPrefab(BuildingPieceDefinition definition,
            BuildMaterialDefinition metal, Mesh mesh, Material material)
        {
            var root = new GameObject("BR_BuildingPiece_StorageRack")
            {
                layer = LayerConstants.Building
            };

            // RENDER ONLY, bare MeshFilter/MeshRenderer — same split as every piece here. The mesh was
            // baked centred on its own origin, so this child lifts it onto the floor.
            var model = new GameObject("Model") { layer = LayerConstants.Building };
            model.transform.SetParent(root.transform, false);
            model.transform.localPosition = new Vector3(0f, StorageRackHeight * 0.5f, 0f);
            var filter = model.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = model.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;

            var collider = root.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, StorageRackHeight * 0.5f, 0f);
            collider.size = new Vector3(StorageRackWidth, StorageRackHeight, StorageRackDepth);

            // RequireComponent pulls in MaterialEffect (the ghost tint) with this call.
            var piece = root.AddComponent<FreeBuildingPiece>();
            var constructable = root.AddComponent<Constructable>();

            ConfigurePiece(piece, definition,
                new Bounds(new Vector3(0f, StorageRackHeight * 0.5f, 0f),
                    new Vector3(StorageRackWidth, StorageRackHeight, StorageRackDepth)));
            ConfigureConstructable(constructable, metal, StorageRackMetalCost);
            ConfigureMaterialEffect(root.GetComponent<MaterialEffect>(), renderer);

            var saved = PrefabUtility.SaveAsPrefabAsset(root, StorageRackPrefabPath);
            Object.DestroyImmediate(root);
            return saved;
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
            BackroomsEditorFolders.EnsureFolder("Assets/Prefabs");
            BackroomsEditorFolders.EnsureFolder(PrefabFolder);
            BackroomsEditorFolders.EnsureFolder("Assets/Resources");
            BackroomsEditorFolders.EnsureFolder("Assets/Resources/Definitions");
            BackroomsEditorFolders.EnsureFolder(DefinitionFolder);
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
    }
}
#endif
