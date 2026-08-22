//! ADR-028 — lootable player corpses.
//!
//! Corpses live in `World::corpses`, a GLOBAL `HashMap` deliberately OUTSIDE the
//! chunk lifecycle: `update_ownership` unloads chunks past the unload radius
//! (`world.chunks.remove`), and anything stored inside a chunk (like `DroppedItem`)
//! dies with it. A corpse must persist indefinitely until fully looted (validated
//! scope), so it must survive any number of chunk unload/reload cycles around it.
//!
//! The corpse position is the death position frozen server-side by the ADR-025
//! death gate (the player pose does not move again until `respawn_request`), and it
//! NEVER moves afterwards — the client-side ragdoll is cosmetic and per-client.
//!
//! `item_id` here is the raw STP item id (`DataIdReference` hash — may be negative),
//! the same scheme as `Player.equipment`/`held_item` (ADR-022/023). It is NOT the
//! legacy `player::inventory::Item` enum, which is disconnected from the real game.

use serde::{Deserialize, Serialize};

use crate::ipc::{CorpseView, ItemStackView};
use crate::network::PeerId;
use crate::player::session::ItemPropertyValue;
use crate::utils::{world_to_chunk, Vec3};

use super::World;

/// One loot stack inside a corpse. Mirrors `ItemStackView` (the wire shape) but is
/// the authoritative storage form.
///
/// ADR-072: lleva las propiedades de instancia (desgaste, munición cargada...) porque sin ellas
/// morir REPARABA el equipo — se looteaba el propio cadáver y la antorcha volvía a valor de
/// fábrica. Reutiliza `ItemPropertyValue` de ADR-045 en vez de inventar un segundo formato.
///
/// **Las propiedades son POR STACK, no por unidad**, y eso es fidelidad exacta con STP, no una
/// pérdida del wire: un `ItemStack` del vendor es UN `Item` más un contador, funde stacks
/// comparando solo el id sin mirar propiedades, y al partirlos reparte la misma referencia. Ver la
/// enmienda de ADR-072 que cierra esa pregunta con los 12 assets medidos.
///
/// `Eq` cae con este campo: un `f64` no lo admite. Nadie lo usaba como clave de mapa.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CorpseStack {
    /// Raw STP item id (`DataIdReference` — may be negative). Never the legacy enum.
    pub item_id: i32,
    pub quantity: u16,
    /// Vacío en un save anterior a ADR-072 y en cualquier item sin propiedades — que es la
    /// mayoría. `serde(default)` es lo que hace que esos saves carguen sin migración.
    #[serde(default)]
    pub props: Vec<ItemPropertyValue>,
}

impl CorpseStack {
    /// Un stack sin propiedades, que es el caso común. Existe para que los sitios que no tienen
    /// nada que decir sobre propiedades no tengan que escribir `props: Vec::new()` y para que
    /// añadir un campo mañana no vuelva a tocar cincuenta literales.
    pub fn plain(item_id: i32, quantity: u16) -> Self {
        Self {
            item_id,
            quantity,
            props: Vec::new(),
        }
    }
}

/// Authoritative corpse record. Created from the client-reported death-loot
/// snapshot (trust-the-client, same level as position/equipment/held_item);
/// removed when the last stack is looted.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CorpseData {
    pub id: u32,
    pub owner_id: PeerId,
    pub owner_name: String,
    /// Death position, frozen. The loot interaction point — never follows the ragdoll.
    pub position: Vec3,
    /// Worn clothing snapshot [Head, Torso, Legs, Feet] (0 = empty) — dresses the ragdoll.
    pub equipment: [i32; 4],
    /// Held item snapshot (0 = empty hands).
    pub held_item: i32,
    pub items: Vec<CorpseStack>,
    /// ADR-028 amendment (world chests): true for a host-seeded supply chest — same loot
    /// machinery, crate visual instead of a ragdoll, no dead-player owner (`owner_id` = 0,
    /// a reserved value no real peer uses). `serde(default)` → a pre-amendment peer decodes
    /// `false` and renders the chest as a corpse (cosmetic-only degradation, no wire bump).
    #[serde(default)]
    pub is_chest: bool,
}

