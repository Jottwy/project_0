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
        let ground = &inside.building.storeys[0];
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
