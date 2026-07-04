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
use crate::utils::{world_to_chunk, Vec3};

use super::World;

/// One loot stack inside a corpse. Mirrors `ItemStackView` (the wire shape) but is
/// the authoritative storage form.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CorpseStack {
    /// Raw STP item id (`DataIdReference` — may be negative). Never the legacy enum.
    pub item_id: i32,
    pub quantity: u16,
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
        items.retain(|s| s.quantity > 0);
        items.truncate(MAX_CORPSE_STACKS);

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
        let taken = CorpseStack {
            item_id: stack.item_id,
            quantity: granted,
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
                    })
                    .collect(),
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
            .map(|&(item_id, quantity)| CorpseStack { item_id, quantity })
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
        assert_eq!(taken, CorpseStack { item_id: -12345, quantity: 2 });
        assert_eq!(world.corpses[&id].items, stacks(&[(-12345, 3)]));

        // Over-ask clamps to what remains; corpse now empty → entry removed.
        let taken = world
            .take_corpse_item(id, 0, 99, pos, CORPSE_LOOT_MAX_DISTANCE)
            .unwrap();
        assert_eq!(taken.quantity, 3);
        assert!(world.corpses.is_empty());
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
        let radius = world.config.unload_radius.max(world.config.ownership_radius);

        // In range: same chunk. Out of range: (radius+2) chunks away on X.
        let near = spawn_test_corpse(&mut world, center, stacks(&[(1, 1)]));
        let far_x = (radius as f32 + 2.0) * chunk_size + 25.0;
        let _far = spawn_test_corpse(
            &mut world,
            Vec3::new(far_x, 1.8, 25.0),
            stacks(&[(2, 1)]),
        );

        let views = world.visible_corpse_views(center);
        assert_eq!(views.len(), 1);
        assert_eq!(views[0].id, near);
        assert_eq!(views[0].owner_name, "Joel");
        assert_eq!(views[0].held_item, -55);
        assert_eq!(views[0].items.len(), 1);
        assert_eq!(views[0].items[0].item_id, 1);
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
            !world
                .chunks
                .keys()
                .any(|k| (k.0, k.2) == corpse_chunk),
            "test premise: the corpse's chunk must actually be unloaded"
        );

        // The corpse persists, is invisible from the origin, and visible on return.
        assert!(world.corpses.contains_key(&id));
        assert!(world.visible_corpse_views(origin).is_empty());
        assert_eq!(world.visible_corpse_views(far_pos).len(), 1);
    }
}