/// Hygiene cap on reported loot stacks: the STP inventory tops out well below this
/// (≈20–30 slots + 4 equipment + holster), so anything larger is a malformed or
/// malicious report — truncated, never trusted into unbounded server memory.
pub const MAX_CORPSE_STACKS: usize = 64;

/// ADR-028 post-E3: true when a death-loot snapshot has no lootable content (no stacks, or all
/// quantities zero). A corpse born empty would be IMMORTAL — despawn-on-empty only runs after a
/// take, and there is nothing to take — so every spawn entry point (host IPC arm, joiner forward
/// arm, relayed arm) skips spawning when this holds. Found in the E3 play-test data (corpses 6/7,
/// stacks=0, after players died naked).
pub fn corpse_loot_is_empty(items: &[CorpseStack]) -> bool {
    items.iter().all(|s| s.quantity == 0)
}

/// Shared loot-stack hygiene (ADR-028 corpses/chests + ADR-032 amendment `report_inventory`):
/// zero-quantity stacks are dropped, and anything past `MAX_CORPSE_STACKS` is TRUNCATED (the
/// first 64 survive, the rest are discarded — a real STP inventory tops out well below, so an
/// oversized report is malformed or malicious and is never trusted into unbounded server memory).
pub fn sanitize_loot_stacks(items: &mut Vec<CorpseStack>) {
    items.retain(|s| s.quantity > 0);
    items.truncate(MAX_CORPSE_STACKS);
    for stack in items.iter_mut() {
        if stack.props.len() > MAX_PROPS_PER_STACK {
            // A diferencia del recorte de stacks, este SÍ avisa. El tope está por encima de
            // cualquier item real, así que llegar aquí significa una de dos: un reporte
            // malformado/malicioso, o un item nuevo que de verdad necesita más — y ese segundo
            // caso hay que poder verlo, no descubrirlo como "a esta arma se le olvida la mira".
            log::warn!(
                "corpse loot stack item_id={} reportó {} propiedades (tope {}) — recortado",
                stack.item_id,
                stack.props.len(),
                MAX_PROPS_PER_STACK
            );
            stack.props.truncate(MAX_PROPS_PER_STACK);
        }
    }
}

/// ADR-072: tope de propiedades por stack. Higiene contra un reporte del CLIENTE, igual que
/// `MAX_CORPSE_STACKS` y por la misma razón: sin tope, 64 stacks × propiedades sin límite entran
/// en memoria del servidor y en cada `world_state` a 10 Hz.
///
/// **8 y no 4, con el coste medido delante.** Un cadáver saturado de 64 stacks pesa 1836 B sin
/// propiedades; con 1 por stack 3116 B (×1,70), con 3 → 5676 B (×3,09), con 8 → 12076 B (×6,58).
/// Son **20 B por propiedad**, dominados por el nombre de campo repetido en cada entrada — la
/// misma mecánica que descuadró el presupuesto de ADR-068 por 2,6×. Un tope de 4 habría acotado
/// el abuso más, pero de las 12 definiciones con propiedades del proyecto ninguna pasa de 1 hoy y
/// un arma con munición, modo, mira y desgaste llegaría justo a 4: recortar ahí es rozar lo
/// legítimo. Se deja holgura y **el recorte avisa**, que es lo que convierte el tope en una red y
/// no en una pérdida silenciosa.
///
/// El caso REAL no se parece al peor: un cadáver normal trae ~20 stacks y casi ninguno lleva
/// propiedades, así que el sobrecoste típico es de unos pocos cientos de bytes.
pub const MAX_PROPS_PER_STACK: usize = 8;

