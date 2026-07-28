use std::collections::HashSet;

use log::info;
use rand::rngs::StdRng;
use rand::{Rng, SeedableRng};

use crate::utils::{ChunkPos, CHUNK_SIZE};
use crate::world::chunk::ChunkLayer;
// MIG-2: template ids come from the architecture facade; generation helpers stay
// in generator.
use crate::world::architecture::{
    TEMPLATE_ARCH_ROOM, TEMPLATE_BLACKOUT_ZONE, TEMPLATE_CLEANING_AREA, TEMPLATE_DANGER_ROOM,
    TEMPLATE_DEAD_END, TEMPLATE_HALLWAY_CORNER, TEMPLATE_HALLWAY_STRAIGHT, TEMPLATE_HALLWAY_T,
    TEMPLATE_HUMID_ZONE, TEMPLATE_INTERSECTION, TEMPLATE_MANILA_ROOM, TEMPLATE_OPEN_HALL,
    TEMPLATE_PILLAR_ROOM, TEMPLATE_PIT_ROOM_PLACEHOLDER, TEMPLATE_POI_ANOMALY,
    TEMPLATE_POI_DANGER_POCKET, TEMPLATE_POI_LANDMARK, TEMPLATE_POI_SAFE_POCKET,
    TEMPLATE_RED_ROOM_WARNING, TEMPLATE_ROOM_BASIC, TEMPLATE_SAFE_ROOM, TEMPLATE_STORAGE_ROOM,
};
use crate::world::generator::{
    chunk_seed_layer, corner_rotation, dir_delta, fisher_yates, straight_rotation,
    structure_bounds, structure_id, t_junction_rotation,
};

use crate::world::levels::level_0::structure::{StructureType, StructureV0};

pub(crate) struct Level0Builder {
    world_seed: u64,
    rng: StdRng,
    occupied: HashSet<ChunkPos>,
    structures: Vec<StructureV0>,
    sid: u32,
}

impl Level0Builder {
    pub(crate) fn new(world_seed: u64) -> Self {
        Self {
            world_seed,
            rng: StdRng::seed_from_u64(world_seed ^ 0xBACB_00B5_CAFE_0001),
            occupied: HashSet::new(),
            structures: Vec::new(),
            sid: 1,
        }
    }

    fn is_free(&self, pos: ChunkPos) -> bool {
        !self.occupied.contains(&pos)
    }

    fn push_structure(
        &mut self,
        stype: StructureType,
        chunks: Vec<ChunkPos>,
        overrides: Vec<(u8, u16)>,
        tags: Vec<&'static str>,
    ) {
        let layers = vec![0; chunks.len()];
        self.push_layered_structure(stype, chunks, layers, overrides, tags);
    }

    fn push_layered_structure(
        &mut self,
        stype: StructureType,
        chunks: Vec<ChunkPos>,
        layers: Vec<ChunkLayer>,
        overrides: Vec<(u8, u16)>,
        tags: Vec<&'static str>,
    ) {
        let origin = chunks[0];
        let origin_layer = layers.first().copied().unwrap_or(0);
        let (min_x, min_z, max_x, max_z) = structure_bounds(&chunks);
        let size = [
            (max_x - min_x + 1).clamp(1, u8::MAX as i32) as u8,
            (max_z - min_z + 1).clamp(1, u8::MAX as i32) as u8,
        ];
        let mut exits = 0usize;
        for pos in &chunks {
            for dir in [0u8, 1, 2, 3] {
                let d = dir_delta(dir);
                let next = (pos.0 + d.0, pos.1 + d.1);
                if !chunks.contains(&next) {
                    exits += 1;
                }
            }
        }
        let depth = origin.0.abs() + origin.1.abs();
        info!(
            "MPTRACE step=DA event=level0_macro_pattern_created id={} kind={} origin=({},{},{}) size=({},{}) chunks={} exits={} depth={} stability={}",
            structure_id(self.world_seed, self.sid),
            stype.as_str(),
            origin.0,
            origin_layer,
            origin.1,
            size[0],
            size[1],
            chunks.len(),
            exits,
            depth,
            if tags.contains(&"starter") || tags.contains(&"safe") { "stable" } else { "unstable" }
        );
        self.structures.push(StructureV0 {
            id: structure_id(self.world_seed, self.sid),
            structure_type: stype,
            origin,
            origin_layer,
            size,
            seed: chunk_seed_layer(self.world_seed, origin, origin_layer),
            chunks,
            layers,
            tags,
            chunk_overrides: overrides,
        });
        self.sid += 1;
    }

    pub(crate) fn build(mut self) -> Vec<StructureV0> {
        self.build_starter();
        let junctions = self.build_main_corridors();
        self.build_secondary_branches(&junctions);
        self.build_macro_spaces();
        self.build_loop_connections();
        self.place_vertical_showcase();
        self.place_multilayer_showcase();
        // POIs run last so showcases claim their canonical positions first.
        self.build_pois();

        info!(
            "MPTRACE step=AS event=level0_generation_started seed={} structures={} total_chunks={}",
            self.world_seed,
            self.structures.len(),
            self.occupied.len()
        );

        self.structures
    }

