//! ADR-033 — `zone_kind` como input de densidad geométrica en `grid_gen`.
//!
//! Resuelve, para un chunk `(cx, cz, layer)`, qué `LayerRules` debe usar
//! `grid_gen` al generarlo. Acotado por el ADR a las rutas de RENDER
//! (`chunk_tile_walls`) y del ROBAPIELES (`GridGenChunkCache`); la colisión del
//! JUGADOR REAL sigue contra `world::generator` y NO hereda esto — deuda
//! conocida y aceptada mientras las partes 1-2 de ADR-026 sigan bloqueadas.
//!
//! POR QUÉ VIVE EN `world/` Y NO EN `grid_gen/`: `zone_kind` es un dato de
//! `world/`, y `grid_gen` no importa nada de `world/` (invariante del módulo).
//! Este módulo es el puente en la dirección permitida: lee `world/`, produce un
//! `LayerRules` que `grid_gen` consume sin saber qué es una zona.
//!
//! RESOLVER PURO (opción R1 del plan; R3 híbrido DESCARTADO): la zona se
//! re-deriva de `(world_seed, cx, cz, layer)` y NUNCA se lee de la `World` viva.
//! Razón dura: cada peer corre su propio backend y debe derivar geometría
//! idéntica sin comunicación. Leer chunks cargados haría que el MISMO chunk se
//! generase distinto antes y después de cargarse, rompiendo el contrato de
//! determinismo de `grid_gen` (misma seed → grid byte-idéntico) del que depende
//! que todos los peers vean el mismo mundo.

use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};

use rand::rngs::StdRng;
use rand::{Rng, SeedableRng};

use crate::utils::ChunkPos;
use crate::world::architecture::chunk_generator::chunk_seed_layer;
use crate::world::architecture::collision_builder::template_is_vertical;
use crate::world::architecture::layout_grammars::{
    template_zone_kind, TEMPLATE_ARCH_ROOM, TEMPLATE_BLACKOUT_ZONE, TEMPLATE_DEAD_END,
    TEMPLATE_HALLWAY_CORNER, TEMPLATE_HALLWAY_STRAIGHT, TEMPLATE_HALLWAY_T, TEMPLATE_HUMID_ZONE,
    TEMPLATE_INTERSECTION, TEMPLATE_MANILA_ROOM, TEMPLATE_OPEN_HALL, TEMPLATE_PILLAR_ROOM,
    TEMPLATE_PIT_ROOM_PLACEHOLDER, TEMPLATE_RED_ROOM_WARNING, TEMPLATE_ROOM_BASIC,
    TEMPLATE_STORAGE_ROOM,
};
use crate::world::chunk::{ChunkLayer, ZONE_PILLAR_HALL};
use crate::world::generator::{generate_initial_structures, structure_zone_kind};
use crate::world::grid_gen::{LayerRules, LAYER_PROFILES};

/// Zonas de los chunks que pertenecen a una estructura inicial, memoizado por
/// seed. Es memoización de una función pura (`generate_initial_structures` lo
/// es), no estado mutable del mundo.
type StructureZoneMap = HashMap<(i32, i32, ChunkLayer), u8>;

static STRUCTURE_ZONES: OnceLock<Mutex<HashMap<u64, &'static StructureZoneMap>>> = OnceLock::new();

/// `LayerRules` a usar para generar `(cx, cz, layer)` en `grid_gen`.
///
/// Firma compatible con `grid_gen::RulesFn` para poder inyectarse en
/// `GridGenChunkCache` sin que `grid_gen` importe este módulo.
pub fn rules_for(world_seed: u64, cx: i32, cz: i32, layer: u8) -> LayerRules {
    rules_for_zone(zone_kind_for(world_seed, cx, cz, layer), layer)
}

/// `zone_kind` de `(cx, cz, layer)`, re-derivado del seed.
///
/// Orden de resolución (espejo del que usa `world::generator` al construir el
/// mundo): las estructuras iniciales GANAN sobre el sorteo de plantilla, porque
/// `generate_initial_structure_chunks_inner` sobrescribe `layout.zone_kind` con
/// `structure_zone_kind` DESPUÉS de generar el chunk. Sin esta consulta previa,
/// todo el cluster de spawn V30A resolvería a la zona de su plantilla en vez de
/// a la de su estructura — exactamente la clase de error silencioso del bug de
/// capa 0 de `ZoneRegistry`, y otra vez justo en la zona más visible del juego.
pub fn zone_kind_for(world_seed: u64, cx: i32, cz: i32, layer: u8) -> u8 {
    let chunk_layer = layer as ChunkLayer;
    if let Some(&zone) = structure_zones(world_seed).get(&(cx, cz, chunk_layer)) {
        return zone;
    }
    template_zone_kind(expansion_template_id(world_seed, (cx, cz), chunk_layer))
}

