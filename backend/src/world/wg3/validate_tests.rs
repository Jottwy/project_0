//! Auditoría 2026-09-02 — los tests del validador por niveles y el BARRIDO multi-semilla.
//!
//! Lo que hay en `tests.rs` mide una semilla y cuatro regiones; esto mide muchas. El barrido corto
//! corre en la suite; el largo se pide con `WG3_SWEEP_SEEDS=N` (y conviene `--release`).

use super::tests::{real_manifest, SERVED_SEED};
use super::validate::{self, ValidateOptions};
use super::world::Wg3RegionCoord;

/// Las nueve regiones alrededor del origen: donde aparece todo el mundo y donde se cruzan cuatro
/// juntas en el punto (0,0).
const NEAR_REGIONS: [(i32, i32); 9] = [
    (-1, -1),
    (0, -1),
    (1, -1),
    (-1, 0),
    (0, 0),
    (1, 0),
    (-1, 1),
    (0, 1),
    (1, 1),
];

/// La semilla con la que Joel juega (`Wg3LiveBootstrap`). Que la suite la mire es lo mínimo.
const LIVE_SEED: u64 = 42;

fn sweep_seed_count(default: usize) -> usize {
    std::env::var("WG3_SWEEP_SEEDS")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(default)
}

/// Las cuatro regiones de referencia con la semilla de siempre: es el mismo mundo que miden los
/// demás tests, así que si esto falla y aquéllos no, el validador está pidiendo algo nuevo.
#[test]
fn the_audited_regions_pass_every_level() {
    let m = real_manifest();
    let sweep = validate::validate_sweep(
        &m,
        &[SERVED_SEED],
        &[(0, 0), (1, 0), (0, 1), (-1, 2)],
        &ValidateOptions::default(),
    );
    sweep.print();
    assert_eq!(
        sweep.valid(),
        sweep.total(),
        "regiones de referencia con problemas: {:?}",
        sweep.histogram()
    );
}

/// La semilla de la partida real, en las nueve regiones donde se aparece.
#[test]
fn the_live_seed_passes_every_level_near_the_origin() {
    let m = real_manifest();
    let sweep =
        validate::validate_sweep(&m, &[LIVE_SEED], &NEAR_REGIONS, &ValidateOptions::default());
    sweep.print();
    assert_eq!(
        sweep.valid(),
        sweep.total(),
        "regiones de la semilla en vivo con problemas: {:?}",
        sweep.histogram()
    );
}

/// **EL BARRIDO.** N semillas × 9 regiones, todos los niveles. Tres semillas en la suite (que ya
/// son 27 regiones más de lo que había); las cientos, con `WG3_SWEEP_SEEDS=40 cargo test
/// --release ... many_seeds -- --nocapture`.
#[test]
fn many_seeds_produce_valid_regions() {
    let m = real_manifest();
    let seeds = validate::sweep_seeds(sweep_seed_count(3));
    let sweep = validate::validate_sweep(&m, &seeds, &NEAR_REGIONS, &ValidateOptions::default());
    sweep.print();
    assert_eq!(
        sweep.valid(),
        sweep.total(),
        "{} de {} regiones con problemas: {:?}",
        sweep.total() - sweep.valid(),
        sweep.total(),
        sweep.histogram()
    );
}

/// El plan y el relleno solos —sin ráster— son baratos: aquí caben muchas más semillas dentro de
/// la suite, y es donde se cazan los fallos de reparto que no dependen de la colisión.
#[test]
fn many_seeds_plan_and_fill_cleanly() {
    let m = real_manifest();
    let seeds = validate::sweep_seeds(sweep_seed_count(12));
    let opts = ValidateOptions {
        walk: false,
        nav: false,
        determinism: true,
        ..ValidateOptions::default()
    };
    let regions: Vec<(i32, i32)> = (-2..=2)
        .flat_map(|z| (-2..=2).map(move |x| (x, z)))
        .collect();
    let sweep = validate::validate_sweep(&m, &seeds, &regions, &opts);
    let bad: Vec<String> = sweep
        .reports
        .iter()
        .filter(|r| !r.is_valid())
        .map(|r| format!("{} :: {}", r.summary(), r.problems().join(" | ")))
        .collect();
    for b in &bad {
        println!("[wg3-validate] MAL {b}");
    }
    println!(
        "[wg3-validate] plan+fill: {} de {} regiones limpias",
        sweep.valid(),
        sweep.total()
    );
    for (k, n) in sweep.histogram() {
        println!("[wg3-validate]   {n:4} × {k}");
    }
    assert!(
        bad.is_empty(),
        "{} de {} regiones con problemas de plan/relleno/geometría: {:?}",
        bad.len(),
        sweep.total(),
        sweep.histogram()
    );
}

/// El informe de una región es reproducible: dos validaciones dan las mismas cifras.
#[test]
fn a_report_is_deterministic() {
    let m = real_manifest();
    let region = Wg3RegionCoord { x: 0, z: 0 };
    let opts = ValidateOptions::default();
    let a = validate::validate_region(&m, LIVE_SEED, region, &opts);
    let b = validate::validate_region(&m, LIVE_SEED, region, &opts);
    assert_eq!(a.plan, b.plan);
    assert_eq!(a.fill, b.fill);
    assert_eq!(a.walk, b.walk);
    assert_eq!(a.problems(), b.problems());
}

