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

        [Header("Zone tint (first pass — placeholder hues, not final art)")]
        [Tooltip("Multiplied into floor/wall/ceiling tint by the chunk's zone_kind " +
                 "(backend/src/world/chunk/surface_profiles.rs ZONE_* constants, indices " +
                 "0-11). Index out of range or a null/short array falls back to white " +
                 "(no change). GridChunkBuilder looks this up via ZoneRegistry.")]
        public Color[] zoneTints = DefaultZoneTints();

        /// <summary>
        /// 12 placeholder hues, one per ZONE_* (0=Normal .. 11=Pit) — deliberately loud
        /// so the 12 zones read as visually distinct in a first playtest pass. Swap for
        /// authored per-zone palettes later; this is not the final look.
        /// </summary>
        private static Color[] DefaultZoneTints() => new[]
        {
            new Color(1f,    1f,    1f   ), // 0  ZONE_NORMAL      — no change
            new Color(1f,    0.85f, 0.6f ), // 1  ZONE_STORAGE     — warm tan
            new Color(0.75f, 1f,    0.8f ), // 2  ZONE_SAFE        — green
            new Color(1f,    0.55f, 0.55f), // 3  ZONE_DANGER      — red
            new Color(0.85f, 0.9f,  1f   ), // 4  ZONE_OPEN_HALL   — pale blue
            new Color(1f,    0.9f,  0.7f ), // 5  ZONE_PILLAR_HALL — sand
            new Color(0.6f,  0.95f, 0.95f), // 6  ZONE_HUMID       — teal
            new Color(0.35f, 0.35f, 0.4f ), // 7  ZONE_BLACKOUT    — dark grey
            new Color(1f,    0.92f, 0.65f), // 8  ZONE_MANILA      — manila yellow
            new Color(0.9f,  1f,    1f   ), // 9  ZONE_CLEANING    — bright cyan-white
            new Color(1f,    0.35f, 0.35f), // 10 ZONE_RED         — deep red
            new Color(0.35f, 0.25f, 0.35f), // 11 ZONE_PIT         — dark purple
        };

        /// <summary>Bounds-safe lookup; out-of-range or unconfigured falls back to white.</summary>
        public Color ZoneTint(int zoneKind)
        {
            if (zoneTints == null || zoneTints.Length == 0)
                return Color.white;
            int i = Mathf.Clamp(zoneKind, 0, zoneTints.Length - 1);
            return zoneTints[i];
        }
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
