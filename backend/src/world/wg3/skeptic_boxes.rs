//! SONDA ESCEPTICA — reproduccion independiente del recuento de interpenetracion.
//! Fuerza bruta O(n^2), un solo umbral, clasificacion propia.

use std::collections::BTreeMap;

use super::manifest::{self, Wg3Manifest};
use super::placement::{self, PlacedBox};
use super::plan;
use super::segment::{self, KIND_CEILING, KIND_FLOOR, KIND_WALL};
use super::world::{Wg3RegionCoord, Wg3ServedWorld};

const AUDIT_REGIONS: [(i32, i32); 4] = [(0, 0), (1, 0), (0, 1), (-1, 2)];
const SERVED_SEED: u64 = 0xDEAD_BEEF_0000_002A;

fn real_manifest() -> Wg3Manifest {
    let path = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("Assets")
        .join("StreamingAssets")
        .join("wg3_manifest.json");
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("no se pudo leer {}: {e}", path.display()));
    manifest::parse_manifest(&text).expect("manifiesto invalido")
}

#[derive(Debug, Clone, Copy)]
struct B {
    min: [f32; 3],
    max: [f32; 3],
    kind: u8,
    origin: u8,
    owner: usize,
    /// indice de la caja dentro de su duenyo (orden de emision de segment_boxes)
    slot: usize,
    storey: i32,
}

fn aabb_of(b: &PlacedBox, origin: u8, owner: usize, slot: usize, storey: i32) -> B {
    let yaw = b.yaw_degrees.rem_euclid(360.0);
    let quarter = (yaw / 90.0).round();
    let (sx, sz) = if (quarter as i32) % 2 == 0 {
        (b.size[0], b.size[2])
    } else {
        (b.size[2], b.size[0])
    };
    let half = [sx * 0.5, b.size[1] * 0.5, sz * 0.5];
    B {
        min: [
            b.center[0] - half[0],
            b.center[1] - half[1],
            b.center[2] - half[2],
        ],
        max: [
            b.center[0] + half[0],
            b.center[1] + half[1],
            b.center[2] + half[2],
        ],
        kind: b.kind,
        origin,
        owner,
        slot,
        storey,
    }
}

fn kn(k: u8) -> &'static str {
    match k {
        KIND_FLOOR => "FLOOR",
        KIND_CEILING => "CEIL",
        KIND_WALL => "WALL",
        _ => "OTRO",
    }
}

fn ovol(a: &B, b: &B) -> f32 {
    let ox = (a.max[0].min(b.max[0]) - a.min[0].max(b.min[0])).max(0.0);
    let oy = (a.max[1].min(b.max[1]) - a.min[1].max(b.min[1])).max(0.0);
    let oz = (a.max[2].min(b.max[2]) - a.min[2].max(b.min[2])).max(0.0);
    ox * oy * oz
}

fn oxz(a: &B, b: &B) -> f32 {
    let ox = (a.max[0].min(b.max[0]) - a.min[0].max(b.min[0])).max(0.0);
    let oz = (a.max[2].min(b.max[2]) - a.min[2].max(b.min[2])).max(0.0);
    ox * oz
}

/// Un tramo de pared corre en X (lados N/S) o en Z (lados E/O)? Se deduce de la caja.
fn runs_in_x(b: &B) -> bool {
    (b.max[0] - b.min[0]) > (b.max[2] - b.min[2])
}

