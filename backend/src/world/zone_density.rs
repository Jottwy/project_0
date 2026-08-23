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
    TEMPLATE_INTERSECTION, TEMPLATE_MANILA_ROOM, TEMPLATE_OFFICE, TEMPLATE_OPEN_HALL,
    TEMPLATE_PILLAR_ROOM, TEMPLATE_PIT_ROOM_PLACEHOLDER, TEMPLATE_RED_ROOM_WARNING,
    TEMPLATE_ROOM_BASIC, TEMPLATE_STORAGE_ROOM,
};
use crate::world::chunk::{ChunkLayer, ZONE_OFFICE, ZONE_PILLAR_HALL};
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
        0..=34 => TEMPLATE_HALLWAY_STRAIGHT,
        35..=38 => TEMPLATE_OFFICE, // ver el comentario del original, en `world::generator`
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
/// PASE INCREMENTAL (ADR-033 + OFFICE): `ZONE_PILLAR_HALL` y `ZONE_OFFICE`
/// cambian la geometría; los otros 11 `zone_kind` devuelven el perfil de capa
/// SIN TOCAR. TODO(balance): los 11 restantes siguen deliberadamente inertes —
/// mismo criterio que los perfiles de loot de la Pieza 3, pase no final.
fn rules_for_zone(zone_kind: u8, layer: u8) -> LayerRules {
    let base = &LAYER_PROFILES[(layer as usize).min(LAYER_PROFILES.len() - 1)];
    if zone_kind == ZONE_OFFICE {
        return office_rules(base);
    }
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
    // `generate_layer` siembre pilares (el umbral `eff_sz_x.min(eff_sz_z) >= 6` en
    // `grid_gen::generator::generate_layer`). Referencia por SÍMBOLO y no por número
    // de línea a propósito: los números se pudren en el primer commit, y además hay
    // dos `generator.rs` en el árbol y este módulo importa de los dos.
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
    // Override EXPLÍCITO, no heredado: la identidad de PILLAR_HALL es la
    // retícula de columnas sobre una zona Open — incompatible con perímetro
    // sellado (SealedRoom/CorridorSpine). Si el perfil de capa base activa
    // RoomType (layer 0 en producción, desde esta sesión), un chunk
    // PILLAR_HALL heredaría esos pesos SIN QUERERLO, produciendo salas
    // selladas con retícula de pilares por encima — y, peor, exactamente el
    // caso borde de una SealedRoom incomunicada (entradas que no llegan al
    // maze) ya se disparó de verdad en un chunk PILLAR_HALL real durante el
    // experimento previo (chunk (9,4), seed 42: sala colapsada a 25 celdas
    // transitables, por debajo del suelo de 30 que exige
    // `pillar_hall_chunks_stay_connected_and_non_degenerate`). Forzar Open
    // aquí es la corrección, no una mitigación temporal.
    // Literal, no `default_room_type_weights()`: esa función es `pub(super)`
    // dentro de `grid_gen` (visible a sus descendientes), y `zone_density` es
    // un módulo hermano, no descendiente — no la alcanza. El tuple es el
    // mismo valor documentado y estable de "RoomType::Open siempre".
    rules.room_type_weights = (1.0, 0.0, 0.0);
    // Override EXPLÍCITO, mismo motivo que `room_type_weights` arriba: la
    // partición 2×2 (sesión de sub-regiones) puede sortear SealedRoom/
    // CorridorSpine por cuadrante, incompatible con la retícula de pilares
    // de PILLAR_HALL igual que el modo legacy. Si el perfil de capa base
    // activa `subregion_grid` (layer 0 en producción), un chunk PILLAR_HALL
    // lo heredaría sin querer — se fuerza a `false` aquí, no se mitiga.
    rules.subregion_grid = false;
    rules
}