/// Mapa de zonas de estructura del seed (construido una vez por seed).
fn structure_zones(world_seed: u64) -> &'static StructureZoneMap {
    let cache = STRUCTURE_ZONES.get_or_init(|| Mutex::new(HashMap::new()));
    let mut guard = cache.lock().expect("structure zone cache poisoned");
    guard.entry(world_seed).or_insert_with(|| {
        let mut map = StructureZoneMap::new();
        for structure in generate_initial_structures(world_seed) {
            for (index, pos) in structure.chunks.iter().copied().enumerate() {
                let layer = structure.chunk_layer(index);
                // `generate_structure_chunk` aplica el override por-chunk cuando
                // existe; si no, el chunk conserva el `template_id` del sorteo de
                // expansión. `structure_zone_kind` solo mira el template en su
                // rama `_` (tipos sin zona propia), pero se resuelve igual para
                // los dos casos para no depender de qué rama toca.
                let template_id = structure
                    .chunk_overrides
                    .get(index)
                    .map(|&(template_id, _)| template_id)
                    .unwrap_or_else(|| expansion_template_id(world_seed, pos, layer));
                map.insert(
                    (pos.0, pos.1, layer),
                    structure_zone_kind(structure.structure_type, template_id),
                );
            }
        }
        // Fugado a propósito: una entrada por seed, viva durante todo el proceso
        // (el backend usa UN world_seed). Evita clonar el mapa en cada consulta y
        // mantiene la firma `&'static` que hace `rules_for` usable como `fn`.
        Box::leak(Box::new(map))
    })
}

/// Sorteo de plantilla del camino de EXPANSIÓN.
///
/// ESPEJO LITERAL de `world::generator::generate_chunk_layer` (el bloque
/// `template_id` + la regla anti-verticalidad de la Fase 2.6). Se duplica en vez
/// de llamarse porque aquella función genera el chunk COMPLETO (entidades,
/// items, relocalización), coste que la ruta de render no puede pagar por chunk
/// pedido, y ADR-033 prohíbe tocar `world::generator` para extraer el trozo.
///
/// La duplicación está clavada por `resolver_matches_real_world_zone_kind`: si
/// el sorteo original cambia y este no, ese test falla. NO editar uno sin el
/// otro.
fn expansion_template_id(world_seed: u64, pos: ChunkPos, layer: ChunkLayer) -> u8 {
    let seed = chunk_seed_layer(world_seed, pos, layer);
    let mut rng = StdRng::seed_from_u64(seed);
    let depth = (pos.0.abs() + pos.1.abs()) as u32;
    let template_id = match rng.gen_range(0..100u32) {
        0..=38 => TEMPLATE_HALLWAY_STRAIGHT,
        39..=51 => TEMPLATE_HALLWAY_CORNER,
        52..=61 => TEMPLATE_HALLWAY_T,
        62..=70 => TEMPLATE_INTERSECTION,
        71..=77 => TEMPLATE_ROOM_BASIC,
        78..=83 => TEMPLATE_OPEN_HALL,
        84..=88 => TEMPLATE_PILLAR_ROOM,
        89..=91 => TEMPLATE_STORAGE_ROOM,
        92..=94 => TEMPLATE_HUMID_ZONE,
        95 if depth >= 8 => TEMPLATE_BLACKOUT_ZONE,
        96 if depth >= 7 => TEMPLATE_ARCH_ROOM,
        97 if depth >= 9 => TEMPLATE_MANILA_ROOM,
        98 if depth >= 12 => TEMPLATE_RED_ROOM_WARNING,
        99 if depth >= 12 => TEMPLATE_PIT_ROOM_PLACEHOLDER,
        _ => TEMPLATE_DEAD_END,
    };
    if depth <= 2 && template_is_vertical(template_id) {
        TEMPLATE_ROOM_BASIC
    } else {
        template_id
    }
}

