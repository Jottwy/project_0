//! ADR-131 — LOS VIGILANTES: la tercera especie de faceling, `species = 3`.
//!
//! Es la especie que se define por lo que NO hace (ADR-131 D1): no anda, no ataca, no es manada y
//! no vocaliza. Por eso este módulo **no tiene `step`**: un vigilante se da de alta una vez, con su
//! sitio y su giro ya decididos por la silla en la que nace, y nadie le vuelve a escribir la
//! posición. Todo lo que hay aquí es el reconcile de población — despertar y retirar—, que es la
//! única parte de un driver que un vigilante necesita.
//!
//! **El sitio sale del MUNDO, no de un sorteo de posición** (ADR-131 D2). Las otras dos especies
//! sortean coordenadas de un chunk (`world::faceling_spawn`) y luego buscan suelo donde caen; aquí
//! las anclas `PROP_CHAIR` que `fill::office_cubicles` ya emite (ADR-129) son la lista de sitios, y
//! traen posición, cota de suelo y giro. Un vigilante no necesita ráster, ni `resolve_spawn_near`,
//! ni `standable_near`: una silla la puso el mismo `fill` que le dejó el hueco libre, así que está
//! en suelo pisable por construcción.

use super::*;

use crate::world::wg3::chunk::{Wg3ChunkCoord, WG3_CHUNK_M};
use crate::world::wg3::segment::PROP_CHAIR;

use rand::rngs::StdRng;
use rand::{Rng, SeedableRng};

/// Misma cadencia que el resto de las poblaciones: una vez por segundo.
const WATCHER_POPULATION_SYNC_INTERVAL: f32 = 1.0;

/// ADR-131 D3 — radios. Más cortos que los del adulto (70/100) a propósito: un vigilante no tiene
/// que estar puesto antes de que dobles la esquina porque no tiene que llegar a ningún sitio.
const WATCHER_ACTIVATE_RADIUS: f32 = 60.0;
/// Histéresis por encima del de activación, igual que en todas las especies.
const WATCHER_DEACTIVATE_RADIUS: f32 = 90.0;
/// Suelo además de techo: nadie aparece en la cara de nadie.
const WATCHER_MIN_SPAWN_DISTANCE: f32 = 8.0;
/// ADR-131 D3 — más alto que los 32 del adulto porque un vigilante cuesta un peer y una entrada en
/// un `Vec`: ni ráster, ni A\*, ni paso.
const WATCHER_ACTIVE_CAP: usize = 48;

/// ADR-131 D3, **recalibrado por la enmienda 2**: probabilidad de que una silla de la CALLE tenga a
/// alguien sentado.
///
/// Los primeros números (0,06 + 0,06 por sótano) salieron de suponer que un puesto de oficina es
/// abundante. **No lo es**: la región (0,0) entera tiene 60 sillas repartidas en cinco plantas —14
/// en la calle—, así que el 6 % dejaba la planta baja con MEDIA persona sentada y toda la población
/// de la región en tres. Joel, jugando: «no los veo».
const WATCHER_CHAIR_BASE: f32 = 0.25;
/// Cuánto sube por cada planta bajo rasante. Doce puntos y no seis: con el tope en 0,70 el fondo
/// sigue siendo casi el doble que la calle, que es lo que hace que bajar signifique algo.
const WATCHER_CHAIR_PER_STOREY: f32 = 0.12;
/// Tope. Con los 30 sótanos de ADR-130 D3 se alcanza en B4, y a partir de ahí siete de cada diez
/// puestos están ocupados — que es tanto como se puede llenar sin que la sala deje de leerse.
const WATCHER_CHAIR_MAX: f32 = 0.70;

/// La sal que separa este sorteo de todos los demás consumidores de `chunk_seed_layer` — mismo
/// truco y mismo motivo que `FACELING_ADULT_DRAW_SALT`.
const WATCHER_DRAW_SALT: u64 = 0x5EA7_ED17_0131_0000;

/// ADR-131 D4 — el bit 5 de `buttons`, el primero libre tras el spray de ADR-068. Un estado
/// SOSTENIDO en un campo que ya viaja: sin campo nuevo y sin bump de esquema. La misma asignación
/// vive en `Assets/Scripts/Network/RemoteButtons.cs`, que es donde el cliente la lee.
pub(super) const BUTTON_SEATED_BIT: u16 = 1 << 5;

/// ADR-131 D1 — la etiqueta cosmética de especie (0 humano, 1 adulto, 2 niño, 3 vigilante).
pub(super) const WATCHER_SPECIES: u8 = 3;

/// Un vigilante activo. No hay estado que ticar: la posición es la de su silla y no cambia nunca,
/// y se guarda aquí para que la retirada no tenga que ir a buscarla al peer.
pub(super) struct Watcher {
    pub(super) id: PeerId,
    pub(super) at: Vec3,
}

