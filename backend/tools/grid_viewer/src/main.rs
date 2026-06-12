//! Fase 2 — visualizador 2D del grid real.
//!
//! Consume `generate_chunk_layer` (el Rust REAL, incluido vía #[path]) y
//! exporta PNGs. No toca grid_gen, ni Unity, ni IPC: solo lectura y export.
//!
//! Uso:
//!   grid_viewer [seed...] [--chunk CX,CZ] [--layer L] [--scale N] [--out DIR]
//!               [--mode single|layers|mosaic|all]
//!
//!   seed       una o más seeds (default: 42)
//!   --chunk    chunk base para vistas single/layers y centro del mosaico (default: 0,0)
//!   --layer    capa para single y mosaic (default: todas en mosaic, 0 en single)
//!   --scale    píxeles por celda (default: 10)
//!   --out      directorio de salida (default: viz_out junto al CWD)
//!   --mode     single = un chunk; layers = 4 capas lado a lado;
//!              mosaic = 3×3 chunks fusionados; all = layers + mosaics (default)

// Código fuente REAL del motor — mismos archivos .rs que compila backrooms_server.
#[path = "../../../src/world/grid_gen/mod.rs"]
mod grid_gen;

use grid_gen::{generate_chunk_layer, CellType, LayerGrid, CHUNK_CELLS, LAYER_PROFILES};
use image::{Rgb, RgbImage};

/// Paleta por cell_type (validada en el encargo de Fase 2).
fn color(ct: CellType) -> Rgb<u8> {
    match ct {
        CellType::Wall => Rgb([0x2a, 0x26, 0x20]),
        CellType::Corridor => Rgb([0xc9, 0xb8, 0x4a]),
        CellType::Open => Rgb([0xe0, 0xd2, 0x7a]),
        CellType::Pillar => Rgb([0x7a, 0x6a, 0x28]),
        CellType::Stair => Rgb([0x3a, 0xa3, 0x5a]),
        CellType::Pit => Rgb([0x3a, 0x7a, 0xa3]),
        CellType::Void => Rgb([0x1a, 0x16, 0x12]),
        CellType::Anomaly => Rgb([0xb8, 0x54, 0x2f]),
    }
}

const GAP_PX: u32 = 6;
const GAP_COLOR: Rgb<u8> = Rgb([0x10, 0x10, 0x10]);

struct Args {
    seeds: Vec<u64>,
    chunk: (i32, i32),
    layer: Option<i32>,
    scale: u32,
    out: String,
    mode: String,
}

fn parse_args() -> Args {
    let mut a = Args {
        seeds: Vec::new(),
        chunk: (0, 0),
        layer: None,
        scale: 10,
        out: "viz_out".to_string(),
        mode: "all".to_string(),
    };
    let mut it = std::env::args().skip(1);
    while let Some(arg) = it.next() {
        match arg.as_str() {
            "--chunk" => {
                let v = it.next().expect("--chunk CX,CZ");
                let (cx, cz) = v.split_once(',').expect("--chunk CX,CZ");
                a.chunk = (cx.trim().parse().unwrap(), cz.trim().parse().unwrap());
            }
            "--layer" => a.layer = Some(it.next().expect("--layer L").parse().unwrap()),
            "--scale" => a.scale = it.next().expect("--scale N").parse().unwrap(),
            "--out" => a.out = it.next().expect("--out DIR"),
            "--mode" => a.mode = it.next().expect("--mode M"),
            s => a.seeds.push(s.parse().unwrap_or_else(|_| panic!("seed inválida: {s}"))),
        }
    }
    if a.seeds.is_empty() {
        a.seeds.push(42);
    }
    a
}

/// Pinta un LayerGrid en la imagen con esquina superior izquierda en (ox, oy).
/// Eje z del grid hacia abajo en la imagen (vista de planta).
fn blit_grid(img: &mut RgbImage, grid: &LayerGrid, ox: u32, oy: u32, scale: u32) {
    for z in 0..CHUNK_CELLS {
        for x in 0..CHUNK_CELLS {
            let c = color(grid.get(x, z).kind());
            for pz in 0..scale {
                for px in 0..scale {
                    img.put_pixel(
                        ox + x as u32 * scale + px,
                        oy + z as u32 * scale + pz,
                        c,
                    );
                }
            }
        }
    }
}