/// SONDA — **por qué una región no pasa**: cada pozo de escalera cota a cota, cada espacio sin
/// suelo con sus tramos y enlaces, y cada puerta de junta con lo que hay detrás.
///
/// `WG3_PROBE_SEED=42 WG3_PROBE_REGION=1,-1 cargo test --bin backrooms_server probe_region_inside
/// -- --ignored --nocapture`
#[test]
#[ignore = "sonda: imprime, no exige"]
fn probe_region_inside() {
    use super::plan::{PlanRect, STOREY_HEIGHT_CM};

    let m = real_manifest();
    let seed: u64 = std::env::var("WG3_PROBE_SEED")
        .ok()
        .and_then(|v| match v.strip_prefix("0x") {
            Some(hex) => u64::from_str_radix(hex, 16).ok(),
            None => v.parse().ok(),
        })
        .unwrap_or(LIVE_SEED);
    let (rx, rz) = std::env::var("WG3_PROBE_REGION")
        .ok()
        .and_then(|v| {
            let mut it = v.split(',').map(|t| t.trim().parse::<i32>());
            Some((it.next()?.ok()?, it.next()?.ok()?))
        })
        .unwrap_or((0, 0));
    let region = Wg3RegionCoord { x: rx, z: rz };

    let report = validate::validate_region(&m, seed, region, &ValidateOptions::default());
    println!("[probe] {}", report.summary());
    for p in report.problems() {
        println!("[probe]   {p}");
    }
    let inside = validate::region_inside(&m, seed, region);
    let grid = &inside.grid;
    println!(
        "[probe] mancha mayor = {} ({} cotas)",
        grid.main,
        grid.sizes[grid.main.max(0) as usize]
    );

    // ── pozos ──────────────────────────────────────────────────────────────────────────────
    for (i, w) in inside.building.wells.iter().enumerate() {
        let below = &inside.building.storeys[w.storey_below].spaces[w.space_below];
        let entry = below.rise_from_side % 4;
        let across_x = !entry.is_multiple_of(2);
        let (cx, cz) = w.rect.centre_m();
        let (a0, a1) = if across_x {
            (w.rect.min_x_cm, w.rect.max_x_cm)
        } else {
            (w.rect.min_z_cm, w.rect.max_z_cm)
        };
        let mut line = String::new();
        let mut t = a0 + 25;
        while t < a1 {
            let (x, z) = if across_x {
                (t as f32 / 100.0, cz)
            } else {
                (cx, t as f32 / 100.0)
            };
            match grid.cell(x, z) {
                Some(c) => {
                    let cotas: Vec<String> = grid.floors[c]
                        .iter()
                        .zip(&grid.blob_of[c])
                        .filter(|(f, _)| {
                            **f >= below.floor_y_cm as f32 / 100.0 - 0.4
                                && **f <= (below.floor_y_cm + STOREY_HEIGHT_CM) as f32 / 100.0 + 0.4
                        })
                        .map(|(f, b)| format!("{:.2}{}", f, if *b == grid.main { "" } else { "*" }))
                        .collect();
                    line += &format!(" [{}]", cotas.join(","));
                }
                None => line += " [fuera]",
            }
            t += 50;
        }
        // Y quién rodea al pozo: los espacios de la planta de abajo que lo tocan y los de la planta
        // de arriba que lo pisan, con su papel y su rect. Es lo que dice de dónde sale una pared.
        let touching = |storey: usize, grow: i32| -> Vec<String> {
            let g = super::plan::PlanRect {
                min_x_cm: w.rect.min_x_cm - grow,
                min_z_cm: w.rect.min_z_cm - grow,
                max_x_cm: w.rect.max_x_cm + grow,
                max_z_cm: w.rect.max_z_cm + grow,
            };
            inside.building.storeys[storey]
                .spaces
                .iter()
                .enumerate()
                .filter(|(_, t)| t.rect.overlaps(&g))
                .map(|(k, t)| {
                    format!(
                        "{k}:{}({},{})-({},{})r{}",
                        t.role.name(),
                        t.rect.min_x_cm,
                        t.rect.min_z_cm,
                        t.rect.max_x_cm,
                        t.rect.max_z_cm,
                        t.rise_cm
                    )
                })
                .collect()
        };
        println!(
            "[well {i}]   abajo tocan: {} | arriba pisan: {}",
            touching(w.storey_below, 20).join(" "),
            touching(w.storey_below + 1, 0).join(" ")
        );
        println!(
            "[well {i}] planta {}→{} espacio {} rect ({},{})-({},{}) entra por lado {entry} | {line}",
            w.storey_below,
            w.storey_below + 1,
            w.space_below,
            w.rect.min_x_cm,
            w.rect.min_z_cm,
            w.rect.max_x_cm,
            w.rect.max_z_cm
        );
    }

    // ── espacios sin suelo o sin alcance ───────────────────────────────────────────────────
    for (n, storey) in inside.building.storeys.iter().enumerate() {
        let wells: Vec<PlanRect> = inside
            .building
            .wells
            .iter()
            .filter(|w| w.storey_below + 1 == n)
            .map(|w| w.rect)
            .collect();
        for (i, s) in storey.built() {
            let r = s.rect.shrunk(60);
            if r.width_cm() <= 0 || r.depth_cm() <= 0 {
                continue;
            }
            let lo_y = (s.floor_y_cm.min(s.floor_y_cm + s.rise_cm) - 30) as f32 / 100.0;
            let hi_y = (s.floor_y_cm.max(s.floor_y_cm + s.rise_cm) + 30) as f32 / 100.0;
            let (mut total, mut with_floor, mut in_main) = (0usize, 0usize, 0usize);
            let mut x = r.min_x_cm + 25;
            while x < r.max_x_cm {
                let mut z = r.min_z_cm + 25;
                while z < r.max_z_cm {
                    if !wells.iter().any(|w| w.contains_point(x, z)) {
                        if let Some(c) = grid.cell(x as f32 / 100.0, z as f32 / 100.0) {
                            total += 1;
                            if let Some((li, _)) = grid.floors[c]
                                .iter()
                                .enumerate()
                                .find(|(_, y)| **y >= lo_y && **y <= hi_y)
                            {
                                with_floor += 1;
                                if grid.blob_of[c][li] == grid.main {
                                    in_main += 1;
                                }
                            }
                        }
                    }
                    z += 50;
                }
                x += 50;
            }
            if total == 0 {
                continue;
            }
            let cov = with_floor as f32 / total as f32;
            let reach = in_main as f32 / total as f32;
            if cov < 0.5 || reach < 0.5 {
                let segs = inside
                    .filled
                    .segments
                    .iter()
                    .filter(|g| {
                        g.x_cm >= s.rect.min_x_cm
                            && g.z_cm >= s.rect.min_z_cm
                            && g.x_cm + g.size_x_cm <= s.rect.max_x_cm
                            && g.z_cm + g.size_z_cm <= s.rect.max_z_cm
                            && (g.floor_y_cm - s.floor_y_cm).abs() <= s.rise_cm.abs() + 1
                    })
                    .count();
                let placed: Vec<String> = inside
                    .filled
                    .placements
                    .iter()
                    .filter(|p| {
                        s.rect.contains_point(p.origin_x_cm + 1, p.origin_z_cm + 1)
                            && p.origin_y_cm == s.floor_y_cm
                    })
                    .map(|p| {
                        let piece = m.piece(p.piece).expect("pieza del catálogo");
                        format!(
                            "{}[{}×{} h{} r{} @({},{})]",
                            piece.id,
                            piece.size_x,
                            piece.size_z,
                            piece.height_meters,
                            p.rotation,
                            p.origin_x_cm,
                            p.origin_z_cm
                        )
                    })
                    .collect();
                let pieces = placed.len();
                // Y lo que hay DEBAJO de este espacio en la planta de abajo con más altura de la
                // que cabe: una pieza alta asoma su techo dentro de esta sala.
                let below_pieces: Vec<String> = if n > 0 {
                    inside
                        .filled
                        .placements
                        .iter()
                        .filter(|p| {
                            let piece = m.piece(p.piece).expect("pieza");
                            let (w, d) = if p.rotation % 2 == 0 {
                                (piece.size_x, piece.size_z)
                            } else {
                                (piece.size_z, piece.size_x)
                            };
                            let pr = PlanRect {
                                min_x_cm: p.origin_x_cm,
                                min_z_cm: p.origin_z_cm,
                                max_x_cm: p.origin_x_cm + (w * 100.0) as i32,
                                max_z_cm: p.origin_z_cm + (d * 100.0) as i32,
                            };
                            p.origin_y_cm < s.floor_y_cm && pr.overlaps(&s.rect)
                        })
                        .map(|p| {
                            let piece = m.piece(p.piece).expect("pieza");
                            format!("{} h{} y{}", piece.id, piece.height_meters, p.origin_y_cm)
                        })
                        .collect()
                } else {
                    Vec::new()
                };
                // Y cada puerta: la mancha a 75 cm a cada lado del punto del paso.
                let door_sides: Vec<String> = storey
                    .links
                    .iter()
                    .filter(|l| l.a == i || l.b == i)
                    .map(|l| {
                        let side = super::plan::door_fits_on(&s.rect, l.at_x_cm, l.at_z_cm);
                        let (x, z) = (l.at_x_cm as f32 / 100.0, l.at_z_cm as f32 / 100.0);
                        let y = s.floor_y_cm as f32 / 100.0;
                        let probes = [(0.75f32, 0.0f32), (-0.75, 0.0), (0.0, 0.75), (0.0, -0.75)];
                        let around: Vec<String> = probes
                            .iter()
                            .map(|(dx, dz)| {
                                let c = grid.cell(x + dx, z + dz);
                                match c {
                                    Some(c) => format!(
                                        "{:?}",
                                        grid.floors[c]
                                            .iter()
                                            .zip(&grid.blob_of[c])
                                            .filter(|(f, _)| (**f - y).abs() < 0.5)
                                            .map(|(f, b)| format!("{f:.2}/{b}"))
                                            .collect::<Vec<_>>()
                                    ),
                                    None => "fuera".into(),
                                }
                            })
                            .collect();
                        // Y las columnas del ráster a lo largo de la NORMAL de la pared, a 75, 25,
                        // −25 y −75 cm del paso: es donde se ve qué tramo macizo cierra la puerta.
                        let along_x = (s.rect.max_z_cm - l.at_z_cm).abs() <= 2
                            || (s.rect.min_z_cm - l.at_z_cm).abs() <= 2;
                        let cols: Vec<String> = [-0.75f32, -0.25, 0.25, 0.75]
                            .iter()
                            .map(|d| {
                                let (px, pz) = if along_x { (x, z + d) } else { (x + d, z) };
                                format!("{d:+.2}:{:?}", inside.rasters.column(px, pz))
                            })
                            .collect();
                        format!(
                            "@({},{}) en pared {} → {} | columnas {}",
                            l.at_x_cm,
                            l.at_z_cm,
                            side,
                            around.join(" "),
                            cols.join(" ")
                        )
                    })
                    .collect();
                let links: Vec<String> = storey
                    .links
                    .iter()
                    .filter(|l| l.a == i || l.b == i)
                    .map(|l| {
                        let o = if l.a == i { l.b } else { l.a };
                        format!(
                            "{o}({},{:?}@{},{})",
                            storey.spaces[o].role.name(),
                            l.kind,
                            l.at_x_cm,
                            l.at_z_cm
                        )
                    })
                    .collect();
                let gates = storey.gates.iter().filter(|g| g.space == i).count();
                println!(
                    "[hollow] planta {n} espacio {i} {} rect ({},{})-({},{}) cota {} rise {} \
                     void_above {} max_clear {} | suelo {:.0} % alcanzable {:.0} % | {segs} tramos \
                     {pieces} piezas {gates} puertas | enlaces {}",
                    s.role.name(),
                    s.rect.min_x_cm,
                    s.rect.min_z_cm,
                    s.rect.max_x_cm,
                    s.rect.max_z_cm,
                    s.floor_y_cm,
                    s.rise_cm,
                    s.void_above,
                    s.max_clear_cm,
                    cov * 100.0,
                    reach * 100.0,
                    links.join(" ")
                );
                if !placed.is_empty() {
                    println!("[hollow]   piezas: {}", placed.join(" "));
                }
                if !below_pieces.is_empty() {
                    println!("[hollow]   debajo: {}", below_pieces.join(" "));
                }
                for d in &door_sides {
                    println!("[hollow]   puerta {d}");
                }
                // El mapa: `.` sin suelo a esa cota, `#` suelo fuera de la mancha mayor, `o` suelo
                // en la mancha mayor, `W` pozo. Una fila por 50 cm, de norte (z alto) a sur.
                let mut z = s.rect.max_z_cm - 25;
                while z > s.rect.min_z_cm {
                    let mut row = String::new();
                    let mut x = s.rect.min_x_cm + 25;
                    while x < s.rect.max_x_cm {
                        let ch = if wells.iter().any(|w| w.contains_point(x, z)) {
                            'W'
                        } else if let Some(c) = grid.cell(x as f32 / 100.0, z as f32 / 100.0) {
                            match grid.floors[c]
                                .iter()
                                .enumerate()
                                .find(|(_, y)| **y >= lo_y && **y <= hi_y)
                            {
                                Some((li, _)) if grid.blob_of[c][li] == grid.main => 'o',
                                Some(_) => '#',
                                None => '.',
                            }
                        } else {
                            '?'
                        };
                        row.push(ch);
                        x += 50;
                    }
                    println!("[hollow]   {row}");
                    z -= 50;
                }
            }
        }
    }

    // ── columnas sueltas: `WG3_PROBE_COLUMNS="x,z;x,z"` en metros ─────────────────────────
    if let Ok(list) = std::env::var("WG3_PROBE_COLUMNS") {
        for item in list.split(';') {
            let mut it = item.split(',').map(|t| t.trim().parse::<f32>());
            if let (Some(Ok(x)), Some(Ok(z))) = (it.next(), it.next()) {
                let c = grid.cell(x, z);
                let levels: Vec<String> = c
                    .map(|c| {
                        grid.floors[c]
                            .iter()
                            .zip(&grid.blob_of[c])
                            .map(|(f, b)| format!("{f:.2}/{b}"))
                            .collect()
                    })
                    .unwrap_or_default();
                println!(
                    "[column] ({x:.2},{z:.2}) → {:?} | pisable {:?}",
                    inside.rasters.column(x, z),
                    levels
                );
            }
        }
    }

    // ── puertas de junta ───────────────────────────────────────────────────────────────────
    for g in &inside.gates {
        let (nx, nz) = super::placement::outward_normal(g.outward_side);
        let x = g.x - nx * 0.75;
        let z = g.z - nz * 0.75;
        let blob = grid.blob_at(x, z, 0.0);
        let ground = &inside.building.storeys[inside.building.ground];
        let pg = ground.gates.iter().find(|pg| {
            (pg.x_cm as f32 / 100.0 - g.x).abs() < 0.05
                && (pg.z_cm as f32 / 100.0 - g.z).abs() < 0.05
        });
        let owner = pg.map(|pg| {
            let s = &ground.spaces[pg.space];
            format!(
                "espacio {} ({}) rect ({},{})-({},{}) rise {} enlaces {}",
                pg.space,
                s.role.name(),
                s.rect.min_x_cm,
                s.rect.min_z_cm,
                s.rect.max_x_cm,
                s.rect.max_z_cm,
                s.rise_cm,
                ground
                    .links
                    .iter()
                    .filter(|l| l.a == pg.space || l.b == pg.space)
                    .count()
            )
        });
        println!(
            "[gate] ({:.1},{:.1}) lado {} → mancha {:?} {} | {}",
            g.x,
            g.z,
            g.outward_side,
            blob,
            if blob == Some(grid.main) {
                "OK"
            } else {
                "NO ALCANZABLE"
            },
            owner.unwrap_or_else(|| "sin espacio en el plan".into())
        );
    }
}

/// ADR-120 — clasifica la huella de un espacio y devuelve `(clase, perímetro en metros)`.
///
/// La clase sale de la MÁSCARA sobre la rejilla de las caras de las partes, que es exactamente la
/// rejilla con la que el relleno emite (ADR-120 D4): así lo que se cuenta aquí es lo que se construye
/// allí. Contar las casillas MUERTAS de esa rejilla —las que quedan dentro de la envolvente y fuera
/// de la huella— dice la forma sin necesidad de reconocer ninguna:
///
/// - ninguna → un rectángulo;
/// - una en esquina → una L;
/// - una en el lado → una T o una U;
/// - dos en el mismo lado y a distinta profundidad → una Z o una escalonada.
fn shape_of(sp: &super::plan::PlannedSpace) -> (usize, f32) {
    let parts = sp.parts();
    let mut xs: Vec<i32> = parts
        .iter()
        .flat_map(|p| [p.min_x_cm, p.max_x_cm])
        .collect();
    let mut zs: Vec<i32> = parts
        .iter()
        .flat_map(|p| [p.min_z_cm, p.max_z_cm])
        .collect();
    xs.sort_unstable();
    xs.dedup();
    zs.sort_unstable();
    zs.dedup();
    let (nx, nz) = (xs.len() - 1, zs.len() - 1);
    let mut alive = vec![false; nx * nz];
    for iz in 0..nz {
        for ix in 0..nx {
            let (cx, cz) = ((xs[ix] + xs[ix + 1]) / 2, (zs[iz] + zs[iz + 1]) / 2);
            alive[iz * nx + ix] = parts.iter().any(|p| p.contains_point(cx, cz));
        }
    }

    // Perímetro: cada cara de celda viva que no da a otra celda viva.
    let mut perim_cm = 0i64;
    for iz in 0..nz {
        for ix in 0..nx {
            if !alive[iz * nx + ix] {
                continue;
            }
            let (w, d) = ((xs[ix + 1] - xs[ix]) as i64, (zs[iz + 1] - zs[iz]) as i64);
            if ix + 1 == nx || !alive[iz * nx + ix + 1] {
                perim_cm += d;
            }
            if ix == 0 || !alive[iz * nx + ix - 1] {
                perim_cm += d;
            }
            if iz + 1 == nz || !alive[(iz + 1) * nx + ix] {
                perim_cm += w;
            }
            if iz == 0 || !alive[(iz - 1) * nx + ix] {
                perim_cm += w;
            }
        }
    }
    let perim_m = perim_cm as f32 / 100.0;

    let dead: Vec<(usize, usize)> = (0..nz)
        .flat_map(|iz| (0..nx).map(move |ix| (ix, iz)))
        .filter(|&(ix, iz)| !alive[iz * nx + ix])
        .collect();
    let corner = |ix: usize, iz: usize| (ix == 0 || ix + 1 == nx) && (iz == 0 || iz + 1 == nz);
    let klass = match dead.len() {
        0 => 0,
        1 => {
            if corner(dead[0].0, dead[0].1) {
                1
            } else {
                2
            }
        }
        2 => {
            let same_row = dead[0].1 == dead[1].1;
            let same_col = dead[0].0 == dead[1].0;
            if same_row || same_col {
                3
            } else {
                4
            }
        }
        _ => 4,
    };
    (klass, perim_m)
}

