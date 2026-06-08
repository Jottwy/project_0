using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    public sealed class ChunkRenderer : MonoBehaviour
    {
        [Header("Visuals")]
        public float chunkSize = 50f;
        public float wallHeight = 4f;
        public float ceilingHeight = 3.5f;

        private readonly Dictionary<long, GameObject> _pool = new Dictionary<long, GameObject>();
        private Material _floorMat;
        private Material _wallMat;
        private Material _ceilingMat;
        private Material _workbenchMat;

        private static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        private void Start()
        {
            _floorMat = MaterialHelper.MakeLit(new Color(0.72f, 0.68f, 0.55f));
            _wallMat = MaterialHelper.MakeLit(new Color(0.82f, 0.80f, 0.72f));
            _ceilingMat = MaterialHelper.MakeLit(new Color(0.88f, 0.86f, 0.80f));
            _workbenchMat = MaterialHelper.MakeLit(new Color(0.45f, 0.30f, 0.18f));

            // Force a dark camera background so there's no magenta sky.
            if (Camera.main != null)
            {
                Camera.main.clearFlags = CameraClearFlags.SolidColor;
                Camera.main.backgroundColor = new Color(0.05f, 0.05f, 0.08f);
            }
        }

        private void LateUpdate()
        {
            if (!IPCClient.TryGetInstance(out var ipc))
                return;

            var state = ipc.LatestState;
            if (state == null) return;

            var alive = new HashSet<long>();

            foreach (var cv in state.visibleChunks)
            {
                long key = Key(cv.pos[0], cv.pos[1]);
                alive.Add(key);

                if (!_pool.ContainsKey(key))
                    _pool[key] = BuildChunk(cv);
            }

            var stale = new List<long>();
            foreach (var kv in _pool)
            {
                if (!alive.Contains(kv.Key))
                {
                    Destroy(kv.Value);
                    stale.Add(kv.Key);
                }
            }
            foreach (long k in stale) _pool.Remove(k);
        }

        private GameObject BuildChunk(ChunkViewMsg cv)
        {
            var root = new GameObject($"Chunk_{cv.pos[0]}_{cv.pos[1]}");
            float ox = cv.pos[0] * chunkSize;
            float oz = cv.pos[1] * chunkSize;
            root.transform.position = new Vector3(ox, 0f, oz);

            // Floor
            CreateSlab(root.transform, "Floor",
                new Vector3(chunkSize * 0.5f, -0.05f, chunkSize * 0.5f),
                new Vector3(chunkSize, 0.1f, chunkSize),
                _floorMat);

            // Ceiling
            CreateSlab(root.transform, "Ceiling",
                new Vector3(chunkSize * 0.5f, ceilingHeight, chunkSize * 0.5f),
                new Vector3(chunkSize, 0.1f, chunkSize),
                _ceilingMat);

            // Walls (4 sides)
            CreateSlab(root.transform, "WallN",
                new Vector3(chunkSize * 0.5f, wallHeight * 0.5f, 0f),
                new Vector3(chunkSize, wallHeight, 0.2f), _wallMat);
            CreateSlab(root.transform, "WallS",
                new Vector3(chunkSize * 0.5f, wallHeight * 0.5f, chunkSize),
                new Vector3(chunkSize, wallHeight, 0.2f), _wallMat);
            CreateSlab(root.transform, "WallW",
                new Vector3(0f, wallHeight * 0.5f, chunkSize * 0.5f),
                new Vector3(0.2f, wallHeight, chunkSize), _wallMat);
            CreateSlab(root.transform, "WallE",
                new Vector3(chunkSize, wallHeight * 0.5f, chunkSize * 0.5f),
                new Vector3(0.2f, wallHeight, chunkSize), _wallMat);

            // Fluorescent lights (2 rows of 3)
            for (int row = 0; row < 2; row++)
                for (int col = 0; col < 3; col++)
                {
                    float lx = chunkSize * (0.25f + row * 0.5f);
                    float lz = chunkSize * (0.2f + col * 0.3f);
                    CreateLight(root.transform, new Vector3(lx, ceilingHeight - 0.15f, lz));
                }

            // Workbench
            if (cv.hasWorkbench)
            {
                CreateSlab(root.transform, "Workbench",
                    new Vector3(chunkSize * 0.5f, 0.5f, chunkSize * 0.5f),
                    new Vector3(2f, 1f, 1.2f), _workbenchMat);
            }

            // Stabilized/anchored tint
            if (cv.state == "anchored")
                TintChunk(root, new Color(0.6f, 0.8f, 1f, 1f));
            else if (cv.state == "stabilized")
                TintChunk(root, new Color(0.8f, 1f, 0.8f, 1f));

            return root;
        }

        private static void CreateSlab(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            Destroy(go.GetComponent<Collider>());
        }

        private void CreateLight(Transform parent, Vector3 pos)
        {
            var lightObj = new GameObject("CeilingLight");
            lightObj.transform.SetParent(parent, false);
            lightObj.transform.localPosition = pos;

            var fixture = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fixture.transform.SetParent(lightObj.transform, false);
            fixture.transform.localScale = new Vector3(2f, 0.08f, 0.3f);
            fixture.GetComponent<Renderer>().sharedMaterial =
                MaterialHelper.MakeEmissive(new Color(1f, 0.98f, 0.85f), 2f);
            Destroy(fixture.GetComponent<Collider>());

            var light = lightObj.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.96f, 0.82f);
            light.intensity = 1.2f;
            light.range = 18f;
            light.shadows = LightShadows.None;
        }

        private static void TintChunk(GameObject root, Color tint)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                var mat = r.material;
                mat.color = mat.color * tint;
                r.material = mat;
            }
        }

        private void OnDestroy()
        {
            foreach (var go in _pool.Values) if (go != null) Destroy(go);
            _pool.Clear();
        }
    }
}
