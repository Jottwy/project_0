#if UNITY_EDITOR
using System.IO;
using System.Reflection;
using System.Text;
using BackroomsSurvival.Gameplay.Building;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Read-only diagnostic for the imported metal shelf (Assets/MeshyImports/
    /// metal-shelf-gamemesh_20260817_205927/): reports per-submesh bounds/names so shelf boards can
    /// be told apart from uprights, and renders front/side screenshots to Temp/ since the FBX is
    /// ~180 MB binary and cannot be eyeballed any other way with the editor open. Same recipe as
    /// BackroomsDoorFrameProbe.cs / [[unity-inedit-trigger-automation]]: additive empty scene,
    /// instantiate, camera to RenderTexture, ReadPixels, close scene without touching the user's
    /// open one. Writes nothing to any tracked asset. Delete once the rack prefab is authored
    /// (FARMING-ROADMAP.md E1).
    /// </summary>
    public static class BackroomsStorageRackProbe
    {
        private const string FbxPath =
            "Assets/MeshyImports/metal-shelf-gamemesh_20260817_205927/" +
            "Meshy_AI_metal_shelf_gamemesh_0817185423_texture.fbx";

        private const string ReportPath = "Temp/claude_rack_measure.txt";
        private const string ShotPath = "Temp/claude_rack_shot.png";
        private const string SideShotPath = "Temp/claude_rack_side_shot.png";
        private const string TopShotPath = "Temp/claude_rack_top_shot.png";

        private const string FinalPrefabPath = "Assets/Prefabs/Building/BR_BuildingPiece_StorageRack.prefab";
        private const string FinalShotPath = "Temp/claude_rack_final_shot.png";

        private const string ContainerReportPath = "Temp/claude_rack_container.txt";
        private const string AlmondWaterDefinitionPath = "Assets/Resources/Definitions/Item/BR_Almond Water.asset";
        private const string DisplayReportPath = "Temp/claude_rack_display.txt";
        private const string DisplayShotPath = "Temp/claude_rack_display_shot.png";

        /// <summary>
        /// Exercises StorageRackDisplay's REAL runtime code (not a reflection stand-in for it, like
        /// VerifyContainer above) without entering Play mode. Its Update() lazily resolves the
        /// container via StorageStation.GetContainers(), which reads Workstation.Name →
        /// _interactable.Title — a field Workstation.Start() assigns. In actual Play this is
        /// race-free (every Start() in the scene runs before any Update() does, each frame); in Edit
        /// mode NEITHER Start() nor Update() ticks automatically, so this probe drives them by hand,
        /// in the same order Unity would: Workstation.Start() first, then StorageRackDisplay.Update()
        /// once (which resolves + subscribes), then real SlotChanged events from here on are exactly
        /// what Play would produce — AddItemsById below is not a shortcut, it is the actual container
        /// API a real interaction would call.
        /// </summary>
        [MenuItem("Backrooms/Diagnostics/Verify Storage Rack Display")]
        public static void VerifyDisplay()
        {
            var report = new StringBuilder("[BackroomsStorageRackProbe] VerifyDisplay\n");

            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(FinalPrefabPath);
            var almondWater = AssetDatabase.LoadAssetAtPath<ItemDefinition>(AlmondWaterDefinitionPath);
            if (prefabAsset == null || almondWater == null)
            {
                Debug.LogError("[BackroomsStorageRackProbe] missing prefab or item definition.");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset, scene);
                var station = instance.GetComponent<StorageStation>();
                var display = instance.GetComponent<StorageRackDisplay>();
                if (station == null || display == null)
                {
                    report.AppendLine($"FAIL: station={station != null}, display={display != null}");
                    Debug.LogError(report.ToString());
                    File.WriteAllText(DisplayReportPath, report.ToString());
                    return;
                }

                typeof(Workstation).GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(station, null);
                typeof(StorageRackDisplay).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(display, null);

                var container = station.GetContainers()[0];
                report.AppendLine($"Container resolved: SlotsCount={container.SlotsCount}");

                int VisualChildCount() => instance.transform.childCount - 1; // -1 for the "Model" mesh child

                report.AppendLine($"Visual children before add: {VisualChildCount()} (expect 0)");

                for (int i = 0; i < 3; i++)
                    container.AddItemsById(almondWater.Id, 1);

                int afterAdd = VisualChildCount();
                report.AppendLine($"Visual children after 3 single adds: {afterAdd} (expect 3)");

                Vector3[] positions = new Vector3[3];
                int found = 0;
                foreach (Transform child in instance.transform)
                {
                    if (child.name.Contains("Model")) continue;
                    if (found < positions.Length) positions[found] = child.localPosition;
                    found++;
                    report.AppendLine($"  visual '{child.name}' localPos={child.localPosition:F3}");
                }

                container.RemoveItemsById(almondWater.Id, 3);

                // NOT childCount here: Destroy() is deferred to end-of-frame (correct for real
                // Play — nothing to fix in StorageRackDisplay itself), and no frame boundary passes
                // between synchronous calls in this editor script, so the GameObjects are still
                // physically in the hierarchy at this exact line even though they are marked for
                // destruction. RefreshSlot nulls its own _visuals[index] the instant it calls
                // Destroy(), in the SAME synchronous call — reading that private array is what
                // actually reflects "does the component think this slot is now empty", which is the
                // real thing under test (the GameObject's eventual physical removal is standard
                // Destroy() behavior, not this component's logic).
                var visualsField = typeof(StorageRackDisplay).GetField("_visuals",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var visuals = (GameObject[])visualsField.GetValue(display);
                int stillTracked = 0;
                foreach (var v in visuals)
                    if (v != null) stillTracked++;
                report.AppendLine($"Slots still tracking a visual after removing all 3: {stillTracked} " +
                                  "(expect 0 — GameObjects themselves are destroy-PENDING, not yet gone, " +
                                  "since no frame boundary passed in this synchronous probe)");

                bool pass = container.SlotsCount == StorageRackSlotsExpected
                            && afterAdd == 3
                            && stillTracked == 0;
                report.AppendLine(pass ? "RESULT: PASS" : "RESULT: FAIL");

                // Force-clear the destroy-PENDING GameObjects from the remove step above before
                // the shot below — purely screenshot hygiene for this probe (see comment above);
                // real Play never needs this, the engine's own frame loop does it for free.
                // Snapshot children first: DestroyImmediate while iterating transform's own
                // enumerator would mutate the collection mid-iteration.
                var staleChildren = new System.Collections.Generic.List<GameObject>();
                foreach (Transform child in instance.transform)
                    if (!child.name.Contains("Model"))
                        staleChildren.Add(child.gameObject);
                foreach (var stale in staleChildren)
                    Object.DestroyImmediate(stale);

                // Re-add for a visual screenshot (leave the scene populated for the shot).
                for (int i = 0; i < 3; i++)
                    container.AddItemsById(almondWater.Id, 1);

                // Frame by REAL combined bounds of the rack's own renderers, not a fixed offset —
                // if an item's visual came out oddly scaled/placed this is what would catch it,
                // rather than a fixed-distance shot that could end up inside or a mile from whatever
                // is actually there. Scoped to `instance` specifically, NOT
                // Object.FindObjectsByType<Renderer> — that searches every LOADED scene, including
                // whatever the user has open (RoomTesting mid-session, this time), not just this
                // probe's additive one.
                var rackRenderers = instance.GetComponentsInChildren<Renderer>(true);
                var bounds = new Bounds();
                bool started = false;
                foreach (var r in rackRenderers)
                {
                    if (started) bounds.Encapsulate(r.bounds);
                    else { bounds = r.bounds; started = true; }
                }
                report.AppendLine($"Rack renderer bounds: size=({bounds.size.x:F3},{bounds.size.y:F3}," +
                                  $"{bounds.size.z:F3}) centre=({bounds.center.x:F3},{bounds.center.y:F3}," +
                                  $"{bounds.center.z:F3}) rendererCount={rackRenderers.Length}");

                var lightGo = new GameObject("ProbeLight");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.3f;
                lightGo.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
                SceneManager.MoveGameObjectToScene(lightGo, scene);

                var camGo = new GameObject("ProbeCamera");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.85f, 0.85f, 0.9f, 1f);
                SceneManager.MoveGameObjectToScene(camGo, scene);
                float radius = started ? bounds.extents.magnitude : 1f;
                camGo.transform.position = bounds.center + new Vector3(0f, 0f, radius * 2.2f + 0.5f);
                camGo.transform.LookAt(bounds.center, Vector3.up);
                cam.fieldOfView = 45f;
                cam.nearClipPlane = 0.02f;
                cam.farClipPlane = radius * 10f + 5f;
                RenderShot(cam, DisplayShotPath);

                Debug.Log(report.ToString());
                File.WriteAllText(DisplayReportPath, report.ToString());
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        /// <summary>
        /// Structural check of the StorageStation wiring, WITHOUT entering Play mode: exercises the
        /// exact code path <c>StorageStation.GetContainers()</c> uses internally
        /// (<c>_defaultContainer.GenerateContainer(...)</c>) via reflection on the private field,
        /// rather than calling <c>GetContainers()</c> itself — that method reads
        /// <c>Workstation.Name</c>, which dereferences a private <c>_interactable</c> field that
        /// <c>Workstation.Start()</c> only assigns in Play mode; calling it here would NRE. This is
        /// exactly the "measure the real system, don't assume" style of the other probes in this
        /// project — bugs in the reflection path would themselves be a signal something in
        /// StorageStation changed shape.
        ///
        /// Confirms: slot count matches FARMING-ROADMAP.md D3 (16), items add one-per-slot (D1's
        /// no-stacking decision holds inside the CONTAINER too, not just the item's own
        /// `_stackSize`), and <c>SlotChanged</c> actually fires once per added item — the exact hook
        /// point bloque E tarea E3 (<c>StorageRackDisplay</c>) will subscribe to for the live-visual
        /// feature. A failure here means E3 has no signal to build on.
        /// </summary>
        [MenuItem("Backrooms/Diagnostics/Verify Storage Rack Container")]
        public static void VerifyContainer()
        {
            var report = new StringBuilder("[BackroomsStorageRackProbe] VerifyContainer\n");

            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(FinalPrefabPath);
            if (prefabAsset == null)
            {
                Debug.LogError($"[BackroomsStorageRackProbe] MISSING prefab at '{FinalPrefabPath}'");
                return;
            }

            var almondWater = AssetDatabase.LoadAssetAtPath<ItemDefinition>(AlmondWaterDefinitionPath);
            if (almondWater == null)
            {
                Debug.LogError($"[BackroomsStorageRackProbe] MISSING item definition at '{AlmondWaterDefinitionPath}'");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset, scene);

                var station = instance.GetComponent<StorageStation>();
                if (station == null)
                {
                    report.AppendLine("FAIL: no StorageStation on the prefab.");
                    Debug.LogError(report.ToString());
                    File.WriteAllText(ContainerReportPath, report.ToString());
                    return;
                }

                var field = typeof(StorageStation).GetField("_defaultContainer",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var generator = (ItemContainerGenerator)field.GetValue(station);

                int slotChangedCount = 0;
                var container = generator.GenerateContainer(null, populateWithItems: true, customName: "Storage Rack (verify)");
                container.SlotChanged += (in SlotReference args, SlotChangeType changeType) => slotChangedCount++;

                report.AppendLine($"SlotsCount={container.SlotsCount} (expect {StorageRackSlotsExpected})");
                report.AppendLine($"AllowStacking wired: MaxWeight={container.MaxWeight} Name='{container.Name}'");
                report.AppendLine($"Starts empty: IsEmpty={container.IsEmpty()}");

                // ONE call per item, matching how a player actually adds items (UI drag/pickup, one
                // ItemStack at a time) — NOT AddItemsById(id, 3) in one shot. That bulk form has a
                // real vendor gotcha, found while writing this probe: ItemStack's own constructor
                // (ItemStack.cs:35) clamps Count to item.StackSize AT CONSTRUCTION, so
                // AddItemsById's internal pre-check (which builds ONE dummy ItemStack(item, amount)
                // to ask "how much am I allowed") silently caps the whole bulk request to StackSize
                // — 1, now that FARMING-ROADMAP.md D1 set every item's _stackSize to 1 — regardless
                // of how many empty slots the container has. Confirmed directly: with amount=3,
                // GetAllowedCount(dummy, 3) itself returns 1, before any slot loop runs. This is a
                // caller-side gotcha (never edit vendor code, [[stp-no-direct-edits]]), and it is NOT
                // specific to this rack — InventoryRestorer.cs:236 calls the same
                // Inventory.AddItemsById(itemId, quantity) on session restore, which could now
                // silently short-change a restored stack of non-stacking items. Flagged separately;
                // out of scope for this prefab's wiring.
                int totalAdded = 0;
                for (int i = 0; i < 3; i++)
                {
                    var (added, reject) = container.AddItemsById(almondWater.Id, 1);
                    totalAdded += added;
                    if (added != 1)
                        report.AppendLine($"DIAG: single add #{i} -> added={added}, reject='{reject}'");
                }
                report.AppendLine($"Three single AddItemsById(AlmondWater, 1) calls -> totalAdded={totalAdded}, " +
                                  $"slotChangedCount={slotChangedCount}");

                int occupiedSlots = 0;
                for (int i = 0; i < container.SlotsCount; i++)
                    if (container.GetItemAtIndex(i).HasItem())
                        occupiedSlots++;
                report.AppendLine($"Occupied slots after add: {occupiedSlots} (expect 3, ONE PER ITEM — " +
                                  "no-stacking holds even though all 3 are the same item)");

                bool pass = container.SlotsCount == StorageRackSlotsExpected
                            && totalAdded == 3
                            && occupiedSlots == 3
                            && slotChangedCount == 3;
                report.AppendLine(pass ? "RESULT: PASS" : "RESULT: FAIL");

                Debug.Log(report.ToString());
                File.WriteAllText(ContainerReportPath, report.ToString());
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        // Mirrors BackroomsBuildingPieceCreator.StorageRackSlots (16) — duplicated as a literal
        // rather than referenced across files (that constant is private to the creator, and this is
        // a throwaway diagnostic, not shipped code) so this check fails LOUDLY if the two drift.
        private const int StorageRackSlotsExpected = 16;

        /// <summary>
        /// Renders the BAKED prefab (post-BackroomsBuildingPieceCreator), not the raw FBX — sanity
        /// check that the mesh survived the bake with its material and stands on the floor pivot as
        /// authored (root Y=0, piece centre at StorageRackHeight/2).
        /// </summary>
        [MenuItem("Backrooms/Diagnostics/Measure Storage Rack Result")]
        public static void MeasureResult()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(FinalPrefabPath);
            if (asset == null)
            {
                Debug.LogError($"[BackroomsStorageRackProbe] MISSING at '{FinalPrefabPath}'");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);

                var lightGo = new GameObject("ProbeLight");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.3f;
                lightGo.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
                SceneManager.MoveGameObjectToScene(lightGo, scene);

                var camGo = new GameObject("ProbeCamera");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.85f, 0.85f, 0.9f, 1f);
                SceneManager.MoveGameObjectToScene(camGo, scene);

                var centre = new Vector3(0f, 0.95f, 0f); // known ~half-height of the baked rack
                camGo.transform.position = centre + new Vector3(0f, 0f, 3.2f);
                camGo.transform.LookAt(centre, Vector3.up);
                cam.fieldOfView = 45f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 50f;

                RenderShot(cam, FinalShotPath);
                Debug.Log($"[BackroomsStorageRackProbe] final shot -> '{FinalShotPath}'");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [MenuItem("Backrooms/Diagnostics/Measure Storage Rack")]
        public static void Measure()
        {
            var report = new StringBuilder("[BackroomsStorageRackProbe]\n");

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            if (asset == null)
            {
                Debug.LogError($"[BackroomsStorageRackProbe] MISSING at '{FbxPath}'");
                File.WriteAllText(ReportPath, $"MISSING FBX at '{FbxPath}'\n");
                return;
            }

            var filters = asset.GetComponentsInChildren<MeshFilter>(true);
            report.AppendLine($"MeshFilter count under FBX root: {filters.Length}");
            foreach (var filter in filters)
            {
                var mesh = filter.sharedMesh;
                if (mesh == null)
                {
                    report.AppendLine($"  '{filter.name}': null sharedMesh");
                    continue;
                }

                var s = mesh.bounds.size;
                var c = mesh.bounds.center;
                report.AppendLine($"  '{filter.name}': localPos={filter.transform.localPosition:F4} " +
                                  $"localRot={filter.transform.localRotation.eulerAngles} " +
                                  $"localScale={filter.transform.localScale} " +
                                  $"raw mesh.bounds size=({s.x:F5},{s.y:F5},{s.z:F5}) " +
                                  $"centre=({c.x:F5},{c.y:F5},{c.z:F5}) " +
                                  $"verts={mesh.vertexCount} subMeshes={mesh.subMeshCount}");
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                // Left exactly as instantiated: authored node scale/rotation (Meshy's unit/axis
                // correction) is what this probe needs to measure — resetting it would erase it.
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);

                var bounds = new Bounds();
                bool started = false;
                foreach (var filter in instance.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null) continue;
                    var local = filter.sharedMesh.bounds;
                    var matrix = filter.transform.localToWorldMatrix;
                    var centre = matrix.MultiplyPoint3x4(local.center);
                    var extents = matrix.MultiplyVector(local.extents);
                    var world = new Bounds(centre, new Vector3(
                        Mathf.Abs(extents.x) * 2f, Mathf.Abs(extents.y) * 2f, Mathf.Abs(extents.z) * 2f));
                    if (started) bounds.Encapsulate(world);
                    else { bounds = world; started = true; }
                }

                var bs = bounds.size;
                var bc = bounds.center;
                report.AppendLine($"INSTANCE world bounds: size=({bs.x:F4},{bs.y:F4},{bs.z:F4}) " +
                                  $"centre=({bc.x:F4},{bc.y:F4},{bc.z:F4})");

                float min = Mathf.Min(bounds.size.x, Mathf.Min(bounds.size.y, bounds.size.z));
                float max = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                string thinAxis = Mathf.Approximately(min, bounds.size.x) ? "X"
                    : Mathf.Approximately(min, bounds.size.y) ? "Y" : "Z";
                string tallAxis = Mathf.Approximately(max, bounds.size.x) ? "X"
                    : Mathf.Approximately(max, bounds.size.y) ? "Y" : "Z";
                report.AppendLine($"thin axis (depth) = {thinAxis}, tall axis (height) = {tallAxis}");

                // Per-instance-filter world bounds too, in case names identify individual shelf
                // boards vs uprights/frame (helps count shelves/slots directly).
                report.AppendLine("Per-node WORLD bounds (instance):");
                foreach (var filter in instance.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null) continue;
                    var local = filter.sharedMesh.bounds;
                    var matrix = filter.transform.localToWorldMatrix;
                    var centre = matrix.MultiplyPoint3x4(local.center);
                    var extents = matrix.MultiplyVector(local.extents);
                    var world = new Bounds(centre, new Vector3(
                        Mathf.Abs(extents.x) * 2f, Mathf.Abs(extents.y) * 2f, Mathf.Abs(extents.z) * 2f));
                    report.AppendLine($"  '{filter.name}': worldPos={filter.transform.position:F4} " +
                                      $"size=({world.size.x:F4},{world.size.y:F4},{world.size.z:F4}) " +
                                      $"centre=({world.center.x:F4},{world.center.y:F4},{world.center.z:F4})");
                }

                var lightGo = new GameObject("ProbeLight");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.2f;
                lightGo.transform.rotation = Quaternion.Euler(40f, -30f, 0f);
                SceneManager.MoveGameObjectToScene(lightGo, scene);

                var camGo = new GameObject("ProbeCamera");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.15f, 0.15f, 0.18f, 1f);
                SceneManager.MoveGameObjectToScene(camGo, scene);

                float radius = bounds.extents.magnitude;

                // Front view: looking down -Z at the instance.
                camGo.transform.position = bounds.center + new Vector3(0f, 0f, radius * 2.2f + 0.5f);
                camGo.transform.LookAt(bounds.center, Vector3.up);
                cam.fieldOfView = 45f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = radius * 10f + 5f;
                RenderShot(cam, ShotPath);

                // Side view: looking down -X, to read depth of the shelf boards.
                camGo.transform.position = bounds.center + new Vector3(radius * 2.2f + 0.5f, 0f, 0f);
                camGo.transform.LookAt(bounds.center, Vector3.up);
                RenderShot(cam, SideShotPath);

                // Top-down view: to read shelf-board layout / how many uprights.
                camGo.transform.position = bounds.center + new Vector3(0f, radius * 2.2f + 0.5f, 0.001f);
                camGo.transform.LookAt(bounds.center, Vector3.forward);
                RenderShot(cam, TopShotPath);

                Debug.Log(report.ToString());
                Debug.Log($"[BackroomsStorageRackProbe] shots -> '{ShotPath}', '{SideShotPath}', '{TopShotPath}'");
                File.WriteAllText(ReportPath, report.ToString());
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void RenderShot(Camera cam, string path)
        {
            const int w = 1280, h = 960;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();

            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;

            cam.targetTexture = null;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }
}
#endif