    // ── Step 1: Starter cluster at (0,0)-(1,1) ──

    fn build_starter(&mut self) {
        let positions: Vec<ChunkPos> = vec![(0, 0), (1, 0), (0, 1), (1, 1)];
        let overrides = vec![
            (TEMPLATE_SAFE_ROOM, 0u16),
            (TEMPLATE_ROOM_BASIC, 90),
            (TEMPLATE_ROOM_BASIC, 180),
            (TEMPLATE_ROOM_BASIC, 270),
        ];
        for &p in &positions {
            self.occupied.insert(p);
        }
        self.push_structure(
            StructureType::StarterCluster,
            positions,
            overrides,
            vec!["starter", "safeish"],
        );
    }

    // ── Step 2: Main corridors extending from starter ──

    fn build_main_corridors(&mut self) -> Vec<(ChunkPos, u8)> {
        // Candidate arm starting points and directions
        let arm_options: [(ChunkPos, u8); 6] = [
            ((2, 0), 0),  // East from (1,0)
            ((-1, 0), 2), // West from (0,0)
            ((0, 2), 1),  // North from (0,1)
            ((1, -1), 3), // South from (1,0)
            ((-1, 1), 2), // West from (0,1)
            ((1, 2), 1),  // North from (1,1)
        ];

        let num_arms = self.rng.gen_range(4..=5usize);
        let mut indices: Vec<usize> = (0..arm_options.len()).collect();
        fisher_yates(&mut indices, &mut self.rng);
        indices.truncate(num_arms);
        // Always include East arm (index 0) for Backrooms corridor feel
        if !indices.contains(&0) {
            indices[0] = 0;
        }

        let mut junctions: Vec<(ChunkPos, u8)> = Vec::new();

        for &idx in &indices {
            let (start, dir) = arm_options[idx];
            if !self.is_free(start) {
                continue;
            }

            let length: usize = self.rng.gen_range(4..=8);
            let should_turn = self.rng.gen_bool(0.3);
            let turn_at = if should_turn {
                self.rng.gen_range(2..length.max(3))
            } else {
                usize::MAX
            };
            let perp: [u8; 2] = if dir % 2 == 0 { [1, 3] } else { [0, 2] };
            let turn_dir = perp[self.rng.gen_range(0..2)];

            let mut chunks = Vec::new();
            let mut overrides = Vec::new();
            let mut pos = start;
            let mut cur_dir = dir;

            for step in 0..length {
                if !self.is_free(pos) {
                    break;
                }

                if should_turn && step == turn_at {
                    // Place corner
                    let cr = corner_rotation(cur_dir, turn_dir);
                    chunks.push(pos);
                    overrides.push((TEMPLATE_HALLWAY_CORNER, cr));
                    self.occupied.insert(pos);
                    cur_dir = turn_dir;
                } else {
                    chunks.push(pos);
                    overrides.push((TEMPLATE_HALLWAY_STRAIGHT, straight_rotation(cur_dir)));
                    self.occupied.insert(pos);
                }

                let d = dir_delta(cur_dir);
                pos = (pos.0 + d.0, pos.1 + d.1);
            }

            if !chunks.is_empty() {
                self.push_structure(
                    StructureType::HallwayChain,
                    chunks,
                    overrides,
                    vec!["corridor", "main"],
                );
            }

            // Place junction at corridor end
            if self.is_free(pos) {
                let use_intersection = self.rng.gen_bool(0.45);
                if use_intersection {
                    self.occupied.insert(pos);
                    self.push_structure(
                        StructureType::Intersection,
                        vec![pos],
                        vec![(TEMPLATE_INTERSECTION, 0)],
                        vec!["junction"],
                    );
                } else {
                    // T-junction: close the wall opposite the continuation direction
                    let closed = perp[self.rng.gen_range(0..2)];
                    let rot = t_junction_rotation(closed);
                    self.occupied.insert(pos);
                    self.push_structure(
                        StructureType::HallwayT,
                        vec![pos],
                        vec![(TEMPLATE_HALLWAY_T, rot)],
                        vec!["junction"],
                    );
                }
                junctions.push((pos, cur_dir));
            }
        }

        // Ensure at least one intersection exists (for test compatibility)
        let has_intersection = self
            .structures
            .iter()
            .any(|s| s.structure_type == StructureType::Intersection);
        if !has_intersection {
            if let Some(s) = self
                .structures
                .iter_mut()
                .find(|s| s.structure_type == StructureType::HallwayT)
            {
                s.structure_type = StructureType::Intersection;
                s.chunk_overrides = vec![(TEMPLATE_INTERSECTION, 0)];
                s.tags = vec!["junction"];
            }
        }

        junctions
    }

    // ── Step 3: Secondary branches from junctions ──

