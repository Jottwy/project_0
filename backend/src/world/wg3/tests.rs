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
        // ADR-096 — APAGADO, y es lo que mantiene vivo este oráculo. C# no cierra bucles, así que
        // encenderlo aquí no probaría una deriva: probaría que hemos cambiado el algoritmo. La
        // paridad se vigila sobre el algoritmo base; los bucles llevan sus propios tests.
        close_loops: false,
        // Y SIN ACOTAR ni anclar, por lo mismo: el oráculo es el mundo A3 que compone C#, que no
        // conoce ni regiones ni juntas. Acotarlo aquí no mediría paridad, mediría otra cosa.
        bounds: None,
        seed_at: None,
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

use super::world::{
    composer_seed, Wg3RegionCoord, Wg3ServedWorld, Wg3WorldCache, INTERIM_BUDGET, REGION_CHUNKS,
};

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

    let origin = chunk::Wg3ChunkCoord { x: 0, z: 0 };

    let first: Vec<_> = cache
        .region_for(&m, SERVED_SEED, origin)
        .placements()
        .to_vec();
    let second: Vec<_> = cache
        .region_for(&m, SERVED_SEED + 1, origin)
        .placements()
        .to_vec();
    assert_ne!(first, second, "dos semillas dan el mismo mundo");

    let again: Vec<_> = cache
        .region_for(&m, SERVED_SEED, origin)
        .placements()
        .to_vec();
    assert_eq!(first, again);
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

    let a: Vec<_> = cache
        .region_for(&m, SERVED_SEED, inside_a)
        .placements()
        .to_vec();
    let b: Vec<_> = cache
        .region_for(&m, SERVED_SEED, inside_b)
        .placements()
        .to_vec();
    assert_eq!(a, b, "el mismo chunk-región compuso dos cosas distintas");

    let c: Vec<_> = cache
        .region_for(&m, SERVED_SEED, other)
        .placements()
        .to_vec();
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
        assert!(
            elapsed.as_millis() < 250,
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
            let raster =
                chunk::build_chunk_raster(&m, &world.placements_touching_chunk(&m, chunk), chunk);

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
    let raster = chunk::build_chunk_raster(&m, &placements, coord);

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