/// Una silla candidata, con lo que hace falta para ordenar por cercanía antes de gastar el cap.
struct SeatCandidate {
    distance: f32,
    at: Vec3,
    yaw_deg: f32,
}

pub(super) struct WatcherDriver {
    pub(super) watchers: Vec<Watcher>,
    population_sync_in: f32,
}

impl WatcherDriver {
    pub(super) fn new() -> Self {
        Self {
            watchers: Vec::new(),
            population_sync_in: 0.0, // reconcile en el primer tick de entidades
        }
    }

    /// ¿Se sienta alguien en esta silla?
    ///
    /// Determinista por `(semilla, chunk, planta, índice de la silla dentro del chunk)`. El índice
    /// es el del recorrido de `props_owned_by_chunk`, que conserva el orden de emisión de `fill` y
    /// por tanto es estable para una semilla dada — la misma disciplina que
    /// `faceling_spawn::wg3_keeps_position`, que también numera huecos de una lista.
    ///
    /// La PROFUNDIDAD sale de la cota de la silla y no de la planta del jugador: dos sillas de la
    /// misma sala están en la misma planta, y una silla no cambia de densidad porque quien la mire
    /// venga de arriba.
    pub(super) fn seat_is_taken(
        world_seed: u64,
        coord: Wg3ChunkCoord,
        storey: i32,
        index: usize,
    ) -> bool {
        let depth = (-storey).max(0) as f32;
        let chance = (WATCHER_CHAIR_BASE + WATCHER_CHAIR_PER_STOREY * depth).min(WATCHER_CHAIR_MAX);
        let mut rng = StdRng::seed_from_u64(
            crate::world::architecture::chunk_generator::chunk_seed_layer(
                world_seed ^ WATCHER_DRAW_SALT,
                (coord.x, coord.z),
                storey.clamp(i8::MIN as i32, i8::MAX as i32) as crate::world::chunk::ChunkLayer,
            )
            .wrapping_add(index as u64),
        );
        rng.gen::<f32>() < chance
    }