/// AUDITORÍA FASE 2 (2026-09-02) — la LÍNEA BASE arquitectónica, antes de tocar nada.
///
/// Sólo mide. No cambia el generador, y por eso vive en el módulo de tests: lo que se quiere saber
/// es qué reparto de espacios produce hoy WG3, para poder decir después si la Fase 2 lo movió.
///
/// `WG3_METRICS_SEEDS=N` (por defecto 8), `WG3_METRICS_REGIONS=N` (por defecto 9).
#[test]
#[ignore = "sonda de medida; se pide a mano"]
fn probe_architecture_metrics() {
    use super::plan::SpaceRole;

    let m = real_manifest();
    let seeds = validate::sweep_seeds(
        std::env::var("WG3_METRICS_SEEDS")
            .ok()
            .and_then(|v| v.parse().ok())
            .unwrap_or(8),
    );
    let region_n: usize = std::env::var("WG3_METRICS_REGIONS")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(9);
    let regions: Vec<(i32, i32)> = NEAR_REGIONS.iter().copied().take(region_n).collect();

    // Cubetas de área en m²: trastero, despacho, oficina, sala, nave, nave grande.
    const AREA_EDGES: [f32; 5] = [50.0, 120.0, 300.0, 700.0, 1500.0];
    let mut area_hist = [0usize; 6];
    let mut role_hist = [0usize; 10];
    let mut entry_hist = [0usize; 6]; // 0,1,2,3,4,5+
    let mut big_entry_hist = [0usize; 6]; // sólo espacios ≥ 300 m²
    let mut height_hist = std::collections::BTreeMap::<i32, usize>::new();
    let mut sight_hist = [0usize; 8]; // 0-5,5-10,10-15,15-20,20-30,30-45,45-70,70+ m

    let mut spaces_total = 0usize;
    let mut area_sum = 0.0f64;
    let mut area_max = 0.0f32;
    let mut aspect_sum = 0.0f64;
    let mut solids_total = 0usize;
    let mut pillars_total = 0usize;
    let mut segments_total = 0usize;
    let mut sight_max = 0.0f32;
    let mut sight_sum = 0.0f64;
    let mut sight_n = 0usize;
    let mut regions_n = 0usize;
    let mut halls_big = 0usize;
    let mut halls_with_pillars = 0usize;
    let mut pillars_in_rooms = 0usize;
    let mut per_room_hist = [0usize; 10];
    let mut spacing_sum = 0.0f64;
    let mut spacing_n = 0usize;
    let mut spacing_min = i32::MAX;
    // ADR-105 enm. 3 — **cuántos espacios contienen algo.** Es la métrica que la auditoría del
    // 2026-09-03 echó en falta y la que resume la queja: antes de las divisiones, el 92,5 % de los
    // espacios del mundo era una cáscara de seis caras con aire dentro.
    let mut built_spaces = 0usize;
    let mut spaces_with_mass = 0usize;
    let mut parts_total = 0usize;

    // ADR-120 — LA RECTANGULARIDAD, medida. Es la métrica que da sentido a esta tanda, y ninguna de
    // las que ya había la veía: un mundo de cajas y un mundo de eles dan las mismas áreas, los mismos
    // papeles y las mismas entradas.
    let mut part_hist = [0usize; super::plan::MAX_PARTS + 1];
    // I (rectángulo), L, T/U, Z/escalonada, otra.
    let mut shape_hist = [0usize; 5];
    let mut compacity_sum = 0f64;
    let mut bulge_area = 0f64;
    let mut footprint_area = 0f64;
    // Saltos de escala entre vecinos: un enlace cuyos dos lados difieren en área por 3× o más.
    let (mut jump_links, mut total_links) = (0usize, 0usize);
    let mut part_cm = 0i64;

    for &seed in &seeds {
        for &(rx, rz) in &regions {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let inside = validate::region_inside(&m, seed, region);
            regions_n += 1;
            solids_total += inside.filled.solids.len();
            pillars_total += inside
                .filled
                .solids
                .iter()
                .filter(|s| super::fill::is_pillar(s))
                .count();
            segments_total += inside.filled.segments.len();

            for s in &inside.filled.segments {
                *height_hist.entry(s.height_cm).or_default() += 1;
            }

            // PILARES (ADR-119 enm. 1): cuántas naves los llevan, cuántos por nave, y a qué
            // separación real quedan. La separación es la del vecino MÁS PRÓXIMO y no el paso de la
            // retícula: con desorden y omisiones, el paso sorteado ya no es lo que se anda.
            let pillars: Vec<&super::segment::Wg3Solid> = inside
                .filled
                .solids
                .iter()
                .filter(|s| super::fill::is_pillar(s))
                .collect();
            for st in inside.building.storeys.iter() {
                for (_, sp) in st.built() {
                    if sp.role != SpaceRole::Hall || sp.rect.area_m2() < 300.0 {
                        continue;
                    }
                    halls_big += 1;
                    let inside_n = pillars
                        .iter()
                        .filter(|p| {
                            p.bottom_y_cm == sp.floor_y_cm
                                && p.x_cm >= sp.rect.min_x_cm
                                && p.x_cm < sp.rect.max_x_cm
                                && p.z_cm >= sp.rect.min_z_cm
                                && p.z_cm < sp.rect.max_z_cm
                        })
                        .count();
                    if inside_n > 0 {
                        halls_with_pillars += 1;
                        per_room_hist[inside_n.min(9)] += 1;
                        pillars_in_rooms += inside_n;
                    }
                }
            }
            // Masa interior: divisiones (30 cm de canto) y cuántos espacios llevan algo dentro.
            for s in &inside.filled.solids {
                if s.size_x_cm == 30 || s.size_z_cm == 30 {
                    parts_total += 1;
                    part_cm += s.size_x_cm.max(s.size_z_cm) as i64;
                }
            }
            for st in inside.building.storeys.iter() {
                for (_, sp) in st.built() {
                    built_spaces += 1;
                    let has = inside.filled.solids.iter().any(|s| {
                        s.bottom_y_cm == sp.floor_y_cm
                            && s.x_cm >= sp.rect.min_x_cm
                            && s.x_cm < sp.rect.max_x_cm
                            && s.z_cm >= sp.rect.min_z_cm
                            && s.z_cm < sp.rect.max_z_cm
                    });
                    if has {
                        spaces_with_mass += 1;
                    }
                }
            }

            for (a, p) in pillars.iter().enumerate() {
                let mut best = i32::MAX;
                for (b, q) in pillars.iter().enumerate() {
                    if a == b || p.bottom_y_cm != q.bottom_y_cm {
                        continue;
                    }
                    let d = (p.x_cm - q.x_cm).abs().max((p.z_cm - q.z_cm).abs());
                    if d < best {
                        best = d;
                    }
                }
                if best < i32::MAX {
                    spacing_sum += best as f64;
                    spacing_n += 1;
                    if best < spacing_min {
                        spacing_min = best;
                    }
                }
            }

            for (n, plan) in inside.building.storeys.iter().enumerate() {
                for l in &plan.links {
                    let (pa, pb) = (
                        plan.spaces[l.a].area_m2().max(1.0),
                        plan.spaces[l.b].area_m2().max(1.0),
                    );
                    total_links += 1;
                    if pa.max(pb) / pa.min(pb) >= 3.0 {
                        jump_links += 1;
                    }
                }
                for (i, sp) in plan.built() {
                    spaces_total += 1;
                    part_hist[sp.parts().len().min(super::plan::MAX_PARTS)] += 1;
                    let (klass, perim) = shape_of(sp);
                    shape_hist[klass] += 1;
                    let fp = sp.area_m2() as f64;
                    footprint_area += fp;
                    // Compacidad: perímetro² / (16·área). Vale 1 para un cuadrado y sube con cada
                    // quiebro, así que es el escalar único de «esto ya no es una caja».
                    compacity_sum += (perim as f64 * perim as f64) / (16.0 * fp.max(1.0));
                    // Área en bultos: todo lo que no es la parte mayor. Dice si la deformación es
                    // grande de verdad o una mordida decorativa.
                    if sp.is_composite() {
                        let biggest = sp
                            .parts()
                            .iter()
                            .map(|p| p.area_m2() as f64)
                            .fold(0.0, f64::max);
                        bulge_area += fp - biggest;
                    }
                    let a = sp.rect.area_m2();
                    area_sum += a as f64;
                    if a > area_max {
                        area_max = a;
                    }
                    let (w, d) = (sp.rect.width_cm() as f32, sp.rect.depth_cm() as f32);
                    aspect_sum += (w.max(d) / w.min(d).max(1.0)) as f64;

                    let mut k = AREA_EDGES.len();
                    for (j, e) in AREA_EDGES.iter().enumerate() {
                        if a < *e {
                            k = j;
                            break;
                        }
                    }
                    area_hist[k] += 1;

                    role_hist[match sp.role {
                        SpaceRole::Spine => 0,
                        SpaceRole::Corridor => 1,
                        SpaceRole::Stair => 2,
                        SpaceRole::Hall => 4,
                        SpaceRole::Office => 5,
                        SpaceRole::Service => 6,
                        SpaceRole::Storage => 7,
                        SpaceRole::DeadEnd => 8,
                        SpaceRole::Void => 9,
                    }] += 1;

                    let mut e = plan.links.iter().filter(|l| l.a == i || l.b == i).count();
                    if n == 0 {
                        e += plan.gates.iter().filter(|g| g.space == i).count();
                    }
                    entry_hist[e.min(5)] += 1;
                    if a >= 300.0 {
                        big_entry_hist[e.min(5)] += 1;
                    }
                }
            }

            // LÍNEAS DE VISIÓN en la planta baja: el rayo libre más largo a la altura de los ojos
            // desde cada celda pisable, en los dos ejes. Es lo que decide si una nave se lee como
            // espacio o como cuarto grande.
            let (min_x, min_z, _, _) = region.bounds();
            let eye = 160i16;
            // Libre a la altura de los ojos Y CON SUELO DEBAJO. Sin lo segundo el rayo se va por
            // el terreno sin construir y la metrica mide descampado, no arquitectura: la primera
            // version daba 62 m de media y un 26 % por encima de 70 m, que es el tamano de la region.
            let free = |x: f32, z: f32| -> bool {
                inside.grid.blob_at(x, z, 0.0).is_some()
                    && !inside
                        .rasters
                        .column(x, z)
                        .iter()
                        .any(|&(lo, hi)| lo <= eye && eye < hi)
            };
            let step = 2.0f32;
            let mut z = min_z + 1.0;
            while z < min_z + 149.0 {
                let mut x = min_x + 1.0;
                while x < min_x + 149.0 {
                    if free(x, z) {
                        for (dx, dz) in [(step, 0.0f32), (0.0f32, step)] {
                            let mut run = 0.0f32;
                            let (mut px, mut pz) = (x + dx, z + dz);
                            while run < 200.0 && free(px, pz) {
                                run += step;
                                px += dx;
                                pz += dz;
                            }
                            sight_sum += run as f64;
                            sight_n += 1;
                            if run > sight_max {
                                sight_max = run;
                            }
                            let b = match run {
                                r if r < 5.0 => 0,
                                r if r < 10.0 => 1,
                                r if r < 15.0 => 2,
                                r if r < 20.0 => 3,
                                r if r < 30.0 => 4,
                                r if r < 45.0 => 5,
                                r if r < 70.0 => 6,
                                _ => 7,
                            };
                            sight_hist[b] += 1;
                        }
                    }
                    x += step;
                }
                z += step;
            }
        }
    }

    let pc = |n: usize, total: usize| -> f32 {
        if total == 0 {
            0.0
        } else {
            n as f32 * 100.0 / total as f32
        }
    };
    println!("=== LÍNEA BASE ARQUITECTÓNICA WG3 ===");
    println!(
        "semillas {} × regiones {} = {} regiones, {} espacios construidos",
        seeds.len(),
        regions.len(),
        regions_n,
        spaces_total
    );
    println!(
        "área: media {:.0} m², máx {:.0} m², proporción media {:.2}",
        area_sum / spaces_total.max(1) as f64,
        area_max,
        aspect_sum / spaces_total.max(1) as f64
    );
    let names = ["<50", "50-120", "120-300", "300-700", "700-1500", ">1500"];
    for (i, n) in area_hist.iter().enumerate() {
        println!(
            "  área {:>9} m²: {:>6}  {:>5.1} %",
            names[i],
            n,
            pc(*n, spaces_total)
        );
    }
    let rnames = [
        "spine", "corridor", "stair", "junction", "hall", "office", "service", "storage",
        "dead_end", "void",
    ];
    for (i, n) in role_hist.iter().enumerate() {
        println!(
            "  papel {:>9}: {:>6}  {:>5.1} %",
            rnames[i],
            n,
            pc(*n, spaces_total)
        );
    }
    for (i, n) in entry_hist.iter().enumerate() {
        println!(
            "  entradas {}{}: {:>6}  {:>5.1} %",
            i,
            if i == 5 { "+" } else { " " },
            n,
            pc(*n, spaces_total)
        );
    }
    let big: usize = big_entry_hist.iter().sum();
    for (i, n) in big_entry_hist.iter().enumerate() {
        println!(
            "  entradas ≥300 m² {}{}: {:>6}  {:>5.1} %",
            i,
            if i == 5 { "+" } else { " " },
            n,
            pc(*n, big)
        );
    }
    println!(
        "  espacios ≥300 m²: {} ({:.1} %)",
        big,
        pc(big, spaces_total)
    );
    // ---- ADR-120: la rectangularidad ----
    println!(
        "  HUELLA: rectangulares {} de {} ({:.1} %), compuestas {:.1} %",
        part_hist[1],
        spaces_total,
        pc(part_hist[1], spaces_total),
        pc(spaces_total - part_hist[1], spaces_total)
    );
    for (k, n) in part_hist.iter().enumerate().skip(1) {
        println!("    {k} parte(s): {n:6}  {:5.1} %", pc(*n, spaces_total));
    }
    const SHAPES: [&str; 5] = ["I (caja)", "L", "T/U", "Z/escalonada", "otra"];
    for (k, n) in shape_hist.iter().enumerate() {
        println!(
            "    forma {:<14} {:6}  {:5.1} %",
            SHAPES[k],
            n,
            pc(*n, spaces_total)
        );
    }
    println!(
        "  compacidad media (perímetro²/16·área, 1,0 = cuadrado): {:.3}",
        compacity_sum / spaces_total.max(1) as f64
    );
    println!(
        "  área en bultos: {:.1} % del área construida",
        100.0 * bulge_area / footprint_area.max(1.0)
    );
    println!(
        "  saltos de escala (enlaces con razón de áreas ≥ 3): {} de {} ({:.1} %)",
        jump_links,
        total_links,
        pc(jump_links, total_links)
    );
    println!(
        "  tramos {}, macizos {}, de ellos pilares {}",
        segments_total, solids_total, pillars_total
    );
    println!(
        "  pilares por región: {:.2}",
        pillars_total as f32 / regions_n.max(1) as f32
    );
    println!(
        "  divisiones {} ({:.1} por región, {:.0} m lineales por región)",
        parts_total,
        parts_total as f32 / regions_n.max(1) as f32,
        part_cm as f32 / 100.0 / regions_n.max(1) as f32
    );
    println!(
        "  espacios construidos CON masa interior: {} de {} ({:.1} %)",
        spaces_with_mass,
        built_spaces,
        pc(spaces_with_mass, built_spaces)
    );
    println!(
        "  naves ≥300 m²: {}, con pilares {} ({:.1} %), {:.1} pilares por nave con pilares",
        halls_big,
        halls_with_pillars,
        pc(halls_with_pillars, halls_big),
        pillars_in_rooms as f32 / halls_with_pillars.max(1) as f32
    );
    for (i, n) in per_room_hist.iter().enumerate() {
        if *n > 0 {
            println!(
                "    {}{} pilares: {:>5} naves  {:>5.1} %",
                i,
                if i == 9 { "+" } else { " " },
                n,
                pc(*n, halls_with_pillars)
            );
        }
    }
    println!(
        "  separación al pilar más próximo: media {:.0} cm, mínima {} cm",
        spacing_sum / spacing_n.max(1) as f64,
        if spacing_min == i32::MAX {
            0
        } else {
            spacing_min
        }
    );
    println!("alturas libres de tramo (cm → cuántos):");
    for (h, n) in &height_hist {
        println!("  {:>5} cm: {:>6}  {:>5.1} %", h, n, pc(*n, segments_total));
    }
    let snames = [
        "0-5", "5-10", "10-15", "15-20", "20-30", "30-45", "45-70", ">70",
    ];
    println!(
        "líneas de visión (planta baja, ojos a 1,60 m): media {:.1} m, máx {:.0} m, {} rayos",
        sight_sum / sight_n.max(1) as f64,
        sight_max,
        sight_n
    );
    for (i, n) in sight_hist.iter().enumerate() {
        println!("  {:>6} m: {:>7}  {:>5.1} %", snames[i], n, pc(*n, sight_n));
    }
}

