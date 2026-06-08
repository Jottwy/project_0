//! Crafting recipes.
//! See ARCHITECTURE_V1.md and CLAUDE_CODE_INSTRUCTIONS.md Task 1.7.
//! Phase 1 scaffolding: the full recipe table is defined; the crafting *flow*
//! (validation against inventory, timed progress, interruption) lands in Phase 4.

use crate::player::inventory::{Item, StabilizerTier};

/// A single ingredient requirement.
#[derive(Debug, Clone, Copy)]
pub struct Ingredient {
    pub item: Item,
    pub quantity: u16,
    /// If true the item is a tool and is not consumed by crafting.
    pub consumed: bool,
}

#[derive(Debug, Clone)]
pub struct Recipe {
    /// Stable id shared with Unity (e.g. "stabilizer_t1", "anchor").
    pub id: &'static str,
    pub output: Item,
    pub output_quantity: u16,
    pub ingredients: Vec<Ingredient>,
    /// Crafting time in seconds (0 = instant).
    pub craft_time_secs: f32,
    /// Whether a workbench must be nearby.
    pub requires_workbench: bool,
}

fn ing(item: Item, quantity: u16) -> Ingredient {
    Ingredient {
        item,
        quantity,
        consumed: true,
    }
}

/// All craftable recipes (ARCHITECTURE_V1.md / Task 1.7).
pub fn all_recipes() -> Vec<Recipe> {
    vec![
        Recipe {
            id: "stabilizer_t1",
            output: Item::Stabilizer(StabilizerTier::T1),
            output_quantity: 1,
            ingredients: vec![
                ing(Item::Metal, 10),
                ing(Item::Circuit, 5),
                ing(Item::Battery, 1),
            ],
            craft_time_secs: 0.0,
            requires_workbench: true,
        },
        Recipe {
            id: "stabilizer_t2",
            output: Item::Stabilizer(StabilizerTier::T2),
            output_quantity: 1,
            ingredients: vec![
                ing(Item::Metal, 25),
                ing(Item::Circuit, 15),
                ing(Item::Battery, 3),
                ing(Item::Cable, 10),
            ],
            craft_time_secs: 15.0 * 60.0,
            requires_workbench: true,
        },
        Recipe {
            id: "stabilizer_t3",
            output: Item::Stabilizer(StabilizerTier::T3),
            output_quantity: 1,
            ingredients: vec![
                ing(Item::Metal, 50),
                ing(Item::Circuit, 40),
                ing(Item::Battery, 10),
                ing(Item::Cable, 30),
            ],
            craft_time_secs: 30.0 * 60.0,
            requires_workbench: true,
        },
        Recipe {
            id: "anchor",
            output: Item::Tool, // placeholder output; anchor install handled separately
            output_quantity: 1,
            ingredients: vec![
                ing(Item::Cable, 50),
                Ingredient {
                    item: Item::Tool,
                    quantity: 1,
                    consumed: false,
                },
            ],
            craft_time_secs: 25.0 * 60.0,
            requires_workbench: true,
        },
    ]
}

/// Look up a recipe by its string id.
pub fn find_recipe(id: &str) -> Option<Recipe> {
    all_recipes().into_iter().find(|r| r.id == id)
}