    /// El reconcile. Sin WG3 no hay anclas de atrezo que leer, así que no hay vigilantes: es la
    /// misma degradación que el resto de ADR-129 y no un caso que haya que emular con WG2.
    pub(super) fn sync_population(
        &mut self,
        net: &mut NetworkManager,
        host_player_pos: Vec3,
        dt: f32,
        wg3: Option<super::faceling::Wg3SpawnCtx<'_>>,
    ) {
        self.population_sync_in -= dt;
        if self.population_sync_in > 0.0 {
            return;
        }
        self.population_sync_in = WATCHER_POPULATION_SYNC_INTERVAL;

        let Some(ctx) = wg3 else {
            return;
        };

        let players: Vec<Vec3> = std::iter::once(host_player_pos)
            .chain(
                net.peers
                    .iter()
                    .filter(|(id, p)| {
                        !net.is_phantom(**id) && !net.is_faceling(**id) && !p.relay_only
                    })
                    .map(|(_, p)| Vec3::from_array(p.position)),
            )
            .collect();

        // ── Retirar a los que ya no tiene nadie cerca ──
        //
        // La cota se mide con `same_level` (ADR-108) y no con una capa: un vigilante de la planta de
        // abajo está a 3,32 m del de arriba en Y y a cero en XZ, así que sin esto una escalera
        // retiraría media torre.
        let mut retired: Vec<PeerId> = Vec::new();
        for w in &self.watchers {
            let far = players.iter().all(|p| {
                !super::faceling::same_level(true, 0, w.at.y, p.y)
                    || p.distance_xz(w.at) > WATCHER_DEACTIVATE_RADIUS
            });
            if far {
                retired.push(w.id);
            }
        }
        for id in &retired {
            net.despawn_faceling(*id);
        }
        self.watchers.retain(|w| !retired.contains(&w.id));

        if self.watchers.len() >= WATCHER_ACTIVE_CAP {
            return;
        }

        // ── Despertar los que alguien tiene cerca ──
        let taken: Vec<Vec3> = self.watchers.iter().map(|w| w.at).collect();
        let mut seen: HashSet<(i32, i32)> = HashSet::new();
        let mut candidates: Vec<SeatCandidate> = Vec::new();

        for p in &players {
            let cx0 = ((p.x - WATCHER_ACTIVATE_RADIUS) / WG3_CHUNK_M).floor() as i32;
            let cx1 = ((p.x + WATCHER_ACTIVATE_RADIUS) / WG3_CHUNK_M).floor() as i32;
            let cz0 = ((p.z - WATCHER_ACTIVATE_RADIUS) / WG3_CHUNK_M).floor() as i32;
            let cz1 = ((p.z + WATCHER_ACTIVATE_RADIUS) / WG3_CHUNK_M).floor() as i32;
            for cx in cx0..=cx1 {
                for cz in cz0..=cz1 {
                    if !seen.insert((cx, cz)) {
                        continue;
                    }
                    let coord = Wg3ChunkCoord { x: cx, z: cz };
                    let region = ctx.worlds.region_for(ctx.manifest, ctx.world_seed, coord);
                    let props = region.props_owned_by_chunk(coord);
                    // El índice del sorteo es el de la lista ENTERA del chunk y no el de las sillas
                    // filtradas: así añadir un tipo de atrezo nuevo no reubica a los vigilantes que
                    // ya estaban, que es la misma garantía que el resto de los sorteos de este
                    // proyecto se toma la molestia de dar.
                    for (index, prop) in props.iter().enumerate() {
                        // Sólo sillas EN PIE. En una volcada no se sienta nadie (ADR-131 D2).
                        if prop.kind != PROP_CHAIR {
                            continue;
                        }
                        let storey = crate::world::wg3::plan::storey_of_floor_cm(prop.y_cm);
                        if !Self::seat_is_taken(ctx.world_seed, coord, storey, index) {
                            continue;
                        }
                        let at = Vec3::new(
                            prop.x_cm as f32 / 100.0,
                            prop.y_cm as f32 / 100.0 + crate::world::collision::PLAYER_BASE_Y,
                            prop.z_cm as f32 / 100.0,
                        );
                        if taken.iter().any(|t| t.distance(at) < 0.01) {
                            continue;
                        }
                        if candidates.iter().any(|c| c.at.distance(at) < 0.01) {
                            continue;
                        }
                        // **EL DESPERTAR MIDE LA COTA CON EL MISMO PREDICADO QUE LA RETIRADA.**
                        //
                        // Sin este `filter` la distancia salía del jugador más cercano en XZ *sea
                        // cual sea su planta*, así que una silla del piso de arriba —a cero metros
                        // en planta y a 3,32 en Y— nacía y la retirada la mataba al segundo
                        // siguiente, para volver a nacer al otro. Cazado por
                        // `a_watcher_never_moves_and_never_attacks`, que vio a un vigilante entrar y
                        // salir de la lista con la posición intacta. Es el mismo parpadeo que la
                        // auditoría de ADR-120 tuvo que quitarle al adulto: dos predicados distintos
                        // para «está cerca» en las dos puntas del reconcile.
                        let distance = players
                            .iter()
                            .filter(|q| super::faceling::same_level(true, 0, at.y, q.y))
                            .map(|q| q.distance_xz(at))
                            .fold(f32::INFINITY, f32::min);
                        if !(WATCHER_MIN_SPAWN_DISTANCE..=WATCHER_ACTIVATE_RADIUS)
                            .contains(&distance)
                        {
                            continue;
                        }
                        candidates.push(SeatCandidate {
                            distance,
                            at,
                            yaw_deg: prop.yaw_deg as f32,
                        });
                    }
                }
            }
        }

        // El cap se gasta por CERCANÍA y no por orden de bucle — la disciplina que ADR-110 D3/T5
        // tuvo que rescatar en el adulto, donde el tope recortaba «lo último del recorrido» y se
        // leía como un lado de la sala poblado y el otro vacío. `total_cmp` para que un NaN no
        // entre en pánico, y `sort_by` estable para que los empates conserven el orden determinista.
        candidates.sort_by(|a, b| a.distance.total_cmp(&b.distance));

        for cand in candidates {
            if self.watchers.len() >= WATCHER_ACTIVE_CAP {
                break;
            }
            // `insert_faceling_peer` y NO `spawn_faceling`: éste último busca sitio de pie con
            // `standable_near_bounded`, que movería al vigilante fuera de su silla. Aquí el sitio ya
            // está decidido por el mundo (ADR-131 D2) y lo único que falta es el alta.
            let id = net.insert_faceling_peer("Watcher", cand.at.to_array(), WATCHER_SPECIES);
            if let Some(peer) = net.peers.get_mut(&id) {
                // El cuerpo mira a donde mira el asiento, y no se gira jamás (D2). La cabeza sí
                // sigue al jugador, pero eso es cliente puro (D6) y no viaja.
                peer.rotation = cand.yaw_deg;
                // ADR-131 D4 — el estado «sentado», escrito una vez y nunca más.
                peer.buttons |= BUTTON_SEATED_BIT;
            }
            self.watchers.push(Watcher { id, at: cand.at });
            info!(
                "MPTRACE step=FL_POP event=watcher_spawned faceling_id={} pos=({:.2},{:.2},{:.2}) yaw={:.0}",
                id, cand.at.x, cand.at.y, cand.at.z, cand.yaw_deg
            );
        }
    }
}