/// AUDITORÍA FASE 2 — DE DÓNDE SALEN LAS ISLAS.
///
/// La fracción de mancha mayor es la puerta que más cuesta pasar del validador, y hasta ahora sólo
/// decía CUÁNTAS islas hay. Esto dice DÓNDE están: cuántas cotas, a qué altura, y en qué espacio del
/// plan cae su centro. Sin eso no se puede distinguir «una sala sellada» de «la cavidad bajo un tiro
/// de escalera», que son el mismo número y problemas distintos.
///
/// `WG3_PROBE_SEED` y `WG3_PROBE_REGION` como en `probe_region_inside`.
#[test]
#[ignore = "sonda de medida; se pide a mano"]
fn probe_islands() {
    let m = real_manifest();
    let seed = std::env::var("WG3_PROBE_SEED")
        .ok()
        .and_then(|v| {
            let t = v.trim().to_string();
            if let Some(h) = t.strip_prefix("0x") {
                u64::from_str_radix(h, 16).ok()
            } else {
                t.parse().ok()
            }
        })
        .unwrap_or(SERVED_SEED);
    let (rx, rz) = std::env::var("WG3_PROBE_REGION")
        .ok()
        .and_then(|v| {
            let mut it = v.split(',');
            Some((
                it.next()?.trim().parse().ok()?,
                it.next()?.trim().parse().ok()?,
            ))
        })
        .unwrap_or((0, 0));
    let region = Wg3RegionCoord { x: rx, z: rz };
    let inside = validate::region_inside(&m, seed, region);
    let g = &inside.grid;

    // Censo: por mancha, cuántas cotas, el rango de alturas y el centro de masas.
    let n = g.sizes.len();
    let mut ymin = vec![f32::MAX; n];
    let mut ymax = vec![f32::MIN; n];
    let mut sx = vec![0.0f64; n];
    let mut sz = vec![0.0f64; n];
    for c in 0..g.floors.len() {
        let (ix, iz) = (c % g.cells, c / g.cells);
        let x = g.min_x + (ix as f32 + 0.5) * 0.5;
        let z = g.min_z + (iz as f32 + 0.5) * 0.5;
        for (li, y) in g.floors[c].iter().enumerate() {
            let b = g.blob_of[c][li];
            if b < 0 {
                continue;
            }
            let b = b as usize;
            ymin[b] = ymin[b].min(*y);
            ymax[b] = ymax[b].max(*y);
            sx[b] += x as f64;
            sz[b] += z as f64;
        }
    }

    let mut order: Vec<usize> = (0..n).filter(|&b| g.sizes[b] >= 40).collect();
    order.sort_by_key(|&b| std::cmp::Reverse(g.sizes[b]));

    let total: usize = g.sizes.iter().sum();
    println!(
        "[islas] seed {:#x} región ({},{}): {} cotas, {} manchas, mayor {} ({:.1} %)",
        seed,
        rx,
        rz,
        total,
        n,
        g.sizes[g.main.max(0) as usize],
        g.sizes[g.main.max(0) as usize] as f32 * 100.0 / total.max(1) as f32
    );
    let mut island_cells = 0usize;
    let mut under_stair = 0usize;
    for &b in order.iter().take(30) {
        if b as i32 == g.main {
            continue;
        }
        island_cells += g.sizes[b];
        let (cx, cz) = (
            (sx[b] / g.sizes[b] as f64) as f32,
            (sz[b] / g.sizes[b] as f64) as f32,
        );
        // ¿En qué espacio del plan cae el centro, y en qué planta?
        let mut owner = String::from("—");
        for (n_st, plan) in inside.building.storeys.iter().enumerate() {
            for (i, s) in plan.spaces.iter().enumerate() {
                let (x0, z0, x1, z1) = s.rect.bounds_m();
                if cx >= x0 && cx < x1 && cz >= z0 && cz < z1 {
                    let tag = format!(
                        "p{} {}:{} rise {} cota {}",
                        n_st,
                        i,
                        s.role.name(),
                        s.rise_cm,
                        s.floor_y_cm
                    );
                    if owner == "—" {
                        owner = tag;
                    } else {
                        owner.push_str(" | ");
                        owner.push_str(&tag);
                    }
                }
            }
        }
        // Bajo un tiro: el centro cae en un espacio `stair` que SUBE, y la isla está por debajo de
        // su cota de llegada.
        let is_under_stair =
            owner.contains("stair") && owner.contains("rise ") && !owner.contains("rise 0");
        if is_under_stair {
            under_stair += g.sizes[b];
        }
        println!(
            "[islas]   {:>6} cotas  y {:.2}..{:.2}  centro ({:.0},{:.0})  {}{}",
            g.sizes[b],
            ymin[b],
            ymax[b],
            cx,
            cz,
            owner,
            if is_under_stair {
                "  <-- BAJO TIRO"
            } else {
                ""
            }
        );
    }
    println!(
        "[islas] islas ≥40 celdas: {} cotas, de ellas {} bajo un tiro de escalera ({:.0} %)",
        island_cells,
        under_stair,
        under_stair as f32 * 100.0 / island_cells.max(1) as f32
    );
}

/// ADR-105 enmienda 3 — **las cuatro invariantes de la masa interior, contra 30 semillas.**
///
/// Es el gemelo de [`pillars_land_where_the_grammar_says`] y existe por el mismo motivo: ninguna de
/// estas cuatro cosas produce un test rojo en otro sitio si se rompe. Un tabique delante de un vano
/// tapia la sala sin que ningún contador se entere, y el síntoma sale cien metros más allá como una
/// puerta que no lleva a ninguna parte.
///
/// El grosor es lo que identifica una división: el pretil mide 20 cm, el pilar 200 y la división 30.
#[test]
fn partitions_land_where_the_grammar_says() {
    use super::plan::{PlanRect, SpaceRole};

    let m = real_manifest();
    let seeds = validate::sweep_seeds(sweep_seed_count(3));
    // `PARTITION_DOOR_CLEAR_CM`.
    const DOOR_CLEAR_CM: i32 = 350;
    const T_CM: i32 = 30;
    const MAX_RUN_CM: i32 = 2000;
    const SCREEN_H_CM: i32 = 230;
    // Enm. 8: medio muro bajo y división colgada del techo.
    const LOW_H_CM: i32 = super::fill::PARTITION_LOW_H_CM;
    const HANG_CLEAR_CM: i32 = super::fill::PARTITION_HANG_CLEAR_CM;

    let mut seen = 0usize;
    let mut screens = 0usize;
    let mut lows = 0usize;
    let mut hung = 0usize;
    for &seed in &seeds {
        for &(rx, rz) in NEAR_REGIONS.iter() {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let inside = validate::region_inside(&m, seed, region);
            let parts: Vec<&super::segment::Wg3Solid> = inside
                .filled
                .solids
                .iter()
                // **Por el eje FINO, no por cualquiera de los dos.** Un pretil mide 20 cm de grueso
                // y su tramo puede salir de 30 de largo: con el filtro por «alguno de los dos vale
                // 30» ese pretil entraba aquí como si fuera un tabique y se estrellaba contra la
                // comprobación de altura —110 cm, que es la del pretil— en 1 de 30 semillas. El eje
                // fino de una división vale 30 por construcción; el de un pretil, 20.
                .filter(|s| s.size_x_cm.min(s.size_z_cm) == T_CM)
                .collect();
            seen += parts.len();

            for p in &parts {
                let rect = PlanRect {
                    min_x_cm: p.x_cm,
                    min_z_cm: p.z_cm,
                    max_x_cm: p.x_cm + p.size_x_cm,
                    max_z_cm: p.z_cm + p.size_z_cm,
                };

                // 1 — toda división cae DENTRO de un espacio construido, plano y que no es
                //     circulación, y a la cota de su planta. Un tabique en la espina parte el
                //     edificio en dos.
                let mut host = None;
                for (n, st) in inside.building.storeys.iter().enumerate() {
                    for (i, sp) in st.built() {
                        // Por la HUELLA (ADR-120 D1): desde que dos espacios se entrelazan sus
                        // envolventes se pisan, así que preguntando por `rect` el macizo se le
                        // atribuye al vecino y el test mide la sala equivocada. Las huellas son
                        // disjuntas, así que dueño hay exactamente uno.
                        // A la cota de su suelo, o colgada dos metros por encima (enm. 8).
                        let at_floor = sp.floor_y_cm == p.bottom_y_cm
                            || sp.floor_y_cm + HANG_CLEAR_CM == p.bottom_y_cm;
                        if at_floor && sp.covers_rect(&rect) {
                            host = Some((n, i, sp));
                        }
                    }
                }
                let Some((n, i, sp)) = host else {
                    panic!(
                        "semilla {seed:#x} región ({rx},{rz}): división en ({},{}) cota {} fuera \
                         de todo espacio construido",
                        p.x_cm, p.z_cm, p.bottom_y_cm
                    )
                };
                assert!(
                    !sp.role.is_circulation(),
                    "semilla {seed:#x} región ({rx},{rz}): división en un espacio {}, que es \
                     circulación",
                    sp.role.name()
                );
                assert_ne!(
                    sp.role,
                    SpaceRole::Void,
                    "semilla {seed:#x} región ({rx},{rz}): división en un vacío"
                );
                assert_eq!(
                    sp.rise_cm, 0,
                    "semilla {seed:#x} región ({rx},{rz}): división en un espacio con desnivel"
                );

                // 2 — el grosor es de UN eje. Una división cuadrada de 30 × 30 sería un poste, y un
                //     poste no divide nada.
                assert!(
                    (p.size_x_cm == T_CM) != (p.size_z_cm == T_CM),
                    "semilla {seed:#x} región ({rx},{rz}): división de {} × {} cm",
                    p.size_x_cm,
                    p.size_z_cm
                );
                let long = p.size_x_cm.max(p.size_z_cm);
                assert!(
                    long <= MAX_RUN_CM,
                    "semilla {seed:#x} región ({rx},{rz}): tirada de {long} cm sobre un tope de \
                     {MAX_RUN_CM} — un macizo se dibuja en el chunk de su centro"
                );

                // 3 — su altura es la del techo o la de la mampara, nunca otra cosa. Una división a
                //     media altura que no sea ninguna de las dos es un dato roto que se lee como un
                //     bordillo.
                let h = p.top_y_cm - p.bottom_y_cm;
                let clear = super::fill::clear_height_cm(sp);
                let hanging = p.bottom_y_cm != sp.floor_y_cm;
                if hanging {
                    // Colgada: de los dos metros al techo, ni un centímetro menos por abajo.
                    hung += 1;
                    assert_eq!(
                        p.top_y_cm,
                        sp.floor_y_cm + clear,
                        "semilla {seed:#x} región ({rx},{rz}): división colgada que no llega al \
                         techo"
                    );
                } else if h == SCREEN_H_CM.min(clear) {
                    screens += 1;
                } else if h == LOW_H_CM {
                    lows += 1;
                } else {
                    assert_eq!(
                        h, clear,
                        "semilla {seed:#x} región ({rx},{rz}): división de {h} cm de alto en un \
                         espacio de {clear} cm"
                    );
                }

                // 4 — ninguna división delante de una puerta.
                let near = rect.shrunk(-DOOR_CLEAR_CM);
                let plan = &inside.building.storeys[n];
                for (dx, dz) in plan
                    .links
                    .iter()
                    .filter(|l| l.a == i || l.b == i)
                    .map(|l| (l.at_x_cm, l.at_z_cm))
                    .chain(
                        plan.gates
                            .iter()
                            .filter(|g| g.space == i)
                            .map(|g| (g.x_cm, g.z_cm)),
                    )
                {
                    assert!(
                        !near.contains_point(dx, dz),
                        "semilla {seed:#x} región ({rx},{rz}): división en ({},{})-({},{}) a menos \
                         de {DOOR_CLEAR_CM} cm de la puerta ({dx},{dz})",
                        rect.min_x_cm,
                        rect.min_z_cm,
                        rect.max_x_cm,
                        rect.max_z_cm
                    );
                }

                // 5 — ni encima del aterrizaje de una escalera o de la boca de un pozo.
                for w in &inside.building.wells {
                    if w.storey_below + 1 == n || w.storey_below == n {
                        assert!(
                            !w.rect.shrunk(-50).overlaps(&rect),
                            "semilla {seed:#x} región ({rx},{rz}): división sobre el pozo en \
                             ({},{})",
                            w.rect.min_x_cm,
                            w.rect.min_z_cm
                        );
                    }
                }
            }
        }
    }
    assert!(
        seen > 1000,
        "sólo {seen} divisiones en {} regiones: la gramática no está emitiendo",
        seeds.len() * NEAR_REGIONS.len()
    );
    println!(
        "[divisiones] {seen} revisadas, {screens} mamparas por debajo del techo, {lows} medios \
         muros bajos, {hung} colgadas"
    );
}