/// Loot interaction range (meters). Was 5.0 (mirroring `interact_with_item`'s convention) until
/// Fase D play-test: the client-side interaction collider lives on the ragdoll's pelvis bone
/// (tracks the visual body, not the frozen death_pos — see CorpseSpawner.WireLoot), and normal
/// ragdoll settling regularly displaced it past 5m, causing spurious `too_far` rejections against
/// a body the player was plainly standing next to. Widened to give normal settling headroom;
/// paired with the ragdoll settle-freeze (CorpseSpawner) that caps how far it can drift in the
/// first place — the combination bounds the pathological case (slopes/stairs) without needing an
/// unbounded radius.
pub const CORPSE_LOOT_MAX_DISTANCE: f32 = 8.0;

impl World {
    /// Create a corpse at the (frozen) death position from the client-reported
    /// snapshot. Returns the new corpse id. Stacks beyond `MAX_CORPSE_STACKS` and
    /// zero-quantity stacks are dropped (hygiene, not trust).
    pub fn spawn_corpse(
        &mut self,
        owner_id: PeerId,
        owner_name: String,
        position: Vec3,
        equipment: [i32; 4],
        held_item: i32,
        mut items: Vec<CorpseStack>,
    ) -> u32 {
        sanitize_loot_stacks(&mut items);

        let id = self.next_corpse_id;
        self.next_corpse_id = self.next_corpse_id.wrapping_add(1);
        self.corpses.insert(
            id,
            CorpseData {
                id,
                owner_id,
                owner_name,
                position,
                equipment,
                held_item,
                items,
                is_chest: false,
            },
        );
        self.revision = self.revision.wrapping_add(1);
        id
    }

    /// ADR-028 amendment (world chests): create a host-seeded supply chest. Same storage,
    /// relay, loot, and despawn-on-empty machinery as a corpse — only the flag (and the
    /// crate visual it selects client-side) differs. No dead-player snapshot: owner_id 0
    /// (reserved), empty equipment/held. Same hygiene as `spawn_corpse`.
    pub fn spawn_chest(&mut self, position: Vec3, mut items: Vec<CorpseStack>) -> u32 {
        sanitize_loot_stacks(&mut items);

        let id = self.next_corpse_id;
        self.next_corpse_id = self.next_corpse_id.wrapping_add(1);
        self.corpses.insert(
            id,
            CorpseData {
                id,
                owner_id: 0,
                owner_name: "Supply Crate".into(),
                position,
                equipment: [0; 4],
                held_item: 0,
                items,
                is_chest: true,
            },
        );
        self.revision = self.revision.wrapping_add(1);
        id
    }

    /// Take up to `quantity` from the stack at `item_index` of corpse `corpse_id`.
    /// Returns the stack actually granted. When the last stack leaves, the corpse
    /// entry is removed — it despawns by absence in the next WorldState (the same
    /// self-healing pattern world items use). No reservation/timeout is needed:
    /// the single-threaded game loop already serializes competing requests.
    pub fn take_corpse_item(
        &mut self,
        corpse_id: u32,
        item_index: usize,
        quantity: u16,
        requester_pos: Vec3,
        max_distance: f32,
    ) -> Result<CorpseStack, String> {
        let corpse = self
            .corpses
            .get_mut(&corpse_id)
            .ok_or_else(|| "missing_corpse".to_string())?;

        let distance = requester_pos.distance(corpse.position);
        if distance > max_distance {
            return Err(format!("too_far distance={distance:.2}"));
        }

        if item_index >= corpse.items.len() {
            return Err("bad_index".into());
        }
        if quantity == 0 {
            return Err("zero_quantity".into());
        }

        let stack = &mut corpse.items[item_index];
        let granted = quantity.min(stack.quantity);
        // ADR-072: lo que sale lleva las propiedades del stack, y en una toma PARCIAL las dos
        // mitades se quedan con las mismas. No es un descuido ni una duplicación inventada aquí:
        // es lo que hace STP al partir un `ItemStack`, que reparte la MISMA referencia de `Item` a
        // las dos partes. Un modelo por unidad daría una fidelidad que el juego no tiene en ningún
        // otro sitio y que el inventario del cliente no sabría ni guardar.
        let taken = CorpseStack {
            item_id: stack.item_id,
            quantity: granted,
            props: stack.props.clone(),
        };
        stack.quantity -= granted;
        if stack.quantity == 0 {
            corpse.items.remove(item_index);
        }
        if corpse.items.is_empty() {
            self.corpses.remove(&corpse_id);
        }
        self.revision = self.revision.wrapping_add(1);
        Ok(taken)
    }