/// Perfil de densidad por zona.
///
/// PRIMER PASE INCREMENTAL (ADR-033): solo `ZONE_PILLAR_HALL` cambia la
/// geometría; los otros 11 `zone_kind` devuelven el perfil de capa SIN TOCAR.
/// TODO(balance): los 11 restantes son deliberadamente inertes hasta que
/// PILLAR_HALL se valide en playtest — mismo criterio que los 12 perfiles de
/// loot de la Pieza 3, primer pase no final.
fn rules_for_zone(zone_kind: u8, layer: u8) -> LayerRules {
    let base = &LAYER_PROFILES[(layer as usize).min(LAYER_PROFILES.len() - 1)];
    if zone_kind != ZONE_PILLAR_HALL {
        return base.clone();
    }

    // Salón con pilares: más zonas abiertas, más grandes y con pilares densos,
    // más erosión/anchura de pasillo (las DOS palancas de densidad de pared que
    // `generate_layer` lee de verdad). Se deriva del perfil de capa con `max` en
    // vez de fijar constantes absolutas, para no volver una capa ya más abierta
    // en una MÁS cerrada al entrar en la zona.
    //
    // Los valores son del orden de "El Caos" (LAYER_PROFILES[2]: 4 zonas de 7,
    // pilares 0.6), el perfil más abierto YA calibrado en Fase 2 — no se inventa
    // un régimen nuevo. `open_zone_size >= 6` es REQUISITO para que la Fase 4 de
    // `generate_layer` siembre pilares (generator.rs:212).
    let mut rules = base.clone();
    rules.num_open_zones = rules.num_open_zones.max(4);
    rules.open_zone_size = rules.open_zone_size.max(7);
    rules.pillar_chance = rules.pillar_chance.max(0.6);
    rules.wide_chance = rules.wide_chance.max(0.18);
    rules.erode_chance = rules.erode_chance.max(0.14);
    // Inerte por ahora (ADR-007: `wall_density` es config-only, sin cablear en
    // `generate_layer`). Se baja igualmente para que el perfil no se contradiga a
    // sí mismo si algún día se cablea.
    rules.wall_density = rules.wall_density.min(0.35);
    rules
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::generator::{generate_chunk_layer, generate_initial_structure_chunks};

    const SEEDS: [u64; 4] = [42, 7778, 1, 9_999_999];

    /// TEST OBLIGATORIO de ADR-033 Paso 2: la resolución PURA debe coincidir con
    /// el `zone_kind` que la `World` real acaba teniendo, tanto en chunks de
    /// ESTRUCTURA INICIAL como en chunks de EXPANSIÓN. Si diverge, el render y el
    /// robapieles usarían una densidad que no corresponde a la zona que el resto
    /// del juego (tint, loot, HUD de debug) reporta para ese chunk.
    #[test]
    fn resolver_matches_real_world_zone_kind() {
        for seed in SEEDS {
            // (a) Chunks de estructura inicial — incluye el cluster de spawn V30A.
            let structure_chunks = generate_initial_structure_chunks(seed);
            assert!(
                !structure_chunks.is_empty(),
                "seed {seed}: sin chunks de estructura que comparar"
            );
            for (_, chunk) in &structure_chunks {
                let layer = chunk.layer;
                assert_eq!(
                    zone_kind_for(seed, chunk.pos.0, chunk.pos.1, layer as u8),
                    chunk.layout.zone_kind,
                    "seed {seed}: chunk de ESTRUCTURA ({},{}) capa {layer} — el resolver puro no coincide con la World real",
                    chunk.pos.0,
                    chunk.pos.1
                );
            }

            // (b) Chunks de expansión — barrido amplio lejos del origen, cruzando
            // los umbrales de `depth` del sorteo (>=7, >=8, >=9, >=12) y la regla
            // anti-verticalidad de depth<=2.
            let structure_keys: std::collections::HashSet<_> = structure_chunks
                .iter()
                .map(|(_, c)| (c.pos.0, c.pos.1, c.layer))
                .collect();
            let mut expansion_checked = 0usize;
            for cx in -14..=14 {
                for cz in -14..=14 {
                    for layer in 0..2i8 {
                        if structure_keys.contains(&(cx, cz, layer)) {
                            continue; // cubierto por (a)
                        }
                        let real = generate_chunk_layer(seed, (cx, cz), layer).layout.zone_kind;
                        assert_eq!(
                            zone_kind_for(seed, cx, cz, layer as u8),
                            real,
                            "seed {seed}: chunk de EXPANSIÓN ({cx},{cz}) capa {layer} — el sorteo espejado divergió de generator.rs"
                        );
                        expansion_checked += 1;
                    }
                }
            }
            assert!(
                expansion_checked > 500,
                "seed {seed}: solo {expansion_checked} chunks de expansión comparados"
            );
        }
    }

    /// PILLAR_HALL debe cambiar la geometría de verdad; los otros 11 zone_kind
    /// deben devolver el perfil de capa INTACTO (primer pase incremental).
    #[test]
    fn only_pillar_hall_changes_the_profile() {
        for layer in 0..LAYER_PROFILES.len() as u8 {
            let base = &LAYER_PROFILES[layer as usize];
            for zone_kind in 0..12u8 {
                let rules = rules_for_zone(zone_kind, layer);
                if zone_kind == ZONE_PILLAR_HALL {
                    assert!(
                        rules.open_zone_size >= 6,
                        "capa {layer}: PILLAR_HALL con zonas de {} celdas — por debajo del mínimo que siembra pilares",
                        rules.open_zone_size
                    );
                    assert!(
                        rules.pillar_chance > 0.0,
                        "capa {layer}: PILLAR_HALL sin pilares"
                    );
                    assert!(
                        rules.num_open_zones >= base.num_open_zones
                            && rules.open_zone_size >= base.open_zone_size,
                        "capa {layer}: PILLAR_HALL quedó MÁS cerrado que su capa base"
                    );
                } else {
                    assert_eq!(
                        &rules, base,
                        "capa {layer}: zone_kind {zone_kind} no debería alterar el perfil todavía (TODO(balance))"
                    );
                }
            }
        }
    }

    /// GARANTÍA CENTRAL DE ADR-033 (opción 2): en un chunk que resuelve a
    /// PILLAR_HALL, la geometría que se RENDERIZA y la que el ROBAPIELES usa para
    /// colisionar son la misma. Es el test que se rompería si alguien cableara una
    /// de las dos rutas y olvidara la otra.
    #[test]
    fn render_and_phantom_agree_on_pillar_hall() {
        use crate::world::grid_gen::{chunk_tile_walls, tile_walls_from_grid, GridGenChunkCache};

        let mut checked = 0usize;
        for seed in SEEDS {
            // Buscar chunks que de verdad resuelvan a PILLAR_HALL con este seed.
            let hits: Vec<(i32, i32)> = (-12..=12)
                .flat_map(|cx| (-12..=12).map(move |cz| (cx, cz)))
                .filter(|&(cx, cz)| zone_kind_for(seed, cx, cz, 0) == ZONE_PILLAR_HALL)
                .take(6)
                .collect();
            assert!(
                !hits.is_empty(),
                "seed {seed}: ningún chunk PILLAR_HALL en el barrido — la prueba de concepto no tendría dónde verse"
            );

            let mut cache = GridGenChunkCache::with_rules(seed, rules_for);
            for (cx, cz) in hits {
                let rules = rules_for(seed, cx, cz, 0);
                let rendered = chunk_tile_walls(&rules, seed, cx, cz, 0);
                let phantom = tile_walls_from_grid(cache.get_or_generate(cx, cz, 0));
                assert_eq!(
                    rendered, phantom,
                    "seed {seed} chunk ({cx},{cz}) PILLAR_HALL: render y colisión del robapieles divergen"
                );
                // Y el chunk tiene que ser distinto del que daría el perfil plano,
                // o PILLAR_HALL seguiría siendo una etiqueta sin efecto.
                let flat = chunk_tile_walls(&LAYER_PROFILES[0], seed, cx, cz, 0);
                assert_ne!(
                    rendered, flat,
                    "seed {seed} chunk ({cx},{cz}): PILLAR_HALL no cambió la geometría"
                );
                checked += 1;
            }
        }
        assert!(checked > 0, "no se comprobó ningún chunk PILLAR_HALL");
    }

    /// El resolver es una función pura del seed: misma entrada → misma salida,
    /// también tras poblar la memoización. Es el contrato del que depende que
    /// todos los peers generen la misma geometría.
    #[test]
    fn resolver_is_deterministic_across_calls() {
        for seed in SEEDS {
            for (cx, cz) in [(0, 0), (3, -7), (11, 11), (-9, 4)] {
                let a = zone_kind_for(seed, cx, cz, 0);
                let b = zone_kind_for(seed, cx, cz, 0);
                assert_eq!(a, b, "seed {seed} ({cx},{cz}): resolver no determinista");
                assert_eq!(
                    rules_for(seed, cx, cz, 0),
                    rules_for(seed, cx, cz, 0),
                    "seed {seed} ({cx},{cz}): rules_for no determinista"
                );
            }
        }
    }
}
