//! Level 0 — seed-7778 VISFIX validation overlay (MIG-5g).
//!
//! Legacy decorative Phase 3.0A2 validation overlay for seed 7778, moved
//! verbatim from `generator.rs`. Gated OFF in the default runtime path (the
//! replacement is the render-only VolumetricGridV0 showcase); the gated legacy
//! entry point `generate_initial_structure_chunks_with_visfix_overlay` keeps
//! this code exercised by tests. MPTRACE log bodies are unchanged.
//! `log_seed_7778_visfix_generation` runs on BOTH the default and the gated
//! paths (same call sites as before the move).

use log::info;

use crate::utils::ChunkPos;
use crate::world::architecture::chunk_generator::{chunk_seed_layer, structure_id};
use crate::world::architecture::{
    TEMPLATE_CLEANING_AREA, TEMPLATE_OPEN_HALL, TEMPLATE_PILLAR_ROOM,
};
use crate::world::chunk::{Chunk, ChunkLayer};
use crate::world::generator::generate_structure_chunk;
use crate::world::levels::level_0::structure::{
    structure_bounds, structure_zone_kind, StructureType, StructureV0,
};

const V30A2_VISFIX_SEED: u64 = 7778;
const V30A2_VISFIX_CONNECTOR: ChunkPos = (1, 3);
const V30A2_VISFIX_ATRIUM: ChunkPos = (1, 4);
const V30A2_VISFIX_TARGET_LAYER: ChunkLayer = -1;

fn seed_7778_visfix_structure(world_seed: u64) -> StructureV0 {
    let chunks = vec![
        V30A2_VISFIX_CONNECTOR,
        V30A2_VISFIX_CONNECTOR,
        V30A2_VISFIX_ATRIUM,
        V30A2_VISFIX_ATRIUM,
    ];
    let layers = vec![0, V30A2_VISFIX_TARGET_LAYER, 0, V30A2_VISFIX_TARGET_LAYER];
    let (min_x, min_z, max_x, max_z) = structure_bounds(&chunks);
    StructureV0 {
        id: structure_id(world_seed, 30_020),
        structure_type: StructureType::StackedCorridor,
        origin: V30A2_VISFIX_CONNECTOR,
        origin_layer: 0,
        size: [
            (max_x - min_x + 1).clamp(1, u8::MAX as i32) as u8,
            (max_z - min_z + 1).clamp(1, u8::MAX as i32) as u8,
        ],
        seed: chunk_seed_layer(world_seed, V30A2_VISFIX_CONNECTOR, 0),
        chunks,
        layers,
        tags: vec![
            "macro",
            "v30a_multilayer_showcase",
            "v30a2_visfix_showcase",
            "seed_7778_validation",
        ],
        chunk_overrides: vec![
            (TEMPLATE_OPEN_HALL, 0),
            (TEMPLATE_CLEANING_AREA, 0),
            (TEMPLATE_OPEN_HALL, 0),
            (TEMPLATE_PILLAR_ROOM, 0),
        ],
    }
}

fn apply_structure_metadata(structure: &StructureV0, chunk: &mut Chunk, index: usize) {
    let (min_x, min_z, max_x, max_z) = structure_bounds(&structure.chunks);
    chunk.layout.macro_id = structure.id;
    chunk.layout.zone_kind = structure_zone_kind(structure.structure_type, chunk.template_id);
    chunk.layout.macro_local = [
        (chunk.pos.0 - min_x).clamp(0, u8::MAX as i32) as u8,
        (chunk.pos.1 - min_z).clamp(0, u8::MAX as i32) as u8,
    ];
    chunk.layout.macro_size = [
        (max_x - min_x + 1).clamp(1, u8::MAX as i32) as u8,
        (max_z - min_z + 1).clamp(1, u8::MAX as i32) as u8,
    ];
    if structure.tags.contains(&"v30a2_visfix_showcase") {
        chunk.teleport_timer = f32::MAX;
        chunk.entities.clear();
        chunk.items.clear();
        info!(
            "MPTRACE step=V30A2 event=v30a2_visfix_backend_volume_chunk chunk=({},{},{}) structure_id={} chunk_index={} macro_local=({},{})",
            chunk.pos.0,
            chunk.layer,
            chunk.pos.1,
            structure.id,
            index,
            chunk.layout.macro_local[0],
            chunk.layout.macro_local[1]
        );
    }
}

