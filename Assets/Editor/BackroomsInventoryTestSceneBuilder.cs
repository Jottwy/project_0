#if UNITY_EDITOR
using BackroomsSurvival.Wearables;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Escena de pruebas del inventario mientras dura la migración de la UI (Joel, 2026-09-13): el
    /// inventario nuevo se prueba AQUÍ y <c>STP_Showcase</c> sigue con el del vendor hasta que la
    /// migración acabe. Una sala de Backrooms mínima — suelo y cuatro paredes con los materiales del
    /// grid, luz — con el <c>STP_GameMode</c> del vendor apuntando a <c>BR_UI_Player</c>, un punto de
    /// aparición y nuestros pickups por el suelo. Menú Backrooms/UI/Build Inventory Test Scene.
    ///
    /// IDEMPOTENTE: si la escena existe, sólo se vuelve a apuntar el GameMode a la variante (por si
    /// un reimport del vendor la pisa); la sala y los objetos no se duplican. Para regenerarla
    /// entera, borrar el .unity y relanzar. Sin <c>SurvivalSceneSaveHandler</c> a propósito: aquí
    /// no se guarda nada, es una escena de probar.
    /// </summary>
    public static class BackroomsInventoryTestSceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/BR_InventoryTest.unity";
        private const string GameModePrefab = "Assets/PolymindGames/STP/Prefabs/Core/STP_GameMode.prefab";
        private const string FloorMat = "Assets/Resources/GridMaterials/GridFloor.mat";
        private const string WallMat = "Assets/Resources/GridMaterials/GridWall.mat";
        private const string CeilingMat = "Assets/Resources/GridMaterials/GridCeiling.mat";
        private const string LootCratePrefab = "Assets/PolymindGames/STP/Prefabs/BuildingPieces/Free/STP_BuildingPIece_StorageCrate.prefab";
        private const string LootCrateName = "LootCrate";
        private const string PlayerPrefab = "Assets/PolymindGames/STP/Prefabs/Core/STP_Player.prefab";
        private const string PlayerName = "Player (prototipo mochilas)";
        private const string WornStorageName = "BackpackPrototype";
        private const float RoomSize = 24f;
        private const float RoomHeight = 3f;

        private static readonly string[] Pickups =
        {
            "Assets/Prefabs/Items/BR_Pickup_AlmondWater.prefab",
            "Assets/Prefabs/Items/BR_Pickup_CrankFlashlight.prefab",
            "Assets/Prefabs/Items/BR_Pickup_Screwdriver.prefab",
            "Assets/Prefabs/Items/BR_Pickup_SprayCan.prefab",
        };

        [MenuItem("Backrooms/UI/Build Inventory Test Scene")]
        public static void Build()
        {
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(BackroomsInventoryUiBuilder.VariantPath);
            var ui = variant != null ? variant.GetComponent<PlayerUI>() : null;
            if (ui == null)
            {
                Debug.LogError("[InventoryTestScene] Falta la variante: lanza antes Backrooms/UI/Build Inventory Variant.");
                return;
            }

            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            bool exists = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null;
            var scene = exists
                ? EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single)
                : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            if (!exists)
            {
                BuildRoom();
                BuildLight();
                BuildGameMode();
                BuildPickups();
            }

            PointGameMode(ui);
            EnsureLootCrate();
            EnsureBackpackPrototype();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!exists) System.IO.Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[InventoryTestScene] {(exists ? "actualizada" : "creada")}: {ScenePath}");
        }

        private static void BuildRoom()
        {
            var room = new GameObject("Room");
            Slab(room.transform, "Floor", new Vector3(0f, -0.05f, 0f), new Vector3(RoomSize, 0.1f, RoomSize), FloorMat);
            Slab(room.transform, "Ceiling", new Vector3(0f, RoomHeight + 0.05f, 0f), new Vector3(RoomSize, 0.1f, RoomSize), CeilingMat);
            float h = RoomSize / 2f, y = RoomHeight / 2f;
            Slab(room.transform, "Wall.N", new Vector3(0f, y, h), new Vector3(RoomSize, RoomHeight, 0.2f), WallMat);
            Slab(room.transform, "Wall.S", new Vector3(0f, y, -h), new Vector3(RoomSize, RoomHeight, 0.2f), WallMat);
            Slab(room.transform, "Wall.E", new Vector3(h, y, 0f), new Vector3(0.2f, RoomHeight, RoomSize), WallMat);
            Slab(room.transform, "Wall.W", new Vector3(-h, y, 0f), new Vector3(0.2f, RoomHeight, RoomSize), WallMat);
        }

        private static void Slab(Transform parent, string name, Vector3 pos, Vector3 scale, string matPath)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.isStatic = true;
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat != null) go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            else Debug.LogWarning($"[InventoryTestScene] sin material '{matPath}'");
        }

        private static void BuildLight()
        {
            // Sin cielo: la sala vive de sus lámparas. Una direccional floja para no ir a ciegas y
            // cuatro puntuales cálidas a la altura del techo, como los fluorescentes del nivel.
            var sun = new GameObject("Fill").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 0.25f;
            sun.color = new Color(0.95f, 0.93f, 0.8f);
            sun.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            float q = RoomSize / 4f;
            foreach (var p in new[] { new Vector3(q, 0, q), new Vector3(-q, 0, q), new Vector3(q, 0, -q), new Vector3(-q, 0, -q) })
            {
                var l = new GameObject("Lamp").AddComponent<Light>();
                l.type = LightType.Point;
                l.range = 11f;
                l.intensity = 2.7f;
                l.color = new Color(1f, 0.93f, 0.72f);
                l.transform.position = p + new Vector3(0f, RoomHeight - 0.2f, 0f);
            }
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.22f, 0.2f, 0.12f);
        }

        private static void BuildGameMode()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GameModePrefab);
            if (prefab == null) { Debug.LogError($"[InventoryTestScene] falta {GameModePrefab}"); return; }
            var gm = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            gm.name = "GameMode";
            var spawn = new GameObject("SpawnPoint").transform;
            spawn.position = new Vector3(0f, 0.1f, -4f);
            var so = new SerializedObject(gm.GetComponent<GameMode>());
            so.FindProperty("_initialSpawnPoint").objectReferenceValue = spawn;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void BuildPickups()
        {
            var root = new GameObject("Pickups");
            for (int i = 0; i < Pickups.Length; i++)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Pickups[i]);
                if (prefab == null) { Debug.LogWarning($"[InventoryTestScene] sin pickup '{Pickups[i]}'"); continue; }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.transform.SetParent(root.transform, false);
                // En abanico delante del jugador, a 2,5 m, separados 0,8 m, un poco por encima del suelo.
                go.transform.position = new Vector3(-1.2f + i * 0.8f, 0.3f, -1.5f);
                go.transform.rotation = Quaternion.Euler(0f, i * 37f, 0f);
            }
        }

        /// <summary>
        /// Una caja de loot del vendor (lleva <c>StorageStation</c>) a la derecha del abanico de pickups,
        /// para probar la vista ALREDEDOR. Va fuera del <c>if (!exists)</c>: también entra en la escena ya creada.
        /// </summary>
        private static void EnsureLootCrate()
        {
            if (GameObject.Find(LootCrateName) != null) return;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LootCratePrefab);
            if (prefab == null) { Debug.LogWarning($"[InventoryTestScene] sin caja de loot '{LootCratePrefab}'"); return; }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = LootCrateName;
            go.transform.position = new Vector3(2.5f, 0f, -1.5f);
            go.transform.rotation = Quaternion.Euler(0f, -90f, 0f);
            Debug.Log("[InventoryTestScene] caja de loot añadida");
        }

        /// <summary>
        /// Prototipo de mochilas (ADR-147 enm. 1, condición 1): TODO vive en esta escena. Un jugador instanciado
        /// AQUÍ con los contenedores precreados añadidos como override de ESCENA (el <c>GameMode</c> usa el jugador
        /// que ya exista antes de instanciar el suyo), el componente que empaqueta y las tres mochilas en el suelo.
        /// <c>STP_Player.prefab</c> y <c>STP_GameMode</c> no se tocan; un test lo exige.
        /// </summary>
        private static void EnsureBackpackPrototype()
        {
            var backpacks = BackroomsBackpackPrototypeCreator.EnsureAssets();
            var belts = BackroomsBackpackPrototypeCreator.EnsureBelts();
            var garments = BackroomsBackpackPrototypeCreator.EnsureGarments();
            if (backpacks.Length == 0) return;

            var player = Object.FindAnyObjectByType<Player>(FindObjectsInactive.Include);
            if (player == null)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefab);
                if (prefab == null) { Debug.LogError($"[InventoryTestScene] falta {PlayerPrefab}"); return; }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.name = PlayerName;
                var spawn = GameObject.Find("SpawnPoint");
                if (spawn != null) go.transform.position = spawn.transform.position;
                player = go.GetComponent<Player>();
            }

            var inventory = player.GetComponentInChildren<PolymindGames.InventorySystem.Inventory>(true);
            if (inventory == null) { Debug.LogError("[InventoryTestScene] el jugador no tiene Inventory"); return; }
            var so = new SerializedObject(inventory);
            var list = so.FindProperty("_defaultContainers");
            EnsureContainer(list, BackroomsBackpackPrototypeCreator.BackContainer, 1,
                AssetDatabase.LoadAssetAtPath<ContainerRestriction>(BackroomsBackpackPrototypeCreator.BackRestrictionPath));
            EnsureContainer(list, BackroomsBackpackPrototypeCreator.BackStorageContainer, BackroomsBackpackPrototypeCreator.BackStorageSlots,
                AssetDatabase.LoadAssetAtPath<ContainerRestriction>(BackroomsBackpackPrototypeCreator.StorageRestrictionPath));
            EnsureContainer(list, BackroomsBackpackPrototypeCreator.WaistContainer, 1,
                AssetDatabase.LoadAssetAtPath<ContainerRestriction>(BackroomsBackpackPrototypeCreator.WaistRestrictionPath));
            // Pieza 6: Encima, Manos (el par de guantes) y Cara, también al final.
            EnsureContainer(list, BackroomsBackpackPrototypeCreator.OuterContainer, 1,
                AssetDatabase.LoadAssetAtPath<ContainerRestriction>(BackroomsBackpackPrototypeCreator.OuterRestrictionPath));
            EnsureContainer(list, BackroomsBackpackPrototypeCreator.GlovesContainer, 1,
                AssetDatabase.LoadAssetAtPath<ContainerRestriction>(BackroomsBackpackPrototypeCreator.GlovesRestrictionPath));
            EnsureContainer(list, BackroomsBackpackPrototypeCreator.FaceContainer, 1,
                AssetDatabase.LoadAssetAtPath<ContainerRestriction>(BackroomsBackpackPrototypeCreator.FaceRestrictionPath));
            // D14 enm. 1: base de 9 y barra de 8 (2 manos + cinturón), capada por lo que lleves en la cintura. Override de
            // ESCENA sobre los contenedores 0 y 1 del vendor: el prefab del jugador no cambia.
            SetSlots(list, BackroomsBackpackPrototypeCreator.BaseContainer, BackroomsBackpackPrototypeCreator.BaseSlots);
            SetSlots(list, BackroomsBackpackPrototypeCreator.HolsterContainer, BackroomsBackpackPrototypeCreator.HolsterSlots);
            AddRestriction(list, BackroomsBackpackPrototypeCreator.HolsterContainer,
                AssetDatabase.LoadAssetAtPath<ContainerRestriction>(BackroomsBackpackPrototypeCreator.HolsterRestrictionPath));
            so.ApplyModifiedPropertiesWithoutUndo();

            var worn = GameObject.Find(WornStorageName);
            if (worn == null) worn = new GameObject(WornStorageName, typeof(BackroomsWornStorage));
            // D6 enm. 2: el peso máximo sale del equipo.
            if (worn.GetComponent<BackroomsCarryWeight>() == null) worn.AddComponent<BackroomsCarryWeight>();
            // D6 enm. 3: la carga frena; y lo que da cada prenda puesta.
            if (worn.GetComponent<BackroomsCarrySpeed>() == null) worn.AddComponent<BackroomsCarrySpeed>();
            if (worn.GetComponent<BackroomsWornStats>() == null) worn.AddComponent<BackroomsWornStats>();

            var root = GameObject.Find("Backpacks");
            if (root == null)
            {
                root = new GameObject("Backpacks");
                for (int i = 0; i < backpacks.Length; i++)
                {
                    var def = backpacks[i];
                    if (def == null || def.Pickup == null) continue;
                    var pickup = (GameObject)PrefabUtility.InstantiatePrefab(def.Pickup.gameObject);
                    pickup.transform.SetParent(root.transform, false);
                    pickup.transform.position = new Vector3(-2.6f - i * 0.9f, 0.3f, -1.5f);
                    pickup.name = def.name;
                    var item = new SerializedObject(pickup.GetComponent<ItemPickup>());
                    item.FindProperty("_item._value").intValue = def.Id;
                    item.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            if (GameObject.Find("Belts") == null)
            {
                var beltRoot = new GameObject("Belts");
                for (int i = 0; i < belts.Length; i++)
                    SpawnPickup(belts[i], beltRoot.transform, new Vector3(-2.6f - i * 0.9f, 0.3f, -2.6f));
            }
            if (GameObject.Find("Garments") == null)
            {
                var garmentRoot = new GameObject("Garments");
                for (int i = 0; i < garments.Length; i++)
                    SpawnPickup(garments[i], garmentRoot.transform, new Vector3(-2.6f - i * 0.9f, 0.3f, -3.7f));
            }
            Debug.Log("[InventoryTestScene] prototipo de mochilas montado");
        }

        private static SerializedProperty FindContainerEntry(SerializedProperty list, string name)
        {
            for (int i = 0; i < list.arraySize; i++)
            {
                var entry = list.GetArrayElementAtIndex(i);
                if (entry.FindPropertyRelative("Name").stringValue == name) return entry;
            }
            Debug.LogWarning($"[InventoryTestScene] el jugador no tiene contenedor '{name}'");
            return null;
        }

        private static void SetSlots(SerializedProperty list, string name, int slots)
        {
            var entry = FindContainerEntry(list, name);
            if (entry != null) entry.FindPropertyRelative("MaxSlotCount").intValue = slots;
        }

        private static void AddRestriction(SerializedProperty list, string name, ContainerRestriction restriction)
        {
            var entry = FindContainerEntry(list, name);
            if (entry == null || restriction == null) return;
            var restrictions = entry.FindPropertyRelative("Restrictions");
            for (int i = 0; i < restrictions.arraySize; i++)
                if (restrictions.GetArrayElementAtIndex(i).objectReferenceValue == restriction) return;
            restrictions.arraySize++;
            restrictions.GetArrayElementAtIndex(restrictions.arraySize - 1).objectReferenceValue = restriction;
        }

        private static void SpawnPickup(ItemDefinition def, Transform parent, Vector3 position)
        {
            if (def == null || def.Pickup == null) return;
            var pickup = (GameObject)PrefabUtility.InstantiatePrefab(def.Pickup.gameObject);
            pickup.transform.SetParent(parent, false);
            pickup.transform.position = position;
            pickup.name = def.name;
            var item = new SerializedObject(pickup.GetComponent<ItemPickup>());
            item.FindProperty("_item._value").intValue = def.Id;
            item.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Añade el contenedor AL FINAL si falta (ADR-147 punto 1: los índices 0-5 no se mueven).</summary>
        private static void EnsureContainer(SerializedProperty list, string name, int slots, ContainerRestriction restriction)
        {
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).FindPropertyRelative("Name").stringValue == name) return;
            list.arraySize++;
            var entry = list.GetArrayElementAtIndex(list.arraySize - 1);
            entry.FindPropertyRelative("Name").stringValue = name;
            entry.FindPropertyRelative("AllowStacking").boolValue = true;
            entry.FindPropertyRelative("MaxSlotCount").intValue = slots;
            entry.FindPropertyRelative("MaxWeightLimit").floatValue = 1000f;
            var restrictions = entry.FindPropertyRelative("Restrictions");
            restrictions.arraySize = restriction != null ? 1 : 0;
            if (restriction != null) restrictions.GetArrayElementAtIndex(0).objectReferenceValue = restriction;
            entry.FindPropertyRelative("PredefinedItems").arraySize = 0;
            entry.FindPropertyRelative("LootTable").objectReferenceValue = null;
        }

        private static void PointGameMode(PlayerUI ui)
        {
            var gameModes = Object.FindObjectsByType<GameMode>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (gameModes.Length != 1) { Debug.LogError($"[InventoryTestScene] esperaba UN GameMode, hay {gameModes.Length}"); return; }
            var so = new SerializedObject(gameModes[0]);
            var prop = so.FindProperty("_playerUIPrefab");
            if (prop.objectReferenceValue != ui)
            {
                prop.objectReferenceValue = ui;
                so.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log("[InventoryTestScene] GameMode._playerUIPrefab -> BR_UI_Player");
            }
        }
    }
}
#endif