    fn build_secondary_branches(&mut self, junctions: &[(ChunkPos, u8)]) {
        let junction_data: Vec<(ChunkPos, u8)> = junctions.to_vec();
        let mut has_storage = false;

        for (junction_pos, main_dir) in &junction_data {
            let perp: [u8; 2] = if main_dir % 2 == 0 { [1, 3] } else { [0, 2] };
            let num_branches: usize = self.rng.gen_range(1..=2);

            // Branch in perpendicular directions
            for b in 0..num_branches {
                let bdir = perp[b % 2];
                let bd = dir_delta(bdir);
                let bstart = (junction_pos.0 + bd.0, junction_pos.1 + bd.1);

                if !self.is_free(bstart) {
                    continue;
                }

                let blen: usize = self.rng.gen_range(2..=4);
                let brot = straight_rotation(bdir);
                let mut bchunks = Vec::new();
                let mut boverrides = Vec::new();
                let mut bpos = bstart;

                for _ in 0..blen {
                    if !self.is_free(bpos) {
                        break;
                    }
                    bchunks.push(bpos);
                    boverrides.push((TEMPLATE_HALLWAY_STRAIGHT, brot));
                    self.occupied.insert(bpos);
                    bpos = (bpos.0 + bd.0, bpos.1 + bd.1);
                }

                if !bchunks.is_empty() {
                    self.push_structure(
                        StructureType::HallwayChain,
                        bchunks,
                        boverrides,
                        vec!["corridor", "secondary"],
                    );
                }

                // Terminal room at branch end
                if self.is_free(bpos) {
                    let depth = (bpos.0.abs() + bpos.1.abs()) as f32;
                    let (stype, template, tags) = self.pick_terminal_room(depth, !has_storage);
                    if stype == StructureType::StorageRoom {
                        has_storage = true;
                    }
                    self.occupied.insert(bpos);
                    self.push_structure(stype, vec![bpos], vec![(template, 0)], tags);
                }
            }

            // Continue main direction from junction (50% chance)
            if self.rng.gen_bool(0.5) {
                let cd = dir_delta(*main_dir);
                let cstart = (junction_pos.0 + cd.0, junction_pos.1 + cd.1);
                if self.is_free(cstart) {
                    let clen: usize = self.rng.gen_range(2..=5);
                    let crot = straight_rotation(*main_dir);
                    let mut cchunks = Vec::new();
                    let mut coverrides = Vec::new();
                    let mut cpos = cstart;

                    for _ in 0..clen {
                        if !self.is_free(cpos) {
                            break;
                        }
                        cchunks.push(cpos);
                        coverrides.push((TEMPLATE_HALLWAY_STRAIGHT, crot));
                        self.occupied.insert(cpos);
                        cpos = (cpos.0 + cd.0, cpos.1 + cd.1);
                    }

                    if !cchunks.is_empty() {
                        self.push_structure(
                            StructureType::HallwayChain,
                            cchunks,
                            coverrides,
                            vec!["corridor", "continuation"],
                        );
                    }

                    // Terminal at continuation end
                    if self.is_free(cpos) {
                        let depth = (cpos.0.abs() + cpos.1.abs()) as f32;
                        let (stype, template, tags) = self.pick_terminal_room(depth, !has_storage);
                        if stype == StructureType::StorageRoom {
                            has_storage = true;
                        }
                        self.occupied.insert(cpos);
                        self.push_structure(stype, vec![cpos], vec![(template, 0)], tags);
                    }
                }
            }
        }

        // Guarantee at least one storage room exists
        if !has_storage {
            let last_terminal = self
                .structures
                .iter_mut()
                .rev()
                .find(|s| s.structure_type == StructureType::DeadEnd);
            if let Some(s) = last_terminal {
                s.structure_type = StructureType::StorageRoom;
                s.chunk_overrides = vec![(TEMPLATE_STORAGE_ROOM, 0)];
                s.tags = vec!["loot"];
            }
        }
    }

    fn pick_terminal_room(
        &mut self,
        depth: f32,
        force_storage: bool,
    ) -> (StructureType, u8, Vec<&'static str>) {
        if force_storage && depth < 8.0 {
            return (
                StructureType::StorageRoom,
                TEMPLATE_STORAGE_ROOM,
                vec!["loot"],
            );
        }

        if depth < 4.0 {
            match self.rng.gen_range(0..3) {
                0 => (
                    StructureType::StorageRoom,
                    TEMPLATE_STORAGE_ROOM,
                    vec!["loot"],
                ),
                1 => (StructureType::SafeRoom, TEMPLATE_SAFE_ROOM, vec!["safe"]),
                _ => (
                    StructureType::PillarRoom,
                    TEMPLATE_PILLAR_ROOM,
                    vec!["atmospheric"],
                ),
            }
        } else if depth < 8.0 {
            match self.rng.gen_range(0..5) {
                0 => (
                    StructureType::StorageRoom,
                    TEMPLATE_STORAGE_ROOM,
                    vec!["loot"],
                ),
                1 => (
                    StructureType::PillarRoom,
                    TEMPLATE_PILLAR_ROOM,
                    vec!["atmospheric"],
                ),
                2 => (StructureType::DeadEnd, TEMPLATE_DEAD_END, vec!["dead_end"]),
                3 => (
                    StructureType::DangerRoom,
                    TEMPLATE_DANGER_ROOM,
                    vec!["danger"],
                ),
                _ => (StructureType::SafeRoom, TEMPLATE_SAFE_ROOM, vec!["safe"]),
            }
        } else {
            match self.rng.gen_range(0..5) {
                0 => (
                    StructureType::DangerRoom,
                    TEMPLATE_DANGER_ROOM,
                    vec!["danger", "deep"],
                ),
                1 => (
                    StructureType::PillarRoom,
                    TEMPLATE_PILLAR_ROOM,
                    vec!["atmospheric", "deep"],
                ),
                2 => (
                    StructureType::DeadEnd,
                    TEMPLATE_DEAD_END,
                    vec!["dead_end", "deep"],
                ),
                3 => (
                    StructureType::StorageRoom,
                    TEMPLATE_STORAGE_ROOM,
                    vec!["loot", "deep"],
                ),
                _ => (
                    StructureType::DangerRoom,
                    TEMPLATE_DANGER_ROOM,
                    vec!["danger", "deep"],
                ),
            }
        }
    }