fn upsert_seed_7778_visfix_chunk(
    out: &mut Vec<(StructureV0, Chunk)>,
    structure: &StructureV0,
    index: usize,
    world_seed: u64,
) {
    let pos = structure.chunks[index];
    let layer = structure.chunk_layer(index);
    let mut chunk = generate_structure_chunk(world_seed, pos, layer, structure, index as u32);
    apply_structure_metadata(structure, &mut chunk, index);

    if let Some((existing_structure, existing_chunk)) = out
        .iter_mut()
        .find(|(_, c)| c.pos == pos && c.layer == layer)
    {
        *existing_structure = structure.clone();
        *existing_chunk = chunk;
    } else {
        out.push((structure.clone(), chunk));
    }
}

pub(crate) fn apply_seed_7778_visfix_overlay(world_seed: u64, out: &mut Vec<(StructureV0, Chunk)>) {
    if world_seed != V30A2_VISFIX_SEED {
        return;
    }

    info!(
        "MPTRACE step=V30A2 event=v30a2_visfix_backend_seed_7778_active mode=validation_overlay connector=({},{},{}) atrium=({},{},{})",
        V30A2_VISFIX_CONNECTOR.0,
        0,
        V30A2_VISFIX_CONNECTOR.1,
        V30A2_VISFIX_ATRIUM.0,
        V30A2_VISFIX_TARGET_LAYER,
        V30A2_VISFIX_ATRIUM.1
    );

    let structure = seed_7778_visfix_structure(world_seed);
    for index in 0..structure.chunks.len() {
        upsert_seed_7778_visfix_chunk(out, &structure, index, world_seed);
    }
}

pub(crate) fn log_seed_7778_visfix_generation(world_seed: u64, chunks: &[(StructureV0, Chunk)]) {
    if world_seed != V30A2_VISFIX_SEED {
        return;
    }

    let volume_count: usize = chunks
        .iter()
        .map(|(_, c)| c.layout.inter_layer_volumes.len())
        .sum();
    let volume_chunks = chunks
        .iter()
        .filter(|(_, c)| !c.layout.inter_layer_volumes.is_empty())
        .count();
    info!(
        "MPTRACE step=V30A2 event=v30a2_visfix_backend_volume_count seed={} volumes={} volume_chunks={} generated_chunks={}",
        world_seed,
        volume_count,
        volume_chunks,
        chunks.len()
    );

    for (_, chunk) in chunks
        .iter()
        .filter(|(_, c)| !c.layout.inter_layer_volumes.is_empty())
    {
        let dist_from_spawn = chunk.pos.0.abs().max(chunk.pos.1.abs());
        info!(
            "MPTRACE step=V30A2 event=v30a2_visfix_backend_volume_chunk chunk=({},{},{}) volumes={} layer_y={:.2} visible_radius_5={}",
            chunk.pos.0,
            chunk.layer,
            chunk.pos.1,
            chunk.layout.inter_layer_volumes.len(),
            crate::world::chunk::layer_y(chunk.layer),
            dist_from_spawn <= 5
        );
    }

    for pos in [V30A2_VISFIX_CONNECTOR, (-5, -1)] {
        let dist_from_spawn = pos.0.abs().max(pos.1.abs());
        info!(
            "MPTRACE step=V30A2 event=v30a2_visfix_backend_visible_radius_check chunk=({},{}) dist_from_spawn={} ownership_radius=5 visible={}",
            pos.0,
            pos.1,
            dist_from_spawn,
            dist_from_spawn <= 5
        );
    }

    let connector_world = crate::utils::chunk_center(V30A2_VISFIX_CONNECTOR);
    let original_world = crate::utils::chunk_center((-5, -1));
    info!(
        "MPTRACE step=V30A2 event=v30a2_visfix_backend_showcase_world_position validation_chunk=({},{},{}) validation_world=({:.1},{:.1},{:.1}) original_chunk=(-5,0,-1) original_world=({:.1},{:.1},{:.1}) spawn_world=(25.0,1.8,25.0)",
        V30A2_VISFIX_CONNECTOR.0,
        0,
        V30A2_VISFIX_CONNECTOR.1,
        connector_world.x,
        0.0,
        connector_world.z,
        original_world.x,
        0.0,
        original_world.z
    );
}