    /// Build the corpse views for the WorldState IPC message, filtered by the same
    /// bandwidth criterion as chunks: only corpses whose chunk is within the unload
    /// radius (Chebyshev) of the player's chunk are serialized. The `corpses` map
    /// itself is NEVER pruned by this — persistence is indefinite.
    pub fn visible_corpse_views(&self, center: Vec3) -> Vec<CorpseView> {
        let center_chunk = world_to_chunk(center);
        let radius = self.config.unload_radius.max(self.config.ownership_radius);
        let mut views: Vec<CorpseView> = self
            .corpses
            .values()
            .filter(|c| {
                let chunk = world_to_chunk(c.position);
                (chunk.0 - center_chunk.0)
                    .abs()
                    .max((chunk.1 - center_chunk.1).abs())
                    <= radius
            })
            .map(|c| CorpseView {
                id: c.id,
                owner_id: c.owner_id as u32,
                owner_name: c.owner_name.clone(),
                position: c.position.to_array(),
                equipment: c.equipment,
                held_item: c.held_item,
                items: c
                    .items
                    .iter()
                    .map(|s| ItemStackView {
                        item_id: s.item_id,
                        quantity: s.quantity,
                        props: s.props.clone(),
                    })
                    .collect(),
                is_chest: c.is_chest,
            })
            .collect();
        // HashMap iteration order is nondeterministic — sort for stable serialization
        // (same rationale as visible_chunk_views).
        views.sort_unstable_by_key(|v| v.id);
        views
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn stacks(pairs: &[(i32, u16)]) -> Vec<CorpseStack> {
        pairs
            .iter()
            .map(|&(item_id, quantity)| CorpseStack {
                item_id,
                quantity,
                props: Vec::new(),
            })
            .collect()
    }

    fn spawn_test_corpse(world: &mut World, pos: Vec3, items: Vec<CorpseStack>) -> u32 {
        world.spawn_corpse(7, "Joel".into(), pos, [101, 202, 303, 404], -55, items)
    }

    #[test]
    fn spawn_corpse_stores_snapshot_and_increments_ids() {
        let mut world = World::new(42);
        let pos = Vec3::new(10.0, 1.8, 20.0);
        let a = spawn_test_corpse(&mut world, pos, stacks(&[(-12345, 3), (99, 1)]));
        let b = spawn_test_corpse(&mut world, pos, stacks(&[(1, 1)]));
        assert_ne!(a, b);

        let corpse = &world.corpses[&a];
        assert_eq!(corpse.owner_id, 7);
        assert_eq!(corpse.owner_name, "Joel");
        assert_eq!(corpse.equipment, [101, 202, 303, 404]);
        assert_eq!(corpse.held_item, -55);
        assert_eq!(corpse.items, stacks(&[(-12345, 3), (99, 1)]));
    }

    #[test]
    fn spawn_corpse_drops_zero_stacks_and_caps_length() {
        let mut world = World::new(42);
        let mut items = stacks(&[(1, 0)]); // zero-quantity → dropped
        for i in 0..(MAX_CORPSE_STACKS as i32 + 20) {
            items.push(CorpseStack {
                item_id: i,
                quantity: 1,
                props: Vec::new(),
            });
        }
        let id = spawn_test_corpse(&mut world, Vec3::new(0.0, 0.0, 0.0), items);
        let corpse = &world.corpses[&id];
        assert_eq!(corpse.items.len(), MAX_CORPSE_STACKS);
        assert!(corpse.items.iter().all(|s| s.quantity > 0));
    }

    #[test]
    fn take_corpse_item_partial_then_deplete_removes_corpse() {
        let mut world = World::new(42);
        let pos = Vec3::new(10.0, 1.8, 20.0);
        let id = spawn_test_corpse(&mut world, pos, stacks(&[(-12345, 5)]));

        // Partial take: 2 of 5.
        let taken = world
            .take_corpse_item(id, 0, 2, pos, CORPSE_LOOT_MAX_DISTANCE)
            .unwrap();
        assert_eq!(
            taken,
            CorpseStack {
                item_id: -12345,
                quantity: 2,
                props: Vec::new(),
            }
        );
        assert_eq!(world.corpses[&id].items, stacks(&[(-12345, 3)]));

        // Over-ask clamps to what remains; corpse now empty → entry removed.
        let taken = world
            .take_corpse_item(id, 0, 99, pos, CORPSE_LOOT_MAX_DISTANCE)
            .unwrap();
        assert_eq!(taken.quantity, 3);
        assert!(world.corpses.is_empty());
    }

    /// El contrato que el cliente TIENE que respetar, fijado aquí porque es aquí donde se rompe.
    ///
    /// `take_corpse_item` hace `Vec::remove` en cuanto agota un stack, así que cada índice mayor
    /// baja una posición EN ESE INSTANTE. Una ráfaga de peticiones calculadas todas de golpe
    /// contra el estado inicial —que es lo que hacía "Take All", un `OnSlotChanged` por slot en el
    /// mismo frame— no vacía el cadáver: apunta a otras entradas y deja alguna sin pedir jamás.
    /// El cadáver sobrevive, y como el cliente despawnea por ausencia en el roster, se queda ahí
    /// para siempre aunque en pantalla se vea vacío. Las cajas comparten este camino entero.
    #[test]
    fn a_burst_of_takes_with_stale_indices_cannot_empty_a_corpse() {
        let mut world = World::new(42);
        let pos = Vec3::new(10.0, 1.8, 20.0);
        let id = spawn_test_corpse(
            &mut world,
            pos,
            stacks(&[(-1, 1), (-2, 1), (-3, 1), (-4, 1)]),
        );

        // Los cuatro índices resueltos ANTES de que se aplique ninguno, que es justo lo que hacía
        // el cliente al no serializar.
        for stale_index in 0..4 {
            let _ = world.take_corpse_item(id, stale_index, 1, pos, CORPSE_LOOT_MAX_DISTANCE);
        }

        // La traza real, medida por este test y no supuesta:
        //   take(0) → saca -1;              quedan [-2, -3, -4]
        //   take(1) → índice 1 es -3 ahora; quedan [-2, -4]      ← se saltó -2
        //   take(2) → len 2 → bad_index (la lista ya encogió por debajo del índice)
        //   take(3) → bad_index
        let survivor = world
            .corpses
            .get(&id)
            .expect("el cadáver sobrevive a la ráfaga: ESE es el bug");
        assert_eq!(
            survivor.items,
            stacks(&[(-2, 1), (-4, 1)]),
            "quedan las entradas que el desplazamiento se saltó y las que ya no se pudieron pedir"
        );
    }

    /// La otra mitad: resolviendo el índice en el momento de CADA petición —una en vuelo cada vez,
    /// que es lo que hace el cliente desde el arreglo— las mismas cuatro tomas sí vacían el
    /// cadáver y lo hacen desaparecer.
    #[test]
    fn takes_resolved_one_at_a_time_do_empty_the_corpse() {
        let mut world = World::new(42);
        let pos = Vec3::new(10.0, 1.8, 20.0);
        let id = spawn_test_corpse(
            &mut world,
            pos,
            stacks(&[(-1, 1), (-2, 1), (-3, 1), (-4, 1)]),
        );

        for _ in 0..4 {
            // Con el shift ya aplicado, el primer slot vivo es siempre el 0.
            world
                .take_corpse_item(id, 0, 1, pos, CORPSE_LOOT_MAX_DISTANCE)
                .expect("cada toma serializada apunta a una entrada real");
        }

        assert!(
            world.corpses.is_empty(),
            "vaciado de verdad → despawnea por ausencia en el roster"
        );
    }

    /// ADR-072: lo que sale del cadáver lleva el desgaste del stack del que salió. Sin esto,
    /// lootear el propio cuerpo REPARABA el equipo — el bug que abrió el ADR.
    #[test]
    fn a_taken_stack_carries_the_wear_of_the_stack_it_came_from() {
        let mut world = World::new(42);
        let pos = Vec3::new(10.0, 1.8, 20.0);
        let worn = vec![CorpseStack {
            item_id: -8792658,
            quantity: 3,
            props: vec![ItemPropertyValue {
                id: -8792658,
                value: 0.4237,
            }],
        }];
        let id = spawn_test_corpse(&mut world, pos, worn);

        // Toma PARCIAL: 1 de 3.
        let taken = world
            .take_corpse_item(id, 0, 1, pos, CORPSE_LOOT_MAX_DISTANCE)
            .unwrap();
        assert_eq!(taken.props.len(), 1, "lo que sale lleva su desgaste");
        assert!((taken.props[0].value - 0.4237).abs() < 1e-9);

        // Y lo que queda conserva el mismo, que es lo que hace STP al partir un ItemStack.
        let left = &world.corpses[&id].items[0];
        assert_eq!(left.quantity, 2);
        assert_eq!(
            left.props, taken.props,
            "las dos mitades comparten propiedades, igual que dentro del inventario"
        );
    }

    /// El tope de `MAX_PROPS_PER_STACK` es higiene contra un reporte del cliente, igual que el de
    /// stacks: sin él, 64 stacks × propiedades sin límite entran en memoria del servidor.
    #[test]
    fn sanitize_truncates_the_property_list_of_each_stack() {
        let mut items = vec![CorpseStack {
            item_id: 1,
            quantity: 1,
            props: (0..MAX_PROPS_PER_STACK as i32 + 5)
                .map(|i| ItemPropertyValue {
                    id: i,
                    value: i as f64,
                })
                .collect(),
        }];

        sanitize_loot_stacks(&mut items);

        assert_eq!(items[0].props.len(), MAX_PROPS_PER_STACK);
        assert_eq!(items[0].props[0].id, 0, "se quedan las primeras, no otras");
    }

    /// ADR-072 exige MEDIR el peor caso del wire, no estimarlo: el presupuesto de ADR-068 se
    /// equivocó por 2,6× y lo cazó un test, no el diseño. Esto codifica con el MISMO encoder que
    /// usa el IPC (`to_vec_named`) un cadáver saturado —64 stacks, cada uno con el tope de 8
    /// propiedades— y compara contra el mismo cadáver sin propiedades.
    ///
    /// `#[ignore]`: es una medición, no una aserción de comportamiento. Correr con
    /// `cargo test -- --ignored corpse_view_wire_cost --nocapture`.
    #[test]
    #[ignore = "sonda: imprime, no afirma; correr con -- --ignored --nocapture"]
    fn corpse_view_wire_cost() {
        let mut world = World::new(42);
        let pos = Vec3::new(10.0, 1.8, 20.0);

        let plain: Vec<CorpseStack> = (0..MAX_CORPSE_STACKS as i32)
            .map(|i| CorpseStack::plain(i, 1))
            .collect();
        let id_plain = spawn_test_corpse(&mut world, pos, plain);
        let bytes_plain = rmp_serde::to_vec_named(&world.visible_corpse_views(pos))
            .unwrap()
            .len();
        world.corpses.remove(&id_plain);

        println!("ADR-072 wire, cadáver de {MAX_CORPSE_STACKS} stacks:");
        println!("  sin props            = {bytes_plain} B  (línea base)");

        // Varias densidades, porque el peor caso teórico y el real no se parecen: de las 12
        // definiciones con propiedades del proyecto, 10 tienen UNA y ninguna llega a 3.
        for n in [1usize, 3, MAX_PROPS_PER_STACK] {
            let worn: Vec<CorpseStack> = (0..MAX_CORPSE_STACKS as i32)
                .map(|i| CorpseStack {
                    item_id: i,
                    quantity: 1,
                    props: (0..n as i32)
                        .map(|p| ItemPropertyValue {
                            id: p,
                            value: 0.4237,
                        })
                        .collect(),
                })
                .collect();
            let id = spawn_test_corpse(&mut world, pos, worn);
            let bytes = rmp_serde::to_vec_named(&world.visible_corpse_views(pos))
                .unwrap()
                .len();
            world.corpses.remove(&id);
            println!(
                "  {n} props por stack    = {bytes} B  (+{} B, ×{:.2})",
                bytes as i64 - bytes_plain as i64,
                bytes as f64 / bytes_plain as f64
            );
        }
    }

    #[test]
    fn take_corpse_item_rejects_far_missing_and_bad_index() {
        let mut world = World::new(42);
        let pos = Vec3::new(10.0, 1.8, 20.0);
        let id = spawn_test_corpse(&mut world, pos, stacks(&[(1, 1)]));

        let far = Vec3::new(9999.0, 0.0, 9999.0);
        assert!(world.take_corpse_item(id, 0, 1, far, 5.0).is_err());
        assert!(world.take_corpse_item(id, 5, 1, pos, 5.0).is_err());
        assert!(world.take_corpse_item(id, 0, 0, pos, 5.0).is_err());
        assert!(world.take_corpse_item(9999, 0, 1, pos, 5.0).is_err());
        // Nothing was consumed by the rejections.
        assert_eq!(world.corpses[&id].items, stacks(&[(1, 1)]));
    }

    #[test]
    fn visible_corpse_views_filters_by_chunk_radius_and_sorts() {
        let mut world = World::new(42);
        let center = Vec3::new(25.0, 1.8, 25.0); // chunk (0,0)
        let chunk_size = world.config.chunk_size;
        let radius = world
            .config
            .unload_radius
            .max(world.config.ownership_radius);

        // In range: same chunk. Out of range: (radius+2) chunks away on X.
        let near = spawn_test_corpse(&mut world, center, stacks(&[(1, 1)]));
        let far_x = (radius as f32 + 2.0) * chunk_size + 25.0;
        let _far = spawn_test_corpse(&mut world, Vec3::new(far_x, 1.8, 25.0), stacks(&[(2, 1)]));

        let views = world.visible_corpse_views(center);
        assert_eq!(views.len(), 1);
        assert_eq!(views[0].id, near);
        assert_eq!(views[0].owner_name, "Joel");
        assert_eq!(views[0].held_item, -55);
        assert_eq!(views[0].items.len(), 1);
        assert_eq!(views[0].items[0].item_id, 1);
    }

    // ADR-028 amendment (world chests): a chest is a corpse-entry with is_chest=true and no
    // dead-player snapshot; looting it goes through the EXACT same take path, including
    // despawn-on-empty.
    #[test]
    fn spawn_chest_stores_flagged_entry_and_loots_like_a_corpse() {
        let mut world = World::new(42);
        let pos = Vec3::new(10.0, 1.8, 20.0);
        let id = world.spawn_chest(pos, stacks(&[(-5498592, 3), (9692212, 1)]));

        let chest = &world.corpses[&id];
        assert!(chest.is_chest);
        assert_eq!(chest.owner_id, 0);
        assert_eq!(chest.owner_name, "Supply Crate");
        assert_eq!(chest.equipment, [0; 4]);
        assert_eq!(chest.held_item, 0);

        // Corpses and chests share the id space and the view pipeline.
        let corpse_id = spawn_test_corpse(&mut world, pos, stacks(&[(1, 1)]));
        assert_ne!(id, corpse_id);
        let views = world.visible_corpse_views(pos);
        assert_eq!(views.len(), 2);
        assert!(views.iter().any(|v| v.id == id && v.is_chest));
        assert!(views.iter().any(|v| v.id == corpse_id && !v.is_chest));

        // Same take path; last stack out removes the chest (despawn-on-empty).
        let taken = world
            .take_corpse_item(id, 0, 99, pos, CORPSE_LOOT_MAX_DISTANCE)
            .unwrap();
        assert_eq!(
            taken,
            CorpseStack {
                item_id: -5498592,
                quantity: 3,
                props: Vec::new(),
            }
        );
        let taken = world
            .take_corpse_item(id, 0, 1, pos, CORPSE_LOOT_MAX_DISTANCE)
            .unwrap();
        assert_eq!(
            taken,
            CorpseStack {
                item_id: 9692212,
                quantity: 1,
                props: Vec::new(),
            }
        );
        assert!(!world.corpses.contains_key(&id));
        assert!(
            world.corpses.contains_key(&corpse_id),
            "the real corpse must be untouched"
        );
    }

    // ADR-032 amendment: the shared hygiene helper — quantity<=0 dropped FIRST, then the
    // survivors truncated to MAX_CORPSE_STACKS (first 64 kept, 65+ discarded).
    #[test]
    fn sanitize_loot_stacks_drops_zeroes_then_truncates_to_cap() {
        let mut items = vec![CorpseStack {
            item_id: -1,
            quantity: 0,
            props: Vec::new(),
        }];
        for i in 0..(MAX_CORPSE_STACKS as i32 + 6) {
            items.push(CorpseStack {
                item_id: i,
                quantity: 1,
                props: Vec::new(),
            });
        }
        sanitize_loot_stacks(&mut items);
        assert_eq!(items.len(), MAX_CORPSE_STACKS);
        assert!(items.iter().all(|s| s.quantity > 0));
        assert_eq!(
            items[0].item_id, 0,
            "zero-qty stack must not consume a cap slot"
        );
        assert_eq!(items.last().unwrap().item_id, MAX_CORPSE_STACKS as i32 - 1);
    }

    // ADR-028 post-E3: the empty-snapshot rule that prevents immortal empty corpses.
    #[test]
    fn corpse_loot_is_empty_detects_no_lootable_content() {
        assert!(corpse_loot_is_empty(&[]));
        assert!(corpse_loot_is_empty(&stacks(&[(1, 0), (2, 0)])));
        assert!(!corpse_loot_is_empty(&stacks(&[(1, 0), (2, 1)])));
        assert!(!corpse_loot_is_empty(&stacks(&[(-12345, 3)])));
    }

    #[test]
    fn corpses_survive_chunk_unload_cycles() {
        // THE ADR-028 invariant: corpses live outside the chunk lifecycle. Park a
        // corpse far away, let update_ownership unload its chunk, and it must still
        // exist — and become visible again when the player returns.
        let mut world = World::new(42);
        let chunk_size = world.config.chunk_size;
        let far_pos = Vec3::new(40.0 * chunk_size, 1.8, 40.0 * chunk_size);
        let id = spawn_test_corpse(&mut world, far_pos, stacks(&[(1, 1)]));

        // Load around the corpse, then walk the player back to the origin so the
        // corpse's chunk is unloaded.
        world.update_ownership(far_pos, 1);
        let origin = Vec3::new(25.0, 1.8, 25.0);
        world.update_ownership(origin, 1);
        let corpse_chunk = crate::utils::world_to_chunk(far_pos);
        assert!(
            !world.chunks.keys().any(|k| (k.0, k.2) == corpse_chunk),
            "test premise: the corpse's chunk must actually be unloaded"
        );

        // The corpse persists, is invisible from the origin, and visible on return.
        assert!(world.corpses.contains_key(&id));
        assert!(world.visible_corpse_views(origin).is_empty());
        assert_eq!(world.visible_corpse_views(far_pos).len(), 1);
    }
}
