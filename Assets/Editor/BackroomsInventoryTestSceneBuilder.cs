#if UNITY_EDITOR
using PolymindGames;
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