fn chunk_grid(seed: u64, chunk: (i32, i32), layer: i32) -> LayerGrid {
    let rules = &LAYER_PROFILES[layer as usize % LAYER_PROFILES.len()];
    generate_chunk_layer(rules, seed, chunk, layer, &[]).grid
}

/// Vista (a): un chunk individual.
fn render_single(seed: u64, chunk: (i32, i32), layer: i32, scale: u32) -> RgbImage {
    let px = CHUNK_CELLS as u32 * scale;
    let mut img = RgbImage::new(px, px);
    blit_grid(&mut img, &chunk_grid(seed, chunk, layer), 0, 0, scale);
    img
}

/// Vista (b): las 4 capas del mismo chunk lado a lado.
fn render_layers(seed: u64, chunk: (i32, i32), scale: u32) -> RgbImage {
    let n = LAYER_PROFILES.len() as u32;
    let cpx = CHUNK_CELLS as u32 * scale;
    let mut img = RgbImage::from_pixel(n * cpx + (n - 1) * GAP_PX, cpx, GAP_COLOR);
    for layer in 0..n {
        let grid = chunk_grid(seed, chunk, layer as i32);
        blit_grid(&mut img, &grid, layer * (cpx + GAP_PX), 0, scale);
    }
    img
}

/// Vista (c): rejilla 3×3 de chunks fusionada — la prueba visual de las costuras.
fn render_mosaic(seed: u64, center: (i32, i32), layer: i32, scale: u32) -> RgbImage {
    let cpx = CHUNK_CELLS as u32 * scale;
    let mut img = RgbImage::new(3 * cpx, 3 * cpx);
    for dz in 0..3i32 {
        for dx in 0..3i32 {
            let chunk = (center.0 + dx - 1, center.1 + dz - 1);
            let grid = chunk_grid(seed, chunk, layer);
            blit_grid(&mut img, &grid, dx as u32 * cpx, dz as u32 * cpx, scale);
        }
    }
    img
}

fn main() {
    let args = parse_args();
    std::fs::create_dir_all(&args.out).expect("no se pudo crear el directorio de salida");
    let (cx, cz) = args.chunk;
    let mut written: Vec<String> = Vec::new();

    for &seed in &args.seeds {
        match args.mode.as_str() {
            "single" => {
                let layer = args.layer.unwrap_or(0);
                let path = format!("{}/seed{seed}_chunk{cx}_{cz}_layer{layer}.png", args.out);
                render_single(seed, args.chunk, layer, args.scale).save(&path).unwrap();
                written.push(path);
            }
            "layers" => {
                let path = format!("{}/seed{seed}_layers_chunk{cx}_{cz}.png", args.out);
                render_layers(seed, args.chunk, args.scale).save(&path).unwrap();
                written.push(path);
            }
            "mosaic" => {
                let layers: Vec<i32> = match args.layer {
                    Some(l) => vec![l],
                    None => (0..LAYER_PROFILES.len() as i32).collect(),
                };
                for layer in layers {
                    let path = format!("{}/seed{seed}_mosaic3x3_layer{layer}.png", args.out);
                    render_mosaic(seed, args.chunk, layer, args.scale).save(&path).unwrap();
                    written.push(path);
                }
            }
            "all" => {
                let path = format!("{}/seed{seed}_layers_chunk{cx}_{cz}.png", args.out);
                render_layers(seed, args.chunk, args.scale).save(&path).unwrap();
                written.push(path);
                for layer in 0..LAYER_PROFILES.len() as i32 {
                    let path = format!("{}/seed{seed}_mosaic3x3_layer{layer}.png", args.out);
                    render_mosaic(seed, args.chunk, layer, args.scale).save(&path).unwrap();
                    written.push(path);
                }
            }
            m => panic!("modo desconocido: {m}"),
        }
    }

    println!("{} PNGs escritos:", written.len());
    for p in &written {
        println!("  {p}");
    }
}
