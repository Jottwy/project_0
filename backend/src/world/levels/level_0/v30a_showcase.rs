//! Level 0 — Phase 3.0A multilayer showcase (MIG-5f).
//!
//! V30A showcase chunk layouts (connector / atrium / giant-pillar hall) and
//! their decorative `InterLayerVolumeV0` builders, moved verbatim from
//! `generator.rs`. Behaviour, MPTRACE logs, volume IDs and the RNG-free
//! contract are unchanged: nothing in this module consumes RNG — all IDs come
//! from `stable_volume_id` (pure hash).

use log::info;

use crate::utils::ChunkPos;
use crate::world::architecture::build_chunk_layout;
use crate::world::architecture::chunk_generator::stable_volume_id;
use crate::world::architecture::surface_builder::perimeter_openings;
use crate::world::architecture::{TEMPLATE_OPEN_HALL, TEMPLATE_PILLAR_ROOM};
use crate::world::chunk::{
    Chunk, ChunkLayer, ChunkLayoutV1, InterLayerVolumeKindV0, InterLayerVolumeV0,
    CEILING_LOW_SERVICE, CEILING_NORMAL, CEILING_TALL_HALL, CELL_HAZARD, CELL_PILLAR, CELL_PIT,
    CELL_WALKABLE, EDGE_KIND_LOW_WALL, EDGE_KIND_OPEN, FLOOR_CONNECTOR_DOWN, FLOOR_CONNECTOR_UP,
    FLOOR_FLAT, LAYOUT_GRID_SIZE, LIGHT_DIM, LIGHT_WARM, V30A_ATRIUM_VOID_ROOM,
    V30A_BLOCKED_VERTICAL_SHAFT, V30A_CONNECTOR, V30A_DEEP_PRECIPICE_PLACEHOLDER,
    V30A_GIANT_PILLAR_HALL, V30A_LOWER_SERVICE_BRANCH, V30A_STACKED_CORRIDOR,
    V30A_UPPER_OFFICE_BRANCH, VOLUME_VIS_ATRIUM_WALLS, VOLUME_VIS_CEILING_HINTS,
    VOLUME_VIS_DEPTH_CUES, VOLUME_VIS_LOWER_ROOM_VISIBLE, VOLUME_VIS_PILLAR_SPANS,
    VOLUME_VIS_RAILINGS, VOLUME_VIS_RIM_TRIMS, VOLUME_VIS_SHAFT_WALLS,
    VOLUME_VIS_STACKED_ALIGNMENT, VOLUME_VIS_UNDERFLOOR_HINTS, ZONE_OPEN_HALL,
};
use crate::world::levels::level_0::structure::StructureV0;

fn inter_layer_layers(target_layer: ChunkLayer) -> Vec<ChunkLayer> {
    vec![0, target_layer]
}

// TODO(refactor): group into a params struct; deferred to keep this diff to a lint fix.
#[allow(clippy::too_many_arguments)]
fn inter_layer_volume(
    world_seed: u64,
    base_chunk: ChunkPos,
    target_layer: ChunkLayer,
    index: u32,
    kind: InterLayerVolumeKindV0,
    footprint_cell_min: [u8; 2],
    footprint_cell_max: [u8; 2],
    safety_type: &str,
    future_audio_hint: &str,
    visual_flags: u32,
    visual_hints: &[&str],
) -> InterLayerVolumeV0 {
    InterLayerVolumeV0 {
        volume_id: stable_volume_id(world_seed, base_chunk, 0, index),
        kind,
        base_chunk: [base_chunk.0, base_chunk.1],
        involved_layers: inter_layer_layers(target_layer),
        footprint_cell_min,
        footprint_cell_max,
        safety_type: safety_type.into(),
        future_audio_hint: future_audio_hint.into(),
        visual_flags,
        visual_hints: visual_hints.iter().map(|hint| (*hint).into()).collect(),
    }
}