    // ── Step 3.5: Macro-spaces for stronger Backrooms spatial identity ──

    fn build_macro_spaces(&mut self) {
        // These are backend-authored macro visual spaces. Unity should render them based
        // on template_id instead of inventing random ramps/pits client-side.
        //
        // The placement scans deterministic candidate rectangles near existing chunks.
        // Each macro is adjacent to the current graph, so the coarse BFS connectivity
        // invariant stays valid while still producing bigger rooms/halls.

        let mut total_macros = 0u32;
        let mut open_halls = 0u32;
        let mut pillar_halls = 0u32;
        let mut anomaly_count = 0u32;

        let open_hall_placed = self.try_push_macro_rect(
            StructureType::OpenHall,
            3,
            3,
            &[
                ((-4, -2), 0),
                ((3, 2), 180),
                ((-5, 2), 90),
                ((2, -5), 270),
                ((5, -1), 0),
                ((-2, 4), 180),
            ],
            vec![
                TEMPLATE_OPEN_HALL,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_INTERSECTION,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_OPEN_HALL,
            ],
            vec!["macro", "open_hall", "large_room"],
        ) || self.try_push_macro_rect_scan(
            StructureType::OpenHall,
            2,
            2,
            vec![
                TEMPLATE_OPEN_HALL,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_OPEN_HALL,
            ],
            vec!["macro", "open_hall", "empty_office", "fallback"],
            3,
        );
        if open_hall_placed {
            total_macros += 1;
            open_halls += 1;
        }

        if self.try_push_macro_rect(
            StructureType::OpenHall,
            2,
            2,
            &[
                ((-3, 5), 0),
                ((5, -3), 90),
                ((-7, 0), 180),
                ((1, 5), 270),
                ((6, 5), 0),
            ],
            vec![
                TEMPLATE_OPEN_HALL,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_OPEN_HALL,
            ],
            vec!["macro", "open_hall", "empty_office"],
        ) {
            total_macros += 1;
            open_halls += 1;
        }

        let pillar_hall_placed = self.try_push_macro_rect(
            StructureType::PillarHall,
            3,
            3,
            &[
                ((-6, 3), 90),
                ((4, 4), 180),
                ((-6, -4), 0),
                ((3, -6), 270),
                ((6, 2), 90),
            ],
            vec![
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
            ],
            vec!["macro", "pillar_hall", "columns"],
        ) || self.try_push_macro_rect(
            StructureType::PillarHall,
            3,
            2,
            &[
                ((-6, 3), 90),
                ((4, 4), 180),
                ((-6, -4), 0),
                ((3, -6), 270),
                ((6, 2), 90),
                ((-4, 7), 0),
                ((7, -4), 90),
            ],
            vec![
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
            ],
            vec!["macro", "pillar_hall", "columns", "fallback"],
        ) || self.try_push_macro_rect_scan(
            StructureType::PillarHall,
            3,
            2,
            vec![
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_PILLAR_ROOM,
                TEMPLATE_OPEN_HALL,
            ],
            vec!["macro", "pillar_hall", "columns", "scan_fallback"],
            4,
        );
        if pillar_hall_placed {
            total_macros += 1;
            pillar_halls += 1;
        }

        if self.try_push_macro_rect(
            StructureType::HumidZone,
            2,
            2,
            &[
                ((-9, 3), 0),
                ((8, -4), 90),
                ((-5, 7), 180),
                ((3, -8), 270),
                ((7, 6), 0),
            ],
            vec![
                TEMPLATE_HUMID_ZONE,
                TEMPLATE_HUMID_ZONE,
                TEMPLATE_HALLWAY_CORNER,
                TEMPLATE_HUMID_ZONE,
            ],
            vec!["macro", "humid", "stains"],
        ) {
            total_macros += 1;
        }

        if self.try_push_macro_rect(
            StructureType::ArchRoom,
            2,
            2,
            &[((-8, 6), 0), ((8, -5), 90), ((5, 6), 180), ((-6, -7), 270)],
            vec![
                TEMPLATE_ARCH_ROOM,
                TEMPLATE_HALLWAY_T,
                TEMPLATE_ARCH_ROOM,
                TEMPLATE_ROOM_BASIC,
            ],
            vec!["macro", "arch_room", "transition"],
        ) {
            total_macros += 1;
        }

        if self.try_push_macro_rect(
            StructureType::BlackoutZone,
            2,
            2,
            &[
                ((10, -8), 0),
                ((-11, -5), 90),
                ((-8, 9), 180),
                ((11, 4), 270),
            ],
            vec![
                TEMPLATE_BLACKOUT_ZONE,
                TEMPLATE_DANGER_ROOM,
                TEMPLATE_BLACKOUT_ZONE,
                TEMPLATE_DEAD_END,
            ],
            vec!["macro", "blackout", "deep", "danger"],
        ) {
            total_macros += 1;
            anomaly_count += 1;
        }

        if self.try_push_macro_rect(
            StructureType::ManilaRoom,
            2,
            2,
            &[((2, 5), 0), ((-5, -6), 90), ((7, -3), 180), ((-8, 4), 270)],
            vec![
                TEMPLATE_MANILA_ROOM,
                TEMPLATE_ROOM_BASIC,
                TEMPLATE_MANILA_ROOM,
                TEMPLATE_INTERSECTION,
            ],
            vec!["macro", "manila_room", "warm_dim"],
        ) {
            total_macros += 1;
        }

        if self.try_push_macro_rect(
            StructureType::CleaningArea,
            2,
            2,
            &[
                ((-10, 1), 0),
                ((9, 7), 90),
                ((-9, -8), 180),
                ((6, -10), 270),
            ],
            vec![
                TEMPLATE_CLEANING_AREA,
                TEMPLATE_STORAGE_ROOM,
                TEMPLATE_CLEANING_AREA,
                TEMPLATE_HUMID_ZONE,
            ],
            vec!["macro", "cleaning_area", "loot_candidate"],
        ) {
            total_macros += 1;
            anomaly_count += 1;
        }

        if self.try_push_macro_rect(
            StructureType::RedRoom,
            2,
            2,
            &[
                ((13, -9), 0),
                ((-13, 7), 90),
                ((10, 10), 180),
                ((-11, -11), 270),
            ],
            vec![
                TEMPLATE_RED_ROOM_WARNING,
                TEMPLATE_HALLWAY_STRAIGHT,
                TEMPLATE_RED_ROOM_WARNING,
                TEMPLATE_DANGER_ROOM,
            ],
            vec!["macro", "red_room_warning", "deep", "rare"],
        ) {
            total_macros += 1;
            anomaly_count += 1;
        }

        if self.try_push_macro_rect(
            StructureType::PitRoom,
            2,
            2,
            &[
                ((4, -12), 0),
                ((-12, 1), 90),
                ((12, 4), 180),
                ((-4, 12), 270),
            ],
            vec![
                TEMPLATE_PIT_ROOM_PLACEHOLDER,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_HALLWAY_CORNER,
                TEMPLATE_PIT_ROOM_PLACEHOLDER,
            ],
            vec!["macro", "pit_room_placeholder", "deep", "visual_only"],
        ) {
            total_macros += 1;
            anomaly_count += 1;
        }

        info!(
            "MPTRACE step=BA event=level0_macro_spaces_complete seed={} structures={} chunks={} total_macrospaces={} open_halls={} pillar_halls={} anomalies={}",
            self.world_seed,
            self.structures.len(),
            self.occupied.len(),
            total_macros,
            open_halls,
            pillar_halls,
            anomaly_count
        );
    }

