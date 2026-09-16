//! Modo standalone del mismo binario: `SMILER_SANDBOX=1 cargo run` genera una porción REAL de
//! mundo WG3, siembra un [`SmilerFlow`] y transmite geometría + nodos por un canal de depuración
//! **propio** — nunca por el wire de producción.
//!
//! Por qué un canal aparte y no un mensaje más en `PacketPayload` (Joel, 2026-09-16): el wire de
//! producción ya tiene una subida reservada — `docs/STATE.md` («Mapping P1c... wire 70 SIN
//! activar») — y `docs/systems/ipc-wire-schema.md` fija que hasta un cambio solo-P2P bumpea el
//! contador. Reclamar el 70 para esto competiría con ese trabajo en curso y, si los dos lo
//! activaran, el juego entero quedaría inarrancable (`wire_schema_mismatch`, ADR-061). Un socket
//! de depuración aparte no toca `WIRE_SCHEMA_VERSION` en absoluto: no compite por ningún número.
//!
//! Sigue siendo Fase 1 en espíritu — sin IA, sin luz, sin VFX del lado del servidor, sin ataque
//! (ver `smiler/mod.rs`) — solo que ahora hay algo que ver: la geometría real que generó WG3
//! alrededor de un punto de aparición, y el humo propagándose por ella entre dos anclas fijas para
//! que el comportamiento (rodeo, rendijas, verticalidad) se observe en vivo en vez de en un test.

use std::path::PathBuf;
use std::time::{Duration, Instant};

use log::{error, info};
use serde::Serialize;
use tokio::io::AsyncWriteExt;
use tokio::net::TcpListener;
use tokio::sync::broadcast;

use super::flow::{SmilerFlow, SmilerGeometry};
use crate::utils::Vec3;
use crate::world::wg3::collision::Wg3CollisionCache;
use crate::world::wg3::manifest;
use crate::world::wg3::raster::{Span, CM_PER_M, WG3_CELL_M};
use crate::world::wg3::world::Wg3WorldCache;

/// Puerto del canal de depuración. Deliberadamente lejos de `7777` (IPC) y `7778` (P2P por
/// defecto) para que no haga falta tocar ninguno de los dos al levantar el sandbox en la misma
/// máquina.
const DEFAULT_PORT: u16 = 7779;
pub const SANDBOX_PORT_ENV: &str = "SMILER_SANDBOX_PORT";

/// Misma semilla que `wg3::tests::SERVED_SEED`, duplicada a propósito: esa constante vive tras
/// `#[cfg(test)]` (R4, `world/wg3/tests.rs`) y este módulo compila también en el binario normal,
/// así que no puede depender de ella. Es la semilla que el resto de la suite ya trató como «el
/// mundo real», no una inventada para esta sesión.
const DEFAULT_SANDBOX_SEED: u64 = 0xDEAD_BEEF_0000_002A;

const TICK_HZ: f32 = 20.0;
const TICK_DT: f32 = 1.0 / TICK_HZ;
/// Cuánto dura cada tramo del vaivén A→B→A. Fijo por tiempo y no por densidad llegada a propósito:
/// un umbral de densidad puede no alcanzarse nunca si las constantes de `flow.rs` cambian, y
/// entonces el sandbox se quedaría congelado en un extremo sin que nadie lo note.
const SWAP_PERIOD_S: f32 = 15.0;
/// Radio alrededor del punto de aparición que se manda como geometría y en el que se busca la
/// segunda ancla. Bastante para varias salas, poco para no mandar medio mundo por el socket.
const DEBUG_RADIUS_M: f32 = 24.0;
/// Ventana vertical (± metros sobre `origin.y`) de la que se manda geometría. Una columna real de
/// WG3 puede tener tramos de otras plantas enteras por encima o por debajo del punto de aparición
/// (sótanos, pisos superiores) que no aportan nada a una prueba aislada del comportamiento junto
/// al jugador y sí multiplican por varias plantas la cantidad de cajas — de más de 13 000 a un
/// puñado por planta, medido sobre la región real de la semilla por defecto.
const VERTICAL_HALF_M: f32 = 6.0;
const SEED_DENSITY: f32 = 6.0;
const MAX_NODES_SENT: usize = 96;

#[derive(Serialize)]
struct GeometryBoxMsg {
    #[serde(rename = "c")]
    center: [f32; 3],
    #[serde(rename = "h")]
    half_size: [f32; 3],
}

#[derive(Serialize)]
struct GeometryMsg {
    #[serde(rename = "type")]
    kind: &'static str,
    boxes: Vec<GeometryBoxMsg>,
}

#[derive(Serialize)]
struct NodeMsg {
    #[serde(rename = "p")]
    pos: [f32; 3],
    #[serde(rename = "r")]
    radius: f32,
    #[serde(rename = "d")]
    density: f32,
}

