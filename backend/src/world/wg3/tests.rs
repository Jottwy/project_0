//! ADR-095 F2 tanda 1 — el lado Rust, sin wire y sin cliente.
//!
//! Los tests corren contra el manifiesto REAL exportado al repositorio, no contra uno fabricado
//! aquí. Es a propósito: un manifiesto de mentira prueba que el parser funciona, y lo que hay que
//! probar es que **el fichero que hornea Unity se lee y se coloca**, que es el contrato entero.

use std::path::PathBuf;

use super::chunk;
use super::compose;
use super::manifest::{self, Wg3Manifest, Wg3Piece};
use super::placement::{self, PlacedBox, Wg3Placement};
use super::raster::{Span, Wg3Raster, Wg3RasterBuilder, CM_PER_M, WG3_CELL_M};

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

fn real_manifest() -> Wg3Manifest {
    let path = manifest_path();
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("no se pudo leer {}: {e}", path.display()));
    manifest::parse_manifest(&text).expect("el manifiesto exportado no pasa la validación")
}

fn piece_by_id<'a>(m: &'a Wg3Manifest, id: &str) -> &'a Wg3Piece {
    m.pieces
        .iter()
        .find(|p| p.id == id)
        .unwrap_or_else(|| panic!("el catálogo no tiene la pieza {id}"))
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
    // El motivo entero de que el rasterizado sea conservador. Una pared mide 0,15 m y una celda
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
    // LA MEDIDA QUE VALIDA D1. El rasterizado conservador infla cada pared hasta media celda, y eso
    // COME VANO. Si el hueco libre baja del diámetro del jugador, el tamaño de celda elegido en el
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

    let mut previous = -1.0f32;
    let mut climbed = 0;
    let mut z = min_z + 5.6;
    while z < min_z + 8.9 {
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
            "{}: {} celdas, {} tramos, {} B ({:.0} B/m²)",
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

    let m = real_manifest();

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
/// múltiplo de la celda: si empezara, el recorte del borde coincidiría con el de la rejilla por
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
        let chunk_raster = chunk::build_chunk_raster(&m, std::slice::from_ref(&p), *coord);
        let (cx0, cz0, _, _) = coord.bounds();

        for iz in 0..chunk_raster.cells_z() {
            for ix in 0..chunk_raster.cells_x() {
                // Centro de celda en coordenadas de mundo: es lo único que las dos rejillas
                // comparten, porque tienen orígenes distintos.
                let x = cx0 + (ix as f32 + 0.5) * WG3_CELL_M;
                let z = cz0 + (iz as f32 + 0.5) * WG3_CELL_M;
                if x < min_x || x > max_x || z < min_z || z > max_z {
                    continue;
                }
                assert_eq!(
                    whole.column_at(x, z),
                    chunk_raster.column(ix, iz),
                    "la celda de mundo ({x:.2}, {z:.2}) sale distinta al recortarla por el chunk \
                     {coord:?}"
                );
                compared += 1;
            }
        }
    }
    assert!(compared > 2_000, "solo {compared} celdas comparadas");
    println!("[wg3] costura: {compared} celdas idénticas a los dos lados de la frontera");
}

#[test]
fn a_chunk_that_no_piece_touches_comes_out_empty() {
    let m = real_manifest();
    let (_, p) = straddling(&m);
    let far = chunk::Wg3ChunkCoord { x: 40, z: -17 };
    let raster = chunk::build_chunk_raster(&m, std::slice::from_ref(&p), far);
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
    // El máximo es EXCLUSIVO. Reclamar un chunk en el que no se pone ni una celda haría
    // re-rasterizar de más en cada colocación, y peor: un chunk vacío que se cree ocupado.
    let m = real_manifest();
    let piece = piece_by_id(&m, "cor_straight");
    let p = Wg3Placement {
        piece: piece.index,
        rotation: 0,
        origin_x_cm: (50.0 * CM_PER_M) as i32 - (piece.size_x * CM_PER_M) as i32,
        origin_z_cm: 0,
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
            });
            x += hall.size_x;
        }
        z += hall.size_z;
    }

    let coord = chunk::Wg3ChunkCoord { x: 0, z: 0 };
    let start = std::time::Instant::now();
    let raster = chunk::build_chunk_raster(&m, &placements, coord);
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
        "[wg3] chunk lleno: {} colocaciones, {} celdas, {} tramos, {kb:.0} KB, \
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
    let m = real_manifest();

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
                (
                    want.piece,
                    want.rotation,
                    want.origin_x_cm,
                    want.origin_z_cm,
                    want.depth
                ),
                (
                    got.placement.piece,
                    got.placement.rotation,
                    got.placement.origin_x_cm,
                    got.placement.origin_z_cm,
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
/// celda.
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

use super::world::{Wg3ServedWorld, Wg3WorldCache};

/// Semilla de las pruebas del mundo servido. Es la del oráculo con los 32 bits altos puestos: así
/// el test también ejercita `composer_seed`, que se queda con los bajos.
const SERVED_SEED: u64 = 0xDEAD_BEEF_0000_002A;

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

    let first: Vec<_> = cache.world_for(&m, SERVED_SEED).placements().to_vec();
    let second: Vec<_> = cache.world_for(&m, SERVED_SEED + 1).placements().to_vec();
    assert_ne!(first, second, "dos semillas dan el mismo mundo");

    let again: Vec<_> = cache.world_for(&m, SERVED_SEED).placements().to_vec();
    assert_eq!(first, again);
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
        assert!(
            span_x >= 90.0 && span_z >= 90.0,
            "semilla {seed}: el mundo mide {span_x:.0} × {span_z:.0} m, menos de dos chunks de lado"
        );
        assert!(
            elapsed.as_millis() < 250,
            "semilla {seed}: componer costó {} ms y lo hace el primer chunk que se pide",
            elapsed.as_millis()
        );
    }
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