fn push_inter_layer_volume(layout: &mut ChunkLayoutV1, volume: InterLayerVolumeV0) {
    info!(
        "MPTRACE step=V30A2 event=inter_layer_volume_created volume_id={} kind={} base_chunk=({},{}) footprint_cells=({},{})..({},{}) safety_type={} visual_flags={}",
        volume.volume_id,
        volume.kind.as_str(),
        volume.base_chunk[0],
        volume.base_chunk[1],
        volume.footprint_cell_min[0],
        volume.footprint_cell_min[1],
        volume.footprint_cell_max[0],
        volume.footprint_cell_max[1],
        volume.safety_type,
        volume.visual_flags
    );
    info!(
        "MPTRACE step=V30A2 event=vertical_volume_kind volume_id={} kind={}",
        volume.volume_id,
        volume.kind.as_str()
    );
    info!(
        "MPTRACE step=V30A2 event=vertical_volume_layers volume_id={} layers={:?}",
        volume.volume_id, volume.involved_layers
    );
    if !volume.future_audio_hint.is_empty() {
        info!(
            "MPTRACE step=V30A2 event=future_audio_hint_registered volume_id={} hint={}",
            volume.volume_id, volume.future_audio_hint
        );
    }

    match volume.kind {
        InterLayerVolumeKindV0::AtriumStack | InterLayerVolumeKindV0::ServiceShaft => {
            info!(
                "MPTRACE step=V30A2 event=shared_opening_built volume_id={} kind={} base_chunk=({},{}) layers={:?}",
                volume.volume_id,
                volume.kind.as_str(),
                volume.base_chunk[0],
                volume.base_chunk[1],
                volume.involved_layers
            );
        }
        InterLayerVolumeKindV0::StackedCorridorPair => {
            info!(
                "MPTRACE step=V30A2 event=stacked_corridor_pair_built volume_id={} base_chunk=({},{}) layers={:?}",
                volume.volume_id,
                volume.base_chunk[0],
                volume.base_chunk[1],
                volume.involved_layers
            );
        }
        InterLayerVolumeKindV0::OverlookRoom => {
            info!(
                "MPTRACE step=V30A2 event=lower_room_visible_from_above volume_id={} base_chunk=({},{}) layers={:?}",
                volume.volume_id,
                volume.base_chunk[0],
                volume.base_chunk[1],
                volume.involved_layers
            );
        }
        InterLayerVolumeKindV0::GiantPillarSpan => {
            info!(
                "MPTRACE step=V30A2 event=pillar_span_built volume_id={} base_chunk=({},{}) layers={:?}",
                volume.volume_id,
                volume.base_chunk[0],
                volume.base_chunk[1],
                volume.involved_layers
            );
        }
        InterLayerVolumeKindV0::CeilingActivityZone => {
            info!(
                "MPTRACE step=V30A2 event=ceiling_activity_hint_built volume_id={} base_chunk=({},{}) layers={:?}",
                volume.volume_id,
                volume.base_chunk[0],
                volume.base_chunk[1],
                volume.involved_layers
            );
        }
        InterLayerVolumeKindV0::UnderfloorServiceZone => {
            info!(
                "MPTRACE step=V30A2 event=underfloor_service_hint_built volume_id={} base_chunk=({},{}) layers={:?}",
                volume.volume_id,
                volume.base_chunk[0],
                volume.base_chunk[1],
                volume.involved_layers
            );
        }
    }

    layout.inter_layer_volumes.push(volume);
}

fn add_connector_inter_layer_volumes(world_seed: u64, chunk: &mut Chunk, target_layer: ChunkLayer) {
    let base = chunk.pos;
    push_inter_layer_volume(
        &mut chunk.layout,
        inter_layer_volume(
            world_seed,
            base,
            target_layer,
            0,
            InterLayerVolumeKindV0::ServiceShaft,
            [2, 0],
            [8, 10],
            "BACKEND_AUTHORED_VISUAL_NO_FALL",
            "service_shaft_hum_from_lower_layer",
            VOLUME_VIS_SHAFT_WALLS
                | VOLUME_VIS_RAILINGS
                | VOLUME_VIS_RIM_TRIMS
                | VOLUME_VIS_DEPTH_CUES,
            &["shaft_walls", "railing_runs", "matched_receiving_space"],
        ),
    );
    push_inter_layer_volume(
        &mut chunk.layout,
        inter_layer_volume(
            world_seed,
            base,
            target_layer,
            1,
            InterLayerVolumeKindV0::StackedCorridorPair,
            [2, 0],
            [8, 10],
            "BACKEND_AUTHORED_ALIGNMENT",
            "stacked_corridor_air_path",
            VOLUME_VIS_STACKED_ALIGNMENT | VOLUME_VIS_RIM_TRIMS | VOLUME_VIS_CEILING_HINTS,
            &["matching_corridor_axis", "ceiling_floor_alignment"],
        ),
    );
    push_inter_layer_volume(
        &mut chunk.layout,
        inter_layer_volume(
            world_seed,
            base,
            target_layer,
            2,
            InterLayerVolumeKindV0::UnderfloorServiceZone,
            [1, 1],
            [9, 9],
            "VISUAL_HINT_ONLY",
            "underfloor_service_void",
            VOLUME_VIS_UNDERFLOOR_HINTS | VOLUME_VIS_DEPTH_CUES,
            &["open_floor_service_grates", "subfloor_cable_trays"],
        ),
    );
}

