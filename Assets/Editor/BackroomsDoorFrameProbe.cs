#if UNITY_EDITOR
using System.IO;
using System.Text;
using BackroomsSurvival.Gameplay.Building;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Read-only diagnostic for the newly imported steel door frame (Assets/MeshyImports/
    /// steel-door-frame-remesh_20260818_140152/): reports its raw geometry and renders a front-on
    /// screenshot to Temp/, since the FBX is 25 MB binary and cannot be eyeballed any other way with
    /// the editor open. Same recipe as [[unity-inedit-trigger-automation]]: additive empty scene,
    /// instantiate, camera to RenderTexture, ReadPixels, close scene without touching the user's open
    /// one. Writes nothing to any tracked asset. Delete once the frame prefab is authored.
    /// </summary>
    public static class BackroomsDoorFrameProbe
    {
        private const string FbxPath =
            "Assets/MeshyImports/steel-door-frame-remesh_20260818_140152/Meshy_AI_steel_door_frame_reme_0818120142_texture.fbx";

        private const string ShotPath = "Temp/claude_doorframe_shot.png";
        private const string SideShotPath = "Temp/claude_doorframe_side_shot.png";

        private const string FinalPrefabPath = "Assets/Prefabs/Building/BR_BuildingPiece_GridDoorFrame.prefab";
        private const string FinalShotPath = "Temp/claude_doorframe_final_shot.png";

        private const string LeafPrefabPath = "Assets/Prefabs/Building/BR_BuildingPiece_GridDoorLeaf.prefab";
        private const string LeafShotPath = "Temp/claude_doorleaf_shot.png";

        /// <summary>
        /// Renders the frame WITH the leaf nested at its authored hinge offset (both loaded as
        /// separate prefabs and composed here — they are never actually parented in the real game,
        /// this is purely to eyeball whether the leaf's size/position reads as filling the opening).
        /// </summary>
        [MenuItem("Backrooms/Diagnostics/Measure Door Leaf")]
        public static void MeasureLeaf()
        {
            var frameAsset = AssetDatabase.LoadAssetAtPath<GameObject>(FinalPrefabPath);
            var leafAsset = AssetDatabase.LoadAssetAtPath<GameObject>(LeafPrefabPath);
            if (frameAsset == null || leafAsset == null)
            {
                Debug.LogError($"[BackroomsDoorFrameProbe] frame={frameAsset != null} leaf={leafAsset != null}");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var frame = (GameObject)PrefabUtility.InstantiatePrefab(frameAsset, scene);
                var opening = frame.GetComponent<GridDoorFrameOpening>();
                if (opening == null)
                {
                    Debug.LogError("[BackroomsDoorFrameProbe] frame has no GridDoorFrameOpening.");
                    return;
                }

                var leaf = (GameObject)PrefabUtility.InstantiatePrefab(leafAsset, scene);
                leaf.transform.SetPositionAndRotation(
                    frame.transform.TransformPoint(opening.HingeLocalPosition), frame.transform.rotation);

                foreach (var r in frame.GetComponentsInChildren<MeshRenderer>(true))
                    Debug.Log($"[BackroomsDoorFrameProbe] frame renderer '{r.name}' enabled={r.enabled} " +
                              $"activeInHierarchy={r.gameObject.activeInHierarchy} bounds={r.bounds} " +
                              $"mat={(r.sharedMaterial != null ? r.sharedMaterial.name : "null")}");
                foreach (var r in leaf.GetComponentsInChildren<MeshRenderer>(true))
                    Debug.Log($"[BackroomsDoorFrameProbe] leaf renderer '{r.name}' enabled={r.enabled} " +
                              $"activeInHierarchy={r.gameObject.activeInHierarchy} bounds={r.bounds} " +
                              $"mat={(r.sharedMaterial != null ? r.sharedMaterial.name : "null")}");
                Debug.Log($"[BackroomsDoorFrameProbe] frame.pos={frame.transform.position} " +
                          $"leaf.pos={leaf.transform.position} hinge={opening.HingeLocalPosition}");

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

                var centre = new Vector3(0f, 2f, 0f);
                camGo.transform.position = centre + new Vector3(1.2f, 0.3f, 6.5f);
                camGo.transform.LookAt(centre, Vector3.up);
                cam.fieldOfView = 45f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 50f;

                RenderShot(cam, LeafShotPath);
                Debug.Log($"[BackroomsDoorFrameProbe] leaf shot -> '{LeafShotPath}'");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        /// <summary>
        /// Renders the FINISHED prefab (baked mesh + compound collider drawn as wire gizmos) so the
        /// jamb/header split can be checked against the visual opening without opening the scene.
        /// </summary>
        [MenuItem("Backrooms/Diagnostics/Measure Door Frame Result")]
        public static void MeasureResult()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(FinalPrefabPath);
            if (asset == null)
            {
                Debug.LogError($"[BackroomsDoorFrameProbe] MISSING at '{FinalPrefabPath}'");
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

                // Frame the known 5 x 4 envelope head-on, standing at floor pivot (root Y=0, centre
                // of the piece at Y=2).
                var centre = new Vector3(0f, 2f, 0f);
                camGo.transform.position = centre + new Vector3(0f, 0f, 8f);
                camGo.transform.LookAt(centre, Vector3.up);
                cam.fieldOfView = 40f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 50f;

                // Draw the compound collider boxes as bright wire boxes via Handles-less immediate
                // gizmo lines baked into a temporary mesh, so they show up in a plain camera render
                // (Gizmos only draw in the Scene view, not in Camera.Render output).
                foreach (var box in instance.GetComponentsInChildren<BoxCollider>(true))
                    DrawWireBox(box, scene);

                RenderShot(cam, FinalShotPath);
                Debug.Log($"[BackroomsDoorFrameProbe] final shot -> '{FinalShotPath}'");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void DrawWireBox(BoxCollider box, Scene scene)
        {
            var go = new GameObject("ColliderWire");
            SceneManager.MoveGameObjectToScene(go, scene);
            go.transform.SetPositionAndRotation(box.transform.position, box.transform.rotation);

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = false;
            lr.widthMultiplier = 0.02f;
            var mat = new Material(Shader.Find("Sprites/Default")) { color = Color.red };
            lr.material = mat;
            lr.startColor = lr.endColor = Color.red;

            Vector3 c = box.center;
            Vector3 e = box.size * 0.5f;
            // 12 edges of the box drawn as one connected strip (revisits a couple of corners; cheap
            // and this is a throwaway diagnostic render, not shipped geometry).
            Vector3[] p =
            {
                c + new Vector3(-e.x, -e.y, -e.z), c + new Vector3(e.x, -e.y, -e.z),
                c + new Vector3(e.x, -e.y, e.z), c + new Vector3(-e.x, -e.y, e.z),
                c + new Vector3(-e.x, -e.y, -e.z), c + new Vector3(-e.x, e.y, -e.z),
                c + new Vector3(e.x, e.y, -e.z), c + new Vector3(e.x, -e.y, -e.z),
                c + new Vector3(e.x, e.y, -e.z), c + new Vector3(e.x, e.y, e.z),
                c + new Vector3(e.x, -e.y, e.z), c + new Vector3(e.x, e.y, e.z),
                c + new Vector3(-e.x, e.y, e.z), c + new Vector3(-e.x, -e.y, e.z),
                c + new Vector3(-e.x, e.y, e.z), c + new Vector3(-e.x, e.y, -e.z),
            };
            lr.positionCount = p.Length;
            lr.SetPositions(p);
        }

        [MenuItem("Backrooms/Diagnostics/Measure Door Frame")]
        public static void Measure()
        {
            var report = new StringBuilder("[BackroomsDoorFrameProbe]\n");

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            if (asset == null)
            {
                Debug.LogError($"[BackroomsDoorFrameProbe] MISSING at '{FbxPath}'");
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
                // Left exactly as instantiated: if the FBX has a single node, that node IS the
                // instance root, and its own authored scale/rotation (Meshy's unit/axis correction)
                // is precisely what this probe needs to measure — resetting it here would erase it
                // before GetComponentsInChildren below ever reads it.
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
                report.AppendLine($"thin axis (thickness) = {thinAxis}, tall axis (height) = {tallAxis}");

                // Ensure lit, shadeless-ish rendering: add a light so the render isn't pitch black
                // (an empty additive scene has no lights).
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

                // Front view: looking down -Z at the instance, framed to its bounds with margin.
                float radius = bounds.extents.magnitude;
                Vector3 frontPos = bounds.center + new Vector3(0f, 0f, radius * 2.2f + 0.5f);
                camGo.transform.position = frontPos;
                camGo.transform.LookAt(bounds.center, Vector3.up);
                cam.fieldOfView = 45f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = radius * 10f + 5f;
                RenderShot(cam, ShotPath);

                // Side view: looking down -X, to read thickness/depth of the jambs.
                Vector3 sidePos = bounds.center + new Vector3(radius * 2.2f + 0.5f, 0f, 0f);
                camGo.transform.position = sidePos;
                camGo.transform.LookAt(bounds.center, Vector3.up);
                RenderShot(cam, SideShotPath);

                Debug.Log(report.ToString());
                Debug.Log($"[BackroomsDoorFrameProbe] shots -> '{ShotPath}', '{SideShotPath}'");
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
