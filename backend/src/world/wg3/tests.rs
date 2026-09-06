//! ADR-095 F2 tanda 1 — el lado Rust, sin wire y sin cliente.
//!
//! Los tests corren contra el manifiesto REAL exportado al repositorio, no contra uno fabricado
//! aquí. Es a propósito: un manifiesto de mentira prueba que el parser funciona, y lo que hay que
//! probar es que **el fichero que hornea Unity se lee y se coloca**, que es el contrato entero.

use std::path::PathBuf;

use super::chunk;
use super::compose;
use super::junction;
use super::manifest::{self, Wg3Manifest, Wg3Piece};
use super::placement::{self, PlacedBox, Wg3Placement};
use super::raster::{Span, Wg3Raster, Wg3RasterBuilder, CM_PER_M, WG3_CELL_M};
use super::route;

/// Radio del jugador, espejo de `collision::PLAYER_RADIUS`. Duplicado y no importado a propósito
/// (R4): WG3 no debe engancharse a la colisión de WG2 mientras convivan, y un test que se rompa al
/// borrar WG2 sería un test que impide borrarlo.
const PLAYER_RADIUS: f32 = 0.35;

fn manifest_path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("Assets")
        .join("StreamingAssets")
        .join("wg3_manifest.json")
}

pub(crate) fn real_manifest() -> Wg3Manifest {
    let path = manifest_path();
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("no se pudo leer {}: {e}", path.display()));
    manifest::parse_manifest(&text).expect("el manifiesto exportado no pasa la validación")
}

/// EL CATÁLOGO CONGELADO DE LOS ORÁCULOS. Decisión de Joel, 2026-08-28.
///
/// Los dos oráculos —composición y rotación— prueban que **Rust reproduce el ALGORITMO de C#**, y
/// para eso el catálogo es decorado: hace falta que sea el MISMO a los dos lados, no que sea el que
/// se juega. Cuando la biblioteca autorada sustituya al catálogo de código, el manifiesto servido
/// cambiará cada vez que alguien dibuje una pieza; un oráculo que se mueve con él deja de ser una
/// prueba de regresión y pasa a ser un espejo que siempre se da la razón.
///
/// Así que los oráculos leen ESTA foto —el catálogo de código tal y como se exportó— y no
/// `StreamingAssets`. La comparación de digest se conserva y sigue cazando lo de siempre: cambiar
/// una pieza del catálogo de código y olvidar reexportar el oráculo.
///
/// **Lo que esto NO cubre, dicho aquí para que nadie lea un verde de más:** el catálogo que se sirve
/// de verdad. De eso responden los tests que sí leen el manifiesto servido —ráster, chunk,
/// geometría— y los invariantes que no dependen del catálogo: determinismo, cero solapes, ninguna
/// boca al vacío, la junta se cruza.
///
/// Y una consecuencia que es correcta aunque moleste: el día de la conmutación, los tests que buscan
/// piezas por id en el manifiesto SERVIDO (`cor_straight`, `room_pillars`…) van a fallar con un
/// panic claro. Deben hacerlo — siguen la realidad, y re-apuntarlos es parte de conmutar.
fn frozen_catalog() -> Wg3Manifest {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("fixtures")
        .join("wg3_oracle_catalog.json");
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| {
        panic!(
            "no se pudo leer el catálogo congelado {}: {e}",
            path.display()
        )
    });
    manifest::parse_manifest(&text).expect("el catálogo congelado no pasa la validación")
}

fn piece_by_id<'a>(m: &'a Wg3Manifest, id: &str) -> &'a Wg3Piece {
    m.pieces
        .iter()
        .find(|p| p.id == id)
        .unwrap_or_else(|| {
            // El motivo probable NO es un bug: es que el catálogo servido ya no es el de código.
            // Un panic que solo dice "no encontrada" manda a buscar un fallo donde solo hay una
            // conmutación pendiente, así que dice qué hacer.
            panic!(
                "el catálogo no tiene la pieza «{id}» ({} piezas: {}). Si la biblioteca autorada \
                 ya sustituyó al catálogo de código, este test busca por id y hay que reapuntarlo a \
                 una pieza que exista — no es un fallo del ráster",
                m.pieces.len(),
                m.pieces
                    .iter()
                    .map(|p| p.id.as_str())
                    .collect::<Vec<_>>()
                    .join(", ")
            )
        })
}

fn raster_for(piece: &Wg3Piece, placement: &Wg3Placement) -> Wg3Raster {
    let (min_x, min_z, max_x, max_z) = placement.bounds(piece);
    let mut builder = Wg3RasterBuilder::covering(min_x, min_z, max_x, max_z);
    for b in placement::placed_collision(piece, placement) {
        builder.add_box(&b);
    }
    builder.finish()
}

fn at(piece: u16, rotation: u8) -> Wg3Placement {
    Wg3Placement {
        piece,
        rotation,
        // Origen deliberadamente NO alineado a la rejilla de 0,5 m: si estuviera alineado, el
        // rasterizado conservador no inflaría nada y el test del vano mediría el mejor caso en vez
        // del real.
        origin_x_cm: 1337,
        origin_z_cm: -4271,
        origin_y_cm: 0,
    }
}

// ── manifiesto ──────────────────────────────────────────────────────────────────────────────

#[test]
fn the_exported_manifest_parses_and_validates() {
    let m = real_manifest();
    assert_eq!(m.version, manifest::WG3_MANIFEST_FORMAT);
    assert!(m.problems().is_empty(), "{:?}", m.problems());
    assert!(!m.pieces.is_empty());
    assert_eq!(m.digest.len(), 64);
}

#[test]
fn every_piece_carries_a_chuleta_and_no_decoration() {
    // El discriminante 6 es `Wg3VolumeKind.Decoration`. Que no aparezca NUNCA es la regla R25
    // cruzando la frontera de autoridad: lo que no bloquea, no viaja. Si un día llegara, el
    // servidor frenaría al jugador a 12 cm de cada pared del mundo y el cliente no mostraría nada
    // que lo explicara.
    for p in &real_manifest().pieces {
        assert!(!p.collision.is_empty(), "{}: sin chuleta", p.id);
        for b in &p.collision {
            assert_ne!(6, b.kind, "{}: llegó decoración al backend", p.id);
        }
    }
}

#[test]
fn a_manifest_with_a_wrong_index_is_rejected_whole() {
    // Rechazar entero y no pieza a pieza: colocar las sanas y callar las rotas es el modo de fallo
    // que ADR-095 nombra — un mundo al que le falta contenido y nadie sabe por qué.
    let mut m = real_manifest();
    m.pieces[1].index = 99;
    assert!(!m.problems().is_empty());
}

#[test]
fn a_manifest_from_the_future_is_rejected() {
    let mut m = real_manifest();
    m.version = manifest::WG3_MANIFEST_FORMAT + 1;
    assert!(!m.problems().is_empty());
}

// ── colocación: el espejo de C# ─────────────────────────────────────────────────────────────

#[test]
fn rotating_a_piece_keeps_socket_offsets_and_shifts_the_side() {
    // El contrato del que depende todo el emparejado, comprobado a este lado del idioma: girar deja
    // el `offset` intacto y solo suma al lado. Si Rust y C# se separaran aquí, la pared de una
    // pieza girada taparía la puerta de su vecina y el síntoma aparecería a cien metros de la causa.
    let m = real_manifest();
    for piece in &m.pieces {
        for r in 0..4u8 {
            let p = at(piece.index, r);
            let (w, d) = p.footprint(piece);
            for (i, s) in piece.sockets.iter().enumerate() {
                assert_eq!((s.side + r) % 4, p.world_side(piece, i));

                let (lx, lz) = placement::local_point((s.side + r) % 4, s.offset, w, d);
                let (wx, wz) = p.world_socket_point(piece, i);
                assert!((wx - (p.origin_x() + lx)).abs() < 1e-3);
                assert!((wz - (p.origin_z() + lz)).abs() < 1e-3);

                let len = placement::side_length((s.side + r) % 4, w, d);
                assert!(
                    s.offset - s.width * 0.5 >= -1e-3 && s.offset + s.width * 0.5 <= len + 1e-3,
                    "{} giro {r}: la boca {i} no cabe en su lado",
                    piece.id
                );
            }
        }
    }
}

#[test]
fn placed_boxes_stay_inside_the_placement_footprint() {
    let m = real_manifest();
    for piece in &m.pieces {
        for r in 0..4u8 {
            let p = at(piece.index, r);
            let (min_x, min_z, max_x, max_z) = p.bounds(piece);
            for b in placement::placed_collision(piece, &p) {
                let rad = b.yaw_degrees.to_radians();
                let (sin, cos) = rad.sin_cos();
                let ex = (b.size[0] * cos.abs() + b.size[2] * sin.abs()) * 0.5;
                let ez = (b.size[0] * sin.abs() + b.size[2] * cos.abs()) * 0.5;
                assert!(
                    b.center[0] - ex >= min_x - 0.01,
                    "{} giro {r}: −X",
                    piece.id
                );
                assert!(
                    b.center[2] - ez >= min_z - 0.01,
                    "{} giro {r}: −Z",
                    piece.id
                );
                assert!(
                    b.center[0] + ex <= max_x + 0.01,
                    "{} giro {r}: +X",
                    piece.id
                );
                assert!(
                    b.center[2] + ez <= max_z + 0.01,
                    "{} giro {r}: +Z",
                    piece.id
                );
            }
        }
    }
}

// ── ráster ──────────────────────────────────────────────────────────────────────────────────

#[test]
fn a_wall_is_never_lost_between_two_cell_centres() {
    // El motivo entero de que el rasterizado sea conservador. Una pared mide 0,15 m y un tramo
    // 0,5 m: muestreando el centro, una pared entre dos centros DESAPARECE y se atraviesa andando,
    // mientras el cliente la sigue dibujando. Se comprueba pared por pared, no de muestra.
    let m = real_manifest();
    let piece = piece_by_id(&m, "cor_straight");
    let p = at(piece.index, 0);
    let raster = raster_for(piece, &p);

    for b in placement::placed_collision(piece, &p) {
        if b.kind != 2 {
            continue; // solo paredes
        }
        assert!(
            raster.is_solid_at(b.center[0], b.center[1], b.center[2]),
            "una pared se perdió en el rasterizado: {b:?}"
        );
    }
}

#[test]
fn the_doorway_survives_the_conservative_rasterisation() {
    // LA MEDIDA QUE VALIDA D1. El rasterizado conservador infla cada pared hasta media tramo, y eso
    // COME VANO. Si el hueco libre baja del diámetro del jugador, el tamaño de tramo elegido en el
    // ADR está mal y hay que bajarlo — no es una opinión, es este número.
    let m = real_manifest();
    let needed = PLAYER_RADIUS * 2.0;
    let mut worst = f32::INFINITY;
    let mut worst_where = String::new();

    for piece in &m.pieces {
        for r in 0..4u8 {
            let p = at(piece.index, r);
            let raster = raster_for(piece, &p);
            for (i, socket) in piece.sockets.iter().enumerate() {
                let clear = doorway_clearance(&raster, piece, &p, i);
                if clear < worst {
                    worst = clear;
                    worst_where = format!("{} giro {r} boca {i} ({} m)", piece.id, socket.width);
                }
                assert!(
                    clear >= needed,
                    "{} giro {r}: la boca {i} queda en {clear:.2} m libres, por debajo de los \
                     {needed:.2} m que necesita el jugador",
                    piece.id
                );
            }
        }
    }
    println!("[wg3] vano más estrecho tras rasterizar: {worst:.2} m en {worst_where}");
}

/// Anchura libre contigua en el plano de la boca, medida DENTRO de la banda de pared. Medirla más
/// adentro daría el ancho de la sala y no diría nada del vano.
fn doorway_clearance(
    raster: &Wg3Raster,
    piece: &Wg3Piece,
    p: &Wg3Placement,
    socket_index: usize,
) -> f32 {
    let side = p.world_side(piece, socket_index);
    let (mx, mz) = p.world_socket_point(piece, socket_index);
    let (nx, nz) = placement::outward_normal(side);
    // Hacia dentro medio grosor de pared: ahí es donde la pared bloquearía si el corte estuviera mal.
    let (px, pz) = (mx - nx * 0.08, mz - nz * 0.08);
    // A lo largo del vano: perpendicular a la normal.
    let (ax, az) = (-nz, nx);

    let step = WG3_CELL_M * 0.25;
    let half = piece.sockets[socket_index].width * 0.5 + WG3_CELL_M;
    let y = 1.0;

    let mut clear = 0.0;
    let mut t = -half;
    while t <= half {
        let (sx, sz) = (px + ax * t, pz + az * t);
        if raster.blocked_standing_at(sx, y, sz, 0.1) {
            if t >= 0.0 {
                break; // se acabó el hueco contiguo alrededor del centro
            }
            clear = 0.0;
        } else {
            clear += step;
        }
        t += step;
    }
    clear
}

#[test]
fn a_wall_standing_on_the_floor_slab_becomes_one_span() {
    // Fundir tramos que se TOCAN y no solo los que se solapan. Sin eso, la losa de suelo y la pared
    // que se apoya en ella quedan como dos tramos con una junta de espesor cero, y una consulta
    // puede colarse por ella.
    let m = real_manifest();
    let piece = piece_by_id(&m, "cor_straight");
    let p = at(piece.index, 0);
    let raster = raster_for(piece, &p);

    // Un punto claramente dentro de la pared del lado sur.
    let (min_x, min_z, _, _) = p.bounds(piece);
    let column = raster.column_at(min_x + 5.0, min_z + 0.05);
    assert_eq!(1, column.len(), "esperaba un solo tramo macizo: {column:?}");
}

/// ADR-102 D6 — **el arnés de medida tiene que distinguir una planta de dos.**
///
/// Las sondas cuentan de dos formas y sólo una sobrevive a apilar: por COLUMNA (`seen[iz*c+ix]`) y
/// por (celda, nivel) (`seen_level[iz*c+ix][l]`). Con una planta las dos dan lo mismo, así que la
/// diferencia no se nota mirando ninguna cifra de hoy — y el día que haya dos plantas, la que cuenta
/// columnas sigue dando el MISMO porcentaje que antes de añadirla. No falla: miente y sale en verde.
///
/// Este test es un mundo de dos plantas hecho a mano, sin generador de por medio, para que la
/// propiedad se pueda exigir ANTES de que el generador sepa apilar. Si alguien colapsa los niveles a
/// un escalar, aquí se entera.
#[test]
fn the_flood_distinguishes_storeys() {
    const HEAD_M: f32 = 1.8;
    // La planta canónica de ADR-102 D2: 3,20 m libres más 12 cm de losa. La losa CUELGA por debajo de
    // su cota, así que el suelo de la planta 1 está en 3,32 y ocupa [3,20 – 3,32].
    const STOREY_M: f32 = 3.32;
    const SLAB_M: f32 = 0.12;

    let slab = |floor_y: f32| PlacedBox {
        center: [5.0, floor_y - SLAB_M * 0.5, 5.0],
        size: [10.0, SLAB_M, 10.0],
        yaw_degrees: 0.0,
        kind: segment::KIND_FLOOR,
    };

    let mut b = Wg3RasterBuilder::covering(0.0, 0.0, 10.0, 10.0);
    b.add_box(&slab(0.0)); // suelo de la planta baja
    b.add_box(&slab(STOREY_M)); // suelo de la primera, que es el techo de la baja
    b.add_box(&slab(STOREY_M * 2.0)); // el tejado: hay suelo, pero encima no hay techo
    let raster = b.finish();

    // Tres macizos disjuntos: si `finish()` fundiera lo que no se toca, no habría dos plantas que
    // contar y el resto del test no significaría nada.
    let column = raster.column_at(5.0, 5.0);
    assert_eq!(3, column.len(), "esperaba tres losas sueltas: {column:?}");

    // La misma regla que usan las sondas: hay suelo, y encima hay hueco para la cabeza CON techo.
    let levels: Vec<f32> = column
        .iter()
        .enumerate()
        .filter_map(|(i, span)| {
            let head = match column.get(i + 1) {
                Some(next) => (next.bottom_cm - span.top_cm) as f32 / 100.0,
                None => f32::MAX,
            };
            (HEAD_M..=CEILING_CAP_M)
                .contains(&head)
                .then_some(span.top_cm as f32 / 100.0)
        })
        .collect();

    assert_eq!(
        2,
        levels.len(),
        "dos plantas tienen que dar dos niveles pisables, no {levels:?} — el tejado no cuenta \
         porque no tiene techo encima"
    );
    assert!(
        (levels[1] - levels[0] - STOREY_M).abs() < 0.02,
        "las dos plantas tienen que estar separadas una altura de planta: {levels:?}"
    );

    // Y AQUÍ está el fallo silencioso que este test existe para impedir. Se recorre un trozo del
    // mundo con los DOS recuentos, como hace `probe_walkable_surface`: el de columnas da lo mismo que
    // daría con una sola planta, el de niveles es el doble. Si algún día vuelven a coincidir, o es
    // que se perdió una planta o es que se perdió el recuento.
    const CELL: f32 = 0.5;
    let (mut columns, mut pairs) = (0usize, 0usize);
    for iz in 0..12 {
        for ix in 0..12 {
            let (x, z) = (2.0 + ix as f32 * CELL, 2.0 + iz as f32 * CELL);
            let col = raster.column_at(x, z);
            let here = col
                .iter()
                .enumerate()
                .filter(|(i, span)| {
                    let head = match col.get(i + 1) {
                        Some(next) => (next.bottom_cm - span.top_cm) as f32 / 100.0,
                        None => f32::MAX,
                    };
                    (HEAD_M..=CEILING_CAP_M).contains(&head)
                })
                .count();
            if here > 0 {
                columns += 1;
            }
            pairs += here;
        }
    }

    assert_eq!(144, columns, "las 144 celdas del trozo tienen suelo");
    assert_eq!(
        2 * columns,
        pairs,
        "por nivel tiene que salir el DOBLE que por columna: {pairs} contra {columns}"
    );
}

#[test]
fn inside_a_corridor_there_is_floor_below_and_ceiling_above() {
    let m = real_manifest();
    let piece = piece_by_id(&m, "cor_straight");
    let p = at(piece.index, 0);
    let raster = raster_for(piece, &p);

    let (min_x, min_z, _, _) = p.bounds(piece);
    let (x, z) = (min_x + 5.5, min_z + piece.size_z * 0.5);

    assert!(
        !raster.blocked_standing_at(x, 0.0, z, 1.75),
        "el pasillo está tapado"
    );

    let floor = raster
        .floor_below(x, 1.0, z)
        .expect("sin suelo bajo los pies");
    assert!(floor.abs() < 0.02, "el suelo no está a cota 0: {floor}");

    let head = raster.headroom_above_floor(x, 1.0, z).expect("sin techo");
    assert!(
        (head - piece.height_meters).abs() < 0.05,
        "hueco {head} contra altura autorada {}",
        piece.height_meters
    );
}

#[test]
fn a_pillar_blocks_where_it_is_drawn() {
    // El criterio de cierre de F0, ahora a este lado: chocar con una columna interior donde se ve.
    let m = real_manifest();
    let piece = piece_by_id(&m, "room_pillars");
    let p = at(piece.index, 0);
    let raster = raster_for(piece, &p);

    let mut checked = 0;
    for b in placement::placed_collision(piece, &p) {
        if b.kind != 3 {
            continue; // solo columnas
        }
        assert!(
            raster.blocked_standing_at(b.center[0], 0.0, b.center[2], 1.75),
            "una columna no bloquea: {b:?}"
        );
        checked += 1;
    }
    assert_eq!(4, checked, "esperaba las cuatro columnas de room_pillars");
}

#[test]
fn the_stair_climbs_step_by_step_in_the_raster() {
    // El pago de D2 hecho visible: la verticalidad DENTRO de una pieza ya existe y ya se puede
    // subir. Entre pieza y pieza sigue siendo F5.
    let m = real_manifest();
    let piece = piece_by_id(&m, "room_stair");
    let p = at(piece.index, 0);
    let raster = raster_for(piece, &p);

    let (min_x, min_z, _, _) = p.bounds(piece);
    let x = min_x + 7.0; // centro del tramo autorado

    // La ventana ES la longitud del tramo, y cambió con él: ADR-097 enmienda 1 subió la huella del
    // peldaño de 0,29 a 0,60 m —por debajo de la celda de 0,50 el ráster funde peldaños y la
    // escalera se vuelve infranqueable—, así que 12 peldaños miden 7,20 m y no 3,48. Los umbrales
    // NO se tocan: siguen pidiendo 8 escalones distintos y llegar por encima de 1,9 m, que es lo
    // que el test prueba. Con la huella vieja este recorrido da 6 y falla, que es lo que debe.
    let mut previous = -1.0f32;
    let mut climbed = 0;
    let mut z = min_z + 5.6;
    while z < min_z + 12.8 {
        let floor = raster
            .floor_below(x, 4.0, z)
            .unwrap_or_else(|| panic!("sin suelo en z={z}"));
        assert!(
            floor >= previous - 0.001,
            "la escalera baja en z={z}: {previous} → {floor}"
        );
        if floor > previous + 0.001 {
            climbed += 1;
        }
        previous = floor;
        z += 0.1;
    }
    assert!(
        climbed >= 8,
        "solo {climbed} escalones distintos en el ráster"
    );
    assert!(previous > 1.9, "la escalera no llega arriba: {previous} m");
}

#[test]
fn rasterising_twice_gives_the_same_bytes() {
    let m = real_manifest();
    for piece in &m.pieces {
        for r in 0..4u8 {
            let p = at(piece.index, r);
            assert_eq!(raster_for(piece, &p), raster_for(piece, &p), "{}", piece.id);
        }
    }
}

#[test]
fn spans_come_out_sorted_and_disjoint() {
    let m = real_manifest();
    for piece in &m.pieces {
        let p = at(piece.index, 0);
        let raster = raster_for(piece, &p);
        for iz in 0..raster.cells_z() {
            for ix in 0..raster.cells_x() {
                let column = raster.column(ix, iz);
                for pair in column.windows(2) {
                    assert!(
                        pair[0].top_cm < pair[1].bottom_cm,
                        "{}: tramos sin fundir en ({ix},{iz}): {pair:?}",
                        piece.id
                    );
                }
                for s in column {
                    assert!(s.top_cm > s.bottom_cm, "{}: tramo vacío {s:?}", piece.id);
                }
            }
        }
    }
}

// ── presupuesto (R10) ───────────────────────────────────────────────────────────────────────

#[test]
fn the_raster_budget_is_measured_not_estimated() {
    let m = real_manifest();
    let mut worst_bytes_per_m2 = 0.0f32;
    let mut report = Vec::new();

    for piece in &m.pieces {
        let p = at(piece.index, 0);
        let raster = raster_for(piece, &p);
        let area = piece.size_x * piece.size_z;
        let per_m2 = raster.bytes() as f32 / area;
        worst_bytes_per_m2 = worst_bytes_per_m2.max(per_m2);
        report.push(format!(
            "{}: {} tramos, {} tramos, {} B ({:.0} B/m²)",
            piece.id,
            raster.cells_x() * raster.cells_z(),
            raster.span_count(),
            raster.bytes(),
            per_m2
        ));
    }

    // Un chunk de 50 m son 2500 m². La cifra proyectada sale de la peor densidad medida, no de una
    // estimación: el ADR anotó 20 KB pensando en un mapa de alturas plano, y los tramos cuestan
    // más. Si esto se dispara, la enmienda va al ADR con este número delante.
    let projected_kb = worst_bytes_per_m2 * 2500.0 / 1024.0;
    println!("[wg3] presupuesto de ráster:\n  {}", report.join("\n  "));
    println!(
        "[wg3] peor caso {worst_bytes_per_m2:.0} B/m² → {projected_kb:.0} KB por chunk de 50 m"
    );

    assert!(
        projected_kb < 512.0,
        "el ráster proyecta {projected_kb:.0} KB por chunk, que no cabe en el presupuesto"
    );
}

#[test]
fn the_span_is_four_bytes() {
    // Si esto cambia, el presupuesto de arriba miente y hay que rehacer la cuenta.
    assert_eq!(4, std::mem::size_of::<Span>());
    assert_eq!(0.5, WG3_CELL_M);
    assert_eq!(100.0, CM_PER_M);
}

#[test]
fn an_empty_box_is_ignored_instead_of_poisoning_a_column() {
    let mut builder = Wg3RasterBuilder::covering(0.0, 0.0, 4.0, 4.0);
    builder.add_box(&PlacedBox {
        center: [2.0, 1.0, 2.0],
        size: [0.0, 2.0, 1.0],
        yaw_degrees: 0.0,
        kind: 2,
    });
    let raster = builder.finish();
    assert_eq!(0, raster.span_count());
}

// ── el oráculo: la juntura entre los dos idiomas ────────────────────────────────────────────

#[derive(serde::Deserialize)]
struct OracleBox {
    cx: f32,
    cy: f32,
    cz: f32,
    sx: f32,
    sy: f32,
    sz: f32,
    yaw: f32,
    kind: u8,
}

#[derive(serde::Deserialize)]
struct OracleCase {
    piece: u16,
    rotation: u8,
    boxes: Vec<OracleBox>,
}

#[derive(serde::Deserialize)]
struct Oracle {
    origin_x_cm: i32,
    origin_z_cm: i32,
    digest: String,
    cases: Vec<OracleCase>,
}

/// LO ÚNICO QUE PUEDE CAZAR UNA DERIVA ENTRE C# Y RUST.
///
/// La rotación está escrita dos veces a propósito: el cliente dibuja sin preguntar y el servidor
/// colisiona sin dibujar. Pero un test dentro de cada idioma no sirve de nada aquí — los dos pueden
/// ser internamente consistentes y diferir entre ellos. Y el modo de fallo es silencioso: nada
/// revienta, simplemente la pared de una pieza girada tapa la puerta de su vecina y el síntoma
/// aparece a cien metros de la causa.
///
/// Así que uno escribe los números (`Backrooms ▸ WorldGen3 ▸ Exportar oráculo de rotación`) y el
/// otro los verifica. Las 14 piezas, los cuatro giros, caja a caja.
#[test]
fn the_rust_rotation_matches_the_one_unity_baked() {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("fixtures")
        .join("wg3_rotation_oracle.json");
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| {
        panic!(
            "sin oráculo en {}: {e}. Reexpórtalo desde Unity con \
             «Backrooms ▸ WorldGen3 ▸ Exportar oráculo de rotación».",
            path.display()
        )
    });
    let oracle: Oracle = serde_json::from_str(&text).expect("oráculo ilegible");

    // Catálogo CONGELADO, no el servido: esto mide paridad de algoritmo entre dos idiomas, y para
    // eso los dos tienen que estar mirando las mismas piezas. Ver `frozen_catalog`.
    let m = frozen_catalog();

    // El oráculo lleva el digest del catálogo que lo produjo. Sin esta comparación, cambiar una
    // pieza y olvidar reexportar deja el test verde comparando dos cosas viejas entre sí, que es la
    // peor forma de estar verde.
    assert_eq!(
        m.digest, oracle.digest,
        "el oráculo es de otro catálogo — reexpórtalo desde Unity"
    );
    assert_eq!(m.pieces.len() * 4, oracle.cases.len());

    let mut compared = 0;
    for case in &oracle.cases {
        let piece = m
            .piece(case.piece)
            .expect("pieza del oráculo fuera del catálogo");
        let p = Wg3Placement {
            piece: case.piece,
            rotation: case.rotation,
            origin_x_cm: oracle.origin_x_cm,
            origin_z_cm: oracle.origin_z_cm,
            origin_y_cm: 0,
        };
        let ours = placement::placed_collision(piece, &p);

        assert_eq!(
            case.boxes.len(),
            ours.len(),
            "{} giro {}: {} cajas contra {} del oráculo",
            piece.id,
            case.rotation,
            ours.len(),
            case.boxes.len()
        );

        for (i, (theirs, mine)) in case.boxes.iter().zip(ours.iter()).enumerate() {
            let tag = format!("{} giro {} caja {i}", piece.id, case.rotation);
            close(theirs.cx, mine.center[0], &tag, "centro X");
            close(theirs.cy, mine.center[1], &tag, "centro Y");
            close(theirs.cz, mine.center[2], &tag, "centro Z");
            close(theirs.sx, mine.size[0], &tag, "tamaño X");
            close(theirs.sy, mine.size[1], &tag, "tamaño Y");
            close(theirs.sz, mine.size[2], &tag, "tamaño Z");
            // El giro se compara en la circunferencia: 360 y 0 son el mismo giro, y C# suma sin
            // normalizar. Comparar los números crudos daría un falso rojo en el cuarto cuarto.
            let delta = (theirs.yaw - mine.yaw_degrees).rem_euclid(360.0);
            assert!(
                !(1e-2..=360.0 - 1e-2).contains(&delta),
                "{tag}: giro {} contra {}",
                theirs.yaw,
                mine.yaw_degrees
            );
            assert_eq!(theirs.kind, mine.kind, "{tag}: tipo");
            compared += 1;
        }
    }
    assert!(compared > 400, "solo {compared} cajas comparadas");
    println!("[wg3] oráculo: {compared} cajas idénticas entre C# y Rust");
}

fn close(expected: f32, got: f32, tag: &str, what: &str) {
    // Milímetro. Más apretado empezaría a cazar el redondeo de `f32` al viajar por JSON; más flojo
    // dejaría pasar medio centímetro de deriva, que sobre una pared de 15 cm ya es un décimo.
    assert!(
        (expected - got).abs() < 1e-3,
        "{tag}: {what} {expected} contra {got}"
    );
}

// ── chunk ───────────────────────────────────────────────────────────────────────────────────

/// Una colocación que cae a caballo de los chunks (0,0) y (1,0), y que además NO empieza en un
/// múltiplo de el tramo: si empezara, el recorte del borde coincidiría con el de la rejilla por
/// suerte y la prueba de costura no probaría nada.
fn straddling(m: &Wg3Manifest) -> (u16, Wg3Placement) {
    let piece = piece_by_id(m, "hall_void");
    let (w, _) = (piece.size_x, piece.size_z);
    // Centrada sobre la frontera x = 50 m.
    let origin_x_cm = (50.0 * CM_PER_M) as i32 - (w * CM_PER_M * 0.5) as i32 + 13;
    (
        piece.index,
        Wg3Placement {
            piece: piece.index,
            rotation: 1,
            origin_x_cm,
            origin_z_cm: 1_233,
            origin_y_cm: 0,
        },
    )
}

#[test]
fn a_piece_straddling_two_chunks_rasterises_the_same_on_both_sides() {
    // LA PROPIEDAD DE LA QUE COLGARÁ A1. El borde del chunk RECORTA, nunca modifica. Si cambiara el
    // resultado, la misma pieza tendría colisión distinta a cada lado de una línea invisible y el
    // síntoma —quedarse enganchado en mitad de un pasillo— no señalaría jamás a la frontera. Y sin
    // esto, dos chunks vecinos no podrían coincidir sin hablarse ni con el sorteo idéntico.
    let m = real_manifest();
    let (index, p) = straddling(&m);
    let piece = m.piece(index).unwrap();

    let touched = chunk::chunks_touched(&m, &p);
    assert!(
        touched.len() >= 2,
        "la pieza de prueba no cruza ninguna frontera: {touched:?}"
    );

    // Referencia: un ráster que la contiene entera, sin fronteras de por medio.
    let (min_x, min_z, max_x, max_z) = p.bounds(piece);
    let whole = {
        let mut b = Wg3RasterBuilder::covering(min_x, min_z, max_x, max_z);
        for pb in placement::placed_collision(piece, &p) {
            b.add_box(&pb);
        }
        b.finish()
    };

    let mut compared = 0;
    for coord in &touched {
        let chunk_raster = chunk::build_chunk_raster(&m, std::slice::from_ref(&p), &[], *coord);
        let (cx0, cz0, _, _) = coord.bounds();

        for iz in 0..chunk_raster.cells_z() {
            for ix in 0..chunk_raster.cells_x() {
                // Centro de tramo en coordenadas de mundo: es lo único que las dos rejillas
                // comparten, porque tienen orígenes distintos.
                let x = cx0 + (ix as f32 + 0.5) * WG3_CELL_M;
                let z = cz0 + (iz as f32 + 0.5) * WG3_CELL_M;
                if x < min_x || x > max_x || z < min_z || z > max_z {
                    continue;
                }
                assert_eq!(
                    whole.column_at(x, z),
                    chunk_raster.column(ix, iz),
                    "el tramo de mundo ({x:.2}, {z:.2}) sale distinta al recortarla por el chunk \
                     {coord:?}"
                );
                compared += 1;
            }
        }
    }
    assert!(compared > 2_000, "solo {compared} tramos comparadas");
    println!("[wg3] costura: {compared} tramos idénticas a los dos lados de la frontera");
}

#[test]
fn a_chunk_that_no_piece_touches_comes_out_empty() {
    let m = real_manifest();
    let (_, p) = straddling(&m);
    let far = chunk::Wg3ChunkCoord { x: 40, z: -17 };
    let raster = chunk::build_chunk_raster(&m, std::slice::from_ref(&p), &[], far);
    assert_eq!(0, raster.span_count());
}

#[test]
fn chunk_coords_do_not_mirror_at_the_origin() {
    // `div_euclid` contra la división que trunca hacia cero: sin ella, −1 y +1 caen en el mismo
    // chunk y todo el hemisferio negativo sale espejado. Invisible salvo que se mire a propósito, y
    // es el mismo fallo que ya obligó a `div_euclid` al tallar salas entre dos chunks.
    assert_eq!(0, chunk::Wg3ChunkCoord::containing(1.0, 1.0).x);
    assert_eq!(-1, chunk::Wg3ChunkCoord::containing(-1.0, -1.0).x);
    assert_eq!(-1, chunk::Wg3ChunkCoord::containing(-49.9, 0.0).x);
    assert_eq!(-2, chunk::Wg3ChunkCoord::containing(-50.1, 0.0).x);
    assert_eq!(1, chunk::Wg3ChunkCoord::containing(50.0, 0.0).x);
}

#[test]
fn a_piece_ending_exactly_on_the_border_does_not_claim_the_next_chunk() {
    // El máximo es EXCLUSIVO. Reclamar un chunk en el que no se pone ni un tramo haría
    // re-rasterizar de más en cada colocación, y peor: un chunk vacío que se cree ocupado.
    let m = real_manifest();
    let piece = piece_by_id(&m, "cor_straight");
    let p = Wg3Placement {
        piece: piece.index,
        rotation: 0,
        origin_x_cm: (50.0 * CM_PER_M) as i32 - (piece.size_x * CM_PER_M) as i32,
        origin_z_cm: 0,
        origin_y_cm: 0,
    };
    let touched = chunk::chunks_touched(&m, &p);
    assert_eq!(vec![chunk::Wg3ChunkCoord { x: 0, z: 0 }], touched);
}

// ── presupuesto sobre un chunk de verdad (ADR-095 enmienda 1) ───────────────────────────────

#[test]
fn the_chunk_budget_is_measured_on_a_real_chunk() {
    // La enmienda 1 dejó los 159 KB anotados como PROYECCIÓN desde rásters del tamaño de una pieza,
    // donde el margen y la tabla de desplazamientos pesan mucho por metro cuadrado. Ésta es la
    // medida de verdad, y es la que va a `perf-baseline.md`.
    let m = real_manifest();

    // Un chunk lleno hasta arriba: piezas grandes cubriendo los 50 × 50 m. No es un mundo
    // realista, es el PEOR caso, que es lo que hay que presupuestar.
    let hall = piece_by_id(&m, "hall_large");
    let mut placements = Vec::new();
    let mut z = 0.0f32;
    while z < 50.0 {
        let mut x = 0.0f32;
        while x < 50.0 {
            placements.push(Wg3Placement {
                piece: hall.index,
                rotation: 0,
                origin_x_cm: (x * CM_PER_M) as i32,
                origin_z_cm: (z * CM_PER_M) as i32,
                origin_y_cm: 0,
            });
            x += hall.size_x;
        }
        z += hall.size_z;
    }

    let coord = chunk::Wg3ChunkCoord { x: 0, z: 0 };
    let start = std::time::Instant::now();
    let raster = chunk::build_chunk_raster(&m, &placements, &[], coord);
    let elapsed = start.elapsed();

    let kb = raster.bytes() as f32 / 1024.0;
    // El perfil se declara en vez de suponerse: la misma línea salía antes diciendo DEBUG al
    // correrla con `--release`, y un número de rendimiento con el perfil equivocado al lado es
    // peor que no tenerlo.
    let profile = if cfg!(debug_assertions) {
        "debug, sin optimizar"
    } else {
        "release"
    };
    println!(
        "[wg3] chunk lleno: {} colocaciones, {} tramos, {} tramos, {kb:.0} KB, \
         rasterizado en {:.1} ms ({profile})",
        placements.len(),
        raster.cells_x() * raster.cells_z(),
        raster.span_count(),
        elapsed.as_secs_f32() * 1000.0
    );

    assert_eq!(
        chunk::WG3_CHUNK_CELLS * chunk::WG3_CHUNK_CELLS,
        raster.cells_x() * raster.cells_z()
    );
    assert!(
        kb < 512.0,
        "el peor chunk ocupa {kb:.0} KB, fuera de presupuesto"
    );
}

// ── el compositor: el oráculo del mundo entero ──────────────────────────────────────────────

#[derive(serde::Deserialize)]
struct OraclePlacement {
    piece: u16,
    rotation: u8,
    origin_x_cm: i32,
    origin_z_cm: i32,
    origin_y_cm: i32,
    depth: i32,
}

#[derive(serde::Deserialize)]
struct OracleWorld {
    seed: i32,
    budget: usize,
    caps: usize,
    forced_caps: u32,
    rejected_by_overlap: u32,
    placements: Vec<OraclePlacement>,
}

#[derive(serde::Deserialize)]
struct CompositionOracle {
    digest: String,
    deliberate_cap_chance: f32,
    cap_grace_count: usize,
    scale_exact_bonus: f32,
    scale_near_bonus: f32,
    scale_far_bonus: f32,
    repeat_parent_penalty: f32,
    repeat_grandparent_penalty: f32,
    worlds: Vec<OracleWorld>,
}

fn composition_oracle() -> CompositionOracle {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("fixtures")
        .join("wg3_composition_oracle.json");
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| {
        panic!(
            "sin oráculo en {}: {e}. Reexpórtalo desde Unity con «Backrooms ▸ WorldGen3 ▸ \
             Exportar oráculo de composición».",
            path.display()
        )
    });
    serde_json::from_str(&text).expect("oráculo de composición ilegible")
}

fn settings_from(oracle: &CompositionOracle, budget: usize) -> compose::Wg3ComposerSettings {
    compose::Wg3ComposerSettings {
        budget,
        deliberate_cap_chance: oracle.deliberate_cap_chance,
        cap_grace_count: oracle.cap_grace_count,
        scale_exact_bonus: oracle.scale_exact_bonus,
        scale_near_bonus: oracle.scale_near_bonus,
        scale_far_bonus: oracle.scale_far_bonus,
        repeat_parent_penalty: oracle.repeat_parent_penalty,
        repeat_grandparent_penalty: oracle.repeat_grandparent_penalty,
        // ADR-096 — APAGADO, y es lo que mantiene vivo este oráculo. C# no cierra bucles, así que
        // encenderlo aquí no probaría una deriva: probaría que hemos cambiado el algoritmo. La
        // paridad se vigila sobre el algoritmo base; los bucles llevan sus propios tests.
        close_loops: false,
        // Y SIN ACOTAR ni anclar, por lo mismo: el oráculo es el mundo A3 que compone C#, que no
        // conoce ni regiones ni juntas. Acotarlo aquí no mediría paridad, mediría otra cosa.
        bounds: None,
        seed_at: None,
        // ADR-098 — sin enrutador, por lo mismo que sin bucles: C# no genera conectores, así que
        // encenderlo aquí no mediría una deriva, mediría que hemos cambiado el algoritmo.
        route: None,
        // Y sin holgura de solape, por lo mismo que lo de arriba: C# rechaza toda candidata que
        // pise algo colocado. Cualquier valor distinto de cero aquí mediría otro algoritmo.
        overlap_slack_m: 0.0,
        // ADR-099 — apagadas por lo mismo: C# ni absorbe ni densifica.
        absorb_chance: 0.0,
        densify_attempts: 0,
        collect_absorption_hits: false,
        anchors: Vec::new(),
    }
}

/// EL CRITERIO DE CIERRE DEL PORT.
///
/// El compositor está escrito dos veces —C# autora y prueba el catálogo, Rust sirve el mundo— y dos
/// implementaciones internamente consistentes pueden diferir entre ellas sin que nada reviente: la
/// pieza treinta aparece un metro corrida y el síntoma es una pared donde debía haber una puerta,
/// cien metros y media hora después de la causa.
///
/// Así que uno escribe el mundo entero («Backrooms ▸ WorldGen3 ▸ Exportar oráculo de composición») y
/// el otro lo reproduce, pieza a pieza y en orden. La semilla −19 está en la lista a propósito: es
/// donde un `%` que trunca hacia cero en vez de un módulo euclídeo produce otro mundo, y es un fallo
/// que este proyecto ya ha pagado dos veces.
///
/// AL CENTÍMETRO Y NO BIT A BIT: C# compone en `f32` y por el wire viajan centímetros enteros. Exigir
/// igualdad de flotantes sería exigir que Rust reprodujera también el orden exacto de las sumas de
/// C#, atando el port a la FORMA del original en vez de a su RESULTADO.
#[test]
fn the_rust_composer_reproduces_the_world_unity_composes() {
    let oracle = composition_oracle();
    // Catálogo CONGELADO, no el servido. Ver `frozen_catalog`: el oráculo mide que Rust reproduzca
    // el algoritmo de C#, y eso exige que los dos miren las mismas piezas, no las que se jueguen hoy.
    let m = frozen_catalog();

    // Sin esta comparación, cambiar una pieza y olvidar reexportar deja el test verde comparando dos
    // cosas viejas entre sí, que es la peor forma de estar verde.
    assert_eq!(
        m.digest, oracle.digest,
        "el oráculo es de otro catálogo — reexpórtalo desde Unity"
    );
    assert!(!oracle.worlds.is_empty());

    let mut compared = 0;
    for expected in &oracle.worlds {
        let settings = settings_from(&oracle, expected.budget);
        let world = compose::compose(expected.seed, &m, &settings);

        assert_eq!(
            expected.placements.len(),
            world.placements.len(),
            "semilla {}: {} piezas contra {} del oráculo",
            expected.seed,
            world.placements.len(),
            expected.placements.len()
        );

        for (i, want) in expected.placements.iter().enumerate() {
            let got = &world.placements[i];
            assert_eq!(
                // `origin_y_cm` entra en la comparación desde ADR-097. Dejarlo fuera habría sido el
                // agujero clásico: el oráculo seguiría verde mientras los dos idiomas propagan la
                // cota de forma distinta, que es exactamente la deriva silenciosa para la que
                // existe. Un campo que viaja y no se compara es un campo sin vigilar.
                (
                    want.piece,
                    want.rotation,
                    want.origin_x_cm,
                    want.origin_z_cm,
                    want.origin_y_cm,
                    want.depth
                ),
                (
                    got.placement.piece,
                    got.placement.rotation,
                    got.placement.origin_x_cm,
                    got.placement.origin_z_cm,
                    got.placement.origin_y_cm,
                    got.depth
                ),
                "semilla {}: diverge la colocación {} (el oráculo pone {}, aquí sale {})",
                expected.seed,
                i,
                m.piece(want.piece).map(|p| p.id.as_str()).unwrap_or("?"),
                m.piece(got.placement.piece)
                    .map(|p| p.id.as_str())
                    .unwrap_or("?")
            );
            compared += 1;
        }

        // Los tapones y los descartes no viajan, pero son la contabilidad del recorrido: si el port
        // llegara a las mismas piezas por otro camino —otra rama sellada, otra candidata pisada—
        // estos números lo delatan y la lista de arriba no.
        assert_eq!(
            expected.caps,
            world.caps.len(),
            "semilla {}: tapones",
            expected.seed
        );
        assert_eq!(
            expected.forced_caps, world.forced_caps,
            "semilla {}: tapones forzados",
            expected.seed
        );
        assert_eq!(
            expected.rejected_by_overlap, world.rejected_by_overlap,
            "semilla {}: candidatas descartadas por solape",
            expected.seed
        );
    }
    assert!(
        compared >= 100,
        "solo se compararon {compared} colocaciones"
    );
}

/// R3 en forma de test: el compositor no guarda estado entre llamadas ni lo lee del proceso.
#[test]
fn composing_twice_gives_the_same_world() {
    let m = real_manifest();
    let settings = compose::Wg3ComposerSettings::default();
    let a = compose::compose(-19, &m, &settings);
    let b = compose::compose(-19, &m, &settings);
    assert_eq!(a.placements, b.placements);
    assert!(a.placements.len() > 1);
}

/// Conectividad por construcción (R7): ni una pieza pisa a otra, ni una boca queda mirando al vacío.
///
/// Se comprueba sobre las coordenadas EMITIDAS —en centímetros— y no sobre las internas: son las que
/// va a rasterizar el servidor, así que es donde un solape se convierte en dos suelos en la misma
/// tramo.
#[test]
fn a_composed_world_has_no_overlaps_and_no_sockets_left_open() {
    let m = real_manifest();
    let settings = compose::Wg3ComposerSettings::default();

    for seed in [42, 7, 1337, -19, 900001] {
        let world = compose::compose(seed, &m, &settings);

        let mut sockets = 0usize;
        for (i, a) in world.placements.iter().enumerate() {
            let pa = m
                .piece(a.placement.piece)
                .expect("pieza fuera del catálogo");
            sockets += pa.sockets.len();
            let (ax0, az0, ax1, az1) = a.placement.bounds(pa);

            for (j, b) in world.placements.iter().enumerate().skip(i + 1) {
                let pb = m
                    .piece(b.placement.piece)
                    .expect("pieza fuera del catálogo");
                let (bx0, bz0, bx1, bz1) = b.placement.bounds(pb);
                let eps = 0.02;
                assert!(
                    ax0 >= bx1 - eps || bx0 >= ax1 - eps || az0 >= bz1 - eps || bz0 >= az1 - eps,
                    "semilla {seed}: {} #{i} pisa a {} #{j}",
                    pa.id,
                    pb.id
                );
            }
        }

        // Cada boca acaba conectada (dos por junta) o taponada. Sin esta igualdad, «no usar todos los
        // sockets» y «nada da al vacío» se contradicen sin que nada falle.
        let connections = world.placements.len().saturating_sub(1) * 2;
        assert_eq!(
            sockets,
            connections + world.caps.len(),
            "semilla {seed}: {sockets} bocas contra {connections} conectadas y {} taponadas",
            world.caps.len()
        );
    }
}

// ── el mundo servido: componer una vez, repartir por chunk ──────────────────────────────────

use super::world::{
    composer_seed, region_settings, Wg3RegionCoord, Wg3ServedWorld, Wg3WorldCache, INTERIM_BUDGET,
    REGION_CHUNKS, REGION_M,
};

/// Semilla de las pruebas del mundo servido. Es la del oráculo con los 32 bits altos puestos: así
/// el test también ejercita `composer_seed`, que se queda con los bajos.
pub(crate) const SERVED_SEED: u64 = 0xDEAD_BEEF_0000_002A;

/// LO QUE EL ANDAMIO NO PODÍA ROMPER Y ESTO SÍ.
///
/// `demo` encajaba cada pieza entera dentro de su chunk, así que ninguna se repetía. El compositor
/// las hace conectar, y conectar significa cruzar fronteras. El cliente monta un `GameObject` por
/// chunk y NO deduplica: si una pieza saliera en dos chunks, se dibujaría dos veces —con su colisión
/// duplicada y peleando en el z-buffer— y el síntoma sería una pared que parpadea, no un error.
#[test]
fn every_piece_of_the_served_world_is_drawn_by_exactly_one_chunk() {
    let m = real_manifest();
    let world = Wg3ServedWorld::compose(&m, SERVED_SEED);
    assert!(world.placements().len() > 1);

    let mut seen = vec![0usize; world.placements().len()];
    let mut chunks: Vec<chunk::Wg3ChunkCoord> = Vec::new();
    for p in world.placements() {
        for c in chunk::chunks_touched(&m, p) {
            if !chunks.contains(&c) {
                chunks.push(c);
            }
        }
    }

    for coord in &chunks {
        for drawn in world.placements_for_chunk(&m, *coord) {
            let i = world
                .placements()
                .iter()
                .position(|p| *p == drawn)
                .expect("la colocación repartida no está en el mundo");
            seen[i] += 1;
        }
    }

    for (i, count) in seen.iter().enumerate() {
        assert_eq!(
            1, *count,
            "la colocación {i} sale en {count} chunks; tiene que salir en exactamente uno"
        );
    }
}

/// La propiedad que hace que el radio 1 del cliente baste: una pieza nunca asoma más allá de los
/// vecinos inmediatos del chunk que la dibuja.
///
/// Se cumple porque el dueño es el chunk del CENTRO y ninguna pieza del catálogo llega a los 50 m.
/// Autorar una nave de 60 m rompería esto y el síntoma sería geometría que aparece tarde o no
/// aparece — por eso el test mira el catálogo real y no un caso inventado.
#[test]
fn no_piece_reaches_beyond_the_neighbours_of_its_owner_chunk() {
    let m = real_manifest();
    let world = Wg3ServedWorld::compose(&m, SERVED_SEED);

    for p in world.placements() {
        let owner = Wg3ServedWorld::owner_chunk(&m, p).expect("pieza fuera del catálogo");
        for touched in chunk::chunks_touched(&m, p) {
            let (dx, dz) = ((touched.x - owner.x).abs(), (touched.z - owner.z).abs());
            let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
            assert!(
                dx <= 1 && dz <= 1,
                "{} vuela hasta el chunk ({dx},{dz}) desde el suyo: no cabe en el radio 1",
                piece.id
            );
        }
    }
}

/// El mundo es FINITO (A3) y eso es la decisión, no un fallo. Lejos del origen la respuesta correcta
/// es la lista vacía — que el cliente ya sabe distinguir de "todavía no ha llegado".
#[test]
fn a_chunk_past_the_edge_of_the_finite_world_comes_out_empty() {
    let m = real_manifest();
    let world = Wg3ServedWorld::compose(&m, SERVED_SEED);

    let far = chunk::Wg3ChunkCoord { x: 400, z: -400 };
    assert!(world.placements_for_chunk(&m, far).is_empty());
    assert!(world.placements_touching_chunk(&m, far).is_empty());
}

/// El reparto es cosa de lo que se DIBUJA. El ráster de colisión sigue viendo toda pieza que toque
/// el chunk, porque una pared que cruza la frontera tiene que bloquear a los dos lados.
#[test]
fn collision_still_sees_the_pieces_that_only_touch_the_chunk() {
    let m = real_manifest();
    let world = Wg3ServedWorld::compose(&m, SERVED_SEED);

    let mut straddlers = 0;
    for p in world.placements() {
        let touched = chunk::chunks_touched(&m, p);
        if touched.len() < 2 {
            continue;
        }
        straddlers += 1;
        for coord in touched {
            assert!(
                world
                    .placements_touching_chunk(&m, coord)
                    .iter()
                    .any(|q| q == p),
                "la pieza a caballo no llega al ráster del chunk ({},{})",
                coord.x,
                coord.z
            );
        }
    }
    assert!(
        straddlers > 0,
        "ninguna pieza cruza una frontera: el test no está probando nada"
    );
}

/// R3: la caché no es un global escondido. Cambiar de semilla da otro mundo, y volver a la primera
/// da el primero otra vez — sin rastro de la segunda.
#[test]
fn the_cache_recomposes_when_the_seed_changes() {
    let m = real_manifest();
    let mut cache = Wg3WorldCache::default();

    let origin = chunk::Wg3ChunkCoord { x: 0, z: 0 };

    let first = served_content(cache.region_for(&m, SERVED_SEED, origin));
    let second = served_content(cache.region_for(&m, SERVED_SEED + 1, origin));
    assert_ne!(first, second, "dos semillas dan el mismo mundo");

    let again = served_content(cache.region_for(&m, SERVED_SEED, origin));
    assert_eq!(first, again);
}

/// Lo que una región SIRVE, para compararla con otra.
///
/// **Colocaciones Y tramos, y desde ADR-100 casi todo es lo segundo.** Comparar sólo las colocaciones
/// era suficiente mientras el mundo se componía de piezas; con el plan construyendo con tramos, dos
/// mundos distintos tienen los dos la lista de piezas vacía y el test pasaba a comparar nada con nada
/// —`[] != []` es falso— y fallaba diciendo que dos semillas dan el mismo mundo. Mentía en la
/// dirección peligrosa: el mismo test habría dado verde con un generador roto que no emitiera nada.
fn served_content(world: &Wg3ServedWorld) -> (Vec<Wg3Placement>, Vec<super::segment::Wg3Segment>) {
    (world.placements().to_vec(), world.segments().to_vec())
}

/// ADR-096 — dos chunks de la MISMA región comparten composición, y dos regiones distintas no.
///
/// Es la propiedad que hace infinito el mundo sin que las regiones se hablen: la coordenada entra en
/// la semilla, así que cada una compone lo suyo y siempre lo mismo.
#[test]
fn regions_are_independent_and_reproducible() {
    let m = real_manifest();
    let mut cache = Wg3WorldCache::default();

    let inside_a = chunk::Wg3ChunkCoord { x: 1, z: 1 };
    let inside_b = chunk::Wg3ChunkCoord {
        x: REGION_CHUNKS - 1,
        z: 0,
    };
    let other = chunk::Wg3ChunkCoord {
        x: REGION_CHUNKS,
        z: 0,
    };

    assert_eq!(
        Wg3RegionCoord::of_chunk(inside_a),
        Wg3RegionCoord::of_chunk(inside_b),
        "dos chunks de la misma región salieron en regiones distintas"
    );
    assert_ne!(
        Wg3RegionCoord::of_chunk(inside_a),
        Wg3RegionCoord::of_chunk(other)
    );

    let a = served_content(cache.region_for(&m, SERVED_SEED, inside_a));
    let b = served_content(cache.region_for(&m, SERVED_SEED, inside_b));
    assert_eq!(a, b, "el mismo chunk-región compuso dos cosas distintas");
    assert!(
        !a.1.is_empty(),
        "la región no sirve NADA — un `assert_ne` entre dos mundos vacíos daría verde"
    );

    let c = served_content(cache.region_for(&m, SERVED_SEED, other));
    assert_ne!(a, c, "dos regiones vecinas compusieron lo mismo");
}

/// ADR-096 verificación (c) — CUÁNTO llena una región, que es de donde sale su tamaño.
///
/// El número que importa no es cuántas piezas caben sino cuánto terreno ocupan de verdad: una
/// región medio vacía se lee como un descampado con edificios sueltos, y una que se ahoga contra el
/// borde desperdicia el catálogo. Se mide sobre varias regiones porque una sola puede salir con
/// suerte, igual que pasaba con las semillas.
#[test]
fn a_region_is_worth_its_size() {
    let m = real_manifest();
    let region_area = (REGION_CHUNKS * REGION_CHUNKS) as f32 * 50.0 * 50.0;

    let mut worst_fill = f32::MAX;
    for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, -1), (3, -2), (7, 11)] {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let started = std::time::Instant::now();
        let world = Wg3ServedWorld::compose_region(&m, SERVED_SEED, region);
        let elapsed = started.elapsed();

        // Superficie construida, sumando huellas. No descuenta solapes porque no los hay: el
        // compositor rechaza toda candidata que pise algo ya puesto.
        let mut built = 0.0f32;
        for p in world.placements() {
            let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
            let (x0, z0, x1, z1) = p.bounds(piece);
            built += (x1 - x0) * (z1 - z0);
        }
        let fill = built / region_area * 100.0;
        worst_fill = worst_fill.min(fill);

        println!(
            "[wg3] región ({rx},{rz}): {} piezas, {built:.0} m² de {region_area:.0} ({fill:.0} %), \
             {:.0} ms",
            world.placements().len(),
            elapsed.as_secs_f32() * 1000.0
        );

        assert!(
            !world.placements().is_empty(),
            "región ({rx},{rz}) vacía: la semilla no cabe en su propia caja"
        );
        // ADR-098 — el techo sube de 250 a 600 ms porque ahora componer INCLUYE enrutar, y este
        // test corre en depuración. **El número que manda está medido en release: 20–63 ms por
        // región** (antes del enrutador, 0–9), y se paga una vez por región porque la composición se
        // cachea. Lo que sigue vigilando esta aserción es lo de siempre: que a nadie se le vaya la
        // mano y cruzar una frontera pase a costar un tirón.
        //
        // ADR-102 — sube de 600 a 1000, y conviene saber que **no es que componer se haya hecho más
        // lento**: `compose_region` no sabe de plantas. Lo que cambió es la SUITE. Desde que el mundo
        // servido tiene dos plantas, cada test que lo construye hace el doble de trabajo, y esto es un
        // reloj de pared medido mientras otros dieciséis tests se pelean por la CPU: a 729 ms medidos
        // aquí y 20-63 en release, lo que la aserción vigilaba ya lo vigila mal. Queda como red contra
        // un tirón de un orden de magnitud, que es lo único que a esta altura sigue midiendo.
        assert!(
            elapsed.as_millis() < 1000,
            "región ({rx},{rz}): componer costó {} ms y ocurre al cruzar la frontera",
            elapsed.as_millis()
        );
    }

    println!("[wg3] región de {REGION_CHUNKS} chunks: llenado mínimo {worst_fill:.0} %");
}

/// SONDA — barrido del tamaño de región.
///
/// `#[ignore]` porque no afirma nada: busca un número. Lánzala con
/// `cargo test --manifest-path backend/Cargo.toml region_size_sweep -- --ignored --nocapture`.
///
/// La primera elección (8 chunks = 400 m) salió de la extensión de los mundos SIN acotar, y medirla
/// la desmintió: llenados del 1 al 12 %. La extensión no es el dato bueno —un mundo puede medir
/// 900 m y ser cuatro ramas finas—; el dato bueno es la SUPERFICIE CONSTRUIDA, y de ahí sale el
/// lado que la contiene sin sobrarle medio kilómetro de vacío.
#[test]
#[ignore = "sonda de dimensionado: busca el lado de región, no comprueba el código"]
fn region_size_sweep() {
    let m = real_manifest();

    for chunks in [2i32, 3, 4, 6, 8] {
        let side = chunks as f32 * 50.0;
        let area = side * side;
        let mut fills = Vec::new();
        let mut counts = Vec::new();

        for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, -1), (3, -2), (7, 11), (2, 5)] {
            let (min_x, min_z) = (rx as f32 * side, rz as f32 * side);
            let settings = compose::Wg3ComposerSettings {
                budget: INTERIM_BUDGET,
                close_loops: true,
                bounds: Some((min_x, min_z, min_x + side, min_z + side)),
                ..compose::Wg3ComposerSettings::default()
            };
            // Misma semilla por región que en producción, para que el barrido mida el mundo real.
            let seed = Wg3RegionCoord { x: rx, z: rz }.composer_seed(SERVED_SEED);
            let w = compose::compose(seed, &m, &settings);

            let mut built = 0.0f32;
            for c in &w.placements {
                let piece = m.piece(c.placement.piece).expect("pieza");
                let (x0, z0, x1, z1) = c.placement.bounds(piece);
                built += (x1 - x0) * (z1 - z0);
            }
            fills.push(built / area * 100.0);
            counts.push(w.placements.len());
        }

        let mean: f32 = fills.iter().sum::<f32>() / fills.len() as f32;
        let min = fills.iter().cloned().fold(f32::MAX, f32::min);
        let max = fills.iter().cloned().fold(f32::MIN, f32::max);
        println!(
            "[wg3] región {chunks} chunks ({side:.0} m): llenado medio {mean:.0} % \
             (min {min:.0}, max {max:.0}), piezas {counts:?}"
        );
    }
}

/// SONDA — ¿cuánto sube el LLENADO si se deja que las piezas compartan pared?
///
/// `#[ignore]` porque no afirma nada: busca un número. Lánzala con
/// `cargo test --manifest-path backend/Cargo.toml probe_fill_with_overlap_allowed -- --ignored --nocapture`.
///
/// LA PREGUNTA QUE CONTESTA, y es una decisión de diseño esperando un dato. Hoy el compositor
/// rechaza toda candidata que pise algo ya colocado, así que cada pieza carga sus cuatro paredes y
/// entre dos salas contiguas hay DOS muros y un hueco. Eso es lo que se ve en un plano como cajas
/// sueltas unidas por tubos en vez de como un edificio, y por lo que el llenado se queda en ~20 %.
/// Antes de escribir el ADR del excavado hay que saber cuánto techo hay: si al permitir el solape el
/// llenado apenas se mueve, el problema no es la regla de solape y el ADR estaría atacando lo que no
/// es.
///
/// **EL LLENADO SE MIDE POR UNIÓN, NO SUMANDO HUELLAS.** `region_size_sweep` suma y hace bien —hoy no
/// hay solapes que descontar—, pero con holgura esa cuenta se dispara contando dos veces el terreno
/// compartido y diría que el mundo se llena cuando solo se están pisando. Aquí se rasteriza la
/// región a celdas de 0,5 m y se cuenta terreno ÚNICO.
///
/// El otro número, y es el que dice si el excavado tiene de qué tirar: **parejas de piezas que se
/// tocan**. Sin adyacencia no hay muro común que excavar, por mucho llenado que haya.
#[test]
#[ignore]
fn probe_fill_with_overlap_allowed() {
    const CELL: f32 = 0.5;
    let m = real_manifest();
    let side = REGION_CHUNKS as f32 * 50.0;
    let area = side * side;
    let cells_side = (side / CELL) as usize;

    println!(
        "[wg3] región de {REGION_CHUNKS} chunks ({side:.0} m). Llenado por UNIÓN de huellas, \
         celda {CELL} m."
    );

    for slack in [0.0f32, 0.15, 0.30, 0.60, 1.20, 2.50] {
        let mut fills = Vec::new();
        let mut counts = Vec::new();
        let mut touching = Vec::new();

        for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, -1), (3, -2), (7, 11), (2, 5)] {
            let (min_x, min_z) = (rx as f32 * side, rz as f32 * side);
            let settings = compose::Wg3ComposerSettings {
                budget: INTERIM_BUDGET,
                close_loops: true,
                bounds: Some((min_x, min_z, min_x + side, min_z + side)),
                overlap_slack_m: slack,
                ..compose::Wg3ComposerSettings::default()
            };
            let seed = Wg3RegionCoord { x: rx, z: rz }.composer_seed(SERVED_SEED);
            let w = compose::compose(seed, &m, &settings);

            // Unión: una celda cuenta una vez la pisen las piezas que la pisen.
            let mut grid = vec![false; cells_side * cells_side];
            let mut boxes = Vec::with_capacity(w.placements.len());
            for c in &w.placements {
                let piece = m.piece(c.placement.piece).expect("pieza");
                let (x0, z0, x1, z1) = c.placement.bounds(piece);
                boxes.push((x0, z0, x1, z1));

                let cx0 = (((x0 - min_x) / CELL).floor().max(0.0)) as usize;
                let cz0 = (((z0 - min_z) / CELL).floor().max(0.0)) as usize;
                let cx1 = (((x1 - min_x) / CELL).ceil().max(0.0) as usize).min(cells_side);
                let cz1 = (((z1 - min_z) / CELL).ceil().max(0.0) as usize).min(cells_side);
                for cz in cz0..cz1 {
                    for cx in cx0..cx1 {
                        grid[cz * cells_side + cx] = true;
                    }
                }
            }
            let used = grid.iter().filter(|c| **c).count() as f32 * CELL * CELL;
            fills.push(used / area * 100.0);
            counts.push(w.placements.len());

            // Parejas que se tocan CON FACHADA SUFICIENTE PARA UN VANO. Rozar en una esquina no
            // sirve: para excavar una puerta hacen falta 1,2 m de pared compartida. Y se separan
            // las que YA están conectadas por boca de las que no, porque solo las segundas son
            // adyacencias desperdiciadas — dos salas espalda contra espalda, cuatro paredes entre
            // ellas y ni un paso.
            let connected: std::collections::HashSet<(usize, usize)> = w
                .placements
                .iter()
                .enumerate()
                .filter_map(|(i, c)| c.parent.map(|p| (i.min(p), i.max(p))))
                .collect();

            let mut pairs = 0usize;
            for i in 0..boxes.len() {
                for j in (i + 1)..boxes.len() {
                    let (ax0, az0, ax1, az1) = boxes[i];
                    let (bx0, bz0, bx1, bz1) = boxes[j];
                    let gap_x = ax0.max(bx0) - ax1.min(bx1);
                    let gap_z = az0.max(bz0) - az1.min(bz1);
                    // Rozan en X y comparten fachada en Z, o al revés.
                    let touch_x = gap_x <= 0.05 && -gap_z >= 1.2;
                    let touch_z = gap_z <= 0.05 && -gap_x >= 1.2;
                    if (touch_x || touch_z) && !connected.contains(&(i, j)) {
                        pairs += 1;
                    }
                }
            }
            touching.push(pairs);
        }

        let mean: f32 = fills.iter().sum::<f32>() / fills.len() as f32;
        let min = fills.iter().cloned().fold(f32::MAX, f32::min);
        let max = fills.iter().cloned().fold(f32::MIN, f32::max);
        let pieces: usize = counts.iter().sum::<usize>() / counts.len();
        let pairs: usize = touching.iter().sum::<usize>() / touching.len();
        println!(
            "[wg3] holgura {slack:>4.2} m: llenado medio {mean:>5.1} % (min {min:>5.1}, \
             max {max:>5.1}) | piezas/región {pieces:>3} | adyacencias SIN puerta {pairs:>3}"
        );
    }
}

/// SONDA — el techo de la ABSORCIÓN: ¿cuánto mundo hay en los choques que hoy se tiran?
///
/// `#[ignore]` porque no afirma nada: busca un número. Lánzala con
/// `cargo test --manifest-path backend/Cargo.toml probe_absorption_ceiling -- --ignored --nocapture`.
///
/// LA IDEA QUE MIDE, y es de Joel: un pasillo que topa con una sala no se descarta. Se recorta
/// contra ella, le abre un vano y deja de expandirse — la sala manda sobre el pasillo. Hoy ese
/// choque se cuenta en `rejected_by_overlap` y se tira, así que el material ya existe y nadie lo
/// había mirado.
///
/// POR QUÉ ESTA MEDIDA Y NO LA DE ANTES. `probe_fill_with_overlap_allowed` preguntaba si dejar que
/// las piezas compartan pared llenaba el mundo, y la respuesta fue que no: de 31,7 % a 32,6 % con el
/// grosor de un muro, y 1 sola adyacencia sin puerta por región. Las piezas no acaban pegadas porque
/// el compositor las encadena boca con boca. La absorción no necesita que acaben pegadas: usa los
/// choques, que son otra cosa y sí abundan.
///
/// LOS TRES NÚMEROS, y cada uno descarta la idea por un sitio distinto:
///  · **choques** — si son pocos, no hay material y la idea muere aquí;
///  · **con fachada ≥ 1,2 m** — un choque de esquina no da un vano, da un roce;
///  · **destinos DISTINTOS** — cien choques contra la misma sala son una puerta, no cien. Éste es el
///    que dice cuántas conexiones nuevas habría de verdad.
#[test]
#[ignore]
fn probe_absorption_ceiling() {
    const DOOR_M: f32 = 1.2;
    let m = real_manifest();
    let side = REGION_CHUNKS as f32 * 50.0;

    println!(
        "[wg3] región de {REGION_CHUNKS} chunks ({side:.0} m). Choques por solape que hoy se \
         descartan, y qué daría absorberlos."
    );

    let mut total_new = 0usize;
    let mut regions = 0usize;
    for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, -1), (3, -2), (7, 11), (2, 5)] {
        let (min_x, min_z) = (rx as f32 * side, rz as f32 * side);
        let settings = compose::Wg3ComposerSettings {
            budget: INTERIM_BUDGET,
            close_loops: true,
            bounds: Some((min_x, min_z, min_x + side, min_z + side)),
            collect_absorption_hits: true,
            ..compose::Wg3ComposerSettings::default()
        };
        let seed = Wg3RegionCoord { x: rx, z: rz }.composer_seed(SERVED_SEED);
        let w = compose::compose(seed, &m, &settings);

        let wide: Vec<_> = w
            .absorption_hits
            .iter()
            .filter(|h| h.frontage_m >= DOOR_M)
            .collect();

        // Un destino distinto es una conexión nueva; los repetidos son la misma puerta pedida
        // muchas veces desde bocas distintas.
        let destinations: std::collections::HashSet<usize> =
            wide.iter().map(|h| h.hit_node).collect();

        // ¿Contra QUÉ se choca? Si lo que absorbe son salas y lo absorbido pasillos, la regla de
        // Joel —la sala manda— cae sola. Si es al revés, la jerarquía hay que pensarla.
        let mut into_bigger = 0usize;
        for h in &wide {
            let hit_scale = m
                .piece(w.placements[h.hit_node].placement.piece)
                .expect("pieza")
                .scale;
            let cand_scale = m.piece(h.candidate_piece).expect("pieza").scale;
            if hit_scale > cand_scale {
                into_bigger += 1;
            }
        }

        let pieces = w.placements.len();
        println!(
            "[wg3] región ({rx:>2},{rz:>2}): {pieces:>3} piezas | choques {:>5} \
             (con fachada ≥ {DOOR_M} m: {:>4}) | destinos DISTINTOS {:>3} \
             | de los anchos, {into_bigger:>4} son contra algo MAYOR",
            w.absorption_hits.len(),
            wide.len(),
            destinations.len(),
        );
        total_new += destinations.len();
        regions += 1;
    }

    println!(
        "[wg3] conexiones nuevas que daría la absorción: {} por región de media",
        total_new / regions.max(1)
    );
}

/// SONDA — ADR-099 paso 1: ¿cuántas absorciones ocurren DE VERDAD?
///
/// `#[ignore]` porque no afirma nada: busca un número. Lánzala con
/// `cargo test --manifest-path backend/Cargo.toml probe_absorption_applied -- --ignored --nocapture`.
///
/// `probe_absorption_ceiling` predijo ~20 conexiones por región contando los choques que hoy se
/// tiran. Ese número es un TECHO y no una promesa: se midió sobre TODOS los rechazos por solape,
/// mientras que la absorción sólo se intenta donde el catálogo ya no tiene con qué seguir. La
/// distancia entre los dos números es lo que esta sonda mide, y sin ella el techo se leería como
/// resultado.
///
/// Se mira además `forced_caps`, porque una absorción es una boca que ANTES se sellaba: si absorbe
/// mucho y los tapones no bajan, es que está absorbiendo bocas que se iban a resolver solas.
#[test]
#[ignore]
fn probe_absorption_applied() {
    // Alto a propósito: esta sonda busca el TECHO de lo que la absorción puede llegar a hacer, no
    // el valor que se servirá. La perilla de producción se elige después, con estos números.
    const ABSORB_CHANCE: f32 = 1.0;

    let m = real_manifest();
    let side = REGION_CHUNKS as f32 * 50.0;

    println!(
        "[wg3] ADR-099 paso 1 — absorciones aplicadas con chance {ABSORB_CHANCE}, sin absorber \
         contra con absorber."
    );

    let mut total = 0u32;
    let mut regions = 0usize;
    for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, -1), (3, -2), (7, 11), (2, 5)] {
        let (min_x, min_z) = (rx as f32 * side, rz as f32 * side);
        let seed = Wg3RegionCoord { x: rx, z: rz }.composer_seed(SERVED_SEED);
        let base = compose::Wg3ComposerSettings {
            budget: INTERIM_BUDGET,
            close_loops: true,
            bounds: Some((min_x, min_z, min_x + side, min_z + side)),
            ..compose::Wg3ComposerSettings::default()
        };

        let off = compose::compose(seed, &m, &base);
        let on = compose::compose(
            seed,
            &m,
            &compose::Wg3ComposerSettings {
                absorb_chance: ABSORB_CHANCE,
                ..base.clone()
            },
        );

        println!(
            "[wg3] región ({rx:>2},{rz:>2}): piezas {:>3} → {:>3} | tapones forzados {:>3} → {:>3} \
             | tramos {:>3} → {:>3} | ABSORCIONES {:>3} (vanos {:>3}) \
             | intentos {:>3} (estrecha {:>3}, sin destino {:>3}, bloqueada {:>3})",
            off.placements.len(),
            on.placements.len(),
            off.forced_caps,
            on.forced_caps,
            off.segments.len(),
            on.segments.len(),
            on.absorbed,
            on.carves.len(),
            on.absorb_tries,
            on.absorb_narrow,
            on.absorb_no_target,
            on.absorb_blocked,
        );
        total += on.absorbed;
        regions += 1;
    }

    println!(
        "[wg3] absorciones aplicadas: {} por región de media (el TECHO decía ~20)",
        total / regions.max(1) as u32
    );
}

/// ADR-099 D3 — EL TEST QUE DECIDE SI LA ABSORCIÓN VALE PARA ALGO: por el vano SE PASA.
///
/// Un tramo absorbido declara una boca contra la pieza que ha topado. Si el vano no se excava —o se
/// excava y el rasterizado conservador se lo come— esa boca da a materia maciza: el cliente dibuja
/// el paso abierto y el servidor no deja entrar. Es el fallo que ADR-098 enmienda 2 midió, y es el
/// que no sale en una captura.
///
/// Se pregunta al RÁSTER, que es lo que resuelve el movimiento, y no al grafo, que sólo dice lo que
/// el compositor cree.
#[test]
fn the_absorbed_doorway_is_open_in_the_raster() {
    let m = real_manifest();
    let side = REGION_CHUNKS as f32 * 50.0;

    let mut checked = 0usize;
    for (rx, rz) in [(0, 0), (0, 1), (-1, -1), (2, 5)] {
        let (min_x, min_z) = (rx as f32 * side, rz as f32 * side);
        let seed = Wg3RegionCoord { x: rx, z: rz }.composer_seed(SERVED_SEED);
        let composed = compose::compose(
            seed,
            &m,
            &compose::Wg3ComposerSettings {
                budget: INTERIM_BUDGET,
                close_loops: true,
                bounds: Some((min_x, min_z, min_x + side, min_z + side)),
                absorb_chance: 1.0,
                ..compose::Wg3ComposerSettings::default()
            },
        );
        if composed.carves.is_empty() {
            continue;
        }

        let placements: Vec<_> = composed.placements.iter().map(|c| c.placement).collect();

        for k in &composed.carves {
            // El centro del vano, que es por donde se pasaría.
            let cx = (k.x_cm + k.size_x_cm / 2) as f32 / 100.0;
            let cz = (k.z_cm + k.size_z_cm / 2) as f32 / 100.0;
            let probe_y = k.bottom_y_cm as f32 / 100.0 + 1.0;

            let coord = chunk::Wg3ChunkCoord::containing(cx, cz);
            let raster = chunk::build_chunk_raster_with_carves(
                &m,
                &placements,
                &composed.segments,
                &composed.carves,
                coord,
            );

            // Con el vano abierto tiene que haber HUECO a la altura de la cabeza. Sin excavar, la
            // pared del absorbido llega hasta el techo y no lo hay.
            let headroom = raster.headroom_above_floor(cx, probe_y, cz);
            assert!(
                headroom.is_some_and(|h| h >= 1.8),
                "el vano de ({cx:.2}, {cz:.2}) está tapiado: hueco {headroom:?}"
            );
            checked += 1;
        }
    }

    assert!(
        checked > 0,
        "ninguna región produjo un vano, así que este test no ha probado nada"
    );
}

/// SONDA — ADR-099: ¿el vano da a algún sitio, o a un armario?
///
/// `#[ignore]` porque no afirma nada: busca un número. Lánzala con
/// `cargo test --manifest-path backend/Cargo.toml probe_absorption_leads_somewhere -- --ignored --nocapture`.
///
/// LA PREGUNTA QUE NO CONTESTA NINGUNA DE LAS OTRAS. `the_absorbed_doorway_is_open_in_the_raster`
/// prueba que por el vano SE PASA; no prueba que al otro lado haya algo. Y las piezas del catálogo
/// están llenas de materia interior — `room_core` lleva un núcleo de 8 × 7 m, `hall_large` paredes
/// parciales, y varias tienen columnas de medio metro. Un vano contra eso abre un agujero a un
/// bloque macizo: se ve una puerta, se cruza, y detrás hay pared.
///
/// Se mide andando HACIA ADENTRO desde el vano y mirando cuánto se avanza antes de chocar. Es lo
/// más cerca que se puede estar de «¿esto se ve bien?» sin montar el cliente.
#[test]
#[ignore]
fn probe_absorption_leads_somewhere() {
    const CELL: f32 = 0.5;
    const HEAD_M: f32 = 1.8;
    // Lo que hay que poder avanzar hacia adentro para que la puerta lleve a un sitio y no a un
    // hueco entre dos paredes.
    const WANT_M: f32 = 3.0;

    let m = real_manifest();
    let side_m = REGION_CHUNKS as f32 * 50.0;

    let mut good = 0usize;
    let mut shallow = 0usize;
    let mut blind = 0usize;
    let mut depths: Vec<f32> = Vec::new();

    for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, -1), (3, -2), (7, 11), (2, 5)] {
        let (min_x, min_z) = (rx as f32 * side_m, rz as f32 * side_m);
        let seed = Wg3RegionCoord { x: rx, z: rz }.composer_seed(SERVED_SEED);
        let composed = compose::compose(
            seed,
            &m,
            &compose::Wg3ComposerSettings {
                budget: INTERIM_BUDGET,
                close_loops: true,
                bounds: Some((min_x, min_z, min_x + side_m, min_z + side_m)),
                absorb_chance: 1.0,
                ..compose::Wg3ComposerSettings::default()
            },
        );
        let placements: Vec<_> = composed.placements.iter().map(|c| c.placement).collect();

        for k in &composed.carves {
            let cx = (k.x_cm + k.size_x_cm / 2) as f32 / 100.0;
            let cz = (k.z_cm + k.size_z_cm / 2) as f32 / 100.0;
            let probe_y = k.bottom_y_cm as f32 / 100.0 + 1.0;

            // SE ENTRA POR EL EJE ESTRECHO. La caja del vano mide el grosor del contacto (1 m) en
            // la dirección de paso y el ANCHO de la puerta (2,4 o 5) a lo ancho, así que el eje de
            // avance es el MENOR. Escrito al revés —y lo estuvo— la sonda camina a lo largo de la
            // puerta, se mete en la pared de al lado y devuelve 1,0 m para todos los vanos: un
            // número redondo y repetido, que es justo como se ve un eje equivocado.
            let along_x = k.size_x_cm < k.size_z_cm;

            let standable = |x: f32, z: f32| -> bool {
                let coord = chunk::Wg3ChunkCoord::containing(x, z);
                let raster = chunk::build_chunk_raster_with_carves(
                    &m,
                    &placements,
                    &composed.segments,
                    &composed.carves,
                    coord,
                );
                raster
                    .headroom_above_floor(x, probe_y, z)
                    .is_some_and(|h| h >= HEAD_M)
            };

            // SE TOMA EL PEOR DE LOS DOS SENTIDOS, y la diferencia no es cosmética. Uno de los dos
            // lados es el tramo que se acaba de tender —abierto por construcción, hasta 25 m—, así
            // que quedarse con el mejor mide el pasillo propio y da 20 m siempre. El lado que dice
            // si el vano sirve es el OTRO, y como la sonda no sabe cuál es, se queda con el mínimo.
            let mut best = f32::MAX;
            for sign in [1.0f32, -1.0] {
                let mut depth = 0.0f32;
                let mut step = CELL;
                // Tope generoso: con `WANT_M * 2` la mediana salía clavada en el tope y un número
                // que toca el techo de su propia sonda no es una medida.
                while step <= 20.0 {
                    let (x, z) = if along_x {
                        (cx + sign * step, cz)
                    } else {
                        (cx, cz + sign * step)
                    };
                    if !standable(x, z) {
                        break;
                    }
                    depth = step;
                    step += CELL;
                }
                best = best.min(depth);
            }

            depths.push(best);
            if best >= WANT_M {
                good += 1;
            } else if best >= 1.0 {
                shallow += 1;
            } else {
                blind += 1;
            }
        }
    }

    depths.sort_by(f32::total_cmp);
    let median = depths.get(depths.len() / 2).copied().unwrap_or(0.0);
    println!(
        "[wg3] vanos: {} en total | llevan a sitio (≥{WANT_M} m) {good} | cortos (1–3 m) {shallow} \
         | CIEGOS (<1 m) {blind} | avance mediano {median:.1} m",
        depths.len()
    );
}

/// SONDA — ADR-099 paso 2: ¿la absorción con vano AGRANDA lo que se puede andar?
///
/// `#[ignore]` porque no afirma nada: busca un número. Lánzala con
/// `cargo test --manifest-path backend/Cargo.toml probe_absorption_reach -- --ignored --nocapture`.
///
/// Es la pregunta que decide si ADR-099 vale para algo. El paso 1 ya medía absorciones, pero una
/// absorción sin vano es un callejón: geometría, no topología. Aquí se mide lo único que le importa
/// a quien juega — **cuántos metros cuadrados se alcanzan a pie desde donde apareces** — y se mide
/// sobre el RÁSTER, que es lo que resuelve el movimiento.
///
/// Inundación a ras de suelo con una sola cota por celda: no distingue altillos, y por eso NO
/// sustituye a `probe_how_much_of_the_region_can_be_walked_from_the_spawn`. Aquí sólo hace falta
/// comparar dos mundos con la misma vara.
#[test]
#[ignore]
fn probe_absorption_reach() {
    const CELL: f32 = 0.5;
    const HEAD_M: f32 = 1.8;
    let m = real_manifest();
    let side_m = REGION_CHUNKS as f32 * 50.0;
    let cells = (side_m / CELL) as usize;

    // Alcanzable a pie desde el centro de la región, en m².
    let reach = |composed: &compose::Wg3ComposedWorld, min_x: f32, min_z: f32| -> f32 {
        let placements: Vec<_> = composed.placements.iter().map(|c| c.placement).collect();
        let chunks = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        let mut rasters = Vec::with_capacity(chunks * chunks);
        for cz in 0..chunks {
            for cx in 0..chunks {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                rasters.push(chunk::build_chunk_raster_with_carves(
                    &m,
                    &placements,
                    &composed.segments,
                    &composed.carves,
                    coord,
                ));
            }
        }

        let standable = |ix: usize, iz: usize| -> bool {
            let x = min_x + ix as f32 * CELL + CELL * 0.5;
            let z = min_z + iz as f32 * CELL + CELL * 0.5;
            let coord = chunk::Wg3ChunkCoord::containing(x, z);
            let (dx, dz) = (coord.x - base.x, coord.z - base.z);
            if dx < 0 || dz < 0 || dx as usize >= chunks || dz as usize >= chunks {
                return false;
            }
            let Some(r) = rasters.get(dz as usize * chunks + dx as usize) else {
                return false;
            };
            let column = r.column_at(x, z);
            for (i, span) in column.iter().enumerate() {
                let head = match column.get(i + 1) {
                    Some(next) => (next.bottom_cm - span.top_cm) as f32 / 100.0,
                    None => f32::MAX,
                };
                // Con techo y no a cielo abierto: la cara de arriba de una pared cumple «hay suelo
                // y hay hueco» y no es sitio por donde se ande.
                if (HEAD_M..=CEILING_CAP_M).contains(&head) {
                    return true;
                }
            }
            false
        };

        // Arranca en la celda pisable más cercana al centro, que es donde aparece el jugador.
        let mid = cells / 2;
        let mut start = None;
        'outer: for r in 0..mid {
            for dz in -(r as i32)..=(r as i32) {
                for dx in -(r as i32)..=(r as i32) {
                    let (ix, iz) = (mid as i32 + dx, mid as i32 + dz);
                    if ix < 0 || iz < 0 || ix as usize >= cells || iz as usize >= cells {
                        continue;
                    }
                    if standable(ix as usize, iz as usize) {
                        start = Some((ix as usize, iz as usize));
                        break 'outer;
                    }
                }
            }
        }
        let Some(start) = start else {
            return 0.0;
        };

        let mut seen = vec![false; cells * cells];
        let mut stack = vec![start];
        seen[start.1 * cells + start.0] = true;
        let mut count = 0usize;
        while let Some((ix, iz)) = stack.pop() {
            count += 1;
            let neighbours = [
                (ix.wrapping_sub(1), iz),
                (ix + 1, iz),
                (ix, iz.wrapping_sub(1)),
                (ix, iz + 1),
            ];
            for (nx, nz) in neighbours {
                if nx >= cells || nz >= cells || seen[nz * cells + nx] {
                    continue;
                }
                if standable(nx, nz) {
                    seen[nz * cells + nx] = true;
                    stack.push((nx, nz));
                }
            }
        }
        count as f32 * CELL * CELL
    };

    // BARRIDO, y hace falta: absorber tiene un coste —consume la boca y corta la rama— y un
    // beneficio —el vano—, y los dos crecen con la perilla. Un solo valor no dice dónde está el
    // punto en que se cruzan, y a 1,0 el coste ya gana.
    let mut base_total = 0.0f32;
    for chance in [0.0f32, 0.05, 0.10, 0.20, 0.35, 0.60, 1.0] {
        let mut sum = 0.0f32;
        let mut pieces = 0usize;
        let mut carves = 0usize;
        let mut rings = 0u32;
        let mut merges = 0u32;
        for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, -1), (3, -2), (7, 11), (2, 5)] {
            let (min_x, min_z) = (rx as f32 * side_m, rz as f32 * side_m);
            let seed = Wg3RegionCoord { x: rx, z: rz }.composer_seed(SERVED_SEED);
            let w = compose::compose(
                seed,
                &m,
                &compose::Wg3ComposerSettings {
                    budget: INTERIM_BUDGET,
                    close_loops: true,
                    bounds: Some((min_x, min_z, min_x + side_m, min_z + side_m)),
                    absorb_chance: chance,
                    ..compose::Wg3ComposerSettings::default()
                },
            );
            sum += reach(&w, min_x, min_z);
            pieces += w.placements.len();
            carves += w.carves.len();
            rings += w.absorb_rings;
            merges += w.absorb_merges;
        }
        if chance == 0.0 {
            base_total = sum;
        }
        println!(
            "[wg3] chance {chance:>4.2}: andable {sum:>8.0} m² ({:+5.1} %) | piezas {pieces:>4} \
             | vanos {carves:>3} (atajos {rings:>3}, unen islas {merges:>3})",
            if base_total > 0.0 {
                (sum - base_total) / base_total * 100.0
            } else {
                0.0
            }
        );
    }
}

/// SONDA — ADR-099: DENSIFICAR. ¿Plantar piezas en el hueco llena el mundo?
///
/// `#[ignore]` porque no afirma nada. Lánzala con
/// `cargo test --manifest-path backend/Cargo.toml probe_densify_sweep -- --ignored --nocapture`.
///
/// Es la palanca contra el VACÍO, que es lo que se ve en el isométrico de la semilla 42: manchas
/// densas de salas unidas por tubos de decenas de metros, con negro en medio. Ninguna de las
/// anteriores lo movía —compartir pared da +0,9 puntos, la absorción cambia topología sin tocar
/// superficie— porque todas trabajaban sobre las bocas, y el hueco está vacío justamente porque
/// **ahí no llega ninguna boca**.
///
/// Se miden tres cosas y hacen falta las tres:
///  · **llenado** por unión de huellas — cuánto suelo hay;
///  · **andable** desde el spawn — cuánto de eso se alcanza, que es lo único que se juega;
///  · **intentos contra plantadas** — si se gastan muchos y entran pocas, el hueco ya está lleno y
///    subir la perilla no daría nada.
#[test]
#[ignore]
fn probe_densify_sweep() {
    const CELL: f32 = 0.5;
    let m = real_manifest();
    let side_m = REGION_CHUNKS as f32 * 50.0;
    let area = side_m * side_m;
    let cells_side = (side_m / CELL) as usize;

    for attempts in [0usize, 15, 25, 40, 50, 65, 85, 120] {
        let mut fill = 0.0f32;
        let mut pieces = 0usize;
        let mut planted = 0u32;
        let mut segments = 0usize;
        let mut walk = 0.0f32;
        let mut floor = 0.0f32;

        for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, -1), (3, -2), (7, 11), (2, 5)] {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let (min_x, min_z, _, _) = region.bounds();
            // LOS AJUSTES SERVIDOS, no los del defecto. Con `Wg3ComposerSettings::default()` el
            // enrutador va apagado, así que todo lo plantado nace isla y el número de llenado sale
            // precioso mientras el mundo no se puede andar. La primera versión de esta sonda medía
            // eso y daba «tramos 0» sin que nadie lo hubiera pedido.
            let settings = compose::Wg3ComposerSettings {
                densify_attempts: attempts,
                ..region_settings(&m, SERVED_SEED, region)
            };
            let w = compose::compose(region.composer_seed(SERVED_SEED), &m, &settings);

            // Llenado por UNIÓN: sumar huellas contaría dos veces nada aquí, pero la unión es la
            // vara con la que se midieron las otras palancas y hay que compararlas con la misma.
            let mut grid = vec![false; cells_side * cells_side];
            for c in &w.placements {
                let piece = m.piece(c.placement.piece).expect("pieza");
                let (x0, z0, x1, z1) = c.placement.bounds(piece);
                let cx0 = (((x0 - min_x) / CELL).floor().max(0.0)) as usize;
                let cz0 = (((z0 - min_z) / CELL).floor().max(0.0)) as usize;
                let cx1 = (((x1 - min_x) / CELL).ceil().max(0.0) as usize).min(cells_side);
                let cz1 = (((z1 - min_z) / CELL).ceil().max(0.0) as usize).min(cells_side);
                for cz in cz0..cz1 {
                    for cx in cx0..cx1 {
                        grid[cz * cells_side + cx] = true;
                    }
                }
            }
            fill += grid.iter().filter(|c| **c).count() as f32 * CELL * CELL / area * 100.0;
            pieces += w.placements.len();
            planted += w.densified;
            segments += w.segments.len();
            let (r, t) = reach_and_total(&m, &w, min_x, min_z);
            walk += r;
            floor += t;
        }

        println!(
            "[wg3] intentos {attempts:>5}: llenado {:>5.1} % | ANDABLE {walk:>8.0} de {floor:>8.0} m² \
             ({:>5.1} % alcanzable) | piezas {pieces:>4} (plantadas {planted:>4}) | tramos {segments:>4}",
            fill / 7.0,
            if floor > 0.0 { walk / floor * 100.0 } else { 0.0 }
        );
    }
}

/// SONDA — ADR-099: el PRESUPUESTO DEL ENRUTADOR con el mundo densificado.
///
/// `#[ignore]` porque no afirma nada. Lánzala con
/// `cargo test --manifest-path backend/Cargo.toml probe_router_budget_sweep -- --ignored --nocapture`.
///
/// Joel, mirando el isométrico densificado: «aún hay zonas que no son accesibles». Tenía razón y el
/// número lo dice: a 40 intentos de densificado sólo el **82 % del suelo es alcanzable**, y a 120
/// baja al 60 %. Lo que lo delata es que los TRAMOS se quedan clavados en ~380 por más piezas que se
/// planten — un número que no se mueve no es un resultado, es un tope.
///
/// El tope es `max_connectors: 12` por composición. Se eligió con regiones de ~30 piezas y el
/// densificado las pone en 80: el enrutador se queda sin presupuesto antes de llegar a la mitad de
/// las islas. Esta sonda mide cuánto hay que subirlo y qué cuesta en tiempo, que es la otra mitad
/// de la decisión — componer ocurre al cruzar una frontera de región y se nota como un tirón.
#[test]
#[ignore]
fn probe_router_budget_sweep() {
    let m = real_manifest();

    for (densify, budget) in [
        (0usize, 12usize),
        (40, 12),
        (40, 30),
        (40, 60),
        (40, 120),
        (40, 250),
        (120, 250),
        (120, 500),
    ] {
        let mut walk = 0.0f32;
        let mut floor = 0.0f32;
        let mut segments = 0usize;
        let mut pieces = 0usize;
        let mut worst_ms = 0.0f32;

        for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, -1), (3, -2), (7, 11), (2, 5)] {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let (min_x, min_z, _, _) = region.bounds();
            let base = region_settings(&m, SERVED_SEED, region);
            let route = base.route.clone().map(|r| route::RouteSettings {
                max_connectors: budget,
                // Los anillos suben con el presupuesto: dejarlos en 4 haría que todo lo nuevo se
                // fuera a unir islas y el mundo siguiera siendo un árbol, sólo que más grande.
                max_rings: (budget / 3).max(4),
                ..r
            });
            let settings = compose::Wg3ComposerSettings {
                densify_attempts: densify,
                route,
                ..base
            };

            let started = std::time::Instant::now();
            let w = compose::compose(region.composer_seed(SERVED_SEED), &m, &settings);
            worst_ms = worst_ms.max(started.elapsed().as_secs_f32() * 1000.0);

            let (r, t) = reach_and_total(&m, &w, min_x, min_z);
            walk += r;
            floor += t;
            segments += w.segments.len();
            pieces += w.placements.len();
        }

        println!(
            "[wg3] densificado {densify:>3}, conectores {budget:>3}: ANDABLE {walk:>8.0} de \
             {floor:>8.0} m² ({:>5.1} % alcanzable) | piezas {pieces:>4} | tramos {segments:>4} \
             | peor composición {worst_ms:>6.0} ms",
            if floor > 0.0 {
                walk / floor * 100.0
            } else {
                0.0
            }
        );
    }
}

/// Superficie alcanzable a pie desde el centro de la región, en m².
///
/// Aparte y no dentro de una sonda porque lo miden dos, y dos copias de una inundación divergen: la
/// de la absorción y la del densificado tienen que responder con la MISMA vara o sus números no se
/// pueden poner en la misma frase.
///
/// Inundación a ras de suelo, una cota por celda: no distingue altillos, así que NO sustituye a
/// `probe_how_much_of_the_region_can_be_walked_from_the_spawn`. Sirve para comparar dos mundos.
fn reach_from_centre(
    m: &Wg3Manifest,
    composed: &compose::Wg3ComposedWorld,
    min_x: f32,
    min_z: f32,
) -> f32 {
    reach_and_total(m, composed, min_x, min_z).0
}

/// Lo mismo, pero devolviendo también el suelo PISABLE total de la región.
///
/// La diferencia entre los dos es lo que Joel ve y las sondas no decían: superficie que existe, que
/// tiene suelo y techo, y a la que no se llega desde donde apareces. Un «+51 % de andable» puede
/// venir de un mundo mejor conectado o de un mundo más grande igual de roto, y sin el total no se
/// distinguen.
fn reach_and_total(
    m: &Wg3Manifest,
    composed: &compose::Wg3ComposedWorld,
    min_x: f32,
    min_z: f32,
) -> (f32, f32) {
    const CELL: f32 = 0.5;
    const HEAD_M: f32 = 1.8;

    let placements: Vec<_> = composed.placements.iter().map(|c| c.placement).collect();
    let chunks = REGION_CHUNKS as usize;
    let cells = (REGION_CHUNKS as f32 * 50.0 / CELL) as usize;
    let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);

    let mut rasters = Vec::with_capacity(chunks * chunks);
    for cz in 0..chunks {
        for cx in 0..chunks {
            let coord = chunk::Wg3ChunkCoord {
                x: base.x + cx as i32,
                z: base.z + cz as i32,
            };
            rasters.push(chunk::build_chunk_raster_with_carves(
                m,
                &placements,
                &composed.segments,
                &composed.carves,
                coord,
            ));
        }
    }

    let standable = |ix: usize, iz: usize| -> bool {
        let x = min_x + ix as f32 * CELL + CELL * 0.5;
        let z = min_z + iz as f32 * CELL + CELL * 0.5;
        let coord = chunk::Wg3ChunkCoord::containing(x, z);
        let (dx, dz) = (coord.x - base.x, coord.z - base.z);
        if dx < 0 || dz < 0 || dx as usize >= chunks || dz as usize >= chunks {
            return false;
        }
        let Some(r) = rasters.get(dz as usize * chunks + dx as usize) else {
            return false;
        };
        let column = r.column_at(x, z);
        for (i, span) in column.iter().enumerate() {
            let head = match column.get(i + 1) {
                Some(next) => (next.bottom_cm - span.top_cm) as f32 / 100.0,
                None => f32::MAX,
            };
            // Con techo y no a cielo abierto: la cara de arriba de una pared cumple «hay suelo y
            // hay hueco» y contarla metería los TEJADOS en la cuenta.
            if (HEAD_M..=CEILING_CAP_M).contains(&head) {
                return true;
            }
        }
        false
    };

    // El suelo pisable TOTAL, alcanzable o no. Se calcula entero y una vez: `standable` reconstruye
    // el ráster del chunk en cada consulta, así que preguntarlo dos veces cuesta el doble.
    let mut walkable_grid = vec![false; cells * cells];
    let mut total_cells = 0usize;
    for iz in 0..cells {
        for ix in 0..cells {
            if standable(ix, iz) {
                walkable_grid[iz * cells + ix] = true;
                total_cells += 1;
            }
        }
    }
    let total = total_cells as f32 * CELL * CELL;

    let mid = cells / 2;
    let mut start = None;
    'outer: for r in 0..mid {
        for dz in -(r as i32)..=(r as i32) {
            for dx in -(r as i32)..=(r as i32) {
                let (ix, iz) = (mid as i32 + dx, mid as i32 + dz);
                if ix < 0 || iz < 0 || ix as usize >= cells || iz as usize >= cells {
                    continue;
                }
                if walkable_grid[iz as usize * cells + ix as usize] {
                    start = Some((ix as usize, iz as usize));
                    break 'outer;
                }
            }
        }
    }
    let Some(start) = start else {
        return (0.0, total);
    };

    let mut seen = vec![false; cells * cells];
    let mut stack = vec![start];
    seen[start.1 * cells + start.0] = true;
    let mut count = 0usize;
    while let Some((ix, iz)) = stack.pop() {
        count += 1;
        for (nx, nz) in [
            (ix.wrapping_sub(1), iz),
            (ix + 1, iz),
            (ix, iz.wrapping_sub(1)),
            (ix, iz + 1),
        ] {
            if nx >= cells || nz >= cells || seen[nz * cells + nx] {
                continue;
            }
            if walkable_grid[nz * cells + nx] {
                seen[nz * cells + nx] = true;
                stack.push((nx, nz));
            }
        }
    }
    (count as f32 * CELL * CELL, total)
}

/// SONDA — el mundo en ISOMÉTRICA, para mirarlo con ojos en vez de con números.
///
/// `#[ignore]` porque no afirma nada. Lánzala con
/// `WG3_ISO_DIR=<carpeta> WG3_SEED=42 cargo test ... dump_isometric -- --ignored --nocapture`.
///
/// El volcador de planta (`dump_region_maps`) contesta a la topología: qué se anda y qué no. No
/// contesta a la forma, que es lo que se pregunta cuando uno dice «¿esto se verá bien?». Esto
/// tampoco es el juego —ni materiales, ni luz, ni el vestido de los conectores— pero enseña VOLUMEN:
/// alturas, escalas relativas, y si el mundo se lee como edificio o como cajas sueltas.
///
/// **Los techos NO se dibujan.** Con ellos sólo se ve una manta gris: un isométrico de un interior
/// es siempre una sección. Suelos y paredes, que es lo que da la forma.
#[test]
#[ignore]
fn dump_isometric() {
    // Proyección isométrica clásica 2:1. `Y` sube en pantalla.
    const ISO_X: f32 = 0.866; // cos 30°
    const ISO_Y: f32 = 0.5; // sin 30°
    const PX: f32 = 3.2; // píxeles por metro
    const MARGIN: f32 = 40.0;

    let dir = std::env::var("WG3_ISO_DIR").expect("WG3_ISO_DIR: carpeta donde escribir");
    let seed: u64 = std::env::var("WG3_SEED")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(SERVED_SEED);
    let absorb: f32 = std::env::var("WG3_ABSORB")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(0.0);
    let densify: usize = std::env::var("WG3_DENSIFY")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(0);
    // Cuántos chunks de lado se dibujan, centrados en el origen de la región.
    let span: i32 = std::env::var("WG3_ISO_CHUNKS")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(REGION_CHUNKS);

    let m = real_manifest();
    let region0 = Wg3RegionCoord { x: 0, z: 0 };
    let (rmin_x, rmin_z, _, _) = region0.bounds();

    // Ventana de chunks que se dibuja, desde el origen de la región (0,0).
    let win_min_x = rmin_x;
    let win_min_z = rmin_z;
    let win_max_x = rmin_x + span as f32 * chunk::WG3_CHUNK_M;
    let win_max_z = rmin_z + span as f32 * chunk::WG3_CHUNK_M;

    // UNA REGIÓN SON 3 CHUNKS, así que pedir más obliga a componer las VECINAS. Componerlas por
    // separado y dibujarlas juntas es exactamente lo que hace el servidor (ADR-096): cada región es
    // función pura de su coordenada y sólo se ponen de acuerdo en la junta. Sin este bucle, pedir 9
    // chunks devolvía el mismo dibujo de 3 — el mundo no acaba ahí, es que no se había compuesto.
    let regions_across = span.div_euclid(REGION_CHUNKS) + 1;
    let mut boxes: Vec<(placement::PlacedBox, bool)> = Vec::new();
    let mut pieces_drawn = 0usize;
    let mut segments_drawn = 0usize;

    for rz in 0..regions_across {
        for rx in 0..regions_across {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let settings = compose::Wg3ComposerSettings {
                absorb_chance: absorb,
                densify_attempts: densify,
                ..region_settings(&m, seed, region)
            };
            let world = Wg3ServedWorld::compose_region_with(&m, seed, region, &settings);

            for p in world.placements() {
                let Some(piece) = m.piece(p.piece) else {
                    continue;
                };
                let (bx0, bz0, bx1, bz1) = p.bounds(piece);
                if bx1 <= win_min_x || bx0 >= win_max_x || bz1 <= win_min_z || bz0 >= win_max_z {
                    continue;
                }
                pieces_drawn += 1;
                for b in placement::placed_collision(piece, p) {
                    boxes.push((b, false));
                }
            }
            for c in world.segments() {
                let (bx0, bz0, bx1, bz1) = c.bounds();
                if bx1 <= win_min_x || bx0 >= win_max_x || bz1 <= win_min_z || bz0 >= win_max_z {
                    continue;
                }
                segments_drawn += 1;
                for b in segment::segment_boxes(c) {
                    boxes.push((b, true));
                }
            }
        }
    }
    // Fuera techos: un isométrico de un interior es siempre una sección.
    boxes.retain(|(b, _)| b.kind != segment::KIND_CEILING);

    // Painter: lo que está más «al fondo» se pinta antes. En isométrica el fondo es x + z menor, y
    // a igualdad, lo más bajo.
    boxes.sort_by(|(a, _), (b, _)| {
        let ka = a.center[0] + a.center[2] + a.center[1] * 0.001;
        let kb = b.center[0] + b.center[2] + b.center[1] * 0.001;
        ka.total_cmp(&kb)
    });

    let project = |x: f32, y: f32, z: f32| -> (f32, f32) {
        ((x - z) * ISO_X * PX, ((x + z) * ISO_Y - y) * PX)
    };

    // Extensión en pantalla, para encuadrar.
    let (mut sx0, mut sy0, mut sx1, mut sy1) = (f32::MAX, f32::MAX, f32::MIN, f32::MIN);
    for (b, _) in &boxes {
        for dx in [-0.5f32, 0.5] {
            for dy in [-0.5f32, 0.5] {
                for dz in [-0.5f32, 0.5] {
                    let (px, py) = project(
                        b.center[0] + b.size[0] * dx,
                        b.center[1] + b.size[1] * dy,
                        b.center[2] + b.size[2] * dz,
                    );
                    sx0 = sx0.min(px);
                    sy0 = sy0.min(py);
                    sx1 = sx1.max(px);
                    sy1 = sy1.max(py);
                }
            }
        }
    }
    assert!(sx0 < sx1, "no hay ni una caja en la ventana");

    let width = sx1 - sx0 + MARGIN * 2.0;
    let height = sy1 - sy0 + MARGIN * 2.0;
    let mut svg = format!(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width:.0}\" height=\"{height:.0}\" \
         viewBox=\"0 0 {width:.0} {height:.0}\">\n\
         <rect width=\"{width:.0}\" height=\"{height:.0}\" fill=\"#0d0f12\"/>\n"
    );

    let ox = -sx0 + MARGIN;
    let oy = -sy0 + MARGIN;
    for (b, generated) in &boxes {
        let (hx, hy, hz) = (b.size[0] * 0.5, b.size[1] * 0.5, b.size[2] * 0.5);
        let (cx, cy, cz) = (b.center[0], b.center[1], b.center[2]);
        let corner = |dx: f32, dy: f32, dz: f32| -> (f32, f32) {
            let (px, py) = project(cx + hx * dx, cy + hy * dy, cz + hz * dz);
            (px + ox, py + oy)
        };

        // Tres caras visibles: arriba, y las dos que miran a la cámara.
        let top = [
            corner(-1.0, 1.0, -1.0),
            corner(1.0, 1.0, -1.0),
            corner(1.0, 1.0, 1.0),
            corner(-1.0, 1.0, 1.0),
        ];
        let left = [
            corner(-1.0, 1.0, 1.0),
            corner(1.0, 1.0, 1.0),
            corner(1.0, -1.0, 1.0),
            corner(-1.0, -1.0, 1.0),
        ];
        let right = [
            corner(1.0, 1.0, -1.0),
            corner(1.0, 1.0, 1.0),
            corner(1.0, -1.0, 1.0),
            corner(1.0, -1.0, -1.0),
        ];

        // El suelo se distingue de la pared, y lo GENERADO de lo autorado: son las dos cosas que se
        // quieren mirar en un plano así.
        let (c_top, c_left, c_right) = match (*generated, b.kind) {
            (true, segment::KIND_FLOOR) => ("#3f6212", "#2f4a0e", "#25390b"),
            (true, _) => ("#a16207", "#7c4d06", "#613c05"),
            (false, segment::KIND_FLOOR) => ("#3f4652", "#2f353e", "#252a31"),
            _ => ("#8a94a6", "#6b7383", "#565d6a"),
        };

        let poly = |pts: &[(f32, f32); 4], fill: &str| -> String {
            format!(
                "<polygon points=\"{:.1},{:.1} {:.1},{:.1} {:.1},{:.1} {:.1},{:.1}\" \
                 fill=\"{fill}\"/>\n",
                pts[0].0, pts[0].1, pts[1].0, pts[1].1, pts[2].0, pts[2].1, pts[3].0, pts[3].1
            )
        };
        svg.push_str(&poly(&left, c_left));
        svg.push_str(&poly(&right, c_right));
        svg.push_str(&poly(&top, c_top));
    }
    svg.push_str("</svg>\n");

    let path = format!("{dir}/wg3_iso_seed{seed}_{span}chunks.svg");
    std::fs::write(&path, svg).expect("no se pudo escribir el isométrico");
    println!(
        "[wg3] isométrico semilla {seed}, {span} chunks ({} regiones): {pieces_drawn} piezas, \
         {segments_drawn} tramos, {} cajas → {path}",
        regions_across * regions_across,
        boxes.len()
    );
}

// ── contrato de junta (ADR-096) ─────────────────────────────────────────────────────────────

/// EL TEST DEL CONTRATO. Dos regiones vecinas ven la MISMA puerta, con lados opuestos, sin haberse
/// consultado.
///
/// Es toda la tesis de A2 en una aserción: si las dos listas no coinciden, cada región abre por
/// donde quiere y el mundo queda con vanos que dan a un muro y muros donde debería haber paso — y
/// ninguna de las dos cosas da error en ninguna parte.
#[test]
fn two_neighbouring_regions_agree_on_the_same_gate() {
    let seed = composer_seed(SERVED_SEED);

    for (rx, rz) in [(0, 0), (4, -3), (-6, 9)] {
        let here = Wg3RegionCoord { x: rx, z: rz };
        let east = Wg3RegionCoord { x: rx + 1, z: rz };
        let north = Wg3RegionCoord { x: rx, z: rz + 1 };

        let mine = junction::gates_of_region(seed, here.x, here.z, here.bounds());

        // Borde E de ésta ↔ borde O de la de la derecha.
        let theirs = junction::gates_of_region(seed, east.x, east.z, east.bounds());
        for gate in mine.iter().filter(|g| g.outward_side == 1) {
            let matched = theirs.iter().any(|o| {
                o.outward_side == 3 && (o.x - gate.x).abs() < 1e-3 && (o.z - gate.z).abs() < 1e-3
            });
            assert!(
                matched,
                "región ({rx},{rz}): su puerta E en ({:.2},{:.2}) no existe para la vecina",
                gate.x, gate.z
            );
        }

        // Borde N de ésta ↔ borde S de la de arriba.
        let above = junction::gates_of_region(seed, north.x, north.z, north.bounds());
        for gate in mine.iter().filter(|g| g.outward_side == 0) {
            let matched = above.iter().any(|o| {
                o.outward_side == 2 && (o.x - gate.x).abs() < 1e-3 && (o.z - gate.z).abs() < 1e-3
            });
            assert!(
                matched,
                "región ({rx},{rz}): su puerta N en ({:.2},{:.2}) no existe para la vecina",
                gate.x, gate.z
            );
        }
    }
}

/// Y la geometría lo cumple: en cada puerta hay un tramo A LOS DOS LADOS, con sus bocas enfrentadas
/// en el mismo punto.
///
/// El test anterior prueba que las dos regiones ACUERDAN la puerta; éste, que las dos la CONSTRUYEN.
/// Sin él, un acuerdo perfecto podría convivir con un vano que da al vacío — que es el fallo que
/// este diseño no puede permitirse.
#[test]
fn both_sides_of_a_gate_build_their_stub() {
    let m = real_manifest();
    let seed = composer_seed(SERVED_SEED);
    let stub = junction::gate_stub_piece(&m).expect("el catálogo no tiene tramo de puerta");

    let here = Wg3RegionCoord { x: 0, z: 0 };
    let east = Wg3RegionCoord { x: 1, z: 0 };

    let world_here = Wg3ServedWorld::compose_region(&m, SERVED_SEED, here);
    let world_east = Wg3ServedWorld::compose_region(&m, SERVED_SEED, east);

    let gates_e: Vec<_> = junction::gates_of_region(seed, here.x, here.z, here.bounds())
        .into_iter()
        .filter(|g| g.outward_side == 1)
        .collect();
    assert!(!gates_e.is_empty(), "el borde E no sorteó ninguna puerta");

    for gate in gates_e {
        let mine = junction::stub_anchor(&m, stub, gate).expect("sin ancla para la puerta");
        let theirs = junction::stub_anchor(
            &m,
            stub,
            junction::Wg3Gate {
                outward_side: 3,
                ..gate
            },
        )
        .expect("sin ancla para el otro lado");

        let present = |w: &Wg3ServedWorld, a: &compose::Wg3Anchor| {
            w.placements().iter().any(|p| {
                p.piece == a.piece
                    && p.rotation == a.rotation
                    && (p.origin_x() - a.origin_x).abs() < 0.02
                    && (p.origin_z() - a.origin_z).abs() < 0.02
            })
        };

        assert!(
            present(&world_here, &mine),
            "la región (0,0) no construyó su tramo en la puerta ({:.2},{:.2})",
            gate.x,
            gate.z
        );
        assert!(
            present(&world_east, &theirs),
            "la región (1,0) no construyó su tramo en la puerta ({:.2},{:.2})",
            gate.x,
            gate.z
        );
    }
}

/// ADR-096 verificación (e), la mitad que se puede probar sin abrir Unity: **la junta SE CRUZA**.
///
/// Los dos tests de arriba prueban que las regiones acuerdan la puerta y que las dos ponen su tramo.
/// Eso todavía no es cruzarla: dos tramos enfrentados pueden dejar un muro entre medias si el
/// rasterizado conservador engorda las paredes hasta cerrar el vano, o un escalón si las cotas no
/// casan. Esto camina la línea, metro a metro, por el MISMO ráster que usa la colisión del jugador —
/// y componiendo la región de cada punto por separado, igual que hace el servidor.
#[test]
fn a_gate_can_actually_be_walked_through() {
    let m = real_manifest();
    let seed = composer_seed(SERVED_SEED);
    let here = Wg3RegionCoord { x: 0, z: 0 };

    let gates: Vec<_> = junction::gates_of_region(seed, here.x, here.z, here.bounds())
        .into_iter()
        .filter(|g| g.outward_side == 1)
        .collect();
    assert!(!gates.is_empty(), "el borde E no sorteó ninguna puerta");

    // Altura de muestreo: por encima del rodapié y por debajo del dintel. Si a esta cota hay materia
    // en toda la travesía, el vano no existe por mucho que las dos piezas estén puestas.
    const HEAD_M: f32 = 1.0;
    const REACH_M: f32 = 8.0;
    const STEP_M: f32 = 0.25;

    for gate in gates {
        let mut walked = 0;
        let mut t = -REACH_M;
        while t <= REACH_M {
            let (x, z) = (gate.x + t, gate.z);

            // Cada punto se resuelve como lo resolvería el servidor: su chunk, su región, su ráster.
            // Componer una sola región y consultar los dos lados sería hacer trampa — probaría un
            // mundo que en producción no existe.
            let chunk = chunk::Wg3ChunkCoord::containing(x, z);
            let region = Wg3RegionCoord::of_chunk(chunk);
            let world = Wg3ServedWorld::compose_region(&m, SERVED_SEED, region);
            let raster = chunk::build_chunk_raster(
                &m,
                &world.placements_touching_chunk(&m, chunk),
                &world.segments_touching_chunk(chunk),
                chunk,
            );

            assert!(
                !raster.blocked_standing_at(x, 0.0, z, HEAD_M),
                "la puerta ({:.2},{:.2}) está tapiada a {t:+.2} m: no se puede cruzar",
                gate.x,
                gate.z
            );
            assert!(
                raster.floor_below(x, HEAD_M, z).is_some(),
                "la puerta ({:.2},{:.2}) no tiene suelo a {t:+.2} m: se cae al cruzar",
                gate.x,
                gate.z
            );

            walked += 1;
            t += STEP_M;
        }
        println!(
            "[wg3] puerta ({:.2},{:.2}): {walked} puntos caminables a lo largo de {:.0} m",
            gate.x,
            gate.z,
            REACH_M * 2.0
        );
    }
}

/// El margen de puerta tiene que ser mayor que el fondo del tramo, o dos tramos de bordes que se
/// encuentran en una esquina se pisarían. Es una relación entre dos constantes, y sin este test se
/// rompe el día que alguien alargue el tramo sin mirar la otra.
#[test]
fn the_gate_margin_clears_the_stub_depth() {
    // La relación entre las dos constantes la comprueba el compilador (`const _: () = assert!` en
    // junction.rs). Aquí solo queda lo que depende del CATÁLOGO, que el compilador no puede ver.
    let m = real_manifest();
    let stub = junction::gate_stub_piece(&m).expect("sin tramo de puerta");
    let piece = m.piece(stub).expect("índice de tramo inválido");
    assert!(
        piece.size_x.max(piece.size_z) <= junction::GATE_STUB_MAX_DEPTH_M,
        "el tramo elegido ({}) es más largo que su propia cota",
        piece.id
    );
}

/// ADR-096 — ninguna pieza asoma fuera de su región.
///
/// Mientras no haya contrato de junta, una pieza a caballo de dos regiones es geometría que la
/// región vecina no sabe que existe: compondría encima sin enterarse, y el solape solo se vería al
/// llegar el jugador. El precio es que las regiones nacen selladas, y está declarado.
#[test]
fn no_piece_leaves_its_region() {
    let m = real_manifest();

    for (rx, rz) in [(0, 0), (1, 0), (-2, 3), (5, -7)] {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let world = Wg3ServedWorld::compose_region(&m, SERVED_SEED, region);
        let (min_x, min_z, max_x, max_z) = region.bounds();

        for p in world.placements() {
            let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
            let (x0, z0, x1, z1) = p.bounds(piece);
            assert!(
                x0 >= min_x - 0.01
                    && z0 >= min_z - 0.01
                    && x1 <= max_x + 0.01
                    && z1 <= max_z + 0.01,
                "región ({rx},{rz}): una pieza asoma — {x0:.1}..{x1:.1} × {z0:.1}..{z1:.1} \
                 fuera de {min_x:.0}..{max_x:.0} × {min_z:.0}..{max_z:.0}"
            );
        }
    }
}

/// Lo que el mundo interino da de sí, MEDIDO y no supuesto: cuántas piezas, cuánto terreno y cuánto
/// cuesta componerlo, en varias semillas porque una sola puede salir con suerte.
///
/// EL PRESUPUESTO ES UN TECHO, NO UN OBJETIVO, y esto lo enseña: con 300 de tope, seis semillas dan
/// entre 20 y 268 piezas — de 134 m a 921 m de lado. El límite real no es el presupuesto sino que la
/// frontera se seca: cada boca puede sellarse a propósito y las candidatas que pisan algo ya puesto
/// se descartan, así que el árbol termina solo y a veces pronto. Subir el tope no da más mundo; lo
/// dará cerrar bucles, que ADR-095 deja abierto. Los mínimos de abajo son flojos a propósito: miden
/// que el mundo EXISTE, no que sea grande, porque grande no depende de este código.
#[test]
fn the_interim_world_is_worth_walking() {
    let m = real_manifest();

    for seed in [SERVED_SEED, 7, 42, 1337, 900_001, 0] {
        let started = std::time::Instant::now();
        let world = Wg3ServedWorld::compose(&m, seed);
        let elapsed = started.elapsed();

        let (mut min_x, mut min_z) = (f32::MAX, f32::MAX);
        let (mut max_x, mut max_z) = (f32::MIN, f32::MIN);
        for p in world.placements() {
            let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
            let (x0, z0, x1, z1) = p.bounds(piece);
            min_x = min_x.min(x0);
            min_z = min_z.min(z0);
            max_x = max_x.max(x1);
            max_z = max_z.max(z1);
        }
        let (span_x, span_z) = (max_x - min_x, max_z - min_z);
        println!(
            "[wg3] semilla {seed}: {} piezas, {span_x:.0} × {span_z:.0} m, compuesto en {:.0} ms",
            world.placements().len(),
            elapsed.as_secs_f32() * 1000.0
        );

        assert!(
            world.placements().len() >= 15,
            "semilla {seed}: solo {} piezas, el mundo se ahoga antes de ser andable",
            world.placements().len()
        );
        // UMBRAL RELAJADO DE 90 A 80 m EL 2026-08-28, y se dice en vez de cambiarlo callando.
        //
        // El 90 salía de «menos de dos chunks de lado» y se escribió cuando este mundo SIN ACOTAR
        // era el que se servía (A3 interino). Desde ADR-096 el mundo son regiones infinitas de
        // 150 m y `Wg3ServedWorld::compose` ya solo lo usan los tests, así que un mundo pequeño por
        // aquí no es un mundo pequeño para el jugador. Lo que protege de verdad esa propiedad es
        // `a_region_is_worth_its_size`, que mide la superficie construida de una REGIÓN.
        //
        // Lo que queda aquí es un suelo de cordura contra un compositor que se ahogue del todo. La
        // semilla 900001 da 130 × 88 m y las otras cuatro pasan de 490 m: esa dispersión es real y
        // conocida —el mundo sin acotar se seca donde se seca— y no es lo que hay que vigilar.
        assert!(
            span_x >= 80.0 && span_z >= 80.0,
            "semilla {seed}: el mundo mide {span_x:.0} × {span_z:.0} m, el compositor se ahogó"
        );
        assert!(
            elapsed.as_millis() < 250,
            "semilla {seed}: componer costó {} ms y lo hace el primer chunk que se pide",
            elapsed.as_millis()
        );
    }
}

/// ADR-096 verificación (c) — CUÁNTO cambia el mundo al cerrar bucles.
///
/// De aquí sale el tamaño de región, y por eso se mide antes de fijarlo. Dimensionar las regiones
/// con los números de un compositor que se ahoga sería dimensionarlas mal: el número que importa no
/// es cuántas piezas caben, sino cuánto terreno llena de verdad una composición.
///
/// No afirma un mínimo de bucles por semilla. Puede haber geometrías donde ninguna boca caiga sobre
/// otra, y exigirlo convertiría una propiedad del catálogo en un fallo del código. Lo que sí exige
/// es que **cerrar bucles nunca ENCOJA el mundo**: unir dos ramas no puede costar piezas.
#[test]
fn closing_loops_measures_how_much_more_world_there_is() {
    let m = real_manifest();
    let seeds = [SERVED_SEED, 7, 42, 1337, 900_001, 0];

    let open = compose::Wg3ComposerSettings {
        budget: INTERIM_BUDGET,
        close_loops: false,
        ..compose::Wg3ComposerSettings::default()
    };
    let looped = compose::Wg3ComposerSettings {
        close_loops: true,
        ..open.clone()
    };

    let mut total_loops = 0u32;
    for seed in seeds {
        let a = compose::compose(composer_seed(seed), &m, &open);
        let b = compose::compose(composer_seed(seed), &m, &looped);
        total_loops += b.loops_closed;

        println!(
            "[wg3] semilla {seed}: sin bucles {} piezas / con bucles {} piezas, \
             {} bucles cerrados, tapones forzados {} → {}",
            a.placements.len(),
            b.placements.len(),
            b.loops_closed,
            a.forced_caps,
            b.forced_caps
        );

        assert!(
            b.placements.len() >= a.placements.len(),
            "semilla {seed}: cerrar bucles ENCOGIÓ el mundo, de {} a {} piezas",
            a.placements.len(),
            b.placements.len()
        );
        assert_eq!(
            0, a.loops_closed,
            "con la perilla apagada no se cierra nada"
        );
    }

    println!(
        "[wg3] bucles cerrados en total sobre {} semillas: {total_loops}",
        seeds.len()
    );
}

/// SONDA — ¿por qué no se cierra NI UN bucle, y lo arreglaría un catálogo en módulo?
///
/// `#[ignore]` porque no afirma nada del código actual: mide una hipótesis sobre el CATÁLOGO,
/// deformándolo en memoria. Lánzala con
/// `cargo test --manifest-path backend/Cargo.toml modular_catalogue -- --ignored --nocapture`.
///
/// La hipótesis: unir dos bocas exige que caigan en el mismo punto al centímetro, y las posiciones
/// salen de cadenas de sumas sobre piezas de 11, 26, 9, 13… m con offsets de 1,2, 6,4, 2,2. El
/// máximo común divisor de todo eso es 0,1 m, así que la rejilla implícita es tan fina que dos ramas
/// no se encuentran jamás. Un kit modular de verdad vive sobre un módulo —es lo que significa
/// "modular"—, y sobre él las coincidencias dejan de ser casualidad.
///
/// Si esta sonda da bucles y la de arriba da cero, el arreglo NO es más código: es autorar el
/// catálogo en módulo.
#[test]
#[ignore = "sonda de diseño: deforma el catálogo para medir una hipótesis, no prueba el código"]
fn a_modular_catalogue_would_close_loops() {
    let mut m = real_manifest();

    // Redondeo de todo lo que decide una posición: huella y offset de cada boca. La anchura NO se
    // toca — cambiarla rompería la compatibilidad entre tipos y mediría otra cosa.
    const MODULE: f32 = 0.5;
    let snap = |v: f32| (v / MODULE).round() * MODULE;
    for p in &mut m.pieces {
        p.size_x = snap(p.size_x);
        p.size_z = snap(p.size_z);
        for s in &mut p.sockets {
            s.offset = snap(s.offset);
        }
    }

    let settings = compose::Wg3ComposerSettings {
        budget: INTERIM_BUDGET,
        close_loops: true,
        ..compose::Wg3ComposerSettings::default()
    };

    let mut total = 0u32;
    for seed in [SERVED_SEED, 7, 42, 1337, 900_001, 0] {
        let w = compose::compose(composer_seed(seed), &m, &settings);
        total += w.loops_closed;
        println!(
            "[wg3] MÓDULO {MODULE} m — semilla {seed}: {} piezas, {} bucles",
            w.placements.len(),
            w.loops_closed
        );
    }
    println!("[wg3] MÓDULO {MODULE} m — bucles en total: {total}");
}

/// La semilla del mundo es `u64` y la del compositor `i32`: se cogen los 32 bits bajos. Dos semillas
/// que solo se diferencien arriba dan EL MISMO mundo de WG3, y eso se prueba en vez de dejarlo para
/// que alguien lo encuentre de madrugada.
#[test]
fn the_world_seed_is_truncated_to_the_composers_thirty_two_bits() {
    let m = real_manifest();
    assert_eq!(42, super::world::composer_seed(0xDEAD_BEEF_0000_002A));
    assert_eq!(-1, super::world::composer_seed(u64::MAX));

    let disguised = Wg3ServedWorld::compose(&m, 0xDEAD_BEEF_0000_002A);
    let plain = Wg3ServedWorld::compose(&m, 42);
    assert_eq!(plain.placements(), disguised.placements());
}

/// EL ORIGEN DEL MUNDO TIENE SUELO, y con el andamio no lo tenía.
///
/// `demo` dejaba un chunk de cada tres vacío, así que aparecer en (0,0) era caerse al vacío con
/// probabilidad alta y eso obligó a teleportar al jugador a mano para poder probar nada. El
/// compositor pone la pieza semilla CENTRADA en el origen, así que el sitio donde aparece el jugador
/// es el interior de un pasillo. Se comprueba sobre el ráster del chunk, que es lo que decide de
/// verdad si hay algo debajo.
#[test]
fn the_world_has_floor_where_the_player_appears() {
    let m = real_manifest();
    let world = Wg3ServedWorld::compose(&m, SERVED_SEED);

    let coord = chunk::Wg3ChunkCoord::containing(0.0, 0.0);
    let placements = world.placements_touching_chunk(&m, coord);
    let segments = world.segments_touching_chunk(coord);
    let raster = chunk::build_chunk_raster(&m, &placements, &segments, coord);

    let floor = raster
        .floor_below(0.0, 1.0, 0.0)
        .expect("el origen del mundo no tiene suelo debajo");
    assert!(floor.abs() < 0.02, "el suelo del origen está a {floor}");

    let head = raster
        .headroom_above_floor(0.0, 1.0, 0.0)
        .expect("el origen del mundo no tiene techo");
    assert!(
        head >= 2.0,
        "hueco de {head} m en el origen: no se cabe de pie"
    );
}

/// SONDA DE MEDIDA, no aserción — ADR-096 enmienda 2. Imprime la geometría de los anillos que NO se
/// cierran: a qué distancia quedan las bocas abiertas compatibles, cuántas llegan a mirarse de frente
/// y con cuánto desvío lateral. De aquí salen los números de esa enmienda.
///
/// `#[ignore]` porque no afirma nada y tarda: es una regla para medir, no un criterio de corrección.
/// Lanzarla sola:
/// `cargo test --manifest-path backend/Cargo.toml probe_ring_geometry -- --ignored --nocapture`
#[test]
#[ignore = "sonda de medida: imprime, no afirma"]
fn probe_ring_geometry() {
    let m = real_manifest();
    let settings = compose::Wg3ComposerSettings {
        budget: 300,
        ..compose::Wg3ComposerSettings::default()
    };

    for seed in [42i32, 7, 1337, -19, 900_001, 0] {
        let w = compose::compose(seed, &m, &settings);

        // Todas las bocas del mundo, en coordenadas de mundo.
        let mut sockets: Vec<(f32, f32, u8, u8, f32, f32)> = Vec::new();
        for c in &w.placements {
            let Some(piece) = m.piece(c.placement.piece) else {
                continue;
            };
            for i in 0..piece.sockets.len() {
                let (x, z) = c.placement.world_socket_point(piece, i);
                let sk = &piece.sockets[i];
                sockets.push((
                    x,
                    z,
                    c.placement.world_side(piece, i),
                    sk.kind,
                    sk.width,
                    sk.floor_y,
                ));
            }
        }

        // Conectada = otra boca en el mismo punto. El resto están abiertas o taponadas, que para
        // esta medida es lo mismo: son las que un anillo podría haber usado.
        let open: Vec<_> = sockets
            .iter()
            .filter(|a| {
                sockets
                    .iter()
                    .filter(|b| (b.0 - a.0).abs() < 0.01 && (b.1 - a.1).abs() < 0.01)
                    .count()
                    < 2
            })
            .collect();

        let mut nearest = f32::MAX;
        let mut facing: Vec<(f32, f32)> = Vec::new();
        for (i, a) in open.iter().enumerate() {
            for b in open.iter().skip(i + 1) {
                let compatible = (a.2 + 2) % 4 == b.2
                    && a.3 == b.3
                    && (a.4 - b.4).abs() <= 0.001
                    && (a.5 - b.5).abs() <= 0.01;
                if !compatible {
                    continue;
                }
                let (dx, dz) = (b.0 - a.0, b.1 - a.1);
                nearest = nearest.min((dx * dx + dz * dz).sqrt());

                // En el marco de la boca `a`: cuánto hay que avanzar y cuánto corregir de lado.
                let (nx, nz) = placement::outward_normal(a.2);
                let axial = dx * nx + dz * nz;
                if axial > 0.0 {
                    facing.push((axial, (dx * nz - dz * nx).abs()));
                }
            }
        }

        let aligned = facing.iter().filter(|f| f.1 < 0.02).count();
        let best = facing
            .iter()
            .min_by(|x, y| x.1.partial_cmp(&y.1).unwrap_or(std::cmp::Ordering::Equal));
        println!(
            "[wg3] semilla {seed}: {} piezas, {} bocas ({} sin pareja) | mas cercana {:.2} m | se miran {} | alineadas <2cm {} | mejor alineada: {}",
            w.placements.len(),
            sockets.len(),
            open.len(),
            nearest,
            facing.len(),
            aligned,
            best.map(|f| format!("lateral {:.2} m con {:.1} m de avance", f.1, f.0))
                .unwrap_or_else(|| "ninguna".into())
        );
    }
}

/// **ADR-097, verificaciones 1 y 2: la rampa SUBE de verdad lo que cuelga de ella.**
///
/// Sin esto, `origin_y` es un campo que existe y vale cero en todas partes — que es exactamente el
/// estado anterior a F5, solo que con sitio donde escribirlo. El agujero que fundó WG3 era ése: en
/// WG2 la altura del suelo era función del índice de capa, así que rampas y medias plantas no es que
/// faltaran, es que no había dónde ponerlas.
///
/// Comprueba las dos mitades, y la segunda es la que importa:
///  1. que alguna pieza acaba a cota distinta de cero, o sea que la cota se PROPAGA;
///  2. que el RÁSTER —el mismo con el que colisiona el jugador— tiene suelo a esa altura. Eso es lo
///     que separa «el número viaja» de «se puede pisar».
#[test]
fn the_ramp_actually_raises_what_hangs_from_it() {
    let m = real_manifest();

    let ramp = m
        .pieces
        .iter()
        .find(|p| p.sockets.len() >= 2 && p.sockets[0].floor_y != p.sockets[1].floor_y)
        .expect(
            "el catálogo no tiene ninguna pieza con las bocas a cotas distintas, así que nada puede \
             cambiar de nivel: reexporta el manifiesto con `cor_ramp` dentro",
        );
    println!(
        "[wg3] pieza de desnivel: {} ({:.2} m entre sus bocas)",
        ramp.id,
        (ramp.sockets[1].floor_y - ramp.sockets[0].floor_y).abs()
    );

    let mut raised = 0usize;
    let mut highest = 0i32;
    let mut checked_floor = 0usize;

    for seed in [SERVED_SEED, 7, 42, 1337] {
        let world = Wg3ServedWorld::compose(&m, seed);
        for p in world.placements() {
            if p.origin_y_cm == 0 {
                continue;
            }
            raised += 1;
            highest = highest.max(p.origin_y_cm.abs());

            // SE COMPRUEBA EN LAS BOCAS, NO EN EL CENTRO. La primera versión miraba el centro de la
            // pieza y fallaba con razón: `cor_ramp` se coloca a −0,72 m cuando se engancha por su
            // boca ALTA —el cuerpo queda por debajo— y su centro cae sobre la plataforma elevada,
            // así que ahí el suelo está a 0 y no a −0,72. El suelo del centro de una pieza no es su
            // `origin_y` en cuanto la pieza tiene estructura dentro; donde la cota significa algo es
            // en la boca, que es por donde se enchufa la siguiente.
            let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
            for i in 0..piece.sockets.len() {
                let (mx, mz) = p.world_socket_point(piece, i);
                let inward = match p.world_side(piece, i) {
                    0 => (0.0, -0.6),
                    1 => (-0.6, 0.0),
                    2 => (0.0, 0.6),
                    _ => (0.6, 0.0),
                };
                let (x, z) = (mx + inward.0, mz + inward.1);
                let expected = p.origin_y() + piece.sockets[i].floor_y;

                let chunk = chunk::Wg3ChunkCoord::containing(x, z);
                let raster = chunk::build_chunk_raster(
                    &m,
                    &world.placements_touching_chunk(&m, chunk),
                    &world.segments_touching_chunk(chunk),
                    chunk,
                );

                if let Some(y) = raster.floor_below(x, expected + 1.0, z) {
                    assert!(
                        (y - expected).abs() < 0.4,
                        "pieza {} a {:.2} m: su boca {i} debería pisar en {expected:.2} m y el \
                         ráster pone el suelo en {y:.2} m",
                        piece.id,
                        p.origin_y()
                    );
                    checked_floor += 1;
                }
            }
        }
    }

    println!(
        "[wg3] {raised} colocaciones fuera de la cota 0, la más alta a {:.2} m; suelo del ráster \
         comprobado en {checked_floor}",
        highest as f32 * 0.01
    );
    assert!(
        raised > 0,
        "ninguna pieza salió de la cota 0 en cuatro semillas: la cota no se propaga"
    );
    assert!(
        checked_floor > 0,
        "ninguna pieza elevada tenía suelo en el ráster: sube en los números y no en la colisión"
    );
}

/// **¿EL MUNDO SERVIDO ES UNA SOLA PIEZA O SON ISLAS?** — Joel mandó una captura cenital y se ven
/// grupos de geometría separados por vacío.
///
/// R7 dice «conectividad por construcción», y el compositor es un ÁRBOL desde una semilla, así que
/// todo lo que crece de ella está conectado. Lo que el árbol no cubre son las **anclas de junta**:
/// `compose_region` las coloca PRIMERO y como raíces sueltas, y si el crecimiento no llega hasta
/// ellas se quedan flotando. Esta sonda cuenta componentes conexas: 1 = un mundo; más = islas.
///
/// Dos piezas cuentan como conectadas si una boca de una cae sobre una boca de la otra (2 cm), que
/// es la misma condición con la que el compositor las enganchó.
#[test]
fn probe_is_the_served_world_one_piece_or_islands() {
    const CLOSE_LOOPS: bool = false;
    let m = real_manifest();

    for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, 2)] {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let world = if CLOSE_LOOPS {
            // MISMO montaje que `compose_region` pero con el cierre de bucles encendido. Se mide
            // aquí porque `close_loops` solo se había medido sobre el mundo SIN acotar, donde no hay
            // anclas de junta: justo el caso en el que no había nada que unir.
            let (min_x, min_z, max_x, max_z) = region.bounds();
            let seed = composer_seed(SERVED_SEED);
            let settings = compose::Wg3ComposerSettings {
                budget: INTERIM_BUDGET,
                close_loops: true,
                bounds: Some((min_x, min_z, max_x, max_z)),
                seed_at: Some(((min_x + max_x) * 0.5, (min_z + max_z) * 0.5)),
                anchors: junction::gates_of_region(seed, region.x, region.z, region.bounds())
                    .into_iter()
                    .filter_map(|g| {
                        junction::gate_stub_piece(&m).and_then(|p| junction::stub_anchor(&m, p, g))
                    })
                    .collect(),
                ..Default::default()
            };
            Wg3ServedWorld::compose_with(&m, SERVED_SEED, &settings)
        } else {
            Wg3ServedWorld::compose_region(&m, SERVED_SEED, region)
        };
        let n = world.placements().len();

        // Bocas de cada pieza, en mundo.
        let mouths: Vec<Vec<(f32, f32)>> = world
            .placements()
            .iter()
            .map(|p| {
                let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
                (0..piece.sockets.len())
                    .map(|i| p.world_socket_point(piece, i))
                    .collect()
            })
            .collect();

        // Union-find sobre bocas coincidentes.
        let mut parent: Vec<usize> = (0..n).collect();
        fn find(parent: &mut [usize], mut a: usize) -> usize {
            while parent[a] != a {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }
            a
        }
        for i in 0..n {
            for j in (i + 1)..n {
                let touch = mouths[i].iter().any(|a| {
                    mouths[j]
                        .iter()
                        .any(|b| (a.0 - b.0).abs() < 0.02 && (a.1 - b.1).abs() < 0.02)
                });
                if touch {
                    let (ri, rj) = (find(&mut parent, i), find(&mut parent, j));
                    if ri != rj {
                        parent[ri] = rj;
                    }
                }
            }
        }

        // ADR-098 — y las CELDAS generadas unen tanto como una boca coincidente, que es justamente
        // su razón de existir. Se meten en el mismo union-find por GEOMETRÍA —la boca de un tramo
        // cae en el mismo punto que la de la pieza a la que se pegó— y no preguntándole al
        // enrutador qué unió: un mundo transitable es una propiedad del resultado, no de la
        // contabilidad de quien lo hizo.
        let segments = world.segments();
        parent.resize(n + segments.len(), 0);
        for (k, node) in parent.iter_mut().enumerate().skip(n) {
            *node = k;
        }
        let segment_mouths: Vec<Vec<(f32, f32)>> =
            segments.iter().map(segment_mouth_points).collect();

        let touches = |a: &[(f32, f32)], b: &[(f32, f32)]| {
            a.iter().any(|p| {
                b.iter()
                    .any(|q| (p.0 - q.0).abs() < 0.02 && (p.1 - q.1).abs() < 0.02)
            })
        };
        for k in 0..segments.len() {
            for (i, piece_mouths) in mouths.iter().enumerate().take(n) {
                if touches(&segment_mouths[k], piece_mouths) {
                    let (ri, rk) = (find(&mut parent, i), find(&mut parent, n + k));
                    if ri != rk {
                        parent[ri] = rk;
                    }
                }
            }
            for l in (k + 1)..segments.len() {
                if touches(&segment_mouths[k], &segment_mouths[l]) {
                    let (rk, rl) = (find(&mut parent, n + k), find(&mut parent, n + l));
                    if rk != rl {
                        parent[rk] = rl;
                    }
                }
            }
        }

        let mut sizes = std::collections::HashMap::<usize, usize>::new();
        for i in 0..n {
            let r = find(&mut parent, i);
            *sizes.entry(r).or_default() += 1;
        }
        let mut counts: Vec<usize> = sizes.values().copied().collect();
        counts.sort_unstable_by(|a, b| b.cmp(a));
        let biggest = counts.first().copied().unwrap_or(0);

        // ¿Cuadra el número de islas con el de puertas? Si islas == puertas + 1 (el árbol de la
        // semilla), la causa está aislada: cada ancla de junta crece su propio árbol.
        let seed = composer_seed(SERVED_SEED);
        let gates = junction::gates_of_region(seed, region.x, region.z, region.bounds()).len();

        // ADR-097 — cuántas piezas quedan fuera de la cota 0. En el mundo sin acotar hay cientos,
        // pero lo que se juega son regiones: si aquí sale cero, el desnivel existe en el compositor
        // y no en la partida.
        let raised = world
            .placements()
            .iter()
            .filter(|p| p.origin_y_cm != 0)
            .count();

        println!(
            "[wg3] región ({rx},{rz}): {n} piezas y {} tramos en **{} islas** ({gates} puertas + semilla = {}) — la mayor tiene {biggest} ({:.0} %), {raised} a distinta cota, tamaños {:?}",
            segments.len(),
            counts.len(),
            gates + 1,
            biggest as f32 * 100.0 / n.max(1) as f32,
            &counts[..counts.len().min(8)]
        );
    }
}

/// **¿CUÁNTO CIERRA `deliberate_cap_chance`?** — Joel: «llega un punto que se cierra y no hay
/// manera de moverte».
///
/// La perilla sella una boca A PROPÓSITO aunque hubiera con qué seguir (L21: paredes ciegas y
/// espacio residual). Sobre el papel eso da textura; andando, a 0,17, es un callejón cada pocos
/// metros. El barrido existe para elegir el valor con un número en vez de a ojo — que es lo que hice
/// con los pesos y salió mal.
///
/// Mide lo que de verdad importa para «se puede recorrer»: piezas por región y RAMA MÁS LARGA, o sea
/// hasta dónde se puede andar sin volver sobre los pasos. Contar piezas solo no vale: un mundo de 40
/// piezas en ocho ramas de cinco se cierra igual que uno de 20.
#[test]
fn probe_how_much_the_deliberate_cap_closes_the_world() {
    let m = real_manifest();

    for chance in [0.17_f32, 0.10, 0.05, 0.02, 0.0] {
        let mut pieces = 0usize;
        let mut deepest = 0i32;
        let mut regions = 0usize;

        for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, 2)] {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let (min_x, min_z, max_x, max_z) = region.bounds();
            let settings = compose::Wg3ComposerSettings {
                deliberate_cap_chance: chance,
                // INTERIM_BUDGET y NO el 30 por defecto: compose_region usa 300, y con 30 el
                // barrido topaba contra el presupuesto y yo leia el techo como si fuera geometria.
                budget: INTERIM_BUDGET,
                bounds: Some((min_x, min_z, max_x, max_z)),
                seed_at: Some(((min_x + max_x) * 0.5, (min_z + max_z) * 0.5)),
                ..Default::default()
            };
            let world = compose::compose(composer_seed(SERVED_SEED), &m, &settings);

            regions += 1;
            pieces += world.placements.len();
            for c in &world.placements {
                deepest = deepest.max(c.depth);
            }
        }

        println!(
            "[wg3] cap_chance {chance:.2}: {:.0} piezas por región, rama más larga {deepest}",
            pieces as f32 / regions as f32
        );
    }
}

/// **¿HAY VANOS AL VACÍO EN EL MUNDO QUE SE JUEGA?** — reportado andando: «pasillos que dan a la
/// nada y te caerías del mapa».
///
/// El invariante «ninguna boca al vacío» SÍ está probado… pero sobre `compose::compose`, el mundo
/// SIN ACOTAR. El mundo que se sirve es `compose_region`, acotado a la caja de su región, y ahí
/// nadie lo comprobaba: una candidata que se sale de la caja se rechaza, y si la boca que la iba a
/// recibir no se tapona después, queda un vano abierto a donde no hay nada.
///
/// La sonda mide el síntoma, no la teoría: por cada boca de cada pieza colocada, mira un metro POR
/// FUERA del vano y pregunta al ráster —el mismo con el que colisiona el jugador— si hay suelo. Sin
/// suelo ahí, se sale y se cae.
#[test]
fn probe_open_mouths_in_the_served_world() {
    let m = real_manifest();

    // 0,35 m: donde estaría el CUERPO del jugador al asomarse por el vano (su radio es 0,30).
    // La primera versión medía a 1,0 m y los tapones tienen 0,9 m de fondo, así que la sonda caía
    // JUSTO DETRÁS de su pared trasera y contaba como agujero una boca perfectamente sellada.
    // Medir más lejos de lo que el jugador puede llegar no mide el mundo, mide el vacío de al lado.
    const OUT_M: f32 = 0.35;
    const HEAD_M: f32 = 1.0;

    for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, 2)] {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let world = Wg3ServedWorld::compose_region(&m, SERVED_SEED, region);

        let mut mouths = 0usize;
        let mut holes = 0usize;
        let mut first: Option<(f32, f32)> = None;

        for p in world.placements() {
            let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
            for i in 0..piece.sockets.len() {
                mouths += 1;
                let (mx, mz) = p.world_socket_point(piece, i);
                let (nx, nz) = match p.world_side(piece, i) {
                    0 => (0.0, 1.0),
                    1 => (1.0, 0.0),
                    2 => (0.0, -1.0),
                    _ => (-1.0, 0.0),
                };
                let (x, z) = (mx + nx * OUT_M, mz + nz * OUT_M);

                // Se resuelve como el servidor: el chunk del punto, su región, su ráster. Un punto
                // justo fuera de la región cae en la de al lado, y eso es exactamente lo que hay
                // que mirar: es donde el jugador se caería.
                let chunk = chunk::Wg3ChunkCoord::containing(x, z);
                let its_region = Wg3RegionCoord::of_chunk(chunk);
                let w = Wg3ServedWorld::compose_region(&m, SERVED_SEED, its_region);
                // ADR-098 — CON los tramos generados. Sin ellos, una boca que ahora da a un
                // conector se contaria como agujero: la sonda mediria un mundo que no se sirve.
                let raster = chunk::build_chunk_raster(
                    &m,
                    &w.placements_touching_chunk(&m, chunk),
                    &w.segments_touching_chunk(chunk),
                    chunk,
                );

                if raster.floor_below(x, HEAD_M, z).is_none() {
                    holes += 1;
                    if first.is_none() {
                        first = Some((x, z));
                    }
                }
            }
        }

        println!(
            "[wg3] región ({rx},{rz}): {} piezas, {mouths} bocas, **{holes} sin suelo al otro lado**{}",
            world.placements().len(),
            match first {
                Some((x, z)) => format!(" — la primera en ({x:.1}, {z:.1})"),
                None => String::new(),
            }
        );
    }
}

/// **Cuánto catálogo hace falta** — la decisión 5 del brief, medida en vez de estimada.
///
/// La repetición no se nota por cuántas piezas tenga el catálogo, sino por cada cuánto vuelve la
/// MISMA pieza a ponerse cerca. Por eso lo que se mide es la distancia de cada pieza a la copia más
/// próxima de sí misma y no el reparto de frecuencias: dos naves iguales a 200 m no las junta
/// nadie, dos iguales a 15 m se leen como la misma esquina otra vez.
///
/// No asegura nada todavía: es una SONDA, y su salida es el número con el que dimensionar el
/// catálogo autorado. Poner aquí un mínimo antes de saber cuánto da el catálogo actual sería fijar
/// el listón a ojo y llamarlo medida.
#[test]
fn how_soon_the_same_piece_comes_round_again() {
    let m = real_manifest();

    let mut gaps: Vec<f32> = Vec::new();
    let mut used = std::collections::HashMap::<u16, usize>::new();
    let mut regions = 0usize;
    let mut total = 0usize;

    for seed in [SERVED_SEED, 7, 42, 1337, 900_001] {
        for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, 2)] {
            let world = Wg3ServedWorld::compose_region(&m, seed, Wg3RegionCoord { x: rx, z: rz });
            regions += 1;
            total += world.placements().len();

            // El CENTRO de cada pieza, no su origen. Dos naves de 42 m con los orígenes a 30 m se
            // solapan a la vista; medir por esquina escondería justo el caso que duele.
            let centres: Vec<(u16, f32, f32)> = world
                .placements()
                .iter()
                .map(|p| {
                    let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
                    let (x0, z0, x1, z1) = p.bounds(piece);
                    (p.piece, (x0 + x1) * 0.5, (z0 + z1) * 0.5)
                })
                .collect();

            for (piece, _, _) in &centres {
                *used.entry(*piece).or_default() += 1;
            }

            for (i, a) in centres.iter().enumerate() {
                let mut nearest = f32::MAX;
                for (j, b) in centres.iter().enumerate() {
                    if i == j || a.0 != b.0 {
                        continue;
                    }
                    nearest = nearest.min(((a.1 - b.1).powi(2) + (a.2 - b.2).powi(2)).sqrt());
                }
                // Una pieza sin copia en su región no aporta distancia: contarla como "infinito"
                // subiría la mediana justo por las piezas que NO se repiten.
                if nearest < f32::MAX {
                    gaps.push(nearest);
                }
            }
        }
    }

    gaps.sort_by(|a, b| a.partial_cmp(b).expect("distancias sin NaN"));
    let median = gaps[gaps.len() / 2];
    let worst_decile = gaps[gaps.len() / 10];

    let mut ranking: Vec<(u16, usize)> = used.iter().map(|(k, v)| (*k, *v)).collect();
    ranking.sort_by_key(|(_, n)| std::cmp::Reverse(*n));

    println!(
        "[wg3] catálogo de {} piezas | {regions} regiones, {total} colocaciones | \
         distintas usadas {} | vuelve a {median:.0} m de mediana, {worst_decile:.0} m en el peor decil",
        m.pieces.len(),
        used.len()
    );
    for (piece, n) in &ranking {
        let id = &m.piece(*piece).expect("pieza fuera del catálogo").id;
        println!(
            "[wg3]   {id:<16} {n:>4}  ({:.1} %)",
            *n as f32 * 100.0 / total as f32
        );
    }
}

// ───────────────────────────── ADR-098 T1 — el tramo generada ─────────────────────────────

use super::segment::{self, Wg3Opening, Wg3Segment, KIND_CEILING, KIND_FLOOR, KIND_WALL};

/// Un tramo recto: 10 m de largo por 2,4 de ancho, abierto de punta a punta.
fn straight_segment() -> Wg3Segment {
    Wg3Segment {
        x_cm: 0,
        z_cm: 0,
        size_x_cm: 1000,
        size_z_cm: 240,
        floor_y_cm: 0,
        height_cm: 320,
        openings: vec![
            Wg3Opening {
                side: 3,
                offset_cm: 120,
                width_cm: 240,
            },
            Wg3Opening {
                side: 1,
                offset_cm: 120,
                width_cm: 240,
            },
        ],
        style: 0,
    }
}

/// LA PROPIEDAD QUE HACE ÚTIL A UNA CELDA: una boca a todo el ancho no deja pared en ese lado.
///
/// Si dejara aunque fuese un tramo de medio centímetro, dos tramos encadenadas tendrían un tabique
/// entre ellas y el conector sería una fila de armarios. Y el fallo no se vería en una planta: se
/// vería andando, al chocar contra aire.
#[test]
fn a_full_width_opening_leaves_no_wall_on_that_side() {
    let boxes = segment::segment_boxes(&straight_segment());

    assert_eq!(
        4,
        boxes.len(),
        "suelo, techo y las dos paredes largas — nada más: {boxes:#?}"
    );
    assert_eq!(KIND_FLOOR, boxes[0].kind);
    assert_eq!(KIND_CEILING, boxes[1].kind);
    assert!(boxes[2..].iter().all(|b| b.kind == KIND_WALL));

    // Las dos que quedan son las largas (N y S), no las de los extremos.
    for b in &boxes[2..] {
        assert!(
            (b.size[0] - 10.0).abs() < 1e-3,
            "pared corta donde no debía: {b:?}"
        );
    }
}

/// Una boca más estrecha que su lado deja las DOS jambas, y en su sitio.
#[test]
fn a_narrow_opening_leaves_a_jamb_on_each_side() {
    let mut c = straight_segment();
    // El lado O (x = 0) corre en +Z y mide 2,4: una boca de 1,2 centrada deja 0,6 a cada lado.
    c.openings[0] = Wg3Opening {
        side: 3,
        offset_cm: 120,
        width_cm: 120,
    };

    let boxes = segment::segment_boxes(&c);
    let west: Vec<_> = boxes
        .iter()
        .filter(|b| b.kind == KIND_WALL && b.center[0] < 0.2)
        .collect();

    assert_eq!(2, west.len(), "faltan jambas: {west:#?}");
    for b in &west {
        assert!(
            (b.size[2] - 0.6).abs() < 1e-3,
            "jamba de {} m, se esperaba 0,6",
            b.size[2]
        );
    }
}

/// LA TRANSICIÓN DE ANCHO SALE SOLA (ADR-098 D6). Un tramo de 5 m de ancho con una boca de 2,4 en
/// un extremo y otra de 5 en el otro es la pieza de transición que hoy hay que autorar — y no
/// necesita ni un caso especial en el emisor.
#[test]
fn a_width_change_is_just_two_openings_of_different_width() {
    let c = Wg3Segment {
        x_cm: 0,
        z_cm: 0,
        size_x_cm: 600,
        size_z_cm: 500,
        floor_y_cm: 0,
        height_cm: 320,
        openings: vec![
            Wg3Opening {
                side: 3,
                offset_cm: 250,
                width_cm: 240,
            },
            Wg3Opening {
                side: 1,
                offset_cm: 250,
                width_cm: 500,
            },
        ],
        style: 0,
    };

    let boxes = segment::segment_boxes(&c);
    let west = boxes
        .iter()
        .filter(|b| b.kind == KIND_WALL && b.center[0] < 0.2)
        .count();
    let east = boxes
        .iter()
        .filter(|b| b.kind == KIND_WALL && b.center[0] > 5.8)
        .count();

    assert_eq!(2, west, "el lado estrecho tiene que conservar sus jambas");
    assert_eq!(0, east, "el lado ancho está abierto de par en par");
}

/// La cota de el tramo es la cara PISABLE, no la de la losa: dos tramos contiguas a cotas distintas
/// dejan exactamente su diferencia como contrahuella, que es de lo que se hace una escalera (D7).
#[test]
fn the_floor_slab_hangs_below_the_cell_cota() {
    let mut c = straight_segment();
    c.floor_y_cm = 72;

    let boxes = segment::segment_boxes(&c);
    let floor = boxes.iter().find(|b| b.kind == KIND_FLOOR).expect("suelo");
    let top = floor.center[1] + floor.size[1] * 0.5;

    assert!(
        (top - 0.72).abs() < 1e-4,
        "la cara pisable quedó en {top}, no en la cota de el tramo"
    );
}

/// El tope de tramo no es estético: es lo que sostiene el reparto por chunk. Se comprueba que la
/// tramo lo DENUNCIA, porque emitirla sin más dejaría una pieza de la que un cliente con radio 1
/// solo vería la mitad.
#[test]
fn a_cell_over_the_size_cap_reports_it() {
    let mut c = straight_segment();
    c.size_x_cm = 3000;

    let problems = c.problems();
    assert!(
        problems.iter().any(|p| p.contains("tope")),
        "el tope de {} m no se denunció: {problems:?}",
        segment::MAX_SEGMENT_M
    );
}

/// Un tramo sin bocas es una caja maciza, y una boca que se sale de su lado es una pared con un
/// agujero por el que se ve el vacío. Las dos se cazan antes de emitir.
#[test]
fn a_cell_without_openings_or_with_one_that_overflows_is_rejected() {
    let mut sealed = straight_segment();
    sealed.openings.clear();
    assert!(sealed.problems().iter().any(|p| p.contains("sin bocas")));

    let mut spill = straight_segment();
    spill.openings[0].width_cm = 400; // 4 m en un lado de 2,4
    assert!(spill.problems().iter().any(|p| p.contains("se sale")));
}

/// El ráster ve lo que el tramo dice: pasillo libre por dentro, macizo donde hay pared. Va contra el
/// MISMO ráster que colisiona y no contra la lista de cajas, que es lo que distingue este test de
/// una tautología.
#[test]
fn the_raster_of_a_cell_is_hollow_inside_and_solid_at_its_walls() {
    let c = straight_segment();
    let mut builder = Wg3RasterBuilder::covering(-1.0, -1.0, 11.0, 3.4);
    for b in segment::segment_boxes(&c) {
        builder.add_box(&b);
    }
    let raster = builder.finish();

    // Centro del pasillo, a la altura de la cabeza: libre.
    assert!(
        !raster.is_solid_at(5.0, 1.7, 1.2),
        "el pasillo está tapiado por dentro"
    );
    // Contra la pared larga: macizo.
    assert!(
        raster.is_solid_at(5.0, 1.7, 2.35),
        "la pared larga no bloquea"
    );
    // Y hay suelo bajo los pies.
    assert!(
        raster.is_solid_at(5.0, -0.06, 1.2),
        "no hay suelo dentro de el tramo"
    );
}

/// EL ENRUTADOR ESQUIVA (ADR-098 D5).
///
/// Dos bocas enfrentadas con una pieza justo en medio. Si la ruta solo supiera ir recta, aquí no
/// habría conector; y si lo hubiera atravesando la pieza, sería peor que no tenerlo. Es el caso que
/// separa «probar formas» de «buscar camino», y por eso está escrito con geometría de mentira: sobre
/// el mundo real no se puede saber si el enrutador esquivó o si es que había hueco de sobra.
#[test]
fn a_connector_goes_around_what_is_in_the_way() {
    use super::route::{self, Mouth, Rect, RouteSettings};

    let mouths = [
        Mouth {
            node: 0,
            socket: 0,
            x: 0.0,
            z: 0.0,
            side: 1, // mira a +X
            width: 2.4,
            floor_y: 0.0,
            clear_height: 3.2,
            kind: 0,
        },
        Mouth {
            node: 1,
            socket: 0,
            x: 24.0,
            z: 0.0,
            side: 3, // mira a −X
            width: 2.4,
            floor_y: 0.0,
            clear_height: 3.2,
            kind: 0,
        },
    ];

    // Las dos piezas de las bocas, y un obstáculo tapando el camino recto de lado a lado.
    let blocker = Rect {
        min_x: 8.0,
        min_z: -6.0,
        max_x: 14.0,
        max_z: 6.0,
    };
    let occupancy = [
        Rect {
            min_x: -6.0,
            min_z: -1.2,
            max_x: 0.0,
            max_z: 1.2,
        },
        Rect {
            min_x: 24.0,
            min_z: -1.2,
            max_x: 30.0,
            max_z: 1.2,
        },
        blocker,
    ];

    let outcome = route::route(
        &mouths,
        &occupancy,
        None,
        2,
        &[Vec::new(), Vec::new()],
        &RouteSettings::default(),
    );

    assert_eq!(
        1, outcome.connectors,
        "no tendió conector: {} descartes por geometría",
        outcome.rejected_by_geometry
    );
    assert!(
        outcome.segments.len() >= 3,
        "una ruta que rodea necesita al menos tres tramos, salieron {}",
        outcome.segments.len()
    );

    // Y no atraviesa lo que tenía que rodear. Es la mitad que de verdad importa: un conector que
    // pasa por dentro de una pieza se dibuja igual de bien y se anda atravesando una pared.
    for s in &outcome.segments {
        let (min_x, min_z, max_x, max_z) = s.bounds();
        assert!(
            min_x >= blocker.max_x - 0.02
                || max_x <= blocker.min_x + 0.02
                || min_z >= blocker.max_z - 0.02
                || max_z <= blocker.min_z + 0.02,
            "un tramo se metió dentro del obstáculo: ({min_x}, {min_z})–({max_x}, {max_z})"
        );
    }
}

/// LO QUE SE GENERA NO PISA NADA (ADR-098, verificación d).
///
/// Un conector que se solapa con una pieza se dibuja igual de bien y se anda atravesando una pared,
/// que es el peor fallo del sistema porque el jugador ve una cosa y el juego hace otra. Se comprueba
/// sobre el mundo SERVIDO —no sobre un montaje— y contra las dos cosas que puede pisar: las piezas y
/// los otros tramos.
#[test]
fn nothing_generated_overlaps_anything_placed() {
    const EPS: f32 = 0.02;
    let m = real_manifest();

    for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, 2), (3, -2)] {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let world = Wg3ServedWorld::compose_region(&m, SERVED_SEED, region);
        let segments = world.segments();

        let overlaps = |a: (f32, f32, f32, f32), b: (f32, f32, f32, f32)| {
            a.0 < b.2 - EPS && a.2 - EPS > b.0 && a.1 < b.3 - EPS && a.3 - EPS > b.1
        };

        for (i, s) in segments.iter().enumerate() {
            let sb = s.bounds();
            for p in world.placements() {
                let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
                assert!(
                    !overlaps(sb, p.bounds(piece)),
                    "región ({rx},{rz}): el tramo {i} pisa la pieza {} en {:?}",
                    piece.id,
                    sb
                );
            }
            for (j, other) in segments.iter().enumerate().skip(i + 1) {
                assert!(
                    !overlaps(sb, other.bounds()),
                    "región ({rx},{rz}): los tramos {i} y {j} se pisan"
                );
            }
        }
    }
}

/// EL ANILLO ES UN ANILLO (ADR-098, verificación b).
///
/// Contar uniones no vale: un test que solo cuenta no distingue un anillo de una rama más
/// (ADR-096 lo dice con esas palabras). Aquí se monta una cadena de piezas conectadas en fila y se
/// comprueba que el enrutador une sus DOS EXTREMOS — que es exactamente lo que convierte un camino
/// en un ciclo: dos formas distintas de ir de la primera a la última.
///
/// Con geometría de mentira a propósito: sobre el mundo real, los anillos dependen de que sobren
/// bocas después de unir las islas, y eso mide otra cosa.
#[test]
fn the_ring_pass_joins_the_two_ends_of_a_chain() {
    use super::route::{self, Mouth, Rect, RouteSettings};

    // Ocho piezas en fila a lo largo de +X, encadenadas. Los extremos miran hacia −Z, así que la
    // única forma de unirlos es rodear por debajo.
    const N: usize = 8;
    let mut mouths = Vec::new();
    let mut occupancy = Vec::new();
    let mut adjacency = vec![Vec::new(); N];
    for i in 0..N {
        let x = i as f32 * 8.0;
        occupancy.push(Rect {
            min_x: x,
            min_z: 0.0,
            max_x: x + 6.0,
            max_z: 2.4,
        });
        if i + 1 < N {
            adjacency[i].push(i + 1);
            adjacency[i + 1].push(i);
        }
    }
    for i in [0usize, N - 1] {
        mouths.push(Mouth {
            node: i,
            socket: 0,
            x: i as f32 * 8.0 + 3.0,
            z: 0.0,
            side: 2, // mira a −Z
            width: 2.4,
            floor_y: 0.0,
            clear_height: 3.2,
            kind: 0,
        });
    }

    let outcome = route::route(
        &mouths,
        &occupancy,
        None,
        N,
        &adjacency,
        &RouteSettings::default(),
    );

    assert_eq!(
        1, outcome.connectors,
        "no cerró el anillo: {} descartes por geometría",
        outcome.rejected_by_geometry
    );
    assert_eq!(
        0, outcome.connectors_joining_islands,
        "la cadena ya era una sola componente: esto tenía que contar como anillo, no como isla"
    );
    assert_eq!(
        vec![(0usize, N - 1)],
        outcome.edges,
        "el anillo tiene que unir los dos EXTREMOS de la cadena"
    );
}

/// **SE ANDA DE VERDAD, Y HASTA DÓNDE** (ADR-098, verificación g por el lado del servidor).
///
/// Recorre el mundo servido A PIE: desde donde aparece el jugador —el centro de la región, que es
/// donde siembra el compositor— se propaga por celdas de medio metro con las reglas de andar (hay
/// suelo, hay hueco para la cabeza, y el escalón entre celda y celda no pasa de la contrahuella del
/// catálogo). Lo que sale es cuánto del mundo se alcanza sin volar y si se llega a las puertas de
/// junta, que es la pregunta que Joel hizo andando: «llega un punto que se cierra y no hay manera de
/// moverte».
///
/// **Contra el RÁSTER y no contra la lista de piezas**, que es lo que separa esto de una tautología:
/// el ráster es lo que el servidor va a usar para resolver el movimiento, y es donde una pared
/// generada de más o un suelo de menos aparecen.
#[test]
fn probe_how_much_of_the_region_can_be_walked_from_the_spawn() {
    const CELL: f32 = 0.5;
    const HEAD_M: f32 = 1.0;
    // La contrahuella del catálogo. Por encima de esto no es un escalón, es un bordillo.
    const MAX_STEP: f32 = WALK_STEP_M;

    let m = real_manifest();

    for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, 2)] {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let world = Wg3ServedWorld::compose_region(&m, SERVED_SEED, region);
        let (min_x, min_z, max_x, max_z) = region.bounds();

        // Un ráster por chunk de la región, resuelto como lo resolvería el servidor.
        let side = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        let mut rasters = Vec::with_capacity(side * side);
        for cz in 0..side {
            for cx in 0..side {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                rasters.push(chunk::build_chunk_raster(
                    &m,
                    &world.placements_touching_chunk(&m, coord),
                    &world.segments_touching_chunk(coord),
                    coord,
                ));
            }
        }
        let raster_at = |x: f32, z: f32| -> Option<&Wg3Raster> {
            let coord = chunk::Wg3ChunkCoord::containing(x, z);
            let (dx, dz) = (coord.x - base.x, coord.z - base.z);
            if dx < 0 || dz < 0 || dx as usize >= side || dz as usize >= side {
                return None;
            }
            rasters.get(dz as usize * side + dx as usize)
        };

        let cells = (REGION_M / CELL) as usize;

        // **TODAS LAS COTAS DE UNA COLUMNA, Y NO SOLO LA DE ABAJO.** Preguntar por «el suelo bajo la
        // cabeza» daba un número falso desde ADR-097: una pieza a dos metros de alto no tiene suelo
        // por debajo de 1 m, así que la sonda la contaba como no pisable y el mundo salía la mitad
        // de andable de lo que es. Una columna de tramos puede tener varios sitios donde estar de
        // pie, y hay que mirarlos todos — que es justo la razón de que el formato sean tramos.
        let levels_of = |ix: usize, iz: usize| -> Vec<f32> {
            let x = min_x + ix as f32 * CELL + CELL * 0.5;
            let z = min_z + iz as f32 * CELL + CELL * 0.5;
            let Some(r) = raster_at(x, z) else {
                return Vec::new();
            };
            let column = r.column_at(x, z);
            let mut out = Vec::new();
            for (i, span) in column.iter().enumerate() {
                let top = span.top_cm as f32 / 100.0;
                // Hueco para la cabeza hasta el siguiente macizo de la misma columna.
                let head = match column.get(i + 1) {
                    Some(next) => (next.bottom_cm - span.top_cm) as f32 / 100.0,
                    None => f32::MAX,
                };
                // Con techo, y no a cielo abierto: la cara de arriba de una pared o de una losa de
                // techo cumple «hay suelo y hay hueco» y no es sitio donde se ande. Contarla metía
                // los TEJADOS en la cuenta y hacía el mundo más grande y más roto de lo que es.
                if (HEAD_M..=CEILING_CAP_M).contains(&head) {
                    out.push(top);
                }
            }
            out
        };

        let mut floors: Vec<Vec<f32>> = vec![Vec::new(); cells * cells];
        let mut standable = 0usize;
        // ADR-102 D6 — y el mismo recuento por NIVEL. La travesía siempre fue consciente de la cota
        // (`seen_level`), pero la cifra que se publica contaba COLUMNAS: una celda con suelo en dos
        // plantas sumaba uno. Con lo que había eso daba igual porque sólo hay una planta; el día que
        // haya dos, la superficie de arriba no movería el porcentaje ni un punto y el número diría
        // que añadir una planta no añadió mundo.
        let mut standable_levels = 0usize;
        for iz in 0..cells {
            for ix in 0..cells {
                let levels = levels_of(ix, iz);
                if !levels.is_empty() {
                    standable += 1;
                }
                standable_levels += levels.len();
                floors[iz * cells + ix] = levels;
            }
        }

        // Se arranca donde aparece el jugador: el centro de la región.
        let start = (cells / 2, cells / 2);
        let mut queue = std::collections::VecDeque::new();
        let mut seen_level: Vec<Vec<bool>> = floors.iter().map(|l| vec![false; l.len()]).collect();
        // Si el centro exacto no es pisable, se busca la celda pisable más cercana — que es
        // exactamente lo que hace el arnés del cliente al aterrizar.
        let mut from = None;
        'search: for radius in 0..cells / 2 {
            for dz in -(radius as i32)..=(radius as i32) {
                for dx in -(radius as i32)..=(radius as i32) {
                    let (ix, iz) = (start.0 as i32 + dx, start.1 as i32 + dz);
                    if ix < 0 || iz < 0 || ix as usize >= cells || iz as usize >= cells {
                        continue;
                    }
                    if !floors[iz as usize * cells + ix as usize].is_empty() {
                        from = Some((ix as usize, iz as usize));
                        break 'search;
                    }
                }
            }
        }
        let Some(from) = from else {
            println!("[wg3] región ({rx},{rz}): ni una celda pisable — nada que andar");
            continue;
        };

        // Se arranca por la cota MÁS BAJA de esa celda, que es donde caería el jugador al soltarlo.
        seen_level[from.1 * cells + from.0][0] = true;
        queue.push_back((from.0, from.1, 0usize));
        let mut reached = 0usize;
        let mut seen = vec![false; cells * cells];
        while let Some((ix, iz, li)) = queue.pop_front() {
            if !seen[iz * cells + ix] {
                seen[iz * cells + ix] = true;
                reached += 1;
            }
            let here = floors[iz * cells + ix][li];
            for (dx, dz) in [(1i32, 0i32), (-1, 0), (0, 1), (0, -1)] {
                let (nx, nz) = (ix as i32 + dx, iz as i32 + dz);
                if nx < 0 || nz < 0 || nx as usize >= cells || nz as usize >= cells {
                    continue;
                }
                let (nx, nz) = (nx as usize, nz as usize);
                for (nl, there) in floors[nz * cells + nx].iter().enumerate() {
                    if seen_level[nz * cells + nx][nl] {
                        continue;
                    }
                    if (there - here).abs() > MAX_STEP {
                        continue;
                    }
                    seen_level[nz * cells + nx][nl] = true;
                    queue.push_back((nx, nz, nl));
                }
            }
        }

        // ¿Y se llega a las puertas de junta? Es lo que decide si cruzar de región sirve de algo.
        let seed = composer_seed(SERVED_SEED);
        let gates = junction::gates_of_region(seed, region.x, region.z, region.bounds());
        let mut gates_reached = 0usize;
        for g in &gates {
            // DENTRO DEL TRAMO DE PUERTA, no detrás de él: el tramo tiene menos de un metro de
            // fondo, así que medir metro y medio adentro mediría lo que haya al otro lado de su
            // pared, que es otra cosa. Lo que se pregunta es si se llega A la puerta.
            let (nx, nz) = match g.outward_side % 4 {
                0 => (0.0, -0.45),
                1 => (-0.45, 0.0),
                2 => (0.0, 0.45),
                _ => (0.45, 0.0),
            };
            let (x, z) = (g.x + nx, g.z + nz);
            let ix = ((x - min_x) / CELL) as i32;
            let iz = ((z - min_z) / CELL) as i32;
            if ix < 0 || iz < 0 || ix as usize >= cells || iz as usize >= cells {
                continue;
            }
            if seen[iz as usize * cells + ix as usize] {
                gates_reached += 1;
            }
        }

        // Y cuántas PIEZAS se pisan. Es la cuenta que se puede comparar con la de islas: si el grafo
        // dice que el 86 % del mundo es una sola componente y andando solo se llega a la mitad, lo
        // que está roto no es la conexión, es la junta entre lo generado y lo autorado.
        let walked = |x: f32, z: f32| -> bool {
            let ix = ((x - min_x) / CELL) as i32;
            let iz = ((z - min_z) / CELL) as i32;
            ix >= 0
                && iz >= 0
                && (ix as usize) < cells
                && (iz as usize) < cells
                && seen[iz as usize * cells + ix as usize]
        };

        // LOS TAPONES NO CUENTAN, y no es maquillaje: un tapón es una pieza de una sola boca y 0,9 m
        // de fondo puesta para cerrar un extremo. Su centro cae dentro de su propia pared, así que
        // contarlo como «no se pisa» mediría el grosor del tapón y no el mundo.
        let mut pieces_reached = 0usize;
        let mut pieces_counted = 0usize;
        for p in world.placements() {
            let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
            if piece.sockets.len() < 2 {
                continue;
            }
            pieces_counted += 1;
            let (px0, pz0, px1, pz1) = p.bounds(piece);
            if walked((px0 + px1) * 0.5, (pz0 + pz1) * 0.5) {
                pieces_reached += 1;
            }
        }

        // ¿Es la mancha del jugador la MAYOR del mundo, o le ha tocado un rincón? Se recorre todo lo
        // pisable y se cuentan las manchas. Es la diferencia entre «el mundo está partido» y «el
        // jugador aparece en el sitio equivocado», que piden arreglos opuestos.
        {
            let mut visited: Vec<Vec<bool>> = floors.iter().map(|l| vec![false; l.len()]).collect();
            let mut sizes: Vec<usize> = Vec::new();
            for iz0 in 0..cells {
                for ix0 in 0..cells {
                    for l0 in 0..floors[iz0 * cells + ix0].len() {
                        if visited[iz0 * cells + ix0][l0] {
                            continue;
                        }
                        visited[iz0 * cells + ix0][l0] = true;
                        let mut blob = std::collections::VecDeque::new();
                        blob.push_back((ix0, iz0, l0));
                        let mut size = 0usize;
                        while let Some((ix, iz, li)) = blob.pop_front() {
                            size += 1;
                            let here = floors[iz * cells + ix][li];
                            for (dx, dz) in [(1i32, 0i32), (-1, 0), (0, 1), (0, -1)] {
                                let (nx, nz) = (ix as i32 + dx, iz as i32 + dz);
                                if nx < 0 || nz < 0 || nx as usize >= cells || nz as usize >= cells
                                {
                                    continue;
                                }
                                let (nx, nz) = (nx as usize, nz as usize);
                                for (nl, there) in floors[nz * cells + nx].iter().enumerate() {
                                    if visited[nz * cells + nx][nl]
                                        || (there - here).abs() > MAX_STEP
                                    {
                                        continue;
                                    }
                                    visited[nz * cells + nx][nl] = true;
                                    blob.push_back((nx, nz, nl));
                                }
                            }
                        }
                        sizes.push(size);
                    }
                }
            }
            sizes.sort_unstable_by(|a, b| b.cmp(a));
            let big: Vec<usize> = sizes.iter().copied().take(5).collect();
            println!(
                "[wg3]   manchas andables: {} en total, las mayores {:?} celdas",
                sizes.len(),
                big
            );
        }

        // Diagnóstico: hasta dónde llega la mancha que se anda, y por dónde se corta.
        {
            let (mut bx0, mut bz0, mut bx1, mut bz1) = (f32::MAX, f32::MAX, f32::MIN, f32::MIN);
            for iz in 0..cells {
                for ix in 0..cells {
                    if !seen[iz * cells + ix] {
                        continue;
                    }
                    let x = min_x + ix as f32 * CELL;
                    let z = min_z + iz as f32 * CELL;
                    bx0 = bx0.min(x);
                    bz0 = bz0.min(z);
                    bx1 = bx1.max(x);
                    bz1 = bz1.max(z);
                }
            }
            println!(
                "[wg3]   la mancha andable va de ({bx0:.0},{bz0:.0}) a ({bx1:.0},{bz1:.0}), \
                 saliendo de ({:.1},{:.1})",
                min_x + from.0 as f32 * CELL,
                min_z + from.1 as f32 * CELL
            );
        }

        // Diagnóstico: un tramo al que no se llega puede ser que no CONECTE o que no se pueda ni
        // pisar. Son dos fallos distintos y hay que saber cuál es antes de tocar nada.
        for s in world.segments().iter().take(4) {
            let (cx, cz) = s.centre();
            // A cada lado de cada boca del tramo: si fuera se anda y dentro no, el vano no abre — y
            // ése es un fallo de geometría. Si no se anda ni fuera, el tramo cuelga de una isla y el
            // fallo es de enrutado. Son dos arreglos distintos.
            let mut mouths = String::new();
            for o in &s.openings {
                let (lx, lz) = placement::local_point(
                    o.side,
                    o.offset_cm as f32 / 100.0,
                    s.size_x(),
                    s.size_z(),
                );
                let (mx, mz) = (s.min_x() + lx, s.min_z() + lz);
                let (nx, nz) = match o.side % 4 {
                    0 => (0.0, 1.0),
                    1 => (1.0, 0.0),
                    2 => (0.0, -1.0),
                    _ => (-1.0, 0.0),
                };
                mouths += &format!(
                    " boca(lado {}) fuera={} dentro={};",
                    o.side,
                    walked(mx + nx * 0.6, mz + nz * 0.6),
                    walked(mx - nx * 0.6, mz - nz * 0.6)
                );
            }
            println!(
                "[wg3]   tramo ({cx:.2},{cz:.2}) {}×{} cm cota {} pisado {} —{mouths}",
                s.size_x_cm,
                s.size_z_cm,
                s.floor_y_cm,
                walked(cx, cz)
            );
        }

        let segments_reached = world
            .segments()
            .iter()
            .filter(|s| {
                let (cx, cz) = s.centre();
                walked(cx, cz)
            })
            .count();

        // ADR-102 D6 — cuántos (celda, nivel) se pisan de los que hay. Con una planta es idéntico al
        // recuento por columnas; con dos es el único de los dos que se mueve.
        let reached_levels: usize = seen_level.iter().flatten().filter(|v| **v).count();

        println!(
            "[wg3] región ({rx},{rz}): {reached} celdas a pie de {standable} pisables ({:.0} %), \
             {:.0} m² andables | por nivel {reached_levels} de {standable_levels} ({:.0} %), \
             {:.0} m² | piezas pisadas {pieces_reached} de {pieces_counted} | tramos \
             pisados {segments_reached} de {} | puertas alcanzadas {gates_reached} de {}",
            reached as f32 * 100.0 / standable.max(1) as f32,
            reached as f32 * CELL * CELL,
            reached_levels as f32 * 100.0 / standable_levels.max(1) as f32,
            reached_levels as f32 * CELL * CELL,
            world.segments().len(),
            gates.len()
        );
        assert!(
            max_x > min_x && max_z > min_z,
            "la región tiene que tener caja"
        );
    }
}

/// **EL NÚMERO QUE ADR-095 PROMETIÓ Y NADIE ESCRIBIÓ.**
///
/// `raster.rs:22-29` dice, desde el primer día: «cada pared se infla hasta media celda y eso COME
/// VANO. Cuánto exactamente es un número, no una opinión, y lo mide `narrowest_doorway_clearance`
/// en los tests: si baja del diámetro del jugador, el tamaño de celda de D1 está mal elegido». Ese
/// test **no existía**. Aquí está.
///
/// Mide el hueco LIBRE que sobrevive al rasterizado dentro de un pasillo de anchura W, barriendo la
/// alineación sub-celda: dos paredes de 15 cm en un mundo de celdas de 50 cm no caen donde uno
/// quiere, y el peor caso es el que manda porque el mundo se coloca en centímetros arbitrarios.
///
/// Se salvó por los pelos y por accidente: el catálogo autorado tiene bocas de 2,4 y 5,0 m. Lo que
/// ADR-098 empezó a generar baja de eso.
#[test]
fn narrowest_doorway_clearance() {
    // Alto de referencia del cuerpo, para preguntar por el hueco a la altura a la que se anda.
    const BODY_M: f32 = 1.7;

    // El hueco libre de un pasillo de `width_cm` de ancho, con la esquina en `offset_cm`, medido a
    // mitad de su largo. Devuelve metros.
    let clearance = |width_cm: i32, offset_cm: i32| -> f32 {
        let cell = Wg3Segment {
            x_cm: offset_cm,
            z_cm: offset_cm,
            size_x_cm: width_cm,
            size_z_cm: 600,
            floor_y_cm: 0,
            height_cm: 320,
            openings: vec![
                Wg3Opening {
                    side: 0,
                    offset_cm: width_cm / 2,
                    width_cm,
                },
                Wg3Opening {
                    side: 2,
                    offset_cm: width_cm / 2,
                    width_cm,
                },
            ],
            style: 0,
        };
        // A propósito NO se exige `problems().is_empty()`: la regla de anchura mínima que este test
        // justifica vive ahí, así que el pasillo de 120 cm —el que hay que poder medir para saber
        // que está mal— es inválido por construcción. Medirlo es el objeto del test.
        let (x0, z0, x1, z1) = cell.bounds();
        let mut builder = Wg3RasterBuilder::covering(x0 - 1.0, z0 - 1.0, x1 + 1.0, z1 + 1.0);
        for b in segment::segment_boxes(&cell) {
            builder.add_box(&b);
        }
        let raster = builder.finish();

        // Se barre a lo ancho en pasos de un centímetro y se mide la RACHA más larga sin materia a
        // la altura del cuerpo. La racha, y no el total: dos medios huecos separados por una pared
        // no dejan pasar a nadie.
        let z = (z0 + z1) * 0.5;
        let (mut best, mut run) = (0, 0);
        for step in 0..=((x1 - x0) * CM_PER_M) as i32 {
            let x = x0 + step as f32 / CM_PER_M;
            if raster.blocked_standing_at(x, 0.0, z, BODY_M) {
                run = 0;
            } else {
                run += 1;
                best = best.max(run);
            }
        }
        (best - 1).max(0) as f32 / CM_PER_M
    };

    // El peor caso de cada anchura sobre todas las alineaciones sub-celda posibles.
    let worst_of = |width_cm: i32| -> f32 {
        let mut worst = f32::MAX;
        for offset in (0..50).step_by(5) {
            worst = worst.min(clearance(width_cm, offset));
        }
        println!("[wg3] pasillo de {width_cm} cm → hueco libre peor caso {worst:.2} m");
        worst
    };

    let narrow = worst_of(120);
    let medium = worst_of(200);
    let corridor = worst_of(240);
    let wide = worst_of(500);

    // **LO QUE ESTE TEST FIJA.** El catálogo pasa; lo que el enrutador generaba, no.
    assert!(
        corridor >= PLAYER_RADIUS * 2.0,
        "el pasillo de 2,4 m del catálogo deja {corridor:.2} m y el jugador mide {:.2} m de \
         diámetro: la celda de 0,5 m de ADR-095 D1 estaría mal elegida",
        PLAYER_RADIUS * 2.0
    );
    assert!(
        wide >= PLAYER_RADIUS * 2.0,
        "el pasillo de 5,0 m deja {wide:.2} m"
    );
    assert!(
        narrow < PLAYER_RADIUS * 2.0,
        "un pasillo de 1,20 m deja {narrow:.2} m y el jugador cabe: si esto deja de ser cierto es \
         que el rasterizado cambió, y el mínimo del enrutador (MIN_GENERATED_WIDTH_CM) sobra",
    );
    println!(
        "[wg3] el jugador mide {:.2} m de diámetro; 200 cm dejan {medium:.2} m",
        PLAYER_RADIUS * 2.0
    );
}

/// **QUÉ ESCALÓN PIDE CADA PIEZA PARA PODER CRUZARLA**, medido sobre su propia colisión.
///
/// Sale de un hallazgo: los ÚNICOS nodos del mundo servido cuyas bocas caen en manchas andables
/// distintas son `cor_ramp`, siempre. La rampa está bien autorada —cuatro peldaños de 18 cm, por
/// debajo del `m_StepOffset` 0,275 de `FPS_Player.prefab`— pero su HUELLA es de 0,29 m y la celda
/// del ráster mide 0,50 m: el rasterizado conservador se queda con el peldaño más alto de los que
/// toca cada celda, y **funde dos peldaños en uno de 36 cm**. El cliente dibuja una escalera que se
/// sube y el servidor pone un bordillo que no.
///
/// La regla que sale de aquí, y vale para toda escalera que se autore: **la huella tiene que ser al
/// menos la celda del ráster**, o la contrahuella efectiva se multiplica por cuántos peldaños quepan
/// en una celda.
///
/// Esta sonda no supone nada de eso: coloca cada pieza sola, la rasteriza y busca el escalón MÁS
/// PEQUEÑO que la deja de una pieza. Cualquier geometría interior que pida más que el jugador
/// aparece aquí, sea una escalera o no.
#[test]
fn probe_what_step_each_piece_demands() {
    /// `m_StepOffset` de `FPS_Player.prefab`. Lo que el jugador sube sin saltar.
    const PLAYER_STEP_M: f32 = 0.275;
    const CELL: f32 = 0.5;
    const HEAD_M: f32 = 1.0;

    let m = real_manifest();
    let mut demanding = Vec::new();

    for piece in &m.pieces {
        let placement = Wg3Placement {
            piece: piece.index,
            rotation: 0,
            origin_x_cm: 0,
            origin_z_cm: 0,
            origin_y_cm: 0,
        };
        let boxes = placement::placed_collision(piece, &placement);
        if boxes.is_empty() {
            continue;
        }
        let (x0, z0, x1, z1) = placement.bounds(piece);
        let mut builder = Wg3RasterBuilder::covering(x0 - 1.0, z0 - 1.0, x1 + 1.0, z1 + 1.0);
        for b in &boxes {
            builder.add_box(b);
        }
        let raster = builder.finish();

        let cells_x = ((x1 - x0) / CELL).ceil() as usize;
        let cells_z = ((z1 - z0) / CELL).ceil() as usize;
        let levels_of = |ix: usize, iz: usize| -> Vec<f32> {
            let x = x0 + ix as f32 * CELL + CELL * 0.5;
            let z = z0 + iz as f32 * CELL + CELL * 0.5;
            let column = raster.column_at(x, z);
            let mut out = Vec::new();
            for (i, span) in column.iter().enumerate() {
                let head = match column.get(i + 1) {
                    Some(next) => (next.bottom_cm - span.top_cm) as f32 / 100.0,
                    None => f32::MAX,
                };
                if (HEAD_M..=CEILING_CAP_M).contains(&head) {
                    out.push(span.top_cm as f32 / 100.0);
                }
            }
            out
        };
        let floors: Vec<Vec<f32>> = (0..cells_z)
            .flat_map(|iz| (0..cells_x).map(move |ix| (ix, iz)))
            .map(|(ix, iz)| levels_of(ix, iz))
            .collect();

        // Cuántas manchas quedan si el jugador sube como mucho `step`.
        let blobs_with = |step: f32| -> usize {
            let mut seen: Vec<Vec<bool>> = floors.iter().map(|l| vec![false; l.len()]).collect();
            let mut count = 0usize;
            for iz0 in 0..cells_z {
                for ix0 in 0..cells_x {
                    for l0 in 0..floors[iz0 * cells_x + ix0].len() {
                        if seen[iz0 * cells_x + ix0][l0] {
                            continue;
                        }
                        count += 1;
                        seen[iz0 * cells_x + ix0][l0] = true;
                        let mut queue = std::collections::VecDeque::new();
                        queue.push_back((ix0, iz0, l0));
                        while let Some((ix, iz, li)) = queue.pop_front() {
                            let here = floors[iz * cells_x + ix][li];
                            for (dx, dz) in [(1i32, 0i32), (-1, 0), (0, 1), (0, -1)] {
                                let (nx, nz) = (ix as i32 + dx, iz as i32 + dz);
                                if nx < 0
                                    || nz < 0
                                    || nx as usize >= cells_x
                                    || nz as usize >= cells_z
                                {
                                    continue;
                                }
                                let (nx, nz) = (nx as usize, nz as usize);
                                for (nl, there) in floors[nz * cells_x + nx].iter().enumerate() {
                                    if seen[nz * cells_x + nx][nl] || (there - here).abs() > step {
                                        continue;
                                    }
                                    seen[nz * cells_x + nx][nl] = true;
                                    queue.push_back((nx, nz, nl));
                                }
                            }
                        }
                    }
                }
            }
            count
        };

        // El escalón que pide la pieza es el menor que no mejora nada por encima de él. Se busca
        // por barrido grueso: lo que importa es el orden de magnitud contra los 0,275 del jugador.
        let target = blobs_with(10.0);
        let mut needed = f32::MAX;
        for step_cm in (2..=60).step_by(2) {
            let step = step_cm as f32 / 100.0;
            if blobs_with(step) == target {
                needed = step;
                break;
            }
        }
        if needed > PLAYER_STEP_M {
            demanding.push(format!("{} pide {:.2} m", piece.id, needed));
        }
    }

    println!(
        "[wg3] el jugador sube {PLAYER_STEP_M:.3} m sin saltar. Piezas que piden más: {}",
        if demanding.is_empty() {
            "ninguna".to_string()
        } else {
            demanding.join(", ")
        }
    );
}

/// **NINGUNA BOCA DEL MUNDO SERVIDO ESTÁ TAPIADA.** La guardia del arreglo de la enmienda 2.
///
/// `narrowest_doorway_clearance` protege la CONSTANTE; esto protege el MUNDO. Son cosas distintas:
/// la constante sigue bien y aun así una vía nueva —otro sitio donde se elija una anchura, un tramo
/// que se parta, una boca que se recorte contra una esquina— puede volver a emitir un vano que el
/// ráster tapia. El síntoma sería el peor posible: el cliente dibuja el pasillo abierto y el
/// servidor no deja entrar, y eso no sale en una captura.
///
/// Se mide sobre el mundo que se SIRVE y contra el ráster, no contra la lista de cajas.
#[test]
fn no_mouth_in_the_served_world_is_walled_shut() {
    let m = real_manifest();
    let mut walled = Vec::new();

    for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, 2)] {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let world = Wg3ServedWorld::compose_region(&m, SERVED_SEED, region);
        let (min_x, min_z, _, _) = region.bounds();

        let side = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        let mut rasters = Vec::with_capacity(side * side);
        for cz in 0..side {
            for cx in 0..side {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                rasters.push(chunk::build_chunk_raster(
                    &m,
                    &world.placements_touching_chunk(&m, coord),
                    &world.segments_touching_chunk(coord),
                    coord,
                ));
            }
        }
        let raster_at = |x: f32, z: f32| -> Option<&Wg3Raster> {
            let coord = chunk::Wg3ChunkCoord::containing(x, z);
            let (dx, dz) = (coord.x - base.x, coord.z - base.z);
            if dx < 0 || dz < 0 || dx as usize >= side || dz as usize >= side {
                return None;
            }
            rasters.get(dz as usize * side + dx as usize)
        };

        // Las bocas de junta caen en el borde de la región y dan a la vecina: fuera de lo que este
        // ráster cubre, y no son un fallo. Solo se juzga lo que está dentro.
        let mut check = |x: f32, z: f32, who: String| {
            let Some(r) = raster_at(x, z) else { return };
            let column = r.column_at(x, z);
            let solid = column.len() == 1 && column[0].top_cm - column[0].bottom_cm > 200;
            if solid {
                walled.push(format!(
                    "({rx},{rz}) {who} en ({x:.2},{z:.2}): macizo de {} a {} cm",
                    column[0].bottom_cm, column[0].top_cm
                ));
            }
        };

        for (i, p) in world.placements().iter().enumerate() {
            let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
            for s in 0..piece.sockets.len() {
                let (x, z) = p.world_socket_point(piece, s);
                check(x, z, format!("boca {s} de la pieza {i}"));
            }
        }
        for (i, s) in world.segments().iter().enumerate() {
            for (j, (x, z)) in segment_mouth_points(s).into_iter().enumerate() {
                check(x, z, format!("boca {j} del tramo {i}"));
            }
        }
    }

    assert!(
        walled.is_empty(),
        "hay bocas tapiadas en el mundo servido — el cliente las dibuja abiertas y el servidor no \
         deja pasar:\n{}",
        walled.join("\n")
    );
}

/// **DE QUÉ ESTÁ HECHA CADA MANCHA ANDABLE** (diagnóstico de por qué no se llega a los tramos).
///
/// La sonda de arriba dice que la región (0,0) tiene una mancha de 10890 celdas y otra de 4112, y
/// que a los 25 tramos generados no se llega desde ninguna de las dos. Eso admite dos causas
/// opuestas, y piden arreglos opuestos: o los conectores están sueltos —cada uno su propia isla— o
/// forman una RED entre ellos que no engancha con las piezas por ningún sitio.
///
/// Aquí se etiqueta cada mancha con lo que contiene: cuántos centros de pieza y cuántos de tramo.
/// Y para cada tramo, si es pisable por dentro, para separar «no conecta» de «ni siquiera se puede
/// estar de pie ahí», que también son dos arreglos distintos.
#[test]
fn probe_what_each_walkable_blob_is_made_of() {
    const CELL: f32 = 0.5;
    const HEAD_M: f32 = 1.0;
    const MAX_STEP: f32 = WALK_STEP_M;

    let m = real_manifest();

    for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, 2)] {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let world = Wg3ServedWorld::compose_region(&m, SERVED_SEED, region);
        let (min_x, min_z, _, _) = region.bounds();

        let side = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        let mut rasters = Vec::with_capacity(side * side);
        for cz in 0..side {
            for cx in 0..side {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                rasters.push(chunk::build_chunk_raster(
                    &m,
                    &world.placements_touching_chunk(&m, coord),
                    &world.segments_touching_chunk(coord),
                    coord,
                ));
            }
        }
        let raster_at = |x: f32, z: f32| -> Option<&Wg3Raster> {
            let coord = chunk::Wg3ChunkCoord::containing(x, z);
            let (dx, dz) = (coord.x - base.x, coord.z - base.z);
            if dx < 0 || dz < 0 || dx as usize >= side || dz as usize >= side {
                return None;
            }
            rasters.get(dz as usize * side + dx as usize)
        };

        let cells = (REGION_M / CELL) as usize;
        let levels_of = |ix: usize, iz: usize| -> Vec<f32> {
            let x = min_x + ix as f32 * CELL + CELL * 0.5;
            let z = min_z + iz as f32 * CELL + CELL * 0.5;
            let Some(r) = raster_at(x, z) else {
                return Vec::new();
            };
            let column = r.column_at(x, z);
            let mut out = Vec::new();
            for (i, span) in column.iter().enumerate() {
                let top = span.top_cm as f32 / 100.0;
                let head = match column.get(i + 1) {
                    Some(next) => (next.bottom_cm - span.top_cm) as f32 / 100.0,
                    None => f32::MAX,
                };
                if (HEAD_M..=CEILING_CAP_M).contains(&head) {
                    out.push(top);
                }
            }
            out
        };

        let mut floors: Vec<Vec<f32>> = vec![Vec::new(); cells * cells];
        for iz in 0..cells {
            for ix in 0..cells {
                floors[iz * cells + ix] = levels_of(ix, iz);
            }
        }

        // Se numeran TODAS las manchas y se guarda a cuál pertenece cada celda, para poder preguntar
        // luego «¿en qué mancha cae este tramo?».
        let mut blob_of: Vec<Vec<i32>> = floors.iter().map(|l| vec![-1; l.len()]).collect();
        let mut sizes: Vec<usize> = Vec::new();
        for iz0 in 0..cells {
            for ix0 in 0..cells {
                for l0 in 0..floors[iz0 * cells + ix0].len() {
                    if blob_of[iz0 * cells + ix0][l0] >= 0 {
                        continue;
                    }
                    let id = sizes.len() as i32;
                    blob_of[iz0 * cells + ix0][l0] = id;
                    let mut queue = std::collections::VecDeque::new();
                    queue.push_back((ix0, iz0, l0));
                    let mut size = 0usize;
                    while let Some((ix, iz, li)) = queue.pop_front() {
                        size += 1;
                        let here = floors[iz * cells + ix][li];
                        for (dx, dz) in [(1i32, 0i32), (-1, 0), (0, 1), (0, -1)] {
                            let (nx, nz) = (ix as i32 + dx, iz as i32 + dz);
                            if nx < 0 || nz < 0 || nx as usize >= cells || nz as usize >= cells {
                                continue;
                            }
                            let (nx, nz) = (nx as usize, nz as usize);
                            for (nl, there) in floors[nz * cells + nx].iter().enumerate() {
                                if blob_of[nz * cells + nx][nl] >= 0
                                    || (there - here).abs() > MAX_STEP
                                {
                                    continue;
                                }
                                blob_of[nz * cells + nx][nl] = id;
                                queue.push_back((nx, nz, nl));
                            }
                        }
                    }
                    sizes.push(size);
                }
            }
        }

        // La mancha de una posición del mundo: la de la cota más baja pisable de su celda.
        let blob_at = |x: f32, z: f32| -> i32 {
            let ix = ((x - min_x) / CELL) as i32;
            let iz = ((z - min_z) / CELL) as i32;
            if ix < 0 || iz < 0 || ix as usize >= cells || iz as usize >= cells {
                return -1;
            }
            *blob_of[iz as usize * cells + ix as usize]
                .first()
                .unwrap_or(&-1)
        };

        let mut pieces_in: Vec<usize> = vec![0; sizes.len()];
        let mut loose_pieces = 0usize;
        for p in world.placements() {
            let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
            if piece.sockets.len() < 2 {
                continue;
            }
            let (px0, pz0, px1, pz1) = p.bounds(piece);
            let id = blob_at((px0 + px1) * 0.5, (pz0 + pz1) * 0.5);
            if id < 0 {
                loose_pieces += 1;
            } else {
                pieces_in[id as usize] += 1;
            }
        }

        let mut segments_in: Vec<usize> = vec![0; sizes.len()];
        let mut unstandable = 0usize;
        for s in world.segments() {
            let (cx, cz) = s.centre();
            let id = blob_at(cx, cz);
            if id < 0 {
                unstandable += 1;
            } else {
                segments_in[id as usize] += 1;
            }
        }

        // **LA PREGUNTA QUE DECIDE QUÉ HAY QUE ARREGLAR.** El grafo de bocas coincidentes —el que
        // ADR-098 usó para decir «86 % de árbol mayor»— contra las manchas andables. Si dos piezas
        // del MISMO nodo del grafo caen en manchas distintas, lo roto no es el enrutado: es que una
        // boca que el grafo da por unida no se puede cruzar.
        let n = world.placements().len();
        let segs = world.segments();
        let mouths: Vec<Vec<(f32, f32)>> = world
            .placements()
            .iter()
            .map(|p| {
                let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
                (0..piece.sockets.len())
                    .map(|i| p.world_socket_point(piece, i))
                    .collect()
            })
            .chain(segs.iter().map(segment_mouth_points))
            .collect();
        let mut parent: Vec<usize> = (0..mouths.len()).collect();
        fn find(parent: &mut [usize], mut a: usize) -> usize {
            while parent[a] != a {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }
            a
        }
        for i in 0..mouths.len() {
            for j in (i + 1)..mouths.len() {
                let touch = mouths[i].iter().any(|a| {
                    mouths[j]
                        .iter()
                        .any(|b| (a.0 - b.0).abs() < 0.02 && (a.1 - b.1).abs() < 0.02)
                });
                if touch {
                    let (ri, rj) = (find(&mut parent, i), find(&mut parent, j));
                    if ri != rj {
                        parent[ri] = rj;
                    }
                }
            }
        }
        // Por cada componente del grafo, en cuántas manchas andables se rompe.
        let mut split = std::collections::BTreeMap::<usize, Vec<i32>>::new();
        for k in 0..mouths.len() {
            let root = find(&mut parent, k);
            let (x, z) = if k < n {
                let p = &world.placements()[k];
                let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
                let (x0, z0, x1, z1) = p.bounds(piece);
                ((x0 + x1) * 0.5, (z0 + z1) * 0.5)
            } else {
                segs[k - n].centre()
            };
            split.entry(root).or_default().push(blob_at(x, z));
        }
        let mut worst: Vec<(usize, usize, usize)> = split
            .values()
            .map(|blobs| {
                let distinct: std::collections::BTreeSet<i32> =
                    blobs.iter().copied().filter(|b| *b >= 0).collect();
                (
                    blobs.len(),
                    distinct.len(),
                    blobs.iter().filter(|b| **b < 0).count(),
                )
            })
            .collect();
        worst.sort_unstable_by_key(|(nodes, _, _)| std::cmp::Reverse(*nodes));

        let mut order: Vec<usize> = (0..sizes.len()).collect();
        order.sort_unstable_by_key(|&i| std::cmp::Reverse(sizes[i]));
        println!(
            "[wg3] región ({rx},{rz}): {} manchas | {} tramos con el centro NO pisable | {} piezas \
             con el centro NO pisable",
            sizes.len(),
            unstandable,
            loose_pieces
        );
        println!(
            "[wg3]   componentes del grafo (nodos, manchas en que se parte, nodos no pisables): \
             {:?}",
            &worst[..worst.len().min(5)]
        );

        // **LA MEDIDA POR BOCAS, Y POR QUÉ NO POR CENTROS.** El centro de la caja de una pieza en L
        // cae dentro de su propia pared o en un armario de cuarenta celdas, así que preguntar «¿en
        // qué mancha está esta pieza?» por su centro inventa cortes que no existen —comprobado con
        // un transecto: la pieza 13 y la 17 de (0,0) tenían «manchas distintas» y entre sus dos
        // interiores no hay ni un centímetro cerrado—. Una boca, en cambio, es por definición un
        // sitio por donde se pasa.
        //
        // Y de ahí sale la pregunta afilada: **un nodo cuyas DOS bocas caen en manchas distintas es
        // geometría que parece conectada y no se puede cruzar.** Eso ya no es un artefacto de
        // muestreo: es un pasillo tapiado por dentro.
        let blobs_of_node = |k: usize| -> std::collections::BTreeSet<i32> {
            mouths[k]
                .iter()
                .map(|&(x, z)| blob_at(x, z))
                .filter(|b| *b >= 0)
                .collect()
        };
        let spawn_blob = {
            let (cx, cz) = (min_x + REGION_M * 0.5, min_z + REGION_M * 0.5);
            let mut found = -1;
            'ring: for radius in 0..(cells / 2) as i32 {
                for dz in -radius..=radius {
                    for dx in -radius..=radius {
                        let b = blob_at(cx + dx as f32 * CELL, cz + dz as f32 * CELL);
                        if b >= 0 {
                            found = b;
                            break 'ring;
                        }
                    }
                }
            }
            found
        };
        let (mut pieces_ok, mut segs_ok, mut uncrossable) = (0usize, 0usize, 0usize);
        for k in 0..mouths.len() {
            let set = blobs_of_node(k);
            if set.len() > 1 {
                uncrossable += 1;
                // Un nodo con las bocas en manchas distintas es geometría que el grafo da por
                // unida y que no se puede cruzar por dentro. Se nombra: son pocos y cada uno es
                // un sitio concreto del mundo.
                if k < n {
                    let p = &world.placements()[k];
                    let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
                    println!(
                        "[wg3]     NO SE CRUZA: pieza {k} ({}) en ({},{}) giro {} — bocas en las \
                         manchas {set:?}",
                        piece.id, p.origin_x_cm, p.origin_z_cm, p.rotation
                    );
                    // De boca a boca, paso a paso: lo que separa un escalón demasiado alto de una
                    // columna maciza son estos volcados y nada más.
                    if mouths[k].len() >= 2 {
                        let (ax, az) = mouths[k][0];
                        let (bx, bz) = mouths[k][1];
                        for step in 0..=40 {
                            let t = step as f32 / 40.0;
                            let (x, z) = (ax + (bx - ax) * t, az + (bz - az) * t);
                            let col = match raster_at(x, z) {
                                None => "fuera".to_string(),
                                Some(r) => r
                                    .column_at(x, z)
                                    .iter()
                                    .map(|s| format!("[{}..{}]", s.bottom_cm, s.top_cm))
                                    .collect::<Vec<_>>()
                                    .join(" "),
                            };
                            println!(
                                "[wg3]       t={t:.2} ({x:.2},{z:.2}) mancha {:>3}  {col}",
                                blob_at(x, z)
                            );
                        }
                    }
                } else {
                    let s = &segs[k - n];
                    println!(
                        "[wg3]     NO SE CRUZA: tramo {k} en ({},{}) {}×{} cm cota {} — bocas en \
                         las manchas {set:?}",
                        s.x_cm, s.z_cm, s.size_x_cm, s.size_z_cm, s.floor_y_cm
                    );
                }
            }
            if set.contains(&spawn_blob) {
                if k < n {
                    pieces_ok += 1;
                } else {
                    segs_ok += 1;
                }
            }
        }
        println!(
            "[wg3]   por BOCAS: {pieces_ok}/{n} piezas y {segs_ok}/{} tramos tocan la mancha del \
             jugador (#{spawn_blob}) | {uncrossable} nodos con las bocas en manchas DISTINTAS",
            segs.len()
        );

        // **LA BOCA QUE NO SE PISA.** Si dos nodos comparten el punto de boca, comparten mancha por
        // definición — salvo que ese punto no sea pisable, y entonces el enlace que el grafo cuenta
        // como bueno es exactamente el que corta el mundo. Es el único sitio donde puede estar la
        // diferencia entre «una componente de 60 nodos» y «24 nodos alcanzables».
        let mut dead = 0usize;
        let mut dead_dump = 0usize;
        // Tres causas, y cada una se arregla en un sitio distinto: la boca cae fuera de lo medido
        // (las de junta, que dan a la región vecina y no son un fallo), la boca está MACIZA de
        // suelo a techo, o simplemente no hay hueco para la cabeza.
        let (mut dead_outside, mut dead_solid, mut dead_other) = (0usize, 0usize, 0usize);
        let mut solid_by_segment = 0usize;
        for k in 0..mouths.len() {
            for &(mx, mz) in &mouths[k] {
                if blob_at(mx, mz) >= 0 {
                    continue;
                }
                dead += 1;
                match raster_at(mx, mz) {
                    None => dead_outside += 1,
                    Some(r) => {
                        let col = r.column_at(mx, mz);
                        let solid = col.len() == 1 && col[0].top_cm - col[0].bottom_cm > 200;
                        if solid {
                            dead_solid += 1;
                            // ¿Y quién es el macizo? Si hay OTRO tramo cuya huella cubre el punto y
                            // que no declara boca ahí, el vano da contra su pared.
                            let walled = segs.iter().enumerate().any(|(idx, s)| {
                                if n + idx == k {
                                    return false;
                                }
                                let (x0, z0, x1, z1) = s.bounds();
                                mx >= x0 - 0.05
                                    && mx <= x1 + 0.05
                                    && mz >= z0 - 0.05
                                    && mz <= z1 + 0.05
                                    && !mouths[n + idx]
                                        .iter()
                                        .any(|q| (q.0 - mx).abs() < 0.02 && (q.1 - mz).abs() < 0.02)
                            });
                            if walled {
                                solid_by_segment += 1;
                            }
                        } else {
                            dead_other += 1;
                        }
                    }
                }
                if dead_dump < 4 {
                    dead_dump += 1;
                    let col = match raster_at(mx, mz) {
                        None => "fuera del ráster".to_string(),
                        Some(r) => r
                            .column_at(mx, mz)
                            .iter()
                            .map(|s| format!("[{}..{}]", s.bottom_cm, s.top_cm))
                            .collect::<Vec<_>>()
                            .join(" "),
                    };
                    // Quién tiene geometría encima de ese punto. Un tapón de pieza y un tapón de
                    // otro tramo se arreglan en sitios distintos, así que hay que saber cuál es.
                    let mut over = Vec::new();
                    for (idx, p) in world.placements().iter().enumerate() {
                        let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
                        let (x0, z0, x1, z1) = p.bounds(piece);
                        if mx >= x0 - 0.05 && mx <= x1 + 0.05 && mz >= z0 - 0.05 && mz <= z1 + 0.05
                        {
                            over.push(format!("pieza {idx}"));
                        }
                    }
                    for (idx, s) in segs.iter().enumerate() {
                        let (x0, z0, x1, z1) = s.bounds();
                        if mx >= x0 - 0.05 && mx <= x1 + 0.05 && mz >= z0 - 0.05 && mz <= z1 + 0.05
                        {
                            over.push(format!("tramo {}", n + idx));
                        }
                    }
                    println!(
                        "[wg3]     boca muerta en ({mx:.2},{mz:.2}) del {} {k}: {col} — encima: \
                         {over:?}",
                        if k < n { "pieza" } else { "tramo" }
                    );
                    for (idx, s) in segs.iter().enumerate() {
                        let (x0, z0, x1, z1) = s.bounds();
                        if mx < x0 - 0.05 || mx > x1 + 0.05 || mz < z0 - 0.05 || mz > z1 + 0.05 {
                            continue;
                        }
                        println!(
                            "[wg3]       tramo {}: ({},{}) {}×{} cm cota {} alto {} bocas {:?}",
                            n + idx,
                            s.x_cm,
                            s.z_cm,
                            s.size_x_cm,
                            s.size_z_cm,
                            s.floor_y_cm,
                            s.height_cm,
                            s.openings
                                .iter()
                                .map(|o| (o.side, o.offset_cm, o.width_cm))
                                .collect::<Vec<_>>()
                        );
                        for b in segment::segment_boxes(s) {
                            let (hx, hz) = (b.size[0] * 0.5, b.size[2] * 0.5);
                            if (mx - b.center[0]).abs() <= hx + 0.01
                                && (mz - b.center[2]).abs() <= hz + 0.01
                            {
                                println!(
                                    "[wg3]         caja kind {} centro ({:.2},{:.2},{:.2}) tam \
                                     ({:.2},{:.2},{:.2})",
                                    b.kind,
                                    b.center[0],
                                    b.center[1],
                                    b.center[2],
                                    b.size[0],
                                    b.size[1],
                                    b.size[2]
                                );
                            }
                        }
                    }
                }
            }
        }
        let total_mouths: usize = mouths.iter().map(|v| v.len()).sum();
        println!(
            "[wg3]   bocas que no se pisan: {dead} de {total_mouths} — {dead_outside} fuera de lo \
             medido (junta), {dead_solid} MACIZAS ({solid_by_segment} de ellas contra la pared de \
             otro tramo), {dead_other} sin hueco para la cabeza"
        );
        for &i in order.iter().take(6) {
            println!(
                "[wg3]   mancha #{i}: {} celdas, {} piezas, {} tramos",
                sizes[i], pieces_in[i], segments_in[i]
            );
        }
    }
}

/// **QUÉ TIENDE EL ENRUTADOR, Y QUÉ DESCARTA** (ADR-098 T6).
///
/// Los descartes son la mitad útil: dicen si lo que frena a los conectores es el mundo apretado —y
/// entonces la palanca es la geometría— o una regla nuestra —cota, anchura, tipo—, que es una
/// decisión y no un límite. Sin este número, subir una perilla es adivinar.
#[test]
fn probe_generated_connectors() {
    let m = real_manifest();

    for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, 2)] {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let settings = region_settings(&m, SERVED_SEED, region);
        let start = std::time::Instant::now();
        let composed = compose::compose(region.composer_seed(SERVED_SEED), &m, &settings);
        let elapsed = start.elapsed();

        println!(
            "[wg3] región ({rx},{rz}): {} piezas, {} bocas abiertas ({} sin usar), \
             {} componentes al final | {} conectores ({} unen islas, {} anillos), {} tramos | \
             descartes: cota {}, ancho {}, tipo {}, geometría {} | {:?}",
            composed.placements.len(),
            composed.route_mouths,
            composed.route_unused_mouths,
            composed.route_components_left,
            composed.connectors,
            composed.connectors_joining_islands,
            composed.connectors - composed.connectors_joining_islands,
            composed.segments.len(),
            composed.rejected_by_cota,
            composed.rejected_by_width,
            composed.rejected_by_kind,
            composed.rejected_by_route_geometry,
            elapsed
        );
        // **¿EL ENRUTADOR RINDE POCO, O ESTÁ HAMBRIENTO?** Son diagnósticos opuestos: uno se
        // arregla en `route.rs` y el otro solo con catálogo. Un árbol de N piezas gasta 2(N−1)
        // bocas; si el catálogo tiene ~2 bocas por pieza, al terminar no queda NADA que enganchar,
        // y ninguna mejora del enrutado lo cambia.
        let sockets: usize = composed
            .placements
            .iter()
            .map(|p| {
                m.piece(p.placement.piece)
                    .expect("pieza fuera del catálogo")
                    .sockets
                    .len()
            })
            .sum();
        let pieces = composed.placements.len();
        println!(
            "[wg3]   bocas del catálogo colocadas: {sockets} en {pieces} piezas ({:.2} por pieza); \
             un árbol las gasta de dos en dos y deja {} libres en el mejor caso",
            sockets as f32 / pieces.max(1) as f32,
            sockets as i64 - 2 * (pieces as i64 - 1)
        );

        for (x, z, side, width) in &composed.route_leftover {
            println!(
                "[wg3]   sin enganchar: ({:.2}, {:.2}) lado {side} ancho {:.2}",
                *x as f32 / 100.0,
                *z as f32 / 100.0,
                *width as f32 / 100.0
            );
        }
    }
}

/// Los puntos de mundo de las bocas de un tramo. Es lo que permite preguntarle a la GEOMETRÍA si
/// un tramo une dos cosas, en vez de fiarse de lo que el enrutador dice que unió.
fn segment_mouth_points(c: &Wg3Segment) -> Vec<(f32, f32)> {
    let (w, d) = (c.size_x(), c.size_z());
    c.openings
        .iter()
        .map(|o| {
            let (lx, lz) = placement::local_point(o.side, o.offset_cm as f32 / 100.0, w, d);
            (c.min_x() + lx, c.min_z() + lz)
        })
        .collect()
}

// ─────────────────── ADR-098 T2 — el oráculo de conectores (paridad C# ↔ Rust) ───────────────────

#[derive(serde::Deserialize)]
struct ConnOpening {
    side: u8,
    offset_cm: i32,
    width_cm: i32,
}

#[derive(serde::Deserialize)]
struct ConnBox {
    cx_cm: i32,
    cy_cm: i32,
    cz_cm: i32,
    sx_cm: i32,
    sy_cm: i32,
    sz_cm: i32,
    kind: u8,
}

#[derive(serde::Deserialize)]
struct ConnSegment {
    name: String,
    x_cm: i32,
    z_cm: i32,
    size_x_cm: i32,
    size_z_cm: i32,
    floor_y_cm: i32,
    height_cm: i32,
    style: u8,
    openings: Vec<ConnOpening>,
    boxes: Vec<ConnBox>,
}

#[derive(serde::Deserialize)]
struct ConnectorOracle {
    slab_thickness_mm: i32,
    wall_thickness_mm: i32,
    segments: Vec<ConnSegment>,
}

fn connector_oracle() -> ConnectorOracle {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("fixtures")
        .join("wg3_connector_oracle.json");
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| {
        panic!(
            "sin oráculo de conectores en {}: {e}. Reexpórtalo desde Unity con «Backrooms ▸ \
             WorldGen3 ▸ Exportar oráculo de conectores».",
            path.display()
        )
    });
    serde_json::from_str(&text).expect("oráculo de conectores ilegible")
}

/// Metros a centimetros con el MISMO redondeo que `Mathf.RoundToInt`: a la par en los empates.
///
/// Redondear al mas cercano en vez de a la par mete un centimetro de diferencia justo en las cotas
/// que caen en medio —el centro de una pared de 15 mm cae en 232,5— y el oraculo lo lee como una
/// deriva entre idiomas cuando solo es otra regla de redondeo.
fn cm(v: f32) -> i32 {
    ((v * 100.0) as f64).round_ties_even() as i32
}

/// EL CRITERIO DE CIERRE DE LA CELDA GENERADA (ADR-098 D2).
///
/// La expansión está escrita dos veces: C# la usa para dibujar —construyendo una pieza sintética y
/// llamando a `Wg3Geometry.Build`, que es la fuente única del aspecto— y este lado la usa para
/// rasterizar la colisión sin ver una malla (R1). Dos implementaciones consistentes cada una por su
/// cuenta pueden diferir entre ellas, y el síntoma sería el peor del sistema: una pared que se ve y
/// no frena, o al revés.
///
/// Así que uno escribe las cajas («Backrooms ▸ WorldGen3 ▸ Exportar oráculo de conectores») y el otro
/// las reproduce, caja a caja y EN ORDEN. El orden importa: es parte del contrato de `segment_boxes`.
///
/// SOLO LO SÓLIDO: la decoración no cruza la frontera de autoridad (R25). Que el fixture no traiga
/// rodapiés es la afirmación de que Rust no tiene por qué generarlos.
#[test]
fn the_generated_cell_expands_the_same_in_both_languages() {
    let oracle = connector_oracle();

    assert_eq!(
        oracle.slab_thickness_mm,
        cm(segment::SLAB_THICKNESS_M) * 10,
        "grosor de losa distinto entre C# y Rust"
    );
    assert_eq!(
        oracle.wall_thickness_mm,
        cm(segment::WALL_THICKNESS_M) * 10,
        "grosor de pared distinto entre C# y Rust"
    );

    for oc in &oracle.segments {
        let c = Wg3Segment {
            x_cm: oc.x_cm,
            z_cm: oc.z_cm,
            size_x_cm: oc.size_x_cm,
            size_z_cm: oc.size_z_cm,
            floor_y_cm: oc.floor_y_cm,
            height_cm: oc.height_cm,
            openings: oc
                .openings
                .iter()
                .map(|o| Wg3Opening {
                    side: o.side,
                    offset_cm: o.offset_cm,
                    width_cm: o.width_cm,
                })
                .collect(),
            style: oc.style,
        };
        assert!(
            c.problems().is_empty(),
            "el tramo «{}» del oráculo no es válida: {:?}",
            oc.name,
            c.problems()
        );

        let mine = segment::segment_boxes(&c);
        assert_eq!(
            oc.boxes.len(),
            mine.len(),
            "«{}»: C# da {} cajas sólidas y Rust {}",
            oc.name,
            oc.boxes.len(),
            mine.len()
        );

        for (i, (want, got)) in oc.boxes.iter().zip(mine.iter()).enumerate() {
            let got = [
                cm(got.center[0]),
                cm(got.center[1]),
                cm(got.center[2]),
                cm(got.size[0]),
                cm(got.size[1]),
                cm(got.size[2]),
                got.kind as i32,
            ];
            let want = [
                want.cx_cm,
                want.cy_cm,
                want.cz_cm,
                want.sx_cm,
                want.sy_cm,
                want.sz_cm,
                want.kind as i32,
            ];
            assert_eq!(
                want, got,
                "«{}», caja {i}: C# {want:?} contra Rust {got:?} (centro xyz, tamaño xyz, tipo)",
                oc.name
            );
        }
    }
}

/// **DE DÓNDE SALE `ROUTED_CAP_CHANCE`** (ADR-098 enmienda 4). El instrumento que eligió el número.
///
/// Mide, sobre 16 regiones del mundo SERVIDO, qué cambia al dejar más bocas sin tapar: **metros
/// cuadrados alcanzables a pie desde donde aparece el jugador** —la columna que manda—, cuántas
/// regiones se recorren enteras, cuántos conectores generados se pisan, cuántas puertas de junta se
/// alcanzan y cuántas piezas del catálogo quedan.
///
/// Dos trampas que este barrido ya pisó, y por eso se queda escrito:
///
/// 1. La primera versión usaba `compose_with`, que toma `composer_seed(world_seed)` y **no** la
///    semilla de la REGIÓN: componía cuatro veces el mismo mundo con bordes distintos. Los números
///    no se parecían en nada a los del mundo servido. Se compone con `compose_region_with`.
/// 2. La segunda medía el PORCENTAJE de lo pisable que se alcanza. Con eso, 0,30 salía perfecto —y
///    es un mundo un 41 % más pequeño. Una región que pasa de 3298 m² al 89 % a 789 m² al 100 % ha
///    perdido dos tercios de sitio donde estar. La unidad es el metro cuadrado.
#[test]
#[ignore]
fn sweep_cap_chance() {
    const CELL: f32 = 0.5;
    const HEAD_M: f32 = 1.0;
    const MAX_STEP: f32 = WALK_STEP_M;

    let m = real_manifest();

    let walk = |world: &Wg3ServedWorld,
                region: Wg3RegionCoord|
     -> (f32, usize, usize, usize, usize) {
        let (min_x, min_z, _, _) = region.bounds();
        let side = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        let mut rasters = Vec::with_capacity(side * side);
        for cz in 0..side {
            for cx in 0..side {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                rasters.push(chunk::build_chunk_raster(
                    &m,
                    &world.placements_touching_chunk(&m, coord),
                    &world.segments_touching_chunk(coord),
                    coord,
                ));
            }
        }
        let raster_at = |x: f32, z: f32| -> Option<&Wg3Raster> {
            let coord = chunk::Wg3ChunkCoord::containing(x, z);
            let (dx, dz) = (coord.x - base.x, coord.z - base.z);
            if dx < 0 || dz < 0 || dx as usize >= side || dz as usize >= side {
                return None;
            }
            rasters.get(dz as usize * side + dx as usize)
        };
        let cells = (REGION_M / CELL) as usize;
        let mut floors: Vec<Vec<f32>> = vec![Vec::new(); cells * cells];
        for iz in 0..cells {
            for ix in 0..cells {
                let x = min_x + ix as f32 * CELL + CELL * 0.5;
                let z = min_z + iz as f32 * CELL + CELL * 0.5;
                let Some(r) = raster_at(x, z) else { continue };
                let column = r.column_at(x, z);
                let mut out = Vec::new();
                for (i, span) in column.iter().enumerate() {
                    let head = match column.get(i + 1) {
                        Some(next) => (next.bottom_cm - span.top_cm) as f32 / 100.0,
                        None => f32::MAX,
                    };
                    if (HEAD_M..=CEILING_CAP_M).contains(&head) {
                        out.push(span.top_cm as f32 / 100.0);
                    }
                }
                floors[iz * cells + ix] = out;
            }
        }
        let start = (cells / 2, cells / 2);
        let mut from = None;
        'search: for radius in 0..cells / 2 {
            for dz in -(radius as i32)..=(radius as i32) {
                for dx in -(radius as i32)..=(radius as i32) {
                    let (ix, iz) = (start.0 as i32 + dx, start.1 as i32 + dz);
                    if ix < 0 || iz < 0 || ix as usize >= cells || iz as usize >= cells {
                        continue;
                    }
                    if !floors[iz as usize * cells + ix as usize].is_empty() {
                        from = Some((ix as usize, iz as usize));
                        break 'search;
                    }
                }
            }
        }
        let Some(from) = from else {
            return (0.0, 0, 0, 0, world.placements().len());
        };
        let mut seen_level: Vec<Vec<bool>> = floors.iter().map(|l| vec![false; l.len()]).collect();
        let mut seen = vec![false; cells * cells];
        let mut q = std::collections::VecDeque::new();
        seen_level[from.1 * cells + from.0][0] = true;
        q.push_back((from.0, from.1, 0usize));
        let mut reached = 0usize;
        while let Some((ix, iz, li)) = q.pop_front() {
            if !seen[iz * cells + ix] {
                seen[iz * cells + ix] = true;
                reached += 1;
            }
            let here = floors[iz * cells + ix][li];
            for (dx, dz) in [(1i32, 0i32), (-1, 0), (0, 1), (0, -1)] {
                let (nx, nz) = (ix as i32 + dx, iz as i32 + dz);
                if nx < 0 || nz < 0 || nx as usize >= cells || nz as usize >= cells {
                    continue;
                }
                let (nx, nz) = (nx as usize, nz as usize);
                for (nl, there) in floors[nz * cells + nx].iter().enumerate() {
                    if seen_level[nz * cells + nx][nl] || (there - here).abs() > MAX_STEP {
                        continue;
                    }
                    seen_level[nz * cells + nx][nl] = true;
                    q.push_back((nx, nz, nl));
                }
            }
        }
        let walked = |x: f32, z: f32| -> bool {
            let ix = ((x - min_x) / CELL) as i32;
            let iz = ((z - min_z) / CELL) as i32;
            ix >= 0
                && iz >= 0
                && (ix as usize) < cells
                && (iz as usize) < cells
                && seen[iz as usize * cells + ix as usize]
        };
        let segs_ok = world
            .segments()
            .iter()
            .filter(|s| {
                let (cx, cz) = s.centre();
                walked(cx, cz)
            })
            .count();
        let seed = composer_seed(SERVED_SEED);
        let gates = junction::gates_of_region(seed, region.x, region.z, region.bounds());
        let gates_ok = gates
            .iter()
            .filter(|g| {
                let (nx, nz) = match g.outward_side % 4 {
                    0 => (0.0, -0.45),
                    1 => (-0.45, 0.0),
                    2 => (0.0, 0.45),
                    _ => (0.45, 0.0),
                };
                walked(g.x + nx, g.z + nz)
            })
            .count();
        (
            reached as f32 * CELL * CELL,
            segs_ok,
            world.segments().len(),
            gates_ok,
            world.placements().len(),
        )
    };

    let regions: Vec<(i32, i32)> = (-2..2).flat_map(|x| (0..4).map(move |z| (x, z))).collect();
    for chance in [0.05f32, 0.08, 0.11, 0.14, 0.18, 0.22, 0.30] {
        let (
            mut area_sum,
            mut seg_ok,
            mut seg_all,
            mut gate_ok,
            mut gate_all,
            mut pieces,
            mut full,
        ) = (0.0f32, 0usize, 0usize, 0usize, 0usize, 0usize, 0usize);
        for &(rx, rz) in &regions {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let mut settings = region_settings(&m, SERVED_SEED, region);
            settings.deliberate_cap_chance = chance;
            let world = Wg3ServedWorld::compose_region_with(&m, SERVED_SEED, region, &settings);
            let (area, so, st, go, np) = walk(&world, region);
            let gates = junction::gates_of_region(
                composer_seed(SERVED_SEED),
                region.x,
                region.z,
                region.bounds(),
            );
            area_sum += area;
            seg_ok += so;
            seg_all += st;
            gate_ok += go;
            gate_all += gates.len();
            pieces += np;
            if so == st && st > 0 {
                full += 1;
            }
        }
        let n = regions.len() as f32;
        println!(
            "[wg3] cap {chance:.2} | m2 ALCANZABLES/region {:.0} | regiones con todos los tramos              pisados {full}/{} | tramos {seg_ok}/{seg_all} | puertas {gate_ok}/{gate_all} |              piezas/region {:.1}",
            area_sum / n,
            regions.len(),
            pieces as f32 / n
        );
    }
}

/// **MIRAR EL MUNDO SIN UNITY.** Vuelca cada región a un SVG en la carpeta `WG3_MAP_DIR`.
///
/// Existe porque durante toda la migración la única forma de ver WG3 ha sido montar una sesión de
/// juego de noventa segundos, y eso no se puede hacer en cada cambio. Un plano no sustituye a
/// jugarlo —no dice nada del aspecto— pero contesta lo único que se le pregunta a la topología:
/// dónde se puede ir.
///
/// Verde: lo que se anda desde donde aparece el jugador. Gris: pisable pero incomunicado. Trazo
/// claro: piezas del catálogo. Trazo ámbar: conectores GENERADOS. Círculo rojo: el spawn.
#[test]
#[ignore]
fn dump_region_maps() {
    const CELL: f32 = 0.5;
    const HEAD_M: f32 = 1.0;
    const MAX_STEP: f32 = WALK_STEP_M;
    const PX: f32 = 4.0;

    let dir = std::env::var("WG3_MAP_DIR").expect("WG3_MAP_DIR: carpeta donde escribir los planos");
    // ADR-099 — `WG3_ABSORB=0.1` dibuja el mundo CON absorción. Por variable y no por constante
    // para poder poner los dos planos uno al lado del otro sin recompilar entre medias, que es lo
    // único que deja ver qué cambia de forma.
    let absorb: f32 = std::env::var("WG3_ABSORB")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(0.0);
    let m = real_manifest();

    for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, 2)] {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let settings = compose::Wg3ComposerSettings {
            absorb_chance: absorb,
            ..region_settings(&m, SERVED_SEED, region)
        };
        let world = Wg3ServedWorld::compose_region_with(&m, SERVED_SEED, region, &settings);
        let (min_x, min_z, _, _) = region.bounds();

        let side = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        let mut rasters = Vec::with_capacity(side * side);
        for cz in 0..side {
            for cx in 0..side {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                rasters.push(chunk::build_chunk_raster_with_carves(
                    &m,
                    &world.placements_touching_chunk(&m, coord),
                    &world.segments_touching_chunk(coord),
                    &world.carves_touching_chunk(coord),
                    coord,
                ));
            }
        }
        let raster_at = |x: f32, z: f32| -> Option<&Wg3Raster> {
            let coord = chunk::Wg3ChunkCoord::containing(x, z);
            let (dx, dz) = (coord.x - base.x, coord.z - base.z);
            if dx < 0 || dz < 0 || dx as usize >= side || dz as usize >= side {
                return None;
            }
            rasters.get(dz as usize * side + dx as usize)
        };

        let cells = (REGION_M / CELL) as usize;
        let mut floors: Vec<Vec<f32>> = vec![Vec::new(); cells * cells];
        for iz in 0..cells {
            for ix in 0..cells {
                let x = min_x + ix as f32 * CELL + CELL * 0.5;
                let z = min_z + iz as f32 * CELL + CELL * 0.5;
                let Some(r) = raster_at(x, z) else { continue };
                let column = r.column_at(x, z);
                let mut out = Vec::new();
                for (i, span) in column.iter().enumerate() {
                    let head = match column.get(i + 1) {
                        Some(next) => (next.bottom_cm - span.top_cm) as f32 / 100.0,
                        None => f32::MAX,
                    };
                    if (HEAD_M..=CEILING_CAP_M).contains(&head) {
                        out.push(span.top_cm as f32 / 100.0);
                    }
                }
                floors[iz * cells + ix] = out;
            }
        }

        let mut blob_of: Vec<Vec<i32>> = floors.iter().map(|l| vec![-1; l.len()]).collect();
        let mut blobs = 0i32;
        for iz0 in 0..cells {
            for ix0 in 0..cells {
                for l0 in 0..floors[iz0 * cells + ix0].len() {
                    if blob_of[iz0 * cells + ix0][l0] >= 0 {
                        continue;
                    }
                    let id = blobs;
                    blobs += 1;
                    blob_of[iz0 * cells + ix0][l0] = id;
                    let mut q = std::collections::VecDeque::new();
                    q.push_back((ix0, iz0, l0));
                    while let Some((ix, iz, li)) = q.pop_front() {
                        let here = floors[iz * cells + ix][li];
                        for (dx, dz) in [(1i32, 0i32), (-1, 0), (0, 1), (0, -1)] {
                            let (nx, nz) = (ix as i32 + dx, iz as i32 + dz);
                            if nx < 0 || nz < 0 || nx as usize >= cells || nz as usize >= cells {
                                continue;
                            }
                            let (nx, nz) = (nx as usize, nz as usize);
                            for (nl, there) in floors[nz * cells + nx].iter().enumerate() {
                                if blob_of[nz * cells + nx][nl] >= 0
                                    || (there - here).abs() > MAX_STEP
                                {
                                    continue;
                                }
                                blob_of[nz * cells + nx][nl] = id;
                                q.push_back((nx, nz, nl));
                            }
                        }
                    }
                }
            }
        }
        let spawn = {
            let c = cells / 2;
            let mut found = -1;
            'ring: for radius in 0..(cells / 2) as i32 {
                for dz in -radius..=radius {
                    for dx in -radius..=radius {
                        let (ix, iz) = (c as i32 + dx, c as i32 + dz);
                        if ix < 0 || iz < 0 || ix as usize >= cells || iz as usize >= cells {
                            continue;
                        }
                        if let Some(&b) = blob_of[iz as usize * cells + ix as usize].first() {
                            if b >= 0 {
                                found = b;
                                break 'ring;
                            }
                        }
                    }
                }
            }
            found
        };

        let w = REGION_M * PX;
        let mut svg = format!(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w:.0} {w:.0}\" \
             width=\"{w:.0}\" height=\"{w:.0}\">\n<rect width=\"100%\" height=\"100%\" \
             fill=\"#14161a\"/>\n"
        );
        for iz in 0..cells {
            let mut run_start: Option<usize> = None;
            let mut run_blob = -2;
            for ix in 0..=cells {
                let b = if ix == cells {
                    -2
                } else {
                    *blob_of[iz * cells + ix].first().unwrap_or(&-1)
                };
                if b != run_blob {
                    if let Some(s) = run_start {
                        if run_blob >= 0 {
                            let fill = if run_blob == spawn {
                                "#4ade80"
                            } else {
                                "#64748b"
                            };
                            svg += &format!(
                                "<rect x=\"{:.1}\" y=\"{:.1}\" width=\"{:.1}\" height=\"{:.1}\" \
                                 fill=\"{fill}\" opacity=\"0.85\"/>\n",
                                s as f32 * CELL * PX,
                                (cells - 1 - iz) as f32 * CELL * PX,
                                (ix - s) as f32 * CELL * PX,
                                CELL * PX
                            );
                        }
                    }
                    run_start = Some(ix);
                    run_blob = b;
                }
            }
        }
        for p in world.placements() {
            let piece = m.piece(p.piece).expect("pieza fuera del catálogo");
            let (x0, z0, x1, z1) = p.bounds(piece);
            svg += &format!(
                "<rect x=\"{:.1}\" y=\"{:.1}\" width=\"{:.1}\" height=\"{:.1}\" fill=\"none\" \
                 stroke=\"#e2e8f0\" stroke-width=\"1\" opacity=\"0.55\"/>\n",
                (x0 - min_x) * PX,
                (REGION_M - (z1 - min_z)) * PX,
                (x1 - x0) * PX,
                (z1 - z0) * PX
            );
        }
        for s in world.segments() {
            let (x0, z0, x1, z1) = s.bounds();
            svg += &format!(
                "<rect x=\"{:.1}\" y=\"{:.1}\" width=\"{:.1}\" height=\"{:.1}\" fill=\"none\" \
                 stroke=\"#fbbf24\" stroke-width=\"1.6\"/>\n",
                (x0 - min_x) * PX,
                (REGION_M - (z1 - min_z)) * PX,
                (x1 - x0) * PX,
                (z1 - z0) * PX
            );
        }
        svg += &format!(
            "<circle cx=\"{:.1}\" cy=\"{:.1}\" r=\"6\" fill=\"none\" stroke=\"#f87171\" \
             stroke-width=\"2.5\"/>\n</svg>\n",
            REGION_M * 0.5 * PX,
            REGION_M * 0.5 * PX
        );
        let path = format!("{dir}/wg3_region_{rx}_{rz}.svg");
        std::fs::write(&path, svg).expect("escribir el plano");
        println!("[wg3] {path} — mancha del jugador #{spawn} de {blobs}");
    }
}

// ── ADR-100: el plan de región ──────────────────────────────────────────────────────────────

use super::plan::{self, LinkKind, RegionPlan, SpaceRole};

/// Las cuatro regiones sobre las que se mide todo desde la auditoría del 2026-08-28. Se fijan aquí
/// para que cada sonda nueva compare contra las MISMAS, que es lo único que hace comparables dos
/// medidas tomadas con una semana de diferencia.
const AUDIT_REGIONS: [(i32, i32); 4] = [(0, 0), (1, 0), (0, 1), (-1, 2)];

/// Hueco de cabeza por encima del cual una sonda deja de considerar que se anda ahí, en metros.
///
/// **Subido de 6,0 a 7,0 por ADR-102 D5, y el motivo importa más que el número.** El tope existe para
/// que la cara de arriba de una pared no cuente como suelo; el caso de verdad —un tejado— ya lo
/// descarta no tener NADA encima. Lo que el 6,0 descartaba de más era el hueco de escalera: un pozo
/// que atraviesa dos plantas mide 6,52 m de suelo a techo, así que sus celdas del pie quedaban fuera
/// de lo pisable y la escalera aparecía desconectada de la planta baja por abajo. Salió como
/// `x=32.85: pisables []` en el corte del volcador, con los trece peldaños de encima perfectos.
///
/// Cambia todas las cifras de superficie andable que se publiquen a partir de aquí, y por eso está en
/// un solo sitio con nombre.
const CEILING_CAP_M: f32 = 7.0;

/// Escalón que una sonda acepta subir entre dos celdas vecinas, en metros.
///
/// **Subido de 0,20 a 0,27 por ADR-102 D5, y por el mismo motivo que el de arriba.** El 0,20 era la
/// contrahuella del catálogo, y en su día valía porque no había otra escalera; la de planta sube 25,5
/// cm por peldaño —332 entre trece— y con el tope viejo las sondas la declaraban insubible. Salía como
/// «mancha mayor 73 % de lo pisable» con la planta alta entera al otro lado. El número que manda es lo
/// que sube el jugador, `plan::MAX_WALK_STEP_CM`, medido contra el `m_StepOffset` del prefab.
const WALK_STEP_M: f32 = plan::MAX_WALK_STEP_CM as f32 / 100.0;

/// El plan de una región de la auditoría, con sus puertas de junta reales.
fn plan_of(m: &Wg3Manifest, rx: i32, rz: i32) -> RegionPlan {
    let region = Wg3RegionCoord { x: rx, z: rz };
    let bounds = region.bounds();
    let seed = composer_seed(SERVED_SEED);
    let gates = junction::gates_of_region(seed, rx, rz, bounds);
    // La semilla del PLAN es la de la región, igual que la del compositor: dos regiones vecinas del
    // mismo mundo tienen que planificar edificios distintos. La de las PUERTAS es la del mundo, por
    // lo mismo de siempre — la de la región difiere a cada lado del borde y daría dos listas que no
    // casan (`junction`).
    let _ = (m, bounds, gates);
    // ADR-130 — la planta de CALLE del edificio servido: en una torre la calle no se hunde y el
    // índice no es 0, y un plan a solas mediría un mundo que no existe.
    let b = served_building_of(rx, rz);
    b.storeys
        .into_iter()
        .nth(b.ground)
        .expect("la calle siempre existe")
}

#[test]
fn the_plan_is_coherent_in_every_audited_region() {
    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        let p = plan_of(&m, rx, rz);
        let problems = p.problems();
        assert!(
            problems.is_empty(),
            "el plan de ({rx},{rz}) no es coherente: {}",
            problems.join("; ")
        );
        assert!(
            !p.spaces.is_empty(),
            "el plan de ({rx},{rz}) no tiene un solo espacio"
        );
    }
}

// ── ADR-102 D1: el edificio de varias plantas ───────────────────────────────────────────────

/// Las plantas del primer corte (ADR-102 D3). Dos, y no porque dos sea el número correcto: el salto
/// de una a dos es donde están todos los fallos, y el de dos a tres no aporta ninguno nuevo.
const STOREYS: usize = 2;

fn building_of(rx: i32, rz: i32) -> plan::RegionBuilding {
    let region = Wg3RegionCoord { x: rx, z: rz };
    let bounds = region.bounds();
    let gates = junction::gates_of_region(composer_seed(SERVED_SEED), rx, rz, bounds);
    plan::plan_building_at(
        region.composer_seed(SERVED_SEED),
        bounds,
        &gates,
        STOREYS,
        plan::basements_for(rx, rz),
    )
}

/// El edificio **tal y como lo sirve el backend**: [`plan::REGION_STOREYS`] plantas, no [`STOREYS`].
///
/// La diferencia no es un detalle y ya costó una medición entera. `building_of` planifica DOS
/// plantas a propósito —es el corte de ADR-102 D1—, así que cualquier sonda que cuente plantas sobre
/// él sólo puede contestar «dos»: fue esa sonda la que dijo «47 de 49 edificios tienen 2 plantas»,
/// que era la respuesta de su propio parámetro y no la del mundo. Todo lo que se afirme sobre la
/// ALTURA del mundo servido se mide aquí.
fn served_building_of(rx: i32, rz: i32) -> plan::RegionBuilding {
    let region = Wg3RegionCoord { x: rx, z: rz };
    let bounds = region.bounds();
    let gates = junction::gates_of_region(composer_seed(SERVED_SEED), rx, rz, bounds);
    plan::plan_building_at(
        region.composer_seed(SERVED_SEED),
        bounds,
        &gates,
        plan::REGION_STOREYS,
        plan::basements_for(rx, rz),
    )
}

/// **Un barrido, no las cuatro de referencia, y esa lección ya costó una vez.**
///
/// El hueco de escalera depende de que COINCIDAN dos geometrías planificadas por separado: que un
/// espacio de abajo quepa entero dentro de uno construido de arriba. Eso es coincidencia de semilla,
/// igual que lo era el vano perdido de `región (-1,0)` — y aquél no salió mirando cuatro regiones,
/// salió en una partida. Cuarenta y nueve es barato y sí muerde.
#[test]
fn the_building_is_coherent_in_every_region() {
    let mut with_two = 0usize;
    for rz in -3..4 {
        for rx in -3..4 {
            let b = building_of(rx, rz);
            let problems = b.problems();
            assert!(
                problems.is_empty(),
                "el edificio de ({rx},{rz}) no es coherente: {}",
                problems.join("; ")
            );
            if b.storeys.len() > 1 {
                with_two += 1;
            }
        }
    }
    // Y que no esté pasando por no construir nada: un edificio de una sola planta cumple TODO lo de
    // arriba sin esfuerzo, así que sin este suelo el test verde no diría nada. No se exigen las 49
    // —el edificio sube sólo hasta donde se puede subir, y hay regiones donde el hueco no encaja—
    // pero sí que la segunda planta sea la norma y no la excepción.
    println!("[wg3] segunda planta en {with_two} de 49 regiones");
    assert!(
        with_two * 2 >= 49,
        "sólo {with_two} de 49 regiones levantaron una segunda planta"
    );
}

/// La planta alta no puede ser decorado: tiene que subir una escalera a ella, y esa escalera tiene
/// que salir DENTRO de un espacio construido. `problems()` ya lo exige; esto comprueba que lo exige
/// de verdad y no por casualidad de que no haya plantas altas.
#[test]
fn every_upper_storey_has_a_stair_that_lands_somewhere() {
    for (rx, rz) in AUDIT_REGIONS {
        let b = building_of(rx, rz);
        assert_eq!(
            STOREYS,
            b.storeys.len() - b.ground,
            "({rx},{rz}) no levantó las plantas"
        );
        assert!(
            !b.wells.is_empty(),
            "({rx},{rz}) no tiene hueco de escalera"
        );
        for w in &b.wells {
            let below = &b.storeys[w.storey_below].spaces[w.space_below];
            let above = &b.storeys[w.storey_below + 1].spaces[w.space_above];
            assert_eq!(SpaceRole::Stair, below.role);
            assert_eq!(plan::STOREY_HEIGHT_CM, below.rise_cm);
            assert_eq!(plan::STOREY_RISE_CM, below.rise_step_cm);
            assert!(above.role.is_built());
            // Regla de unión (VERTICALITY-ROADMAP D1): TODO lo que solapa la huella arriba es
            // construido y plano — ya no se exige que un solo espacio la contenga.
            for t in b.storeys[w.storey_below + 1]
                .spaces
                .iter()
                .filter(|t| t.rect.overlaps(&w.rect))
            {
                assert!(
                    t.role.is_built() && t.rise_cm == 0,
                    "({rx},{rz}): la boca del hueco pisa vacío o desnivel"
                );
            }
            // Y que la escalera QUEPA: los peldaños se alejan de la puerta, así que el tiro corre
            // perpendicular a su pared. Sin esto el plan puede pedir quince tiras en tres metros y
            // el relleno las construye de 21 cm de huella.
            let steps = plan::storey_steps();
            let (run, across) = if below.rise_from_side.is_multiple_of(2) {
                (below.rect.depth_cm(), below.rect.width_cm())
            } else {
                (below.rect.width_cm(), below.rect.depth_cm())
            };
            assert!(
                run / steps >= plan::MIN_TREAD_CM,
                "({rx},{rz}): {steps} tiras en {run} cm dan huellas de {} cm",
                run / steps
            );
            // Y que esté RECORTADA. Sin esta cota, el hueco se come el espacio entero que le tocó:
            // en las cuatro de referencia eso daba escaleras de hasta 12 × 15 m, o sea quince tiras
            // repartidas en quince metros — una rampa con escalones, no una escalera.
            assert!(
                across <= 900,
                "({rx},{rz}): escalera de {across} cm de ancho — el recorte no se aplicó"
            );
        }
    }
}

/// **Diez plantas (2026-08-29): lo que se sirve tiene que ser coherente, no solo lo que miden los
/// tests con su `STOREYS = 2` local.** El edificio sube "hasta donde se puede", así que pedir
/// [`plan::REGION_STOREYS`] no garantiza esas plantas; lo que sí se exige es que lo que salga pase
/// `problems()` en las 49 regiones y que subir de verdad sea la norma, no la excepción.
#[test]
fn the_served_storey_count_is_coherent_in_every_region() {
    let mut floors = std::collections::BTreeMap::<usize, usize>::new();
    for rz in -3..4 {
        for rx in -3..4 {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let bounds = region.bounds();
            let gates = junction::gates_of_region(composer_seed(SERVED_SEED), rx, rz, bounds);
            let b = plan::plan_building(
                region.composer_seed(SERVED_SEED),
                bounds,
                &gates,
                plan::REGION_STOREYS,
            );
            let problems = b.problems();
            assert!(
                problems.is_empty(),
                "el edificio servido de ({rx},{rz}) no es coherente: {}",
                problems.join("; ")
            );
            *floors.entry(b.storeys.len()).or_default() += 1;
        }
    }
    println!("[wg3] plantas servidas por región: {floors:?}");
    let multi: usize = floors.iter().filter(|(k, _)| **k > 1).map(|(_, v)| v).sum();
    assert!(
        multi * 2 >= 49,
        "sólo {multi} de 49 regiones servidas levantaron más de una planta"
    );
}

/// **Sonda de coste a diez plantas.** Imprime tiempo de plan y de plan+fill por región y el reparto
/// de plantas levantadas, para decidir con números y no con miedo.
#[test]
#[ignore = "sonda: imprime, no exige"]
fn probe_ten_storeys_cost() {
    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let bounds = region.bounds();
        let gates = junction::gates_of_region(composer_seed(SERVED_SEED), rx, rz, bounds);
        let t0 = std::time::Instant::now();
        let b = plan::plan_building(
            region.composer_seed(SERVED_SEED),
            bounds,
            &gates,
            plan::REGION_STOREYS,
        );
        let t_plan = t0.elapsed();
        let t1 = std::time::Instant::now();
        let filled = fill::fill_building(&b, &m);
        let t_fill = t1.elapsed();
        let top = b.storeys.last().unwrap();
        let mut sizes: Vec<(i32, i32, &str)> = top
            .spaces
            .iter()
            .filter(|s| s.role.is_built() && !s.role.is_circulation() && s.rise_cm == 0)
            .map(|s| {
                let (w, d) = (s.rect.width_cm(), s.rect.depth_cm());
                (w.max(d), w.min(d), s.role.name())
            })
            .collect();
        sizes.sort_unstable_by_key(|&(l, ..)| std::cmp::Reverse(l));
        println!(
            "[wg3] ({rx},{rz}) última planta, mayores elegibles: {:?}",
            &sizes[..sizes.len().min(6)]
        );
        println!(
            "[wg3] ({rx},{rz}) plantas {} pozos {} cajas {} — plan {:?} fill {:?}",
            b.storeys.len(),
            b.wells.len(),
            filled.solids.len(),
            t_plan,
            t_fill,
        );
    }
}

#[test]
#[ignore = "sonda: imprime, no exige"]
fn probe_the_well_column() {
    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let b = building_of(rx, rz);
        let Some(w) = b.wells.first() else {
            println!("[wg3] ({rx},{rz}) SIN hueco: se queda en una planta");
            continue;
        };
        let stair = b.storeys[b.ground].spaces[w.space_below];
        println!(
            "[wg3] ({rx},{rz}) escalera {:?} sube {} paso {} entra por {}",
            stair.rect, stair.rise_cm, stair.rise_step_cm, stair.rise_from_side
        );

        let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);
        // Un corte a lo largo del tiro: la cota pisable de cada celda, y el hueco donde se rompe.
        let along_x = !stair.rise_from_side.is_multiple_of(2);
        let (cx, cz) = stair.rect.centre_m();
        let (lo, hi) = if along_x {
            (stair.rect.min_x_cm, stair.rect.max_x_cm)
        } else {
            (stair.rect.min_z_cm, stair.rect.max_z_cm)
        };
        let mut t = lo as f32 / 100.0 - 1.5;
        while t < hi as f32 / 100.0 + 1.5 {
            let (x, z) = if along_x { (t, cz) } else { (cx, t) };
            let coord = chunk::Wg3ChunkCoord::containing(x, z);
            let r = chunk::build_chunk_raster_full(
                &m,
                &served.placements_touching_chunk(&m, coord),
                &served.segments_touching_chunk(coord),
                &served.carves_touching_chunk(coord),
                &served.solids_touching_chunk(coord),
                coord,
            );
            let col = r.column_at(x, z);
            let walk: Vec<i16> = col
                .iter()
                .enumerate()
                .filter(|(i, s)| {
                    let head = match col.get(i + 1) {
                        Some(n) => (n.bottom_cm - s.top_cm) as f32 / 100.0,
                        None => f32::MAX,
                    };
                    (1.0..=CEILING_CAP_M).contains(&head)
                })
                .map(|(_, s)| s.top_cm)
                .collect();
            println!("[wg3]   t={t:.2}: pisables {walk:?} de {col:?}");
            t += 0.5;
        }
    }
}

/// **Dos superficies horizontales no pueden mirar hacia el mismo lado desde la misma cota.**
///
/// Salió de una partida: con dos plantas había artefactos por todas partes. La causa era aritmética
/// —contar UNA losa entre plantas cuando el generador emite dos, el techo de abajo y el suelo de
/// arriba— y las dos caían en `[320, 332]`.
///
/// **No son la MISMA caja, y por eso el primer test que escribí para esto pasó en verde.** Buscaba
/// cajas idénticas; lo que hay son cajas distintas —los rectángulos de las dos plantas no coinciden—
/// con las CARAS superpuestas, que es justamente lo que z-fightea. Un test que mide lo que no es el
/// fallo da la misma tranquilidad que uno que mide bien y no vale nada.
///
/// Espalda contra espalda sí vale, y es el caso normal: el techo de abajo acaba donde empieza el suelo
/// de arriba, una cara mira arriba y la otra abajo, y las dos quedan tapadas. Lo que no vale es dos
/// caras a la misma cota mirando a la misma parte, porque se ven las dos.
#[test]
fn no_two_horizontal_faces_fight_for_the_same_plane() {
    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);
        let boxes: Vec<PlacedBox> = served
            .segments()
            .iter()
            .flat_map(segment::segment_boxes)
            .collect();

        // Cada caja aporta dos caras horizontales. Se agrupan por (cota al centímetro, hacia dónde
        // miran) y sólo se comparan las del mismo grupo: todas contra todas serían millones.
        let mut planes: std::collections::HashMap<(i64, bool), Vec<usize>> = Default::default();
        for (i, b) in boxes.iter().enumerate() {
            let (lo, hi) = (b.center[1] - b.size[1] * 0.5, b.center[1] + b.size[1] * 0.5);
            planes
                .entry(((lo * 100.0).round() as i64, false))
                .or_default()
                .push(i);
            planes
                .entry(((hi * 100.0).round() as i64, true))
                .or_default()
                .push(i);
        }

        let mut dupes = 0usize;
        let mut worst = 0.0f32;
        let mut first: Option<(f32, f32, f32)> = None;
        for ((y_cm, _), group) in &planes {
            for (n, &i) in group.iter().enumerate() {
                for &j in &group[n + 1..] {
                    let (a, b) = (&boxes[i], &boxes[j]);
                    let ox = (a.center[0] + a.size[0] * 0.5).min(b.center[0] + b.size[0] * 0.5)
                        - (a.center[0] - a.size[0] * 0.5).max(b.center[0] - b.size[0] * 0.5);
                    let oz = (a.center[2] + a.size[2] * 0.5).min(b.center[2] + b.size[2] * 0.5)
                        - (a.center[2] - a.size[2] * 0.5).max(b.center[2] - b.size[2] * 0.5);
                    // Tocarse no cuenta: los espacios teselan, así que dos rectángulos vecinos
                    // comparten borde y ahí el solape es cero.
                    if ox * oz <= 0.5 || ox <= 0.01 || oz <= 0.01 {
                        continue;
                    }
                    dupes += 1;
                    if ox * oz > worst {
                        worst = ox * oz;
                        first = Some((a.center[0], *y_cm as f32 / 100.0, a.center[2]));
                    }
                }
            }
        }
        assert_eq!(
            0,
            dupes,
            "({rx},{rz}): {dupes} pares de caras peleando por el mismo plano de {} cajas — la mayor \
             de {worst:.1} m² en {first:?}",
            boxes.len()
        );
    }
}

/// **ADR-102 verificación (b): SE SUBE.**
///
/// Se inunda el ráster del mundo SERVIDO desde un suelo de la planta baja, con clave `(celda, nivel)`
/// y con el escalón que de verdad sube el jugador —[`plan::MAX_WALK_STEP_CM`], medido contra el
/// `m_StepOffset` del prefab—, y se exige llegar a un suelo a la cota de la planta de arriba.
///
/// Es el test que caza los dos fallos que ningún contador ve: el forjado sin perforar —la escalera
/// llega y se da con el suelo de encima en la cabeza— y la escalera construida sin que nada la
/// conecte. En los dos casos el plan es coherente, el relleno no se queja y la geometría existe.
#[test]
fn a_second_storey_is_actually_reachable() {
    const CELL: f32 = 0.5;
    const HEAD_M: f32 = 1.0;
    let max_step = plan::MAX_WALK_STEP_CM as f32 / 100.0;

    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, _, _) = region.bounds();
        let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);

        let side = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        let mut rasters = Vec::with_capacity(side * side);
        for cz in 0..side {
            for cx in 0..side {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                rasters.push(chunk::build_chunk_raster_full(
                    &m,
                    &served.placements_touching_chunk(&m, coord),
                    &served.segments_touching_chunk(coord),
                    &served.carves_touching_chunk(coord),
                    &served.solids_touching_chunk(coord),
                    coord,
                ));
            }
        }

        let cells = (REGION_M / CELL) as usize;
        let mut floors: Vec<Vec<f32>> = vec![Vec::new(); cells * cells];
        for iz in 0..cells {
            for ix in 0..cells {
                let x = min_x + ix as f32 * CELL + CELL * 0.5;
                let z = min_z + iz as f32 * CELL + CELL * 0.5;
                let coord = chunk::Wg3ChunkCoord::containing(x, z);
                let (dx, dz) = (coord.x - base.x, coord.z - base.z);
                if dx < 0 || dz < 0 || dx as usize >= side || dz as usize >= side {
                    continue;
                }
                let Some(r) = rasters.get(dz as usize * side + dx as usize) else {
                    continue;
                };
                let column = r.column_at(x, z);
                let mut out = Vec::new();
                for (i, span) in column.iter().enumerate() {
                    let head = match column.get(i + 1) {
                        Some(next) => (next.bottom_cm - span.top_cm) as f32 / 100.0,
                        None => f32::MAX,
                    };
                    if (HEAD_M..=CEILING_CAP_M).contains(&head) {
                        out.push(span.top_cm as f32 / 100.0);
                    }
                }
                floors[iz * cells + ix] = out;
            }
        }

        // **Se etiquetan TODAS las manchas y se mira la que más planta baja tiene.**
        //
        // Y no se inunda desde una celda cualquiera, que fue el primer intento: la planta baja tiene
        // manchas sueltas, así que salir de un rincón mide en qué rincón se cayó la sonda y no si se
        // sube. La pregunta correcta es si desde el sitio DONDE ESTÁ EL MUNDO se llega arriba.
        let upper = plan::STOREY_HEIGHT_CM as f32 / 100.0;
        let mut blob: Vec<Vec<i32>> = floors.iter().map(|l| vec![-1; l.len()]).collect();
        let mut ground = Vec::new();
        let mut above = Vec::new();
        let mut size = Vec::new();
        for c0 in 0..cells * cells {
            for l0 in 0..floors[c0].len() {
                if blob[c0][l0] >= 0 {
                    continue;
                }
                let id = ground.len() as i32;
                ground.push(0usize);
                above.push(0usize);
                size.push(0usize);
                blob[c0][l0] = id;
                let mut q = std::collections::VecDeque::new();
                q.push_back((c0 % cells, c0 / cells, l0));
                while let Some((ix, iz, li)) = q.pop_front() {
                    let here = floors[iz * cells + ix][li];
                    size[id as usize] += 1;
                    if here.abs() < 0.05 {
                        ground[id as usize] += 1;
                    } else if (here - upper).abs() < 0.05 {
                        above[id as usize] += 1;
                    }
                    for (dx, dz) in [(1i32, 0i32), (-1, 0), (0, 1), (0, -1)] {
                        let (nx, nz) = (ix as i32 + dx, iz as i32 + dz);
                        if nx < 0 || nz < 0 || nx as usize >= cells || nz as usize >= cells {
                            continue;
                        }
                        let (nx, nz) = (nx as usize, nz as usize);
                        for (nl, there) in floors[nz * cells + nx].iter().enumerate() {
                            if blob[nz * cells + nx][nl] >= 0 || (there - here).abs() > max_step {
                                continue;
                            }
                            blob[nz * cells + nx][nl] = id;
                            q.push_back((nx, nz, nl));
                        }
                    }
                }
            }
        }

        let best = (0..ground.len())
            .max_by_key(|&i| ground[i])
            .expect("ni una mancha");
        let total_above: usize = above.iter().sum();
        let total: usize = size.iter().sum();
        let mut top = size.clone();
        top.sort_unstable_by(|a, b| b.cmp(a));
        top.truncate(4);
        println!(
            "[wg3] ({rx},{rz}): mancha mayor {} celdas de planta baja y {} de la alta, de {} altas \
             en total ({:.0} %) | mayores {top:?} de {total} pisables",
            ground[best],
            above[best],
            total_above,
            above[best] as f32 * 100.0 / total_above.max(1) as f32
        );
        // Un puñado de celdas sería el rellano y nada más. Se pide media planta de verdad.
        assert!(
            above[best] > 2000,
            "({rx},{rz}): desde la mancha mayor de la planta baja ({} celdas) sólo se andan {} de la \
             de arriba — o el forjado no está perforado, o la escalera no conecta",
            ground[best],
            above[best]
        );
    }
}

/// SONDA — **qué papel sirve cada chunk, y cuántos de ellos ve un cliente con radio 1.**
///
/// Sale de una sesión real: nueve chunks montados, 504 tramos, y **ni uno solo de escalera**. La
/// pregunta que contesta es si eso es un fallo de emisión o el tamaño de la ventana — nueve chunks
/// son 150 × 150 m, exactamente una región, pero centrados en el jugador reparten esa ventana entre
/// CUATRO regiones y de cada una se ve un cuarto.
#[test]
#[ignore]
fn probe_which_roles_a_client_actually_sees() {
    let m = real_manifest();
    // La semilla de la sesión en vivo (`Wg3LiveBootstrap`), no la de los tests: el reparto de
    // papeles depende de la semilla y comparar dos mundos distintos no contesta nada.
    const LIVE_SEED: u64 = 42;

    let mut worlds: std::collections::HashMap<Wg3RegionCoord, _> = std::collections::HashMap::new();
    let mut seen: std::collections::BTreeMap<u8, usize> = std::collections::BTreeMap::new();
    let mut per_region: std::collections::BTreeMap<(i32, i32), usize> =
        std::collections::BTreeMap::new();

    // EXACTAMENTE los doce chunks que montó el cliente en la sesión medida. Comparar contra otro
    // conjunto no contesta nada: dos ventanas distintas dan repartos distintos.
    for cz in -2..=1 {
        for cx in -1..=1 {
            let coord = chunk::Wg3ChunkCoord { x: cx, z: cz };
            let region = Wg3RegionCoord::of_chunk(coord);
            let world = worlds
                .entry(region)
                .or_insert_with(|| Wg3ServedWorld::plan_region(&m, LIVE_SEED, region));
            let segs = world.segments_for_chunk(coord);
            let stairs = segs.iter().filter(|s| s.style == 6).count();
            println!(
                "[wg3] chunk ({cx},{cz}): {} tramos, {stairs} de escalera",
                segs.len()
            );
            for s in segs {
                *seen.entry(s.style).or_default() += 1;
            }
        }
    }

    // Y el mundo COMPLETO de cada una de esas cuatro regiones, para separar «no se emite» de «no
    // cae dentro de la ventana».
    for (region, world) in &worlds {
        let stairs = world.segments().iter().filter(|s| s.style == 6).count();
        per_region.insert((region.x, region.z), stairs);
    }

    println!("[wg3] semilla {LIVE_SEED}, radio 1 (9 chunks): papeles vistos {seen:?}");
    println!("[wg3] tramos de ESCALERA por región completa: {per_region:?}");
}

/// **La escalera se tiene que poder VESTIR distinta.** El byte `style` es lo único que el cliente
/// recibe para saber qué papel juega un espacio, y `SpaceRole::Stair` caía en el `_ => 0` de una
/// oficina: el único sitio del mundo del que se sale por arriba llegaba al cliente indistinguible de
/// una sala cualquiera, y no había forma de encontrarlo salvo tropezándose.
///
/// El fallo no da rojo en ningún otro test —un mundo con las escaleras de color oficina se anda
/// igual de bien—, así que se mide aquí: sobre los tramos SERVIDOS, no sobre la tabla.
#[test]
fn a_stair_reaches_the_client_dressed_as_a_stair() {
    let m = real_manifest();
    let mut seen = 0usize;
    for (rx, rz) in AUDIT_REGIONS {
        let b = building_of(rx, rz);
        for w in &b.wells {
            let filled = fill::fill_with(&b.storeys[w.storey_below], &m, false);
            for s in filled.segments.iter().filter(|s| {
                let (cx, cz) = (s.x_cm + s.size_x_cm / 2, s.z_cm + s.size_z_cm / 2);
                w.rect.contains_point(cx, cz)
            }) {
                assert_eq!(
                    s.style, 6,
                    "({rx},{rz}): un tramo del hueco de escalera viaja con style {} — el cliente no \
                     puede distinguirlo de una oficina",
                    s.style
                );
                seen += 1;
            }
        }
    }
    assert!(
        seen > 0,
        "ninguna región de referencia sirvió un tramo de escalera"
    );
}

/// **La escalera tiene que LLEGAR.** La última tira a la cota del suelo de arriba, ni un centímetro
/// menos, y medido sobre la geometría y no sobre el plan.
///
/// Es el fallo que este test existe para impedir y que estuvo puesto desde el principio: repartiendo
/// la subida entre las tiras en vez de entre las contrahuellas, la última se quedaba corta. Con los
/// 60 cm de una terraza el error eran 12 y no se notaba; con una planta entera son 26, que es justo
/// por debajo de lo que el jugador sube sin saltar — o sea que se subía igual y nadie se enteraba
/// hasta cambiar la altura de planta.
#[test]
fn the_storey_stair_reaches_the_floor_above() {
    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        let b = building_of(rx, rz);
        let filled = fill::fill_with(&b.storeys[b.ground], &m, false);
        // Sólo los pozos que arrancan en la CALLE: es la única planta rellenada aquí.
        for w in b.wells.iter().filter(|w| w.storey_below == b.ground) {
            let stair = &b.storeys[w.storey_below].spaces[w.space_below];
            let top = filled
                .segments
                .iter()
                .filter(|s| {
                    let (cx, cz) = (s.x_cm + s.size_x_cm / 2, s.z_cm + s.size_z_cm / 2);
                    w.rect.contains_point(cx, cz)
                })
                .map(|s| s.floor_y_cm)
                .max();
            // A UNA CONTRAHUELLA del suelo de arriba, no A la cota: el rellano no se construye —es el
            // suelo de la planta a la que se llega— así que el último peldaño que sí existe queda un
            // escalón por debajo. Lo que hay que exigir es que ese escalón se suba.
            let want = stair.floor_y_cm + plan::STOREY_HEIGHT_CM;
            let top = top.expect("la escalera no emitió un solo tramo");
            assert!(
                top < want && want - top <= plan::MAX_WALK_STEP_CM,
                "({rx},{rz}): último peldaño en {top} y suelo de arriba en {want} — {} cm, y el \
                 jugador sube {}",
                want - top,
                plan::MAX_WALK_STEP_CM
            );
        }
    }
}

/// El recorte parte un espacio en tres, y partir mal deja un agujero o un solape. El solape lo caza
/// `problems()`; el AGUJERO no lo caza nadie, porque un plan con un trozo sin asignar es coherente —
/// simplemente no se construye ahí, y el síntoma es una sala sin suelo.
#[test]
fn cutting_the_stair_out_of_a_room_loses_no_floor() {
    for (rx, rz) in AUDIT_REGIONS {
        let b = building_of(rx, rz);
        for (n, plan) in b.storeys.iter().enumerate() {
            let region = plan.bounds_cm.expect("la planta tiene caja");
            // **Por la HUELLA, no por la envolvente** (ADR-120 D1), y así el test dice más que
            // antes: desde que dos hojas se entrelazan sus envolventes se pisan a propósito, pero la
            // suma de las HUELLAS tiene que seguir siendo exactamente la región. Es la comprobación
            // que caza cualquier error de la transferencia de un plumazo — si un mordisco se pierde
            // o se duplica un centímetro cuadrado, sale aquí.
            let sum: f64 = plan.spaces.iter().map(|s| s.area_m2() as f64).sum();
            let want = region.area_m2() as f64;
            assert!(
                (sum - want).abs() < 1.0,
                "({rx},{rz}) planta {n}: los espacios suman {sum:.1} m² y la huella mide {want:.1}"
            );
        }
    }
}

/// Las plantas altas tienen que ser DISTINTAS de la baja. Un edificio cuyas plantas son fotocopias
/// no se lee como un edificio, y es exactamente lo que sale si la semilla no cambia con la planta.
#[test]
fn an_upper_storey_is_not_a_photocopy_of_the_one_below() {
    for (rx, rz) in AUDIT_REGIONS {
        let b = building_of(rx, rz);
        let (lo, hi) = (&b.storeys[b.ground], &b.storeys[b.ground + 1]);
        // Comparadas por huella y papel, ignorando la cota: si el único cambio fuera la Y, esto sería
        // la misma planta a otra altura.
        let same = hi.spaces.iter().filter(|h| {
            lo.spaces
                .iter()
                .any(|l| l.rect == h.rect && l.role == h.role)
        });
        assert!(
            same.count() * 2 < hi.spaces.len(),
            "({rx},{rz}): más de la mitad de la planta alta es calcada de la baja"
        );
    }
}

#[test]
fn the_building_is_deterministic() {
    for (rx, rz) in AUDIT_REGIONS {
        assert_eq!(
            building_of(rx, rz),
            building_of(rx, rz),
            "el edificio de ({rx},{rz}) cambia entre dos llamadas iguales"
        );
    }
}

#[test]
#[ignore = "sonda: imprime, no exige"]
fn probe_region_buildings() {
    for (rx, rz) in AUDIT_REGIONS {
        let b = building_of(rx, rz);
        for (n, plan) in b.storeys.iter().enumerate() {
            let r = plan.bounds_cm.expect("la planta tiene caja");
            println!(
                "[wg3] ({rx},{rz}) planta {n}: {} espacios, {:.0} m² construidos de {:.0} de huella \
                 ({:.0} %), {} componentes, cota {} cm",
                plan.spaces.len(),
                plan.built_area_m2(),
                r.area_m2(),
                plan.built_area_m2() * 100.0 / r.area_m2(),
                plan.components(),
                n as i32 * plan::STOREY_HEIGHT_CM,
            );
        }
        for w in &b.wells {
            let s = &b.storeys[w.storey_below].spaces[w.space_below];
            println!(
                "[wg3]   hueco: planta {} espacio {} ({:.1}×{:.1} m, entra por el lado {}) sale al \
                 espacio {} de la planta {}",
                w.storey_below,
                w.space_below,
                s.rect.width_cm() as f32 / 100.0,
                s.rect.depth_cm() as f32 / 100.0,
                s.rise_from_side,
                w.space_above,
                w.storey_below + 1,
            );
        }
    }
}

/// **El invariante que sustituye a «cuántas islas quedaron».**
///
/// Antes la conectividad era un resultado que se medía después de construir; ahora es una propiedad
/// del PLAN, y se puede exigir. Un plan en dos trozos no se arregla enrutando: está mal decidido.
#[test]
fn the_plan_is_one_building() {
    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        let p = plan_of(&m, rx, rz);
        assert_eq!(
            p.components(),
            1,
            "el plan de ({rx},{rz}) sale en {} trozos — un plan en islas es un plan mal decidido, \
             no un problema del enrutador",
            p.components()
        );
    }
}

/// **NINGUNA PUERTA DE JUNTA PUEDE PERDERSE EN EL PLAN.**
///
/// Una puerta de junta ya está acordada con la región vecina, que se compone por su cuenta y va a
/// abrir la suya en el mismo punto. Si aquí no hay espacio que la abra, **la región queda sellada por
/// ese lado** y el jugador se encuentra una pared donde el mapa promete un paso. Es lo que se sintió
/// andando —«hay sitios que están cerrados»— y el aviso estaba en el log del backend.
///
/// **Este test no existía y el que había no valía**: `the_fill_honours_every_planned_doorway` mira
/// `gates_failed`, que cuenta las que llegan al RELLENO. Una puerta que el plan descarta nunca llega,
/// así que el contador se quedaba a cero mientras la puerta desaparecía. Un cero que sólo cuenta lo
/// que sobrevivió no dice nada de lo que se perdió.
#[test]
fn every_junction_gate_reaches_the_plan() {
    // Sin manifiesto a propósito: el plan no mira el catálogo, así que una puerta perdida no puede
    // achacarse a las piezas. Es el reparto quien la pierde o no.
    let seed = composer_seed(SERVED_SEED);
    let mut asked = 0usize;
    let mut lost: Vec<(i32, i32, f32, f32)> = Vec::new();

    // Un barrido ancho, no las cuatro de siempre: una puerta se pierde cuando cae justo sobre un
    // corte de la subdivisión, y eso depende de la semilla de cada región.
    for rz in -3..=3 {
        for rx in -3..=3 {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let bounds = region.bounds();
            let gates = junction::gates_of_region(seed, rx, rz, bounds);
            asked += gates.len();
            let p = plan::plan_region(region.composer_seed(SERVED_SEED), bounds, &gates);
            for g in &gates {
                let placed = p.gates.iter().any(|pg| {
                    (pg.x_cm - (g.x * 100.0).round() as i32).abs() <= 2
                        && (pg.z_cm - (g.z * 100.0).round() as i32).abs() <= 2
                });
                if !placed {
                    lost.push((rx, rz, g.x, g.z));
                }
            }
        }
    }

    assert!(
        lost.is_empty(),
        "{} de {asked} puertas de junta no encontraron espacio que las abriera. La región queda \
         SELLADA por ese lado y la vecina abre la suya contra un muro. Primeras: {:?}",
        lost.len(),
        &lost[..lost.len().min(8)]
    );
}

/// Determinismo (R3): el mismo sitio planifica el mismo edificio, siempre.
#[test]
fn the_plan_is_deterministic() {
    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        assert_eq!(
            plan_of(&m, rx, rz),
            plan_of(&m, rx, rz),
            "el plan de ({rx},{rz}) cambia entre dos llamadas"
        );
    }
}

/// **JERARQUÍA, comprobada y no prometida.**
///
/// Un reparto puede tener rectángulos de tamaños distintos y seguir siendo una cuadrícula. Lo que lo
/// separa de un edificio es que haya NIVELES: una espina, corredores que cuelgan de ella y salas que
/// cuelgan de ellos. Este test lo exige como estructura, no como aspecto.
#[test]
fn the_plan_has_a_hierarchy() {
    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        let p = plan_of(&m, rx, rz);

        let spines = p
            .spaces
            .iter()
            .filter(|s| s.role == SpaceRole::Spine)
            .count();
        assert_eq!(
            spines, 1,
            "({rx},{rz}) tiene {spines} espinas — el corte de nivel 0 es uno y sólo uno"
        );

        let depths: Vec<u8> = p.spaces.iter().map(|s| s.depth).collect();
        let spread = depths.iter().max().unwrap() - depths.iter().min().unwrap();
        assert!(
            spread >= 3,
            "({rx},{rz}) reparte todo a la misma profundidad (rango {spread}): eso es una \
             cuadrícula, no una jerarquía"
        );

        // Y las salas tienen que ser MAYORÍA sobre los corredores. Un plano donde manda la
        // circulación es el mundo de antes —«todo es pasillos»— con otro generador debajo.
        let circulation = p.spaces.iter().filter(|s| s.role.is_circulation()).count();
        let rooms = p.spaces.len() - circulation;
        assert!(
            rooms > circulation,
            "({rx},{rz}): {circulation} espacios de circulación contra {rooms} salas — vuelve a ser \
             «todo es pasillos»"
        );
    }
}

/// El vacío tiene que ser una DECISIÓN y no el terreno que sobró: poco, y marcado.
#[test]
fn the_void_is_deliberate_and_bounded() {
    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        let p = plan_of(&m, rx, rz);
        let region_m2 = REGION_M * REGION_M;
        let built = p.built_area_m2();
        let ratio = built / region_m2;
        assert!(
            ratio > 0.60,
            "({rx},{rz}) planifica sólo el {:.0} % de la región — el vacío ha dejado de ser una \
             decisión y ha vuelto a ser lo que sobra",
            ratio * 100.0
        );
        // Y no el 100 %: un edificio sin patios ni zonas muertas no es un Backrooms, es un almacén.
        assert!(
            ratio < 0.995,
            "({rx},{rz}) planifica el {:.1} % — no queda un solo hueco intencionado",
            ratio * 100.0
        );
    }
}

/// **LA SONDA DEL PLAN.** Las diez métricas, antes de que exista una sola pieza.
///
/// No afirma nada a propósito: es la que dice si el reparto se parece a un edificio, y eso se lee,
/// no se asevera. Los invariantes que sí se pueden exigir están en los tests de arriba.
#[test]
fn probe_region_plan() {
    let m = real_manifest();
    let region_m2 = REGION_M * REGION_M;

    for (rx, rz) in AUDIT_REGIONS {
        let p = plan_of(&m, rx, rz);
        let degree = p.degree();

        let built: Vec<usize> = p.built().map(|(i, _)| i).collect();
        let built_area = p.built_area_m2();
        let planned_total: f32 = p.spaces.iter().map(|s| s.rect.area_m2()).sum();

        let max_depth = p.spaces.iter().map(|s| s.depth).max().unwrap_or(0);
        let min_depth = p.spaces.iter().map(|s| s.depth).min().unwrap_or(0);

        // Longitud de un enlace: entre los centros de los dos espacios que une. Es lo que dice si el
        // grafo es de vecinos o de saltos largos — un plan lleno de enlaces de 40 m no es un
        // edificio, es el mundo de antes dibujado de otra forma.
        let mut lengths: Vec<f32> = Vec::with_capacity(p.links.len());
        for l in &p.links {
            let (ax, az) = p.spaces[l.a].rect.centre_m();
            let (bx, bz) = p.spaces[l.b].rect.centre_m();
            lengths.push(((ax - bx).powi(2) + (az - bz).powi(2)).sqrt());
        }
        let mean_len = if lengths.is_empty() {
            0.0
        } else {
            lengths.iter().sum::<f32>() / lengths.len() as f32
        };

        let orphan = built.iter().filter(|&&i| degree[i] == 0).count();
        let to_route = p.links.iter().filter(|l| l.kind == LinkKind::Route).count();
        let impossible = p.problems().len();

        let mut roles: Vec<(&str, usize, f32)> = Vec::new();
        for role in [
            SpaceRole::Spine,
            SpaceRole::Corridor,
            SpaceRole::Hall,
            SpaceRole::Office,
            SpaceRole::Service,
            SpaceRole::Storage,
            SpaceRole::DeadEnd,
            SpaceRole::Stair,
            SpaceRole::Void,
        ] {
            let sel: Vec<&plan::PlannedSpace> =
                p.spaces.iter().filter(|s| s.role == role).collect();
            if sel.is_empty() {
                continue;
            }
            let area: f32 = sel.iter().map(|s| s.rect.area_m2()).sum();
            roles.push((role.name(), sel.len(), area));
        }

        let areas: Vec<f32> = p
            .built()
            .filter(|(_, s)| !s.role.is_circulation())
            .map(|(_, s)| s.rect.area_m2())
            .collect();
        let (amin, amax) = areas
            .iter()
            .fold((f32::MAX, 0.0f32), |(lo, hi), a| (lo.min(*a), hi.max(*a)));

        println!(
            "[plan] región ({rx},{rz}): {} espacios ({} construidos), {} enlaces | \
             planificado {planned_total:.0} m², construido {built_area:.0} m² = {:.0} % de la región \
             | {} componentes | jerarquía {min_depth}..{max_depth}",
            p.spaces.len(),
            built.len(),
            p.links.len(),
            built_area / region_m2 * 100.0,
            p.components(),
        );
        println!(
            "[plan]   enlace medio {mean_len:.1} m | sin conexión {orphan} | pendientes de \
             enrutador {to_route} | incoherencias {impossible}"
        );
        println!(
            "[plan]   sala menor {:.0} m², mayor {:.0} m² (×{:.1})",
            amin,
            amax,
            if amin > 0.0 { amax / amin } else { 0.0 }
        );

        // Histograma de tamaños. **Es lo que distingue «variedad» de «todo grande»**, y sin él un
        // mínimo y un máximo lejanos se leen como variedad cuando puede que el 90 % esté arriba.
        let cuts = [50.0f32, 100.0, 200.0, 400.0, 800.0];
        let mut bins = vec![0usize; cuts.len() + 1];
        for a in &areas {
            let b = cuts.iter().position(|c| a < c).unwrap_or(cuts.len());
            bins[b] += 1;
        }
        println!(
            "[plan]   tamaños: <50 {} | 50-100 {} | 100-200 {} | 200-400 {} | 400-800 {} | >800 {}",
            bins[0], bins[1], bins[2], bins[3], bins[4], bins[5]
        );
        // Y por clase de escala, que es lo que decide el área objetivo: si una región es toda
        // `Large`, salir toda de naves es correcto y no un fallo del reparto.
        let mut by_scale = [0usize; 4];
        for (_, s) in p.built() {
            by_scale[(s.scale as usize).min(3)] += 1;
        }
        println!(
            "[plan]   campo de escala: estrecha {} | media {} | grande {} | rara {}",
            by_scale[0], by_scale[1], by_scale[2], by_scale[3]
        );

        // **PROPORCIÓN, y esta métrica nació de un fallo que ningún número veía.** Con el eje del
        // corte invertido, el reparto salía en lonchas de 5 × 30 m: área correcta, tamaños variados,
        // conectividad perfecta, y un plano que no se parecía a un edificio. Lo cazó el volcado. Ya
        // no hace falta mirarlo para cazarlo otra vez.
        let mut aspects: Vec<f32> = p
            .built()
            .filter(|(_, s)| !s.role.is_circulation())
            .map(|(_, s)| {
                let w = s.rect.width_cm().max(s.rect.depth_cm()) as f32;
                let d = s.rect.width_cm().min(s.rect.depth_cm()).max(1) as f32;
                w / d
            })
            .collect();
        aspects.sort_by(f32::total_cmp);
        let mean_aspect = aspects.iter().sum::<f32>() / aspects.len().max(1) as f32;
        let p90 = aspects[(aspects.len() * 9 / 10).min(aspects.len().saturating_sub(1))];
        println!(
            "[plan]   proporción de sala: media {mean_aspect:.2}:1, p90 {p90:.2}:1, peor {:.2}:1",
            aspects.last().copied().unwrap_or(0.0)
        );
        let reparto: Vec<String> = roles
            .iter()
            .map(|(n, c, a)| format!("{n} {c} ({a:.0} m²)"))
            .collect();
        println!("[plan]   papeles: {}", reparto.join(", "));

        let mut kinds = [0usize; 4];
        for l in &p.links {
            kinds[match l.kind {
                LinkKind::Doorway => 0,
                LinkKind::Access => 1,
                LinkKind::Junction => 2,
                LinkKind::Route => 3,
            }] += 1;
        }
        println!(
            "[plan]   enlaces: {} vano entre salas, {} acceso a corredor, {} cruce, {} por enrutar",
            kinds[0], kinds[1], kinds[2], kinds[3]
        );
    }
}

// ── ADR-100 paso 2: el relleno ──────────────────────────────────────────────────────────────

use super::fill;

/// **Ni un solo tramo emitido puede ser inválido.**
///
/// `Wg3Segment::problems` es lo que separa geometría que se anda de geometría que se dibuja abierta y
/// bloquea: una boca por debajo del mínimo la tapia el ráster conservador, y el síntoma no se ve en
/// una captura. Aquí se exige sobre TODO lo que sale de rellenar las cuatro regiones.
#[test]
fn every_filled_segment_is_valid() {
    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        let p = plan_of(&m, rx, rz);
        let f = fill::fill(&p, &m);
        for (i, s) in f.segments.iter().enumerate() {
            let problems = s.problems();
            assert!(
                problems.is_empty(),
                "({rx},{rz}) tramo {i} en ({},{}) cm: {}",
                s.x_cm,
                s.z_cm,
                problems.join("; ")
            );
        }
        assert_eq!(
            f.spaces_unbuilt, 0,
            "({rx},{rz}) dejó {} espacios sin construir",
            f.spaces_unbuilt
        );
    }
}

/// **Ningún enlace del plan puede quedarse sin cumplir en silencio.**
///
/// Un `Route` es un encargo legítimo al enrutador y no cuenta. Lo que no puede haber es un enlace que
/// el plan declaró como vano y que el relleno no supo abrir: eso es una puerta que existe en el plano
/// y no en el mundo, que es la clase de divergencia que ADR-095 vino a evitar.
/// **BARRIDO ANCHO, y no las cuatro de siempre.**
///
/// Un hueco se pierde por una coincidencia de geometría —cae a caballo de dos tramos hermanas, o en
/// una esquina— y eso depende de la semilla de cada región. Con las cuatro de referencia salía cero;
/// el log del backend de una partida real cantó **`región (-1,0): 1 huecos perdidos`**, que no estaba
/// en el barrido. El agujero no era del código: era del test.
#[test]
fn the_fill_honours_every_planned_doorway() {
    let m = real_manifest();
    let mut regions: Vec<(i32, i32)> = Vec::new();
    for rz in -3..=3 {
        for rx in -3..=3 {
            regions.push((rx, rz));
        }
    }
    for (rx, rz) in regions {
        let p = plan_of(&m, rx, rz);
        let f = fill::fill(&p, &m);
        assert!(
            f.links_failed.is_empty(),
            "({rx},{rz}) no pudo abrir {} vanos que el plan pidió: {:?}",
            f.links_failed.len(),
            &f.links_failed[..f.links_failed.len().min(5)]
        );
        assert_eq!(
            f.gates_failed, 0,
            "({rx},{rz}) dejó {} puertas de junta sin cumplir — la región vecina abre la suya y se \
             cae por ahí",
            f.gates_failed
        );
        // **Y ninguna puerta puede perderse en la tesela.** Es el fallo silencioso de este módulo:
        // los contadores cuadran, ningún enlace sale fallido, y la sala nace sellada.
        assert_eq!(
            f.openings_dropped,
            0,
            "({rx},{rz}) perdió {} huecos entre tramos hermanas — salas selladas con la puerta \
             dibujada en el plano. En {:?}, papeles {:?}",
            f.openings_dropped,
            f.openings_dropped_at,
            f.openings_dropped_at
                .iter()
                .map(|(i, ..)| (
                    p.spaces[*i].role.name(),
                    p.spaces[*i].rect.width_cm(),
                    p.spaces[*i].rect.depth_cm(),
                    p.spaces[*i].rise_cm
                ))
                .collect::<Vec<_>>()
        );
    }
}

/// El determinismo llega hasta el final: mismo plan y mismo catálogo, misma geometría.
#[test]
fn the_fill_is_deterministic() {
    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        let p = plan_of(&m, rx, rz);
        let a = fill::fill(&p, &m);
        let b = fill::fill(&p, &m);
        assert_eq!(a.segments, b.segments, "({rx},{rz}) la geometría cambia");
        assert_eq!(a.placements, b.placements, "({rx},{rz}) las piezas cambian");
    }
}

/// Un plan a mano con dos espacios que NO se tocan y un enlace `Route` entre ellos.
///
/// Existe porque las cuatro regiones de la auditoría dan **cero** enlaces por enrutar —el plan las
/// deja conexas con vanos—, y un camino de código que sólo se ejercita cuando algo va raro es un
/// camino que se descubre roto el día que hace falta.
fn plan_with_a_gap(blocked: bool) -> plan::RegionPlan {
    use plan::{LinkKind, PlanRect, PlannedLink, PlannedSpace, SpaceRole};

    let room = |x0: i32, x1: i32| {
        let rect = PlanRect {
            min_x_cm: x0,
            min_z_cm: 0,
            max_x_cm: x1,
            max_z_cm: 1200,
        };
        PlannedSpace {
            rect,
            parts: [rect; plan::MAX_PARTS],
            part_count: 1,
            floor_y_cm: 0,
            role: SpaceRole::Office,
            scale: 1,
            depth: 3,
            rise_cm: 0,
            rise_from_side: 0,
            rise_step_cm: plan::STEP_RISE_CM,
            max_clear_cm: 0,
            void_above: false,
            void_storeys_above: 0,
            atrium_storeys: 0,
            ceiling_clear_cm: 0,
            open_plan: false,
        }
    };

    let mut spaces = vec![room(0, 1200), room(3000, 4200)];
    if blocked {
        // Un tercer espacio tapando el hueco entero: no hay por dónde pasar, y el enrutador tiene
        // que DECIRLO en vez de inventarse otra conexión.
        // Por `of_rect` y no con `rect:` suelto: desde ADR-120 `rect` es la envolvente y la huella
        // vive en `parts`, así que pisar sólo el campo dejaría al espacio diciendo dos cosas.
        spaces.push(PlannedSpace::of_rect(
            PlanRect {
                min_x_cm: 1300,
                min_z_cm: -600,
                max_x_cm: 2900,
                max_z_cm: 1800,
            },
            room(1300, 2900),
        ));
    }

    plan::RegionPlan {
        spaces,
        links: vec![PlannedLink {
            a: 0,
            b: 1,
            width_cm: plan::DOORWAY_CM,
            kind: LinkKind::Route,
            at_x_cm: 2100,
            at_z_cm: 600,
        }],
        gates: Vec::new(),
        bounds_cm: Some(PlanRect {
            min_x_cm: -600,
            min_z_cm: -800,
            max_x_cm: 4800,
            max_z_cm: 2000,
        }),
    }
}

/// ADR-100 D3 — el enrutador CONSTRUYE el enlace que el plan pidió.
#[test]
fn the_router_builds_the_link_the_plan_asked_for() {
    let m = real_manifest();
    let p = plan_with_a_gap(false);
    let f = fill::fill(&p, &m);

    assert_eq!(
        f.links_to_route,
        vec![(0, 1)],
        "el enlace no llegó al enrutador"
    );
    assert!(
        f.links_failed.is_empty(),
        "el enrutador no pudo tender un enlace que tenía 18 m de hueco libre: {:?}",
        f.links_failed
    );
    // Los dos espacios más la ruta: tiene que haber geometría ENTRE ellos, no sólo dentro.
    let between = f
        .segments
        .iter()
        .filter(|s| {
            let (x0, _, x1, _) = s.bounds();
            x0 >= 11.0 && x1 <= 31.0
        })
        .count();
    assert!(between > 0, "no se tendió un solo tramo en el hueco");

    for s in &f.segments {
        assert!(
            s.problems().is_empty(),
            "tramo inválido en la ruta: {}",
            s.problems().join("; ")
        );
    }
}

/// **Y cuando NO cabe, lo dice con los dos espacios delante.**
///
/// Es la mitad que separa este enrutador del anterior. Allí un enlace que no cabía se sustituía por
/// otro que sí —un puente a la pared de un conector, un empalme a mitad de pasillo— y el mundo salía
/// conectado por sitios que nadie había decidido. Aquí no hay nada que sustituir: el plan pidió esto,
/// esto no cabe, y eso es un sitio al que ir a mirar.
#[test]
fn a_link_that_cannot_be_routed_is_named_and_not_replaced() {
    let m = real_manifest();
    let p = plan_with_a_gap(true);
    let f = fill::fill(&p, &m);

    assert_eq!(
        f.links_failed,
        vec![(0, 1)],
        "un enlace imposible tiene que salir con nombre, y salió {:?}",
        f.links_failed
    );
    // Y no se ha inventado nada: la geometría es EXACTAMENTE la misma que si el enlace no se hubiera
    // pedido. Comparar contra el plan sin enlace y no contar tramos en una caja es lo que hace la
    // afirmación exacta — el espacio que bloquea también emite geometría en ese hueco, y contarla
    // sería acusar al enrutador de algo que hizo el relleno.
    let mut without = plan_with_a_gap(true);
    without.links.clear();
    let g = fill::fill(&without, &m);
    assert_eq!(
        f.segments, g.segments,
        "el enrutador dejó geometría por un enlace que no pudo cumplir"
    );
}

/// **LA MEDIDA QUE COMPARA CON EL MUNDO DE HOY.**
///
/// Rasteriza la región rellenada igual que el servidor y recorre lo andable desde el centro. Es la
/// misma cuenta que `probe_how_much_of_the_region_can_be_walked_from_the_spawn` hace sobre el
/// compositor por bocas, así que los dos números son comparables — y ésa es toda la gracia: el 21, 26,
/// 3,5 y 24 % de la auditoría contra lo que da el plan.
#[test]
fn probe_filled_plan() {
    const CELL: f32 = 0.5;
    const HEAD_M: f32 = 1.0;
    const MAX_STEP: f32 = WALK_STEP_M;

    let m = real_manifest();
    let region_m2 = REGION_M * REGION_M;

    for (rx, rz) in AUDIT_REGIONS {
        let p = plan_of(&m, rx, rz);
        let f = fill::fill(&p, &m);
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, _, _) = region.bounds();

        // **Lo que se rasteriza es el mundo SERVIDO, no el relleno con catálogo.** Son distintos: el
        // servido lleva el catálogo apagado hasta que los vanos excavados crucen el wire, y medir uno
        // para hablar del otro es el error de método que ya costó tres conclusiones falsas aquí.
        let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);

        let side = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        let mut rasters = Vec::with_capacity(side * side);
        for cz in 0..side {
            for cx in 0..side {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                rasters.push(chunk::build_chunk_raster_full(
                    &m,
                    &served.placements_touching_chunk(&m, coord),
                    &served.segments_touching_chunk(coord),
                    &served.carves_touching_chunk(coord),
                    &served.solids_touching_chunk(coord),
                    coord,
                ));
            }
        }
        let raster_at = |x: f32, z: f32| -> Option<&Wg3Raster> {
            let coord = chunk::Wg3ChunkCoord::containing(x, z);
            let (dx, dz) = (coord.x - base.x, coord.z - base.z);
            if dx < 0 || dz < 0 || dx as usize >= side || dz as usize >= side {
                return None;
            }
            rasters.get(dz as usize * side + dx as usize)
        };

        // Suelos pisables por celda: la cara de arriba de un tramo macizo con hueco para la cabeza.
        let cells = (REGION_M / CELL) as usize;
        let mut floors: Vec<Vec<f32>> = vec![Vec::new(); cells * cells];
        for iz in 0..cells {
            for ix in 0..cells {
                let x = min_x + ix as f32 * CELL + CELL * 0.5;
                let z = min_z + iz as f32 * CELL + CELL * 0.5;
                let Some(r) = raster_at(x, z) else { continue };
                let column = r.column_at(x, z);
                let mut out = Vec::new();
                for (i, span) in column.iter().enumerate() {
                    let head = match column.get(i + 1) {
                        Some(next) => (next.bottom_cm - span.top_cm) as f32 / 100.0,
                        None => f32::MAX,
                    };
                    if (HEAD_M..=CEILING_CAP_M).contains(&head) {
                        out.push(span.top_cm as f32 / 100.0);
                    }
                }
                floors[iz * cells + ix] = out;
            }
        }

        // Manchas conexas, con el mismo escalón máximo que usa la sonda del mundo de hoy.
        let mut blob_of: Vec<Vec<i32>> = floors.iter().map(|l| vec![-1; l.len()]).collect();
        let mut sizes: Vec<usize> = Vec::new();
        for iz0 in 0..cells {
            for ix0 in 0..cells {
                for l0 in 0..floors[iz0 * cells + ix0].len() {
                    if blob_of[iz0 * cells + ix0][l0] >= 0 {
                        continue;
                    }
                    let id = sizes.len() as i32;
                    sizes.push(0);
                    blob_of[iz0 * cells + ix0][l0] = id;
                    let mut q = std::collections::VecDeque::new();
                    q.push_back((ix0, iz0, l0));
                    while let Some((ix, iz, li)) = q.pop_front() {
                        sizes[id as usize] += 1;
                        let here = floors[iz * cells + ix][li];
                        for (dx, dz) in [(1i32, 0i32), (-1, 0), (0, 1), (0, -1)] {
                            let (nx, nz) = (ix as i32 + dx, iz as i32 + dz);
                            if nx < 0 || nz < 0 || nx as usize >= cells || nz as usize >= cells {
                                continue;
                            }
                            let (nx, nz) = (nx as usize, nz as usize);
                            for (nl, there) in floors[nz * cells + nx].iter().enumerate() {
                                if blob_of[nz * cells + nx][nl] >= 0
                                    || (there - here).abs() > MAX_STEP
                                {
                                    continue;
                                }
                                blob_of[nz * cells + nx][nl] = id;
                                q.push_back((nx, nz, nl));
                            }
                        }
                    }
                }
            }
        }
        let total: usize = sizes.iter().sum();
        let biggest = sizes.iter().copied().max().unwrap_or(0);
        let walkable_m2 = biggest as f32 * CELL * CELL;

        println!(
            "[fill] región ({rx},{rz}): {} espacios ⇒ con catálogo {} por pieza y {} por tramos; \
             SERVIDO {} tramos, {} piezas, {} vanos",
            p.spaces.len(),
            f.spaces_by_piece,
            f.spaces_by_segment,
            served.segments().len(),
            served.placements().len(),
            f.openings_built,
        );
        println!(
            "[fill]   ANDABLE {walkable_m2:.0} m² = {:.0} % de la región | mancha mayor {:.0} % de \
             lo pisable | {} manchas | por enrutar {} | fallidos {}",
            walkable_m2 / region_m2 * 100.0,
            if total > 0 {
                biggest as f32 / total as f32 * 100.0
            } else {
                0.0
            },
            sizes.len(),
            f.links_to_route.len(),
            f.links_failed.len(),
        );
    }
}

/// **ADR-100 enmienda 2 — LAS ESCALERAS SE BAJAN.**
///
/// Un espacio hundido que no se pueda bajar es un agujero con una puerta: se dibuja abierto y no se
/// entra. Se comprueba sobre el ráster del mundo SERVIDO —lo que de verdad frena— buscando, para cada
/// espacio con desnivel, suelo pisable a la cota del FONDO dentro de su huella.
#[test]
fn a_sunken_space_can_actually_be_walked_down() {
    const HEAD_M: f32 = 1.0;

    let m = real_manifest();
    let mut sunken = 0usize;
    let mut unreachable: Vec<(i32, i32, i32)> = Vec::new();

    for (rx, rz) in AUDIT_REGIONS {
        let p = plan_of(&m, rx, rz);
        let region = Wg3RegionCoord { x: rx, z: rz };
        let world = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);

        for s in p.spaces.iter().filter(|s| s.rise_cm != 0) {
            sunken += 1;
            let bottom = s.floor_y_cm + s.rise_cm;

            // El fondo: el CENTRO de la franja más alejada de la puerta.
            //
            // **En el centro y no «a 60 cm de la pared», y la diferencia importa**: el ráster es
            // conservador, así que una pared de 15 cm ocupa su celda de 50 entera y hasta medio metro
            // de la pared puede salir macizo. Muestrear cerca del muro daba dos falsos negativos —
            // fallo de la sonda, no del mundo.
            let (x0, z0, x1, z1) = s.rect.bounds_m();
            let steps = (s.rise_cm.abs() / plan::STEP_RISE_CM).max(1) as f32;
            let (px, pz) = match s.rise_from_side % 4 {
                0 => ((x0 + x1) * 0.5, z0 + (z1 - z0) / steps * 0.5),
                1 => (x0 + (x1 - x0) / steps * 0.5, (z0 + z1) * 0.5),
                2 => ((x0 + x1) * 0.5, z1 - (z1 - z0) / steps * 0.5),
                _ => (x1 - (x1 - x0) / steps * 0.5, (z0 + z1) * 0.5),
            };

            let coord = chunk::Wg3ChunkCoord::containing(px, pz);
            let raster = chunk::build_chunk_raster_with_carves(
                &m,
                &world.placements_touching_chunk(&m, coord),
                &world.segments_touching_chunk(coord),
                &world.carves_touching_chunk(coord),
                coord,
            );

            // Suelo pisable a la cota del fondo, con hueco para la cabeza. La tolerancia es un
            // peldaño: el centro de la franja puede caer en la penúltima si la huella no divide justo.
            let column = raster.column_at(px, pz);
            let mut ok = false;
            for (i, span) in column.iter().enumerate() {
                let head = match column.get(i + 1) {
                    Some(next) => (next.bottom_cm - span.top_cm) as f32 / 100.0,
                    None => f32::MAX,
                };
                if head < HEAD_M {
                    continue;
                }
                if (span.top_cm as i32 - bottom).abs() <= plan::STEP_RISE_CM + 2 {
                    ok = true;
                    break;
                }
            }
            if !ok {
                unreachable.push((rx, rz, bottom));
            }
        }
    }

    assert!(
        sunken > 0,
        "no se hundió un solo espacio en cuatro regiones"
    );
    assert!(
        unreachable.is_empty(),
        "{} de {sunken} espacios hundidos no tienen suelo a la cota del fondo: se dibujan abiertos \
         y no se entra. {:?}",
        unreachable.len(),
        &unreachable[..unreachable.len().min(5)]
    );
}

/// **¿SE CRUZA DE UNA REGIÓN A OTRA ANDANDO?**
///
/// Es la pregunta que ninguna sonda hacía y la que se siente jugando: «hay partes que están
/// cerradas». Todo lo demás mide DENTRO de una región, y una región perfecta rodeada de muros es
/// exactamente lo que se describe.
///
/// Se rasteriza un bloque de 2 × 2 regiones —300 × 300 m, 36 chunks—, se inunda desde un punto
/// pisable de la de abajo a la izquierda y se mira a cuántas de las cuatro se llega.
#[test]
fn the_regions_can_be_walked_between() {
    const CELL: f32 = 0.5;
    const HEAD_M: f32 = 1.0;
    const MAX_STEP: f32 = WALK_STEP_M;

    let m = real_manifest();
    let base_region = Wg3RegionCoord { x: 0, z: 0 };
    let (min_x, min_z, _, _) = base_region.bounds();

    // Los cuatro mundos, compuestos igual que los sirve el backend.
    let mut worlds = std::collections::HashMap::new();
    for dz in 0..2 {
        for dx in 0..2 {
            let r = Wg3RegionCoord { x: dx, z: dz };
            worlds.insert(r, Wg3ServedWorld::plan_region(&m, SERVED_SEED, r));
        }
    }

    let side = (REGION_CHUNKS * 2) as usize;
    let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
    let mut rasters = Vec::with_capacity(side * side);
    for cz in 0..side {
        for cx in 0..side {
            let coord = chunk::Wg3ChunkCoord {
                x: base.x + cx as i32,
                z: base.z + cz as i32,
            };
            // CADA CHUNK SE PIDE A SU REGIÓN, igual que hace el bucle de juego. Rasterizarlo todo
            // contra una sola región daría un mundo que nadie sirve.
            let region = Wg3RegionCoord::of_chunk(coord);
            let world = &worlds[&region];
            rasters.push(chunk::build_chunk_raster_with_carves(
                &m,
                &world.placements_touching_chunk(&m, coord),
                &world.segments_touching_chunk(coord),
                &world.carves_touching_chunk(coord),
                coord,
            ));
        }
    }
    let raster_at = |x: f32, z: f32| -> Option<&Wg3Raster> {
        let coord = chunk::Wg3ChunkCoord::containing(x, z);
        let (dx, dz) = (coord.x - base.x, coord.z - base.z);
        if dx < 0 || dz < 0 || dx as usize >= side || dz as usize >= side {
            return None;
        }
        rasters.get(dz as usize * side + dx as usize)
    };

    let cells = (REGION_M * 2.0 / CELL) as usize;
    let mut floors: Vec<Vec<f32>> = vec![Vec::new(); cells * cells];
    for iz in 0..cells {
        for ix in 0..cells {
            let x = min_x + ix as f32 * CELL + CELL * 0.5;
            let z = min_z + iz as f32 * CELL + CELL * 0.5;
            let Some(r) = raster_at(x, z) else { continue };
            let column = r.column_at(x, z);
            let mut out = Vec::new();
            for (i, span) in column.iter().enumerate() {
                let head = match column.get(i + 1) {
                    Some(next) => (next.bottom_cm - span.top_cm) as f32 / 100.0,
                    None => f32::MAX,
                };
                if (HEAD_M..=CEILING_CAP_M).contains(&head) {
                    out.push(span.top_cm as f32 / 100.0);
                }
            }
            floors[iz * cells + ix] = out;
        }
    }

    // Se sale de la primera celda pisable de la región (0,0), recorriendo hacia el centro.
    let mut start = None;
    'seek: for radius in 0..(cells / 4) {
        let c = cells / 4;
        for dz in 0..=radius {
            for dx in 0..=radius {
                let (ix, iz) = (c + dx, c + dz);
                if !floors[iz * cells + ix].is_empty() {
                    start = Some((ix, iz, 0usize));
                    break 'seek;
                }
            }
        }
    }
    let Some(start) = start else {
        panic!("no hay ni una celda pisable en la región (0,0)");
    };

    let mut seen: Vec<Vec<bool>> = floors.iter().map(|l| vec![false; l.len()]).collect();
    let mut q = std::collections::VecDeque::new();
    seen[start.1 * cells + start.0][start.2] = true;
    q.push_back(start);
    let mut reached = [false; 4];
    let mut cells_seen = 0usize;
    while let Some((ix, iz, li)) = q.pop_front() {
        cells_seen += 1;
        let quadrant = usize::from(ix >= cells / 2) + 2 * usize::from(iz >= cells / 2);
        reached[quadrant] = true;
        let here = floors[iz * cells + ix][li];
        for (dx, dz) in [(1i32, 0i32), (-1, 0), (0, 1), (0, -1)] {
            let (nx, nz) = (ix as i32 + dx, iz as i32 + dz);
            if nx < 0 || nz < 0 || nx as usize >= cells || nz as usize >= cells {
                continue;
            }
            let (nx, nz) = (nx as usize, nz as usize);
            for (nl, there) in floors[nz * cells + nx].iter().enumerate() {
                if seen[nz * cells + nx][nl] || (there - here).abs() > MAX_STEP {
                    continue;
                }
                seen[nz * cells + nx][nl] = true;
                q.push_back((nx, nz, nl));
            }
        }
    }

    let names = ["(0,0)", "(1,0)", "(0,1)", "(1,1)"];
    let got: Vec<&str> = names
        .iter()
        .zip(reached)
        .filter(|(_, r)| *r)
        .map(|(n, _)| *n)
        .collect();
    println!(
        "[cruce] desde (0,0) se alcanzan {} de 4 regiones: {} | {} celdas, {:.0} m²",
        got.len(),
        got.join(" "),
        cells_seen,
        cells_seen as f32 * CELL * CELL
    );

    assert_eq!(
        got.len(),
        4,
        "sólo se llega a {:?} — las demás están SELLADAS, que es el «hay partes cerradas» de andarlo",
        got
    );
}

/// **EL VOLCADOR DEL MUNDO SERVIDO tras ADR-100: lo que de verdad se anda.**
///
/// Hermano de `dump_region_maps`, que sigue dibujando el compositor por bocas. Éste rasteriza
/// [`Wg3ServedWorld::plan_region`] —lo que responde a un chunk— y pinta en verde lo alcanzable desde
/// el centro de la región. Ponerlos uno al lado del otro es la comparación honesta.
///
/// `#[ignore]`: dibuja, no afirma. `WG3_MAP_DIR=... cargo test dump_served_maps -- --ignored`.
#[test]
#[ignore]
fn dump_served_maps() {
    const CELL: f32 = 0.5;
    const HEAD_M: f32 = 1.0;
    const MAX_STEP: f32 = WALK_STEP_M;
    const PX: f32 = 4.0;

    let dir = std::env::var("WG3_MAP_DIR").expect("WG3_MAP_DIR: carpeta donde escribir los planos");
    let m = real_manifest();
    // **Y con semilla y region a eleccion.** Un fallo del barrido sale con su semilla y su region en
    // el log, y hasta hoy no habia forma de PINTAR esa: el volcado solo sabia dibujar las cuatro de
    // referencia del mundo servido, que son justo las que ya pasan.
    // `WG3_SEED=0x...` y `WG3_REGION=x,z`.
    let seed = match std::env::var("WG3_SEED") {
        Ok(v) => {
            let t = v.trim().trim_start_matches("0x");
            u64::from_str_radix(t, 16).unwrap_or_else(|_| v.trim().parse().expect("WG3_SEED"))
        }
        Err(_) => SERVED_SEED,
    };
    let regions: Vec<(i32, i32)> = match std::env::var("WG3_REGION") {
        Ok(v) => {
            let mut it = v.split(',');
            let x = it.next().unwrap().trim().parse().expect("WG3_REGION x");
            let z = it
                .next()
                .expect("WG3_REGION z")
                .trim()
                .parse()
                .expect("WG3_REGION z");
            vec![(x, z)]
        }
        Err(_) => AUDIT_REGIONS.to_vec(),
    };

    for (rx, rz) in regions {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let world = Wg3ServedWorld::plan_region(&m, seed, region);
        let (min_x, min_z, _, _) = region.bounds();

        let side = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        let mut rasters = Vec::with_capacity(side * side);
        for cz in 0..side {
            for cx in 0..side {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                // **Y con los MACIZOS.** Hasta hoy este volcado llamaba a
                // `build_chunk_raster_with_carves`, que pasa `&[]` en el hueco de los macizos: o
                // sea que el «mundo servido» que dibujaba no llevaba ni un pretil ni un pilar desde
                // que ADR-105 los introdujo. Medir oclusión sobre él y hablar del mundo servido es
                // comparar dos cosas distintas — el mismo aviso que WG3-ROADMAP escribe sobre
                // `compose_region`. El validador nunca tuvo el fallo: `validate.rs:591` ya llamaba
                // a la función completa.
                rasters.push(chunk::build_chunk_raster_full(
                    &m,
                    &world.placements_touching_chunk(&m, coord),
                    &world.segments_touching_chunk(coord),
                    &world.carves_touching_chunk(coord),
                    &world.solids_touching_chunk(coord),
                    coord,
                ));
            }
        }
        let raster_at = |x: f32, z: f32| -> Option<&Wg3Raster> {
            let coord = chunk::Wg3ChunkCoord::containing(x, z);
            let (dx, dz) = (coord.x - base.x, coord.z - base.z);
            if dx < 0 || dz < 0 || dx as usize >= side || dz as usize >= side {
                return None;
            }
            rasters.get(dz as usize * side + dx as usize)
        };

        let cells = (REGION_M / CELL) as usize;
        let mut floors: Vec<Vec<f32>> = vec![Vec::new(); cells * cells];
        for iz in 0..cells {
            for ix in 0..cells {
                let x = min_x + ix as f32 * CELL + CELL * 0.5;
                let z = min_z + iz as f32 * CELL + CELL * 0.5;
                let Some(r) = raster_at(x, z) else { continue };
                let column = r.column_at(x, z);
                let mut out = Vec::new();
                for (i, span) in column.iter().enumerate() {
                    let head = match column.get(i + 1) {
                        Some(next) => (next.bottom_cm - span.top_cm) as f32 / 100.0,
                        None => f32::MAX,
                    };
                    if (HEAD_M..=CEILING_CAP_M).contains(&head) {
                        out.push(span.top_cm as f32 / 100.0);
                    }
                }
                floors[iz * cells + ix] = out;
            }
        }

        let mut blob_of: Vec<Vec<i32>> = floors.iter().map(|l| vec![-1; l.len()]).collect();
        let mut sizes: Vec<usize> = Vec::new();
        for iz0 in 0..cells {
            for ix0 in 0..cells {
                for l0 in 0..floors[iz0 * cells + ix0].len() {
                    if blob_of[iz0 * cells + ix0][l0] >= 0 {
                        continue;
                    }
                    let id = sizes.len() as i32;
                    sizes.push(0);
                    blob_of[iz0 * cells + ix0][l0] = id;
                    let mut q = std::collections::VecDeque::new();
                    q.push_back((ix0, iz0, l0));
                    while let Some((ix, iz, li)) = q.pop_front() {
                        sizes[id as usize] += 1;
                        let here = floors[iz * cells + ix][li];
                        for (dx, dz) in [(1i32, 0i32), (-1, 0), (0, 1), (0, -1)] {
                            let (nx, nz) = (ix as i32 + dx, iz as i32 + dz);
                            if nx < 0 || nz < 0 || nx as usize >= cells || nz as usize >= cells {
                                continue;
                            }
                            let (nx, nz) = (nx as usize, nz as usize);
                            for (nl, there) in floors[nz * cells + nx].iter().enumerate() {
                                if blob_of[nz * cells + nx][nl] >= 0
                                    || (there - here).abs() > MAX_STEP
                                {
                                    continue;
                                }
                                blob_of[nz * cells + nx][nl] = id;
                                q.push_back((nx, nz, nl));
                            }
                        }
                    }
                }
            }
        }
        let main = sizes
            .iter()
            .enumerate()
            .max_by_key(|(_, n)| **n)
            .map(|(i, _)| i as i32)
            .unwrap_or(-1);

        let w = REGION_M * PX;
        let mut svg = format!(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w:.0} {w:.0}\" \
             width=\"{w:.0}\" height=\"{w:.0}\">\n<rect width=\"100%\" height=\"100%\" \
             fill=\"#14161a\"/>\n"
        );
        for iz in 0..cells {
            for ix in 0..cells {
                let b = *blob_of[iz * cells + ix].first().unwrap_or(&-1);
                if b < 0 {
                    continue;
                }
                let fill = if b == main { "#4ade80" } else { "#64748b" };
                svg += &format!(
                    "<rect x=\"{:.1}\" y=\"{:.1}\" width=\"{:.1}\" height=\"{:.1}\" \
                     fill=\"{fill}\" opacity=\"0.9\"/>\n",
                    ix as f32 * CELL * PX,
                    (cells - 1 - iz) as f32 * CELL * PX,
                    CELL * PX,
                    CELL * PX
                );
            }
        }
        svg += "</svg>\n";
        let path = format!("{dir}/wg3_served_{rx}_{rz}.svg");
        std::fs::write(&path, svg).expect("escribir el plano servido");
        println!(
            "[served] {path} — {} manchas, la mayor {} celdas",
            sizes.len(),
            sizes.iter().max().copied().unwrap_or(0)
        );
    }
}

/// **EL VOLCADOR DEL PLAN — y es el criterio de aceptación de ADR-100.**
///
/// Dibuja SOLO el plan: ni piezas, ni conectores, ni ráster, ni una sola malla. Si con las mallas
/// apagadas esto no se lee ya como una planta de edificio, el planificador no está haciendo su
/// trabajo y no hay relleno que lo arregle.
///
/// Ámbar grueso: la espina. Ámbar fino: corredores. Naranja: cruces. Azul: naves. Gris: oficinas.
/// Violeta: servicio. Rojo: callejones. Punteado oscuro: vacío INTENCIONADO. Líneas finas: los
/// enlaces; en rojo discontinuo, los que necesitan enrutador.
///
/// `#[ignore]` porque no afirma nada: dibuja para que se mire. Lánzalo con
/// `WG3_MAP_DIR=... cargo test dump_region_plans -- --ignored --nocapture`.
#[test]
#[ignore]
fn dump_region_plans() {
    const PX: f32 = 4.0;
    let dir = std::env::var("WG3_MAP_DIR").expect("WG3_MAP_DIR: carpeta donde escribir los planos");
    // Mismas perillas que `dump_served_maps`: un fallo del barrido sale con su semilla y su region,
    // y hay que poder pintar ESA. `WG3_SEED=0x...`, `WG3_REGION=x,z`, `WG3_STOREY=n`.
    let seed_over = std::env::var("WG3_SEED").ok().map(|v| {
        let t = v.trim().trim_start_matches("0x").to_string();
        u64::from_str_radix(&t, 16).unwrap_or_else(|_| v.trim().parse().expect("WG3_SEED"))
    });
    let storey_n: usize = std::env::var("WG3_STOREY")
        .ok()
        .and_then(|v| v.trim().parse().ok())
        .unwrap_or(0);
    let regions: Vec<(i32, i32)> = match std::env::var("WG3_REGION") {
        Ok(v) => {
            let mut it = v.split(',');
            let x = it.next().unwrap().trim().parse().expect("WG3_REGION x");
            let z = it
                .next()
                .expect("WG3_REGION z")
                .trim()
                .parse()
                .expect("WG3_REGION z");
            vec![(x, z)]
        }
        Err(_) => AUDIT_REGIONS.to_vec(),
    };

    for (rx, rz) in regions {
        // **La planta baja del EDIFICIO, no `plan_of`** (ADR-120). La composicion de huellas vive
        // en `plan_building` -tiene que correr despues de los pozos-, asi que una planta pedida
        // suelta con `plan_region` sale SIN deformar: este volcado estuvo dibujando un mundo que no
        // es el que se sirve, y con eso no se puede juzgar nada.
        let building = match seed_over {
            Some(sd) => {
                let region = Wg3RegionCoord { x: rx, z: rz };
                let bounds = region.bounds();
                let gates = junction::gates_of_region(composer_seed(sd), rx, rz, bounds);
                plan::plan_building(region.composer_seed(sd), bounds, &gates, STOREYS)
            }
            None => building_of(rx, rz),
        };
        let p = building.storeys[storey_n.min(building.storeys.len() - 1)].clone();
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, _, _) = region.bounds();
        let w = REGION_M * PX;

        // Y hacia ABAJO en SVG, Z hacia arriba en el mundo: se voltea aquí y en un solo sitio, para
        // que un plano y un volcado de ráster se puedan poner uno al lado del otro.
        let to_px = |x: f32, z: f32| ((x - min_x) * PX, (REGION_M - (z - min_z)) * PX);

        let mut svg = format!(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w:.0} {w:.0}\" \
             width=\"{w:.0}\" height=\"{w:.0}\">\n<rect width=\"100%\" height=\"100%\" \
             fill=\"#14161a\"/>\n"
        );

        for s in &p.spaces {
            let (fill, stroke, dash) = match s.role {
                SpaceRole::Spine => ("#f59e0b", "#fbbf24", ""),
                SpaceRole::Corridor => ("#b45309", "#fbbf24", ""),
                // La escalera en verde ácido: es lo único que no es plano, y en un plano de planta
                // hay que poder localizarlo de un vistazo.
                SpaceRole::Stair => ("#65a30d", "#a3e635", ""),
                SpaceRole::Hall => ("#0369a1", "#38bdf8", ""),
                SpaceRole::Office => ("#334155", "#94a3b8", ""),
                SpaceRole::Service => ("#5b21b6", "#a78bfa", ""),
                SpaceRole::Storage => ("#1e293b", "#64748b", ""),
                SpaceRole::DeadEnd => ("#7f1d1d", "#f87171", ""),
                SpaceRole::Void => ("none", "#3f3f46", " stroke-dasharray=\"5 4\""),
            };
            // **UN RECTANGULO POR PARTE, no la envolvente** (ADR-120 D1). Dibujar `rect` haria que
            // este volcado -que es el criterio de aceptacion de ADR-100- mintiera exactamente sobre
            // lo que ADR-120 viene a cambiar: pintaria una L como una caja, y ademas pintaria dos
            // cajas pisandose donde hay dos espacios entrelazados que no comparten un centimetro.
            for r in s.parts() {
                let (x0, z0, x1, z1) = r.bounds_m();
                let (px, py) = to_px(x0, z1);
                svg += &format!(
                    "<rect x=\"{px:.1}\" y=\"{py:.1}\" width=\"{:.1}\" height=\"{:.1}\" \
                     fill=\"{fill}\" fill-opacity=\"0.55\" stroke=\"{stroke}\" \
                     stroke-width=\"1.2\"{dash}/>\n",
                    (x1 - x0) * PX,
                    (z1 - z0) * PX
                );
            }
            // Y el contorno de la HUELLA por encima, para que el quiebro se lea: sin el, dos partes
            // pegadas se ven como dos salas con una pared en medio que no existe.
            if s.is_composite() {
                let mut d = String::new();
                for r in s.parts() {
                    let (x0, z0, x1, z1) = r.bounds_m();
                    let (ax, ay) = to_px(x0, z1);
                    d += &format!(
                        "M{ax:.1} {ay:.1} h{:.1} v{:.1} h{:.1} Z ",
                        (x1 - x0) * PX,
                        (z1 - z0) * PX,
                        -(x1 - x0) * PX
                    );
                }
                svg += &format!(
                    "<path d=\"{d}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"2.2\" \
                     opacity=\"0.9\"/>\n"
                );
            }
        }

        for l in &p.links {
            let (ax, az) = p.spaces[l.a].rect.centre_m();
            let (bx, bz) = p.spaces[l.b].rect.centre_m();
            let (x0, y0) = to_px(ax, az);
            let (x1, y1) = to_px(bx, bz);
            let (colour, extra) = match l.kind {
                LinkKind::Route => ("#f87171", " stroke-dasharray=\"6 4\""),
                LinkKind::Junction => ("#fed7aa", ""),
                LinkKind::Access => ("#e2e8f0", ""),
                LinkKind::Doorway => ("#94a3b8", ""),
            };
            // **La arista va FLOJA y la puerta va FUERTE, y eso lo decidió mirar el volcado.** Con
            // las aristas a plena opacidad, los cincuenta accesos de un corredor convergen a su
            // centro y el plano se lee como un estallido de líneas en vez de como una planta. Lo que
            // importa —dónde está la puerta— tiene que verse; la arista sólo tiene que dejar seguir
            // el grafo a quien lo busque.
            let strength = if l.kind == LinkKind::Route { 0.9 } else { 0.20 };
            svg += &format!(
                "<line x1=\"{x0:.1}\" y1=\"{y0:.1}\" x2=\"{x1:.1}\" y2=\"{y1:.1}\" \
                 stroke=\"{colour}\" stroke-width=\"1\" opacity=\"{strength}\"{extra}/>\n"
            );
            // El punto del paso, que es lo que el relleno tiene que respetar. Sin dibujarlo, dos
            // planos con los mismos enlaces y las puertas en sitios distintos se ven iguales.
            let (dx, dy) = to_px(l.at_x_cm as f32 / 100.0, l.at_z_cm as f32 / 100.0);
            svg +=
                &format!("<circle cx=\"{dx:.1}\" cy=\"{dy:.1}\" r=\"2.4\" fill=\"{colour}\"/>\n");
        }

        svg += "</svg>\n";
        let path = format!("{dir}/wg3_plan_{rx}_{rz}.svg");
        std::fs::write(&path, svg).expect("escribir el plano del plan");
        println!(
            "[plan] {path} — {} espacios, {} enlaces, {} componentes",
            p.spaces.len(),
            p.links.len(),
            p.components()
        );
    }
}

/// E0 de «doblar la escalera»: ¿cuántos sitios GANA una escalera de ida y vuelta?
///
/// **Mide la puerta de HUELLA de `dig_wells`, y sólo ésa** (`plan.rs`, el `continue` de
/// `run < run_cm + GOOD_WALL_CM || across < STAIR_WIDTH_CM`). Los filtros que van después —que ninguna
/// puerta caiga dentro de la franja, que el espacio de arriba contenga el hueco recortado— NO se
/// aplican aquí, así que **estas cifras son una COTA SUPERIOR del número de sitios, no el número de
/// escaleras**. Lo que la sonda compara es válido igualmente porque omite los mismos filtros en las
/// tres variantes: si la huella doblada no ensancha la puerta, nada de lo que viene después lo va a
/// ensanchar.
///
/// Las tres variantes, con la aritmética a la vista:
/// - **recta**, la de hoy: 15 tiras × 60 = 900 de tiro, y el espacio tiene que dar 900 + 360 = **1260**
///   de largo por **360** de ancho.
/// - **doblada estrecha**: dos tramos de 8 y 7 tiras, así que 8 × 60 = 480 de tiro más 150 de rellano
///   = 630, y el espacio pide 630 + 360 = **990** de largo. El ancho son dos tramos de 180 más una
///   celda del ráster de separación: **410**.
/// - **doblada ancha**: igual de larga, pero con tramos de 240 —la anchura de una puerta— y la misma
///   separación: **530** de ancho.
///
/// Las dos anchuras están porque el número que decide no es el largo sino cuál de los dos ata, y con
/// una sola cifra no se ve. La separación de una celda no es holgura: el ráster es conservador y sin
/// ella la frontera entre los dos tramos se maciza a la cota del más alto.
///
/// `cargo test --manifest-path backend/Cargo.toml probe_stair_sites_if_doubled -- --ignored --nocapture`
#[test]
#[ignore]
fn probe_stair_sites_if_doubled() {
    /// Copia local de `plan::side_of_point_in`, que es privada del módulo. Son cuatro comparaciones
    /// contra los bordes del rect con la misma tolerancia; se copia en vez de abrir la visibilidad de
    /// producción para una medida.
    fn side_of(r: &super::plan::PlanRect, x_cm: i32, z_cm: i32) -> Option<u8> {
        const EPS: i32 = 2;
        if (r.max_z_cm - z_cm).abs() <= EPS {
            return Some(0);
        }
        if (r.max_x_cm - x_cm).abs() <= EPS {
            return Some(1);
        }
        if (r.min_z_cm - z_cm).abs() <= EPS {
            return Some(2);
        }
        if (r.min_x_cm - x_cm).abs() <= EPS {
            return Some(3);
        }
        None
    }

    // (nombre, largo mínimo del espacio, ancho mínimo del espacio)
    const VARIANTS: [(&str, i32, i32); 3] = [
        ("recta (hoy)   ", 1260, 360),
        ("doblada 410   ", 990, 410),
        ("doblada 530   ", 990, 530),
    ];

    let m = real_manifest();
    let mut totals = [(0usize, 0usize); VARIANTS.len()];

    for (rx, rz) in AUDIT_REGIONS {
        let p = plan_of(&m, rx, rz);

        // Las puertas de cada espacio, igual que las junta `dig_wells`: enlaces interiores y puertas
        // de junta con la región vecina.
        let mut doors: Vec<Vec<(i32, i32)>> = vec![Vec::new(); p.spaces.len()];
        for l in &p.links {
            doors[l.a].push((l.at_x_cm, l.at_z_cm));
            doors[l.b].push((l.at_x_cm, l.at_z_cm));
        }
        for g in &p.gates {
            doors[g.space].push((g.x_cm, g.z_cm));
        }

        println!(
            "[escalera] región ({rx},{rz}) — {} espacios",
            p.spaces.len()
        );

        for (v, (name, min_run, min_across)) in VARIANTS.iter().enumerate() {
            let mut spaces = 0usize;
            let mut pairs = 0usize;

            for (i, s) in p.spaces.iter().enumerate() {
                if !s.role.is_built() || s.role.is_circulation() || s.rise_cm != 0 {
                    continue;
                }
                let mut any = false;
                for side in doors[i]
                    .iter()
                    .filter_map(|&(dx, dz)| side_of(&s.rect, dx, dz))
                    .collect::<Vec<_>>()
                {
                    // El tiro corre perpendicular a la pared de la puerta, igual que en `dig_wells`.
                    let (run, across) = if side.is_multiple_of(2) {
                        (s.rect.depth_cm(), s.rect.width_cm())
                    } else {
                        (s.rect.width_cm(), s.rect.depth_cm())
                    };
                    if run >= *min_run && across >= *min_across {
                        pairs += 1;
                        any = true;
                    }
                }
                if any {
                    spaces += 1;
                }
            }

            totals[v].0 += spaces;
            totals[v].1 += pairs;
            println!("    {name} {spaces:3} espacios · {pairs:3} pares (espacio, puerta)");
        }
    }

    println!(
        "[escalera] TOTAL sobre las {} regiones:",
        AUDIT_REGIONS.len()
    );
    for (v, (name, _, _)) in VARIANTS.iter().enumerate() {
        println!(
            "    {name} {:3} espacios · {:3} pares",
            totals[v].0, totals[v].1
        );
    }
}

/// ADR-104 D1 — **cuántos ATRIOS produce el plan hoy, y cuántos podría producir.**
///
/// Un atrio es una nave (`Hall`) con la planta de arriba vacía justo encima: `void_above && Hall`.
/// La sonda cuenta eso, y al lado cuenta dos cosas más que dicen si el número es pequeño porque el
/// mecanismo falla o porque **no hay naves**: cuántos espacios tienen vacío encima sea cual sea su
/// papel, y cuántas naves hay en total.
///
/// **Es la medida que decide si D1 basta o hace falta D2.** Si casi todas las naves ya tienen vacío
/// encima, colocar vacíos a propósito no añade nada y el cuello es el reparto de naves; si hay muchos
/// vacíos y pocas naves debajo, D2 es exactamente la palanca.
///
/// `cargo test --manifest-path backend/Cargo.toml probe_how_many_atria -- --ignored --nocapture`
#[test]
#[ignore]
fn probe_how_many_atria() {
    let mut t_atria = 0usize;
    let mut t_halls = 0usize;
    let mut t_void_above = 0usize;

    for (rx, rz) in AUDIT_REGIONS {
        // El edificio SERVIDO: ver `served_building_of`.
        let b = served_building_of(rx, rz);
        let mut atria = 0usize;
        let mut halls = 0usize;
        let mut void_above = 0usize;
        let mut atrium_area = 0.0f32;

        // La última planta no cuenta: `void_above` sólo lo pone quien tiene una planta encima.
        for storey in &b.storeys {
            for s in &storey.spaces {
                if !s.role.is_built() {
                    continue;
                }
                if s.role == SpaceRole::Hall {
                    halls += 1;
                }
                if s.void_above {
                    void_above += 1;
                    if s.role == SpaceRole::Hall {
                        atria += 1;
                        atrium_area += s.rect.area_m2();
                    }
                }
            }
        }

        // POR QUÉ una nave no llega a atrio. `carve_atria` descarta la nave ENTERA si cualquier
        // espacio de arriba que la pise es circulación, así que hay tres desenlaces y conviene
        // separarlos: sin nadie encima (atrio gratis), tallada, o vetada por circulación.
        let mut free = 0usize;
        let mut carved = 0usize;
        let mut vetoed = 0usize;
        for n in 0..b.storeys.len().saturating_sub(1) {
            let (below, above) = (&b.storeys[n], &b.storeys[n + 1]);
            for s in below.spaces.iter().filter(|s| s.role == SpaceRole::Hall) {
                let covering: Vec<&plan::PlannedSpace> = above
                    .spaces
                    .iter()
                    .filter(|t| t.rect.overlaps(&s.rect))
                    .collect();
                if covering.is_empty() {
                    free += 1;
                } else if covering
                    .iter()
                    .any(|t| t.role.is_circulation() || t.role == SpaceRole::Spine)
                {
                    vetoed += 1;
                } else {
                    carved += 1;
                }
            }
        }

        println!(
            "[atrio] ({rx},{rz}) — {atria} atrios ({atrium_area:.0} m²) · {halls} naves · \
             {void_above} espacios con vacío encima"
        );
        println!(
            "         naves bajo la planta alta: {free} sin nadie encima · {carved} talladas · \
             {vetoed} VETADAS por circulación"
        );
        println!("         plantas del edificio: {}", b.storeys.len());
        // ADR-104 enm. 4 — cuántas ALTURAS DE PLANTA mide cada atrio, que es lo que separa un atrio
        // de una MEGASALA. Un histograma y no una media: lo que importa es que la cola exista.
        let mut hist = [0usize; 8];
        for p in &b.storeys {
            for s in p.spaces.iter().filter(|s| s.atrium_storeys > 0) {
                hist[(s.atrium_storeys as usize).min(7)] += 1;
            }
        }
        let tallest = hist
            .iter()
            .enumerate()
            .filter(|(_, &c)| c > 0)
            .map(|(k, _)| k)
            .max()
            .unwrap_or(0);
        // Y DÓNDE están, que es lo que hace falta para ir a mirarlas en partida.
        for p in &b.storeys {
            for s in p.spaces.iter().filter(|s| s.atrium_storeys > 2) {
                let (cx, cz) = s.rect.centre_m();
                println!(
                    "         MEGASALA en ({cx:.1}, {cz:.1}) · suelo a {:.2} m · {:.0} m² · \n                     {:.2} m de altura",
                    s.floor_y_cm as f32 / 100.0,
                    s.area_m2(),
                    (s.atrium_storeys as i32 * plan::STOREY_HEIGHT_CM - 24) as f32 / 100.0
                );
            }
        }
        println!(
            "         alturas de planta por atrio: {hist:?} (índice = plantas) · más alto \
             {:.2} m",
            (tallest as i32 * plan::STOREY_HEIGHT_CM - 24) as f32 / 100.0
        );
        t_atria += atria;
        t_halls += halls;
        t_void_above += void_above;
    }

    println!(
        "[atrio] TOTAL — {t_atria} atrios · {t_halls} naves · {t_void_above} con vacío encima"
    );

    // **Cuántas plantas levanta el mundo de verdad**, que es el techo real de una megasala apilada:
    // un atrio no puede vaciar una planta que no existe. Barrido de 49 y no las cuatro de referencia,
    // por lo mismo que `dig_wells`: la altura del edificio es coincidencia de semilla.
    let mut storeys = [0usize; 12];
    for rx in -3..=3 {
        for rz in -3..=3 {
            storeys[served_building_of(rx, rz).storeys.len().min(11)] += 1;
        }
    }
    println!("[atrio] plantas por edificio en 49 regiones: {storeys:?}");
}

/// ADR-104, verificaciones (a) y (b) — **un atrio mide dos plantas EN EL RÁSTER, y encima no hay losa
/// fantasma.**
///
/// Las dos con una sola medida, y por eso está escrito así: `headroom_above_floor` devuelve el hueco
/// entre el suelo y lo primero macizo que hay encima. Si el forjado siguiera ahí —dibujado o no—, el
/// hueco saldría en los 3,08 m de una planta y no en los 6,40 de dos. **Un techo invisible a media
/// altura es el fallo clásico de este sistema y no sale en una captura**; aquí sale.
///
/// Se mide sobre el mundo SERVIDO, que es lo que resuelve el movimiento, y no sobre el relleno —
/// medir uno para hablar del otro es el error de método que ya costó tres conclusiones falsas.
#[test]
fn an_atrium_is_two_storeys_tall_in_the_raster() {
    /// Lo que tiene que medir un atrio, con margen para la conservadora del ráster: dos plantas menos
    /// dos losas son 6,40 m, y una planta sola son 3,08. Cualquier valor por debajo de esto significa
    /// que hay forjado donde no debería.
    const ATRIUM_MIN_M: f32 = 6.0;
    /// Cuánto hay que meterse desde el borde del rectángulo para no estar midiendo su pared.
    const INSET_M: f32 = 1.5;
    const STEP_M: f32 = 0.5;

    let m = real_manifest();
    let mut checked = 0usize;

    for (rx, rz) in AUDIT_REGIONS {
        let b = building_of(rx, rz);
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, _, _) = region.bounds();
        let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);

        let side = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        let mut rasters = Vec::with_capacity(side * side);
        for cz in 0..side {
            for cx in 0..side {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                rasters.push(chunk::build_chunk_raster_full(
                    &m,
                    &served.placements_touching_chunk(&m, coord),
                    &served.segments_touching_chunk(coord),
                    &served.carves_touching_chunk(coord),
                    &served.solids_touching_chunk(coord),
                    coord,
                ));
            }
        }
        let raster_at = |x: f32, z: f32| -> Option<&Wg3Raster> {
            let coord = chunk::Wg3ChunkCoord::containing(x, z);
            let (dx, dz) = (coord.x - base.x, coord.z - base.z);
            if dx < 0 || dz < 0 || dx as usize >= side || dz as usize >= side {
                return None;
            }
            rasters.get(dz as usize * side + dx as usize)
        };

        for storey in &b.storeys {
            for s in &storey.spaces {
                // La condición es la de `fill::is_atrium`, **compuesta incluida** (ADR-120 D5): una
                // nave deformada NO es atrio en esta tanda, porque el pretil se traza metiendo la
                // envolvente hacia dentro y el vano del forjado se recorta sobre ella — los dos
                // caerían sobre el suelo del vecino. Pedirle aquí 6,40 m sería exigir un atrio que se
                // decidió no construir.
                if !(s.void_above && s.role == SpaceRole::Hall && !s.is_composite()) {
                    continue;
                }
                let r = s.rect;
                let floor_m = s.floor_y_cm as f32 / 100.0;
                let probe_y = floor_m + 0.5;

                // El MÁXIMO y no la media: un atrio lleva contenido dentro —pilares, piezas
                // autoradas— y una celda debajo de una caja mide lo que mide esa caja. Lo que este
                // test afirma es que el hueco de dos plantas EXISTE, no que ocupe toda la huella.
                let mut best = 0.0f32;
                let mut samples = 0usize;
                let (x0, x1) = (r.min_x_cm as f32 / 100.0, r.max_x_cm as f32 / 100.0);
                let (z0, z1) = (r.min_z_cm as f32 / 100.0, r.max_z_cm as f32 / 100.0);
                let mut x = x0 + INSET_M;
                while x < x1 - INSET_M {
                    let mut z = z0 + INSET_M;
                    while z < z1 - INSET_M {
                        if let Some(raster) = raster_at(x, z) {
                            if let Some(h) = raster.headroom_above_floor(x, probe_y, z) {
                                best = best.max(h);
                                samples += 1;
                            }
                        }
                        z += STEP_M;
                    }
                    x += STEP_M;
                }

                if samples == 0 {
                    continue;
                }
                println!(
                    "[atrio-ráster] ({rx},{rz}) nave de {:.0} m² — hueco máximo {best:.2} m \
                     ({samples} celdas)",
                    r.area_m2()
                );
                assert!(
                    best >= ATRIUM_MIN_M,
                    "({rx},{rz}) un atrio de {:.0} m² mide {best:.2} m de hueco y tendría que medir \
                     {ATRIUM_MIN_M}: o el techo se quedó a la altura de una planta, o hay losa de \
                     forjado encima que nadie ve",
                    r.area_m2()
                );
                checked += 1;
            }
        }
    }

    assert!(
        checked > 0,
        "ninguna región produjo un atrio, así que este test no ha probado nada"
    );
    println!("[atrio-ráster] {checked} atrios verificados a dos plantas");
}

/// ADR-104 verificación (c) — **desde la planta alta se VE el atrio**, o sea que el muro que lo
/// sellaba ya no está.
///
/// Hasta D3 el atrio medía dos plantas y no se veía desde arriba, que es la peor combinación posible:
/// todos los contadores en verde y la sensación pedida ausente. `segment::emit_side` emite las cuatro
/// paredes de cada tramo a altura completa, así que cada espacio de la planta alta que daba al vacío
/// le plantaba un muro de 3,08 m.
///
/// **Se mide en el anillo de FUERA del rectángulo del atrio**, que es donde vive la pared del vecino —
/// dentro del atrio no hay nada que mirar, y eso ya lo dice el test de la altura. A la altura de los
/// ojos de quien está en la planta alta.
#[test]
fn from_the_upper_storey_the_atrium_is_open() {
    /// Cuánta pared se tolera en el anillo. No es cero: un atrio puede tener un pilar pegado a una
    /// esquina, y el ráster es conservador. Antes de D3 esto es prácticamente 100 %.
    const MAX_SOLID_FRACTION: f32 = 0.25;
    /// A qué distancia por fuera del rectángulo se mira: dentro del medio metro que ensancha el vano.
    const RING_OUT_M: f32 = 0.25;
    const STEP_M: f32 = 0.5;
    /// Altura de ojos sobre el suelo de la planta de arriba.
    const EYE_M: f32 = 1.6;

    let m = real_manifest();
    let storey_m = plan::STOREY_HEIGHT_CM as f32 / 100.0;
    let mut checked = 0usize;

    for (rx, rz) in AUDIT_REGIONS {
        let b = building_of(rx, rz);
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, _, _) = region.bounds();
        let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);

        let side = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        let mut rasters = Vec::with_capacity(side * side);
        for cz in 0..side {
            for cx in 0..side {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                rasters.push(chunk::build_chunk_raster_full(
                    &m,
                    &served.placements_touching_chunk(&m, coord),
                    &served.segments_touching_chunk(coord),
                    &served.carves_touching_chunk(coord),
                    &served.solids_touching_chunk(coord),
                    coord,
                ));
            }
        }
        let raster_at = |x: f32, z: f32| -> Option<&Wg3Raster> {
            let coord = chunk::Wg3ChunkCoord::containing(x, z);
            let (dx, dz) = (coord.x - base.x, coord.z - base.z);
            if dx < 0 || dz < 0 || dx as usize >= side || dz as usize >= side {
                return None;
            }
            rasters.get(dz as usize * side + dx as usize)
        };

        for (storey_index, storey) in b.storeys.iter().enumerate() {
            for s in &storey.spaces {
                // La condición es la de `fill::is_atrium`, **compuesta incluida** (ADR-120 D5): una
                // nave deformada NO es atrio en esta tanda, porque el pretil se traza metiendo la
                // envolvente hacia dentro y el vano del forjado se recorta sobre ella — los dos
                // caerían sobre el suelo del vecino. Pedirle aquí 6,40 m sería exigir un atrio que se
                // decidió no construir.
                if !(s.void_above && s.role == SpaceRole::Hall && !s.is_composite()) {
                    continue;
                }
                let r = s.rect;
                let eye = s.floor_y_cm as f32 / 100.0 + storey_m + EYE_M;
                let (x0, x1) = (r.min_x_cm as f32 / 100.0, r.max_x_cm as f32 / 100.0);
                let (z0, z1) = (r.min_z_cm as f32 / 100.0, r.max_z_cm as f32 / 100.0);

                // El anillo: las cuatro líneas paralelas a los lados, por fuera.
                let mut ring: Vec<(f32, f32)> = Vec::new();
                let mut x = x0;
                while x <= x1 {
                    ring.push((x, z0 - RING_OUT_M));
                    ring.push((x, z1 + RING_OUT_M));
                    x += STEP_M;
                }
                let mut z = z0;
                while z <= z1 {
                    ring.push((x0 - RING_OUT_M, z));
                    ring.push((x1 + RING_OUT_M, z));
                    z += STEP_M;
                }

                // ADR-104 enm. 3 — sólo cuentan los puntos del anillo que caen sobre una sala
                // CONSTRUIDA de la planta de arriba: donde arriba no hay nadie, el muro se queda a
                // propósito, y medirlo sería exigir un agujero a la nada.
                let up = b.storeys.get(storey_index + 1);
                let mut solid = 0usize;
                let mut seen = 0usize;
                for (px, pz) in ring {
                    let Some(raster) = raster_at(px, pz) else {
                        continue;
                    };
                    let probe = plan::PlanRect {
                        min_x_cm: (px * 100.0) as i32 - 5,
                        min_z_cm: (pz * 100.0) as i32 - 5,
                        max_x_cm: (px * 100.0) as i32 + 5,
                        max_z_cm: (pz * 100.0) as i32 + 5,
                    };
                    let someone_up = up.is_some_and(|u| {
                        u.spaces
                            .iter()
                            .any(|t| t.role.is_built() && t.hits_rect(&probe))
                    });
                    if !someone_up {
                        continue;
                    }
                    seen += 1;
                    if raster.is_solid_at(px, eye, pz) {
                        solid += 1;
                    }
                }
                if seen == 0 {
                    continue;
                }

                let frac = solid as f32 / seen as f32;
                println!(
                    "[atrio-abierto] ({rx},{rz}) nave de {:.0} m² — {:.0} % de macizo en el anillo \
                     ({solid}/{seen})",
                    r.area_m2(),
                    frac * 100.0
                );
                assert!(
                    frac <= MAX_SOLID_FRACTION,
                    "({rx},{rz}) el atrio de {:.0} m² sigue TAPIADO por arriba: {:.0} % del anillo \
                     es macizo a la altura de los ojos de la planta alta. Se ve como una sala normal \
                     y el atrio sólo existe para quien ya está dentro",
                    r.area_m2(),
                    frac * 100.0
                );
                checked += 1;
            }
        }
    }

    assert!(
        checked > 0,
        "ningún atrio medido, el test no ha probado nada"
    );
    println!("[atrio-abierto] {checked} atrios abiertos por arriba");
}

/// ADR-104 D4 — **por un agujero se CAE una planta entera**, y eso se mide en el ráster o no se sabe.
///
/// El fallo que este test existe para cazar no es «no hay agujero»: es **el agujero a medias**. Entre
/// dos plantas hay DOS losas —el techo de abajo y el suelo de arriba, espalda contra espalda—, así que
/// llevarse sólo una deja un hueco por el que se ve y no se pasa: dibujado perfecto, todos los
/// contadores en verde, y el jugador rebotando contra un techo invisible.
///
/// ADR-104 enm. 3 — **un atrio nunca se abre a la nada.** Por cada atrio y cada lado, el vano de
/// esa banda existe si y sólo si arriba hay una sala construida que la toca; y donde no la hay, el
/// ráster conserva macizo a la altura de los ojos de la planta alta justo sobre la pared del atrio.
#[test]
fn an_atrium_never_opens_onto_nothing() {
    const EYE_M: f32 = 1.6;
    let m = real_manifest();
    let grow = (fill::CARVE_DEPTH_M * CM_PER_M) as i32;
    let mut atria = 0usize;
    let mut walled_sides = 0usize;
    let mut open_sides = 0usize;
    for (rx, rz) in AUDIT_REGIONS {
        let b = building_of(rx, rz);
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, _, _) = region.bounds();
        let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);
        let side_n = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        let mut rasters = Vec::with_capacity(side_n * side_n);
        let mut carves = Vec::new();
        let mut mouths: Vec<(f32, f32, f32)> = Vec::new();
        for cz in 0..side_n {
            for cx in 0..side_n {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                let here = served.carves_touching_chunk(coord);
                let segs = served.segments_touching_chunk(coord);
                for g in &segs {
                    let (w, d) = (g.size_x_cm as f32 / 100.0, g.size_z_cm as f32 / 100.0);
                    for o in &g.openings {
                        let (lx, lz) =
                            placement::local_point(o.side, o.offset_cm as f32 / 100.0, w, d);
                        mouths.push((
                            g.x_cm as f32 / 100.0 + lx,
                            g.z_cm as f32 / 100.0 + lz,
                            o.width_cm as f32 / 200.0 + 0.6,
                        ));
                    }
                }
                rasters.push(chunk::build_chunk_raster_full(
                    &m,
                    &served.placements_touching_chunk(&m, coord),
                    &segs,
                    &here,
                    &served.solids_touching_chunk(coord),
                    coord,
                ));
                carves.extend(here);
            }
        }
        let raster_at = |x: f32, z: f32| -> Option<&Wg3Raster> {
            let coord = chunk::Wg3ChunkCoord::containing(x, z);
            let (dx, dz) = (coord.x - base.x, coord.z - base.z);
            if dx < 0 || dz < 0 || dx as usize >= side_n || dz as usize >= side_n {
                return None;
            }
            rasters.get(dz as usize * side_n + dx as usize)
        };
        for (n, storey) in b.storeys.iter().enumerate() {
            let up = b.storeys.get(n + 1);
            for s in &storey.spaces {
                if !(s.void_above && s.role == SpaceRole::Hall && !s.is_composite()) {
                    continue;
                }
                atria += 1;
                let eye = (s.floor_y_cm + plan::STOREY_HEIGHT_CM) as f32 / 100.0 + EYE_M;
                for band in fill::bands_of(&s.rect, grow) {
                    // Sin las esquinas de la banda, igual que `atrium_carves`: una sala de
                    // arriba que sólo pisa el sobrante de la banda está al lado, no encima.
                    let inner = {
                        let along_x = band.width_cm() >= band.depth_cm();
                        let mut c = band;
                        if along_x {
                            c.min_x_cm += grow;
                            c.max_x_cm -= grow;
                        } else {
                            c.min_z_cm += grow;
                            c.max_z_cm -= grow;
                        }
                        c
                    };
                    let someone_up = up.is_some_and(|u| {
                        u.spaces
                            .iter()
                            .any(|t| t.role.is_built() && t.hits_rect(&inner))
                    });
                    // El vano ya no es la banda entera: es el tramo de banda bajo cada sala de
                    // arriba (fusión 2026-09-06). Se mira si algún recorte a la cota del forjado
                    // pisa el CENTRO de la banda, fuera de las esquinas, porque el recorte del
                    // atrio vecino asoma por la esquina compartida sin que este lado esté abierto.
                    let core = {
                        let along_x = band.width_cm() >= band.depth_cm();
                        let mut c = band;
                        if along_x {
                            c.min_x_cm += 2 * grow;
                            c.max_x_cm -= 2 * grow;
                        } else {
                            c.min_z_cm += 2 * grow;
                            c.max_z_cm -= 2 * grow;
                        }
                        c
                    };
                    let along_x = band.width_cm() >= band.depth_cm();
                    let carved = carves.iter().any(|k| {
                        k.bottom_y_cm == s.floor_y_cm + plan::STOREY_HEIGHT_CM
                            // Con la FORMA de la banda: el mismo grosor y en la misma línea de
                            // muro. Una puerta de la planta de arriba también es un recorte a esa
                            // cota y no es un vano de atrio.
                            && if along_x {
                                k.z_cm == band.min_z_cm
                                    && k.size_z_cm == band.depth_cm()
                                    && k.x_cm < core.max_x_cm
                                    && k.x_cm + k.size_x_cm > core.min_x_cm
                            } else {
                                k.x_cm == band.min_x_cm
                                    && k.size_x_cm == band.width_cm()
                                    && k.z_cm < core.max_z_cm
                                    && k.z_cm + k.size_z_cm > core.min_z_cm
                            }
                    });
                    assert_eq!(
                        carved,
                        someone_up,
                        "({rx},{rz}) atrio en {:?}: banda {band:?} {} y arriba {} sala",
                        s.rect,
                        if carved { "abierta" } else { "tapiada" },
                        if someone_up { "hay" } else { "no hay" }
                    );
                    if someone_up {
                        open_sides += 1;
                        continue;
                    }
                    walled_sides += 1;
                    // El muro del atrio de ese lado sigue ahí a la altura de los ojos de arriba,
                    // también sobre sus puertas a salas más bajas (`atrium_aprons`): se sondea cada
                    // metro a lo largo de la banda, medio grosor de pared por dentro de la huella,
                    // saltando las bocas a otra sala de la misma altura (esas se cortan enteras a
                    // propósito).
                    let (cx, cz) = s.rect.centre_m();
                    let along_x = band.width_cm() >= band.depth_cm();
                    let (bx, bz) = band.centre_m();
                    let inward = 0.08;
                    let (x0, x1, z0, z1) = (
                        band.min_x_cm as f32 / 100.0,
                        band.max_x_cm as f32 / 100.0,
                        band.min_z_cm as f32 / 100.0,
                        band.max_z_cm as f32 / 100.0,
                    );
                    // Pasadas las esquinas: ahí se solapan dos bandas y la abierta manda.
                    let mut u = if along_x { x0 } else { z0 } + 1.6;
                    let end = if along_x { x1 } else { z1 } - 1.6;
                    while u < end {
                        let (px, pz) = if along_x {
                            (u, bz + (cz - bz).signum() * inward)
                        } else {
                            (bx + (cx - bx).signum() * inward, u)
                        };
                        let near_mouth = mouths.iter().any(|&(mx, mz, half)| {
                            (mx - px).abs() < half && (mz - pz).abs() < half
                        });
                        if near_mouth {
                            u += 1.0;
                            continue;
                        }
                        if let Some(raster) = raster_at(px, pz) {
                            assert!(
                                raster.is_solid_at(px, eye, pz),
                                "({rx},{rz}) atrio en {:?}: el lado tapiado no tiene muro en ({px:.2}, {pz:.2}) a {eye:.2}",
                                s.rect
                            );
                        }
                        u += 1.0;
                    }
                }
            }
        }
    }
    println!(
        "[atrio] {atria} atrios: {open_sides} lados abiertos a una sala, {walled_sides} tapiados"
    );
    assert!(atria >= 1, "sin atrios en cuatro regiones");
}

/// ADR-126 — **un pozo te deja en el suelo de la cámara, exactamente D más abajo; el pasillo entre
/// dos pozos aguanta; y nadie ha nacido encima de la rejilla.** Se mide contra el ráster servido,
/// que es lo que el servidor consulta para dejar pasar: si la tierra o la cámara faltaran en una
/// celda, `floor_below` devolvería otra cosa o nada.
#[test]
fn pits_drop_you_to_a_dark_chamber_and_the_bridges_hold() {
    const EYE_M: f32 = 1.6;
    let m = real_manifest();
    let mut clusters = 0usize;
    let mut pits = 0usize;

    for (rx, rz) in AUDIT_REGIONS {
        let b = building_of(rx, rz);
        let found = fill::pit_clusters_of(&b, &fill::fill_building(&b, &m).segments);
        if found.is_empty() {
            continue;
        }
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, _, _) = region.bounds();
        let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);

        let side = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        let mut rasters = Vec::with_capacity(side * side);
        let mut solids = Vec::new();
        for cz in 0..side {
            for cx in 0..side {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                let here = served.solids_touching_chunk(coord);
                rasters.push(chunk::build_chunk_raster_full(
                    &m,
                    &served.placements_touching_chunk(&m, coord),
                    &served.segments_touching_chunk(coord),
                    &served.carves_touching_chunk(coord),
                    &here,
                    coord,
                ));
                solids.extend(here);
            }
        }
        let raster_at = |x: f32, z: f32| -> &Wg3Raster {
            let coord = chunk::Wg3ChunkCoord::containing(x, z);
            let (dx, dz) = (coord.x - base.x, coord.z - base.z);
            &rasters[dz as usize * side + dx as usize]
        };

        for c in found {
            clusters += 1;
            // Nadie encima: ningún macizo que no sea de pozo y arranque a ras de suelo pisa la huella.
            let fp = c.footprint();
            let intruders: Vec<&segment::Wg3Solid> = solids
                .iter()
                .filter(|s| {
                    s.style != fill::PIT_STYLE
                        && s.style != fill::PIT_SHAFT_STYLE
                        && !s.is_decoration()
                })
                .filter(|s| s.bottom_y_cm >= c.floor_y_cm - 1 && s.bottom_y_cm < c.floor_y_cm + 100)
                .filter(|s| {
                    let (sx0, sz0, sx1, sz1) = s.bounds();
                    sx1 * 100.0 > fp.min_x_cm as f32
                        && sx0 * 100.0 < fp.max_x_cm as f32
                        && sz1 * 100.0 > fp.min_z_cm as f32
                        && sz0 * 100.0 < fp.max_z_cm as f32
                })
                .collect();
            assert!(
                intruders.is_empty(),
                "({rx},{rz}) rejilla en ({},{}): {} macizos encima, el primero {:?}",
                c.x0_cm,
                c.z0_cm,
                intruders.len(),
                intruders[0]
            );
            let floor_m = c.floor_y_cm as f32 / 100.0;
            let depth_m = c.depth_cm as f32 / 100.0;
            for i in 0..c.nx {
                for j in 0..c.nz {
                    let p = c.pit(i, j);
                    let (cx, cz) = p.centre_m();
                    let bottom = raster_at(cx, cz)
                        .floor_below(cx, floor_m + EYE_M, cz)
                        .unwrap_or_else(|| {
                            panic!(
                                "({rx},{rz}) pozo ({i},{j}) en ({cx:.2},{cz:.2}): sin suelo debajo"
                            )
                        });
                    assert!(
                        (floor_m - bottom - depth_m).abs() < 0.03,
                        "({rx},{rz}) pozo ({i},{j}): cae {:.2} m y la rejilla dice {depth_m}",
                        floor_m - bottom
                    );
                    pits += 1;
                    // El pasillo hasta el pozo de al lado, en las dos direcciones: el centro de
                    // la celda del pasillo (que desde enm. 1 es UNA celda de 50 cm).
                    for (bx, bz) in [
                        ((p.max_x_cm + fill::PIT_BRIDGE_CM / 2) as f32 / 100.0, cz),
                        (cx, (p.max_z_cm + fill::PIT_BRIDGE_CM / 2) as f32 / 100.0),
                    ] {
                        let held = raster_at(bx, bz).floor_below(bx, floor_m + EYE_M, bz);
                        assert!(
                            held.is_some_and(|f| (f - floor_m).abs() < 0.01),
                            "({rx},{rz}) pasillo en ({bx:.2},{bz:.2}) junto al pozo ({i},{j}): suelo {held:?}, no {floor_m}; columna {:?}; vanos {:?}",
                            raster_at(bx, bz).column_at(bx, bz),
                            served
                                .carves_touching_chunk(chunk::Wg3ChunkCoord::containing(bx, bz))
                                .iter()
                                .filter(|k| (k.x_cm as f32) <= bx * 100.0
                                    && bx * 100.0 <= (k.x_cm + k.size_x_cm) as f32
                                    && (k.z_cm as f32) <= bz * 100.0
                                    && bz * 100.0 <= (k.z_cm + k.size_z_cm) as f32)
                                .collect::<Vec<_>>()
                        );
                    }
                }
            }
            println!(
                "[pozos] ({rx},{rz}) rejilla {}×{} en ({:.1},{:.1}) m, {depth_m} m de caída",
                c.nx,
                c.nz,
                c.x0_cm as f32 / 100.0,
                c.z0_cm as f32 / 100.0
            );
        }
    }
    assert!(
        clusters >= 1,
        "ninguna rejilla en cuatro regiones: o el sorteo no sale o `covers_rect` lo tira todo"
    );
    println!("[pozos] {clusters} rejillas, {pits} pozos en cuatro regiones");
}

/// Se mide con `floor_below` desde la altura de los ojos de la planta alta, en el centro de cada
/// espacio construido de arriba. Si hay agujero, el suelo que devuelve está una planta más abajo.
#[test]
fn a_hole_drops_you_a_whole_storey() {
    const EYE_M: f32 = 1.6;
    /// Cuánto tiene que bajar para contar como agujero de planta: una planta menos margen de losa.
    const MIN_DROP_M: f32 = 2.5;

    let m = real_manifest();
    let mut holes = 0usize;

    for (rx, rz) in AUDIT_REGIONS {
        let b = building_of(rx, rz);
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, _, _) = region.bounds();
        let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);

        let side = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        let mut rasters = Vec::with_capacity(side * side);
        for cz in 0..side {
            for cx in 0..side {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                rasters.push(chunk::build_chunk_raster_full(
                    &m,
                    &served.placements_touching_chunk(&m, coord),
                    &served.segments_touching_chunk(coord),
                    &served.carves_touching_chunk(coord),
                    &served.solids_touching_chunk(coord),
                    coord,
                ));
            }
        }
        let raster_at = |x: f32, z: f32| -> Option<&Wg3Raster> {
            let coord = chunk::Wg3ChunkCoord::containing(x, z);
            let (dx, dz) = (coord.x - base.x, coord.z - base.z);
            if dx < 0 || dz < 0 || dx as usize >= side || dz as usize >= side {
                return None;
            }
            rasters.get(dz as usize * side + dx as usize)
        };

        // Sólo las plantas ALTAS: la baja no tiene forjado que perforar.
        for storey in b.storeys.iter().skip(1) {
            for s in &storey.spaces {
                if !s.role.is_built() {
                    continue;
                }
                let (cx, cz) = s.rect.centre_m();
                let floor_m = s.floor_y_cm as f32 / 100.0;
                let Some(raster) = raster_at(cx, cz) else {
                    continue;
                };
                let Some(found) = raster.floor_below(cx, floor_m + EYE_M, cz) else {
                    continue;
                };
                let drop = floor_m - found;
                if drop >= MIN_DROP_M {
                    println!(
                        "[agujero] ({rx},{rz}) espacio de {:.0} m² — se cae {drop:.2} m, del suelo \
                         {floor_m:.2} al {found:.2}",
                        s.rect.area_m2()
                    );
                    holes += 1;
                }
            }
        }
    }

    // **El umbral no es cero, y esa es la diferencia entre un test y un adorno.** Verificado
    // desactivando `hole_carves`: quedan **1**, que es un pozo de escalera cuyo centro de espacio cae
    // encima. Con `> 0` la mutación pasaba. Ocho deja margen a que el sorteo se mueva y sigue estando
    // muy por debajo de los 15 que produce el emisor.
    assert!(
        holes >= 8,
        "sólo {holes} agujeros que bajen una planta en cuatro regiones, y sin emitir ninguno ya sale \
         1 por los pozos de escalera: o no se están emitiendo, o el vano se queda a medias y el techo \
         de abajo sigue ahí — un agujero por el que se ve y no se pasa"
    );
    println!("[agujero] {holes} agujeros que bajan una planta entera");
}

/// ADR-105 enmienda 6 — **por una ventana interior se VE y no se PASA, y por una rendija tampoco.**
///
/// Es la invariante que separa «hueco en la pared» de «puerta que no se dibuja»: el ráster tiene
/// que dejar el antepecho y el dintel macizos (el cuerpo de pie choca) y la banda de en medio
/// abierta (los ojos ven). Para la rendija sólo cuenta lo primero: es un corte por debajo de la
/// celda y el servidor puede abrir una celda entera o ninguna, pero en ningún caso un paso.
#[test]
fn windows_are_seen_through_and_not_walked_through() {
    const EYE_M: f32 = 1.55;
    let m = real_manifest();
    let mut windows = 0usize;
    let mut slits = 0usize;
    let mut grilles = 0usize;

    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, _, _) = region.bounds();
        let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);
        let side = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        for cz in 0..side {
            for cx in 0..side {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                let carves = served.carves_touching_chunk(coord);
                if !carves
                    .iter()
                    .any(|c| fill::is_window(c) || fill::is_slit(c))
                {
                    continue;
                }
                let solids = served.solids_touching_chunk(coord);
                let raster = chunk::build_chunk_raster_full(
                    &m,
                    &served.placements_touching_chunk(&m, coord),
                    &served.segments_touching_chunk(coord),
                    &carves,
                    &solids,
                    coord,
                );
                let (bx0, bz0, bx1, bz1) = coord.bounds();
                for c in &carves {
                    let is_window = fill::is_window(c);
                    // Enm. 12 — una ventana con REJILLA es pared para el ráster (cada barrote maciza
                    // su celda) y se ve a través sólo en el cliente: no se le exige lo segundo.
                    let barred = solids.iter().any(|s| {
                        fill::is_grille_bar(s)
                            && s.x_cm < c.x_cm + c.size_x_cm
                            && s.x_cm + s.size_x_cm > c.x_cm
                            && s.z_cm < c.z_cm + c.size_z_cm
                            && s.z_cm + s.size_z_cm > c.z_cm
                            && s.bottom_y_cm == c.bottom_y_cm
                    });
                    if !is_window && !fill::is_slit(c) {
                        continue;
                    }
                    let (x, z) = (
                        (c.x_cm as f32 + c.size_x_cm as f32 * 0.5) / 100.0,
                        (c.z_cm as f32 + c.size_z_cm as f32 * 0.5) / 100.0,
                    );
                    if x < bx0 || x >= bx1 || z < bz0 || z >= bz1 {
                        continue;
                    }
                    let floor = if is_window {
                        (c.bottom_y_cm - fill::WINDOW_SILL_CM) as f32 / 100.0
                    } else {
                        (c.bottom_y_cm - fill::SLIT_BOTTOM_CM) as f32 / 100.0
                    };
                    assert!(
                        raster.blocked_standing_at(x, floor, z, 1.8),
                        "el hueco de ({x:.2}, {z:.2}) deja pasar un cuerpo de pie: eso no es una \
                         ventana, es una puerta que no se dibuja"
                    );
                    if is_window {
                        windows += 1;
                        assert!(
                            raster.is_solid_at(x, floor + 0.5, z),
                            "la ventana de ({x:.2}, {z:.2}) no tiene antepecho en el ráster"
                        );
                        if barred {
                            grilles += 1;
                        } else {
                            assert!(
                                !raster.is_solid_at(x, floor + EYE_M, z),
                                "por la ventana de ({x:.2}, {z:.2}) no se ve: el ráster sigue \
                                 macizo a la altura de los ojos"
                            );
                        }
                    } else {
                        slits += 1;
                    }
                }
            }
        }
    }
    assert!(
        windows >= 20,
        "sólo {windows} ventanas en cuatro regiones: o no se emiten, o el vano no llega al ráster"
    );
    println!(
        "[ventana] {windows} ventanas ({grilles} con rejilla) y {slits} rendijas verificadas en el \
         ráster"
    );
}

/// Sonda: vuelca regiones de la semilla servida a JSON para el visor de planta
/// (`docs/web/wg3-planta.html`). `WG3_DUMP_REGIONS="0,0;1,0"`, `WG3_DUMP_OUT=ruta.json`. Cada macizo
/// y cada vano van etiquetados por su FORMA con las mismas reglas que usan los tests, y con `new`
/// cuando la forma nació en las enmiendas 8–13 de ADR-105.
#[test]
#[ignore]
fn probe_dump_regions_json() {
    use serde_json::json;
    let spec = std::env::var("WG3_DUMP_REGIONS").unwrap_or_else(|_| "0,0".into());
    let out_path = std::env::var("WG3_DUMP_OUT").unwrap_or_else(|_| "wg3_dump.json".into());
    let m = real_manifest();
    let mut regions = Vec::new();
    for pair in spec.split(';') {
        let mut it = pair.split(',').map(|v| v.trim().parse::<i32>().unwrap());
        let (rx, rz) = (it.next().unwrap(), it.next().unwrap());
        let b = building_of(rx, rz);
        let f = fill::fill_building(&b, &m);
        let floors: Vec<i32> = b
            .storeys
            .iter()
            .flat_map(|st| st.spaces.iter().map(|s| s.floor_y_cm))
            .collect();
        let spaces: Vec<_> = b
            .storeys
            .iter()
            .enumerate()
            .flat_map(|(n, st)| {
                st.spaces.iter().filter(|s| s.role.is_built()).map(move |s| {
                    let character = match fill::knobs_of(b.seed, s).character {
                        fill::Character::Open => "abierto",
                        fill::Character::Office => "oficina",
                        fill::Character::Hall => "nave",
                        fill::Character::Maze => "laberinto",
                        fill::Character::Weird => "raro",
                    };
                    json!({
                        "storey": n,
                        "role": s.role.name(),
                        "character": character,
                        "floor": s.floor_y_cm,
                        "clear": fill::clear_height_cm(s),
                        "parts": s.parts().iter().map(|p| [p.min_x_cm, p.min_z_cm, p.max_x_cm, p.max_z_cm]).collect::<Vec<_>>(),
                    })
                })
            })
            .collect();
        let props: Vec<_> = f
            .props
            .iter()
            .map(|p| json!({"x": p.x_cm, "z": p.z_cm, "y": p.y_cm, "yaw": p.yaw_deg, "kind": p.kind, "style": p.style}))
            .collect();
        let segments: Vec<_> = f
            .segments
            .iter()
            .map(|s| {
                json!({
                    "r": [s.x_cm, s.z_cm, s.x_cm + s.size_x_cm, s.z_cm + s.size_z_cm],
                    "floor": s.floor_y_cm, "h": s.height_cm, "style": s.style,
                    "openings": s.openings.iter().map(|o| [o.side as i32, o.offset_cm, o.width_cm]).collect::<Vec<_>>(),
                })
            })
            .collect();
        let solids: Vec<_> = f
            .solids
            .iter()
            .map(|s| {
                let thin = s.size_x_cm.min(s.size_z_cm);
                let long = s.size_x_cm.max(s.size_z_cm);
                let h = s.top_y_cm - s.bottom_y_cm;
                let standing = floors.contains(&s.bottom_y_cm);
                let (kind, new) = if fill::is_fallen_plate(s) {
                    // ADR-105 enm. 20 — van ANTES que el marco: las dos son decoración, y la
                    // decoración se clasifica por su forma como todo lo demás.
                    ("placa caída", true)
                } else if fill::is_raised_floor_tile(s) {
                    ("baldosa levantada", true)
                } else if s.is_decoration() {
                    ("marco", true)
                } else if s.shape == segment::SHAPE_ARCH {
                    ("arco liso", true)
                } else if fill::is_round_pilaster(s) {
                    ("media luna", true)
                } else if fill::is_pillar(s) {
                    (
                        match (s.shape, s.size_x_cm == s.size_z_cm) {
                            (segment::SHAPE_CYLINDER, _) => "cilindro",
                            (segment::SHAPE_OCTAGON, _) => "octógono",
                            (_, true) => "pilar",
                            (_, false) => "cruz",
                        },
                        s.shape != segment::SHAPE_BOX,
                    )
                } else if fill::is_pilaster(s) {
                    ("pilastra", true)
                } else if fill::is_beam(s) {
                    ("viga", false)
                } else if fill::is_platform(s) {
                    ("tarima", true)
                } else if fill::is_grille_bar(s) {
                    ("rejilla", true)
                } else if fill::is_hung_band(s) {
                    ("arcada/bóveda", true)
                } else if fill::is_cubicle_wall(s) {
                    ("mampara de cubículo", true)
                } else if thin == 15 {
                    (
                        if h == fill::ARCH_BAND_CM {
                            "arco"
                        } else {
                            "dintel/faldón"
                        },
                        false,
                    )
                } else if thin == 20 && h <= 200 {
                    ("pretil", false)
                } else if thin == 30 {
                    (
                        if !standing {
                            "medio muro colgado"
                        } else if h == 110 {
                            "medio muro bajo"
                        } else if h == 230 {
                            "mampara"
                        } else {
                            "tabique"
                        },
                        !standing || h == 110,
                    )
                } else if thin == 35 && s.size_x_cm == s.size_z_cm && h > 200 {
                    ("parteluz", true)
                } else if thin == 35 && h == 30 {
                    ("vigueta", true)
                } else if thin == 10 && h == 10 {
                    ("cornisa", true)
                } else if thin == 60 && h == 50 && long > 400 {
                    ("descuelgue", true)
                } else if h == 30 && thin >= 260 && s.size_x_cm == s.size_z_cm {
                    (if standing { "zapata" } else { "capitel" }, true)
                } else {
                    ("otro", false)
                };
                json!({
                    "r": [s.x_cm, s.z_cm, s.x_cm + s.size_x_cm, s.z_cm + s.size_z_cm],
                    "y": [s.bottom_y_cm, s.top_y_cm], "kind": kind, "new": new, "style": s.style,
                    "shape": s.shape, "yaw": s.yaw_deg,
                })
            })
            .collect();
        let carves: Vec<_> = f
            .carves
            .iter()
            .map(|c| {
                let thin = c.size_x_cm.min(c.size_z_cm);
                let h = c.top_y_cm - c.bottom_y_cm;
                let (kind, new) = if fill::is_slit(c) {
                    ("rendija", false)
                } else if fill::is_window(c) {
                    ("ventana", false)
                } else if thin == 10 {
                    ("hornacina", true)
                } else if c.bottom_y_cm < 0
                    || floors
                        .iter()
                        .any(|&fl| c.bottom_y_cm < fl && c.top_y_cm > fl && fl > 0)
                {
                    ("agujero", false)
                } else if h >= 200 {
                    ("puerta", false)
                } else {
                    ("otro", false)
                };
                json!({
                    "r": [c.x_cm, c.z_cm, c.x_cm + c.size_x_cm, c.z_cm + c.size_z_cm],
                    "y": [c.bottom_y_cm, c.top_y_cm], "kind": kind, "new": new,
                })
            })
            .collect();
        let (min_x, min_z, max_x, max_z) = Wg3RegionCoord { x: rx, z: rz }.bounds();
        regions.push(json!({
            "region": [rx, rz],
            "bounds": [(min_x * 100.0) as i32, (min_z * 100.0) as i32, (max_x * 100.0) as i32, (max_z * 100.0) as i32],
            "storeys": b.storeys.len(),
            "props": props,
            "spaces": spaces, "segments": segments, "solids": solids, "carves": carves,
        }));
    }
    let doc = json!({ "seed": format!("{SERVED_SEED:#x}"), "regions": regions });
    std::fs::write(&out_path, serde_json::to_string(&doc).unwrap()).unwrap();
    println!(
        "[dump] {} regiones en {out_path}",
        doc["regions"].as_array().unwrap().len()
    );
}

/// Sonda: qué macizos tocan una columna del mundo servido. `WG3_PROBE_COLUMN="x,z"` en metros.
#[test]
#[ignore]
fn probe_solids_at_column() {
    let spec = std::env::var("WG3_PROBE_COLUMN").unwrap_or_else(|_| "128.4,211.78".into());
    let mut it = spec.split(',').map(|v| v.trim().parse::<f32>().unwrap());
    let (x, z) = (it.next().unwrap(), it.next().unwrap());
    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, max_x, max_z) = region.bounds();
        if x < min_x || x >= max_x || z < min_z || z >= max_z {
            continue;
        }
        let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);
        let coord = chunk::Wg3ChunkCoord::containing(x, z);
        for s in served.solids_touching_chunk(coord) {
            let (x0, z0, x1, z1) = s.bounds();
            if x >= x0 && x <= x1 && z >= z0 && z <= z1 {
                println!("[columna] macizo {s:?}");
            }
        }
        for c in served.carves_touching_chunk(coord) {
            let (cx0, cz0) = (c.x_cm as f32 / 100.0, c.z_cm as f32 / 100.0);
            let (cx1, cz1) = (
                (c.x_cm + c.size_x_cm) as f32 / 100.0,
                (c.z_cm + c.size_z_cm) as f32 / 100.0,
            );
            if x >= cx0 && x <= cx1 && z >= cz0 && z <= cz1 {
                println!("[columna] vano {c:?}");
            }
        }
        for seg in served.segments_touching_chunk(coord) {
            if x >= seg.min_x()
                && x <= seg.min_x() + seg.size_x()
                && z >= seg.min_z()
                && z <= seg.min_z() + seg.size_z()
            {
                println!(
                    "[columna] tramo ({},{}) {}x{} suelo {} alto {} estilo {}",
                    seg.x_cm,
                    seg.z_cm,
                    seg.size_x_cm,
                    seg.size_z_cm,
                    seg.floor_y_cm,
                    seg.height_cm,
                    seg.style
                );
            }
        }
    }
}

/// ADR-105 verificaciones (a) y (b) — **todo macizo emitido EXISTE en el ráster, y ningún vano se lo
/// come.**
///
/// Las dos con una sola medida, y por eso está escrito así: si los vanos se aplicaran después de los
/// macizos —el orden ingenuo—, el pretil de un atrio desaparecería, porque el vano de atrio de
/// ADR-104 cubre su huella ensanchada medio metro, o sea exactamente el borde donde va. **El síntoma
/// sería «el pretil no sale», sin un solo error en ninguna parte.** Aquí sale como un macizo que se
/// emitió y que el ráster no tiene.
///
/// Y se comprueba además que un pretil **no llega al techo**: una barandilla que tapa es una pared, y
/// entonces el atrio vuelve a ser el pozo sellado que ADR-104 D3 quitó.
#[test]
fn every_solid_survives_into_the_raster() {
    let m = real_manifest();
    let mut checked = 0usize;
    let mut parapets = 0usize;
    let mut pillars = 0usize;
    let mut aprons = 0usize;
    let mut beams = 0usize;
    let mut arches = 0usize;
    let mut smooth_arches = 0usize;
    let mut casings = 0usize;
    let mut lows = 0usize;

    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, _, _) = region.bounds();
        let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);

        let side = REGION_CHUNKS as usize;
        let base = chunk::Wg3ChunkCoord::containing(min_x + 1.0, min_z + 1.0);
        for cz in 0..side {
            for cx in 0..side {
                let coord = chunk::Wg3ChunkCoord {
                    x: base.x + cx as i32,
                    z: base.z + cz as i32,
                };
                let solids = served.solids_touching_chunk(coord);
                if solids.is_empty() {
                    continue;
                }
                let raster = chunk::build_chunk_raster_full(
                    &m,
                    &served.placements_touching_chunk(&m, coord),
                    &served.segments_touching_chunk(coord),
                    &served.carves_touching_chunk(coord),
                    &solids,
                    coord,
                );

                for s in &solids {
                    let (cx_m, cz_m) = s.centre();
                    // Sólo los que caen DENTRO de este chunk se pueden preguntar a su ráster: uno que
                    // lo toca por el borde tiene su centro en el vecino.
                    let (bx0, bz0, bx1, bz1) = coord.bounds();
                    if cx_m < bx0 || cx_m >= bx1 || cz_m < bz0 || cz_m >= bz1 {
                        continue;
                    }
                    // ADR-125 enm. 2 — la decoración no se estampa: un marco no está en el ráster
                    // por definición.
                    if s.is_decoration() {
                        casings += 1;
                        continue;
                    }
                    // ADR-125 enm. 1 — el arco liso es hueco en su centro geométrico (ahí está
                    // la luz de la puerta): se comprueba la CLAVE, tres centímetros bajo el remate,
                    // y que bajo el arranque la puerta sigue abierta.
                    if s.shape == segment::SHAPE_ARCH {
                        let key = s.top_y_cm as f32 / 100.0 - 0.03;
                        assert!(
                            raster.is_solid_at(cx_m, key, cz_m),
                            "el arco de ({cx_m:.2}, {cz_m:.2}) no tiene clave en el ráster"
                        );
                        let under = s.bottom_y_cm as f32 / 100.0 - 0.05;
                        assert!(
                            !raster.is_solid_at(cx_m, under, cz_m),
                            "el arco de ({cx_m:.2}, {cz_m:.2}) cierra la puerta bajo el arranque"
                        );
                        smooth_arches += 1;
                        checked += 1;
                        continue;
                    }
                    let mid = (s.bottom_y_cm + s.top_y_cm) as f32 / 200.0;
                    assert!(
                        raster.is_solid_at(cx_m, mid, cz_m),
                        "el macizo de ({cx_m:.2}, {cz_m:.2}) entre {} y {} cm se emitió y el ráster \
                         no lo tiene: o no se estampa, o un vano se lo ha comido — que es el orden \
                         invertido de ADR-105 D2",
                        s.bottom_y_cm,
                        s.top_y_cm
                    );
                    checked += 1;

                    // Un pretil es bajo y largo; un pilar, alto y cuadrado. No hace falta un campo
                    // para distinguirlos: la forma ya lo dice, y así el dato no puede mentir.
                    //
                    // **Y desde los techos por espacio hay un tercero que también es bajo: el FALDÓN
                    // del vano** (`fill::ceiling_aprons`). No es una barandilla y por encima de él hay
                    // que ver TAPADO, no despejado: es la pared que le falta a la boca del lado alto.
                    // Se distingue por el grosor, que es exactamente el de una pared — un pretil mide
                    // `PARAPET_T_CM` (20) y una partición `PARTITION_T_CM` (30). La forma sigue
                    // diciendo qué es cada macizo; lo que hacía falta era una forma más.
                    let h = s.top_y_cm - s.bottom_y_cm;
                    // **Y desde ADR-105 enm. 5 hay macizos que CUELGAN: las vigas.** Un pretil apoya
                    // en la losa —seis centímetros bajo su cara inferior hay materia—; una viga cuelga
                    // de la de techo y debajo tiene aire. Por encima de una viga hay que ver TAPADO
                    // (es el techo), así que la comprobación del pretil no le aplica.
                    let hanging =
                        !raster.is_solid_at(cx_m, s.bottom_y_cm as f32 / 100.0 - 0.06, cz_m);
                    if s.size_x_cm.min(s.size_z_cm) == fill::WALL_T_CM {
                        // Grosor de pared: faldón, dintel o hilada de arco (enm. 7, media celda
                        // de alto exacta). Los tres cuelgan sobre una boca y por encima hay pared.
                        if h == fill::ARCH_BAND_CM {
                            arches += 1;
                        } else {
                            aprons += 1;
                        }
                    } else if fill::is_beam(s) || hanging {
                        // Por la forma primero: una viga que pasa sobre una isla o un pilar tiene
                        // materia debajo y «apoya» sin dejar de ser viga. Medido: 9 de 527 en las
                        // cuatro regiones de referencia.
                        beams += 1;
                    } else if s.size_x_cm.min(s.size_z_cm) == 20 && h <= 200 {
                        // Grosor de PRETIL (`PARAPET_T_CM`). Sólo a él se le exige ver por encima:
                        // un medio muro de sala (enm. 8, grosor 30) puede compartir celda de ráster
                        // con un pilar o un tabique vecino, y esa columna sube hasta el techo sin
                        // que el medio muro tenga nada encima — medido en (128.40, 211.78).
                        parapets += 1;
                        // Por encima del pretil hay que VER. Medio metro más arriba de su remate.
                        let over = s.top_y_cm as f32 / 100.0 + 0.5;
                        assert!(
                            !raster.is_solid_at(cx_m, over, cz_m),
                            "el pretil de ({cx_m:.2}, {cz_m:.2}) sigue siendo macizo {over:.2} m \
                             arriba: eso no es una barandilla, es una pared, y el atrio vuelve a \
                             estar sellado"
                        );
                    } else if h <= 200 {
                        lows += 1;
                    } else {
                        pillars += 1;
                    }
                }
            }
        }
    }

    assert!(
        checked > 0,
        "ninguna región emitió un macizo, así que este test no ha probado nada"
    );
    // Y los faldones tienen que EXISTIR: si salieran cero, la comprobación de arriba estaría
    // pasando por no mirar ninguno.
    assert!(
        aprons > 0,
        "ningún faldón de vano en las regiones auditadas: o no se emiten, o el ráster no los tiene"
    );
    // Y las vigas: si salieran cero, la clase «cuelga» no habría probado nada.
    assert!(
        beams > 0,
        "ninguna viga en las regiones auditadas: o no se emiten, o el ráster no las tiene"
    );
    println!(
        "[macizo] {checked} verificados en el ráster: {parapets} pretiles, {pillars} pilares, \
         {aprons} faldones y dinteles, {beams} vigas, {arches} hiladas de arco, {smooth_arches} \
         arcos lisos, {lows} medios muros; {casings} piezas de marco fuera del ráster"
    );
}

/// ADR-121 D4 / ADR-125 — **todo macizo servido pasa sus propias comprobaciones, y las formas
/// nuevas EXISTEN.** Un giro fuera de paso o una media luna con el fondo mal medido cruzarían el
/// cable sin error y el cliente dibujaría otra cosa que la que el ráster frena.
#[test]
fn every_served_solid_is_well_formed_and_the_round_ones_exist() {
    let m = real_manifest();
    let mut checked = 0usize;
    let mut by_shape = [0usize; 5];
    let mut rotated = 0usize;
    let mut decorations = 0usize;
    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let inside = super::validate::region_inside(&m, SERVED_SEED, region);
        for s in &inside.filled.solids {
            let problems = s.problems();
            assert!(
                problems.is_empty(),
                "región ({rx},{rz}): macizo en ({},{}) {}×{} forma {} giro {}: {problems:?}",
                s.x_cm,
                s.z_cm,
                s.size_x_cm,
                s.size_z_cm,
                s.shape,
                s.yaw_deg
            );
            checked += 1;
            by_shape[s.shape as usize] += 1;
            if s.is_decoration() {
                decorations += 1;
            }
            if s.yaw_deg != 0 {
                rotated += 1;
            }
        }
    }
    assert!(checked > 0, "ninguna región emitió un macizo");
    assert!(
        decorations > 0,
        "ningún macizo decorativo: los marcos de puerta no se emiten"
    );
    assert!(
        by_shape[1] > 0 && by_shape[2] > 0 && by_shape[3] > 0 && by_shape[4] > 0,
        "faltan formas: cajas {} cilindros {} medias lunas {} octógonos {} arcos {}",
        by_shape[0],
        by_shape[1],
        by_shape[2],
        by_shape[3],
        by_shape[4]
    );
    assert!(
        rotated > 0,
        "ningún macizo girado: el giro no cruza el cable"
    );
    println!(
        "[formas] {checked} macizos: {} cajas, {} cilindros, {} medias lunas, {} octógonos, {} \
         arcos lisos, {} girados",
        by_shape[0], by_shape[1], by_shape[2], by_shape[3], by_shape[4], rotated
    );
}

/// ADR-125 — **el ráster estampa el DISCO, no la caja.** Es la mitad servidor del contrato: el
/// cliente dibuja un cilindro y por sus esquinas se pasa; si el ráster macizara la envolvente,
/// el servidor frenaría donde el cliente deja pasar (regla R6, el tirón).
#[test]
fn round_solids_stamp_as_discs_not_boxes() {
    use super::segment::{
        Wg3Solid, SHAPE_ARCH, SHAPE_CYLINDER, SHAPE_HALF_CYLINDER, SHAPE_OCTAGON,
    };

    let stamp = |s: Wg3Solid| {
        let mut b = Wg3RasterBuilder::new(0, 0, 40, 40);
        b.add_solid(&s);
        b.finish()
    };
    // Cilindro de 4 m centrado en (10, 10): el centro es macizo, la esquina de su caja no.
    let cyl = stamp(Wg3Solid {
        x_cm: 800,
        z_cm: 800,
        size_x_cm: 400,
        size_z_cm: 400,
        bottom_y_cm: 0,
        top_y_cm: 300,
        style: 3,
        yaw_deg: 0,
        shape: SHAPE_CYLINDER,
    });
    assert!(
        cyl.is_solid_at(10.0, 1.5, 10.0),
        "el centro del cilindro no es macizo"
    );
    assert!(
        cyl.is_solid_at(11.9, 1.5, 10.0),
        "el borde del cilindro no es macizo"
    );
    assert!(
        !cyl.is_solid_at(8.25, 1.5, 8.25),
        "la esquina de la caja del cilindro es maciza: se estampó la envolvente"
    );
    // Octógono de 6 m: mismo centro, la esquina de la caja libre. Seis y no cuatro porque se
    // estampa por el círculo CIRCUNSCRITO (1,082 apotemas) y con el ráster conservador la celda de
    // esquina de un octógono de 4 m sigue tocándolo (2,12 < 2,165); a 6 m ya no (3,54 > 3,25).
    let oct = stamp(Wg3Solid {
        x_cm: 700,
        z_cm: 700,
        size_x_cm: 600,
        size_z_cm: 600,
        bottom_y_cm: 0,
        top_y_cm: 300,
        style: 3,
        yaw_deg: 0,
        shape: SHAPE_OCTAGON,
    });
    assert!(oct.is_solid_at(10.0, 1.5, 10.0));
    assert!(
        !oct.is_solid_at(7.1, 1.5, 7.1),
        "la esquina del octógono es maciza"
    );
    // Media luna de 2 m de cuerda contra la pared de z mínima (giro 0): panza hacia +z. Delante
    // de la cara plana (z menor) nada; en la panza, macizo.
    let half = stamp(Wg3Solid {
        x_cm: 900,
        z_cm: 1000,
        size_x_cm: 200,
        size_z_cm: 100,
        bottom_y_cm: 0,
        top_y_cm: 300,
        style: 3,
        yaw_deg: 0,
        shape: SHAPE_HALF_CYLINDER,
    });
    assert!(
        half.is_solid_at(10.0, 1.5, 10.25),
        "la panza de la media luna no es maciza"
    );
    // A 9,4 y no a 9,6: la celda 9,5..10,0 TOCA la cara plana y el ráster conservador la maciza
    // igual que hace `add_box` con una pared apoyada en la línea de una celda.
    assert!(
        !half.is_solid_at(10.0, 1.5, 9.4),
        "hay macizo detrás de la cara plana de la media luna"
    );
    // Arco de puerta de 1,20 de cuerda a lo largo de x, banda 190..240 (clave a 230): en el
    // centro, libre a 2,00 y macizo a 2,35; en la jamba, macizo desde el arranque.
    let arch = stamp(Wg3Solid {
        x_cm: 940,
        z_cm: 1000,
        size_x_cm: 120,
        size_z_cm: 15,
        bottom_y_cm: 190,
        top_y_cm: 240,
        style: 3,
        yaw_deg: 0,
        shape: SHAPE_ARCH,
    });
    assert!(
        !arch.is_solid_at(10.0, 2.0, 10.05),
        "el arco cierra el centro de la puerta a 2,00"
    );
    assert!(
        arch.is_solid_at(10.0, 2.35, 10.05),
        "el arco no tiene clave"
    );
    assert!(
        arch.is_solid_at(9.45, 1.95, 10.05),
        "la jamba del arco no arranca a 1,90"
    );
    assert!(
        !arch.is_solid_at(10.0, 1.85, 10.05),
        "hay macizo bajo el arranque del arco"
    );
    // Y la DECORACIÓN no estampa nada: una jamba de marco de suelo a dintel dentro de la luz de
    // la puerta deja la puerta abierta (ADR-125 enm. 2).
    let casing = stamp(Wg3Solid {
        x_cm: 939,
        z_cm: 983,
        size_x_cm: 10,
        size_z_cm: 34,
        bottom_y_cm: 0,
        top_y_cm: 191,
        style: 3 | segment::STYLE_DECOR_BIT,
        yaw_deg: 0,
        shape: segment::SHAPE_BOX,
    });
    assert!(
        !casing.is_solid_at(9.44, 1.0, 10.0),
        "una jamba decorativa se ha estampado: cierra la puerta"
    );

    // Y girada 90: la panza va hacia +x, la cara plana queda en x mínima.
    let turned = stamp(Wg3Solid {
        x_cm: 900,
        z_cm: 1000,
        size_x_cm: 200,
        size_z_cm: 100,
        bottom_y_cm: 0,
        top_y_cm: 300,
        style: 3,
        yaw_deg: 90,
        shape: SHAPE_HALF_CYLINDER,
    });
    assert!(
        turned.is_solid_at(10.25, 1.5, 10.5),
        "la panza girada no es maciza"
    );
    // Girada, la cara plana queda en x = 9,5 (el disco se centra en el medio de la cara plana,
    // que el giro lleva a −x): la celda 9,0..9,5 la toca, la 8,5..9,0 no.
    assert!(
        !turned.is_solid_at(8.9, 1.5, 10.5),
        "hay macizo detrás de la cara plana girada"
    );
}

/// Cuántos macizos emite el plan, por región y por tipo. Sin afirmar nada: es la cifra que dice si
/// los pretiles cubren los bordes que deberían o si la comprobación de «hay suelo al lado» está
/// descartando de más.
/// Cubículos (2026-09-06): dónde están los puestos del mundo SERVIDO, para las capturas. Imprime
/// hasta ocho mesas de cubículo por región con la cota de su suelo y el giro de la mesa.
#[test]
#[ignore]
fn probe_cubicle_spots() {
    let m = real_manifest();
    for (rx, rz) in [(0, 0), (1, 0), (0, 1), (-1, 2)] {
        let b = served_building_of(rx, rz);
        let f = fill::fill_building(&b, &m);
        let walls: Vec<&segment::Wg3Solid> = f
            .solids
            .iter()
            .filter(|s| fill::is_cubicle_wall(s))
            .collect();
        let desks: Vec<&segment::Wg3Prop> = f
            .props
            .iter()
            .filter(|p| {
                p.kind == segment::PROP_DESK
                    && walls
                        .iter()
                        .any(|w| (w.x_cm - p.x_cm).abs() < 300 && (w.z_cm - p.z_cm).abs() < 300)
            })
            .collect();
        println!(
            "[cubículos] ({rx},{rz}) — {} mamparas, {} puestos",
            walls.len(),
            desks.len()
        );
        for p in desks.iter().take(8) {
            println!(
                "  mesa en ({:.2}, {:.2}, {:.2}) yaw {}",
                p.x_cm as f32 / 100.0,
                p.y_cm as f32 / 100.0,
                p.z_cm as f32 / 100.0,
                p.yaw_deg
            );
        }
    }
}

/// Deterioro del falso techo (ADR-105 enm. 20): dónde mirar en el mundo SERVIDO. Imprime, por
/// planta, cuántas placas caídas y baldosas levantadas hay y las primeras posiciones, que es lo que
/// hace falta para apuntar la cámara de una captura.
#[test]
#[ignore]
fn probe_decay_spots() {
    let m = real_manifest();
    for (rx, rz) in [(0, 0), (1, 0), (0, 1)] {
        let b = served_building_of(rx, rz);
        let f = fill::fill_building(&b, &m);
        for (n, st) in b.storeys.iter().enumerate() {
            let Some(floor) = st.spaces.first().map(|s| s.floor_y_cm) else {
                continue;
            };
            let plates = f
                .solids
                .iter()
                .filter(|s| fill::is_fallen_plate(s) && s.bottom_y_cm == floor)
                .count();
            let tiles = f
                .solids
                .iter()
                .filter(|s| fill::is_raised_floor_tile(s) && s.bottom_y_cm == floor)
                .count();
            let hung: Vec<&segment::Wg3Prop> = f
                .props
                .iter()
                .filter(|p| {
                    (p.kind == segment::PROP_CEILING_TILE_HUNG
                        || p.kind == segment::PROP_LIGHT_HUNG)
                        && p.y_cm > floor
                        && p.y_cm < floor + 500
                })
                .collect();
            let breaches = f
                .carves
                .iter()
                .filter(|c| fill::is_breach(c) && c.bottom_y_cm - 30 == floor)
                .count();
            if plates + tiles + hung.len() + breaches == 0 {
                continue;
            }
            println!(
                "[deterioro] ({rx},{rz}) planta {n} (suelo {:.2} m, calle en la {}) — {plates} placas caídas, {tiles} baldosas, {} colgando, {breaches} boquetes",
                floor as f32 / 100.0,
                b.ground,
                hung.len()
            );
            for s in f
                .solids
                .iter()
                .filter(|s| fill::is_fallen_plate(s) && s.bottom_y_cm == floor)
                .take(4)
            {
                println!(
                    "  placa en ({:.2}, {:.2}, {:.2})",
                    s.x_cm as f32 / 100.0,
                    s.bottom_y_cm as f32 / 100.0,
                    s.z_cm as f32 / 100.0
                );
            }
            for p in hung.iter().take(3) {
                println!(
                    "  colgando kind {} en ({:.2}, {:.2}, {:.2}) yaw {}",
                    p.kind,
                    p.x_cm as f32 / 100.0,
                    p.y_cm as f32 / 100.0,
                    p.z_cm as f32 / 100.0,
                    p.yaw_deg
                );
            }
        }
    }
}

/// Carteles (ADR-129 enm. 1): dónde ponerse para VER uno, en el mundo servido. Imprime, por
/// familia, el punto a 1,5 m delante del cartel y el giro con el que hay que mirarlo — que es lo
/// que come el arnés de capturas (`Temp/claude_capture.json`), no la posición del cartel.
#[test]
#[ignore]
fn probe_sign_spots() {
    let m = real_manifest();
    for (rx, rz) in [(0, 0), (1, 0)] {
        let b = served_building_of(rx, rz);
        let f = fill::fill_building(&b, &m);
        let signs: Vec<&segment::Wg3Prop> = f
            .props
            .iter()
            .filter(|p| p.kind == segment::PROP_SIGN)
            .collect();
        println!("[carteles] ({rx},{rz}) — {} anclas", signs.len());
        for (name, lo, hi) in [
            ("placa", segment::SIGN_DOOR_PLATE, segment::SIGN_CUBICLE_TAG),
            ("rótulo", segment::SIGN_CUBICLE_TAG, segment::SIGN_EXIT),
            ("salida", segment::SIGN_EXIT, segment::SIGN_CORKBOARD),
            ("tablón", segment::SIGN_CORKBOARD, segment::SIGN_CALENDAR),
            (
                "calendario",
                segment::SIGN_CALENDAR,
                segment::SIGN_CALENDAR + segment::SIGN_CALENDAR_N,
            ),
        ] {
            // De la planta baja: es la que se anda sin escalera en una captura.
            let pick = signs.iter().find(|p| {
                let v = p.style % segment::SIGN_GARBLED_BASE;
                v >= lo && v < hi && p.y_cm > 100 && p.y_cm < 260
            });
            let Some(p) = pick else {
                println!("  {name}: ninguno");
                continue;
            };
            // La normal del cartel por su giro, y el ojo a 1,5 m delante mirando de vuelta.
            let (nx, nz) = match p.yaw_deg {
                0 => (0.0f32, 1.0f32),
                90 => (1.0, 0.0),
                180 => (0.0, -1.0),
                _ => (-1.0, 0.0),
            };
            let (ex, ez) = (
                p.x_cm as f32 / 100.0 + nx * 1.5,
                p.z_cm as f32 / 100.0 + nz * 1.5,
            );
            // El arnés quiere la cota del SUELO; el cartel cuelga a su altura sobre ella.
            let floor = f
                .segments
                .iter()
                .filter(|g| {
                    (g.floor_y_cm as f32) < p.y_cm as f32
                        && p.x_cm > g.x_cm
                        && p.x_cm < g.x_cm + g.size_x_cm
                        && p.z_cm > g.z_cm
                        && p.z_cm < g.z_cm + g.size_z_cm
                })
                .map(|g| g.floor_y_cm)
                .max()
                .unwrap_or(0);
            println!(
                "  {name} v{} en ({:.2}, {:.2}, {:.2}) yaw {} → ojo en ({:.2}, {:.2}, {:.2}) \
                 mirando yaw {}",
                p.style,
                p.x_cm as f32 / 100.0,
                p.y_cm as f32 / 100.0,
                p.z_cm as f32 / 100.0,
                p.yaw_deg,
                ex,
                floor as f32 / 100.0,
                ez,
                (p.yaw_deg + 180) % 360
            );
        }
    }
}

#[test]
#[ignore]
fn probe_how_many_solids() {
    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        let b = building_of(rx, rz);
        let f = fill::fill_building(&b, &m);
        let parapets = f
            .solids
            .iter()
            .filter(|s| s.top_y_cm - s.bottom_y_cm <= 200)
            .count();
        let pillars = f.solids.len() - parapets;
        let atria = b
            .storeys
            .iter()
            .flat_map(|p| p.spaces.iter())
            .filter(|s| s.void_above && s.role == SpaceRole::Hall)
            .count();
        println!(
            "[macizo] ({rx},{rz}) — {} macizos: {parapets} pretiles y {pillars} pilares sobre \
             {atria} atrios",
            f.solids.len()
        );
    }
}

/// ADR-106 verificaciones (a) y (b) — **en la planta alta ya NO se bloquea, y el suelo que se
/// encuentra es el suyo.**
///
/// Éste es el fallo que el ADR existe para quitar, y hoy es reproducible: la capa salía de la Y
/// (`layer_from_player_y`, capas de 4 m), `update_ownership` sólo generaba la capa 0, un chunk ausente
/// bloquea, y el resultado era `Blocked` → `position = from`. **Subir una planta te congelaba.**
///
/// Se mide contra el ráster de WG3 —el que este ADR pone a mandar— y no contra la rejilla de WG2, que
/// es la que no sabe que hay una planta arriba.
#[test]
fn on_the_upper_storey_wg3_does_not_freeze_you() {
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::wg3::world::Wg3WorldCache;
    use crate::world::Vec3;

    /// Cota a la que reporta el transform de un jugador de pie sobre un suelo a cero.
    const PLAYER_BASE_Y: f32 = 1.8;
    const STOREY_M: f32 = 3.32;

    let m = real_manifest();
    let mut checked = 0usize;

    for (rx, rz) in AUDIT_REGIONS {
        // **El edificio SERVIDO, no el de dos plantas del test** (auditoría 2026-09-02). El ráster
        // contra el que se pregunta es el de `plan_region`, que levanta `REGION_STOREYS`; con un
        // edificio de dos plantas aquí, una sala de la planta 1 podía ser en el mundo servido el
        // pozo de la escalera a la planta 2, y el test medía dos mundos distintos.
        let b = served_building_of(rx, rz);
        // La planta ALTA: es la que congelaba. Sin ella no hay nada que probar.
        let Some(upper) = b.storeys.get(b.ground + 1) else {
            continue;
        };
        let mut worlds = Wg3WorldCache::default();
        let mut cache = Wg3CollisionCache::new();

        for space in upper.spaces.iter().filter(|s| {
            s.role.is_built()
                && s.rise_cm == 0
                && s.rect.width_cm() > 600
                && s.rect.depth_cm() > 600
        }) {
            // FUERA del centro: ADR-104 D4 abre el agujero de forjado justo en el centro de la
            // sala, y caer por él es geometría correcta, no un fallo del ráster. Metro y medio a un
            // lado sigue a más de un metro de cualquier pared en una sala de más de seis.
            let (cx, cz) = space.rect.centre_m();
            let cx = cx + 1.5;
            let feet = space.floor_y_cm as f32 / 100.0;
            let pos = Vec3::new(cx, feet + PLAYER_BASE_Y, cz);

            cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, pos, pos);

            // De pie en el centro de una sala de la planta alta no estorba nada. Si estorba puede ser
            // contenido legítimo, así que no se afirma nada de ese caso.
            if cache.blocked_at(pos, 0.35) {
                continue;
            }

            // Y el suelo que encuentra es el de SU planta, no el de la baja. Si cayera abajo, el
            // jugador atravesaría el forjado — el fallo espejo del congelado.
            let y = cache.floor_y(pos);
            let drop = pos.y - y;
            assert!(
                drop.abs() < STOREY_M * 0.5 || {
                    eprintln!(
                        "[freeze] ({rx},{rz}) espacio {:?} en ({:.1},{:.1}) cota {}",
                        space.role,
                        pos.x,
                        pos.z,
                        space.floor_y_cm
                    );
                    false
                },
                "({rx},{rz}) en la planta alta a y={:.2} el suelo sale a {y:.2}: se cae {drop:.2} m, \
                 o sea que el ráster no ve el forjado de su planta",
                pos.y
            );
            checked += 1;
            if checked >= 12 {
                break;
            }
        }
    }

    assert!(
        checked > 0,
        "ninguna región dio un sitio de pie en la planta alta: el test no ha probado nada"
    );
    println!("[autoridad] {checked} posiciones de planta alta andables contra el ráster de WG3");
}

/// ADR-108 verificaciones (a) y (d) — **una criatura SUBE una escalera de WG3**, y lo que cuesta
/// preguntarlo se mide antes de enchufarlo.
///
/// Es la prueba de D2: la celda de navegación lleva su cota y las vecinas se enlazan por el mismo
/// escalón que sube el jugador. Con una celda 2D —la de `grid_gen`— esto es imposible: una escalera
/// pisa la misma XZ a diez cotas, así que o se ve como una columna maciza o se cruza por el aire.
///
/// La medida de coste no es adorno: sin grafo horneado, cada expansión paga contra el ráster, y si eso
/// no cabe en un tick hay que saberlo **antes** de que lo llame el robapieles y no después.
#[test]
fn a_creature_can_climb_a_wg3_stair() {
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::wg3::nav;
    use crate::world::wg3::world::Wg3WorldCache;
    use crate::world::Vec3;

    const PLAYER_BASE_Y: f32 = 1.8;
    const STOREY_M: f32 = 3.32;

    let m = real_manifest();
    let mut climbed = 0usize;
    let mut worst_expansions = 0usize;

    for (rx, rz) in AUDIT_REGIONS {
        let b = building_of(rx, rz);
        let region = Wg3RegionCoord { x: rx, z: rz };
        let mut worlds = Wg3WorldCache::default();
        let mut cache = Wg3CollisionCache::new();
        let _ = region;

        for well in &b.wells {
            // El pie de la escalera: el centro del hueco, en la planta de abajo.
            let (wx, wz) = well.rect.centre_m();
            let below = &b.storeys[well.storey_below];
            let base = below.spaces[well.space_below].floor_y_cm as f32 / 100.0;
            // Y la salida: el CENTRO DEL ESPACIO DE ARRIBA, no la misma XZ. Con el mismo XZ la celda
            // de destino es la de origen y la búsqueda termina antes de empezar — un destino de dos
            // coordenadas no puede decir «una planta más arriba».
            let above = &b.storeys[well.storey_below + 1];
            let up = &above.spaces[well.space_above];
            let (ux, uz) = up.rect.centre_m();
            let to = Vec3::new(ux, up.floor_y_cm as f32 / 100.0 + PLAYER_BASE_Y, uz);

            cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, to, to);

            // **El pie de la escalera no está en el centro del hueco.** Ahí el tiro ya va por la
            // mitad, a metro y medio, así que a la cota de la planta baja no hay nada en lo que
            // apoyarse. Se busca el suelo que REALMENTE haya en esa vertical, empezando por arriba.
            let Some(start_floor) = cache.floor_below_m(wx, wz, base + STOREY_M) else {
                continue;
            };
            let from = Vec3::new(wx, start_floor + PLAYER_BASE_Y, wz);
            cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, from, to);

            let mut path = Vec::new();
            let outcome = nav::find_path(&cache, from, to, &mut path);
            worst_expansions = worst_expansions.max(outcome.expansions);
            if path.is_empty() {
                continue;
            }

            // Lo que se afirma no es que llegue al punto exacto —eso depende de dónde caiga el
            // rellano— sino que **el camino ALCANZA la cota de la planta de arriba**. Sin cota en la
            // celda, la altura del camino no se movería nunca de la de partida.
            let top = path.iter().map(|p| p.y).fold(f32::MIN, f32::max);
            let gained = top - from.y;
            if top >= to.y - 0.5 {
                climbed += 1;
                println!(
                    "[nav] ({rx},{rz}) escalera en ({wx:.1},{wz:.1}) — sube {gained:.2} m en \
                     {} pasos, {} expansiones",
                    path.len(),
                    outcome.expansions
                );
            }
            if climbed >= 6 {
                break;
            }
        }
    }

    println!(
        "[nav] peor caso {worst_expansions} expansiones (tope {})",
        nav::NAV_MAX_EXPANSIONS
    );
    assert!(
        climbed > 0,
        "ninguna escalera de WG3 se pudo subir navegando: la celda de navegación no está viendo el \
         cambio de cota, que es exactamente lo que ADR-108 D2 existe para resolver"
    );
}

/// ADR-106 — **el spawn cae de pie en el mundo de WG3, no en el de WG2.**
///
/// Con la autoridad movida, aparecer en una celda que WG2 da por buena y WG3 tiene maciza no deja al
/// jugador flotando: lo deja **atascado**, porque el movimiento ya lo resuelve el ráster y un macizo
/// no se cruza. Este test comprueba que el sitio elegido cumple las tres cosas que hacen habitable un
/// punto —hay suelo, cabe uno de pie, y no le estorba nada— y no sólo que la función devuelva algo.
#[test]
fn the_wg3_spawn_lands_on_something_you_can_stand_on() {
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::wg3::world::Wg3WorldCache;
    use crate::world::Vec3;

    const PLAYER_BASE_Y: f32 = 1.8;

    let m = real_manifest();
    let mut found = 0usize;

    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, max_x, max_z) = region.bounds();
        let preferred = Vec3::new((min_x + max_x) * 0.5, PLAYER_BASE_Y, (min_z + max_z) * 0.5);

        let mut worlds = Wg3WorldCache::default();
        let mut cache = Wg3CollisionCache::new();
        cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, preferred, preferred);

        let Some(spawn) = cache.standable_near(preferred) else {
            // No se afirma que SIEMPRE haya sitio: el centro exacto de una región puede caer en un
            // vacío intencionado. Lo que se afirma es que cuando devuelve algo, ese algo vale.
            continue;
        };

        assert!(
            !cache.blocked_at(spawn, crate::world::collision::PLAYER_RADIUS),
            "({rx},{rz}) el spawn de WG3 en ({:.2},{:.2},{:.2}) está DENTRO de algo: con el \
             movimiento resuelto por el ráster, eso es aparecer atascado",
            spawn.x,
            spawn.y,
            spawn.z
        );
        // Y de pie sobre suelo suyo: si el suelo que encuentra estuviera lejos, estaría cayendo.
        let floor = cache.floor_y(spawn);
        assert!(
            (spawn.y - floor).abs() < 0.5,
            "({rx},{rz}) el spawn está a {:.2} y su suelo a {floor:.2}: no está apoyado en nada",
            spawn.y
        );
        println!(
            "[spawn] ({rx},{rz}) de pie en ({:.2}, {:.2}, {:.2})",
            spawn.x, spawn.y, spawn.z
        );
        found += 1;
    }

    assert!(
        found > 0,
        "ninguna de las cuatro regiones dio un sitio de pie: el spawn de WG3 no sirve para nada"
    );
}

/// ADR-108 — **el suavizado no puede inventar un atajo por el aire.**
///
/// `simplify` quita puntos intermedios mientras la recta entre los que quedan sea andable, y en un
/// mundo de una sola planta eso es sólo mirar paredes. Aquí no: dos puntos pueden verse y estar en
/// plantas distintas, así que una recta que cruza por encima de un atrio pasaría por «libre» porque
/// abajo hay suelo — a tres metros. Este test comprueba que el camino simplificado **sigue siendo
/// andable de punta a punta**, tramo a tramo.
#[test]
fn a_simplified_path_is_still_walkable() {
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::wg3::nav;
    use crate::world::wg3::world::Wg3WorldCache;
    use crate::world::Vec3;

    const PLAYER_BASE_Y: f32 = 1.8;

    let m = real_manifest();
    let mut checked = 0usize;
    let mut saved = 0usize;
    let mut raw_total = 0usize;

    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, max_x, max_z) = region.bounds();
        let mut worlds = Wg3WorldCache::default();
        let mut cache = Wg3CollisionCache::new();

        let from = Vec3::new((min_x + max_x) * 0.5, PLAYER_BASE_Y, (min_z + max_z) * 0.5);
        cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, from, from);
        let Some(start) = cache.standable_near(from) else {
            continue;
        };

        // Un destino a veinte metros en cada sentido: alguno cae en sala y alguno en pared, que es
        // justo la mezcla que interesa.
        for (dx, dz) in [(20.0, 0.0), (-20.0, 0.0), (0.0, 20.0), (0.0, -20.0)] {
            let to = Vec3::new(start.x + dx, start.y, start.z + dz);
            cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, start, to);

            let mut raw = Vec::new();
            nav::find_path(&cache, start, to, &mut raw);
            if raw.len() < 3 {
                continue;
            }
            let mut simple = Vec::new();
            nav::simplify(&cache, start, &raw, 0.35, &mut simple);

            raw_total += raw.len();
            saved += raw.len() - simple.len();

            let mut prev = start;
            for (k, p) in simple.iter().enumerate() {
                // El suavizado no puede ser PEOR que el camino crudo: si un tramo no cabe en recta,
                // el punto tiene que ser uno que el A* ya diera por bueno celda a celda —o sea, el
                // siguiente del camino crudo— y no un salto inventado más lejos.
                if !nav::segment_is_clear(&cache, prev, *p, 0.35) {
                    assert!(
                        raw.contains(p),
                        "({rx},{rz}) el tramo {k} no cabe en recta Y el punto no está en el camino                          crudo: eso es un atajo inventado, de ({:.1},{:.2},{:.1}) a                          ({:.1},{:.2},{:.1})",
                        prev.x,
                        prev.y,
                        prev.z,
                        p.x,
                        p.y,
                        p.z
                    );
                    prev = *p;
                    continue;
                }
                assert!(
                    nav::segment_is_clear(&cache, prev, *p, 0.35),
                    "({rx},{rz}) el tramo {k} del camino simplificado no es andable: de \
                     ({:.1},{:.2},{:.1}) a ({:.1},{:.2},{:.1}). El suavizado se ha inventado un \
                     atajo, y en un mundo con plantas eso es cruzar por el aire",
                    prev.x,
                    prev.y,
                    prev.z,
                    p.x,
                    p.y,
                    p.z
                );
                prev = *p;
            }
            checked += 1;
        }
    }

    assert!(
        checked > 0,
        "ningún camino que simplificar: el test no probó nada"
    );
    println!("[nav] {checked} caminos simplificados, {saved} puntos quitados de {raw_total}");
}

/// **DÓNDE NO PASA LA IA Y SÍ PASA EL JUGADOR.**
///
/// El síntoma reportado jugando es que el robapieles no cruza algunos pasillos ni algunos huecos. Las
/// dos preguntas —«¿cabe el jugador?» y «¿es andable para navegar?»— salen de primitivas distintas
/// del mismo ráster, así que pueden discrepar sin que nada falle. Esta sonda cuenta la discrepancia y
/// dice POR QUÉ: sin suelo, sin altura libre, o suelo a un escalón imposible.
///
/// `cargo test --manifest-path backend/Cargo.toml probe_where_nav_disagrees -- --ignored --nocapture`
#[test]
#[ignore]
fn probe_where_nav_disagrees_with_the_player() {
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::wg3::nav;
    use crate::world::wg3::world::Wg3WorldCache;
    use crate::world::Vec3;

    const PLAYER_BASE_Y: f32 = 1.8;
    const STEP: f32 = 0.5;

    let m = real_manifest();

    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, _, _) = region.bounds();
        let mut worlds = Wg3WorldCache::default();
        let mut cache = Wg3CollisionCache::new();

        // Un chunk entero, precalentado alrededor de su centro.
        let centre = Vec3::new(min_x + 75.0, PLAYER_BASE_Y, min_z + 75.0);
        cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, centre, centre);

        let (mut player_ok, mut nav_ok) = (0usize, 0usize);
        let (mut sin_suelo, mut sin_altura) = (0usize, 0usize);

        let cells = (50.0 / STEP) as i32;
        for iz in 0..cells {
            for ix in 0..cells {
                let x = min_x + 50.0 + ix as f32 * STEP + STEP * 0.5;
                let z = min_z + 50.0 + iz as f32 * STEP + STEP * 0.5;
                let Some(floor) = cache.floor_below_m(x, z, 0.0) else {
                    continue;
                };
                // El jugador puede estar aquí si no le estorba nada de pie.
                let stand = Vec3::new(x, floor + PLAYER_BASE_Y, z);
                if cache.blocked_at(stand, crate::world::collision::PLAYER_RADIUS) {
                    continue;
                }
                player_ok += 1;

                match nav::floor_at(&cache, x, z, floor) {
                    Some(_) => nav_ok += 1,
                    None => {
                        if cache.floor_below_m(x, z, floor).is_none() {
                            sin_suelo += 1;
                        } else {
                            sin_altura += 1;
                        }
                    }
                }
            }
        }

        let lost = player_ok.saturating_sub(nav_ok);
        let pct = if player_ok > 0 {
            lost as f32 * 100.0 / player_ok as f32
        } else {
            0.0
        };
        println!(
            "[nav-diff] ({rx},{rz}) jugador {player_ok} celdas · navegación {nav_ok} · \
             PIERDE {lost} ({pct:.1} %) — sin suelo {sin_suelo}, sin altura libre {sin_altura}"
        );
    }
}

/// **¿SE PARTE EL GRAFO?** Inundación desde un punto de pie, con la regla de vecindad de la
/// navegación, contra el total de celdas que el jugador puede pisar.
///
/// La sonda anterior dice que ANDABILIDAD no es el problema: jugador y navegación ven las mismas
/// celdas. Si aun así la IA no cruza un pasillo, el fallo está en el ENLACE — el salto de cota que
/// separa dos vecinas — o en la ventana de búsqueda. Esto separa las dos cosas: la inundación no tiene
/// ventana ni tope, así que lo que no alcance es culpa del enlace y de nada más.
///
/// `cargo test --manifest-path backend/Cargo.toml probe_is_the_nav_graph_connected -- --ignored --nocapture`
#[test]
#[ignore]
fn probe_is_the_nav_graph_connected() {
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::wg3::nav;
    use crate::world::wg3::world::Wg3WorldCache;
    use crate::world::Vec3;
    use std::collections::HashSet;

    const PLAYER_BASE_Y: f32 = 1.8;
    const STEP: f32 = 0.5;

    let m = real_manifest();

    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, _, _) = region.bounds();
        let mut worlds = Wg3WorldCache::default();
        let mut cache = Wg3CollisionCache::new();
        let centre = Vec3::new(min_x + 75.0, PLAYER_BASE_Y, min_z + 75.0);
        cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, centre, centre);

        // Todas las celdas pisables del chunk central, y de paso una para arrancar.
        let mut walkable: HashSet<(i32, i32)> = HashSet::new();
        let mut seed_cell: Option<(i32, i32, f32)> = None;
        let cells = (50.0 / STEP) as i32;
        for iz in 0..cells {
            for ix in 0..cells {
                let x = min_x + 50.0 + ix as f32 * STEP + STEP * 0.5;
                let z = min_z + 50.0 + iz as f32 * STEP + STEP * 0.5;
                let Some(floor) = cache.floor_below_m(x, z, 0.0) else {
                    continue;
                };
                if nav::floor_at(&cache, x, z, floor).is_none() {
                    continue;
                }
                let c = nav::cell_of(x, z);
                walkable.insert(c);
                if seed_cell.is_none() {
                    seed_cell = Some((c.0, c.1, floor));
                }
            }
        }
        let Some((sx, sz, sfloor)) = seed_cell else {
            continue;
        };

        // Inundación con la MISMA regla de vecindad que usa la búsqueda.
        let mut seen: HashSet<(i32, i32, i32)> = HashSet::new();
        let mut stack = vec![(sx, sz, (sfloor * 100.0).round() as i32)];
        seen.insert(stack[0]);
        let mut reached: HashSet<(i32, i32)> = HashSet::new();
        while let Some((cx, cz, cf)) = stack.pop() {
            reached.insert((cx, cz));
            let floor = cf as f32 / 100.0;
            for (dx, dz) in [(1, 0), (-1, 0), (0, 1), (0, -1)] {
                let nc = (cx + dx, cz + dz);
                let (nx, nz) = nav::cell_centre(nc.0, nc.1);
                let Some(nf) = nav::floor_at(&cache, nx, nz, floor) else {
                    continue;
                };
                if ((nf - floor).abs() * 100.0) as i32 > crate::world::wg3::plan::MAX_WALK_STEP_CM {
                    continue;
                }
                let k = (nc.0, nc.1, (nf * 100.0).round() as i32);
                if seen.insert(k) {
                    stack.push(k);
                }
            }
        }

        let inside: usize = reached.iter().filter(|c| walkable.contains(c)).count();
        let pct = inside as f32 * 100.0 / walkable.len().max(1) as f32;
        println!(
            "[nav-conn] ({rx},{rz}) pisables {} · alcanzadas desde una {} ({pct:.1} %)",
            walkable.len(),
            inside
        );
    }
}

/// ADR-108 — **una entidad se APOYA en el suelo de WG3; el jugador conserva el suyo.**
///
/// El robapieles calcula su Y con las cotas de WG2 (`grid_floor_y(layer) + PLAYER_BASE_Y`). En un
/// mundo de WG3 esa Y no cae en ningún suelo: si los pies quedan dentro del forjado,
/// `blocked_standing_at` dice que sí y el movimiento sale `Blocked` — la criatura se queda quieta
/// justo donde el jugador pasa sin enterarse. Ése fue el síntoma reportado jugando: «en algunos
/// pasillos no pasa».
///
/// La diferencia entre las dos puertas es `claimed_y`, y este test la fija para que nadie las vuelva
/// a confundir.
#[test]
fn an_entity_is_pinned_to_the_wg3_floor_and_a_player_is_not() {
    use crate::world::collision::Level0Collision;
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::wg3::world::Wg3WorldCache;
    use crate::world::Vec3;

    const PLAYER_BASE_Y: f32 = 1.8;

    let m = real_manifest();
    let mut checked = 0usize;

    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, max_x, max_z) = region.bounds();
        let mut worlds = Wg3WorldCache::default();
        let mut cache = Wg3CollisionCache::new();
        let centre = Vec3::new((min_x + max_x) * 0.5, PLAYER_BASE_Y, (min_z + max_z) * 0.5);
        cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, centre, centre);
        let Some(good) = cache.standable_near(centre) else {
            continue;
        };

        // Una Y equivocada: medio metro por encima de la buena, como la que traería una entidad
        // grounded contra otro mundo.
        let wrong = Vec3::new(good.x, good.y + 0.5, good.z);
        let target = Vec3::new(good.x + 0.4, wrong.y, good.z);

        let ent = Level0Collision::resolve_move_wg3_entity(&cache, wrong, target, 0.35);
        assert!(
            (ent.position.y - good.y).abs() < 0.2,
            "({rx},{rz}) la entidad no se apoyó en el suelo de WG3: entró a {:.2} y salió a {:.2}, \
             y el suelo está a {:.2}. Sin el pin, sus pies quedan dentro del forjado y se congela",
            wrong.y,
            ent.position.y,
            good.y
        );

        // Y el jugador NO se pega: su Y viene del cliente, que simula su propio cuerpo.
        let ply = Level0Collision::resolve_move_wg3(&cache, wrong, target);
        assert!(
            (ply.position.y - wrong.y).abs() < 0.01,
            "({rx},{rz}) al jugador se le movió la Y: entró a {:.2} y salió a {:.2}. Eso es el \
             tirón sin causa que ADR-026 parte 3 existe para evitar",
            wrong.y,
            ply.position.y
        );
        checked += 1;
    }

    assert!(
        checked > 0,
        "ninguna región dio un sitio de pie: el test no probó nada"
    );
    println!("[entidad] {checked} regiones: la entidad se apoya, el jugador conserva su Y");
}

/// **¿SE BAJAN LAS ESCALERAS?** Visto jugando: el robapieles se queda pillado intentando bajar.
///
/// Subir y bajar NO son simétricos en esta navegación: el suelo de una celda sale de
/// `floor_below(x, cota + escalón, z)`, que se queda con la superficie MÁS ALTA que no pase del
/// escalón. Yendo hacia arriba eso es justo lo que se quiere; yendo hacia abajo puede devolver
/// siempre la misma cota y dejar el grafo sin salida. Esta sonda intenta cada escalera en los dos
/// sentidos y dice cuál de los dos falla.
///
/// `cargo test --manifest-path backend/Cargo.toml probe_stairs_in_both_directions -- --ignored --nocapture`
#[test]
#[ignore]
fn probe_stairs_in_both_directions() {
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::wg3::nav;
    use crate::world::wg3::world::Wg3WorldCache;
    use crate::world::Vec3;

    const PLAYER_BASE_Y: f32 = 1.8;
    const STOREY_M: f32 = 3.32;

    let m = real_manifest();
    let (mut up_ok, mut up_fail, mut down_ok, mut down_fail) = (0, 0, 0, 0);

    for (rx, rz) in AUDIT_REGIONS {
        let b = building_of(rx, rz);
        let mut worlds = Wg3WorldCache::default();
        let mut cache = Wg3CollisionCache::new();

        for well in &b.wells {
            let (wx, wz) = well.rect.centre_m();
            let below = &b.storeys[well.storey_below];
            let base = below.spaces[well.space_below].floor_y_cm as f32 / 100.0;
            let above = &b.storeys[well.storey_below + 1];
            let up = &above.spaces[well.space_above];
            let (ux, uz) = up.rect.centre_m();
            let top = Vec3::new(ux, up.floor_y_cm as f32 / 100.0 + PLAYER_BASE_Y, uz);

            cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, top, top);
            let Some(mid_floor) = cache.floor_below_m(wx, wz, base + STOREY_M) else {
                continue;
            };
            let mid = Vec3::new(wx, mid_floor + PLAYER_BASE_Y, wz);
            cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, mid, top);

            // ¿Son válidos los DOS extremos? Un `find_path` de cero pasos casi siempre es un
            // origen que la propia navegación considera inandable, no un camino que no existe.
            let top_floor = cache.floor_below_m(top.x, top.z, top.y - PLAYER_BASE_Y);
            let top_walk = top_floor
                .and_then(|f| nav::floor_at(&cache, top.x, top.z, f))
                .is_some();
            let mid_walk = nav::floor_at(&cache, mid.x, mid.z, mid_floor).is_some();
            if !top_walk || !mid_walk {
                println!(
                    "[escalera] ({rx},{rz}) EXTREMO INANDABLE en ({wx:.1},{wz:.1}):                      arriba={top_walk} (suelo {top_floor:?}, esperado {:.2}) · medio={mid_walk}",
                    top.y - PLAYER_BASE_Y
                );
            }

            // Cuántas de las cuatro vecinas del origen son andables. Cero = isla de una celda, y
            // entonces `find_path` no puede hacer nada por mucho que el destino exista.
            let vecinas = |p: Vec3, floor: f32| -> usize {
                let c = nav::cell_of(p.x, p.z);
                [(1, 0), (-1, 0), (0, 1), (0, -1)]
                    .iter()
                    .filter(|(dx, dz)| {
                        let (nx, nz) = nav::cell_centre(c.0 + dx, c.1 + dz);
                        nav::floor_at(&cache, nx, nz, floor).is_some()
                    })
                    .count()
            };
            println!(
                "[escalera] ({rx},{rz}) ({wx:.1},{wz:.1}) vecinas andables: arriba {} · medio {}",
                top_floor.map(|f| vecinas(top, f)).unwrap_or(9),
                vecinas(mid, mid_floor)
            );

            let mut path = Vec::new();

            // SUBIR: del medio de la escalera al espacio de arriba.
            let o_up = nav::find_path(&cache, mid, top, &mut path);
            println!(
                "[escalera-diag] ({rx},{rz}) SUBIR exp={} reached={} best={} pasos={}                  dist_celdas={}",
                o_up.expansions,
                o_up.reached,
                o_up.best_effort,
                path.len(),
                (nav::cell_of(mid.x, mid.z).0 - nav::cell_of(top.x, top.z).0).abs()
                    + (nav::cell_of(mid.x, mid.z).1 - nav::cell_of(top.x, top.z).1).abs()
            );
            let reached_up = path.iter().map(|p| p.y).fold(f32::MIN, f32::max) >= top.y - 0.5;
            if reached_up {
                up_ok += 1;
            } else {
                up_fail += 1;
            }

            // BAJAR: del espacio de arriba al medio de la escalera.
            let o_dn = nav::find_path(&cache, top, mid, &mut path);
            println!(
                "[bajar] ({rx},{rz}) exp={} reached={} best={} pasos={} · candidatos bajo el                  rellano={}",
                o_dn.expansions,
                o_dn.reached,
                o_dn.best_effort,
                path.len(),
                {
                    let c = nav::cell_of(top.x, top.z);
                    let f = top.y - PLAYER_BASE_Y;
                    [(1, 0), (-1, 0), (0, 1), (0, -1)]
                        .iter()
                        .map(|(dx, dz)| {
                            let (nx, nz) = nav::cell_centre(c.0 + dx, c.1 + dz);
                            nav::floors_at(&cache, nx, nz, f).len()
                        })
                        .sum::<usize>()
                }
            );
            let low = path.iter().map(|p| p.y).fold(f32::MAX, f32::min);
            let reached_down = !path.is_empty() && low <= mid.y + 0.5;
            if reached_down {
                down_ok += 1;
            } else {
                down_fail += 1;
                println!(
                    "[escalera] ({rx},{rz}) NO BAJA en ({wx:.1},{wz:.1}): de y={:.2} a y={:.2}, \
                     {} pasos, lo más bajo alcanzado y={:.2}",
                    top.y,
                    mid.y,
                    path.len(),
                    if path.is_empty() { top.y } else { low }
                );
            }
        }
    }

    println!(
        "[escalera] SUBIR {up_ok} ok / {up_fail} fallan · BAJAR {down_ok} ok / {down_fail} fallan"
    );
}

/// **UN CUERPO SOBRE UNA ESCALERA PUEDE AVANZAR.**
///
/// Era el fallo que quedaba después de que la navegación ya encontrara la ruta: había camino y la
/// criatura no se movía. `blocked_at` medía el cuerpo desde los PIES, y la contrahuella de 25,5 cm
/// mete el peldaño siguiente DENTRO del cuerpo — así que la muestra que cae sobre él declara
/// bloqueado y el movimiento sale `Blocked`. Sólo a veces, porque depende de en qué parte de la
/// huella de 60 cm se pregunte: exactamente el «se clava a mitad de escalera» que se vio jugando.
///
/// Con la tolerancia de un escalón —lo que hace cualquier `CharacterController` con su `stepOffset`,
/// y lo que el cliente ya hacía con 0,275— el cuerpo empieza por encima de lo que puede pisar.
#[test]
fn a_body_on_a_stair_can_move_forward() {
    use crate::world::collision::{Level0Collision, PLAYER_RADIUS};
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::wg3::world::Wg3WorldCache;
    use crate::world::Vec3;

    const PLAYER_BASE_Y: f32 = 1.8;
    const STOREY_M: f32 = 3.32;

    let m = real_manifest();
    let (mut moved, mut stuck) = (0usize, 0usize);

    for (rx, rz) in AUDIT_REGIONS {
        let b = building_of(rx, rz);
        let mut worlds = Wg3WorldCache::default();
        let mut cache = Wg3CollisionCache::new();

        for well in &b.wells {
            let (wx, wz) = well.rect.centre_m();
            let base =
                b.storeys[well.storey_below].spaces[well.space_below].floor_y_cm as f32 / 100.0;
            let probe = Vec3::new(wx, base + STOREY_M + PLAYER_BASE_Y, wz);
            cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, probe, probe);
            let Some(floor) = cache.floor_below_m(wx, wz, base + STOREY_M) else {
                continue;
            };
            let from = Vec3::new(wx, floor + PLAYER_BASE_Y, wz);

            // Medio metro en cada sentido: la escalera corre por uno de los dos ejes, así que al
            // menos una de las cuatro tiene que poder darse. Si NINGUNA se puede, está clavado.
            let any = [(0.5, 0.0), (-0.5, 0.0), (0.0, 0.5), (0.0, -0.5)]
                .iter()
                .any(|(dx, dz)| {
                    let to = Vec3::new(from.x + dx, from.y, from.z + dz);
                    let r = Level0Collision::resolve_move_wg3_entity(&cache, from, to, 0.35);
                    r.position.distance_xz(from) > 0.05
                });
            if any {
                moved += 1;
            } else {
                stuck += 1;
                println!(
                    "[escalón] ({rx},{rz}) CLAVADO en ({wx:.1},{wz:.1}) a y={:.2}: ninguna de las \
                     cuatro direcciones se puede dar",
                    from.y
                );
            }
            let _ = PLAYER_RADIUS;
        }
    }

    println!("[escalón] {moved} escaleras donde el cuerpo avanza · {stuck} clavadas");
    assert!(
        stuck == 0,
        "{stuck} escaleras dejan a un cuerpo clavado a media subida: el peldaño siguiente cae dentro \
         del cuerpo y ninguna dirección se puede dar"
    );
}

/// ADR-108 — la vista NO es el paso. Mide cuántas veces se separan, porque si nunca se separaran
/// `line_of_sight` sobraría y bastaría con `segment_is_clear`.
///
/// Los dos casos que busca, y son opuestos:
///   - VE PERO NO PASA: la recta está despejada a la altura del ojo y no hay suelo debajo, o hay un
///     escalón que corta el paso. Un atrio, una barandilla. Con el test de andar la criatura te
///     pierde de vista con el hueco delante.
///   - PASA PERO NO VE: suelo continuo con algo bajo por encima — un dintel, un forjado a media
///     cota. Raro, pero si aparece, andar como sustituto de ver regala wallhack.
#[test]
fn probe_sight_is_not_the_same_as_passage() {
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::wg3::nav;
    use crate::world::wg3::world::Wg3WorldCache;
    use crate::world::Vec3;

    const EYE_M: f32 = 1.8;
    const RANGE_M: f32 = 12.0;

    let m = real_manifest();
    let (mut ve_no_pasa, mut pasa_no_ve, mut iguales) = (0usize, 0usize, 0usize);

    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let (min_x, min_z, _, _) = region.bounds();
        let mut worlds = Wg3WorldCache::default();
        let mut cache = Wg3CollisionCache::new();
        let centre = Vec3::new(min_x + 75.0, EYE_M, min_z + 75.0);
        cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, centre, centre);

        // Parejas a lo largo de una rejilla gruesa: caras, y lo que se mide es la proporción.
        for iz in 0..10 {
            for ix in 0..10 {
                let x = min_x + 55.0 + ix as f32 * 4.0;
                let z = min_z + 55.0 + iz as f32 * 4.0;
                let Some(floor) = cache.floor_below_m(x, z, 0.0) else {
                    continue;
                };
                let a = Vec3::new(x, floor + EYE_M, z);
                for (dx, dz) in [(RANGE_M, 0.0), (0.0, RANGE_M), (RANGE_M, RANGE_M)] {
                    let (bx, bz) = (x + dx, z + dz);
                    let Some(bfloor) = cache.floor_below_m(bx, bz, 0.0) else {
                        continue;
                    };
                    let b = Vec3::new(bx, bfloor + EYE_M, bz);
                    let sight = nav::line_of_sight(&cache, a, b);
                    let walk = nav::segment_is_clear(&cache, a, b, 0.5);
                    match (sight, walk) {
                        (true, false) => ve_no_pasa += 1,
                        (false, true) => pasa_no_ve += 1,
                        _ => iguales += 1,
                    }
                }
            }
        }
    }

    println!(
        "PROBE sight_vs_passage: ve_no_pasa={ve_no_pasa} pasa_no_ve={pasa_no_ve} iguales={iguales}"
    );
    // La única afirmación que se sostiene sola: los dos tests se separan de verdad. Un cero aquí
    // querría decir que uno de los dos está mintiendo, y esta sonda existe para verlo.
    assert!(
        ve_no_pasa > 0,
        "la vista nunca supera al paso: line_of_sight no está aportando nada"
    );
}

#[test]
fn probe_space_overlap() {
    let m = real_manifest();
    let region = Wg3RegionCoord { x: 0, z: 0 };
    let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);
    let segs = served.segments();
    let mut solapes = 0;
    let mut total = 0;
    let mut ejemplo = String::new();
    for (i, a) in segs.iter().enumerate() {
        for b in segs.iter().skip(i + 1) {
            total += 1;
            let xz = a.x_cm < b.x_cm + b.size_x_cm
                && b.x_cm < a.x_cm + a.size_x_cm
                && a.z_cm < b.z_cm + b.size_z_cm
                && b.z_cm < a.z_cm + a.size_z_cm;
            let y = a.floor_y_cm < b.floor_y_cm + b.height_cm
                && b.floor_y_cm < a.floor_y_cm + a.height_cm;
            if xz && y {
                solapes += 1;
                if ejemplo.is_empty() {
                    ejemplo = format!(
                        "a=({},{} {}x{} y{} h{} s{}) b=({},{} {}x{} y{} h{} s{})",
                        a.x_cm,
                        a.z_cm,
                        a.size_x_cm,
                        a.size_z_cm,
                        a.floor_y_cm,
                        a.height_cm,
                        a.style,
                        b.x_cm,
                        b.z_cm,
                        b.size_x_cm,
                        b.size_z_cm,
                        b.floor_y_cm,
                        b.height_cm,
                        b.style
                    );
                }
            }
        }
    }
    println!(
        "PROBE overlap: tramos={} pares={total} solapan={solapes} {ejemplo}",
        segs.len()
    );
}

/// ADR-108 D6 — `space_at` contesta por PLANTA y sin depender del orden de la lista.
///
/// Los espacios SÍ se solapan (sonda `probe_space_overlap`: 26 pares en la región (0,0); el caso
/// típico es una escalera de dos plantas cruzando el volumen de una sala de arriba), así que «el
/// primero que contenga el punto» sería una respuesta que cambia con el orden del vector — y el
/// cliente los recibe repartidos por chunk, o sea en otro orden. El invariante que se exige es el
/// que sí es estable: de todos los que contienen el punto, el del SUELO MÁS ALTO.
#[test]
fn space_at_answers_by_storey_not_by_list_order() {
    let m = real_manifest();
    let region = Wg3RegionCoord { x: 0, z: 0 };
    let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);

    let mut comprobados = 0;
    let mut resuelven_a_otro = 0;
    for seg in served.segments() {
        let x = (seg.x_cm + seg.size_x_cm / 2) as f32 / 100.0;
        let z = (seg.z_cm + seg.size_z_cm / 2) as f32 / 100.0;
        let y = (seg.floor_y_cm + 10) as f32 / 100.0;

        let hit = served
            .space_at(x, y, z)
            .expect("el centro de un tramo cae dentro de algún espacio");

        let x_cm = (x * 100.0).round() as i32;
        let z_cm = (z * 100.0).round() as i32;
        let y_cm = (y * 100.0).round() as i32;
        assert!(
            x_cm >= hit.x_cm
                && x_cm <= hit.x_cm + hit.size_x_cm
                && z_cm >= hit.z_cm
                && z_cm <= hit.z_cm + hit.size_z_cm,
            "el espacio devuelto tiene que contener el punto"
        );
        for otro in served.segments() {
            let contiene = x_cm >= otro.x_cm
                && x_cm <= otro.x_cm + otro.size_x_cm
                && z_cm >= otro.z_cm
                && z_cm <= otro.z_cm + otro.size_z_cm
                && y_cm >= otro.floor_y_cm - 40
                && y_cm <= otro.floor_y_cm + otro.height_cm;
            assert!(
                !contiene || otro.floor_y_cm <= hit.floor_y_cm,
                "hay un espacio con el suelo más alto que también contiene el punto"
            );
        }

        if (hit.x_cm, hit.z_cm, hit.floor_y_cm) != (seg.x_cm, seg.z_cm, seg.floor_y_cm) {
            resuelven_a_otro += 1;
        }
        comprobados += 1;
    }

    assert!(comprobados > 0, "la región servida no tiene tramos");
    println!("PROBE space_at: centros={comprobados} resuelven_a_otro={resuelven_a_otro}");
}

/// ADR-109 — el sitio donde NACE una criatura tiene que salir del ráster, no de la capa de WG2.
///
/// Hasta esta tanda el robapieles y los facelings se movían con WG3 y aparecían donde dijera la
/// rejilla vieja: la cota salía de `grid_floor_y(layer)`, que es la cota de una capa de 4 m —o sea
/// CERO para la capa 0— en un mundo cuyas plantas miden 3,32 y cuyos suelos suben y bajan. Este test
/// mide cuántos de los sitios que sortea el reparto de facelings quedan mal por eso, y exige que el
/// ráster los corrija.
#[test]
fn creature_spawn_height_comes_from_the_raster_not_from_the_wg2_layer() {
    use crate::world::faceling_spawn::draw_adults;
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::wg3::world::Wg3WorldCache;
    use crate::world::Vec3;

    let m = real_manifest();
    let mut worlds = Wg3WorldCache::default();
    let mut cache = Wg3CollisionCache::new();

    let mut sorteados = 0;
    let mut corregidos = 0;
    let mut sin_sitio = 0;
    let mut peor_correccion: f32 = 0.0;

    for cx in 0..3 {
        for cz in 0..3 {
            for pos in draw_adults(SERVED_SEED, cx, cz, 0, 8.0) {
                sorteados += 1;
                let wg2 = Vec3::new(pos[0], pos[1], pos[2]);
                cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, wg2, wg2);
                match cache.standable_near(wg2) {
                    Some(p) => {
                        // La corrección que importa es la de SITIO, no sólo la de cota: en la planta
                        // baja de esta región el suelo está a cero y la cota de WG2 acierta por
                        // casualidad, pero el punto sorteado cae dentro de un muro a menudo.
                        let delta = ((p.x - wg2.x).powi(2) + (p.z - wg2.z).powi(2)).sqrt()
                            + (p.y - wg2.y).abs();
                        if delta > 0.27 {
                            corregidos += 1;
                            peor_correccion = peor_correccion.max(delta);
                        }
                        // Lo que se exige de verdad: donde el ráster deja aparecer, se está de pie.
                        assert!(
                            !cache.blocked_at(p, 0.35),
                            "el sitio elegido para nacer no puede estar ocupado"
                        );
                    }
                    None => sin_sitio += 1,
                }
            }
        }
    }

    assert!(sorteados > 0, "el reparto no sorteó ni un faceling");
    assert!(
        corregidos > 0,
        "si el ráster no corrigiera ni un sitio, este cambio no estaría haciendo nada"
    );
    println!(
        "PROBE spawn: sorteados={sorteados} corregidos={corregidos}          sin_sitio={sin_sitio} peor_correccion={peor_correccion:.2}m"
    );
}

/// ADR-109 D5 — el reparto de facelings con WG3: ¿queda mundo poblado?
///
/// La concentración en oficinas pasa de decidirse por CHUNK (WG2: una zona de 50 m) a decidirse por
/// HUECO, con el papel del espacio donde cae. La pregunta que hay que contestar antes de dar esto por
/// bueno no es si el código corre: es si la población sobrevive al cambio de resolución, porque un
/// hueco que cae en el vacío del plan ya no se queda con nadie.
#[test]
fn probe_faceling_draw_under_wg3() {
    use crate::world::faceling_spawn::{draw_adults_into, wg3_keeps_position};
    use crate::world::wg3::world::Wg3WorldCache;

    let m = real_manifest();
    let mut worlds = Wg3WorldCache::default();

    let mut wg2_total = 0usize;
    let mut wg3_sorteados = 0usize;
    let mut wg3_sin_espacio = 0usize;
    let mut wg3_oficina = 0usize;
    let mut wg3_quedan = 0usize;
    let mut drawn = Vec::new();

    for cx in 0..6 {
        for cz in 0..6 {
            draw_adults_into(SERVED_SEED, cx, cz, 0, 8.0, false, &mut drawn);
            wg2_total += drawn.len();

            draw_adults_into(SERVED_SEED, cx, cz, 0, 8.0, true, &mut drawn);
            for (index, pos) in drawn.iter().copied().enumerate() {
                wg3_sorteados += 1;
                let coord = crate::world::wg3::chunk::Wg3ChunkCoord::containing(pos[0], pos[2]);
                let region = worlds.region_for(&m, SERVED_SEED, coord);
                // ADR-110 D3 / T1 — la PLANTA BAJA explícitamente, que es lo que esta banda
                // vigila. `lowest_space_at_xz` contestaba el espacio más bajo de la vertical, y en
                // las que bajan de la cota 0 ése es un peldaño de la planta −1: medía una planta que
                // el reparto ya no puebla.
                let style = region
                    .space_on_storey_at_xz(pos[0], pos[2], 0)
                    .map(|s| s.style);
                match style {
                    None => wg3_sin_espacio += 1,
                    Some(0) => wg3_oficina += 1,
                    Some(_) => {}
                }
                if wg3_keeps_position(SERVED_SEED, cx, cz, 0, index, style) {
                    wg3_quedan += 1;
                }
            }
        }
    }

    println!(
        "PROBE reparto: wg2={wg2_total} wg3_sorteados={wg3_sorteados} sin_espacio={wg3_sin_espacio}          oficina={wg3_oficina} quedan={wg3_quedan}"
    );
    assert!(wg3_quedan > 0, "el reparto de WG3 no deja a nadie en pie");
    // LA POBLACIÓN NO CAMBIA AL MUDARSE. Es lo que justifica `FACELING_WG3_OFFICE_KEEP`, y sin esta
    // aserción ese número se puede tocar sin enterarse de que multiplica los facelings del mundo.
    let baja = wg2_total as f32 * 0.75;
    let alta = wg2_total as f32 * 1.25;
    assert!(
        (wg3_quedan as f32) >= baja && (wg3_quedan as f32) <= alta,
        "el reparto de WG3 deja {wg3_quedan} donde WG2 dejaba {wg2_total}: fuera de la banda          [{baja:.0}, {alta:.0}]. Si el cambio es a propósito, recalibra FACELING_WG3_OFFICE_KEEP."
    );
}

/// ADR-109 D7 — lo que un jugador CONSTRUYE tiene que parar a la criatura, también con WG3.
///
/// La puerta existía desde ADR-043 (`sync_built_cells`) pero escribía en la caja de `grid_gen`, que
/// la navegación de WG3 no mira: desde ADR-108 el robapieles volvía a atravesar el muro que acababas
/// de levantar, sin que nada diera error. Se mide sobre el mundo servido, no sobre una maqueta.
#[test]
fn built_pieces_block_wg3_navigation() {
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::wg3::world::Wg3WorldCache;
    use crate::world::Vec3;

    const PLAYER_BASE_Y: f32 = 1.8;

    let m = real_manifest();
    let mut worlds = Wg3WorldCache::default();
    let mut cache = Wg3CollisionCache::new();
    let region = Wg3ServedWorld::plan_region(&m, SERVED_SEED, Wg3RegionCoord { x: 0, z: 0 });

    // Un sitio de pie de verdad y un tramo corto que lo cruza, dentro del mismo espacio.
    let seg = region
        .segments()
        .iter()
        .find(|s| s.size_x_cm > 800 && s.size_z_cm > 800)
        .expect("hace falta un espacio ancho para tender dos metros de recta");
    let cx = (seg.x_cm + seg.size_x_cm / 2) as f32 / 100.0;
    let cz = (seg.z_cm + seg.size_z_cm / 2) as f32 / 100.0;
    let y = seg.floor_y_cm as f32 / 100.0 + PLAYER_BASE_Y;
    let a = Vec3::new(cx - 1.5, y, cz);
    let b = Vec3::new(cx + 1.5, y, cz);
    cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, a, b);

    assert_eq!(
        cache.blocked_cell_count(),
        0,
        "sin piezas no hay nada bloqueado"
    );
    assert!(
        crate::world::wg3::nav::segment_is_clear(&cache, a, b, 0.35),
        "el control falla: la recta ya estaba cortada antes de construir nada"
    );

    // Y ahora alguien pone una pieza justo en medio.
    cache.set_blocked_from(std::iter::once([cx, y, cz]));
    assert!(
        cache.blocked_cell_count() > 0,
        "la pieza tiene que ocupar celdas"
    );
    assert!(
        !crate::world::wg3::nav::segment_is_clear(&cache, a, b, 0.35),
        "la criatura sigue pasando por donde hay una pieza construida"
    );
    // Y el caché del JUGADOR, que nunca recibe piezas, no cambia de comportamiento.
    let limpio = Wg3CollisionCache::new();
    assert!(!limpio.is_blocked_xz(cx, cz));
}

/// Sonda temporal: columna clavada de (0,1) tras subir a diez plantas.
#[test]
#[ignore = "sonda: imprime, no exige"]
fn probe_stuck_stair_column() {
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::wg3::world::Wg3WorldCache;
    use crate::world::Vec3;
    let m = real_manifest();
    let mut worlds = Wg3WorldCache::default();
    let mut cache = Wg3CollisionCache::new();
    let p = Vec3::new(90.9, 5.5, 160.6);
    cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, p, p);
    for (dx, dz) in [(0.0, 0.0), (0.5, 0.0), (-0.5, 0.0), (0.0, 0.5), (0.0, -0.5)] {
        let (x, z) = (90.9 + dx, 160.6 + dz);
        let f = cache.floor_below_m(x, z, 3.32);
        let h = f.and_then(|ff| cache.headroom_m(x, z, ff));
        println!("[col] ({x:.2},{z:.2}) suelo {f:?} hueco {h:?}");
    }
    let raster = cache.raster_for(90.9, 160.6).expect("raster prewarmed");
    println!(
        "[col] spans en (90.9,160.6): {:?}",
        raster.column_at(90.9, 160.6)
    );
    let region = Wg3RegionCoord { x: 0, z: 1 };
    let bounds = region.bounds();
    let gates = junction::gates_of_region(composer_seed(SERVED_SEED), 0, 1, bounds);
    let b = plan::plan_building(
        region.composer_seed(SERVED_SEED),
        bounds,
        &gates,
        plan::REGION_STOREYS,
    );
    let filled = fill::fill_building(&b, &m);
    let (px, pz) = (9090, 16060);
    for s in &filled.solids {
        if s.x_cm <= px && px < s.x_cm + s.size_x_cm && s.z_cm <= pz && pz < s.z_cm + s.size_z_cm {
            println!("[col] SOLID {s:?}");
        }
    }
    for s in &filled.segments {
        if s.x_cm <= px && px < s.x_cm + s.size_x_cm && s.z_cm <= pz && pz < s.z_cm + s.size_z_cm {
            println!(
                "[col] SEG floor {} h {} style {}",
                s.floor_y_cm, s.height_cm, s.style
            );
        }
    }
    for c in &filled.carves {
        if c.x_cm <= px && px < c.x_cm + c.size_x_cm && c.z_cm <= pz && pz < c.z_cm + c.size_z_cm {
            println!("[col] CARVE {c:?}");
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// T0 de PLAN-PLANTAS-ALTAS.md — LA SONDA DE POBLACIÓN POR PLANTA
//
// Mide, no arregla. Su trabajo es decir si la hipótesis del plan es cierta ANTES de que nadie
// toque producción: que el reparto de población indexa por la CAPA de 4 m de WG2 y que por eso
// una planta de WG3 por encima de la baja nace sin criaturas.
//
// Nada de aquí exige un resultado sobre la población: si estas sondas asertaran el estado ACTUAL,
// arreglar el fallo las pondría rojas y el arreglo parecería la regresión. Lo único que se exige
// es que la sonda no esté ciega (que haya mundo que medir), que es la lección 5 de WG3-ROADMAP.
// ═══════════════════════════════════════════════════════════════════════════════

/// Escala de reparto con la que corre el bucle de verdad (`AdultDriver::new`, `ChildDriver::new`).
const T0_DENSITY_SCALE: f32 = 1.0;

/// Espejo de `collision::PLAYER_BASE_Y`. Duplicado a propósito, misma razón que `PLAYER_RADIUS`.
const T0_PLAYER_BASE_Y: f32 = 1.8;

/// Lo que mide la sonda para UNA planta de UNA región.
#[derive(Debug, Clone, Copy)]
struct T0StoreyRow {
    storey: i32,
    floor_y_cm: i32,
    spaces: usize,
    /// De ésos, los que están exactamente en la cota de la planta (suelo, no peldaño).
    spaces_flat: usize,
    player_y: f32,
    layer: u8,
    adults_drawn: usize,
    adults_kept: usize,
    packs_drawn: usize,
    pack_members: usize,
}

/// El sitio donde estaría un jugador de pie en el suelo de esa planta.
fn t0_player_y(floor_y_cm: i32) -> f32 {
    floor_y_cm as f32 / 100.0 + T0_PLAYER_BASE_Y
}

/// Los chunks de WG2 que cubren una región de WG3. Los dos miden 50 m de lado, así que una región
/// de 150 m son exactamente 3 x 3 chunks — pero se calcula y no se escribe, que es lo que hace que
/// la sonda siga siendo cierta si alguna de las dos medidas cambia.
fn t0_wg2_chunks_of_region(rx: i32, rz: i32) -> Vec<(i32, i32)> {
    let chunk_m = crate::world::grid_gen::CELL_SIZE_M * crate::world::grid_gen::CHUNK_CELLS as f32;
    let (min_x, min_z, max_x, max_z) = Wg3RegionCoord { x: rx, z: rz }.bounds();
    let cx0 = (min_x / chunk_m).floor() as i32;
    let cx1 = ((max_x - 0.01) / chunk_m).floor() as i32;
    let cz0 = (min_z / chunk_m).floor() as i32;
    let cz1 = ((max_z - 0.01) / chunk_m).floor() as i32;
    let mut out = Vec::new();
    for cx in cx0..=cx1 {
        for cz in cz0..=cz1 {
            out.push((cx, cz));
        }
    }
    out
}

/// **T0 (a) — LA TABLA: planta por planta, qué se despierta.**
///
/// Replica el camino EXACTO de `AdultDriver::sync_population` y `ChildDriver::sync_population` con
/// el jugador de pie en el suelo de cada planta:
///
/// 1. `layer = world_pos_to_layer(p.y)` — la capa de 4 m de WG2, que es lo que hoy indexa todo.
/// 2. `draw_adults_into(.., layer, ..)` / `draw_child_pack_into(.., layer, ..)`.
/// 3. Por cada hueco, el papel de su espacio y `wg3_keeps_position`, como hace `wg3_spawn_point`.
///
/// Lo que NO replica, y por qué no hace falta: el radio de activación y la distancia mínima. Ambos
/// filtran POR DISTANCIA a un jugador, y aquí el jugador se coloca dentro de la región que se mide;
/// si el sorteo da cero, ningún radio lo va a subir.
#[test]
#[ignore = "sonda: imprime, no exige"]
fn probe_population_by_storey() {
    use crate::world::faceling_spawn::{
        draw_adults_into, draw_child_pack_into, wg3_keeps_position,
    };
    use crate::world::grid_gen::world_pos_to_layer;

    let m = real_manifest();
    let mut all_rows: Vec<(i32, i32, Vec<T0StoreyRow>)> = Vec::new();

    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3ServedWorld::plan_region(&m, SERVED_SEED, Wg3RegionCoord { x: rx, z: rz });

        // **AGRUPAR POR PLANTA, NO POR COTA — y la diferencia no es cosmética.** La primera versión
        // de esta sonda agrupó por `floor_y_cm` distinto y dio 32 «plantas» en la región (-1,2), con
        // cotas de 25, 51 y 76 cm. No son plantas: son los PELDAÑOS de las escaleras y las celdas de
        // los conectores que suben (ADR-098 enm. 1 encendió `climb`, con contrahuellas de 18-26 cm).
        // Contarlas como plantas habría inflado ×10 el número de plantas del mundo justo en la sonda
        // que existe para decidir CUÁNTA gente vive en cada una.
        //
        // La planta de un tramo es su cota dividida por la altura de planta: un peldaño a 306 cm
        // pertenece a la planta 0, que es de donde sale su escalera.
        let storey_h = crate::world::wg3::plan::STOREY_HEIGHT_CM;
        let mut by_storey: std::collections::BTreeMap<i32, (usize, usize)> = Default::default();
        for s in region.segments() {
            let e = by_storey
                .entry(s.floor_y_cm.div_euclid(storey_h))
                .or_default();
            e.0 += 1;
            // Los tramos que están EXACTAMENTE en la cota de la planta: el suelo de verdad, sin
            // peldaños. Es el número que hay que mirar para repartir población.
            if s.floor_y_cm.rem_euclid(storey_h) == 0 {
                e.1 += 1;
            }
        }

        let chunks = t0_wg2_chunks_of_region(rx, rz);
        let mut rows = Vec::new();
        let mut drawn = Vec::new();

        for (storey_idx, (spaces, spaces_flat)) in by_storey.iter() {
            let storey = *storey_idx;
            let spaces = *spaces;
            let spaces_flat = *spaces_flat;
            let floor_y_cm = storey * storey_h;
            let player_y = t0_player_y(floor_y_cm);
            // Se sigue publicando la capa de WG2 porque es la columna que explica el fallo, pero
            // **el eje del sorteo es la PLANTA** desde T2, igual que en producción
            // (`wg3_player_storey`). Medir con la capa daría una población que el juego no reparte:
            // el eje entra en la semilla, así que no es sólo la densidad lo que cambia.
            let layer = world_pos_to_layer(player_y);
            // Y por debajo de cero no se reparte, que es lo que decidió T2 mientras no haya política.
            let Ok(axis) = u8::try_from(storey) else {
                rows.push(T0StoreyRow {
                    storey,
                    floor_y_cm,
                    spaces,
                    spaces_flat,
                    player_y,
                    layer,
                    adults_drawn: 0,
                    adults_kept: 0,
                    packs_drawn: 0,
                    pack_members: 0,
                });
                continue;
            };

            let mut adults_drawn = 0usize;
            let mut adults_kept = 0usize;
            let mut packs_drawn = 0usize;
            let mut pack_members = 0usize;

            for (cx, cz) in &chunks {
                draw_adults_into(
                    SERVED_SEED,
                    *cx,
                    *cz,
                    axis,
                    T0_DENSITY_SCALE,
                    true,
                    &mut drawn,
                );
                adults_drawn += drawn.len();
                for (index, pos) in drawn.iter().copied().enumerate() {
                    // Igual que `wg3_spawn_point`: el papel sale del espacio donde cae el hueco, y
                    // desde T1 ese espacio es el de LA PLANTA que se sortea, no el de más abajo. Un
                    // hueco cuya vertical no llega a esa planta no se queda con nadie.
                    let style = region
                        .space_on_storey_at_xz(pos[0], pos[2], storey)
                        .map(|s| s.style);
                    if wg3_keeps_position(SERVED_SEED, *cx, *cz, axis, index, style) {
                        adults_kept += 1;
                    }
                }

                draw_child_pack_into(
                    SERVED_SEED,
                    *cx,
                    *cz,
                    axis,
                    T0_DENSITY_SCALE,
                    true,
                    &mut drawn,
                );
                // LA MANADA SE DECIDE ENTERA, con el hueco de CABEZA, igual que producción
                // (ADR-109 D5 + T1): si la cabeza no tiene espacio en esta planta o su papel no la
                // concentra, no hay manada. Sin este filtro la sonda contaba manadas que el juego
                // nunca llega a soltar.
                if let Some(cabeza) = drawn.first().copied() {
                    let style = region
                        .space_on_storey_at_xz(cabeza[0], cabeza[2], storey)
                        .map(|s| s.style);
                    if style.is_some() && wg3_keeps_position(SERVED_SEED, *cx, *cz, axis, 0, style)
                    {
                        packs_drawn += 1;
                        pack_members += drawn.len();
                    }
                }
            }

            rows.push(T0StoreyRow {
                storey,
                floor_y_cm,
                spaces,
                spaces_flat,
                player_y,
                layer,
                adults_drawn,
                adults_kept,
                packs_drawn,
                pack_members,
            });
        }

        println!(
            "\n[T0] REGION ({rx},{rz}) - {} plantas servidas",
            rows.len()
        );
        println!("[T0] planta cota_cm espacios en_cota jugador_y capa_WG2 sorteados quedan manadas miembros");
        for r in &rows {
            println!(
                "[T0]   {:>4} {:>7} {:>8} {:>8} {:>9.2} {:>8} {:>9} {:>6} {:>7} {:>8}",
                r.storey,
                r.floor_y_cm,
                r.spaces,
                r.spaces_flat,
                r.player_y,
                r.layer,
                r.adults_drawn,
                r.adults_kept,
                r.packs_drawn,
                r.pack_members,
            );
        }
        all_rows.push((rx, rz, rows));
    }

    // ── El veredicto, impreso para que no haya que deducirlo de la tabla ──
    println!("\n[T0] ====== VEREDICTO ======");
    let mut poblada_baja = 0usize;
    let mut vacias_arriba = 0usize;
    let mut pobladas_arriba = 0usize;
    let mut espacios_arriba = 0usize;
    let mut bajo_cota = 0usize;
    let mut espacios_bajo_cota = 0usize;
    for (_rx, _rz, rows) in &all_rows {
        for r in rows {
            match r.storey.cmp(&0) {
                std::cmp::Ordering::Equal => {
                    if r.adults_kept > 0 {
                        poblada_baja += 1;
                    }
                }
                // POR ENCIMA de la planta baja: lo que este plan viene a poblar.
                std::cmp::Ordering::Greater => {
                    espacios_arriba += r.spaces;
                    match r.adults_kept + r.pack_members {
                        0 => vacias_arriba += 1,
                        _ => pobladas_arriba += 1,
                    }
                }
                // POR DEBAJO. Se cuenta aparte y no como «poblada»: una Y negativa satura a capa 0
                // al convertirse a `u8`, así que estas filas repiten la población de la planta baja
                // en vez de tener una propia. Meterlas en el mismo saco diría «hay plantas altas
                // pobladas» cuando no hay ninguna.
                std::cmp::Ordering::Less => {
                    bajo_cota += 1;
                    espacios_bajo_cota += r.spaces;
                }
            }
        }
    }
    println!(
        "[T0] plantas bajas con poblacion: {poblada_baja} de {}",
        all_rows.len()
    );
    println!(
        "[T0] plantas POR ENCIMA de la baja: {vacias_arriba} vacias, {pobladas_arriba} pobladas"
    );
    println!("[T0] espacios servidos por encima de la baja: {espacios_arriba}");
    println!(
        "[T0] plantas por DEBAJO de cota 0: {bajo_cota} ({espacios_bajo_cota} tramos) - ver nota"
    );

    // Lo ÚNICO que se exige: que haya mundo que medir. Sin esto, un cero de arriba podría ser
    // "no hay plantas altas" en vez de "las plantas altas están vacías", y son cosas distintas.
    let total_storeys: usize = all_rows.iter().map(|(_, _, r)| r.len()).sum();
    assert!(
        total_storeys > AUDIT_REGIONS.len(),
        "ninguna region audita mas de una planta: la sonda no tiene nada que medir"
    );
}

// ═══════════════════════════════════════════════════════════════════════════════
// T2 de PLAN-PLANTAS-ALTAS.md — EL EJE DE PLANTA, Y QUE NO COLAPSE NI SE TRAGUE LAS COTAS NEGATIVAS
//
// Estos SÍ exigen. Son el gate de T2: cada uno fija una de las formas en las que la capa de 4 m de
// WG2 mentía sobre un mundo de plantas de 3,32.
// ═══════════════════════════════════════════════════════════════════════════════

/// **La planta N es la planta N.** El caso base, y el que la capa de WG2 fallaba desde la 1.
#[test]
fn every_storey_maps_to_itself_and_not_to_a_wg2_layer() {
    use crate::world::grid_gen::world_pos_to_layer;
    use crate::world::wg3::plan::{storey_of_floor_cm, REGION_STOREYS, STOREY_HEIGHT_CM};

    for storey in 0..REGION_STOREYS as i32 {
        let floor_y_cm = storey * STOREY_HEIGHT_CM;
        assert_eq!(
            storey_of_floor_cm(floor_y_cm),
            storey,
            "la planta {storey} (cota {floor_y_cm} cm) no se clasifica en si misma"
        );
    }

    // Y la prueba de que esto NO es lo que hacia el eje viejo: con la capa de WG2, un jugador de pie
    // en la planta 1 daba capa 1 —densidad cero— y de la planta 4 en adelante todas colapsaban en la
    // capa 3. Si algun dia `world_pos_to_layer` dejara de hacer esto, este test lo dira y la
    // migracion podra simplificarse.
    let layer_of_storey =
        |s: i32| world_pos_to_layer(s as f32 * STOREY_HEIGHT_CM as f32 / 100.0 + 1.8);
    assert_ne!(
        layer_of_storey(1),
        0,
        "la capa de WG2 ya no confunde la planta 1: revisar si este eje sigue haciendo falta"
    );
    assert_eq!(
        (layer_of_storey(4), layer_of_storey(9)),
        (3, 3),
        "la capa de WG2 ha dejado de saturar: el motivo de este eje ha cambiado"
    );
}

/// **Las plantas altas NO colapsan.** Cuatro plantas distintas, cuatro respuestas distintas — que es
/// exactamente lo que la saturacion a `u8` de la capa de WG2 impedia de la planta 4 en adelante.
#[test]
fn upper_storeys_stay_distinct_instead_of_collapsing() {
    use crate::world::wg3::plan::{storey_of_floor_cm, REGION_STOREYS, STOREY_HEIGHT_CM};

    let seen: std::collections::BTreeSet<i32> = (0..REGION_STOREYS as i32)
        .map(|s| storey_of_floor_cm(s * STOREY_HEIGHT_CM))
        .collect();
    assert_eq!(
        seen.len(),
        REGION_STOREYS,
        "{} plantas distintas colapsan en {} valores",
        REGION_STOREYS,
        seen.len()
    );
}

/// **El canto de la losa es la planta de ARRIBA** (2026-09-06). La losa cuelga por debajo de la cota
/// de su planta, asi que un sitio de pie apoyado en el canto sale a `332 n - 12` y la division lo
/// mandaba una planta abajo: es la costura de 664 que se llevo por delante el respawn de una cama de
/// la planta 2. La regla vale de la planta 1 hacia arriba y ni un centimetro mas: por debajo de cero
/// manda `geometry_below_zero_is_not_the_ground_floor`, y en la costura de 332 viven las
/// contrahuellas de las escaleras de la planta baja.
#[test]
fn the_slab_edge_belongs_to_the_storey_above() {
    use crate::world::wg3::plan::{storey_of_floor_cm, STOREY_HEIGHT_CM};

    for n in 2..=5 {
        let line = n * STOREY_HEIGHT_CM;
        assert_eq!(
            storey_of_floor_cm(line),
            n,
            "la cota {line} es la planta {n}"
        );
        assert_eq!(
            storey_of_floor_cm(line - 12),
            n,
            "el canto de losa a {} cm tiene que ser la planta {n}",
            line - 12
        );
        assert_eq!(
            storey_of_floor_cm(line - 13),
            n - 1,
            "trece centimetros ya no son canto: {} cm es la planta {}",
            line - 13,
            n - 1
        );
    }
    // La costura de 332 no se toca: ahi estan las contrahuellas de la planta baja.
    assert_eq!(storey_of_floor_cm(STOREY_HEIGHT_CM - 12), 0);
    // Y por debajo de cero, tampoco.
    assert_eq!(storey_of_floor_cm(-12), -1);
    assert_eq!(storey_of_floor_cm(-1), -1);
}

/// **Una cota negativa NO es la planta 0.** El fallo que T0 destapó: `(y / 4.0) as u8` satura a 0 con
/// Y negativa, así que un jugador a −1,52 m se clasificaba en la planta baja y heredaba su población.
/// Hay 113 tramos por debajo de cero en las cuatro regiones auditadas, así que no es hipotético.
#[test]
fn geometry_below_zero_is_not_the_ground_floor() {
    use crate::world::grid_gen::world_pos_to_layer;
    use crate::world::wg3::plan::{storey_of_floor_cm, STOREY_HEIGHT_CM};

    for cota in [-1, -12, -60, -STOREY_HEIGHT_CM, -2 * STOREY_HEIGHT_CM] {
        assert!(
            storey_of_floor_cm(cota) < 0,
            "la cota {cota} cm se clasifica en la planta {} y deberia ser negativa",
            storey_of_floor_cm(cota)
        );
    }

    // El eje viejo, en cambio, las metia en la planta baja. Es la razon por la que la sonda de T0
    // dijo «4 plantas altas pobladas» cuando las pobladas eran cero.
    assert_eq!(
        world_pos_to_layer(-1.52),
        0,
        "si la capa de WG2 ya no satura en negativo, revisar la nota de T0"
    );
}

/// **Y el que cierra el gate: una cota negativa queda FUERA del reparto, no dentro de la planta 0.**
///
/// Se mide sobre el mundo servido y no sobre una maqueta: la region (0,0) tiene tramos por debajo de
/// cero de verdad (peldaños y conectores que bajan), y lo que se exige es que ninguno de ellos se
/// clasifique en una planta que reciba poblacion.
#[test]
fn no_served_segment_below_zero_lands_on_a_populated_storey() {
    use crate::world::wg3::plan::storey_of_floor_cm;

    let m = real_manifest();
    let mut bajo_cero = 0usize;
    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3ServedWorld::plan_region(&m, SERVED_SEED, Wg3RegionCoord { x: rx, z: rz });
        for s in region.segments() {
            if s.floor_y_cm < 0 {
                bajo_cero += 1;
                assert!(
                    storey_of_floor_cm(s.floor_y_cm) < 0,
                    "({rx},{rz}): un tramo a {} cm se clasifica en la planta {}, que si recibe poblacion",
                    s.floor_y_cm,
                    storey_of_floor_cm(s.floor_y_cm)
                );
            }
        }
    }
    // Que la sonda no este ciega: si el mundo dejara de tener geometria bajo cero, este test pasaria
    // sin comprobar nada y habria que enterarse.
    assert!(
        bajo_cero > 0,
        "ninguna region servida tiene geometria bajo la cota 0: este test ya no mide nada"
    );
}

/// **T0 (b) — LA TABLA DE VERDAD DE `world_pos_to_layer` CONTRA LAS PLANTAS DE WG3.**
///
/// Aísla el mecanismo: qué capa de WG2 le toca a un jugador de pie en cada planta de WG3, y qué
/// densidad tiene esa capa en las tablas que gobiernan el sorteo. Es la sonda que dice si la causa
/// es ésta o hay que buscar en otro sitio.
#[test]
#[ignore = "sonda: imprime, no exige"]
fn probe_wg3_storey_to_wg2_layer() {
    use crate::world::faceling_spawn::{
        FACELING_ADULT_LAYER_DENSITY, FACELING_CHILD_PACK_LAYER_PROBABILITY,
    };
    use crate::world::grid_gen::world_pos_to_layer;
    use crate::world::phantom_spawn::PHANTOM_LAYER_DENSITY;

    println!(
        "\n[T0] planta cota_m jugador_y capa_WG2 densidad_adultos prob_manada densidad_robapieles"
    );
    let mut primera_muda: Option<usize> = None;
    for storey in 0..crate::world::wg3::plan::REGION_STOREYS {
        let floor_y_cm = storey as i32 * crate::world::wg3::plan::STOREY_HEIGHT_CM;
        let player_y = t0_player_y(floor_y_cm);
        let layer = world_pos_to_layer(player_y);
        let adult = FACELING_ADULT_LAYER_DENSITY
            .get(layer as usize)
            .copied()
            .unwrap_or(0.0);
        let pack = FACELING_CHILD_PACK_LAYER_PROBABILITY
            .get(layer as usize)
            .copied()
            .unwrap_or(0.0);
        let phantom = PHANTOM_LAYER_DENSITY
            .get(layer as usize)
            .copied()
            .unwrap_or(0.0);
        println!(
            "[T0]   {:>4} {:>6.2} {:>9.2} {:>8} {:>16.2} {:>11.2} {:>19.2}",
            storey,
            floor_y_cm as f32 / 100.0,
            player_y,
            layer,
            adult,
            pack,
            phantom
        );
        if adult == 0.0 && primera_muda.is_none() {
            primera_muda = Some(storey);
        }
    }
    match primera_muda {
        Some(s) => println!(
            "[T0] == PRIMERA PLANTA MUDA: la {s}. De ahi para arriba el sorteo devuelve cero antes de mirar el mundo."
        ),
        None => println!("[T0] == ninguna planta queda muda por la capa."),
    }
}

/// **T0 (c) — ¿LA RETIRADA POR CAPA DESALOJA AL SUBIR DE PLANTA?**
///
/// `AdultDriver::sync_population` retira con `world_pos_to_layer(p.y) != m.layer` y `ChildDriver`
/// con `!= pack.layer`. Si dos plantas de WG3 caen en capas distintas, subir una planta hace que
/// TODO lo que estaba activo en la de abajo deje de estarlo — que es un síntoma distinto de "arriba
/// no nace nadie" y que se nota más, porque vacía lo que ya estabas viendo.
#[test]
#[ignore = "sonda: imprime, no exige"]
fn probe_storey_change_evicts_by_layer() {
    use crate::world::grid_gen::world_pos_to_layer;

    let storeys = 6usize;
    println!("\n[T0] subir de planta N a N+1: cambia la capa y desaloja lo de la planta N?");
    let mut desalojos = 0usize;
    for storey in 0..storeys.saturating_sub(1) {
        let here = world_pos_to_layer(t0_player_y(
            storey as i32 * crate::world::wg3::plan::STOREY_HEIGHT_CM,
        ));
        let up = world_pos_to_layer(t0_player_y(
            (storey + 1) as i32 * crate::world::wg3::plan::STOREY_HEIGHT_CM,
        ));
        let evicts = here != up;
        if evicts {
            desalojos += 1;
        }
        println!(
            "[T0]   planta {storey} (capa {here}) -> planta {} (capa {up}): {}",
            storey + 1,
            match evicts {
                true => "DESALOJA lo de la planta de abajo",
                false => "conserva",
            }
        );
    }
    println!(
        "[T0] == transiciones que desalojan: {desalojos} de {}",
        storeys - 1
    );
}

// ═══════════════════════════════════════════════════════════════════════════════
// T1 de PLAN-PLANTAS-ALTAS.md — LOS ESPACIOS DE WG3 COMO AUTORIDAD DEL REPARTO
// ═══════════════════════════════════════════════════════════════════════════════

/// **Una consulta por planta devuelve espacios de ESA planta y de ninguna otra.**
///
/// Es lo que `lowest_space_at_xz` no podía dar y por lo que el reparto nacía siempre abajo.
#[test]
fn a_storey_query_never_mixes_spaces_from_other_storeys() {
    use crate::world::wg3::plan::storey_of_floor_cm;

    let m = real_manifest();
    let mut comprobados = 0usize;
    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3ServedWorld::plan_region(&m, SERVED_SEED, Wg3RegionCoord { x: rx, z: rz });
        // Centros de tramos reales: puntos que sabemos que caen dentro de algo.
        for seg in region.segments().iter().take(120) {
            let x = (seg.x_cm + seg.size_x_cm / 2) as f32 / 100.0;
            let z = (seg.z_cm + seg.size_z_cm / 2) as f32 / 100.0;
            for storey in -1..5i32 {
                if let Some(s) = region.space_on_storey_at_xz(x, z, storey) {
                    assert_eq!(
                        storey_of_floor_cm(s.floor_y_cm),
                        storey,
                        "({rx},{rz}) pidiendo la planta {storey} contesta un espacio a {} cm",
                        s.floor_y_cm
                    );
                    comprobados += 1;
                }
            }
        }
    }
    assert!(comprobados > 0, "ninguna consulta resolvio un espacio");
}

/// **Ni espacios fantasma ni espacios perdidos**: `spaces_at_xz` devuelve exactamente los tramos que
/// contienen el punto, ordenados de abajo arriba, y su primero es el que daba `lowest_space_at_xz`.
#[test]
fn spaces_at_xz_is_ordered_and_agrees_with_the_lowest() {
    let m = real_manifest();
    let mut con_varias = 0usize;
    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3ServedWorld::plan_region(&m, SERVED_SEED, Wg3RegionCoord { x: rx, z: rz });
        for seg in region.segments().iter().take(150) {
            let x = (seg.x_cm + seg.size_x_cm / 2) as f32 / 100.0;
            let z = (seg.z_cm + seg.size_z_cm / 2) as f32 / 100.0;
            let all = region.spaces_at_xz(x, z);
            assert!(
                !all.is_empty(),
                "({rx},{rz}) el centro de un tramo no resuelve ni un espacio"
            );
            // Orden por cota, de abajo arriba.
            for w in all.windows(2) {
                assert!(
                    w[0].floor_y_cm <= w[1].floor_y_cm,
                    "({rx},{rz}) spaces_at_xz devuelve las cotas desordenadas"
                );
            }
            // Sin fantasmas: todo lo devuelto contiene de verdad el punto.
            let x_cm = (x * 100.0).round() as i32;
            let z_cm = (z * 100.0).round() as i32;
            for s in &all {
                assert!(
                    x_cm >= s.x_cm
                        && x_cm <= s.x_cm + s.size_x_cm
                        && z_cm >= s.z_cm
                        && z_cm <= s.z_cm + s.size_z_cm,
                    "({rx},{rz}) spaces_at_xz devuelve un espacio que no contiene el punto"
                );
            }
            // Y el primero es el mismo que contestaba la funcion vieja.
            if let Some(low) = region.lowest_space_at_xz(x, z) {
                assert_eq!(
                    all[0].floor_y_cm, low.floor_y_cm,
                    "({rx},{rz}) el primero de spaces_at_xz no es el mas bajo"
                );
            }
            if all.len() > 1 {
                con_varias += 1;
            }
        }
    }
    // Si ninguna vertical tuviera mas de un espacio, este test no estaria midiendo un mundo apilado.
    assert!(
        con_varias > 0,
        "ninguna vertical tiene mas de un espacio: no hay plantas que distinguir"
    );
}

/// **Las plantas altas tienen espacios utilizables donde de verdad existen.** No basta con que la
/// consulta sea correcta: si el mundo servido no ofreciera espacio en las plantas altas, el reparto
/// de T3 no tendria donde poner a nadie y el problema seria otro.
#[test]
fn upper_storeys_offer_usable_spaces_where_they_exist() {
    use crate::world::wg3::plan::storey_of_floor_cm;

    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3ServedWorld::plan_region(&m, SERVED_SEED, Wg3RegionCoord { x: rx, z: rz });
        let mut por_planta: std::collections::BTreeMap<i32, usize> = Default::default();
        for s in region.segments() {
            *por_planta
                .entry(storey_of_floor_cm(s.floor_y_cm))
                .or_default() += 1;
        }
        // Toda planta por encima de la baja que exista tiene que poder resolverse por consulta.
        for (storey, count) in por_planta.iter().filter(|(s, _)| **s > 0) {
            let seg = region
                .segments()
                .iter()
                .find(|s| storey_of_floor_cm(s.floor_y_cm) == *storey)
                .expect("la planta se conto a partir de sus tramos");
            let x = (seg.x_cm + seg.size_x_cm / 2) as f32 / 100.0;
            let z = (seg.z_cm + seg.size_z_cm / 2) as f32 / 100.0;
            assert!(
                region.space_on_storey_at_xz(x, z, *storey).is_some(),
                "({rx},{rz}) la planta {storey} tiene {count} tramos y no resuelve ni uno"
            );
        }
    }
}

/// **T3 de PLAN-PLANTAS-ALTAS.md — DE DÓNDE SALE EL VACÍO DE LAS PLANTAS ALTAS.**
///
/// Con T2 (eje de planta) y T1 (espacio de esa planta) puestos, arriba sigue sin nacer casi nadie.
/// Esta sonda separa las TRES causas posibles para no calibrar a ciegas:
///
/// 1. **Cobertura** — cuántos de los huecos sorteados tienen espacio EN esa planta. El edificio se
///    estrecha al subir (ADR-102 D3), así que la planta 4 ocupa una fracción de la huella de la 0.
/// 2. **Papel** — de los que sí tienen espacio, cuántos caen en oficina (`style` 0).
/// 3. **El filtro de concentración** — cuántos sobreviven a `FACELING_WG3_OFFICE_KEEP`, que se
///    calibró contra una planta baja y contra un mundo de UNA planta poblada.
#[test]
#[ignore = "sonda: imprime, no exige"]
fn probe_storey_coverage_and_population() {
    use crate::world::faceling_spawn::{draw_adults_into, wg3_keeps_position};
    use crate::world::wg3::plan::storey_of_floor_cm;

    let m = real_manifest();
    println!(
        "\n[T3] planta  sorteados  con_espacio  cobertura  oficina  quedan  esperado_por_region"
    );
    let mut tot_por_planta: std::collections::BTreeMap<i32, (usize, usize, usize, usize)> =
        Default::default();

    // **Muestra ancha a propósito.** Con las cuatro regiones de auditoría, una planta con el 8 % de
    // cobertura espera 1,1 adultos y sacar CERO no distingue «vacía por pequeña» de «vacía por un
    // fallo». 25 regiones dan cuentas con las que se puede DECIDIR el modelo de densidad en vez de
    // adivinarlo — que es para lo que existe esta sonda.
    const T3_REGIONS: usize = 25;
    let regiones: Vec<(i32, i32)> = (0..5).flat_map(|x| (0..5).map(move |z| (x, z))).collect();
    for (rx, rz) in regiones.iter().copied() {
        let region = Wg3ServedWorld::plan_region(&m, SERVED_SEED, Wg3RegionCoord { x: rx, z: rz });
        let mut plantas: std::collections::BTreeSet<i32> = Default::default();
        for s in region.segments() {
            plantas.insert(storey_of_floor_cm(s.floor_y_cm));
        }
        let chunks = t0_wg2_chunks_of_region(rx, rz);
        let mut drawn = Vec::new();

        for storey in plantas.iter().copied().filter(|s| *s >= 0) {
            let axis = storey as u8;
            let (mut sorteados, mut con_espacio, mut oficina, mut quedan) = (0, 0, 0, 0);
            for (cx, cz) in &chunks {
                draw_adults_into(
                    SERVED_SEED,
                    *cx,
                    *cz,
                    axis,
                    T0_DENSITY_SCALE,
                    true,
                    &mut drawn,
                );
                for (index, pos) in drawn.iter().copied().enumerate() {
                    sorteados += 1;
                    let style = region
                        .space_on_storey_at_xz(pos[0], pos[2], storey)
                        .map(|s| s.style);
                    if style.is_some() {
                        con_espacio += 1;
                    }
                    if style == Some(0) {
                        oficina += 1;
                    }
                    if wg3_keeps_position(SERVED_SEED, *cx, *cz, axis, index, style) {
                        quedan += 1;
                    }
                }
            }
            let e = tot_por_planta.entry(storey).or_default();
            e.0 += sorteados;
            e.1 += con_espacio;
            e.2 += oficina;
            e.3 += quedan;
        }
    }

    for (storey, (sorteados, con_espacio, oficina, quedan)) in &tot_por_planta {
        let cobertura = match sorteados {
            0 => 0.0,
            n => *con_espacio as f32 / *n as f32 * 100.0,
        };
        println!(
            "[T3]   {:>4}  {:>9}  {:>11}  {:>8.1}%  {:>7}  {:>6}  {:>19.2}",
            storey,
            sorteados,
            con_espacio,
            cobertura,
            oficina,
            quedan,
            *quedan as f32 / T3_REGIONS as f32
        );
    }
    let total: usize = tot_por_planta.values().map(|v| v.3).sum();
    let baja = tot_por_planta.get(&0).map(|v| v.3).unwrap_or(0);
    println!(
        "[T3] == total {total} en {T3_REGIONS} regiones · planta baja {baja} · plantas altas {}",
        total - baja
    );
}

/// **EL GATE DE T3 — la distribución vertical, con las tres formas de romperla vigiladas.**
///
/// La banda de ±25 % de ADR-109 D5 vigila la PLANTA BAJA contra WG2 y sigue haciéndolo
/// (`probe_faceling_draw_under_wg3`). Lo que no vigilaba nadie es lo que T3 decidió: que las plantas
/// altas se pueblen, que no se saturen, y que la baja no se vacíe para conseguirlo.
#[test]
fn the_vertical_population_is_intentional() {
    use crate::world::faceling_spawn::{draw_adults_into, wg3_keeps_position};
    use crate::world::wg3::plan::storey_of_floor_cm;

    let m = real_manifest();
    // Misma muestra ancha que la sonda que decidió el modelo: con cuatro regiones, una planta alta
    // saca cuentas de un dígito y el ruido del sorteo pesa más que el modelo.
    let regiones: Vec<(i32, i32)> = (0..5).flat_map(|x| (0..5).map(move |z| (x, z))).collect();
    let mut por_planta: std::collections::BTreeMap<i32, usize> = Default::default();
    let mut drawn = Vec::new();

    for (rx, rz) in regiones.iter().copied() {
        let region = Wg3ServedWorld::plan_region(&m, SERVED_SEED, Wg3RegionCoord { x: rx, z: rz });
        let mut plantas: std::collections::BTreeSet<i32> = Default::default();
        for s in region.segments() {
            plantas.insert(storey_of_floor_cm(s.floor_y_cm));
        }
        for storey in plantas.iter().copied().filter(|s| *s >= 0) {
            let axis = storey as u8;
            for (cx, cz) in t0_wg2_chunks_of_region(rx, rz) {
                draw_adults_into(SERVED_SEED, cx, cz, axis, 1.0, true, &mut drawn);
                for (index, pos) in drawn.iter().copied().enumerate() {
                    let style = region
                        .space_on_storey_at_xz(pos[0], pos[2], storey)
                        .map(|s| s.style);
                    if wg3_keeps_position(SERVED_SEED, cx, cz, axis, index, style) {
                        *por_planta.entry(storey).or_default() += 1;
                    }
                }
            }
        }
    }

    let baja = por_planta.get(&0).copied().unwrap_or(0);
    let altas: usize = por_planta
        .iter()
        .filter(|(s, _)| **s > 0)
        .map(|(_, n)| n)
        .sum();
    let bajo_cero: usize = por_planta
        .iter()
        .filter(|(s, _)| **s < 0)
        .map(|(_, n)| n)
        .sum();

    // 1. La planta baja NO se sacrifica. Es la mitad de la decisión de T3 y la que se rompe sola si
    //    alguien busca simetría vertical bajando la densidad base.
    assert!(
        baja >= 40,
        "la planta baja se ha quedado en {baja} sobre 25 regiones: se esta vaciando para poblar arriba"
    );

    // 2. Las plantas altas SÍ se pueblan. Es lo que T0 midió a cero y lo que T2+T1 vienen a arreglar.
    assert!(
        altas > 0,
        "ninguna planta por encima de la baja recibe poblacion: T2/T1 han dejado de hacer efecto"
    );

    // 3. Y NO se saturan. El edificio se estrecha al subir, asi que la suma de todo lo que hay
    //    arriba tiene que seguir siendo menor que la planta baja sola. Si esto se invierte, el mundo
    //    tiene mas gente en las torres que en la calle y el reparto ha dejado de seguir la geometria.
    assert!(
        altas < baja,
        "las plantas altas suman {altas} contra {baja} de la baja: el reparto vertical esta saturado"
    );

    // 4. Y bajo la cota 0 no nace nadie, que es lo que T2 dejo explicitamente fuera.
    assert_eq!(
        bajo_cero, 0,
        "han nacido {bajo_cero} criaturas bajo la cota 0, que no tiene politica de poblacion"
    );
}

// ═══════════════════════════════════════════════════════════════════════════════
// T4 de PLAN-PLANTAS-ALTAS.md — EL ROBAPIELES, QUE NO ES UNA COPIA DEL FACELING
// ═══════════════════════════════════════════════════════════════════════════════

/// **El robapieles puede nacer en las plantas válidas, y su cuenta NO se compara contra una banda.**
///
/// Es UNO, no una población: lo que hay que exigirle es que siga existiendo por encima de la planta
/// baja, no que su censo case con WG2. La densidad plana de T3 aplicada a su propia constante.
#[test]
fn the_phantom_draw_reaches_upper_storeys() {
    use crate::world::phantom_spawn::{block_of, draw_into};

    let mut por_planta: std::collections::BTreeMap<u8, usize> = Default::default();
    let mut drawn = Vec::new();
    let (bx1, bz1) = block_of(600.0, 600.0);
    for storey in 0..5u8 {
        for bx in 0..=bx1 {
            for bz in 0..=bz1 {
                draw_into(SERVED_SEED, (bx, bz), storey, 1.0, true, &mut drawn);
                *por_planta.entry(storey).or_default() += drawn.len();
            }
        }
    }
    for storey in 0..5u8 {
        let n = por_planta.get(&storey).copied().unwrap_or(0);
        assert!(
            n > 0,
            "la planta {storey} no sortea ni un robapieles: el eje sigue siendo la capa de WG2"
        );
    }
    // Y con WG2 la tabla vieja sigue mandando, intacta: capas 1-3 a cero (ADR-043 D-TABLA).
    for layer in 1..4u8 {
        let mut wg2 = Vec::new();
        let mut total = 0usize;
        for bx in 0..=bx1 {
            for bz in 0..=bz1 {
                draw_into(SERVED_SEED, (bx, bz), layer, 1.0, false, &mut wg2);
                total += wg2.len();
            }
        }
        assert_eq!(
            total, 0,
            "la capa {layer} de WG2 se ha poblado: su semantica tenia que quedar intacta"
        );
    }
}

// ---------------------------------------------------------------------------------------------
// ADR-103 — el campo de identidad. De sus seis tests sobrevive UNO, y con la mitad de su cuerpo.
// El 2026-09-05 (bloque B4 del saneamiento) se borro el campo de mezcla entero de `identity.rs`
// —`LevelAnchor`, `LevelProfile`, `ANCHOR_PROFILES`, `LevelMix`, `at`, `for_region`,
// `nearest_anchor`, `max_band_width_mul`— por no tener un solo consumidor de produccion. Con el se
// fueron `the_identity_field_is_pure_position`, `pure_threshold_is_all_ones_exactly`,
// `band_width_never_shrinks_and_the_max_is_known`, `the_mix_brackets_neighbours_and_pure_poles_exist`
// y `the_identity_mirror_golden_values`: los cinco afirmaban sobre codigo que ya no existe.
//
// El sexto, `an_identity_cell_never_splits_a_region`, medía dos cosas a la vez —que ninguna region
// cae a caballo de dos celdas, y que la celda mide 6 regiones—. La primera se iba con `at()`; la
// segunda es la que se queda, porque `IDENTITY_CELL_M` sigue vivo: lo lee
// `world::spawn_distribution`, que reparte los spawns una celda por jugador.
//
// OJO al reescribir el campo: `the_identity_mirror_golden_values` era el oraculo que ataba este
// lado con `Wg3IdentityTests.cs` bit a bit. El lado C# sigue en pie y sigue verde por su cuenta,
// pero ya no hay nada que lo contraste.
// ---------------------------------------------------------------------------------------------

use super::identity;

/// D4 — la celda de identidad es multiplo EXACTO de la region: 6 x 150 m. Es lo que garantiza que
/// la frontera de identidad sea siempre frontera de region, o sea un sitio donde el mundo ya
/// cambia de mano y ya hay contrato.
#[test]
fn an_identity_cell_is_an_exact_multiple_of_a_region() {
    use super::world::REGION_M;
    assert_eq!(identity::IDENTITY_CELL_M, 6.0 * REGION_M);
    assert_eq!(identity::IDENTITY_CELL_REGIONS, 6);
}

// ── ADR-116 / ADR-045 enm. 1: el reparto y la posición restaurada, contra el mundo servido ──

/// ADR-116 D6 — un punto repartido tiene que poder pisarse, y **en su planta**.
///
/// Esta es la sonda que decide si el reparto funciona en la práctica o si el fallback de D7 acaba
/// siendo el camino normal: `standable_near_bounded(.., same_storey = true)` sólo acepta suelo de la
/// planta pedida, y un punto sorteado a ciegas puede caer sobre un vacío de la planta baja.
#[test]
fn los_puntos_repartidos_tienen_sitio_de_pie() {
    use crate::world::spawn_distribution::choose_spawn;
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::wg3::plan::storey_of_floor_cm;
    use crate::world::wg3::world::Wg3WorldCache;

    const PLAYER_BODY_M: f32 = 1.8;

    let m = real_manifest();
    let mut asignados: Vec<crate::world::Vec3> = Vec::new();
    let mut con_sitio = 0usize;
    let unidades = 9;

    for unit in 0..unidades {
        let Some(p) = choose_spawn(SERVED_SEED, unit, &asignados) else {
            continue; // sin candidato: D7, y eso ya lo cubre el test puro del módulo
        };
        asignados.push(p);

        let mut worlds = Wg3WorldCache::default();
        let mut cache = Wg3CollisionCache::new();
        cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, p, p);
        let Some(spawn) = cache.standable_near_bounded(p, true) else {
            println!(
                "[reparto] unidad {unit} sin sitio de pie en ({:.0},{:.0})",
                p.x, p.z
            );
            continue;
        };

        assert!(
            !cache.blocked_at(spawn, crate::world::collision::PLAYER_RADIUS),
            "unidad {unit}: el punto repartido cayó DENTRO de algo, que con el ráster mandando es \
             aparecer atascado"
        );
        // D6: la planta pedida es la que se conserva. Un reparto que baja de planta sola es el
        // mismo fallo que ADR-110 D3 arregló en las criaturas.
        let pedida = storey_of_floor_cm(((p.y - PLAYER_BODY_M) * 100.0).round() as i32);
        let dada = storey_of_floor_cm(((spawn.y - PLAYER_BODY_M) * 100.0).round() as i32);
        assert_eq!(
            pedida, dada,
            "unidad {unit}: el reparto se cambió de planta"
        );

        con_sitio += 1;
        println!(
            "[reparto] unidad {unit} de pie en ({:.1}, {:.2}, {:.1})",
            spawn.x, spawn.y, spawn.z
        );
    }

    println!("[reparto] {con_sitio} de {unidades} unidades con sitio de pie");
    // MEDIDO el 2026-09-01 sobre `SERVED_SEED`: **9 de 9**, las nueve en la planta baja. El suelo
    // se pone en 7 y no en 9 para dejar sitio a que el plan cambie sin volver rojo por un punto,
    // pero por debajo de eso el fallback de D7 habría dejado de ser la excepción y sería el camino
    // normal — o sea, todos naciendo en el origen con el reparto puesto.
    assert!(
        con_sitio >= 7,
        "sólo {con_sitio} de {unidades} unidades encontraron sitio (medido 9/9): el reparto se \
         estaría degradando al fallback de D7"
    );
}

/// ADR-045 enm. 1 E1.2 — una posición restaurada DENTRO de un macizo se corrige.
///
/// Con WG3 mandando, el macizo no se cruza: no deja flotando, deja ATASCADO. Hasta esta enmienda
/// ninguna de las dos rutas de restauración pasaba por el ráster.
#[test]
fn una_posicion_restaurada_dentro_de_un_macizo_se_corrige() {
    use crate::world::collision::PLAYER_RADIUS;
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::wg3::world::Wg3WorldCache;
    use crate::world::Vec3;

    const PLAYER_BODY_M: f32 = 1.8;

    let m = real_manifest();
    let region = Wg3RegionCoord { x: 0, z: 0 };
    let (min_x, min_z, max_x, max_z) = region.bounds();
    let centre = Vec3::new((min_x + max_x) * 0.5, PLAYER_BODY_M, (min_z + max_z) * 0.5);

    let mut worlds = Wg3WorldCache::default();
    let mut cache = Wg3CollisionCache::new();
    cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, centre, centre);

    // Se BUSCA un punto macizo en vez de inventarlo: una posición guardada dentro de una pared es
    // exactamente esto, y hardcodear coordenadas ataría el test a una semilla concreta del plan.
    let mut atascado = None;
    'buscar: for dx in -40..=40 {
        for dz in -40..=40 {
            let p = Vec3::new(
                centre.x + dx as f32 * 1.0,
                centre.y,
                centre.z + dz as f32 * 1.0,
            );
            if cache.blocked_at(p, PLAYER_RADIUS) {
                atascado = Some(p);
                break 'buscar;
            }
        }
    }
    let Some(atascado) = atascado else {
        println!("[restaurada] sin macizo a 40 m del centro: nada que corregir en esta semilla");
        return;
    };

    let Some(corregida) = cache.standable_near_bounded(atascado, true) else {
        panic!(
            "una posición restaurada en ({:.1},{:.1}) no encontró salida: el jugador se quedaría \
             atascado y eso es justo lo que la enmienda evita",
            atascado.x, atascado.z
        );
    };
    assert!(
        !cache.blocked_at(corregida, PLAYER_RADIUS),
        "la corrección dejó al jugador dentro de otra cosa"
    );
    println!(
        "[restaurada] ({:.1},{:.1}) macizo -> ({:.1},{:.2},{:.1})",
        atascado.x, atascado.z, corregida.x, corregida.y, corregida.z
    );
}

/// **LA REGLA DE MEDIR LA FORMA.** Una tabla de números comparable entre iteraciones.
///
/// Existe porque la pregunta de esta tanda —«¿sigue leyéndose como una cuadrícula BSP?»— es visual, y
/// tunear una gramática mirando sólo el volcado es tunear a ciegas: se mueve un número, la imagen
/// cambia un poco y no hay forma de saber si el cambio fue el que se buscaba. Estas seis cifras no
/// sustituyen al ojo; le ponen un raíl.
///
/// - **compuestos**: qué fracción de los espacios construidos tiene más de una parte. Es el
///   rendimiento bruto de la composición de huellas.
/// - **área deformada**: qué fracción del suelo construido está en un espacio compuesto. Un 5 % de
///   espacios compuestos que son los trasteros no se ve; el mismo 5 % en las naves sí.
/// - **rectangularidad**: área de la huella entre área de su envolvente, promediada por ÁREA. Un
///   rectángulo vale 1,00. Es la cifra que dice si el mundo son cajas.
/// - **compacidad**: perímetro² / (16·área) de la huella, promediada por área. Un cuadrado vale
///   1,00; una L larga, 1,3-1,6. Sube cuando la forma se alarga o se quiebra.
/// - **bandas compuestas**: cuántas de las de circulación tienen bahía. Es lo único que rompe la
///   retícula que cruza la región.
/// - **líneas largas**: cuántas caras de espacio caen sobre la misma coordenada a lo largo de más de
///   media región. **Es la métrica de «cuadrícula»**: lo que delata a un BSP no es que cada sala sea
///   un rectángulo, es que el corte de guillotina deja una LÍNEA recta compartida por todas las
///   hojas de su subárbol.
///
/// `WG3_SWEEP_SEEDS=N cargo test --release shape_metrics -- --ignored --nocapture`.
#[test]
#[ignore]
fn shape_metrics() {
    let seeds: Vec<u64> = match std::env::var("WG3_SWEEP_SEEDS")
        .ok()
        .and_then(|v| v.parse::<usize>().ok())
    {
        Some(n) => super::validate::sweep_seeds(n),
        None => vec![SERVED_SEED],
    };
    let regions: Vec<(i32, i32)> = AUDIT_REGIONS.to_vec();

    let (mut n_built, mut n_comp) = (0usize, 0usize);
    let (mut a_built, mut a_comp) = (0f64, 0f64);
    let (mut rect_w, mut comp_w) = (0f64, 0f64);
    let (mut n_band, mut n_band_comp) = (0usize, 0usize);
    let mut long_lines = 0usize;
    let mut storeys = 0usize;

    for &seed in &seeds {
        for &(rx, rz) in &regions {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let bounds = region.bounds();
            let gates = junction::gates_of_region(composer_seed(seed), rx, rz, bounds);
            let building = plan::plan_building(region.composer_seed(seed), bounds, &gates, STOREYS);
            for p in &building.storeys {
                storeys += 1;
                // Las caras por eje, para las líneas largas. En centímetros y por valor exacto: dos
                // hojas hermanas del mismo corte comparten la coordenada al centímetro.
                // Los INTERVALOS de cada coordenada, no su suma: lo que el ojo lee es un tramo
                // recto sin interrumpir, y una línea de 150 m con un mordisco en medio son dos de 75
                // aunque la suma no se mueva. Medir la suma daba 20,4 antes y 20,6 después de subir
                // la presión sobre las líneas: la métrica no veía el trabajo.
                let mut faces_x: std::collections::HashMap<i32, Vec<(i32, i32)>> =
                    std::collections::HashMap::new();
                let mut faces_z: std::collections::HashMap<i32, Vec<(i32, i32)>> =
                    std::collections::HashMap::new();
                for (_, s) in p.built() {
                    let a = s.area_m2() as f64;
                    n_built += 1;
                    a_built += a;
                    if s.is_composite() {
                        n_comp += 1;
                        a_comp += a;
                    }
                    if s.role.is_circulation() {
                        n_band += 1;
                        if s.is_composite() {
                            n_band_comp += 1;
                        }
                    }
                    let env = s.rect.area_m2() as f64;
                    rect_w += a * (a / env.max(1.0));
                    // Perímetro de la huella: suma de los de las partes menos el doble de lo que se
                    // tocan. Con una parte es el del rectángulo, y ahí la compacidad vale 1 en un
                    // cuadrado por construcción.
                    let mut per = 0f64;
                    for r in s.parts() {
                        per += 2.0 * ((r.width_cm() + r.depth_cm()) as f64) / 100.0;
                    }
                    let ps = s.parts();
                    for i in 0..ps.len() {
                        for j in (i + 1)..ps.len() {
                            let ox = (ps[i].max_x_cm.min(ps[j].max_x_cm)
                                - ps[i].min_x_cm.max(ps[j].min_x_cm))
                            .max(0);
                            let oz = (ps[i].max_z_cm.min(ps[j].max_z_cm)
                                - ps[i].min_z_cm.max(ps[j].min_z_cm))
                            .max(0);
                            let touch = if ps[i].max_x_cm == ps[j].min_x_cm
                                || ps[j].max_x_cm == ps[i].min_x_cm
                            {
                                oz
                            } else if ps[i].max_z_cm == ps[j].min_z_cm
                                || ps[j].max_z_cm == ps[i].min_z_cm
                            {
                                ox
                            } else {
                                0
                            };
                            per -= 2.0 * (touch as f64) / 100.0;
                        }
                    }
                    comp_w += a * (per * per / (16.0 * a.max(1.0)));

                    // **Sólo las caras que son PARED.** Una cara entre dos partes del MISMO espacio
                    // es interior: ahí no hay muro, hay sala. Contándolas, la línea de guillotina
                    // seguía apareciendo entera detrás de cada bahía y la métrica daba exactamente lo
                    // mismo con la composición encendida y apagada —7,1 y 7,1—, que es lo que hizo
                    // ver que estaba midiendo bordes de rectángulo y no arquitectura.
                    let ps = s.parts();
                    for r in ps {
                        for (k, hi_side) in [(r.min_x_cm, false), (r.max_x_cm, true)] {
                            let mut segs = vec![(r.min_z_cm, r.max_z_cm)];
                            for o in ps {
                                let adj = if hi_side {
                                    o.min_x_cm == k
                                } else {
                                    o.max_x_cm == k
                                };
                                if !adj {
                                    continue;
                                }
                                segs = subtract(&segs, o.min_z_cm, o.max_z_cm);
                            }
                            faces_x.entry(k).or_default().extend(segs);
                        }
                        for (k, hi_side) in [(r.min_z_cm, false), (r.max_z_cm, true)] {
                            let mut segs = vec![(r.min_x_cm, r.max_x_cm)];
                            for o in ps {
                                let adj = if hi_side {
                                    o.min_z_cm == k
                                } else {
                                    o.max_z_cm == k
                                };
                                if !adj {
                                    continue;
                                }
                                segs = subtract(&segs, o.min_x_cm, o.max_x_cm);
                            }
                            faces_z.entry(k).or_default().extend(segs);
                        }
                    }
                }
                // Media región, en centímetros. Una cara acumulada por encima de eso es una línea de
                // guillotina que cruza medio plano.
                let half = (REGION_M * 100.0 / 2.0) as i32;
                /// Lo que queda de estos tramos al quitarles `[a, b)`.
                fn subtract(segs: &[(i32, i32)], a: i32, b: i32) -> Vec<(i32, i32)> {
                    let mut out = Vec::new();
                    for &(s0, s1) in segs {
                        if b <= s0 || a >= s1 {
                            out.push((s0, s1));
                            continue;
                        }
                        if s0 < a {
                            out.push((s0, a));
                        }
                        if b < s1 {
                            out.push((b, s1));
                        }
                    }
                    out
                }
                fn longest(v: &mut [(i32, i32)]) -> i32 {
                    v.sort_unstable();
                    if v.is_empty() {
                        return 0;
                    }
                    let (mut best, mut cur) = (0, (v[0].0, v[0].1));
                    for &(a, b) in v.iter().skip(1) {
                        if a <= cur.1 {
                            cur.1 = cur.1.max(b);
                        } else {
                            best = best.max(cur.1 - cur.0);
                            cur = (a, b);
                        }
                    }
                    best.max(cur.1 - cur.0)
                }
                // **Sin las cuatro del borde de región.** Ésas miden la región entera por
                // definición y no las puede romper nada de esta pasada: dejarlas dentro pone un
                // suelo de 4 en la cuenta y esconde el movimiento de las que sí se pueden romper.
                let edges = [
                    (bounds.0 * 100.0) as i32,
                    (bounds.2 * 100.0) as i32,
                    (bounds.1 * 100.0) as i32,
                    (bounds.3 * 100.0) as i32,
                ];
                for (m, e) in [
                    (&mut faces_x, [edges[0], edges[1]]),
                    (&mut faces_z, [edges[2], edges[3]]),
                ] {
                    for (k, v) in m.iter_mut() {
                        if e.iter().any(|c| (c - k).abs() <= 2) {
                            continue;
                        }
                        if longest(v.as_mut_slice()) >= half {
                            long_lines += 1;
                        }
                    }
                }
            }
        }
    }

    let nb = n_built as f64;
    println!(
        "[forma] {} semillas x {} regiones ({storeys} plantas, {n_built} espacios)",
        seeds.len(),
        regions.len()
    );
    println!(
        "[forma] compuestos {:.1} % | area deformada {:.1} % | rectangularidad {:.3} | \
         compacidad {:.3} | bandas compuestas {}/{} | lineas largas {:.1}/planta",
        100.0 * n_comp as f64 / nb.max(1.0),
        100.0 * a_comp / a_built.max(1.0),
        rect_w / a_built.max(1.0),
        comp_w / a_built.max(1.0),
        n_band_comp,
        n_band,
        long_lines as f64 / storeys.max(1) as f64
    );
}

/// **EL MUNDO EN ALTURAS DE JUGADOR.** Qué mide de verdad lo que se recorre.
///
/// La pregunta que contesta no es «cuántos metros cuadrados tiene una sala» —eso ya lo dice el
/// objetivo de área— sino la que se hace de pie dentro: **el ancho de un pasillo y la altura de un
/// techo, divididos por lo que mide el jugador**. Un número absoluto no dice si un sitio se siente
/// grande; una proporción sí.
///
/// `cargo test --release player_scale_reality -- --ignored --nocapture`.
#[test]
#[ignore]
fn player_scale_reality() {
    /// Lo que mide el jugador, en metros.
    const PLAYER_M: f32 = 1.86;

    let mut band_w: Vec<f32> = Vec::new();
    let mut room_min: Vec<f32> = Vec::new();
    let mut clear: Vec<f32> = Vec::new();
    let mut clear_band: Vec<f32> = Vec::new();
    let mut ratio: Vec<f32> = Vec::new();

    for (rx, rz) in AUDIT_REGIONS {
        let building = building_of(rx, rz);
        for p in &building.storeys {
            for (_, s) in p.built() {
                let h = super::fill::clear_height_cm(s) as f32 / 100.0;
                for r in s.parts() {
                    let (w, d) = (r.width_cm() as f32 / 100.0, r.depth_cm() as f32 / 100.0);
                    if s.role.is_circulation() {
                        band_w.push(w.min(d));
                        clear_band.push(h);
                    } else {
                        room_min.push(w.min(d));
                        clear.push(h);
                        // Lo ancho que es un sitio para lo alto que es. Una nave de 33 m con el
                        // techo a 3 sale a 11: eso no se lee como sala grande, se lee como
                        // aparcamiento.
                        ratio.push(w.max(d) / h);
                    }
                }
            }
        }
    }

    let stat = |mut v: Vec<f32>| -> (f32, f32, f32) {
        v.sort_by(|a, b| a.partial_cmp(b).unwrap());
        let med = v[v.len() / 2];
        (v[0], med, v[v.len() - 1])
    };
    let show = |label: &str, v: Vec<f32>, in_players: bool| {
        let (lo, med, hi) = stat(v);
        if in_players {
            println!(
                "[escala] {label:<22} min {lo:5.2} m  mediana {med:5.2} m  max {hi:6.2} m   \
                 | mediana = {:.2} jugadores",
                med / PLAYER_M
            );
        } else {
            println!("[escala] {label:<22} min {lo:5.2}  mediana {med:5.2}  max {hi:6.2}");
        }
    };

    println!(
        "[escala] jugador {PLAYER_M} m — {} regiones, semilla servida",
        AUDIT_REGIONS.len()
    );
    show("ancho de banda", band_w, true);
    show("techo de banda", clear_band, true);
    show("lado menor de sala", room_min, true);
    show("techo de sala", clear, true);
    show("ancho/alto de sala", ratio, false);
}

/// **CORTES VERTICALES DEL MUNDO SERVIDO.** Lo único que enseña un techo sin abrir el juego.
///
/// El volcado de planta contesta «dónde se puede ir»; ésta contesta «qué altura tiene esto». Corta la
/// región por planos de Z y dibuja, en X–Y, los tramos macizos que el ráster tiene en cada columna:
/// suelos, techos, forjados y macizos. Con la silueta del jugador a escala al lado, para que la
/// pregunta —¿esto se siente bajo?— se conteste mirando y no calculando.
///
/// `WG3_MAP_DIR=... cargo test --release dump_region_sections -- --ignored --nocapture`.
#[test]
#[ignore]
fn dump_region_sections() {
    /// Ancho de píxel por metro, en horizontal.
    const PX: f32 = 6.0;
    /// Y en vertical, exagerado: sin esto una planta de 3 m al lado de 150 de ancho es una raya.
    const PY: f32 = 26.0;
    /// Lo que mide el jugador.
    const PLAYER_M: f32 = 1.86;

    let dir = std::env::var("WG3_MAP_DIR").expect("WG3_MAP_DIR: carpeta donde escribir los cortes");
    let m = real_manifest();
    let regions: Vec<(i32, i32)> = AUDIT_REGIONS.to_vec();

    for (rx, rz) in regions {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let inside = super::validate::region_inside(&m, SERVED_SEED, region);
        let (min_x, min_z, _, _) = region.bounds();

        // Cinco cortes repartidos por la región, evitando los bordes.
        for (k, frac) in [0.2f32, 0.35, 0.5, 0.65, 0.8].iter().enumerate() {
            let z = min_z + REGION_M * frac;
            let h_m = 14.0f32;
            let (w, h) = (REGION_M * PX, h_m * PY + 40.0);
            let mut svg = format!(
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w:.0} {h:.0}\" \
                 width=\"{w:.0}\" height=\"{h:.0}\">\n<rect width=\"100%\" height=\"100%\" \
                 fill=\"#14161a\"/>\n"
            );
            // Y hacia ARRIBA en el mundo, hacia abajo en SVG.
            let to_y = |y_m: f32| (h_m - y_m) * PY;

            let mut cell = 0.0f32;
            while cell < REGION_M {
                let x = min_x + cell + WG3_CELL_M * 0.5;
                for (b, t) in inside.rasters.column(x, z) {
                    let (y0, y1) = (b as f32 / 100.0, t as f32 / 100.0);
                    if y1 < -1.0 || y0 > h_m {
                        continue;
                    }
                    // Una losa fina se lee como suelo o techo; lo grueso es muro o macizo.
                    let thin = y1 - y0 <= 0.30;
                    let fill = if thin { "#38bdf8" } else { "#64748b" };
                    svg += &format!(
                        "<rect x=\"{:.1}\" y=\"{:.1}\" width=\"{:.1}\" height=\"{:.1}\" \
                         fill=\"{fill}\" fill-opacity=\"0.85\"/>\n",
                        cell * PX,
                        to_y(y1),
                        WG3_CELL_M * PX,
                        (y1 - y0) * PY
                    );
                }
                cell += WG3_CELL_M;
            }

            // Las cotas de planta, para leer de un vistazo dónde cae cada forjado.
            let storey_m = super::plan::STOREY_HEIGHT_CM as f32 / 100.0;
            let mut n = 0;
            while (n as f32) * storey_m <= h_m {
                let y = to_y(n as f32 * storey_m);
                svg += &format!(
                    "<line x1=\"0\" y1=\"{y:.1}\" x2=\"{w:.0}\" y2=\"{y:.1}\" stroke=\"#f59e0b\" \
                     stroke-width=\"0.7\" stroke-dasharray=\"6 6\" opacity=\"0.5\"/>\n"
                );
                n += 1;
            }

            // El jugador, a escala, sobre el primer suelo que haya cerca del centro.
            let px = w * 0.5;
            svg += &format!(
                "<rect x=\"{:.1}\" y=\"{:.1}\" width=\"{:.1}\" height=\"{:.1}\" fill=\"#f87171\" \
                 opacity=\"0.9\"/>\n<text x=\"{:.1}\" y=\"{:.1}\" fill=\"#f87171\" \
                 font-family=\"sans-serif\" font-size=\"11\">1,86 m</text>\n",
                px,
                to_y(PLAYER_M),
                0.45 * PX,
                PLAYER_M * PY,
                px + 8.0,
                to_y(PLAYER_M) + 12.0
            );
            svg += &format!(
                "<text x=\"8\" y=\"16\" fill=\"#e2e8f0\" font-family=\"sans-serif\" \
                 font-size=\"13\">region ({rx},{rz}) — corte en z = {z:.0} m</text>\n"
            );
            svg += "</svg>\n";
            let path = format!("{dir}/wg3_section_{rx}_{rz}_{k}.svg");
            std::fs::write(&path, svg).expect("escribir el corte");
            println!("[corte] {path}");
        }
    }
}

/// SONDA — qué hay de verdad en la columna del ráster SERVIDO bajo un punto.
///
/// Existe porque el plan y el mundo servido se pueden contradecir sin que ningún contador se queje:
/// una megasala con el suelo a cota 0 en el plan y un jugador que atraviesa esa cota en partida es
/// exactamente ese caso. Imprime los tramos macizos, que es lo único que la colisión mira.
#[test]
#[ignore]
fn probe_column_under_point() {
    let m = real_manifest();
    for (rx, rz, x, z) in [
        (1, 0, 263.2f32, 52.1f32),
        (1, 0, 263.2, 68.9),
        (1, 0, 263.2, 44.0),
        (1, 0, 263.2, 40.0),
        (1, 0, 263.2, 76.0),
        (1, 0, 263.2, 80.0),
        (1, 0, 250.0, 60.0),
        (1, 0, 263.2, 60.0),
    ] {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);
        let rasters = super::validate::RegionRasters::build(&m, &served, region);
        println!("[columna] ({x:.1}, {z:.1}) → {:?}", rasters.column(x, z));
    }
}

/// ADR-130 — SONDA: dónde están las escaleras de la torre (0,0), sótanos incluidos, para ir a
/// capturarlas. `cargo test --release probe_tower_wells -- --ignored --nocapture`.
#[test]
#[ignore]
fn probe_tower_wells() {
    let b = served_building_of(0, 0);
    eprintln!("ground {} storeys {}", b.ground, b.storeys.len());
    for w in &b.wells {
        let st = &b.storeys[w.storey_below];
        let sp = st.spaces[w.space_below];
        let (cx, cz) = w.rect.centre_m();
        eprintln!(
            "well below {} -> {} rect ({:.1},{:.1}) cota {} rise {}",
            w.storey_below,
            w.storey_below + 1,
            cx,
            cz,
            sp.floor_y_cm,
            sp.rise_cm
        );
    }
}

#[test]
#[ignore]
fn probe_tower_floors() {
    use crate::world::wg3::collision::Wg3CollisionCache;
    use crate::world::Vec3;
    let m = real_manifest();
    let region = Wg3RegionCoord { x: 0, z: 0 };
    let world = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);
    let mut per: std::collections::BTreeMap<i32, usize> = Default::default();
    for sg in world.segments() {
        *per.entry(sg.floor_y_cm).or_default() += 1;
    }
    eprintln!("segmentos por cota: {per:?}");
    let mut worlds = Wg3WorldCache::default();
    let mut cache = Wg3CollisionCache::new();
    for (x, z, y) in [
        (105.9, 54.4, -9.96),
        (64.1, 79.9, -9.96),
        (128.6, 102.0, -6.64),
        (90.3, 71.8, -6.64),
        (42.0, 31.2, -3.32),
        (42.0, 26.0, 0.0),
    ] {
        let pos = Vec3::new(x, y + 1.0, z);
        cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, pos, pos);
        let fy = cache.floor_y(pos);
        let blocked = cache.blocked_at(pos, 0.35);
        eprintln!("({x},{z}) pies {y}: suelo {fy} bloqueado {blocked}");
    }
    // Las naves de B3 (índice 0) por su centro, y la columna del ráster en cada una.
    let b = served_building_of(0, 0);
    let mut shown = 0;
    for (_, sp) in b.storeys[0].built() {
        if sp.role != SpaceRole::Hall || shown >= 4 {
            continue;
        }
        shown += 1;
        let (cx, cz) = sp.rect.centre_m();
        let pos = Vec3::new(cx, -9.96 + 1.0, cz);
        cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, pos, pos);
        let fy = cache.floor_y(pos);
        let below = cache.floor_below_m(cx, cz, -9.96);
        let head = cache.headroom_m(cx, cz, -9.96);
        let strictly = cache.floor_strictly_below_m(cx, cz, -9.0);
        eprintln!("nave B3 ({cx:.1},{cz:.1}) cota {} clear {}: floor_y {fy} below {below:?} headroom {head:?} strictly_below(-9) {strictly:?}", sp.floor_y_cm, sp.max_clear_cm);
    }
}

/// ADR-130 — SONDA: ¿hay naves del sótano más bajo sin suelo en su centro? Un agujero en la
/// planta más baja da al vacío.
#[test]
#[ignore]
fn probe_lowest_basement_floorless() {
    let m = real_manifest();
    for (rx, rz) in [(0, 0), (-1, -1)] {
        let b = served_building_of(rx, rz);
        let region = Wg3RegionCoord { x: rx, z: rz };
        let world = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);
        let mut worlds = Wg3WorldCache::default();
        let mut cache = crate::world::wg3::collision::Wg3CollisionCache::new();
        let lowest = &b.storeys[0];
        let floor_m = lowest
            .spaces
            .iter()
            .find(|s| s.role.is_built())
            .map(|s| s.floor_y_cm)
            .unwrap_or(0) as f32
            / 100.0;
        let mut bad = 0;
        let mut total = 0;
        for (i, sp) in lowest.built() {
            total += 1;
            let (cx, cz) = sp.rect.centre_m();
            let pos = crate::world::Vec3::new(cx, floor_m + 1.0, cz);
            cache.prewarm_for_move(&mut worlds, &m, SERVED_SEED, pos, pos);
            // Nueve muestras en 2 m: un centro dentro de un tabique o de un macizo no es «sin suelo».
            if sp.role == SpaceRole::Stair {
                // El centro de una escalera es el tramo: macizo hasta media altura. No es un agujero.
                continue;
            }
            let any_floor = (-3..=3).any(|dx| {
                (-3..=3).any(|dz| {
                    cache
                        .floor_below_m(cx + dx as f32, cz + dz as f32, floor_m)
                        .is_some()
                })
            });
            if !any_floor {
                bad += 1;
                let above: Vec<String> = b.storeys[1]
                    .spaces
                    .iter()
                    .filter(|t| {
                        t.rect
                            .contains_point((cx * 100.0) as i32, (cz * 100.0) as i32)
                    })
                    .map(|t| format!("{:?} cota {} rise {}", t.role, t.floor_y_cm, t.rise_cm))
                    .collect();
                let wells: Vec<String> = b
                    .wells
                    .iter()
                    .filter(|w| {
                        w.rect
                            .contains_point((cx * 100.0) as i32, (cz * 100.0) as i32)
                    })
                    .map(|w| format!("pozo {}->{}", w.storey_below, w.storey_below + 1))
                    .collect();
                eprintln!(
                    "({rx},{rz}) espacio {i} {:?} en ({cx:.1},{cz:.1}) {:?} rise {} sin suelo; encima {above:?} {wells:?}",
                    sp.role, sp.rect, sp.rise_cm
                );
            }
        }
        let _ = world;
        eprintln!("({rx},{rz}) planta más baja: {bad}/{total} sin suelo en el centro");
    }
}

// ─────────── variantes de sala (2026-09-06, ADR-129 enm. 2) ───────────

/// La huella en planta de un ancla de atrezo, ya girada. Los giros que no son múltiplos de 90
/// (la silla caída, la caja) se miden por su cuadrado circunscrito: es lo que comprobó el emisor.
fn prop_rect(p: &segment::Wg3Prop) -> Option<(i32, i32, i32, i32, i32)> {
    let (w, d, h) = fill::prop_footprint_cm(p.kind)?;
    let (w, d) = match p.yaw_deg.rem_euclid(180) {
        0 => (w, d),
        90 => (d, w),
        _ => (w.max(d), w.max(d)),
    };
    Some((
        p.x_cm - w / 2,
        p.z_cm - d / 2,
        p.x_cm + w / 2,
        p.z_cm + d / 2,
        h,
    ))
}

/// **Ningún mueble pisa un macizo ni una boca.** Vale para todo el atrezo, no sólo para el de las
/// variantes: lo que se apoya en el suelo tiene huella, y esa huella no puede solapar ni un macizo
/// que se vea (los invisibles SON los suyos) ni el hueco de una puerta, que es por donde se pasa.
///
/// Lo de encima de una mesa y lo colgado no entran: no tienen huella en el suelo y no frenan.
#[test]
fn props_clear_solids_and_mouths() {
    let m = real_manifest();
    let mut checked = 0usize;
    for (rx, rz) in AUDIT_REGIONS {
        let b = served_building_of(rx, rz);
        let f = fill::fill_building(&b, &m);
        // Las bocas de todos los tramos, con su media anchura REAL (sin la holgura del emisor) y
        // medidas como lo que son: una LINEA en una cara (a lo largo, el ancho; perpendicular, nada).
        // La junta entre dos tramos hermanas de la planta abierta (enm. 21) es una boca de cara
        // entera, y un cuadrado de 15 m de lado taparia la sala.
        let mouths: Vec<(i32, i32, i32, i32, i32)> = f
            .segments
            .iter()
            .flat_map(|g| {
                let (w, d) = (g.size_x_cm as f32 / 100.0, g.size_z_cm as f32 / 100.0);
                g.openings.iter().map(move |o| {
                    let (lx, lz) = placement::local_point(o.side, o.offset_cm as f32 / 100.0, w, d);
                    let half = o.width_cm / 2;
                    let (hx, hz) = if o.side % 2 == 0 {
                        (half, 0)
                    } else {
                        (0, half)
                    };
                    (
                        g.x_cm + (lx * 100.0).round() as i32,
                        g.z_cm + (lz * 100.0).round() as i32,
                        hx,
                        hz,
                        g.floor_y_cm,
                    )
                })
            })
            .collect();
        for p in &f.props {
            let Some((x0, z0, x1, z1, h)) = prop_rect(p) else {
                continue;
            };
            // La altura que ocupa: una silla o una papelera no declaran alto, pero ocupan.
            let (y0, y1) = (p.y_cm, p.y_cm + h.max(80));
            checked += 1;
            for s in &f.solids {
                if s.is_hidden() || s.is_decoration() {
                    continue;
                }
                if s.bottom_y_cm >= y1 || s.top_y_cm <= y0 {
                    continue;
                }
                assert!(
                    s.x_cm >= x1
                        || s.x_cm + s.size_x_cm <= x0
                        || s.z_cm >= z1
                        || s.z_cm + s.size_z_cm <= z0,
                    "({rx},{rz}) atrezo {} en ({},{},{}) pisa un macizo {:?}",
                    p.kind,
                    p.x_cm,
                    p.y_cm,
                    p.z_cm,
                    (
                        s.x_cm,
                        s.z_cm,
                        s.size_x_cm,
                        s.size_z_cm,
                        s.bottom_y_cm,
                        s.top_y_cm
                    ),
                );
            }
            for &(mx, mz, hx, hz, fl) in &mouths {
                if (fl - p.y_cm).abs() >= 100 {
                    continue;
                }
                assert!(
                    mx + hx <= x0 || mx - hx >= x1 || mz + hz <= z0 || mz - hz >= z1,
                    "({rx},{rz}) atrezo {} en ({},{}) pisa la boca de ({mx},{mz}) medias {hx}/{hz}",
                    p.kind,
                    p.x_cm,
                    p.z_cm,
                );
            }
        }
    }
    assert!(
        checked > 200,
        "sólo {checked} anclas con huella: la sonda no mide"
    );
}

/// Los pesos de `variants` son probabilidades ABSOLUTAS: su suma es la proporción de despachos con
/// variante, y pasar de 1,0 sería declarar que todos la llevan — y dejar muertas a las últimas de
/// la fila, que es un fallo mudo.
#[test]
fn variant_weights_are_probabilities() {
    for kn in fill::KNOBS.iter() {
        let sum: f32 = kn.variants.iter().sum();
        assert!(
            kn.variants.iter().all(|&w| (0.0..=1.0).contains(&w)),
            "{:?}: un peso fuera de [0,1] en {:?}",
            kn.character,
            kn.variants
        );
        assert!(
            sum <= 1.0,
            "{:?}: los pesos suman {sum}, y por encima de 1,0 las últimas variantes no salen nunca",
            kn.character
        );
    }
}

/// Sonda: dónde cae cada variante. `WG3_PROBE_REGION="rx,rz"`, en coordenadas de región.
#[test]
#[ignore]
fn probe_variant_spots() {
    let spec = std::env::var("WG3_PROBE_REGION").unwrap_or_else(|_| "0,0".into());
    let mut it = spec.split(',').map(|v| v.trim().parse::<i32>().unwrap());
    let (rx, rz) = (it.next().unwrap(), it.next().unwrap());
    let m = real_manifest();
    let b = served_building_of(rx, rz);
    let f = fill::fill_building(&b, &m);
    for (kind, name) in [
        (segment::PROP_TABLE_LONG, "mesa larga"),
        (segment::PROP_COUNTER, "mostrador"),
        (segment::PROP_FRIDGE, "nevera"),
        (segment::PROP_MICROWAVE, "microondas"),
        (segment::PROP_RACK, "rack"),
    ] {
        let hits: Vec<&segment::Wg3Prop> = f.props.iter().filter(|p| p.kind == kind).collect();
        println!("[variantes] ({rx},{rz}) {name}: {} anclas", hits.len());
        for p in hits.iter().take(12) {
            println!(
                "  ({:.2}, {:.2}, {:.2}) giro {}",
                p.x_cm as f32 / 100.0,
                p.y_cm as f32 / 100.0,
                p.z_cm as f32 / 100.0,
                p.yaw_deg
            );
        }
    }
    // El archivo no tiene mueble propio: se reconoce por la FILA — cuatro archivadores o más
    // alineados y a menos de un metro y medio entre vecinos.
    let mut cabs: Vec<&segment::Wg3Prop> = f
        .props
        .iter()
        .filter(|p| p.kind == segment::PROP_CABINET)
        .collect();
    cabs.sort_by_key(|p| (p.y_cm, p.z_cm, p.x_cm));
    println!("[variantes] ({rx},{rz}) archivadores: {}", cabs.len());
    let mut row: Vec<&segment::Wg3Prop> = Vec::new();
    let flush = |row: &Vec<&segment::Wg3Prop>| {
        if row.len() >= 3 {
            let p = row[row.len() / 2];
            println!(
                "[variantes] ({rx},{rz}) archivo: fila de {} en ({:.2}, {:.2}, {:.2})",
                row.len(),
                p.x_cm as f32 / 100.0,
                p.y_cm as f32 / 100.0,
                p.z_cm as f32 / 100.0
            );
        }
    };
    for c in cabs {
        let same = row.last().is_some_and(|p: &&segment::Wg3Prop| {
            let (dx, dz) = ((p.x_cm - c.x_cm).abs(), (p.z_cm - c.z_cm).abs());
            p.y_cm == c.y_cm && ((dz < 50 && dx < 150) || (dx < 50 && dz < 150))
        });
        if !same {
            flush(&row);
            row.clear();
        }
        row.push(c);
    }
    flush(&row);
}