/// Sonda: la MEZCLA por carácter (ADR-105 enm. 14) y la REPETICIÓN entre vecinos, sobre el barrido
/// corto. Repetición = proporción de enlaces del plan cuyos dos espacios tienen la misma firma de
/// relleno (el multiconjunto de formas de macizo que contienen). Es el número que Joel pidió bajar
/// «un 50 %»; sin él no hay antes y después.
#[test]
#[ignore]
fn probe_character_mix() {
    use std::collections::BTreeMap;
    let m = real_manifest();
    let seeds = validate::sweep_seeds(sweep_seed_count(3));
    let mut by_char: BTreeMap<&'static str, (usize, usize, usize)> = BTreeMap::new();
    let (mut links, mut same) = (0usize, 0usize);
    for &seed in &seeds {
        for &(rx, rz) in NEAR_REGIONS.iter() {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let inside = validate::region_inside(&m, seed, region);
            let b = &inside.building;
            for (n, st) in b.storeys.iter().enumerate() {
                let _ = n;
                let mut sigs: Vec<Option<Vec<(i32, i32)>>> = vec![None; st.spaces.len()];
                for (i, sp) in st.built() {
                    let k = super::fill::knobs_of(b.seed, sp);
                    let name: &'static str = match k.character {
                        super::fill::Character::Open => "abierto",
                        super::fill::Character::Office => "oficina",
                        super::fill::Character::Hall => "nave",
                        super::fill::Character::Maze => "laberinto",
                        super::fill::Character::Weird => "raro",
                    };
                    let r = sp.rect;
                    let mut sig: Vec<(i32, i32)> = inside
                        .filled
                        .solids
                        .iter()
                        .filter(|s| {
                            s.bottom_y_cm >= sp.floor_y_cm
                                && s.bottom_y_cm < sp.floor_y_cm + 332
                                && s.x_cm >= r.min_x_cm
                                && s.x_cm < r.max_x_cm
                                && s.z_cm >= r.min_z_cm
                                && s.z_cm < r.max_z_cm
                        })
                        .map(|s| (s.size_x_cm.min(s.size_z_cm), s.top_y_cm - s.bottom_y_cm))
                        .collect();
                    sig.sort_unstable();
                    let e = by_char.entry(name).or_insert((0, 0, 0));
                    e.0 += 1;
                    e.1 += sig.len();
                    if sig.is_empty() {
                        e.2 += 1;
                    }
                    // La firma compara FORMAS presentes, no cantidades: dos naves con 6 y 9 pilares
                    // son la misma nave.
                    sig.dedup();
                    sigs[i] = Some(sig);
                }
                for l in &st.links {
                    if let (Some(a), Some(b2)) = (&sigs[l.a], &sigs[l.b]) {
                        links += 1;
                        if a == b2 {
                            same += 1;
                        }
                    }
                }
            }
        }
    }
    // ADR-124 — los pasillos: cuántos por región y cuánto miden de largo (lado mayor).
    let (mut corridors, mut long_sum, mut junction_links) = (0usize, 0f32, 0usize);
    for &seed in &seeds {
        for &(rx, rz) in NEAR_REGIONS.iter() {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let inside = validate::region_inside(&m, seed, region);
            for st in inside.building.storeys.iter() {
                for (_, sp) in st.built() {
                    if sp.role.is_circulation() {
                        corridors += 1;
                        long_sum += sp.rect.width_cm().max(sp.rect.depth_cm()) as f32 / 100.0;
                    }
                }
                junction_links += st
                    .links
                    .iter()
                    .filter(|l| l.kind == super::plan::LinkKind::Junction)
                    .count();
            }
        }
    }
    let regions = (seeds.len() * NEAR_REGIONS.len()) as f32;
    println!(
        "[pasillos] {:.1} por región, {:.1} m de largo medio, {:.1} cruces por región",
        corridors as f32 / regions,
        long_sum / corridors.max(1) as f32,
        junction_links as f32 / regions
    );
    for (name, (n, solids, empty)) in &by_char {
        println!(
            "[caracter] {name:10} {n:5} espacios | {:.1} macizos/espacio | {:.0} % sin nada",
            *solids as f32 / *n as f32,
            *empty as f32 * 100.0 / *n as f32
        );
    }
    println!(
        "[repeticion] {same} de {links} enlaces unen dos espacios con la misma firma: {:.1} %",
        same as f32 * 100.0 / links.max(1) as f32
    );
}

/// Sonda: el resumen de CADA región del barrido corto, para diferenciar dos versiones del relleno
/// región a región (las medias esconden en qué sala apareció una isla).
#[test]
#[ignore]
fn probe_sweep_per_region() {
    let m = real_manifest();
    let seeds = validate::sweep_seeds(sweep_seed_count(3));
    let sweep = validate::validate_sweep(&m, &seeds, &NEAR_REGIONS, &ValidateOptions::default());
    for r in &sweep.reports {
        println!("[region] {}", r.summary());
    }
}

/// ADR-105 enmienda 13 — **toda tarima apoya en el suelo de su sala y es un escalón, no un muro.**
#[test]
fn platforms_sit_on_their_floor() {
    use super::plan::PlanRect;

    let m = real_manifest();
    let seeds = validate::sweep_seeds(sweep_seed_count(3));
    let mut seen = 0usize;
    for &seed in &seeds {
        for &(rx, rz) in NEAR_REGIONS.iter() {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let inside = validate::region_inside(&m, seed, region);
            for p in inside
                .filled
                .solids
                .iter()
                .filter(|s| super::fill::is_platform(s))
            {
                seen += 1;
                let rect = PlanRect {
                    min_x_cm: p.x_cm,
                    min_z_cm: p.z_cm,
                    max_x_cm: p.x_cm + p.size_x_cm,
                    max_z_cm: p.z_cm + p.size_z_cm,
                };
                let host = inside.building.storeys.iter().find_map(|st| {
                    st.built()
                        .find(|(_, sp)| sp.floor_y_cm == p.bottom_y_cm && sp.covers_rect(&rect))
                        .map(|(_, sp)| sp)
                });
                assert!(
                    host.is_some(),
                    "semilla {seed:#x} región ({rx},{rz}): tarima en ({},{}) cota {} fuera de \
                     todo espacio o despegada de su suelo",
                    p.x_cm,
                    p.z_cm,
                    p.bottom_y_cm
                );
                assert!(
                    !host.unwrap().role.is_circulation(),
                    "semilla {seed:#x} región ({rx},{rz}): tarima en circulación"
                );
            }
        }
    }
    assert!(
        seen > 30,
        "sólo {seen} tarimas en {} regiones: la gramática no está emitiendo",
        seeds.len() * NEAR_REGIONS.len()
    );
    println!("[tarimas] {seen} tarimas revisadas");
}

/// ADR-105 enmienda 11 — **toda hilada de arcada o bóveda cuelga a 2,50 o más del suelo de su sala.**
#[test]
fn hung_bands_stay_above_the_head() {
    use super::plan::PlanRect;

    let m = real_manifest();
    let seeds = validate::sweep_seeds(sweep_seed_count(3));
    // `ARCADE_CLEAR_CM`; la bóveda deja 260.
    const MIN_UNDER_CM: i32 = 250;

    let mut seen = 0usize;
    for &seed in &seeds {
        for &(rx, rz) in NEAR_REGIONS.iter() {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let inside = validate::region_inside(&m, seed, region);
            for b in inside
                .filled
                .solids
                .iter()
                .filter(|s| super::fill::is_hung_band(s))
            {
                seen += 1;
                let rect = PlanRect {
                    min_x_cm: b.x_cm,
                    min_z_cm: b.z_cm,
                    max_x_cm: b.x_cm + b.size_x_cm,
                    max_z_cm: b.z_cm + b.size_z_cm,
                };
                let host = inside.building.storeys.iter().find_map(|st| {
                    st.built()
                        .find(|(_, sp)| sp.covers_rect(&rect) && sp.floor_y_cm < b.bottom_y_cm)
                        .map(|(_, sp)| sp)
                });
                let Some(sp) = host else {
                    panic!(
                        "semilla {seed:#x} región ({rx},{rz}): hilada en ({},{}) sin sala debajo",
                        b.x_cm, b.z_cm
                    );
                };
                assert!(
                    b.bottom_y_cm - sp.floor_y_cm >= MIN_UNDER_CM,
                    "semilla {seed:#x} región ({rx},{rz}): hilada a {} cm del suelo",
                    b.bottom_y_cm - sp.floor_y_cm
                );
            }
        }
    }
    assert!(
        seen > 50,
        "sólo {seen} hiladas colgadas en {} regiones: la gramática no está emitiendo",
        seeds.len() * NEAR_REGIONS.len()
    );
    println!("[hiladas] {seen} hiladas de arcada y bóveda revisadas");
}