fn add_atrium_inter_layer_volumes(world_seed: u64, chunk: &mut Chunk, target_layer: ChunkLayer) {
    let base = chunk.pos;
    push_inter_layer_volume(
        &mut chunk.layout,
        inter_layer_volume(
            world_seed,
            base,
            target_layer,
            10,
            InterLayerVolumeKindV0::AtriumStack,
            [3, 3],
            [7, 7],
            "BACKEND_AUTHORED_BLOCKED_SHAFT_NO_FALL",
            "atrium_vertical_reverb",
            VOLUME_VIS_ATRIUM_WALLS
                | VOLUME_VIS_LOWER_ROOM_VISIBLE
                | VOLUME_VIS_RAILINGS
                | VOLUME_VIS_RIM_TRIMS
                | VOLUME_VIS_DEPTH_CUES,
            &["shared_opening", "shaft_wall_panels", "lower_room_cues"],
        ),
    );
    push_inter_layer_volume(
        &mut chunk.layout,
        inter_layer_volume(
            world_seed,
            base,
            target_layer,
            11,
            InterLayerVolumeKindV0::OverlookRoom,
            [2, 2],
            [8, 8],
            "VISUAL_OVERLOOK_WITH_RAILING",
            "lower_room_floor_reflection",
            VOLUME_VIS_LOWER_ROOM_VISIBLE
                | VOLUME_VIS_RAILINGS
                | VOLUME_VIS_RIM_TRIMS
                | VOLUME_VIS_DEPTH_CUES,
            &[
                "visible_lower_room",
                "overlook_railings",
                "depth_floor_patch",
            ],
        ),
    );
    push_inter_layer_volume(
        &mut chunk.layout,
        inter_layer_volume(
            world_seed,
            base,
            target_layer,
            12,
            InterLayerVolumeKindV0::GiantPillarSpan,
            [1, 1],
            [9, 9],
            "STRUCTURAL_VISUAL_SUPPORT",
            "pillar_span_occlusion",
            VOLUME_VIS_PILLAR_SPANS | VOLUME_VIS_DEPTH_CUES,
            &[
                "layer_spanning_pillars",
                "pillar_caps_visible_across_layers",
            ],
        ),
    );
    push_inter_layer_volume(
        &mut chunk.layout,
        inter_layer_volume(
            world_seed,
            base,
            target_layer,
            13,
            InterLayerVolumeKindV0::CeilingActivityZone,
            [1, 1],
            [9, 9],
            "VISUAL_HINT_ONLY",
            "muffled_ceiling_activity",
            VOLUME_VIS_CEILING_HINTS | VOLUME_VIS_STACKED_ALIGNMENT,
            &["ceiling_service_panels", "upper_layer_activity_hint"],
        ),
    );
}

