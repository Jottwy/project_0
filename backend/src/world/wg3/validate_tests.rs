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

    let mut seen = 0usize;
    let mut screens = 0usize;
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
                        if sp.floor_y_cm == p.bottom_y_cm && sp.covers_rect(&rect) {
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
                if h == SCREEN_H_CM.min(clear) {
                    screens += 1;
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
    println!("[divisiones] {seen} revisadas, {screens} mamparas por debajo del techo");
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
                         de {DOOR_CLEAR_CM} cm de la puerta ({dx},{dz})"
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