/// ADR-105 enmienda 10 — **toda pilastra abraza una pared y ninguna tapa una boca.**
#[test]
fn pilasters_hug_their_wall() {
    use super::plan::PlanRect;

    let m = real_manifest();
    let seeds = validate::sweep_seeds(sweep_seed_count(3));
    // `PILASTER_DOOR_CLEAR_CM`.
    const DOOR_CLEAR_CM: i32 = 150;

    let mut seen = 0usize;
    for &seed in &seeds {
        for &(rx, rz) in NEAR_REGIONS.iter() {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let inside = validate::region_inside(&m, seed, region);
            let doors: Vec<(i32, i32, i32)> = inside
                .filled
                .segments
                .iter()
                .flat_map(|seg| {
                    let (x0, z0) = (seg.x_cm, seg.z_cm);
                    let (x1, z1) = (x0 + seg.size_x_cm, z0 + seg.size_z_cm);
                    seg.openings.iter().filter_map(move |o| {
                        let side_len = if o.side.is_multiple_of(2) {
                            seg.size_x_cm
                        } else {
                            seg.size_z_cm
                        };
                        if o.width_cm >= side_len - 1 {
                            return None;
                        }
                        Some(match o.side % 4 {
                            0 => (x0 + o.offset_cm, z1, seg.floor_y_cm),
                            1 => (x1, z1 - o.offset_cm, seg.floor_y_cm),
                            2 => (x1 - o.offset_cm, z0, seg.floor_y_cm),
                            _ => (x0, z0 + o.offset_cm, seg.floor_y_cm),
                        })
                    })
                })
                .collect();
            for p in inside
                .filled
                .solids
                .iter()
                .filter(|s| super::fill::is_pilaster(s))
            {
                seen += 1;
                let rect = PlanRect {
                    min_x_cm: p.x_cm,
                    min_z_cm: p.z_cm,
                    max_x_cm: p.x_cm + p.size_x_cm,
                    max_z_cm: p.z_cm + p.size_z_cm,
                };
                // 1 — dentro de un espacio construido a su cota, y a un grosor de pared de una de
                //     sus paredes: su cara trasera toca la cara interior del muro.
                let host = inside.building.storeys.iter().find_map(|st| {
                    st.built()
                        .find(|(_, sp)| sp.floor_y_cm == p.bottom_y_cm && sp.covers_rect(&rect))
                        .map(|(_, sp)| sp)
                });
                let Some(sp) = host else {
                    panic!(
                        "semilla {seed:#x} región ({rx},{rz}): pilastra en ({},{}) fuera de todo \
                         espacio",
                        p.x_cm, p.z_cm
                    );
                };
                let r = sp.rect;
                const T: i32 = 15;
                let hugs = rect.min_x_cm == r.min_x_cm + T
                    || rect.max_x_cm == r.max_x_cm - T
                    || rect.min_z_cm == r.min_z_cm + T
                    || rect.max_z_cm == r.max_z_cm - T;
                assert!(
                    hugs,
                    "semilla {seed:#x} región ({rx},{rz}): pilastra en ({},{}) que no toca pared",
                    p.x_cm, p.z_cm
                );
                // 2 — ninguna boca a menos del margen.
                let near = rect.shrunk(-DOOR_CLEAR_CM);
                for &(dx, dz, floor) in &doors {
                    if floor != p.bottom_y_cm {
                        continue;
                    }
                    assert!(
                        !near.contains_point(dx, dz),
                        "semilla {seed:#x} región ({rx},{rz}): pilastra en ({},{}) a menos de \
                         {DOOR_CLEAR_CM} cm de la boca ({dx},{dz})",
                        p.x_cm,
                        p.z_cm
                    );
                }
            }
        }
    }
    assert!(
        seen > 100,
        "sólo {seen} pilastras en {} regiones: la gramática no está emitiendo",
        seeds.len() * NEAR_REGIONS.len()
    );
    println!("[pilastras] {seen} pilastras revisadas");
}

/// ADR-125 — **la media luna toca su pared por la cara plana**, sobre varias semillas.
///
/// La huella del cable es la caja SIN girar y el giro dice contra qué pared va; si el giro y la
/// pared no casan, la panza queda dentro del muro y la cara plana mira a la sala — que se ve como
/// una pilastra cuadrada, o sea que no se ve.
#[test]
fn half_moon_pilasters_hug_their_wall_by_the_flat_face() {
    let m = real_manifest();
    let seeds = validate::sweep_seeds(sweep_seed_count(3));
    let mut seen = 0usize;
    for &seed in &seeds {
        for &(rx, rz) in NEAR_REGIONS.iter() {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let inside = validate::region_inside(&m, seed, region);
            for p in inside
                .filled
                .solids
                .iter()
                .filter(|s| super::fill::is_round_pilaster(s))
            {
                seen += 1;
                let (cx, cz) = (p.x_cm + p.size_x_cm / 2, p.z_cm + p.size_z_cm / 2);
                let (w, d) = (p.size_x_cm, p.size_z_cm);
                // Envolvente en el mundo según el giro, y en qué cara está el plano.
                let (env, flat) = match p.yaw_deg {
                    0 => ((cx - w / 2, cz - d / 2, cx + w / 2, cz + d / 2), 'z'),
                    180 => ((cx - w / 2, cz - d / 2, cx + w / 2, cz + d / 2), 'Z'),
                    90 => ((cx - d / 2, cz - w / 2, cx + d / 2, cz + w / 2), 'x'),
                    270 => ((cx - d / 2, cz - w / 2, cx + d / 2, cz + w / 2), 'X'),
                    other => {
                        panic!("semilla {seed:#x} región ({rx},{rz}): media luna con giro {other}")
                    }
                };
                let rect = super::plan::PlanRect {
                    min_x_cm: env.0,
                    min_z_cm: env.1,
                    max_x_cm: env.2,
                    max_z_cm: env.3,
                };
                let host = inside.building.storeys.iter().find_map(|st| {
                    st.built()
                        .find(|(_, sp)| sp.floor_y_cm == p.bottom_y_cm && sp.covers_rect(&rect))
                        .map(|(_, sp)| sp)
                });
                let Some(sp) = host else {
                    panic!(
                        "semilla {seed:#x} región ({rx},{rz}): media luna en ({cx},{cz}) fuera de \
                         todo espacio"
                    );
                };
                let r = sp.rect;
                const T: i32 = 15;
                let hugs = match flat {
                    'z' => rect.min_z_cm == r.min_z_cm + T,
                    'Z' => rect.max_z_cm == r.max_z_cm - T,
                    'x' => rect.min_x_cm == r.min_x_cm + T,
                    _ => rect.max_x_cm == r.max_x_cm - T,
                };
                assert!(
                    hugs,
                    "semilla {seed:#x} región ({rx},{rz}): media luna en ({cx},{cz}) giro {} con \
                     la cara plana lejos de la pared",
                    p.yaw_deg
                );
            }
        }
    }
    assert!(
        seen > 20,
        "sólo {seen} medias lunas en {} regiones: la gramática no está emitiendo",
        seeds.len() * NEAR_REGIONS.len()
    );
    println!("[medias lunas] {seen} revisadas");
}

/// ADR-105 enmienda 5 — **las invariantes duras de las vigas**, sobre varias semillas.
///
/// Una viga cuelga del techo, así que lo que puede romper no es el paso sino la CABEZA y la SUBIDA:
/// menos de 2,60 m de hueco bajo ella, o una viga cruzando la boca de un pozo o bajo un agujero de
/// forjado. Ninguna de las tres produce un test rojo en otro sitio.
#[test]
fn beams_hang_where_the_grammar_says() {
    use super::plan::PlanRect;

    let m = real_manifest();
    let seeds = validate::sweep_seeds(sweep_seed_count(3));
    // `BEAM_MIN_CLEAR_CM` (300) menos `BEAM_DROP_CM` (40).
    const MIN_UNDER_CM: i32 = 260;

    let mut seen = 0usize;
    for &seed in &seeds {
        for &(rx, rz) in NEAR_REGIONS.iter() {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let inside = validate::region_inside(&m, seed, region);
            for b in inside
                .filled
                .solids
                .iter()
                .filter(|s| super::fill::is_beam(s))
            {
                seen += 1;
                let rect = PlanRect {
                    min_x_cm: b.x_cm,
                    min_z_cm: b.z_cm,
                    max_x_cm: b.x_cm + b.size_x_cm,
                    max_z_cm: b.z_cm + b.size_z_cm,
                };
                // 1 — cuelga de UN espacio construido y deja 2,60 de hueco sobre su suelo.
                let mut host = None;
                for (n, st) in inside.building.storeys.iter().enumerate() {
                    for (_, sp) in st.built() {
                        if sp.covers_rect(&rect) && sp.floor_y_cm < b.bottom_y_cm {
                            host = Some((n, sp));
                        }
                    }
                }
                let Some((n, sp)) = host else {
                    panic!(
                        "semilla {seed:#x} región ({rx},{rz}): viga en ({},{}) sin espacio debajo",
                        b.x_cm, b.z_cm
                    );
                };
                assert!(
                    b.bottom_y_cm - sp.floor_y_cm >= MIN_UNDER_CM,
                    "semilla {seed:#x} región ({rx},{rz}): viga a {} cm del suelo de su sala",
                    b.bottom_y_cm - sp.floor_y_cm
                );
                // 2 — ni sobre la boca de un pozo que arranca en esta planta.
                for w in inside.building.wells.iter().filter(|w| w.storey_below == n) {
                    assert!(
                        !w.rect.shrunk(-50).overlaps(&rect),
                        "semilla {seed:#x} región ({rx},{rz}): viga sobre la boca del pozo en \
                         ({},{})",
                        w.rect.min_x_cm,
                        w.rect.min_z_cm
                    );
                }
            }
        }
    }
    assert!(
        seen > 100,
        "sólo {seen} vigas en {} regiones: la gramática no está emitiendo",
        seeds.len() * NEAR_REGIONS.len()
    );
    println!("[vigas] {seen} vigas revisadas");
}