#[derive(Serialize)]
struct NodesMsg {
    #[serde(rename = "type")]
    kind: &'static str,
    t: f64,
    nodes: Vec<NodeMsg>,
}

fn manifest_path() -> PathBuf {
    if let Some(p) = manifest::manifest_path_from_env() {
        return p;
    }
    // Mismo fichero que Unity ya exporta para el servidor real (`Wg3Config::from_env`); por
    // defecto aquí para que levantar el sandbox no exija fijar una variable de entorno más.
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("Assets")
        .join("StreamingAssets")
        .join("wg3_manifest.json")
}

fn spans_eq(a: &[Span], b: &[Span]) -> bool {
    a.len() == b.len()
        && a.iter()
            .zip(b)
            .all(|(x, y)| x.bottom_cm == y.bottom_cm && x.top_cm == y.top_cm)
}

/// Cajas macizas alrededor de `origin`, fusionando en X las columnas contiguas con exactamente los
/// mismos tramos — no es una descomposición óptima de rectángulos, solo evita mandar una caja de
/// 0,5 m por columna cuando un pasillo entero comparte la misma sección. Unity las dibuja una vez
/// al conectar: son estáticas mientras dure el proceso del sandbox.
fn spans_in_window(spans: &[Span], lo_cm: i32, hi_cm: i32) -> Vec<Span> {
    spans
        .iter()
        .copied()
        .filter(|s| (s.bottom_cm as i32) < hi_cm && (s.top_cm as i32) > lo_cm)
        .collect()
}

fn geometry_boxes(cache: &Wg3CollisionCache, origin: Vec3, radius_m: f32) -> Vec<GeometryBoxMsg> {
    let half_n = (radius_m / WG3_CELL_M).ceil() as i32;
    let lo_cm = ((origin.y - VERTICAL_HALF_M) * CM_PER_M).round() as i32;
    let hi_cm = ((origin.y + VERTICAL_HALF_M) * CM_PER_M).round() as i32;
    let mut boxes = Vec::new();
    for iz in -half_n..=half_n {
        let z = origin.z + iz as f32 * WG3_CELL_M;
        let mut ix = -half_n;
        while ix <= half_n {
            let x = origin.x + ix as f32 * WG3_CELL_M;
            if !cache.has_data(x, z) {
                ix += 1;
                continue;
            }
            let spans = spans_in_window(cache.column_spans(x, z), lo_cm, hi_cm);
            if spans.is_empty() {
                ix += 1;
                continue;
            }
            let mut run_end = ix;
            while run_end < half_n {
                let nx = origin.x + (run_end + 1) as f32 * WG3_CELL_M;
                let next = spans_in_window(cache.column_spans(nx, z), lo_cm, hi_cm);
                if cache.has_data(nx, z) && spans_eq(&next, &spans) {
                    run_end += 1;
                } else {
                    break;
                }
            }
            let center_x = origin.x + ((ix + run_end) as f32 * 0.5) * WG3_CELL_M;
            let half_x = ((run_end - ix + 1) as f32 * WG3_CELL_M) * 0.5;
            for span in &spans {
                // Recortado a la ventana, no solo filtrado por ella: un tramo que solo asoma 12 cm
                // dentro de la ventana (el resto es de otra planta) dibujaría una caja con su
                // altura ENTERA si no se recorta aquí, y eso mentiría sobre qué hay cerca del punto
                // de aparición.
                let bottom_m = (span.bottom_cm as i32).max(lo_cm) as f32 / CM_PER_M;
                let top_m = (span.top_cm as i32).min(hi_cm) as f32 / CM_PER_M;
                boxes.push(GeometryBoxMsg {
                    center: [center_x, (bottom_m + top_m) * 0.5, z],
                    half_size: [half_x, (top_m - bottom_m) * 0.5, WG3_CELL_M * 0.5],
                });
            }
            ix = run_end + 1;
        }
    }
    boxes
}

/// Un segundo punto de pie, lejos del primero, para que el humo tenga a dónde ir y volver. `None`
/// si la región no tiene sitio de pie a esa distancia en ninguna de las direcciones probadas — el
/// llamador se limita entonces a dejar que se asiente sobre el origen, que sigue siendo un
/// comportamiento observable (solo que sin vaivén).
fn find_second_anchor(cache: &Wg3CollisionCache, origin: Vec3, radius_m: f32) -> Option<Vec3> {
    let far = radius_m * 0.85;
    let near = far * 0.7;
    let offsets = [
        (far, 0.0),
        (-far, 0.0),
        (0.0, far),
        (0.0, -far),
        (near, near),
        (-near, near),
        (near, -near),
        (-near, -near),
    ];
    for (dx, dz) in offsets {
        let guess = Vec3::new(origin.x + dx, origin.y, origin.z + dz);
        if let Some(p) = cache.standable_near(guess) {
            if p.distance_xz(origin) > radius_m * 0.3 {
                return Some(p);
            }
        }
    }
    None
}