/// Planta de oficinas: el espejo invertido de PILLAR_HALL.
///
/// PILLAR_HALL es una nave ABIERTA con columnas; OFFICE es una planta
/// COMPARTIMENTADA sin columnas. Los dos usan exactamente la misma palanca
/// (reconfigurar `LayerRules`, cero estampadores nuevos) y los dos fuerzan sus
/// campos de identidad con `=` explícito en vez de `max`/`min`: heredar del
/// perfil de capa produciría un chunk que no es ninguna de las dos cosas.
fn office_rules(base: &LayerRules) -> LayerRules {
    let mut rules = base.clone();

    // IDENTIDAD 1 — UNA sala de 14×14, no cuatro cuadrantes (ADR-087 paso 1).
    //
    // La redacción anterior decía que `subregion_grid` "es literalmente la planta
    // de oficinas que se quiere". **Medido, no lo era.** A footprint IDÉNTICO
    // (49,0 % del chunk en los dos casos), una sola sala de 13×13 deja 37,5 % de
    // interior pisable contra el 31,2 % de los cuatro cuadrantes: cinco puntos y
    // pico, sin una línea de código, porque cuatro salas pagan cuatro anillos
    // `SealedWall` y una paga uno. Ese es el argumento del perímetro compartido
    // de ADR-086, que era correcto — lo que estaba mal era dónde se aplicaba.
    //
    // **13 Y NO 14, Y NO ES COSMÉTICO.** Para una `SealedRoom` los dos dan el
    // MISMO rect —`tile_aligned_size` redondea 13 a 14— así que la cobertura no
    // se mueve. Lo que cambia es la zona `Open` del 20 %, que NO se alinea a tile
    // (`has_sealed_perimeter` es falso): a 14 deja una tira de una celda contra
    // el borde que el maze no alcanza. Medido sobre 784 chunks × 4 capas: **14 da
    // 1 chunk incomunicado, 13 da 0.** Aislado el mecanismo poniendo los pesos al
    // extremo — todo sellado da 0 a los dos tamaños, todo Open da 7 y 8. El
    // riesgo residual vive en la cuota Open y lo vigila
    // `office_chunks_stay_connected_and_non_degenerate`.
    //
    // 13 y no 18: con 18×18 el footprint sube al 81 % y el interior al 64,5 %,
    // pero el paso real se hunde al **0,8 %** — el maze ha desaparecido y los
    // chunks vecinos se conectan sala contra sala sin corredor. Eso no es una
    // planta de oficinas, es un almacén del tamaño del chunk — y encima deja
    // **217 chunks incomunicados de 784**. Un número que sube demasiado es tan
    // malo como uno que baja; ver la verificación (c) de ADR-087.
    //
    // `subregion_grid` NO se retira del motor: sigue vivo para ADR-057 y para
    // cualquier otra zona. Lo que se retira es que OFFICE lo use.
    rules.subregion_grid = false;
    rules.num_open_zones = 1;
    // IDENTIDAD 2 — mayoría de despachos con perímetro sellado y 1-3 puertas
    // (`carve_sealed_room_entrances`), con un 20% de cuadrante Open que hace de
    // sala común. Sin `CorridorSpine`: su ancho fijo de 2 celdas ya lo aporta el
    // maze circundante, y un spine dentro de un cuadrante leería como pasillo
    // duplicado.
    rules.room_type_weights = (0.2, 0.8, 0.0);
    // IDENTIDAD 3 — sin columnas. Es lo único que separa visualmente un
    // cuadrante OFFICE grande de uno PILLAR_HALL. El bucle de retícula de Fase 4
    // sigue corriendo (el umbral `min(eff_sz) >= 6` se cumple con sz=6) y sigue
    // consumiendo sus draws del stream por cuadrante — no se salta, simplemente
    // nunca coloca. Mantenerlo así deja el stream idéntico a cualquier otro
    // perfil con `subregion_grid`, que es lo que hace comparables las huellas.
    rules.pillar_chance = 0.0;

    // HISTÓRICO — lo de abajo razona sobre la partición en cuadrantes, que
    // ADR-087 paso 1 ya no usa. Se conserva porque su conclusión es la que
    // sostiene la identidad 1 de arriba: el perímetro sellado cuesta 1 celda
    // cueste lo que cueste, así que más salas pequeñas gastan MÁS pared. Lo que
    // ADR-087 hace es llevar esa misma conclusión hasta el final —una sola sala—
    // en vez de quedarse en cuatro.
    //
    // SE QUEDABA EN LA PARTICIÓN 2×2 DE ADR-057, y era una decisión MEDIDA que
    // contradice la hipótesis con la que se construyó `subregion_divisions`.
    //
    // La hipótesis era: como el 62% de los paneles de un chunk OFFICE son pasillo
    // del maze, partir en 3×3 (nueve despachos de 5×5 m en vez de cuatro de
    // 10×10) reduciría el pasillo. **Medido sobre 144 chunks, hace lo contrario:**
    // el espacio pisable que cae DENTRO de una sala baja de 44.5% a 38.9%.
    //
    // El motivo es el perímetro sellado, que es de 1 celda cueste lo que cueste:
    // un rect de 6×6 tiene 16 celdas de interior sobre 36 (44% útil), uno de 4×4
    // tiene 4 sobre 16 (25% útil). Más salas pequeñas gastan MÁS presupuesto de
    // chunk en pared y dejan menos suelo de oficina, no más.
    //
    // El mecanismo de 3×3 queda construido, probado e INERTE por si un diseño
    // futuro lo quiere por ESCALA (una sala de 5×5 m se lee más a oficina que una
    // de 10×10, aunque haya menos metros de sala) — pero eso es una decisión de
    // lectura, no de densidad, y no se toma a partir de este número.
    // ADR-087 paso 1: 14×14, el tamaño medido. Ya NO es un fallback documental —
    // con `subregion_grid = false` este es el tamaño real de la única sala del
    // chunk, y `subregion_fill_band` no lo pisa porque su camino no corre.
    rules.open_zone_size = 13;
    rules.open_zone_size_x = Some(13);
    rules.open_zone_size_z = Some(13);

    // IDENTIDAD 4 — cobertura de sala (enmienda 2026-08-09, sesión de
    // `office_room_coverage_report`).
    //
    // ⚠️ TODO ESTE PÁRRAFO ES HISTÓRICO: describe la configuración de cuatro
    // cuadrantes que ADR-087 paso 1 retiró. La cifra viva es la de la identidad 1.
    //
    // ⚠️ LOS DOS NÚMEROS DE PISABLE DE ESTE PÁRRAFO SON DE ANTES DE
    // `subregion_fill_band`, y llevan desde entonces junto a un footprint que sí
    // se actualizó — leídos juntos parecen todos actuales y no lo son. Medido de
    // nuevo el 2026-08-23 con `office_room_coverage_report`, que vuelve a existir
    // y ahora imprime los dos caminos: **con `fill_band` (o sea, hoy) el interior
    // pisable es 31,2 % y el pasillo 36,9 %**, no 21,9 % y 54 %. El camino viejo
    // reproduce 21,4 % y 53,7 %, que confirma de dónde salían. Ver ADR-086
    // enmienda 1; el guard determinista es
    // `office_baseline_matches_the_numbers_adr_086_argues_from`.
    //
    // MEDIDO con `sz=6` fijo: footprint 36% del
    // chunk (4 cuadrantes × 36 celdas), interior pisable de sala real 21.9% de
    // media (17-36% según Open/Sealed) — el pasillo es MÁS de la mitad de lo
    // pisable (54%), coherente con el 62% de paneles-pasillo ya medido en
    // Unity. Causa: `sz=6` desperdicia las 2 celdas extra que la banda LOW
    // ([2,10), 8 celdas) tiene de sobra sobre la banda HIGH ([12,18), 6 celdas)
    // — un tamaño fijo tiene que caber en la más estrecha de las dos.
    //
    // `subregion_fill_band` (ADR-057, enmienda 2026-08-09) rompe ese techo:
    // cada cuadrante ocupa el ancho ENTERO de su propia banda, así que los
    // cuadrantes de banda LOW crecen a 8×8 mientras los de banda HIGH se
    // quedan en 6×6 — asimétrico A PROPÓSITO, no un descuido: la banda LOW
    // mide 8 por el mismo split de bandas que ADR-057 ya documentó como no
    // simétrico. Con esto el footprint sube de 36% a ~49% del chunk (medido).
    //
    // Perímetro de sala más fino (quitar el anillo `SealedWall` y colisionar
    // solo contra el borde de tile, como ya renderiza Unity) se DESCARTA para
    // esta enmienda: cambia la semántica de colisión del robapieles y la
    // identidad entera de `SealedRoom`/`carve_sealed_room_entrances` — mayor
    // ganancia potencial, pero es un rediseño que merece su propio ADR si
    // algún día se retoma, no una mejora de esta sesión.
    // Inerte desde ADR-087 paso 1 (`subregion_grid = false` ⇒ su camino no corre).
    // Se deja puesto y no se borra: si algún día OFFICE volviera a cuadrantes,
    // quitarlo aquí sería perder en silencio los 13 puntos de footprint que esta
    // línea compró en agosto.
    rules.subregion_fill_band = true;

    // `wide_chance`/`erode_chance` se dejan TAL CUAL los del perfil de capa: un
    // pasillo de oficina es estrecho, así que no hay motivo para abrirlos como
    // hace PILLAR_HALL, y cerrarlos por debajo del perfil convertiría una capa
    // ya densa en intransitable. `num_open_zones` SÍ se toca ahora (identidad 1):
    // con `subregion_grid = false` es él quien manda, y vale 1.
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

    /// PILLAR_HALL debe cambiar la geometría de verdad Y forzar
    /// `room_type_weights` a `(1,0,0)` (Open) SIN IMPORTAR lo que diga el
    /// perfil de capa base — su identidad es la retícula de columnas sobre
    /// una zona Open, incompatible con perímetro `SealedWall`. OFFICE cambia
    /// la suya en sentido contrario (compartimentada, sin columnas). Los
    /// otros 11 zone_kind deben devolver el perfil de capa INTACTO, weights
    /// incluidos (pase incremental).
    #[test]
    fn only_pillar_hall_and_office_change_the_profile() {
        for layer in 0..LAYER_PROFILES.len() as u8 {
            let base = &LAYER_PROFILES[layer as usize];
            for zone_kind in 0..=ZONE_OFFICE {
                let rules = rules_for_zone(zone_kind, layer);
                if zone_kind == ZONE_OFFICE {
                    // ADR-087 paso 1: UNA sala de 13, no cuatro cuadrantes.
                    assert!(
                        !rules.subregion_grid,
                        "capa {layer}: OFFICE dejó de usar subregion_grid (ADR-087)"
                    );
                    assert_eq!(
                        (
                            rules.num_open_zones,
                            rules.open_zone_size_x,
                            rules.open_zone_size_z
                        ),
                        (1, Some(13), Some(13)),
                        "capa {layer}: OFFICE debe estampar UNA sala de 13×13"
                    );
                    assert_eq!(
                        rules.room_type_weights,
                        (0.2, 0.8, 0.0),
                        "capa {layer}: OFFICE heredó {:?} del perfil base",
                        rules.room_type_weights
                    );
                    assert_eq!(
                        rules.pillar_chance, 0.0,
                        "capa {layer}: OFFICE con columnas — es la identidad de PILLAR_HALL, no la suya"
                    );
                    // Enmienda 2026-08-09 (cobertura de sala): OFFICE deja de leer
                    // `open_zone_size_x/z` — cada cuadrante ocupa el ancho ENTERO
                    // de su banda (asimétrico: 8 en LOW, 6 en HIGH). El campo se
                    // conserva como fallback documental (ver `office_rules`), así
                    // que se comprueba igual que antes por si el flag se apaga
                    // algún día — con `subregion_fill_band` en `true` esta
                    // comprobación es del respaldo, no del camino activo.
                    assert!(
                        rules.subregion_fill_band,
                        "capa {layer}: OFFICE debe forzar subregion_fill_band=true \
                         (enmienda de cobertura de sala, 2026-08-09)"
                    );
                    // El tamaño NO es estético: la banda HIGH de
                    // `subregion_origin_slots` solo admite <= 6. Con 8 (lo que
                    // daría `open_zone_size: 7` de layer 2 tras alinear) los
                    // tres cuadrantes de banda HIGH se saltarían EN SILENCIO.
                    // Ancho de banda LITERAL y no leído de `subregion_band_bounds`:
                    // esa función es `pub(super)` dentro de `grid_gen` y
                    // `zone_density` es un módulo hermano, no descendiente — mismo
                    // caso que `default_room_type_weights`, y se resuelve igual,
                    // con el literal documentado en vez de ensanchar la visibilidad
                    // de un interno del generador para un test.
                    //
                    // El techo del tamaño ya NO es la banda de un cuadrante
                    // (ADR-087 paso 1 apagó `subregion_grid` para OFFICE): es el
                    // ancho utilizable del chunk. Y el límite de verdad no es
                    // geométrico sino de CONECTIVIDAD — medido, 18 deja 217 de 784
                    // chunks incomunicados y 16 deja 2. Quien vigila eso es
                    // `office_chunks_stay_connected_and_non_degenerate`; aquí solo
                    // se comprueba que la sala cabe con maze alrededor.
                    assert!(
                        rules.open_zone_size_x.unwrap() <= 14,
                        "capa {layer}: sala de {:?} — por encima de 14 el maze que                          queda alrededor empieza a dejar bolsillos incomunicados",
                        rules.open_zone_size_x
                    );
                    assert_eq!(rules.open_zone_size_x, rules.open_zone_size_z);
                    assert_eq!(
                        (rules.wide_chance, rules.erode_chance),
                        (base.wide_chance, base.erode_chance),
                        "capa {layer}: OFFICE no debe tocar las palancas de densidad del maze"
                    );
                    continue;
                }
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
                    assert_eq!(
                        rules.room_type_weights,
                        (1.0, 0.0, 0.0),
                        "capa {layer}: PILLAR_HALL debe forzar RoomType::Open, heredó {:?} del perfil base",
                        rules.room_type_weights
                    );
                    assert!(
                        !rules.subregion_grid,
                        "capa {layer}: PILLAR_HALL debe forzar subregion_grid=false, heredó true del perfil base"
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
                // o PILLAR_HALL seguiría siendo una etiqueta sin efecto. ADR-033/
                // Pillar: comparar SOLO el nibble bajo (aristas) — desde que el
                // nibble alto existe, comparar el byte completo se volvería
                // trivialmente satisfacible por los bits de pilar sin que la
                // GEOMETRÍA de aristas hubiera cambiado en absoluto (falso verde).
                let flat = chunk_tile_walls(&LAYER_PROFILES[0], seed, cx, cz, 0);
                let rendered_edges: Vec<u8> = rendered.iter().flatten().map(|b| b & 0x0F).collect();
                let flat_edges: Vec<u8> = flat.iter().flatten().map(|b| b & 0x0F).collect();
                assert_ne!(
                    rendered_edges, flat_edges,
                    "seed {seed} chunk ({cx},{cz}): PILLAR_HALL no cambió la geometría de aristas"
                );
                checked += 1;
            }
        }
        assert!(checked > 0, "no se comprobó ningún chunk PILLAR_HALL");
    }

    /// ADR-033/Pillar (b): en un chunk PILLAR_HALL real (mismo barrido que
    /// `render_and_phantom_agree_on_pillar_hall`), el nibble alto del bitmask
    /// coincide CELDA A CELDA con las `CellType::Pillar` reales del grid — no
    /// solo "hay pilares en algún sitio", sino la sub-celda EXACTA. Es la
    /// verificación de que la enmienda "Opción (c)" no introdujo el mismo
    /// riesgo de desalineación que descartó la Opción (b) (props client-side).
    #[test]
    // `tx`/`tz` indexan `walls` (2 ejes) Y derivan `x0/x1/z0/z1` para leer el
    // grid original; no hay un `enumerate()` único que cubra ambos usos.
    #[allow(clippy::needless_range_loop)]
    fn pillar_nibble_matches_real_pillar_cells_on_pillar_hall_chunks() {
        use crate::world::grid_gen::{
            tile_walls_from_grid, CellType, PILLAR_NE, PILLAR_NW, PILLAR_SE, PILLAR_SW,
            TILES_PER_SIDE,
        };

        let mut checked_chunks = 0usize;
        let mut checked_subcells = 0usize;
        for seed in SEEDS {
            let hits: Vec<(i32, i32)> = (-12..=12)
                .flat_map(|cx| (-12..=12).map(move |cz| (cx, cz)))
                .filter(|&(cx, cz)| zone_kind_for(seed, cx, cz, 0) == ZONE_PILLAR_HALL)
                .take(6)
                .collect();

            for (cx, cz) in hits {
                let rules = rules_for(seed, cx, cz, 0);
                let out =
                    crate::world::grid_gen::generate_chunk_layer(&rules, seed, (cx, cz), 0, &[]);
                let walls = tile_walls_from_grid(&out.grid);

                for tx in 0..TILES_PER_SIDE {
                    for tz in 0..TILES_PER_SIDE {
                        let (x0, x1, z0, z1) = (tx * 2, tx * 2 + 1, tz * 2, tz * 2 + 1);
                        let expect =
                            |x: usize, z: usize| out.grid.get(x, z).kind() == CellType::Pillar;
                        let byte = walls[tx][tz];
                        assert_eq!(
                            (byte & PILLAR_NW) != 0, expect(x0, z0),
                            "seed {seed} chunk ({cx},{cz}) tile ({tx},{tz}) NW: nibble no coincide con la celda real"
                        );
                        assert_eq!(
                            (byte & PILLAR_NE) != 0, expect(x1, z0),
                            "seed {seed} chunk ({cx},{cz}) tile ({tx},{tz}) NE: nibble no coincide con la celda real"
                        );
                        assert_eq!(
                            (byte & PILLAR_SW) != 0, expect(x0, z1),
                            "seed {seed} chunk ({cx},{cz}) tile ({tx},{tz}) SW: nibble no coincide con la celda real"
                        );
                        assert_eq!(
                            (byte & PILLAR_SE) != 0, expect(x1, z1),
                            "seed {seed} chunk ({cx},{cz}) tile ({tx},{tz}) SE: nibble no coincide con la celda real"
                        );
                        checked_subcells += 4;
                    }
                }
                checked_chunks += 1;
            }
        }
        assert!(
            checked_chunks > 0,
            "no se comprobó ningún chunk PILLAR_HALL"
        );
        assert!(checked_subcells > 0, "no se comprobó ninguna sub-celda");
    }

    /// El perfil PILLAR_HALL debe seguir produciendo chunks JUGABLES.
    ///
    /// Hueco de cobertura real que abre ADR-033: `all_walkable_cells_are_connected`
    /// y `sealing_preserves_main_component` barren `LAYER_PROFILES`, así que
    /// NINGUNO toca el perfil derivado de PILLAR_HALL. Un perfil más abierto
    /// cambia el sustrato del sellado de bolsillos (la calibración de Fase 2 ya
    /// avisa de que zonas sobredimensionadas borran el laberinto), así que la
    /// conectividad hay que probarla explícitamente sobre el perfil nuevo.
    #[test]
    fn pillar_hall_chunks_stay_connected_and_non_degenerate() {
        use crate::world::grid_gen::{generate_chunk_layer, LayerGrid, CHUNK_CELLS};

        fn flood(grid: &LayerGrid) -> (usize, usize) {
            let (mut total, mut start) = (0usize, None);
            for z in 0..CHUNK_CELLS {
                for x in 0..CHUNK_CELLS {
                    if grid.get(x, z).is_walkable() {
                        total += 1;
                        start.get_or_insert((x, z));
                    }
                }
            }
            let Some(start) = start else { return (0, 0) };
            let mut visited = vec![false; CHUNK_CELLS * CHUNK_CELLS];
            visited[start.1 * CHUNK_CELLS + start.0] = true;
            let mut queue = vec![start];
            let mut reached = 0usize;
            while let Some((x, z)) = queue.pop() {
                reached += 1;
                for (dx, dz) in [(-1i32, 0i32), (1, 0), (0, -1), (0, 1)] {
                    let (nx, nz) = (x as i32 + dx, z as i32 + dz);
                    if !LayerGrid::in_bounds(nx, nz) {
                        continue;
                    }
                    let (nx, nz) = (nx as usize, nz as usize);
                    if !visited[nz * CHUNK_CELLS + nx] && grid.get(nx, nz).is_walkable() {
                        visited[nz * CHUNK_CELLS + nx] = true;
                        queue.push((nx, nz));
                    }
                }
            }
            (reached, total)
        }

        let mut checked = 0usize;
        for seed in SEEDS {
            for cx in -12..=12 {
                for cz in -12..=12 {
                    if zone_kind_for(seed, cx, cz, 0) != ZONE_PILLAR_HALL {
                        continue;
                    }
                    let rules = rules_for(seed, cx, cz, 0);
                    let out = generate_chunk_layer(&rules, seed, (cx, cz), 0, &[]);
                    let (reached, total) = flood(&out.grid);
                    assert_eq!(
                        reached, total,
                        "seed {seed} chunk ({cx},{cz}) PILLAR_HALL: {reached} de {total} celdas alcanzables — zonas aisladas"
                    );
                    // Mismo suelo absoluto que `sealing_preserves_main_component`:
                    // un sellado catastrófico dejaría un bolsillo minúsculo.
                    assert!(
                        total >= 30,
                        "seed {seed} chunk ({cx},{cz}) PILLAR_HALL: solo {total} celdas transitables"
                    );
                    checked += 1;
                }
            }
        }
        assert!(
            checked > 20,
            "solo {checked} chunks PILLAR_HALL comprobados"
        );
    }

    /// OFFICE produce de verdad una PLANTA: 4 sub-regiones por chunk, mayoría
    /// con perímetro sellado, y CERO columnas. Se ejercita el perfil DIRECTO
    /// (no barriendo chunks) porque el sorteo todavía no puede emitir OFFICE —
    /// el flip de banda es un commit posterior. Cuando llegue, este test sigue
    /// siendo válido tal cual: prueba el perfil, no su frecuencia.
    #[test]
    fn office_profile_stamps_sealed_rooms_and_never_pillars() {
        use crate::world::grid_gen::{generate_chunk_layer, CellType, CHUNK_CELLS};

        const WIRE_SEALED_ROOM: u8 = 1;
        let (mut zones_seen, mut sealed_seen) = (0usize, 0usize);
        for seed in SEEDS {
            for layer in 0..LAYER_PROFILES.len() as u8 {
                let rules = rules_for_zone(ZONE_OFFICE, layer);
                for (cx, cz) in [(0, 0), (3, -7), (11, 11), (-9, 4), (-2, 13)] {
                    let out = generate_chunk_layer(&rules, seed, (cx, cz), layer as i32, &[]);
                    // 4 cuadrantes, presencia 1.0, y `sz = 6` cabe en las dos
                    // bandas ⇒ la guarda `count == 0` no puede dispararse. Si
                    // alguien cambia el tamaño y rompe la banda HIGH, esto lo
                    // caza en vez de dejar medio chunk sin estampar en silencio.
                    // Derivado de las divisiones, no un literal: 3×3 = 9. Con
                    // presencia 1.0 y un tamaño que cabe en la banda, la guarda
                    // `count == 0` no puede dispararse, así que salen TODAS.
                    // ADR-087 paso 1: `num_open_zones = 1`, así que sale UNA y
                    // solo una. Derivado del campo y no un literal, igual que
                    // antes lo derivaba de `subregion_divisions`.
                    let expected = rules.num_open_zones as usize;
                    assert_eq!(
                        out.room_zones.len(),
                        expected,
                        "seed {seed} capa {layer} chunk ({cx},{cz}): OFFICE estampó {} zonas, no {expected}",
                        out.room_zones.len()
                    );
                    zones_seen += out.room_zones.len();
                    sealed_seen += out
                        .room_zones
                        .iter()
                        .filter(|z| z.kind == WIRE_SEALED_ROOM)
                        .count();

                    let pillars = out
                        .grid
                        .cells()
                        .iter()
                        .filter(|c| c.kind() == CellType::Pillar)
                        .count();
                    assert_eq!(
                        pillars, 0,
                        "seed {seed} capa {layer} chunk ({cx},{cz}): OFFICE colocó {pillars} columnas"
                    );
                    assert_eq!(out.grid.cells().len(), CHUNK_CELLS * CHUNK_CELLS);
                }
            }
        }
        // 80% esperado sobre las 320 sub-regiones del barrido (4 seeds × 4
        // capas × 5 chunks × 4 cuadrantes); el suelo (50%) es holgado a
        // propósito — verifica que el peso está CABLEADO y no invertido, no
        // calibra su valor.
        assert!(
            sealed_seen * 2 > zones_seen,
            "solo {sealed_seen} de {zones_seen} sub-regiones OFFICE salieron selladas — el peso 0.8 no está llegando"
        );
    }

    /// Mismo hueco de cobertura que abrió PILLAR_HALL, ahora para OFFICE:
    /// `all_walkable_cells_are_connected` barre `LAYER_PROFILES`, así que
    /// ningún test toca el perfil derivado. Cuatro salas selladas por chunk son
    /// el caso MÁS agresivo que ha estampado este generador — si alguna
    /// combinación deja un bolsillo incomunicado, sale aquí.
    #[test]
    fn office_chunks_stay_connected_and_non_degenerate() {
        use crate::world::grid_gen::{generate_chunk_layer, LayerGrid, CHUNK_CELLS};

        fn flood(grid: &LayerGrid) -> (usize, usize) {
            let (mut total, mut start) = (0usize, None);
            for z in 0..CHUNK_CELLS {
                for x in 0..CHUNK_CELLS {
                    if grid.get(x, z).is_walkable() {
                        total += 1;
                        start.get_or_insert((x, z));
                    }
                }
            }
            let Some(start) = start else { return (0, 0) };
            let mut visited = vec![false; CHUNK_CELLS * CHUNK_CELLS];
            visited[start.1 * CHUNK_CELLS + start.0] = true;
            let mut queue = vec![start];
            let mut reached = 0usize;
            while let Some((x, z)) = queue.pop() {
                reached += 1;
                for (dx, dz) in [(-1i32, 0i32), (1, 0), (0, -1), (0, 1)] {
                    let (nx, nz) = (x as i32 + dx, z as i32 + dz);
                    if !LayerGrid::in_bounds(nx, nz) {
                        continue;
                    }
                    let (nx, nz) = (nx as usize, nz as usize);
                    if !visited[nz * CHUNK_CELLS + nx] && grid.get(nx, nz).is_walkable() {
                        visited[nz * CHUNK_CELLS + nx] = true;
                        queue.push((nx, nz));
                    }
                }
            }
            (reached, total)
        }

        let mut checked = 0usize;
        for seed in SEEDS {
            for layer in 0..LAYER_PROFILES.len() as u8 {
                let rules = rules_for_zone(ZONE_OFFICE, layer);
                for cx in -3..=3 {
                    for cz in -3..=3 {
                        let out = generate_chunk_layer(&rules, seed, (cx, cz), layer as i32, &[]);
                        let (reached, total) = flood(&out.grid);
                        assert_eq!(
                            reached, total,
                            "seed {seed} capa {layer} chunk ({cx},{cz}) OFFICE: {reached} de {total} celdas alcanzables — despacho incomunicado"
                        );
                        // Mismo suelo absoluto que PILLAR_HALL y que
                        // `sealing_preserves_main_component`.
                        assert!(
                            total >= 30,
                            "seed {seed} capa {layer} chunk ({cx},{cz}) OFFICE: solo {total} celdas transitables"
                        );
                        checked += 1;
                    }
                }
            }
        }
        assert!(checked > 100, "solo {checked} chunks OFFICE comprobados");
    }

    /// El resolver PURO llega a OFFICE, y cuando llega devuelve el perfil de
    /// OFFICE — no el de la capa. Espejo, del lado del render/robapieles, de
    /// `office_is_reachable_from_the_expansion_lottery` (`layout_grammars.rs`),
    /// que cubre el sorteo original. Los dos juntos son lo que garantiza que el
    /// flip aterrizó ENTERO: `resolver_matches_real_world_zone_kind` ya prueba
    /// que ambos sorteos coinciden, pero solo estos prueban que además EMITEN.
    #[test]
    fn office_reaches_the_resolver_and_brings_its_own_profile() {
        let mut hits = 0usize;
        for seed in SEEDS {
            for cx in -10..=10 {
                for cz in -10..=10 {
                    for layer in 0..LAYER_PROFILES.len() as u8 {
                        if zone_kind_for(seed, cx, cz, layer) != ZONE_OFFICE {
                            continue;
                        }
                        hits += 1;
                        let rules = rules_for(seed, cx, cz, layer);
                        assert_eq!(rules, rules_for_zone(ZONE_OFFICE, layer),
                            "seed {seed} chunk ({cx},{cz}) capa {layer}: resuelve a OFFICE pero `rules_for` no devolvió su perfil");
                        // Campos que difieren del perfil de capa en las CUATRO
                        // capas. `subregion_grid`/`pillar_chance` no valdrían:
                        // `LAYER_PROFILES[0]` ya los trae con esos valores, así
                        // que en layer 0 el assert sería verde sin que
                        // `office_rules` hubiera intervenido.
                        assert_eq!(rules.room_type_weights, (0.2, 0.8, 0.0));
                        assert_eq!(rules.open_zone_size_x, Some(13));
                    }
                }
            }
        }
        assert!(
            hits > 50,
            "solo {hits} chunks resolvieron a OFFICE en el barrido — el flip no llegó al resolver"
        );
    }

    /// SONDA DE COORDENADAS, no test (`#[ignore]`). Lista las HABITACIONES CONSTRUIBLES más cercanas
    /// al origen, con el centro de cada una.
    ///
    /// Existe porque "el chunk (5,2) tiene habitación" no sirve para ir andando. El centro de la
    /// habitación SÍ es seguro por construcción: el tallado deja sus 3 × 3 tiles huecos, así que a
    /// diferencia del centro de un chunk cualquiera no puede caer dentro de una pared.
    ///
    /// `WORLD_SEED=<n> cargo test list_buildable_rooms -- --ignored --nocapture`
    #[test]
    #[ignore = "sonda: imprime, no afirma; correr con -- --ignored --nocapture"]
    fn list_buildable_rooms() {
        use crate::world::grid_gen::{room_in_chunk, CELL_SIZE_M, CHUNK_CELLS, ROOM_SIZE_M};

        let seed: u64 = std::env::var("WORLD_SEED")
            .ok()
            .and_then(|v| v.parse().ok())
            .unwrap_or(42);

        let chunk_size = CHUNK_CELLS as f32 * CELL_SIZE_M;
        let mut found: Vec<(f32, i32, i32, f32, f32)> = Vec::new();
        for cx in -12..=12 {
            for cz in -12..=12 {
                let Some(plan) = room_in_chunk(seed, cx, cz, 0) else {
                    continue;
                };
                let (x0, z0) = plan.cell_origin();
                let cxm = cx as f32 * chunk_size + x0 as f32 * CELL_SIZE_M + ROOM_SIZE_M * 0.5;
                let czm = cz as f32 * chunk_size + z0 as f32 * CELL_SIZE_M + ROOM_SIZE_M * 0.5;
                found.push(((cxm * cxm + czm * czm).sqrt(), cx, cz, cxm, czm));
            }
        }
        found.sort_by(|a, b| a.0.partial_cmp(&b.0).unwrap());

        println!(
            "BUILDROOM seed={seed} rooms={} (radio de 12 chunks)",
            found.len()
        );
        for (dist, cx, cz, x, z) in &found {
            println!(
                "BUILDROOM chunk=({cx},{cz}) centro=({x:.1}, 0.0, {z:.1}) lado={ROOM_SIZE_M:.0}m dist={dist:.0}m"
            );
        }
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

    /// GUARDA REAL (enmienda 2026-08-09, cobertura de sala). Mide, sobre una
    /// muestra fija, el footprint de sala (suma de rects de `room_zones` /
    /// área del chunk) con `office_rules` tal cual (que fuerza
    /// `subregion_fill_band = true`) contra la MISMA regla con el flag
    /// forzado de vuelta a `false`. `office_rules` YA documenta el número
    /// (36% → 49%, medido sobre 256 chunks con `office_room_coverage_report`,
    /// instrumentación efímera ya retirada); este test lo convierte en guard
    /// determinista para que un futuro cambio a `stamp_subregion_grid` o a
    /// las bandas de ADR-057 que borre la ganancia falle aquí, no en
    /// producción.
    #[test]
    fn office_fill_band_grows_room_footprint_over_the_fixed_size_path() {
        use crate::world::grid_gen::generate_layer;

        // Ya NO se monta desde `office_rules`: ADR-087 paso 1 apagó `subregion_grid`
        // para OFFICE, así que pasar por ahí probaría un camino que esa zona no
        // recorre. El mecanismo SIGUE VIVO para ADR-057 y para cualquier otro
        // perfil, y esta guarda sigue siendo suya — solo que ahora se construye la
        // configuración de cuadrantes a mano, que es de quien era el guard desde
        // el principio.
        let mut with_fill = LAYER_PROFILES[0].clone();
        with_fill.subregion_grid = true;
        with_fill.subregion_presence = 1.0;
        with_fill.subregion_divisions = 2;
        with_fill.open_zone_size = 6;
        with_fill.open_zone_size_x = Some(6);
        with_fill.open_zone_size_z = Some(6);
        // Sin `CorridorSpine`: su ancho fijo cambia el eff_sz del cuadrante y el
        // footprint dejaría de ser 4 × sz². `LAYER_PROFILES[0]` trae (0.5,0.3,0.2).
        with_fill.room_type_weights = (0.2, 0.8, 0.0);
        with_fill.subregion_fill_band = true;

        let mut without_fill = with_fill.clone();
        without_fill.subregion_fill_band = false; // vuelve al sz=6 fijo de antes

        let footprint_of = |rules: &LayerRules, seed: u64, cx: i32| -> u32 {
            let out = generate_layer(rules, seed, (cx, cx * 7 + 3), 0, &[]);
            out.room_zones
                .iter()
                .map(|z| (z.x1 - z.x0) as u32 * (z.z1 - z.z0) as u32)
                .sum()
        };

        // subregion_presence = 1.0 ⇒ los 4 cuadrantes SIEMPRE
        // presentes: footprint es una función pura de sz por banda, no del
        // sorteo de presencia — una sola muestra basta y es determinista.
        let footprint_with = footprint_of(&with_fill, 42, 0);
        let footprint_without = footprint_of(&without_fill, 42, 0);

        assert_eq!(
            footprint_without,
            4 * 36,
            "camino sz=6 fijo: 4 cuadrantes de 6×6 = 144 celdas — si esto cambia, \
             cambió `open_zone_size_x/z` de office_rules, no el mecanismo nuevo"
        );
        // Cuadrantes por (x_band, z_band): idx0=(LOW,LOW) 8×8=64,
        // idx1=(HIGH,LOW) 6×8=48, idx2=(LOW,HIGH) 8×6=48, idx3=(HIGH,HIGH)
        // 6×6=36. Suma 196 — NO 1 grande + 2 mixtos + 1 pequeño simétrico,
        // porque solo hay UN cuadrante 8×8 (idx0) y uno 6×6 (idx3): los dos
        // "mixtos" (idx1, idx2) ya tenían 48 cada uno antes también.
        assert_eq!(
            footprint_with,
            8 * 8 + 6 * 8 + 8 * 6 + 6 * 6,
            "fill_band: idx0(LOW,LOW)=8×8, idx1(HIGH,LOW)=6×8, idx2(LOW,HIGH)=8×6, \
             idx3(HIGH,HIGH)=6×6 — si esto cambia, cambiaron las bandas de ADR-057 \
             o el mapeo de cuadrante"
        );
        assert!(
            footprint_with > footprint_without,
            "fill_band debe crecer el footprint sobre el camino de tamaño fijo"
        );
    }

    /// LÍNEA BASE de ADR-086, medida y no citada.
    ///
    /// `office_rules` documenta "21,9 % de interior de sala, 54 % de lo pisable es pasillo", pero
    /// esos números salieron de `office_room_coverage_report`, instrumentación efímera que se
    /// retiró — hoy solo viven en un comentario. Las verificaciones (a) y (b) de ADR-086 se miden
    /// contra ellos, así que antes de tocar nada hacen falta VERIFICADOS.
    ///
    /// Mide dos cosas distintas y no las confunde:
    /// - **footprint**: la parte del chunk que cubren los rects de `room_zones`, pared incluida.
    ///   Es lo que ya guarda el test de arriba.
    /// - **interior pisable**: celdas caminables que caen DENTRO de un rect. Es lo que se juega, y
    ///   lo que ADR-086 tiene que subir. El perímetro `SealedWall` cuenta en el footprint y NO
    ///   aquí, que es justo de dónde sale la tasa que el ADR ataca.
    ///
    /// `#[ignore]`: es un informe, no una guarda. La guarda determinista es
    /// `office_baseline_matches_the_numbers_adr_086_argues_from`, debajo.
    ///
    /// ```text
    /// cargo test --release -p backrooms_server office_room_coverage_report -- --ignored --nocapture
    /// ```
    #[test]
    #[ignore = "informe de linea base de ADR-086; lanzar con --nocapture para leer los numeros"]
    fn office_room_coverage_report() {
        // El efecto de la ESCALA, aislado: UNA sala por chunk en vez de cuatro cuadrantes, sin
        // nada dentro. Separa "una sala grande rinde mas que cuatro pequenas" (perimetro
        // compartido, cero codigo) de "una planta trazada rinde mas que una sala" (ADR-086), que
        // es lo que la enmienda 2 no distinguio y por eso se equivoco de escala.
        for sz in [10u32, 14, 18] {
            let mut whole = office_rules(&LAYER_PROFILES[0]);
            whole.subregion_grid = false;
            whole.num_open_zones = 1;
            whole.open_zone_size = sz;
            whole.open_zone_size_x = Some(sz);
            whole.open_zone_size_z = Some(sz);
            whole.room_type_weights = (0.0, 1.0, 0.0); // SealedRoom siempre
            let (c, _) = measure_office_coverage_with(&whole, 256);
            println!(
                "ESCALA SOLA {sz}x{sz} SealedRoom: footprint {:.1} % | interior pisable {:.1} %                  | paso(CellType) {:.1} %",
                c.footprint_pct, c.in_room_pct, c.passage_share_pct
            );
        }
        println!("  (18x18 deja el paso en 0,8 %: eso no es una planta, es el maze desaparecido)");
        println!();
        let with_fill = office_rules(&LAYER_PROFILES[0]);
        let mut without_fill = with_fill.clone();
        without_fill.subregion_fill_band = false;

        let (old, _) = measure_office_coverage_with(&without_fill, 256);
        println!("SIN subregion_fill_band (el camino sz=6 de antes de 2026-08-09):");
        println!(
            "  interior PISABLE dentro de sala        : {:.1} %",
            old.in_room_pct
        );
        println!(
            "  de lo pisable, cuanto es pasillo       : {:.1} % (por rect) / {:.1} % (por CellType)",
            old.corridor_share_pct, old.passage_share_pct
        );
        println!();

        let (m, chunks) = measure_office_coverage(256);
        println!("LINEA BASE OFFICE — {chunks} chunks, seed 42, capa 0");
        println!(
            "  footprint de sala (rects, pared incl.) : {:.1} %",
            m.footprint_pct
        );
        println!(
            "  interior PISABLE dentro de sala        : {:.1} %  <- (a) de ADR-086",
            m.in_room_pct
        );
        println!(
            "  pisable total del chunk                : {:.1} %",
            m.walkable_pct
        );
        println!(
            "  paso por RECT (metrica vieja, miente si la zona cubre el chunk) : {:.1} %",
            m.corridor_share_pct
        );
        println!(
            "  paso por CELLTYPE (la buena)           : {:.1} %  <- (b) de ADR-086",
            m.passage_share_pct
        );
    }

    /// Lo mismo, como GUARDA. Fija la línea base con la que ADR-086 discute para que nadie la
    /// mueva sin darse cuenta: si estos números cambian, el "sube del 21,9 %" del ADR deja de
    /// significar lo que significaba y hay que remedirlo, no reinterpretarlo.
    ///
    /// Las horquillas son anchas a propósito — clavar el decimal convertiría cualquier retoque de
    /// `LAYER_PROFILES` en un rojo sin información.
    #[test]
    fn office_baseline_matches_the_numbers_adr_086_argues_from() {
        let (m, _) = measure_office_coverage(64);

        // POST-ADR-087 PASO 1 (una sala de 13×13). La historia, para que los números
        // de los comentarios de arriba no se lean como actuales nunca más:
        //
        //   4 cuadrantes + fill_band (hasta 2026-08-23) : interior 31,2 % | paso 39,4 %
        //   4 cuadrantes, sz=6 fijo (hasta 2026-08-09)  : interior 21,4 % | paso 53,7 %
        //   UNA sala 13×13 (hoy)                        : interior 37,5 % | paso 32,0 %
        //
        // Horquillas anchas a propósito: clavar el decimal convertiría cualquier
        // retoque de `LAYER_PROFILES` en un rojo sin información.
        assert!(
            (43.0..53.0).contains(&m.footprint_pct),
            "footprint fuera de la linea base de ~47,9 %: {:.1} %",
            m.footprint_pct
        );
        assert!(
            (33.0..42.0).contains(&m.in_room_pct),
            "interior pisable fuera de la linea base de ~37,5 %: {:.1} %              (antes de ADR-087 eran 31,2 % — si ha bajado ahi, se ha revertido el paso 1)",
            m.in_room_pct
        );

        // VERIFICACIÓN (c) DE ADR-087, y es de dos lados. Por debajo de 25 % el maze
        // se esta muriendo —18×18 da 0,8 % y 217 chunks incomunicados de 784—; por
        // encima de 45 % la oficina no es oficina. Un numero que SUBE de mas es tan
        // malo como uno que baja, y eso es justo lo que a ADR-086 le falto vigilar.
        assert!(
            (25.0..45.0).contains(&m.passage_share_pct),
            "paso real fuera de la banda 25-45 % de ADR-087: {:.1} %",
            m.passage_share_pct
        );

        // La tasa que ADR-086 ataca: de los rects de sala, la parte que NO se pisa.
        // Baja de 17,8 a ~10,4 puntos por el perimetro compartido — una sala paga un
        // anillo donde cuatro pagaban cuatro. Es la ganancia del paso 1, hecha numero.
        let perimeter_tax = m.footprint_pct - m.in_room_pct;
        assert!(
            (7.0..14.0).contains(&perimeter_tax),
            "la tasa de perimetro se movio de sus ~10,4 puntos: {perimeter_tax:.1}"
        );
    }

    struct OfficeCoverage {
        footprint_pct: f32,
        in_room_pct: f32,
        walkable_pct: f32,
        corridor_share_pct: f32,
        /// Lo pisable que es `CellType::Corridor`, sobre lo pisable total. **Es la métrica
        /// buena**, y la de arriba no lo es: ver `measure_office_coverage_with`.
        passage_share_pct: f32,
    }

    /// Recorre `chunks` chunks de `office_rules` y saca los porcentajes. Determinista: la muestra
    /// es una rejilla fija, no un sorteo.
    fn measure_office_coverage(chunks: i32) -> (OfficeCoverage, i32) {
        measure_office_coverage_with(&office_rules(&LAYER_PROFILES[0]), chunks)
    }

    /// **Dos definiciones de "pasillo", y la diferencia importa.**
    ///
    /// `corridor_share_pct` es la histórica: lo pisable que cae FUERA de todo rect de
    /// `room_zones`. Sirve mientras las salas sean islas en un maze, y **miente en cuanto una zona
    /// crece hasta cubrir el chunk** — entonces todo lo pisable cae dentro de algún rect y la cifra
    /// se va a 0 % aunque el sitio esté lleno de pasillo. Pasó exactamente eso midiendo la planta
    /// de chunk entero de ADR-086 enmienda 2, y el 0,0 % que salió no significaba nada.
    ///
    /// `passage_share_pct` es la buena y no depende de los rects: mira el `CellType`. El corte ya
    /// existe en el generador y es limpio — `Open` lo pinta el interior de una zona estampada
    /// (`stamp_open_zone`, `stamp_sealed_room`), `Corridor` lo pintan el maze de Fase 1 y TODA ruta
    /// de tallado (`carve_aperture`, `connect_zone_to_maze`, `carve_explicit_entrance`). Así que
    /// una espina de pasillo dentro de una sala cuenta como paso, que es lo que es.
    fn measure_office_coverage_with(rules: &LayerRules, chunks: i32) -> (OfficeCoverage, i32) {
        use crate::world::grid_gen::{generate_layer, CellType, CHUNK_CELLS};

        let side = (chunks as f64).sqrt() as i32;
        let cells_per_chunk = (CHUNK_CELLS * CHUNK_CELLS) as u64;

        let (mut footprint, mut walkable, mut in_room, mut total) = (0u64, 0u64, 0u64, 0u64);
        let mut passage = 0u64;
        for cx in 0..side {
            for cz in 0..side {
                let out = generate_layer(rules, 42, (cx, cz), 0, &[]);
                total += cells_per_chunk;

                for z in &out.room_zones {
                    footprint += (z.x1 - z.x0) as u64 * (z.z1 - z.z0) as u64;
                }

                for x in 0..CHUNK_CELLS {
                    for z in 0..CHUNK_CELLS {
                        let cell = out.grid.get(x, z);
                        if !cell.is_walkable() {
                            continue;
                        }
                        walkable += 1;
                        if cell.kind() == CellType::Corridor {
                            passage += 1;
                        }
                        let inside = out.room_zones.iter().any(|r| {
                            (x as u8) >= r.x0
                                && (x as u8) < r.x1
                                && (z as u8) >= r.z0
                                && (z as u8) < r.z1
                        });
                        if inside {
                            in_room += 1;
                        }
                    }
                }
            }
        }

        let pct = |a: u64, b: u64| {
            if b == 0 {
                0.0
            } else {
                a as f32 * 100.0 / b as f32
            }
        };
        (
            OfficeCoverage {
                footprint_pct: pct(footprint, total),
                in_room_pct: pct(in_room, total),
                walkable_pct: pct(walkable, total),
                corridor_share_pct: pct(walkable - in_room, walkable),
                passage_share_pct: pct(passage, walkable),
            },
            side * side,
        )
    }
}