    fn try_push_macro_rect(
        &mut self,
        stype: StructureType,
        w: i32,
        h: i32,
        candidates: &[(ChunkPos, u16)],
        templates: Vec<u8>,
        tags: Vec<&'static str>,
    ) -> bool {
        if templates.is_empty() {
            return false;
        }

        // Deterministically shuffle candidate order, but keep it stable for a given seed.
        let mut order: Vec<usize> = (0..candidates.len()).collect();
        fisher_yates(&mut order, &mut self.rng);

        for idx in order {
            let (origin, base_rotation) = candidates[idx];
            let mut chunks = Vec::new();
            let mut free = true;
            let mut adjacent = false;

            for dz in 0..h {
                for dx in 0..w {
                    let pos = (origin.0 + dx, origin.1 + dz);
                    if !self.is_free(pos) {
                        free = false;
                        break;
                    }

                    for d in [(1, 0), (-1, 0), (0, 1), (0, -1)] {
                        if self.occupied.contains(&(pos.0 + d.0, pos.1 + d.1)) {
                            adjacent = true;
                        }
                    }

                    chunks.push(pos);
                }

                if !free {
                    break;
                }
            }

            if !free || !adjacent || chunks.is_empty() {
                continue;
            }

            let mut overrides = Vec::with_capacity(chunks.len());
            for i in 0..chunks.len() {
                let template = templates[i % templates.len()];
                let rotation = ((base_rotation as u32 + ((i as u32 % 4) * 90)) % 360) as u16;
                overrides.push((template, rotation));
            }

            for &p in &chunks {
                self.occupied.insert(p);
            }

            self.push_structure(stype, chunks, overrides, tags);
            return true;
        }

        false
    }

    // ── Step 3.6: Procedural POIs V1 ──
    //
    // Places 3–4 POI archetypes deterministically after the main macro-spaces
    // step, so the earlier passes have already claimed their territory and POIs
    // fill whatever is left without conflicting. Every placement call uses the
    // same adjacency-required constraint as macro-spaces, so POIs are always
    // physically connected to the existing chunk graph.

