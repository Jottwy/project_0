//! Level 0 — golden-slice determinism fixtures (MIG-5 anchor).
//!
//! Pins absolute values from `generate_chunk(42, (3,7))` and
//! `export_level0_ascii(42/7778)` so future migrations cannot silently
//! change the RNG stream or entity/item generation without a test failure.

#[cfg(test)]
mod tests {
    use crate::player::inventory::Item;
    use crate::world::entity::EntityType;
    use crate::world::generator::{export_level0_ascii, generate_chunk};

    fn ascii_hash(s: &str) -> u64 {
        s.bytes().fold(0u64, |h, b| {
            h.wrapping_mul(0x9e3779b97f4a7c15).wrapping_add(b as u64)
        })
    }

    /// Pins entity and item IDs, types, and positions for seed=42, pos=(3,7).
    /// Any change to the RNG stream or generation algorithm will break this test.
    #[test]
    fn golden_chunk_seed42_pos_3_7() {
        let c = generate_chunk(42, (3, 7));

        assert_eq!(c.entities.len(), 4, "entity count changed");
        assert_eq!(c.entities[0].id, 1921235681);
        assert_eq!(c.entities[0].entity_type, EntityType::Shadow);
        assert!((c.entities[0].position.x - 181.7984_f32).abs() < 0.001);
        assert!((c.entities[0].position.z - 369.4901_f32).abs() < 0.001);

        assert_eq!(c.entities[1].id, 335462221);
        assert_eq!(c.entities[1].entity_type, EntityType::Crawler);
        assert!((c.entities[1].position.x - 195.3512_f32).abs() < 0.001);
        assert!((c.entities[1].position.z - 381.3600_f32).abs() < 0.001);

        assert_eq!(c.entities[2].id, 813281721);
        assert_eq!(c.entities[2].entity_type, EntityType::Lurker);
        assert!((c.entities[2].position.x - 156.6388_f32).abs() < 0.001);
        assert!((c.entities[2].position.z - 366.4967_f32).abs() < 0.001);

        assert_eq!(c.entities[3].id, 1442489330);
        assert_eq!(c.entities[3].entity_type, EntityType::Crawler);
        assert!((c.entities[3].position.x - 174.0550_f32).abs() < 0.001);
        assert!((c.entities[3].position.z - 378.3161_f32).abs() < 0.001);

        assert_eq!(c.items.len(), 8, "item count changed");
        assert_eq!(c.items[0].id, 724155099);
        assert_eq!(c.items[0].item, Item::Metal);
        assert!((c.items[0].position.x - 159.4346_f32).abs() < 0.001);
        assert!((c.items[0].position.z - 370.0032_f32).abs() < 0.001);

        assert_eq!(c.items[1].id, 1247068023);
        assert_eq!(c.items[1].item, Item::Circuit);
        assert!((c.items[1].position.x - 182.5457_f32).abs() < 0.001);
        assert!((c.items[1].position.z - 352.6400_f32).abs() < 0.001);

        assert_eq!(c.items[2].id, 1775481219);
        assert_eq!(c.items[2].item, Item::Circuit);
        assert!((c.items[2].position.x - 192.9992_f32).abs() < 0.001);
        assert!((c.items[2].position.z - 397.1271_f32).abs() < 0.001);

        assert_eq!(c.items[3].id, 206611400);
        assert_eq!(c.items[3].item, Item::Circuit);
        assert!((c.items[3].position.x - 152.4518_f32).abs() < 0.001);
        assert!((c.items[3].position.z - 352.4769_f32).abs() < 0.001);

        assert_eq!(c.items[4].id, 786279530);
        assert_eq!(c.items[4].item, Item::Battery);
        assert!((c.items[4].position.x - 194.3758_f32).abs() < 0.001);
        assert!((c.items[4].position.z - 391.7702_f32).abs() < 0.001);

        assert_eq!(c.items[5].id, 1078507053);
        assert_eq!(c.items[5].item, Item::Food);
        assert!((c.items[5].position.x - 155.4602_f32).abs() < 0.001);
        assert!((c.items[5].position.z - 366.7973_f32).abs() < 0.001);

        assert_eq!(c.items[6].id, 1708886269);
        assert_eq!(c.items[6].item, Item::Food);
        assert!((c.items[6].position.x - 193.1333_f32).abs() < 0.001);
        assert!((c.items[6].position.z - 392.1334_f32).abs() < 0.001);

        assert_eq!(c.items[7].id, 61114970);
        assert_eq!(c.items[7].item, Item::Water);
        assert!((c.items[7].position.x - 152.8123_f32).abs() < 0.001);
        assert!((c.items[7].position.z - 381.0226_f32).abs() < 0.001);
    }

    /// Pins the ASCII export hash for seed=42 (world layout + zone rendering).
    #[test]
    fn golden_ascii_export_seed42() {
        let ascii = export_level0_ascii(42);
        assert_eq!(ascii.len(), 56592, "ascii export length changed (seed 42)");
        assert_eq!(
            ascii_hash(&ascii),
            0x43CBBFB7E7F818FB,
            "ascii export content changed (seed 42)"
        );
    }

    /// Pins the ASCII export hash for seed=7778 (VISFIX showcase seed).
    #[test]
    fn golden_ascii_export_seed7778() {
        let ascii = export_level0_ascii(7778);
        assert_eq!(
            ascii.len(),
            64149,
            "ascii export length changed (seed 7778)"
        );
        assert_eq!(
            ascii_hash(&ascii),
            0x7A43CA4EDFEA6665,
            "ascii export content changed (seed 7778)"
        );
    }
}
