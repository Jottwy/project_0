using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Audio;
using BackroomsSurvival.Gameplay.GridWorld;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.World
{
    /// <summary>
    /// Fase 5A — per-chunk fluorescent lighting driven by a <see cref="LayerVisualConfig"/>.
    /// For each tile: a luminaire MESH (emissive tube) is placed with probability
    /// <c>lightDensity</c>; of those, <c>brokenLampChance</c> are dark (mesh only, no
    /// Light); the rest cast a point Light, and ~30% of those flicker (10% of which
    /// eventually die). All decisions are deterministic per chunk (System.Random
    /// seeded by chunk coords) so every client/run agrees on which lamps are broken.
    ///
    /// Lights/meshes are parented to the chunk root, so they are destroyed with the
    /// chunk when it streams out — no manual cleanup. RenderSettings are not touched
    /// here (fog is owned by ChunkStreamer per active layer).
    /// </summary>
    public sealed class BackroomsLighting : MonoBehaviour
    {
        // ~30% of working lamps flicker; ~10% of those eventually burn out.
        private const double FlickerChance = 0.30;
        private const double DeathChance = 0.10;

        // Pieza D — salt for the per-chunk lamp-density jitter ("LDNS").
        private const uint LightSaltDensity = 0x4C444E53U;

        /// <summary>
        /// Cuánto cuelga un Spot POR DEBAJO del difusor (enmienda ADR-059, canon Level 0).
        /// Con la luz en el plano exacto del panel, un Spot de 155° corta 12,5° por debajo de
        /// ese plano y el techo circundante NO recibe nada: cada panel se leía como un
        /// rectángulo quemado flotando en vacío negro. Bajándola 0,25 m el corte del cono
        /// alcanza el techo a 0,25/tan(12,5°) ≈ 1,13 m del eje, así que el techo entre paneles
        /// deja de ser negro. No mueve la MALLA — el difusor sigue empotrado donde estaba.
        /// </summary>
        private const float SpotDropBelowPanel = 0.25f;

        /// <summary>
        /// Lo mismo para un Point, y NO es el mismo número porque no lo decide la misma física.
        /// Un Spot se ajusta por ÁNGULO de corte; un Point por INVERSO DEL CUADRADO. La malla
        /// del difusor va a <c>ceilingHeight − 0.3</c>, así que con la caída del Spot la Light
        /// queda a 0,55 m de la losa de techo: un Point ahí entrega 1/0,55² = 3,3 × intensidad
        /// justo encima, que con cualquier intensidad autorada revienta el techo y desborda el
        /// bloom — exactamente el "más superficie quemada que los propios paneles" que motivó
        /// pasar a Spot en su día.
        ///
        /// A 0,70 la Light queda a 1,0 m de la losa y el factor cae a 1,0: el techo se convierte
        /// en la superficie más brillante de la escena SIN quemarse, que es la lectura del canon
        /// de Level 0. Coste declarado: la luz baja a 3,0 m, así que el alcance horizontal a
        /// nivel de suelo pasa a √(range² − 9) y el cap de sangrado lateral de ADR-059 se aprieta
        /// de 6,07 a 5,83 PARA LAS ZONAS QUE USEN POINT. Hoy la única es ZONE_NORMAL, que ya
        /// autora 9 como excepción declarada; las demás siguen en Spot y con su cap intacto.
        /// </summary>
        private const float PointDropBelowPanel = 0.70f;

        // Lazy-init at first use, NOT via a field initializer: a static initializer in a
        // MonoBehaviour runs inside the first AddComponent's constructor context, where
        // creating a MaterialPropertyBlock throws "CreateImpl not allowed from constructor".
        private static MaterialPropertyBlock _lampMpb;

        // Buffers del alta de audio, reusados chunk a chunk: el director copia lo que
        // necesita, así que aquí no queda nada retenido. De instancia y no estáticos porque
        // el streamer tiene un único BackroomsLighting y este método no es reentrante.
        private readonly List<Vector3> _humPositions = new List<Vector3>();
        private readonly List<float>   _humPitches   = new List<float>();

        /// <summary>
        /// Spawn luminaires across <paramref name="chunkRoot"/>'s ceiling. Positions
        /// follow GridChunkBuilder's tile-centre convention ((tile + 0.5) × tileSize).
        /// <paramref name="lampMat"/> is the shared emissive material for this layer.
        /// <paramref name="zoneKind"/> (ADR-059) selecciona un <see cref="ZoneLightSet"/>
        /// de overrides si la capa autora uno para esa zona; −1 ("zona aún desconocida")
        /// solo casa con un set comodín, y la reconstrucción de chunks blancos ya vuelve
        /// a pasar por aquí con la zona correcta. Los overrides cambian VALORES, nunca
        /// el número de draws del rng — el stream por chunk queda intacto.
        /// </summary>
        public void PlaceFluorescentLights(Transform chunkRoot, int tilesX, int tilesZ,
            float tileSize, float ceilingHeight, LayerVisualConfig cfg, Material lampMat,
            int chunkX, int chunkZ, int worldLayer, byte[,] walls, int zoneKind = -1)
        {
            if (cfg == null) return;

            // ADR-059 — valores efectivos: los de la capa salvo override por zona. Se
            // resuelven UNA vez por chunk, fuera del bucle de tiles.
            Color lampColor      = cfg.lampColor;
            float lampIntensity  = cfg.lampIntensity;
            float lampRange      = cfg.lampRange;
            float lightDensity   = cfg.lightDensity;
            float brokenChance   = cfg.brokenLampChance;
            float lampEmission   = cfg.lampEmission;
            // Spot es el default histórico y sigue siéndolo: una zona que no autora nada
            // renderiza byte-idéntica a antes de que este override existiera.
            bool  lampIsPoint    = false;
            if (cfg.TryGetZoneLightSet(zoneKind, out var zl))
            {
                if (zl.overrideLampColor)        lampColor     = zl.lampColor;
                if (zl.overrideLampIntensity)    lampIntensity = zl.lampIntensity;
                if (zl.overrideLampRange)        lampRange     = zl.lampRange;
                if (zl.overrideLightDensity)     lightDensity  = zl.lightDensity;
                if (zl.overrideBrokenLampChance) brokenChance  = zl.brokenLampChance;
                if (zl.overrideLampEmission)     lampEmission  = zl.lampEmission;
                if (zl.overrideLampPoint)        lampIsPoint   = zl.lampPoint;
                // humVolume NO se resuelve aquí: lo hace el director en cada pasada vía
                // cfg.HumVolumeFor(zoneKind), para poder ajustarlo en vivo desde el
                // Inspector durante el Play. Congelarlo aquí lo volvería a atar al momento
                // de construir el chunk.
            }
            // El tipo decide la caída, no al revés: son dos derivaciones distintas (ángulo de
            // corte del cono vs inverso del cuadrado) y mezclarlas es lo que quema el techo.
            float lightDrop = lampIsPoint ? PointDropBelowPanel : SpotDropBelowPanel;

            // One deterministic RNG per chunk; tiles drawn in scan order.
            var rng = new System.Random(unchecked(chunkX * 73856093 ^ chunkZ * 19349663));
            // Enmienda ADR-066: la emisión del difusor sale de lampEmission, NO de
            // lampIntensity. Eran la misma cifra y mezclaban unidades — la potencia de la
            // Light pintaba la superficie del panel a 2.8–3.4 × blanco, que ningún
            // tonemapper puede recuperar. Cambiar cuánto ilumina ya no cambia cómo se ve.
            //
            // .linear NO es decorativo: el proyecto está en espacio LINEAL y las dos vías
            // por las que sale este color NO lo tratan igual. `light.color` es gamma y
            // Unity la convierte al renderizar; `_EmissionColor` está declarada [HDR] en
            // URP/Lit y llega por MaterialPropertyBlock, o sea que se consume TAL CUAL,
            // como si ya fuera lineal. Con un color casi blanco las dos vías coincidían y
            // el desajuste no se veía; con el amarillo mostaza de NORMAL el difusor se
            // quedaba en 0.44 de azul donde la luz proyecta 0.16 — panel pálido tirando a
            // blanco encima de una pared amarilla, que es justo lo que canta.
            Color litEmission = lampColor.linear * Mathf.Max(0f, lampEmission);

            // Pieza D — per-chunk density jitter. With a flat lightDensity every
            // chunk lands within a couple of lamps of the same count, so a corridor is
            // lit identically end to end and the space reads as one uniform box. The
            // band is deliberately asymmetric downward so genuinely DARK stretches
            // exist; the pure hash keeps it deterministic per chunk (all peers agree)
            // without touching the rng stream above. TODO(balance): band tuned by eye.
            float density = Mathf.Clamp01(lightDensity *
                (0.30f + GridChunkBuilder.Hash01(chunkX, chunkZ, LightSaltDensity) * 0.90f));

            // Lamps hang from the ceiling of a tile the player can be under. A fully
            // enclosed tile has no such space — its luminaire was buried inside solid
            // geometry, wasting a draw call and a realtime Light on something never
            // seen. Same 0x0F test GridChunkBuilder.PlaceProps uses. null ⇒ no filter
            // (caller without geometry to hand, e.g. a fixed test room).
            bool HasCeilingSpace(int tx, int tz)
                => walls == null || (walls[tx, tz] & 0x0F) != 0x0F;

            // Fase 5A (Bug #1): isolate this layer's lamps to this layer's geometry so
            // light never bleeds through the thin slabs into adjacent stacked layers.
            // The luminaire meshes go on the same Unity layer as their chunk; each Light
            // illuminates that geometry layer PLUS everything non-geometric (player,
            // props) — never the other macro-layers. (~GeoMask clears all 4 layer bits,
            // then we re-add only this one.)
            int geoLayer = GridChunkBuilder.GeoLayer(worldLayer);
            int litMask  = ~GridChunkBuilder.GeoMask | (1 << geoLayer);

            // Se vacían aquí y no al final: una salida temprana del bucle nunca puede dejar
            // dentro las lámparas del chunk anterior.
            _humPositions.Clear();
            _humPitches.Clear();

            for (int tz = 0; tz < tilesZ; tz++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    if (!HasCeilingSpace(tx, tz)) continue;   // solid tile — nothing to light
                    if (rng.NextDouble() >= density) continue; // no luminaire here

                    var localPos = new Vector3(
                        (tx + 0.5f) * tileSize,
                        ceilingHeight - 0.3f,   // just below the ceiling slab
                        (tz + 0.5f) * tileSize);

                    bool broken     = rng.NextDouble() < brokenChance;
                    bool flickering = !broken && rng.NextDouble() < FlickerChance;
                    bool dying      = flickering && rng.NextDouble() < DeathChance;
                    float frequency = flickering ? (float)(0.5 + rng.NextDouble() * 3.5) : 0f;   // 0.5–4 Hz
                    float deathIn   = dying ? (float)(10.0 + rng.NextDouble() * 50.0) : 0f;       // 10–60 s

                    // Luminaire mesh (always): emissive when working, near-dark when broken.
                    var mesh = MakeLuminaire(chunkRoot, localPos, lampMat,
                        broken ? lampColor.linear * 0.04f : litEmission, out var meshRenderer);
                    mesh.layer = geoLayer; // so only this layer's lamps light the tube

                    if (broken) continue; // dark tube, no light cast (mesh stays)

                    // Alta del zumbido. La posición es la del DIFUSOR, no la de la Light:
                    // el jugador señala el panel que ve, no el punto de emisión que le
                    // cuelga 0,25–0,70 m por debajo. Un tubo roto no zumba, por eso va tras
                    // el `continue` de arriba. El pitch sale del tile GLOBAL, así que dos
                    // clientes —y dos visitas al mismo chunk— oyen el mismo detune.
                    _humPositions.Add(chunkRoot.TransformPoint(localPos));
                    _humPitches.Add(FluorescentHumDirector.PitchFor(
                        chunkX * tilesX + tx, chunkZ * tilesZ + tz));

                    // One centred light per panel (4 lights/panel killed perf).
                    //
                    // Pieza D: the range comes from lampRange instead of a hardcoded 5 m that
                    // silently ignored the field. RESTRICCIÓN de ADR-059: el alcance horizontal
                    // a nivel de suelo es √(range² − h²) y debe quedar bajo los 5 m de baldosa,
                    // o una lámpara ilumina el pasillo MÁS ALLÁ de su propia pared — el
                    // cullingMask de abajo solo corta el sangrado entre macro-capas, nunca de
                    // lado dentro de una. Con la luz a 3,45 m eso capa el rango en ~6,07.
                    //
                    // ENMIENDA (2026-08-11, canon Level 0): ZONE_NORMAL autora range 9 y ACEPTA
                    // ese sangrado a cambio de cobertura continua. El razonamiento está en el
                    // ADR; el resumen es que a densidad 1.0 la sala de al lado tiene su propia
                    // lámpara casi siempre, así que la luz que cruza la pared cae sobre algo ya
                    // iluminado y no se lee. Las otras 12 zonas siguen capadas a 6 y el test
                    // Layer0LampRangeStaysCappedOutsideNormal lo sostiene.
                    //
                    // La Light cuelga del chunkRoot, NO de la malla: el difusor tiene escala
                    // (1.65, 0.08, 1.65) y un hijo posicionado ahí vería su offset dividido por
                    // esa escala no uniforme (0,25 m se habrían quedado en 2 cm).
                    var lightGo = new GameObject("FluorescentLight");
                    lightGo.transform.SetParent(chunkRoot, false);
                    lightGo.transform.localPosition =
                        localPos - new Vector3(0f, lightDrop, 0f);
                    // Enmienda ADR-066: Spot hacia abajo, no Point. Un Point a 0,3 m de la losa
                    // lavaba el techo alrededor de cada panel con la misma fuerza que el suelo —
                    // más superficie quemada que los propios paneles. Un difusor empotrado emite
                    // hacia abajo y ya está.
                    //
                    // NO relaja la restricción de sangrado lateral: el alcance sigue acotado por
                    // `range` igual que con Point (√(range² − h²) a nivel de suelo), y el cono
                    // solo RECORTA. Con 120° el cono da 3,7·tan60° = 6,4 m, muy por encima de los
                    // 4,7 m que impone range ≤ 6 — o sea que manda el rango, como antes.
                    lightGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // +Z → −Y
                    var light = lightGo.AddComponent<Light>();
                    // ENMIENDA (canon Level 0): el TIPO lo elige la zona. Un Spot hacia abajo
                    // no puede iluminar el plano del que cuelga — el techo negro de ADR-066 no
                    // era un fallo, era el objetivo escrito. El canon de Level 0 es lo contrario:
                    // el techo es la superficie MÁS brillante de la escena. Point lo consigue;
                    // Spot no puede, con ninguna combinación de ángulo e intensidad.
                    // La rotación de arriba se aplica igual y es inerte en Point — se deja para
                    // que volver a Spot sea cambiar un booleano en el asset y nada más.
                    light.type           = lampIsPoint ? LightType.Point : LightType.Spot;
                    // 155°, casi un hemisferio, que es lo que emite un difusor empotrado.
                    // El primer intento fue 120° y dejó las PAREDES negras: semiángulo 60°
                    // ⇒ un punto de pared a 2 m horizontales solo recibe luz si está 1,15 m
                    // por debajo del panel, o sea nada por encima de 2,55 m de altura. Con
                    // Point eso lo cubría la esfera completa; sin GI ni ambient que rellenen,
                    // recortar el hemisferio superior se llevó también las paredes.
                    // A 155° (semiángulo 77,5°) el corte cae 12,5° por debajo del plano del
                    // panel: se ilumina suelo y pared, y el techo sigue a oscuras — que era
                    // el objetivo de dejar de usar Point.
                    light.spotAngle      = 155f;
                    light.innerSpotAngle = 110f; // borde suave: un difusor no recorta en seco
                    light.color          = lampColor;
                    light.intensity      = lampIntensity;
                    light.range          = Mathf.Max(0.1f, lampRange);
                    light.shadows        = LightShadows.None;
                    light.renderMode     = LightRenderMode.Auto;
                    light.cullingMask    = litMask; // only this layer's geometry + non-geo

                    if (flickering)
                    {
                        var f = lightGo.AddComponent<LampFlicker>();
                        f.target        = light;
                        f.baseIntensity = lampIntensity;
                        f.frequency     = frequency;
                        f.mesh          = meshRenderer; // already resolved by MakeLuminaire
                        f.litEmission   = litEmission;
                        if (dying) f.Invoke(nameof(LampFlicker.StartDying), deathIn);
                    }
                }
            }

            // Un alta por CHUNK, no por lámpara: el director no necesita que la luminaria
            // tenga comportamiento, solo dónde está, así que no se le añade componente
            // ninguno. El lote se retira solo cuando `chunkRoot` muera con el chunk — no
            // hay baja explícita que se pueda olvidar, y ningún AudioSource cuelga jamás de
            // un chunk, así que descargarlo no puede dejar fuentes huérfanas.
            FluorescentHumDirector.RegisterChunkLamps(
                chunkRoot, worldLayer, _humPositions, _humPitches, cfg, zoneKind);
        }

        /// <paramref name="renderer"/> is handed back so the flicker path does not look the
        /// MeshRenderer up a second time on a component this method already resolved.
        private static GameObject MakeLuminaire(Transform parent, Vector3 localPos,
            Material lampMat, Color emission, out MeshRenderer renderer)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Luminaire";
            if (go.TryGetComponent<Collider>(out var col)) Destroy(col); // decorative — no collision
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(1.65f, 0.08f, 1.65f); // flat square ceiling panel

            var r = go.GetComponent<MeshRenderer>();
            if (lampMat != null) r.sharedMaterial = lampMat;
            _lampMpb ??= new MaterialPropertyBlock();
            _lampMpb.Clear();
            _lampMpb.SetColor(LayerVisualMaterials.EmissionColorId, emission);
            r.SetPropertyBlock(_lampMpb);
            renderer = r;
            return go;
        }
    }

    /// <summary>
    /// Fase 5A — per-lamp flicker. Oscillates the Light intensity at a per-lamp
    /// frequency/phase (not synced between lamps). A lamp told to die fades its
    /// intensity AND its luminaire mesh emission to zero, then removes itself.
    /// </summary>
    public sealed class LampFlicker : MonoBehaviour
    {
        public Light target;
        public float baseIntensity;
        public float minIntensity = 0.3f;   // × base
        public float frequency    = 2f;     // Hz

        // Optional: dim the luminaire mesh emission alongside the light when dying,
        // so a dead tube goes dark instead of glowing with no light cast.
        public MeshRenderer mesh;
        public Color litEmission;

        private float _offset;
        private bool  _dying;
        // Lazy-init at first use (same reason as BackroomsLighting._lampMpb).
        private static MaterialPropertyBlock _emb;

        private void Start() => _offset = Random.Range(0f, 100f); // cosmetic phase, non-deterministic OK

        private void Update()
        {
            if (target == null) { Destroy(this); return; }

            if (_dying)
            {
                target.intensity = Mathf.Max(0f, target.intensity - Time.deltaTime * 0.5f);
                float f = baseIntensity > 0f ? target.intensity / baseIntensity : 0f;
                SetMeshEmission(litEmission * f);
                if (target.intensity <= 0f) { SetMeshEmission(Color.black); Destroy(this); }
                return;
            }

            float t = Mathf.Sin((Time.time + _offset) * frequency * Mathf.PI * 2f);
            target.intensity = Mathf.Lerp(baseIntensity * minIntensity, baseIntensity, (t + 1f) * 0.5f);
        }

        public void StartDying() => _dying = true;

        private void SetMeshEmission(Color e)
        {
            if (mesh == null) return;
            _emb ??= new MaterialPropertyBlock();
            _emb.Clear();
            _emb.SetColor(LayerVisualMaterials.EmissionColorId, e);
            mesh.SetPropertyBlock(_emb);
        }
    }
}