    fn build_pois(&mut self) {
        // ── Landmark: 2×2 cluster, distance 4–9 ──
        // Deterministic candidate list (origin positions). Tried in shuffled
        // order so different seeds find different positions.
        let landmark_candidates: &[(ChunkPos, u16)] = &[
            ((-4, -3), 0),
            ((5, 3), 90),
            ((-3, 5), 180),
            ((4, -5), 270),
            ((6, -2), 0),
            ((-6, 2), 90),
            ((3, 6), 180),
            ((-2, -6), 270),
        ];
        let landmark_placed = self.try_push_macro_rect(
            StructureType::PoiLandmark,
            2,
            2,
            landmark_candidates,
            vec![
                TEMPLATE_POI_LANDMARK,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_POI_LANDMARK,
            ],
            vec!["poi", "landmark", "poi_v1"],
        ) || self.try_push_macro_rect_scan(
            StructureType::PoiLandmark,
            2,
            2,
            vec![
                TEMPLATE_POI_LANDMARK,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_OPEN_HALL,
                TEMPLATE_POI_LANDMARK,
            ],
            vec!["poi", "landmark", "poi_v1", "scan"],
            4,
        );

        // ── Anomaly Cluster: 1×3 strip, distance 3–8 ──
        // A narrow disorienting run that feels spatially wrong.
        let anomaly_candidates: &[(ChunkPos, u16)] = &[
            ((-5, 1), 0),
            ((4, -2), 90),
            ((-2, 4), 180),
            ((3, -4), 270),
            ((7, 1), 0),
            ((-7, -1), 0),
            ((1, 7), 90),
            ((-1, -7), 90),
        ];
        let anomaly_placed = self.try_push_macro_rect(
            StructureType::PoiAnomalyCluster,
            1,
            3,
            anomaly_candidates,
            vec![
                TEMPLATE_POI_ANOMALY,
                TEMPLATE_HALLWAY_CORNER,
                TEMPLATE_POI_ANOMALY,
            ],
            vec!["poi", "anomaly", "poi_v1"],
        ) || self.try_push_macro_rect(
            StructureType::PoiAnomalyCluster,
            3,
            1,
            anomaly_candidates,
            vec![
                TEMPLATE_POI_ANOMALY,
                TEMPLATE_HALLWAY_CORNER,
                TEMPLATE_POI_ANOMALY,
            ],
            vec!["poi", "anomaly", "poi_v1", "horiz"],
        ) || self.try_push_macro_rect_scan(
            StructureType::PoiAnomalyCluster,
            1,
            3,
            vec![
                TEMPLATE_POI_ANOMALY,
                TEMPLATE_HALLWAY_CORNER,
                TEMPLATE_POI_ANOMALY,
            ],
            vec!["poi", "anomaly", "poi_v1", "scan"],
            3,
        );

        // ── Danger Pocket: 2×2, distance 8+ ──
        let danger_candidates: &[(ChunkPos, u16)] = &[
            ((10, -6), 0),
            ((-10, 5), 90),
            ((7, 8), 180),
            ((-8, -7), 270),
            ((11, 2), 0),
            ((-9, -9), 0),
            ((6, 10), 90),
            ((-11, 3), 90),
        ];
        let danger_placed = self.try_push_macro_rect(
            StructureType::PoiDangerPocket,
            2,
            2,
            danger_candidates,
            vec![
                TEMPLATE_POI_DANGER_POCKET,
                TEMPLATE_DANGER_ROOM,
                TEMPLATE_BLACKOUT_ZONE,
                TEMPLATE_POI_DANGER_POCKET,
            ],
            vec!["poi", "danger", "deep", "poi_v1"],
        ) || self.try_push_macro_rect_scan(
            StructureType::PoiDangerPocket,
            2,
            2,
            vec![
                TEMPLATE_POI_DANGER_POCKET,
                TEMPLATE_DANGER_ROOM,
                TEMPLATE_BLACKOUT_ZONE,
                TEMPLATE_POI_DANGER_POCKET,
            ],
            vec!["poi", "danger", "deep", "poi_v1", "scan"],
            8,
        );

        // ── Safe Pocket: 1×2, distance 3–7 ──
        let safe_candidates: &[(ChunkPos, u16)] = &[
            ((2, 4), 0),
            ((-4, 2), 90),
            ((3, -3), 180),
            ((-3, 3), 270),
            ((5, 1), 0),
            ((-5, -2), 90),
            ((1, -5), 180),
            ((-1, 5), 270),
        ];
        let safe_placed = self.try_push_macro_rect(
            StructureType::PoiSafePocket,
            1,
            2,
            safe_candidates,
            vec![TEMPLATE_POI_SAFE_POCKET, TEMPLATE_MANILA_ROOM],
            vec!["poi", "safe", "poi_v1"],
        ) || self.try_push_macro_rect(
            StructureType::PoiSafePocket,
            2,
            1,
            safe_candidates,
            vec![TEMPLATE_POI_SAFE_POCKET, TEMPLATE_MANILA_ROOM],
            vec!["poi", "safe", "poi_v1", "horiz"],
        ) || self.try_push_macro_rect_scan(
            StructureType::PoiSafePocket,
            1,
            2,
            vec![TEMPLATE_POI_SAFE_POCKET, TEMPLATE_MANILA_ROOM],
            vec!["poi", "safe", "poi_v1", "scan"],
            3,
        );

        let total = [landmark_placed, anomaly_placed, danger_placed, safe_placed]
            .iter()
            .filter(|&&p| p)
            .count();
        info!(
            "MPTRACE step=POI1 event=level0_pois_v1 seed={} total_pois={} landmark={} anomaly={} danger={} safe={}",
            self.world_seed,
            total,
            landmark_placed as u8,
            anomaly_placed as u8,
            danger_placed as u8,
            safe_placed as u8,
        );
    }

