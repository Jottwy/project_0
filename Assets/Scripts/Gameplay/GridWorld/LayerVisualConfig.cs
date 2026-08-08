using BackroomsSurvival.Net;
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

        [Tooltip("Sparse override list: alternative prop catalogues selected by zone_kind. The " +
                 "FIRST matching entry wins. Empty/null (or no match) ⇒ every zone uses `props`, " +
                 "which is exactly the behaviour before this field existed.")]
        public ZonePropSet[] zonePropSets;

        /// <summary>
        /// Prop catalogue for <paramref name="zoneKind"/>: the first matching
        /// <see cref="ZonePropSet"/>, else the layer-wide <see cref="props"/>. Same sparse,
        /// first-match-wins shape as <see cref="WallPrefabFor"/> and the same degradation for an
        /// unknown zone (−1, ZoneRegistry hasn't answered) — it matches no zone-SPECIFIC set, so
        /// the chunk gets the layer's own props rather than baking a wrong zone's furniture.
        ///
        /// This is the generalisation the zone_kind pattern was missing: until OFFICE, props were
        /// per-LAYER only and nothing but the tint, the wall model and the loot table could vary
        /// by zone. Deliberately NOT a 13-row matrix — an unauthored zone must cost nothing.
        /// </summary>
        public PropEntry[] PropsFor(int zoneKind)
        {
            if (zonePropSets == null) return props;
            for (int i = 0; i < zonePropSets.Length; i++)
            {
                if (!zonePropSets[i].Matches(zoneKind)) continue;
                var set = zonePropSets[i].props;
                // Un set autorado pero VACÍO no significa "sin props": significa que el autor
                // no terminó de rellenarlo. Cae al catálogo de capa en vez de dejar la zona
                // pelada sin ningún aviso.
                if (set != null && set.Length > 0) return set;
            }
            return props;
        }

        [Header("Per-tile tint palette (Pieza C)")]
        [Tooltip("Alternative floor tints picked per TILE by a deterministic hash. Empty/null " +
                 "⇒ every tile uses floorTint (behaviour before this field existed).")]
        public Color[] floorTintVariants;
        [Tooltip("Alternative wall tints, same rule. Also drives the tile's pillars, so a " +
                 "column matches the wall panels of its own tile.")]
        public Color[] wallTintVariants;
        [Tooltip("Alternative ceiling tints, same rule.")]
        public Color[] ceilingTintVariants;

        [Header("Wall variety — medias paredes")]
        [Range(0f, 1f)]
        [Tooltip("Fraction of drawn wall panels that become KNEE WALLS (partial height, " +
                 "see-over). 0 = every panel full height (behaviour before this field " +
                 "existed). Picked per panel by a deterministic hash, not by the tint rng.")]
        public float wallPanelVariety = 0f;
        [Tooltip("Height (m) of a knee wall. Clamped up to MinKneeWallHeight — a knee wall " +
                 "MUST stay untraversable (see KneeWallHeightClamped).")]
        public float kneeWallHeight = 1.35f;

        /// <summary>
        /// Floor for <see cref="kneeWallHeight"/>. Wall panels are the ONLY grid_gen surface
        /// that carries a runtime Unity collider (GridChunkBuilder.AddColliderIfMissing;
        /// pillars and ceiling panels do not), so their height is a gameplay quantity, not a
        /// cosmetic one. FPS_Player.prefab: _jumpHeight 1.05, m_StepOffset 0.275 — below
        /// ~1.15 m the player clears the panel and walks through an edge the backend marks
        /// solid. 1.2 m keeps the margin; eye height ~1.65 m still sees over.
        /// </summary>
        public const float MinKneeWallHeight = 1.2f;

        /// <summary>Effective knee-wall height: never low enough to become traversable.</summary>
        public float KneeWallHeightClamped => Mathf.Max(MinKneeWallHeight, kneeWallHeight);

        [Range(0f, 1f)]
        [Tooltip("Chance that a DOORWAY (an open tile edge with wall continuing on both " +
                 "sides of the same line) gets a hanging lintel beam. 0 = none. Lintels " +
                 "only ever go over edges the backend already marks OPEN — never over a " +
                 "walled edge, which would make the render disagree with the phantom's " +
                 "grid_gen collision (ADR-018).")]
        public float lintelChance = 0f;
        [Tooltip("Height (m) of the underside of a lintel beam. Clamped up to " +
                 "MinLintelClearance so the doorway stays walkable.")]
        public float lintelClearance = 2.2f;

        /// <summary>
        /// Floor for <see cref="lintelClearance"/>. Same reasoning as
        /// <see cref="MinKneeWallHeight"/>, mirrored: the beam carries a runtime collider,
        /// so hanging it lower than the player capsule (FPS_Player.prefab m_Height 1.8)
        /// would CLOSE a doorway the backend marks open — the phantom would walk through
        /// where the player cannot. 1.9 m keeps the margin.
        /// </summary>
        public const float MinLintelClearance = 1.9f;

        /// <summary>Effective lintel clearance: never low enough to block the doorway.</summary>
        public float LintelClearanceClamped => Mathf.Max(MinLintelClearance, lintelClearance);

        [Header("Wall model variants (ADR-035)")]
        [Tooltip("Sparse override list: sets of wall prefabs picked per PANEL by a " +
                 "deterministic hash, selected by zone_kind + RoomType. Empty/null ⇒ every " +
                 "panel uses GridPrefabSet.wall (behaviour before this field existed). The " +
                 "FIRST matching entry wins, so put the specific ones before the wildcards.")]
        public WallVariantSet[] wallVariantSets;

        /// <summary>
        /// Wall prefab for a panel in (<paramref name="zoneKind"/>, <paramref name="roomType"/>)
        /// whose hash is <paramref name="h"/> ∈ [0,1), or <c>null</c> when nothing is authored
        /// for that combination — the caller then uses <c>GridPrefabSet.wall</c>. Same shape and
        /// spirit as <see cref="PickTint"/>: an unauthored layer keeps exactly the render it had
        /// before this field existed.
        ///
        /// <paramref name="zoneKind"/> may be −1 ("unknown" — ZoneRegistry hasn't answered
        /// yet), which never matches a zone-SPECIFIC set: a chunk built before its zone landed
        /// falls back to the plain panel rather than baking a wrong zone's model, the same
        /// degradation its zone tint already takes. A set marked <c>anyZoneKind</c> still
        /// applies, by design — it claims every zone, and an unknown one is still one of them.
        ///
        /// FIRST match wins (not best match): the list is short and hand-ordered, and a
        /// "most specific wins" rule would need a scoring pass that is impossible to predict
        /// from the Inspector.
        /// </summary>
        public GameObject WallPrefabFor(int zoneKind, RoomZoneKind roomType, float h)
        {
            if (wallVariantSets == null) return null;
            for (int i = 0; i < wallVariantSets.Length; i++)
            {
                if (!wallVariantSets[i].Matches(zoneKind, roomType)) continue;
                return PickWallVariant(wallVariantSets[i].variants, h);
            }
            return null;
        }

        /// <summary>
        /// Weighted pick over <paramref name="variants"/> by <paramref name="h"/> ∈ [0,1) —
        /// same accumulate-and-compare shape as <c>GridChunkBuilder.PickProp</c>. Returns null
        /// (⇒ caller uses the plain wall) when the set is empty or every weight is ≤ 0, and a
        /// null <c>prefab</c> inside a variant means the same thing, so "the stock panel" can
        /// be authored as one weighted option among the new models.
        /// </summary>
        private static GameObject PickWallVariant(WallVariant[] variants, float h)
        {
            if (variants == null || variants.Length == 0) return null;

            float total = 0f;
            for (int i = 0; i < variants.Length; i++)
                total += SaneWeight(variants[i].weight);
            // `!(total > 0f)`, no `total <= 0f`: con NaN AMBAS comparaciones son falsas, así
            // que la forma negada es la única que atrapa un peso mal autorizado en vez de
            // dejar pasar un `r` NaN que nunca cumple `r < acc`.
            if (!(total > 0f)) return null;

            float r = Mathf.Clamp01(h) * total;
            float acc = 0f;
            int last = -1;
            for (int i = 0; i < variants.Length; i++)
            {
                float w = SaneWeight(variants[i].weight);
                if (w <= 0f) continue; // una variante de peso 0 no debe salir NUNCA
                last = i;
                acc += w;
                if (r < acc) return variants[i].prefab;
            }
            // Solo se alcanza con h == 1f exacto (Hash01 puede devolverlo cuando los 24 bits
            // bajos son todos 1): cae en la última variante CON PESO, no en la última del
            // array, que podría tener peso 0.
            return last >= 0 ? variants[last].prefab : null;
        }

        /// <summary>Peso saneado: negativo, NaN o infinito cuentan como 0.</summary>
        private static float SaneWeight(float w) =>
            (w > 0f && !float.IsInfinity(w)) ? w : 0f;

        /// <summary>
        /// One entry of <paramref name="variants"/> chosen by <paramref name="h"/> ∈ [0,1),
        /// or <paramref name="baseTint"/> when the palette is unset. Bounds-safe, same shape
        /// as <see cref="ZoneTint"/> — an unauthored layer keeps its single flat tint, so
        /// adding this field changed nothing until a palette was written.
        /// </summary>
        private static Color PickTint(Color[] variants, Color baseTint, float h)
        {
            if (variants == null || variants.Length == 0) return baseTint;
            return variants[Mathf.Clamp((int)(h * variants.Length), 0, variants.Length - 1)];
        }

        /// <summary>Floor tint for a tile whose palette hash is <paramref name="h"/>.</summary>
        public Color FloorTintFor(float h) => PickTint(floorTintVariants, floorTint, h);

        /// <summary>Wall (and pillar) tint for a tile whose palette hash is <paramref name="h"/>.</summary>
        public Color WallTintFor(float h) => PickTint(wallTintVariants, wallTint, h);

        /// <summary>Ceiling tint for a tile whose palette hash is <paramref name="h"/>.</summary>
        public Color CeilingTintFor(float h) => PickTint(ceilingTintVariants, ceilingTint, h);

        [Header("Zone tint (first pass — placeholder hues, not final art)")]
        [Tooltip("Multiplied into floor/wall/ceiling tint by the chunk's zone_kind " +
                 "(backend/src/world/chunk/surface_profiles.rs ZONE_* constants, indices " +
                 "0-12). Index out of range or a null/short array falls back to white " +
                 "(no change). GridChunkBuilder looks this up via ZoneRegistry.")]
        public Color[] zoneTints = DefaultZoneTints();

        /// <summary>
        /// 13 placeholder hues, one per ZONE_* (0=Normal .. 12=Office) — deliberately loud
        /// so the zones read as visually distinct in a first playtest pass. Swap for
        /// authored per-zone palettes later; this is not the final look.
        ///
        /// THE LENGTH IS LOAD-BEARING: <see cref="ZoneTint"/> clamps out-of-range instead of
        /// throwing, so a short array serves the LAST hue silently. An OFFICE chunk built
        /// against the 12-entry version would have been tinted ZONE_PIT's dark purple.
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
            new Color(0.82f, 0.86f, 0.80f), // 12 ZONE_OFFICE      — pale institutional green
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

    /// <summary>
    /// Un catálogo de props ligado a un <c>zone_kind</c>. Lista DISPERSA con la misma forma y el
    /// mismo criterio que <see cref="WallVariantSet"/>: solo se autoriza la zona que de verdad
    /// quiere muebles propios, todo lo demás cae a <see cref="LayerVisualConfig.props"/>.
    ///
    /// El comodín es un BOOLEANO EXPLÍCITO por el mismo motivo que allí: un elemento recién
    /// añadido desde el Inspector nace con <c>zoneKind == 0</c>, que es ZONE_NORMAL — una zona
    /// concreta, no "todas". Con un centinela el default habría aplicado en silencio solo a
    /// ZONE_NORMAL cuando el autor casi siempre quiere lo contrario.
    /// </summary>
    [System.Serializable]
    public struct ZonePropSet
    {
        [Tooltip("zone_kind al que aplica (0..12, ver ZoneTint). Se ignora si anyZoneKind está " +
                 "marcado.")]
        public int zoneKind;

        [Tooltip("Marcado ⇒ aplica a todos los zone_kind.")]
        public bool anyZoneKind;

        [Tooltip("Catálogo de props de esta zona, con sus pesos. Vacío ⇒ se usa el de la capa.")]
        public PropEntry[] props;

        /// <summary>True si este set aplica a <paramref name="zoneKindQuery"/>. Un −1 ("zona aún
        /// desconocida") no casa con ningún set específico, porque −1 no es un zone_kind válido;
        /// uno marcado <c>anyZoneKind</c> sí.</summary>
        public bool Matches(int zoneKindQuery) => anyZoneKind || zoneKind == zoneKindQuery;
    }

    /// <summary>
    /// ADR-035 — one wall-model option inside a <see cref="WallVariantSet"/>. Same
    /// (prefab, weight) shape as <see cref="PropEntry"/>, and the same null rule:
    /// <c>prefab</c> null ⇒ the stock <c>GridPrefabSet.wall</c> panel, so "keep the old
    /// wall 70 % of the time" is authorable without referencing the prefab.
    ///
    /// CONTRATO DE GEOMETRÍA: cualquier malla puesta aquí debe ser un panel de 5 m × 4 m
    /// × 0.2 m CON EL PIVOTE EN EL SUELO, igual que Wall.prefab. Knee walls
    /// (<c>localScale.y = KneeWallHeightClamped / 4</c>) y dinteles
    /// (<c>localScale.y = (4 − clearance) / 4</c>, <c>position.y = clearance</c>) escalan
    /// contra esa altura de 4 m hardcodeada, y no hay assert que lo detecte — un modelo
    /// de otra altura o con pivote centrado las rompe EN SILENCIO.
    /// </summary>
    [System.Serializable]
    public struct WallVariant
    {
        public GameObject prefab; // null → the stock GridPrefabSet.wall panel
        public float      weight; // relative weight in the per-panel pick
    }

    /// <summary>
    /// ADR-035 — un conjunto de variantes de pared con su selector. Lista DISPERSA: solo
    /// se autoriza la combinación que de verdad quiere un modelo distinto; todo lo demás
    /// cae al panel de siempre. No es una matriz de zone_kind × RoomType.
    /// </summary>
    [System.Serializable]
    public struct WallVariantSet
    {
        [Tooltip("zone_kind al que aplica (0..12, ver ZoneTint). Se ignora si anyZoneKind " +
                 "está marcado.")]
        public int zoneKind;

        [Tooltip("Marcado ⇒ aplica a todos los zone_kind.")]
        public bool anyZoneKind;

        [Tooltip("RoomType al que aplica. Se ignora si anyRoomType está marcado.")]
        public RoomZoneKind roomType;

        [Tooltip("Marcado ⇒ aplica a los 3 RoomType (Open/SealedRoom/CorridorSpine).")]
        public bool anyRoomType;

        [Tooltip("Opciones de modelo con su peso relativo. Vacío ⇒ el panel de siempre.")]
        public WallVariant[] variants;

        /// <summary>
        /// True si este set aplica a (<paramref name="zoneKindQuery"/>,
        /// <paramref name="roomTypeQuery"/>).
        ///
        /// Los comodines son BOOLEANOS EXPLÍCITOS, no un valor centinela: un elemento recién
        /// añadido desde el Inspector nace con <c>zoneKind == 0</c>, que es ZONE_NORMAL —
        /// una zona concreta. Con un centinela "−1 = cualquiera" ese default habría aplicado
        /// en silencio solo a ZONE_NORMAL cuando el autor casi siempre quiere "en todas".
        /// Así el default es igual de restrictivo pero se LEE en el Inspector.
        ///
        /// Un <paramref name="zoneKindQuery"/> de −1 ("zona aún desconocida", ZoneRegistry
        /// sin responder) no casa con ningún set específico, porque −1 no es un zone_kind
        /// válido; un set marcado <c>anyZoneKind</c> SÍ aplica, y es lo correcto: dice
        /// "cualquier zona" y una zona desconocida sigue siendo una de ellas.
        /// </summary>
        public bool Matches(int zoneKindQuery, RoomZoneKind roomTypeQuery) =>
            (anyZoneKind || zoneKind == zoneKindQuery) &&
            (anyRoomType || roomType == roomTypeQuery);
    }
}