/// ADR-119 enmienda 1 — **las invariantes duras de los pilares**, sobre varias semillas.
///
/// El barrido de niveles ya dice que el mundo con pilares sigue siendo andable (mancha mayor y nav);
/// esto dice que cada pilar está donde tiene que estar. Son las cuatro cosas que, si se rompen, no
/// producen un test rojo en ningún otro sitio: el ráster los estampa macizos sin quejarse y el
/// síntoma es una puerta tapiada o dos columnas pegadas cien metros más allá.
#[test]
fn pillars_land_where_the_grammar_says() {
    use super::plan::{PlanRect, SpaceRole};

    let m = real_manifest();
    let seeds = validate::sweep_seeds(sweep_seed_count(3));
    // `PILLAR_GAP_MIN_CM` (500, cara a cara) menos el desorden de los dos vecinos
    // (2 × `PILLAR_JITTER_MAX_CM`). Desde ADR-105 enm. 4 el lado varía, así que se mide el HUECO.
    const MIN_GAP_CM: i32 = 500 - 2 * 55;
    // `PILLAR_DOOR_CLEAR_CM`, con un centímetro de tolerancia por el redondeo del centro.
    const DOOR_CLEAR_CM: i32 = 400;

    let mut seen = 0usize;
    // Histograma de lados (200, 250, …, 400) y brazos de cruz, para leer la enmienda 4 en cifras.
    let mut sides = [0usize; 5];
    let mut arms = 0usize;
    for &seed in &seeds {
        for &(rx, rz) in NEAR_REGIONS.iter() {
            let region = Wg3RegionCoord { x: rx, z: rz };
            let inside = validate::region_inside(&m, seed, region);
            let pillars: Vec<&super::segment::Wg3Solid> = inside
                .filled
                .solids
                .iter()
                .filter(|s| super::fill::is_pillar(s))
                .collect();
            seen += pillars.len();
            for p in &pillars {
                let long = p.size_x_cm.max(p.size_z_cm);
                sides[((long - 200) / 50).clamp(0, 4) as usize] += 1;
                if p.size_x_cm != p.size_z_cm {
                    arms += 1;
                }
            }

            for p in &pillars {
                let rect = PlanRect {
                    min_x_cm: p.x_cm,
                    min_z_cm: p.z_cm,
                    max_x_cm: p.x_cm + p.size_x_cm,
                    max_z_cm: p.z_cm + p.size_z_cm,
                };
                let (mid_x, mid_z) = (p.x_cm + p.size_x_cm / 2, p.z_cm + p.size_z_cm / 2);

                // 1 — todo pilar cae DENTRO de una nave construida y a la cota de su planta.
                let mut host = None;
                for (n, st) in inside.building.storeys.iter().enumerate() {
                    for (i, sp) in st.built() {
                        // Por la HUELLA (ADR-120 D1): desde que dos espacios se entrelazan sus
                        // envolventes se pisan, así que preguntando por `rect` el macizo se le
                        // atribuye al vecino y el test mide la sala equivocada. Las huellas son
                        // disjuntas, así que dueño hay exactamente uno.
                        if sp.floor_y_cm == p.bottom_y_cm && sp.covers_rect(&rect) {
                            host = Some((n, i, sp));
                        }
                    }
                }
                let Some((n, i, sp)) = host else {
                    let near: Vec<String> = inside
                        .building
                        .storeys
                        .iter()
                        .enumerate()
                        .flat_map(|(n, st)| {
                            st.built()
                                .filter(|(_, sp)| sp.rect.overlaps(&rect))
                                .map(move |(i, sp)| {
                                    format!(
                                        "planta {n} espacio {i} ({}) cota {} partes {:?}",
                                        sp.role.name(),
                                        sp.floor_y_cm,
                                        sp.parts()
                                    )
                                })
                                .collect::<Vec<_>>()
                        })
                        .collect();
                    panic!(
                        "semilla {seed:#x} región ({rx},{rz}): pilar en ({},{}) cota {} fuera de \
                         todo espacio construido — envolventes que lo pisan: {near:?}",
                        p.x_cm, p.z_cm, p.bottom_y_cm
                    );
                };
                assert_eq!(
                    sp.role,
                    SpaceRole::Hall,
                    "semilla {seed:#x} región ({rx},{rz}): pilar en un espacio {} y no en una nave",
                    sp.role.name()
                );
                assert_eq!(
                    sp.rise_cm, 0,
                    "semilla {seed:#x} región ({rx},{rz}): pilar en un espacio con desnivel"
                );

                // 2 — ningún pilar delante de una puerta. Un vano tapiado por un macizo es
                //     exactamente el fallo mudo que ADR-118 persiguió una auditoría entera.
                let plan = &inside.building.storeys[n];
                for (dx, dz) in plan
                    .links
                    .iter()
                    .filter(|l| l.a == i || l.b == i)
                    .map(|l| (l.at_x_cm, l.at_z_cm))
                    .chain(
                        plan.gates
                            .iter()
                            .filter(|g| g.space == i)
                            .map(|g| (g.x_cm, g.z_cm)),
                    )
                {
                    assert!(
                        (dx - mid_x).abs() >= DOOR_CLEAR_CM || (dz - mid_z).abs() >= DOOR_CLEAR_CM,
                        "semilla {seed:#x} región ({rx},{rz}): pilar en ({mid_x},{mid_z}) a menos \
                         de {DOOR_CLEAR_CM} cm de la puerta ({dx},{dz}) [planta {n} espacio {i}                          cota {} tamaño {}x{} alto {}..{} estilo {} ground {}]",
                        sp.floor_y_cm,
                        p.size_x_cm,
                        p.size_z_cm,
                        p.bottom_y_cm,
                        p.top_y_cm,
                        p.style,
                        inside.building.ground
                    );
                }

                // 3 — ni encima del aterrizaje de una escalera: deja el cuerpo clavado a media
                //     subida, y con dos plantas el barrido no podía verlo.
                for w in inside
                    .building
                    .wells
                    .iter()
                    .filter(|w| w.storey_below + 1 == n)
                {
                    assert!(
                        !w.rect.shrunk(-50).overlaps(&rect),
                        "semilla {seed:#x} región ({rx},{rz}): pilar sobre el rellano del pozo en \
                         ({},{})",
                        w.rect.min_x_cm,
                        w.rect.min_z_cm
                    );
                }
            }

            // 4 — dos pilares de la misma cota nunca se juntan por debajo del mínimo. El paso entre
            //     dos pilares es lo que se anda, y el ráster conservador ya se come medio metro por
            //     cada cara (ADR-105 D6).
            for (a, p) in pillars.iter().enumerate() {
                for q in pillars.iter().skip(a + 1) {
                    if p.bottom_y_cm != q.bottom_y_cm {
                        continue;
                    }
                    // Los dos brazos de una cruz comparten centro: son el mismo pilar.
                    if p.x_cm + p.size_x_cm / 2 == q.x_cm + q.size_x_cm / 2
                        && p.z_cm + p.size_z_cm / 2 == q.z_cm + q.size_z_cm / 2
                    {
                        continue;
                    }
                    // Hueco cara a cara en el eje que más los separa.
                    let d = (q.x_cm - (p.x_cm + p.size_x_cm))
                        .max(p.x_cm - (q.x_cm + q.size_x_cm))
                        .max(q.z_cm - (p.z_cm + p.size_z_cm))
                        .max(p.z_cm - (q.z_cm + q.size_z_cm));
                    assert!(
                        d >= MIN_GAP_CM,
                        "semilla {seed:#x} región ({rx},{rz}): pilares a {d} cm en ({},{}) y \
                         ({},{})",
                        p.x_cm,
                        p.z_cm,
                        q.x_cm,
                        q.z_cm
                    );
                }
            }
        }
    }
    assert!(
        seen > 100,
        "sólo {seen} pilares en {} regiones: la gramática no está emitiendo",
        seeds.len() * NEAR_REGIONS.len()
    );
    println!(
        "[pilares] {seen} pilares revisados | lados 200/250/300/350/400: {}/{}/{}/{}/{} | {} brazos \
         de cruz ({} cruces)",
        sides[0],
        sides[1],
        sides[2],
        sides[3],
        sides[4],
        arms,
        arms / 2
    );
}

/// SONDA — **dónde están las naves con pilares**, para poder ir a verlas.
///
/// Un test dice que la retícula cumple sus invariantes; no dice si andando por dentro se lee como
/// arquitectura. Para eso hay que ir, y para ir hace falta la coordenada.
///
/// `WG3_PROBE_SEED=42 WG3_PROBE_REGION=0,0 cargo test --release --bin backrooms_server
/// probe_pillar_halls -- --ignored --nocapture`
#[test]
#[ignore = "sonda de medida; se pide a mano"]
fn probe_pillar_halls() {
    use super::plan::SpaceRole;

    let m = real_manifest();
    let seed = std::env::var("WG3_PROBE_SEED")
        .ok()
        .and_then(|v| match v.trim().strip_prefix("0x") {
            Some(h) => u64::from_str_radix(h, 16).ok(),
            None => v.trim().parse().ok(),
        })
        .unwrap_or(LIVE_SEED);
    let regions: Vec<(i32, i32)> = match std::env::var("WG3_PROBE_REGION").ok() {
        Some(v) => {
            let mut it = v.split(',').map(|t| t.trim().parse::<i32>());
            vec![(it.next().unwrap().unwrap(), it.next().unwrap().unwrap())]
        }
        None => NEAR_REGIONS.to_vec(),
    };

    /// Una nave con retícula, tal como la ordena esta sonda: `(pilares, centro x, centro z,
    /// distancia al origen, cota, planta, ancho, fondo)`. Es una tupla y no un struct porque su
    /// única razón de existir es el `sort_by` de tres líneas más abajo.
    type PillarHall = (usize, f32, f32, f32, i32, usize, f32, f32);
    let mut found: Vec<PillarHall> = Vec::new();
    for (rx, rz) in regions {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let inside = validate::region_inside(&m, seed, region);
        let pillars: Vec<&super::segment::Wg3Solid> = inside
            .filled
            .solids
            .iter()
            .filter(|s| super::fill::is_pillar(s))
            .collect();
        for (n, st) in inside.building.storeys.iter().enumerate() {
            for (_, sp) in st.built() {
                if sp.role != SpaceRole::Hall {
                    continue;
                }
                let k = pillars
                    .iter()
                    .filter(|p| {
                        p.bottom_y_cm == sp.floor_y_cm
                            && p.x_cm >= sp.rect.min_x_cm
                            && p.x_cm < sp.rect.max_x_cm
                            && p.z_cm >= sp.rect.min_z_cm
                            && p.z_cm < sp.rect.max_z_cm
                    })
                    .count();
                if k == 0 {
                    continue;
                }
                let (cx, cz) = sp.rect.centre_m();
                found.push((
                    k,
                    cx,
                    cz,
                    sp.rect.area_m2(),
                    sp.floor_y_cm,
                    n,
                    sp.rect.width_cm() as f32 / 100.0,
                    sp.rect.depth_cm() as f32 / 100.0,
                ));
            }
        }
    }
    // Las más pobladas primero, y a igualdad la más cerca del origen: es adonde se puede llegar.
    found.sort_by(|a, b| {
        b.0.cmp(&a.0)
            .then((a.1 * a.1 + a.2 * a.2).total_cmp(&(b.1 * b.1 + b.2 * b.2)))
    });
    println!(
        "[naves] semilla {seed:#x}: {} naves con pilares",
        found.len()
    );
    for (k, cx, cz, area, floor, storey, w, d) in found.iter().take(20) {
        println!(
            "[naves]   {k:>2} pilares  centro ({cx:>8.1}, {cz:>8.1})  {w:>5.1} × {d:>5.1} m = \
             {area:>6.0} m²  planta {storey} cota {floor} cm  · dist origen {:.0} m",
            (cx * cx + cz * cz).sqrt()
        );
    }
}

// -- LA PLANTA ABIERTA DE OFICINA ----------------------------------------------------------------

/// **Una por planta, con papel de oficina, del tamano pedido y con DOS bocas.**
///
/// Las cuatro cosas juntas y no en cuatro tests, porque las cuatro son la misma decision: si la sala
/// sale `Hall` no lleva falso techo ni puestos, si sale de 800 m2 no es una oficina, y si sale con
/// una sola boca `retag_dead_ends` la degrada a `DeadEnd` y pierde las dos primeras.
///
/// El area se mide sobre la ENVOLVENTE: un hueco de escalera puede morderle la huella despues
/// (`dig_wells`), y eso no la deja de ser lo que se fundio.
#[test]
fn the_open_plan_office_is_one_per_storey_and_reads_as_an_office() {
    use super::plan::{self, SpaceRole};

    let seeds = validate::sweep_seeds(sweep_seed_count(6));
    let mut storeys = 0usize;
    let mut rooms = 0usize;
    let mut area_sum = 0.0f32;
    for &seed in &seeds {
        for &(x, z) in &NEAR_REGIONS {
            let region = Wg3RegionCoord { x, z };
            let building = validate::building_of(seed, region, plan::REGION_STOREYS);
            for (n, storey) in building.storeys.iter().enumerate() {
                storeys += 1;
                let here: Vec<usize> = storey
                    .spaces
                    .iter()
                    .enumerate()
                    .filter(|(_, s)| s.open_plan)
                    .map(|(i, _)| i)
                    .collect();
                assert!(
                    here.len() <= 1,
                    "semilla {seed:#x} region ({x},{z}) planta {n}: {} plantas abiertas, y solo puede haber una",
                    here.len()
                );
                for &i in &here {
                    let s = &storey.spaces[i];
                    assert_eq!(
                        s.role,
                        SpaceRole::Office,
                        "semilla {seed:#x} region ({x},{z}) planta {n}: la planta abierta salio con papel {}",
                        s.role.name()
                    );
                    let area = s.rect.area_m2();
                    assert!(
                        (300.0..=500.0).contains(&area),
                        "semilla {seed:#x} region ({x},{z}) planta {n}: {area:.0} m2 fuera del rango pedido"
                    );
                    let doors = storey.links.iter().filter(|l| l.a == i || l.b == i).count();
                    assert!(
                        doors >= 2,
                        "semilla {seed:#x} region ({x},{z}) planta {n}: la planta abierta tiene {doors} boca(s)"
                    );
                    rooms += 1;
                    area_sum += area;
                }
            }
        }
    }
    let media = if rooms > 0 {
        area_sum / rooms as f32
    } else {
        0.0
    };
    println!(
        "[planta-abierta] {rooms} salas en {storeys} plantas ({:.0} %), media {media:.0} m2",
        100.0 * rooms as f32 / storeys.max(1) as f32
    );
    // No se pide una por planta -hace falta que la zona sea de caracter oficina y que haya un par de
    // hermanas del tamano justo-, pero si esto baja a cero la fusion ha dejado de dispararse.
    assert!(
        rooms * 20 >= storeys,
        "solo {rooms} plantas abiertas en {storeys} plantas: la fusion casi no encuentra pareja"
    );
}

/// **Conectividad desde el spawn, en el grafo del plan.** Se llega a la planta abierta andando
/// desde la espina, que es de donde cuelga todo lo que el jugador puede recorrer.
#[test]
fn the_open_plan_office_is_reachable_from_the_spine() {
    use super::plan::{self, SpaceRole};

    let seeds = validate::sweep_seeds(sweep_seed_count(6));
    let mut checked = 0usize;
    for &seed in &seeds {
        for &(x, z) in &NEAR_REGIONS {
            let region = Wg3RegionCoord { x, z };
            let building = validate::building_of(seed, region, plan::REGION_STOREYS);
            for (n, storey) in building.storeys.iter().enumerate() {
                let Some(room) = storey.spaces.iter().position(|s| s.open_plan) else {
                    continue;
                };
                let Some(start) = storey
                    .spaces
                    .iter()
                    .position(|s| s.role == SpaceRole::Spine)
                    .or_else(|| storey.spaces.iter().position(|s| s.role.is_circulation()))
                else {
                    continue;
                };
                let mut seen = vec![false; storey.spaces.len()];
                seen[start] = true;
                let mut queue = vec![start];
                while let Some(a) = queue.pop() {
                    for l in &storey.links {
                        let other = if l.a == a {
                            l.b
                        } else if l.b == a {
                            l.a
                        } else {
                            continue;
                        };
                        if !seen[other] {
                            seen[other] = true;
                            queue.push(other);
                        }
                    }
                }
                assert!(
                    seen[room],
                    "semilla {seed:#x} region ({x},{z}) planta {n}: a la planta abierta no se llega desde la circulacion"
                );
                checked += 1;
            }
        }
    }
    println!("[planta-abierta] {checked} salas alcanzables desde la espina");
    assert!(checked > 0, "ninguna planta abierta que comprobar");
}

