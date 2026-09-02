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
                .filter(|s| s.size_x_cm == 200 && s.size_z_cm == 200)
                .count();
            segments_total += inside.filled.segments.len();

            for s in &inside.filled.segments {
                *height_hist.entry(s.height_cm).or_default() += 1;
            }

            for (n, plan) in inside.building.storeys.iter().enumerate() {
                for (i, sp) in plan.built() {
                    spaces_total += 1;
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
                        SpaceRole::Junction => 3,
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
    println!(
        "  tramos {}, macizos {}, de ellos pilares {}",
        segments_total, solids_total, pillars_total
    );
    println!(
        "  pilares por región: {:.2}",
        pillars_total as f32 / regions_n.max(1) as f32
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
