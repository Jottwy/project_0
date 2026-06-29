using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Fase 5A — per-macro-layer visual identity (World Feel Alpha). One asset per
    /// layer (0..3) drives the floor/wall/ceiling textures + tints, the lighting
    /// character (density, broken-lamp rate, colour/intensity/range) and the fog of
    /// that layer. Consumed by <see cref="ChunkStreamer"/> →
    /// <see cref="GridChunkBuilder.BuildFromWalls"/> and <see cref="World.BackroomsLighting"/>.
    ///
    /// Textures are the bespoke Backrooms set in Resources/Textures (CarpetBeige,
    /// WallpaperYellow, CeilingTiles, TileWall, TileWall 1); variety per layer is
    /// achieved by re-TINTING those base textures (no per-layer art).
    /// </summary>
    [CreateAssetMenu(menuName = "Backrooms/LayerVisualConfig", fileName = "LayerVisualConfig")]
    public sealed class LayerVisualConfig : ScriptableObject
    {
        [Header("Floor")]
        [Tooltip("Base texture for the floor slab. null → falls back to the GridFloor texture.")]
        public Texture2D floorTexture;
        public Color floorTint = Color.white;

        [Header("Wall")]
        [Tooltip("Base texture for wall panels. null → falls back to the GridWall texture.")]
        public Texture2D wallTexture;
        public Color wallTint = Color.white;

        [Header("Ceiling")]
        [Tooltip("Base texture for the ceiling slab. null → falls back to the GridCeiling texture.")]
        public Texture2D ceilingTexture;
        public Color ceilingTint = Color.white;
        [Tooltip("If true, every tile gets a ceiling slab (and the top-layer roof slab is suppressed to avoid a coplanar double surface).")]
        public bool showCeiling = true;

        [Header("Ceiling variety (Fase 5B)")]
        [Range(0f, 1f)] [Tooltip("0 = uniform panels; 1 = full variety. Scales the share of non-normal panels (sunken/absent/dropped) in a fixed 3:2:1 ratio.")]
        public float ceilingPanelVariety = 0f;
        [Range(0f, 1f)] [Tooltip("Per-chunk probability of overhead pipes (consumed in Slice 2).")]
        public float ceilingPipeChance = 0f;
        [Tooltip("Enable overhead pipes for this layer (consumed in Slice 2).")]
        public bool ceilingPipes = false;

        [Header("Lighting")]
        [Range(0f, 1f)] [Tooltip("Fraction of tiles that get a luminaire slot.")]
        public float lightDensity = 0.6f;
        [Range(0f, 1f)] [Tooltip("Of the luminaire slots, fraction with NO Light component (dark/broken; the mesh still shows).")]
        public float brokenLampChance = 0.15f;
        public Color lampColor = new Color(1f, 0.95f, 0.78f);
        public float lampIntensity = 1.8f;
        public float lampRange = 12f;

        [Header("Fog (applied to RenderSettings when this is the player's active layer)")]
        public float fogDensity = 0.04f;
        public Color fogColor = new Color(0.776f, 0.711f, 0.684f);

        [Header("Props (Fase 5C)")]
        [Tooltip("Props available for this layer (weighted pick per tile). Empty = none.")]
        public PropEntry[] props;
        [Range(0f, 1f)] [Tooltip("Fraction of eligible tiles that get a prop.")]
        public float propDensity = 0.15f;
        [Range(0f, 1f)] [Tooltip("0 = uniform placement, 1 = strongly clustered.")]
        public float propClusterBias = 0.3f;
    }

    /// <summary>
    /// Fase 5C — one prop option for a layer. <c>prefab</c> null ⇒ a primitive placeholder
    /// of <c>placeholderType</c> (desk/chair/cabinet/bin/paper/cable/stain), built at runtime
    /// by <see cref="PlaceholderFactory"/> and swappable for a real prefab later.
    /// </summary>
    [System.Serializable]
    public struct PropEntry
    {
        public GameObject prefab;          // null → use the placeholderType primitive
        public string     placeholderType; // desk/chair/cabinet/bin/paper/cable/stain
        public float      spawnWeight;     // relative weight in the per-tile pick
        public bool       canBeRotated;    // false → wall-aligned (no random yaw)
        public bool       floorOnly;       // false → may hang from the ceiling (cables)
    }
}