/// **Y andando de verdad**: el suelo de la sala esta en la MANCHA MAYOR del raster, que es la que
/// contiene al jugador. El grafo del plan puede decir que hay puerta y el raster tapiarla -es
/// exactamente el fallo que ya midio ADR-098-, asi que esto se comprueba sobre lo inundado.
#[test]
fn the_open_plan_office_floor_is_in_the_main_blob() {
    use super::plan::REGION_STOREYS;

    let m = real_manifest();
    let mut checked = 0usize;
    for &(x, z) in &NEAR_REGIONS {
        let region = Wg3RegionCoord { x, z };
        let building = validate::building_of(LIVE_SEED, region, REGION_STOREYS);
        let ground = &building.storeys[building.ground];
        let Some(room) = ground.spaces.iter().position(|s| s.open_plan) else {
            continue;
        };
        let s = ground.spaces[room];
        let inside = validate::region_inside(&m, LIVE_SEED, region);
        let y = s.floor_y_cm as f32 / 100.0;
        let (mut on_main, mut sampled) = (0usize, 0usize);
        // Rejilla de muestreo cada metro y medio, con medio metro de margen a las paredes.
        let (x0, x1) = (s.rect.min_x_cm + 50, s.rect.max_x_cm - 50);
        let (z0, z1) = (s.rect.min_z_cm + 50, s.rect.max_z_cm - 50);
        let mut px = x0;
        while px <= x1 {
            let mut pz = z0;
            while pz <= z1 {
                let (fx, fz) = (px as f32 / 100.0, pz as f32 / 100.0);
                if let Some(blob) = inside.grid.blob_at(fx, fz, y) {
                    sampled += 1;
                    if blob == inside.grid.main {
                        on_main += 1;
                    }
                }
                pz += 150;
            }
            px += 150;
        }
        println!(
            "[planta-abierta] region ({x},{z}): {on_main}/{sampled} muestras en la mancha mayor"
        );
        assert!(
            sampled > 0,
            "region ({x},{z}): la planta abierta no tiene ni una cota pisable"
        );
        assert!(
            on_main * 10 >= sampled * 9,
            "region ({x},{z}): solo {on_main} de {sampled} muestras de la planta abierta caen en la mancha mayor"
        );
        checked += 1;
    }
    assert!(
        checked > 0,
        "ninguna de las nueve regiones cercanas tiene planta abierta en la calle"
    );
}

/// **La sala se llena de puestos, en filas y con su pasillo transversal.**
///
/// Un despacho normal pasa por el sorteo de `Knobs::cubicles`; esta sala no, porque para eso se ha
/// fundido. Se cuentan las MESAS dentro de su envolvente y la columna de celdas que se cede al
/// pasillo que cruza las filas: sin ella se entra por una esquina y se sale por la otra andando
/// veinte metros entre mamparas.
#[test]
fn the_open_plan_office_is_filled_with_desks_in_rows() {
    use super::fill;
    use super::plan::REGION_STOREYS;

    let m = real_manifest();
    let mut checked = 0usize;
    for &(x, z) in &NEAR_REGIONS {
        let region = Wg3RegionCoord { x, z };
        let building = validate::building_of(LIVE_SEED, region, REGION_STOREYS);
        let ground = &building.storeys[building.ground];
        let Some(room) = ground.spaces.iter().position(|s| s.open_plan) else {
            continue;
        };
        let s = ground.spaces[room];
        let filled = fill::fill_building(&building, &m);
        let floor = s.floor_y_cm;
        let inside = |px: i32, pz: i32| -> bool {
            px >= s.rect.min_x_cm
                && px <= s.rect.max_x_cm
                && pz >= s.rect.min_z_cm
                && pz <= s.rect.max_z_cm
        };
        let desks = filled
            .props
            .iter()
            .filter(|p| {
                p.kind == super::segment::PROP_DESK
                    && (p.y_cm - floor).abs() < 100
                    && inside(p.x_cm, p.z_cm)
            })
            .count();
        let walls = filled
            .solids
            .iter()
            .filter(|o| {
                fill::is_cubicle_wall(o)
                    && (o.bottom_y_cm - floor).abs() < 100
                    && inside(o.x_cm, o.z_cm)
            })
            .count();
        println!(
            "[planta-abierta] region ({x},{z}): {desks} mesas y {walls} mamparas en {:.0} m2",
            s.rect.area_m2()
        );
        assert!(
            desks >= 12,
            "region ({x},{z}): la planta abierta se quedo en {desks} puestos"
        );
        assert!(walls > 0, "region ({x},{z}): puestos sin una sola mampara");
        checked += 1;
    }
    assert!(
        checked > 0,
        "ninguna planta abierta en la calle que rellenar"
    );
}

/// ADR-122 B2 — **las rampas existen y se andan en los dos sentidos.**
///
/// La rampa se añade encima de las tiras de un hundido, así que no puede quitar suelo; lo que este
/// test fija es lo que sí puede fallar sin que el barrido lo note: que el productor no emita ninguna
/// (la mancha mayor seguiría al 99,7 %), que emita una ilegal, o que la navegación de las criaturas no
/// la recorra de abajo arriba o de arriba abajo por el ráster conservador.
#[test]
fn every_served_ramp_is_legal_and_walkable_both_ways() {
    use super::collision::Wg3CollisionCache;
    use super::nav;
    use super::world::{Wg3ServedWorld, Wg3WorldCache};
    use crate::world::Vec3;

    const BODY_M: f32 = 1.8;
    let m = real_manifest();
    let seeds = validate::sweep_seeds(sweep_seed_count(3));
    let mut total = 0usize;
    let mut failures: Vec<String> = Vec::new();

    for &seed in &seeds {
        for &(rx, rz) in NEAR_REGIONS.iter() {
            let served = Wg3ServedWorld::plan_region(&m, seed, Wg3RegionCoord { x: rx, z: rz });
            for r in served.ramps() {
                total += 1;
                let problems = r.problems();
                if !problems.is_empty() {
                    failures.push(format!(
                        "semilla {seed:#x} región ({rx},{rz}): {r:?} {problems:?}"
                    ));
                    continue;
                }
                // Punto bajo: dentro de la rampa, a 1,25 m de su borde bajo — el borde bajo entra en el grosor
                // de la pared del fondo y el ráster conservador cierra esa celda. Punto alto: 75 cm más
                // allá del borde alto, ya en la tira de la puerta.
                let (cx, cz) = r.centre();
                let (dx, dz) = match r.dir {
                    0 => (0.0, 1.0),
                    1 => (1.0, 0.0),
                    2 => (0.0, -1.0),
                    _ => (-1.0, 0.0),
                };
                let half = r.along_cm() as f32 * 0.005;
                let low = Vec3::new(
                    cx - dx * (half - 1.25),
                    r.bottom_y_cm as f32 * 0.01 + BODY_M + 0.1,
                    cz - dz * (half - 1.25),
                );
                let high = Vec3::new(
                    cx + dx * (half + 0.75),
                    r.top_y_cm as f32 * 0.01 + BODY_M,
                    cz + dz * (half + 0.75),
                );

                // Por TRAMOS de como mucho 10 m: la búsqueda sólo mira una ventana de
                // `nav::NAV_WINDOW_M` (30 m), y hay hundidos de 40 m. Cada tramo, en los dos sentidos.
                let mut worlds = Wg3WorldCache::default();
                let mut cache = Wg3CollisionCache::new();
                cache.prewarm_for_move(&mut worlds, &m, seed, low, high);
                let span = ((high.x - low.x).powi(2) + (high.z - low.z).powi(2)).sqrt();
                let legs = (span / 10.0).ceil().max(1.0) as usize;
                let mut path = Vec::new();
                let mut broken = None;
                for leg in 0..legs {
                    let t0 = leg as f32 / legs as f32;
                    let t1 = (leg + 1) as f32 / legs as f32;
                    let at = |t: f32| {
                        // La cota, del SUELO REAL del ráster (la caja de la rampa, o la tira), y la
                        // columna, una PISABLE: entre tiras hay parteluces que parten las bocas anchas,
                        // y un extremo de tramo clavado en uno es un destino imposible, no una rampa
                        // rota. Se busca a lo ancho, perpendicular a `dir`, hasta 2 m.
                        let (x0, z0) = (low.x + (high.x - low.x) * t, low.z + (high.z - low.z) * t);
                        let top = r.top_y_cm as f32 * 0.01 + 0.3;
                        for off in [0.0f32, 0.75, -0.75, 1.5, -1.5, 2.0, -2.0] {
                            let (x, z) = (x0 - dz * off, z0 + dx * off);
                            if let Some(floor) = cache.floor_below_m(x, z, top) {
                                if nav::floor_at(&cache, x, z, floor).is_some() {
                                    return Vec3::new(x, floor + BODY_M, z);
                                }
                            }
                        }
                        Vec3::new(x0, r.bottom_y_cm as f32 * 0.01 + BODY_M, z0)
                    };
                    let (a, b) = (at(t0), at(t1));
                    let up = nav::find_path(&cache, a, b, &mut path).reached;
                    let down = nav::find_path(&cache, b, a, &mut path).reached;
                    if !up || !down {
                        broken = Some((leg, up, down));
                        break;
                    }
                }
                if let Some((leg, up, down)) = broken {
                    failures.push(format!(
                        "semilla {seed:#x} región ({rx},{rz}): rampa {r:?} tramo {leg}/{legs} sube={up} baja={down}"
                    ));
                }
            }
        }
    }

    println!(
        "[wg3-ramp] {total} rampas en {} semillas × {} regiones",
        seeds.len(),
        NEAR_REGIONS.len()
    );
    assert!(total > 0, "el productor no ha emitido ninguna rampa");
    assert!(
        failures.is_empty(),
        "{} de {total} rampas fallan:\n{}",
        failures.len(),
        failures.join("\n")
    );
}

/// Sonda (ADR-122 B2): recorre la rampa `WG3_PROBE_RAMP="seed,rx,rz,x_cm,z_cm"` cada 50 cm a lo largo
/// de su eje, del borde bajo al alto, e imprime por punto el suelo del ráster, el techo libre, si la
/// navegación lo acepta y las cotas que ofrece a sus cuatro vecinas.
#[test]
#[ignore]
fn probe_ramp_columns() {
    use super::collision::Wg3CollisionCache;
    use super::nav;
    use super::world::{Wg3ServedWorld, Wg3WorldCache};
    use crate::world::Vec3;

    let spec = std::env::var("WG3_PROBE_RAMP").expect("WG3_PROBE_RAMP=seed,rx,rz,x_cm,z_cm");
    let parts: Vec<&str> = spec.split(',').collect();
    let seed = u64::from_str_radix(parts[0].trim_start_matches("0x"), 16).unwrap();
    let (rx, rz): (i32, i32) = (parts[1].parse().unwrap(), parts[2].parse().unwrap());
    let (x_cm, z_cm): (i32, i32) = (parts[3].parse().unwrap(), parts[4].parse().unwrap());

    let m = real_manifest();
    let served = Wg3ServedWorld::plan_region(&m, seed, Wg3RegionCoord { x: rx, z: rz });
    let r = *served
        .ramps()
        .iter()
        .find(|r| r.x_cm == x_cm && r.z_cm == z_cm)
        .expect("rampa no encontrada");
    println!("[probe-ramp] {r:?}");
    let (cx, cz) = r.centre();
    let (dx, dz) = match r.dir {
        0 => (0.0, 1.0),
        1 => (1.0, 0.0),
        2 => (0.0, -1.0),
        _ => (-1.0, 0.0),
    };
    let half = r.along_cm() as f32 * 0.005;
    let a = Vec3::new(cx - dx * half, 1.8, cz - dz * half);
    let b = Vec3::new(cx + dx * (half + 1.0), 1.8, cz + dz * (half + 1.0));
    let mut worlds = Wg3WorldCache::default();
    let mut cache = Wg3CollisionCache::new();
    cache.prewarm_for_move(&mut worlds, &m, seed, a, b);

    let mut t = -half - 0.5;
    while t <= half + 1.5 {
        let (x, z) = (cx + dx * t, cz + dz * t);
        let top = r.top_y_cm as f32 * 0.01 + 0.3;
        let floor = cache.floor_below_m(x, z, top);
        let head = floor.and_then(|f| cache.headroom_m(x, z, f));
        let navf = floor.and_then(|f| nav::floor_at(&cache, x, z, f));
        let mut next = Vec::new();
        if let Some(f) = floor {
            let (nx, nz) = (x + dx * 0.5, z + dz * 0.5);
            next = nav::floors_at(&cache, nx, nz, f);
        }
        println!(
            "[probe-ramp] t={t:+.2} ({x:.2},{z:.2}) suelo={floor:?} libre={head:?} nav={navf:?} siguiente={next:?}"
        );
        t += 0.5;
    }
}