pub async fn run() {
    let path = manifest_path();
    let Some(manifest) = manifest::load_manifest(&path) else {
        error!(
            "[smiler-sandbox] no se pudo cargar el manifiesto WG3 en {} — nada que mostrar sin \
             geometría real. ¿Existe el fichero exportado por Unity?",
            path.display()
        );
        return;
    };

    let seed: u64 = std::env::var("WORLD_SEED")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(DEFAULT_SANDBOX_SEED);

    let mut regions = Wg3WorldCache::default();
    let mut cache = Wg3CollisionCache::new();
    let guess = Vec3::new(0.0, 2.0, 0.0);
    cache.prewarm_for_move(&mut regions, &manifest, seed, guess, guess);
    let Some(origin) = cache.standable_near(guess) else {
        error!(
            "[smiler-sandbox] sin sitio de pie cerca de {guess:?} con semilla {seed:#x} — no hay \
             dónde sembrar el humo"
        );
        return;
    };

    let boxes = geometry_boxes(&cache, origin, DEBUG_RADIUS_M);
    let anchor_b = find_second_anchor(&cache, origin, DEBUG_RADIUS_M);
    let geometry_json = serde_json::to_string(&GeometryMsg {
        kind: "geometry",
        boxes,
    })
    .expect("GeometryMsg siempre serializa");

    let port: u16 = std::env::var(SANDBOX_PORT_ENV)
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(DEFAULT_PORT);
    let listener = match TcpListener::bind(("127.0.0.1", port)).await {
        Ok(l) => l,
        Err(e) => {
            error!("[smiler-sandbox] no se pudo escuchar en 127.0.0.1:{port}: {e}");
            return;
        }
    };
    info!(
        "[smiler-sandbox] escuchando en 127.0.0.1:{port} — origen {origin:?}, ancla B {anchor_b:?}, \
         semilla {seed:#x}, {} m² de geometría de depuración",
        (2.0 * DEBUG_RADIUS_M) * (2.0 * DEBUG_RADIUS_M)
    );

    let (tx, _) = broadcast::channel::<String>(64);
    let accept_tx = tx.clone();
    tokio::spawn(async move {
        loop {
            let (mut socket, peer) = match listener.accept().await {
                Ok(pair) => pair,
                Err(e) => {
                    error!("[smiler-sandbox] error de accept: {e}");
                    break;
                }
            };
            info!("[smiler-sandbox] cliente conectado: {peer}");
            let mut rx = accept_tx.subscribe();
            let geometry_line = geometry_json.clone();
            tokio::spawn(async move {
                if socket
                    .write_all(format!("{geometry_line}\n").as_bytes())
                    .await
                    .is_err()
                {
                    return;
                }
                loop {
                    match rx.recv().await {
                        Ok(line) => {
                            if socket
                                .write_all(format!("{line}\n").as_bytes())
                                .await
                                .is_err()
                            {
                                break;
                            }
                        }
                        Err(broadcast::error::RecvError::Lagged(_)) => continue,
                        Err(broadcast::error::RecvError::Closed) => break,
                    }
                }
                info!("[smiler-sandbox] cliente desconectado: {peer}");
            });
        }
    });

    let mut flow = SmilerFlow::new();
    flow.seed(&cache, origin, SEED_DENSITY);
    let start = Instant::now();
    let mut ticker = tokio::time::interval(Duration::from_secs_f32(TICK_DT));
    loop {
        ticker.tick().await;
        let elapsed = start.elapsed().as_secs_f32();
        let target = if anchor_b.is_some() {
            let phase = (elapsed / SWAP_PERIOD_S) as u64;
            if phase.is_multiple_of(2) {
                anchor_b
            } else {
                Some(origin)
            }
        } else {
            None
        };
        flow.step(&cache, TICK_DT, target);

        let nodes: Vec<NodeMsg> = flow
            .nodes(MAX_NODES_SENT)
            .into_iter()
            .map(|n| NodeMsg {
                pos: [n.pos.x, n.pos.y, n.pos.z],
                radius: n.radius,
                density: n.density,
            })
            .collect();
        let msg = NodesMsg {
            kind: "nodes",
            t: start.elapsed().as_secs_f64(),
            nodes,
        };
        if let Ok(line) = serde_json::to_string(&msg) {
            // Sin receptores todavía (nadie conectado) devuelve error; es el caso normal al
            // arrancar y no un fallo que registrar en cada tick.
            let _ = tx.send(line);
        }
    }
}
