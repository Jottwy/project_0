#if UNITY_EDITOR
using System.IO;
using System.Text;
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