    // ── Step 4: Loop connections ──

    fn try_push_macro_rect_scan(
        &mut self,
        stype: StructureType,
        w: i32,
        h: i32,
        templates: Vec<u8>,
        tags: Vec<&'static str>,
        min_depth: i32,
    ) -> bool {
        if templates.is_empty() {
            return false;
        }

        let mut anchors: Vec<ChunkPos> = self.occupied.iter().copied().collect();
        anchors.sort_by_key(|p| (p.0.abs() + p.1.abs(), p.0, p.1));

        for anchor in anchors {
            let origins = [
                (anchor.0 + 1, anchor.1 - h / 2),
                (anchor.0 - w, anchor.1 - h / 2),
                (anchor.0 - w / 2, anchor.1 + 1),
                (anchor.0 - w / 2, anchor.1 - h),
            ];

            for origin in origins {
                if origin.0.abs() + origin.1.abs() < min_depth {
                    continue;
                }

                let mut chunks = Vec::new();
                let mut free = true;
                let mut adjacent = false;

                for dz in 0..h {
                    for dx in 0..w {
                        let pos = (origin.0 + dx, origin.1 + dz);
                        if !self.is_free(pos) {
                            free = false;
                            break;
                        }

                        for d in [(1, 0), (-1, 0), (0, 1), (0, -1)] {
                            if self.occupied.contains(&(pos.0 + d.0, pos.1 + d.1)) {
                                adjacent = true;
                            }
                        }

                        chunks.push(pos);
                    }

                    if !free {
                        break;
                    }
                }

                if !free || !adjacent || chunks.is_empty() {
                    continue;
                }

                let mut overrides = Vec::with_capacity(chunks.len());
                for i in 0..chunks.len() {
                    let template = templates[i % templates.len()];
                    let rotation = ((i as u32 % 4) * 90) as u16;
                    overrides.push((template, rotation));
                }

                for &p in &chunks {
                    self.occupied.insert(p);
                }

                self.push_structure(stype, chunks, overrides, tags);
                return true;
            }
        }

        false
    }

