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
        // ADR-098 — el techo sube de 250 a 600 ms porque ahora componer INCLUYE enrutar, y este
        // test corre en depuración. **El número que manda está medido en release: 20–63 ms por
        // región** (antes del enrutador, 0–9), y se paga una vez por región porque la composición se
        // cachea. Lo que sigue vigilando esta aserción es lo de siempre: que a nadie se le vaya la
        // mano y cruzar una frontera pase a costar un tirón.
        assert!(
            elapsed.as_millis() < 600,
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
    const MAX_STEP: f32 = 0.20;

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
                if (HEAD_M..=6.0).contains(&head) {
                    out.push(top);
                }
            }
            out
        };

        let mut floors: Vec<Vec<f32>> = vec![Vec::new(); cells * cells];
        let mut standable = 0usize;
        for iz in 0..cells {
            for ix in 0..cells {
                let levels = levels_of(ix, iz);
                if !levels.is_empty() {
                    standable += 1;
                }
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

        println!(
            "[wg3] región ({rx},{rz}): {reached} celdas a pie de {standable} pisables ({:.0} %), \
             {:.0} m² andables | piezas pisadas {pieces_reached} de {pieces_counted} | tramos \
             pisados {segments_reached} de {} | puertas alcanzadas {gates_reached} de {}",
            reached as f32 * 100.0 / standable.max(1) as f32,
            reached as f32 * CELL * CELL,
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
                if (HEAD_M..=6.0).contains(&head) {
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
    const MAX_STEP: f32 = 0.20;

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
                if (HEAD_M..=6.0).contains(&head) {
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
    const MAX_STEP: f32 = 0.20;

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
                    if (HEAD_M..=6.0).contains(&head) {
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
    const MAX_STEP: f32 = 0.20;
    const PX: f32 = 4.0;

    let dir = std::env::var("WG3_MAP_DIR").expect("WG3_MAP_DIR: carpeta donde escribir los planos");
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
                    if (HEAD_M..=6.0).contains(&head) {
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