#[test]
#[ignore = "sonda esceptica"]
fn skeptic_overlap_recount() {
    let m = real_manifest();
    for (rx, rz) in AUDIT_REGIONS {
        let region = Wg3RegionCoord { x: rx, z: rz };
        let served = Wg3ServedWorld::plan_region(&m, SERVED_SEED, region);

        let mut thin_segments = 0usize;
        let mut thinnest_cm = i32::MAX;
        let mut boxes: Vec<B> = Vec::new();
        for (i, p) in served.placements().iter().enumerate() {
            let Some(piece) = m.piece(p.piece) else {
                continue;
            };
            let storey = (p.origin_y() * 100.0 / plan::STOREY_HEIGHT_CM as f32).round() as i32;
            for (s, b) in placement::placed_collision(piece, p).iter().enumerate() {
                boxes.push(aabb_of(b, 0, i, s, storey));
            }
        }
        for (i, s) in served.segments().iter().enumerate() {
            let storey = s.floor_y_cm.div_euclid(plan::STOREY_HEIGHT_CM);
            let narrow = s.size_x_cm.min(s.size_z_cm);
            thinnest_cm = thinnest_cm.min(narrow);
            if narrow < 30 {
                thin_segments += 1;
            }
            for (k, b) in segment::segment_boxes(s).iter().enumerate() {
                boxes.push(aabb_of(b, 1, i, k, storey));
            }
        }

        // --- pasada unica, fuerza bruta ---
        let mut n_solid = 0usize;
        let mut v_solid = 0.0f32;
        let mut n_touch = 0usize;
        let mut by_kind: BTreeMap<(u8, u8), (usize, f32)> = BTreeMap::new();

        let mut ww_total = 0usize;
        let mut ww_total_v = 0.0f32;
        let mut ww_same_seg = 0usize;
        let mut ww_same_seg_v = 0.0f32;
        let mut ww_same_seg_perp = 0usize;
        let mut ww_same_seg_par = 0usize;
        let mut ww_other = 0usize;
        let mut ww_other_v = 0.0f32;
        // esquina "canonica": prisma de 0.15 x 0.15 x h
        let mut corner_canon = 0usize;
        let mut corner_odd: Vec<String> = Vec::new();
        let mut corners: Vec<(B, B)> = Vec::new();

        // umbral alternativo, el que usa el apartado B ajeno
        let mut ww_same_seg_001 = 0usize;
        let mut ww_other_001 = 0usize;
        let mut b_same_literal = 0usize;
        let mut b_same_literal_pieces = 0usize;
        let mut b_other_literal = 0usize;

        let mut fc_v = 0.0f32;
        let mut fc_n = 0usize;
        // Caras coplanares VERTICALES duplicadas por la esquina: estan tapadas por vecino o al aire?
        let mut faces_total = 0usize;
        let mut faces_covered = 0usize;
        let mut faces_exposed = 0usize;

        for i in 0..boxes.len() {
            for j in (i + 1)..boxes.len() {
                let (x, y) = (&boxes[i], &boxes[j]);
                let v = ovol(x, y);
                let kinds = (x.kind.min(y.kind), x.kind.max(y.kind));

                if kinds == (KIND_WALL, KIND_WALL) && v > 0.001 {
                    if x.origin == 1 && y.origin == 1 && x.owner == y.owner {
                        ww_same_seg_001 += 1;
                    } else {
                        ww_other_001 += 1;
                    }
                    // criterio LITERAL del apartado B ajeno: no distingue pieza de tramo
                    if x.origin == y.origin && x.owner == y.owner {
                        b_same_literal += 1;
                        if x.origin == 0 {
                            b_same_literal_pieces += 1;
                        }
                    } else {
                        b_other_literal += 1;
                    }
                }

                if v > 0.01 {
                    n_solid += 1;
                    v_solid += v;
                    let e = by_kind.entry(kinds).or_insert((0, 0.0));
                    e.0 += 1;
                    e.1 += v;

                    if kinds == (KIND_FLOOR, KIND_CEILING) {
                        fc_n += 1;
                        fc_v += v;
                    }

                    if kinds == (KIND_WALL, KIND_WALL) {
                        ww_total += 1;
                        ww_total_v += v;
                        if x.owner == y.owner && x.origin == y.origin {
                            ww_same_seg += 1;
                            ww_same_seg_v += v;
                            if runs_in_x(x) != runs_in_x(y) {
                                ww_same_seg_perp += 1;
                            } else {
                                ww_same_seg_par += 1;
                            }
                            let dx = x.max[0].min(y.max[0]) - x.min[0].max(y.min[0]);
                            let dz = x.max[2].min(y.max[2]) - x.min[2].max(y.min[2]);
                            let dy = x.max[1].min(y.max[1]) - x.min[1].max(y.min[1]);
                            if (dx - 0.15).abs() < 0.005 && (dz - 0.15).abs() < 0.005 {
                                corner_canon += 1;
                                corners.push((*x, *y));
                            } else if corner_odd.len() < 5 {
                                corner_odd.push(format!(
                                    "    NO-ESQUINA misma pieza origin={} owner={} slots {}/{} \
                                     solape dx={dx:.3} dy={dy:.3} dz={dz:.3} v={v:.3}",
                                    x.origin, x.owner, x.slot, y.slot
                                ));
                            }
                        } else {
                            ww_other += 1;
                            ww_other_v += v;
                        }
                    }
                } else {
                    let dy = y.max[1].min(x.max[1]) - y.min[1].max(x.min[1]);
                    if dy.abs() <= 1e-4 && oxz(x, y) > 1e-4 {
                        n_touch += 1;
                    }
                }
            }
        }

        // Cada esquina duplica DOS caras verticales coplanares (la del plano X exterior y la del
        // plano Z exterior del prisma). Se sondea 2 cm mas alla de cada plano: si hay materia, la
        // cara queda tapada; si no, hay dos quads a la misma profundidad, al aire.
        for (x, y) in &corners {
            let px0 = x.min[0].max(y.min[0]);
            let px1 = x.max[0].min(y.max[0]);
            let pz0 = x.min[2].max(y.min[2]);
            let pz1 = x.max[2].min(y.max[2]);
            let cy = (x.min[1].max(y.min[1]) + x.max[1].min(y.max[1])) * 0.5;
            let px = if (x.min[0] - y.min[0]).abs() < 1e-3 {
                px0 - 0.02
            } else {
                px1 + 0.02
            };
            let pz = if (x.min[2] - y.min[2]).abs() < 1e-3 {
                pz0 - 0.02
            } else {
                pz1 + 0.02
            };
            let probes = [
                [px, cy, (pz0 + pz1) * 0.5],
                [(px0 + px1) * 0.5, cy, pz],
            ];
            for p in probes {
                faces_total += 1;
                let hit = boxes.iter().any(|b| {
                    p[0] > b.min[0]
                        && p[0] < b.max[0]
                        && p[1] > b.min[1]
                        && p[1] < b.max[1]
                        && p[2] > b.min[2]
                        && p[2] < b.max[2]
                });
                if hit {
                    faces_covered += 1;
                } else {
                    faces_exposed += 1;
                }
            }
        }

        println!("\n=== REGION ({rx},{rz}) ===");
        println!(
            "  {} colocaciones, {} tramos, {} cajas",
            served.placements().len(),
            served.segments().len(),
            boxes.len()
        );
        println!("  solape>0,01 m3: {n_solid} pares, {v_solid:.1} m3 | contacto sin solape: {n_touch}");
        for ((a, b), (n, v)) in &by_kind {
            println!("      {}/{}: {n} pares, {v:.1} m3", kn(*a), kn(*b));
        }
        println!(
            "  WALL/WALL (>0,01): {ww_total} pares {ww_total_v:.1} m3 | MISMO duenyo: \
             {ww_same_seg} ({ww_same_seg_v:.1} m3; perpendiculares {ww_same_seg_perp}, paralelas \
             {ww_same_seg_par}) | distinto duenyo: {ww_other} ({ww_other_v:.1} m3)"
        );
        println!(
            "  de las del MISMO duenyo, prisma 0,15x0,15: {corner_canon} de {ww_same_seg}"
        );
        for l in &corner_odd {
            println!("{l}");
        }
        println!(
            "  [umbral 0,001, solo TRAMOS] WALL/WALL mismo tramo: {ww_same_seg_001} | resto: \
             {ww_other_001}"
        );
        println!(
            "  [umbral 0,001, criterio LITERAL de B ajeno (origin==origin && owner==owner)] \
             b_same={b_same_literal} (de los cuales {b_same_literal_pieces} son de la MISMA PIEZA \
             del catalogo, no de un tramo) | b_other={b_other_literal}"
        );
        println!(
            "  tramos con lado < 30 cm (2 x WALL_THICKNESS): {thin_segments} de {}; lado minimo \
             {thinnest_cm} cm",
            served.segments().len()
        );
        println!(
            "  caras verticales de esquina sondeadas: {faces_total}; TAPADAS por otra caja \
             {faces_covered}, AL AIRE {faces_exposed} ({:.0} % al aire)",
            faces_exposed as f32 * 100.0 / faces_total.max(1) as f32
        );
        println!(
            "  FLOOR/CEIL >0,01: {fc_n} pares, {fc_v:.1} m3 ({:.0} % del volumen total)",
            fc_v * 100.0 / v_solid.max(1e-6)
        );
    }
}