    // Phase 2.10B: guarantee one reachable, connected vertical "showcase" chunk
    // at Chebyshev distance 4–8 from spawn so verticality is always easy to find.
    // Deterministic (no RNG), placed adjacent to the existing graph → connected,
    // and well outside the radius-2 flat spawn region.
    fn place_vertical_showcase(&mut self) {
        let kinds: [(StructureType, u8, &'static str); 5] = [
            (StructureType::ManilaRoom, TEMPLATE_MANILA_ROOM, "raised"),
            (StructureType::ArchRoom, TEMPLATE_ARCH_ROOM, "ramp"),
            (
                StructureType::CleaningArea,
                TEMPLATE_CLEANING_AREA,
                "stairs",
            ),
            (StructureType::HumidZone, TEMPLATE_HUMID_ZONE, "sunken"),
            (StructureType::PitRoom, TEMPLATE_PIT_ROOM_PLACEHOLDER, "pit"),
        ];
        let (stype, template, kind) = kinds[(self.world_seed % 5) as usize];

        let mut occ: Vec<ChunkPos> = self.occupied.iter().copied().collect();
        occ.sort_by_key(|p| (p.0.abs().max(p.1.abs()), p.0, p.1));
        let dirs = [(1, 0), (-1, 0), (0, 1), (0, -1)];

        for o in occ {
            for d in dirs {
                let c = (o.0 + d.0, o.1 + d.1);
                let dist = c.0.abs().max(c.1.abs());
                if self.is_free(c) && (4..=8).contains(&dist) {
                    self.occupied.insert(c);
                    self.push_structure(
                        stype,
                        vec![c],
                        vec![(template, 0)],
                        vec!["macro", "vertical_showcase"],
                    );
                    info!(
                        "MPTRACE step=V210B event=vertical_showcase_created seed={} origin=({},{}) kind={} distance={} connected=true",
                        self.world_seed, c.0, c.1, kind, dist
                    );
                    return;
                }
            }
        }
        info!(
            "MPTRACE step=V210B event=vertical_showcase_created seed={} origin=(0,0) kind={} distance=0 connected=false",
            self.world_seed, kind
        );
    }

    fn place_multilayer_showcase(&mut self) {
        let target_layer: ChunkLayer = if self.world_seed.is_multiple_of(2) {
            -1
        } else {
            1
        };
        let connector_kind = if target_layer > 0 {
            "broad_stairwell"
        } else {
            "service_ramp"
        };
        let branch_type = if target_layer > 0 {
            StructureType::UpperOfficeBranch
        } else {
            StructureType::LowerServiceBranch
        };
        let branch_template = if target_layer > 0 {
            TEMPLATE_ARCH_ROOM
        } else {
            TEMPLATE_CLEANING_AREA
        };

        let mut occ: Vec<ChunkPos> = self.occupied.iter().copied().collect();
        occ.sort_by_key(|p| (p.0.abs().max(p.1.abs()), p.0, p.1));
        let dirs: [(i32, i32, u16); 4] = [(1, 0, 90), (-1, 0, 90), (0, 1, 0), (0, -1, 0)];

        for anchor in occ {
            for (dx, dz, rot) in dirs {
                let connector = (anchor.0 + dx, anchor.1 + dz);
                let atrium = (connector.0 + dx, connector.1 + dz);
                let dist = connector.0.abs().max(connector.1.abs());
                if !(4..=8).contains(&dist) || !self.is_free(connector) || !self.is_free(atrium) {
                    continue;
                }

                self.occupied.insert(connector);
                self.occupied.insert(atrium);

                let chunks = vec![connector, connector, atrium, atrium];
                let layers = vec![0, target_layer, 0, target_layer];
                let overrides = vec![
                    (TEMPLATE_OPEN_HALL, rot),
                    (branch_template, rot),
                    (TEMPLATE_OPEN_HALL, 0),
                    (TEMPLATE_PILLAR_ROOM, 0),
                ];

                self.push_layered_structure(
                    StructureType::StackedCorridor,
                    chunks,
                    layers,
                    overrides,
                    vec![
                        "macro",
                        "v30a_multilayer_showcase",
                        "connector",
                        "atrium",
                        "pillar_hall",
                        "precipice",
                    ],
                );

                info!(
                    "MPTRACE step=V30A event=multilayer_showcase_created seed={} origin=({},{},{}) target_layer={} kind={} connected=true",
                    self.world_seed,
                    connector.0,
                    0,
                    connector.1,
                    target_layer,
                    connector_kind
                );
                info!(
                    "MPTRACE step=V30AFIX event=showcase_world_target connector_chunk=({},{},{}) world_center=({:.0},{:.0},{:.0}) target_layer={}",
                    connector.0,
                    0,
                    connector.1,
                    connector.0 as f32 * CHUNK_SIZE + CHUNK_SIZE * 0.5,
                    0.0,
                    connector.1 as f32 * CHUNK_SIZE + CHUNK_SIZE * 0.5,
                    target_layer
                );

                info!(
                    "MPTRACE step=V30A event=multilayer_branch_created seed={} origin=({},{},{}) target_layer={} kind={} connected=true",
                    self.world_seed,
                    atrium.0,
                    target_layer,
                    atrium.1,
                    target_layer,
                    branch_type.as_str()
                );
                if self.world_seed == 7778 {
                    info!(
                        "MPTRACE step=V30A2 event=seed_7778_vertical_showcase_ready connector_chunk=({},{},{}) atrium_chunk=({},{},{}) world_center=({:.0},{:.0},{:.0}) target_layer={} volumes=7",
                        connector.0,
                        0,
                        connector.1,
                        atrium.0,
                        target_layer,
                        atrium.1,
                        connector.0 as f32 * CHUNK_SIZE + CHUNK_SIZE * 0.5,
                        0.0,
                        connector.1 as f32 * CHUNK_SIZE + CHUNK_SIZE * 0.5,
                        target_layer
                    );
                }
                return;
            }
        }

        info!(
            "MPTRACE step=V30A event=multilayer_showcase_created seed={} origin=(0,0,0) target_layer={} kind={} connected=false",
            self.world_seed, target_layer, connector_kind
        );
    }

    fn build_loop_connections(&mut self) {
        // Sort positions for deterministic iteration (HashSet order is random)
        let mut all_positions: Vec<ChunkPos> = self.occupied.iter().copied().collect();
        all_positions.sort();
        let mut candidates: Vec<ChunkPos> = Vec::new();

        // Find gaps of exactly 1 between two occupied chunks (axis-aligned)
        for &pos in &all_positions {
            for &(dx, dz) in &[(2i32, 0i32), (0, 2), (-2, 0), (0, -2)] {
                let other = (pos.0 + dx, pos.1 + dz);
                if self.occupied.contains(&other) {
                    let mid = (pos.0 + dx / 2, pos.1 + dz / 2);
                    if self.is_free(mid) && !candidates.contains(&mid) {
                        candidates.push(mid);
                    }
                }
            }
        }

        // Shuffle candidates deterministically, then pick 1-3
        let mut indices: Vec<usize> = (0..candidates.len()).collect();
        fisher_yates(&mut indices, &mut self.rng);
        let num_loops = self.rng.gen_range(1..=3usize).min(candidates.len());
        for i in 0..num_loops {
            let mid = candidates[indices[i]];
            if self.is_free(mid) {
                self.occupied.insert(mid);
                self.push_structure(
                    StructureType::Intersection,
                    vec![mid],
                    vec![(TEMPLATE_INTERSECTION, 0)],
                    vec!["corridor", "loop"],
                );
            }
        }
    }
}