pub(crate) fn apply_v30a_layout(
    world_seed: u64,
    chunk: &mut Chunk,
    structure: &StructureV0,
    chunk_index: u32,
) {
    if !structure.tags.contains(&"v30a_multilayer_showcase") {
        return;
    }

    let target_layer = structure
        .layers
        .iter()
        .copied()
        .find(|layer| *layer != 0)
        .unwrap_or(if world_seed.is_multiple_of(2) { -1 } else { 1 });
    let branch_flag = if target_layer > 0 {
        V30A_UPPER_OFFICE_BRANCH
    } else {
        V30A_LOWER_SERVICE_BRANCH
    };

    chunk.entities.clear();
    chunk.items.clear();
    chunk.teleport_timer = f32::MAX;

    match chunk_index {
        0 => {
            chunk.layout = connector_layout(target_layer);
            chunk.template_id = TEMPLATE_OPEN_HALL;
            chunk.rotation = 0;
            chunk.layout.floor_profile = if target_layer > 0 {
                FLOOR_CONNECTOR_UP
            } else {
                FLOOR_CONNECTOR_DOWN
            };
            chunk.layout.ceiling_profile = CEILING_TALL_HALL;
            chunk.layout.light_profile = LIGHT_WARM;
            chunk.layout.vertical_flags |= V30A_CONNECTOR | branch_flag | V30A_STACKED_CORRIDOR;
            add_connector_inter_layer_volumes(world_seed, chunk, target_layer);
        }
        1 => {
            chunk.layout.vertical_flags |= V30A_STACKED_CORRIDOR | branch_flag;
            chunk.layout.floor_profile = FLOOR_FLAT;
            chunk.layout.floor_level = 0;
            chunk.layout.ceiling_profile = if target_layer > 0 {
                CEILING_NORMAL
            } else {
                CEILING_LOW_SERVICE
            };
            chunk.layout.light_profile = if target_layer > 0 {
                LIGHT_WARM
            } else {
                LIGHT_DIM
            };
            add_connector_inter_layer_volumes(world_seed, chunk, target_layer);
        }
        2 => {
            chunk.template_id = TEMPLATE_OPEN_HALL;
            chunk.layout = build_chunk_layout(TEMPLATE_OPEN_HALL, 0);
            mark_railed_vertical_opening(&mut chunk.layout, true);
            chunk.layout.ceiling_profile = CEILING_TALL_HALL;
            chunk.layout.vertical_flags |= V30A_ATRIUM_VOID_ROOM
                | V30A_DEEP_PRECIPICE_PLACEHOLDER
                | V30A_BLOCKED_VERTICAL_SHAFT;
            add_atrium_inter_layer_volumes(world_seed, chunk, target_layer);
        }
        _ => {
            chunk.template_id = TEMPLATE_PILLAR_ROOM;
            chunk.layout = build_chunk_layout(TEMPLATE_PILLAR_ROOM, 0);
            mark_giant_pillars(&mut chunk.layout);
            chunk.layout.ceiling_profile = CEILING_TALL_HALL;
            chunk.layout.vertical_flags |=
                V30A_GIANT_PILLAR_HALL | V30A_STACKED_CORRIDOR | branch_flag;
            add_atrium_inter_layer_volumes(world_seed, chunk, target_layer);
        }
    }
}

fn connector_layout(target_layer: ChunkLayer) -> ChunkLayoutV1 {
    let size = LAYOUT_GRID_SIZE as usize;
    let mut layout = ChunkLayoutV1::new(vec![CELL_WALKABLE; size * size], 0, ZONE_OPEN_HALL);
    layout.ceiling_profile = CEILING_TALL_HALL;
    layout.light_profile = LIGHT_WARM;

    for z in 0..size {
        layout.set_edge_v(2, z, EDGE_KIND_LOW_WALL);
        layout.set_edge_v(size - 2, z, EDGE_KIND_LOW_WALL);
    }
    for x in 3..=6 {
        layout.set_edge_h(x, 0, EDGE_KIND_OPEN);
        layout.set_edge_h(x, size, EDGE_KIND_OPEN);
    }

    layout.floor_profile = if target_layer > 0 {
        FLOOR_CONNECTOR_UP
    } else {
        FLOOR_CONNECTOR_DOWN
    };
    layout.vertical_flags = V30A_CONNECTOR | V30A_STACKED_CORRIDOR;
    layout.edge_openings = perimeter_openings(&layout);
    layout
}

fn mark_railed_vertical_opening(layout: &mut ChunkLayoutV1, deep_precipice: bool) {
    let g = layout.grid_size as usize;
    let start = 3usize;
    let end = (g - 3).max(start + 1);
    for z in start..end {
        for x in start..end {
            if let Some(idx) = layout.cell_index(x, z) {
                layout.cells[idx] = CELL_WALKABLE | CELL_PIT | CELL_HAZARD;
            }
        }
    }
    for z in start..end {
        layout.set_edge_v(start, z, EDGE_KIND_LOW_WALL);
        layout.set_edge_v(end, z, EDGE_KIND_LOW_WALL);
    }
    for x in start..end {
        layout.set_edge_h(x, start, EDGE_KIND_LOW_WALL);
        layout.set_edge_h(x, end, EDGE_KIND_LOW_WALL);
    }
    layout.floor_profile = FLOOR_FLAT;
    layout.vertical_flags |= V30A_ATRIUM_VOID_ROOM | V30A_BLOCKED_VERTICAL_SHAFT;
    if deep_precipice {
        layout.vertical_flags |= V30A_DEEP_PRECIPICE_PLACEHOLDER;
        layout.anomaly_flags |= 1 << 4;
    }
}

fn mark_giant_pillars(layout: &mut ChunkLayoutV1) {
    let positions = [(2usize, 2usize), (7, 2), (2, 7), (7, 7)];
    for (x, z) in positions {
        if let Some(idx) = layout.cell_index(x, z) {
            layout.cells[idx] = CELL_WALKABLE | CELL_PILLAR;
        }
    }
    layout.floor_profile = FLOOR_FLAT;
    layout.vertical_flags |= V30A_GIANT_PILLAR_HALL;
}
