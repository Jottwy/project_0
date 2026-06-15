using UnityEngine;

namespace BackroomsSurvival.Gameplay.Chunks
{
    // Standalone authoring data for the Chunk Editor tool. This is NOT the
    // validated edge-based chunk format nor the Rust wire contract — it is an
    // additive design-time template, exported to JSON for later use.

    public enum LayerType { Grate, Floor, Void }

    [System.Serializable]
    public struct LayerConfig
    {
        public float zPosition;     // absolute Z (depth) of the layer
        public float height;        // height of this layer

        public LayerType type;

        // Used only when type == Grate.
        public float cellSize;      // 0.01–0.5 m
        public float holeSize;      // cellSize*0.5 – cellSize*0.99
    }

    [System.Serializable]
    public struct WallOpening
    {
        public float position;      // centre along the wall run (0–wallLength)
        public float width;         // 0.5–5 m
        public float height;        // 0.5–5 m
    }

    [System.Serializable]
    public struct WallConfig
    {
        public bool enabled;
        public float thickness;     // 0.1–1 m
        public WallOpening[] openings;
    }

    /// <summary>Design-time description of a single chunk template.</summary>
    public class ChunkTemplate : ScriptableObject
    {
        [Header("Basic Info")]
        public new string name;
        public int version = 1;
        public float weight = 0.15f;
        [TextArea(2, 4)]
        public string description;
        public string[] tags;

        [Header("Dimensions (meters)")]
        [Range(1, 100)] public float width = 5f;
        [Range(1, 100)] public float length = 5f;
        [Range(1, 100)] public float depth = 12f;

        [Header("Layers")]
        public LayerConfig[] layers;

        [Header("Walls")]
        public WallConfig north;
        public WallConfig south;
        public WallConfig east;
        public WallConfig west;

        [Header("Visual")]
        public Color wallColor  = new Color(0.5f, 0.5f, 0.5f);
        public Color floorColor = new Color(0.7f, 0.7f, 0.7f);
        public Color grateColor = new Color(0.3f, 0.3f, 0.3f);
        public Material customMaterial;

        public void InitializeDefaultLayers()
        {
            layers = new LayerConfig[]
            {
                new LayerConfig { zPosition =   0f, height = 4f, type = LayerType.Grate, cellSize = 0.05f, holeSize = 0.04f },
                new LayerConfig { zPosition =  -4f, height = 4f, type = LayerType.Floor },
                new LayerConfig { zPosition =  -8f, height = 4f, type = LayerType.Floor },
                new LayerConfig { zPosition = -12f, height = 2f, type = LayerType.Floor },
            };
        }

        public void InitializeDefaultWalls()
        {
            north = new WallConfig { enabled = true, thickness = 0.2f, openings = new WallOpening[0] };
            south = new WallConfig { enabled = true, thickness = 0.2f, openings = new WallOpening[0] };
            east  = new WallConfig { enabled = true, thickness = 0.2f, openings = new WallOpening[0] };
            west  = new WallConfig { enabled = true, thickness = 0.2f, openings = new WallOpening[0] };
        }
    }
}
