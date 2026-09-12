//! ADR-064 enm. 1 (E1.3/E1.6) — la tabla de recetas por `item_id` de STP que consulta
//! `"craft_item"`.
//!
//! Espejo del `CraftingData` de cada `ItemDefinition` craftable del catálogo de Unity
//! (`IsCraftable` = blueprint no vacío y `_craftAmount > 0`). El ORÁCULO común de las dos puntas
//! es `docs/data/crafting-recipes.json`: el test de EditMode lo regenera desde los assets y este
//! módulo lo lee en su test; si esta tabla y el JSON discrepan, `cargo test` cae. La tabla es a
//! mano, y no un parse del JSON en arranque, a propósito: un JSON roto no debe tumbar el servidor,
//! sólo la suite.
//!
//! Los ids son los `DataIdReference` REALES de los assets (pueden ser negativos), nunca el enum
//! `Item` heredado — ADR-064 lo congela en su papel de drops del mundo y prohíbe extenderlo.

/// Un ingrediente: id de `ItemDefinition` y unidades que consume UNA ejecución de la receta.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Ingredient {
    pub item_id: i32,
    pub count: u16,
}

/// Una receta: cuántas unidades produce una ejecución y qué consume.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Recipe {
    pub item_id: i32,
    /// `_craftAmount` de STP. Siempre ≥ 1 aquí: una receta con 0 no es craftable y no entra.
    pub amount: u16,
    pub ingredients: &'static [Ingredient],
}

const fn ing(item_id: i32, count: u16) -> Ingredient {
    Ingredient { item_id, count }
}

// Ids de materiales que se repiten, con nombre para que la tabla se lea.
const STICK: i32 = 9878845;
const CLOTH: i32 = 8505358;
const ROPE: i32 = -20407;
const FEATHER: i32 = -9821754;
const METAL_SHARD: i32 = 1760821921;
const STONE_SHARD: i32 = -2172927;
const MEDICINAL_CORN: i32 = -2044372056;

/// Ordenada por `item_id`, igual que el JSON.
pub const RECIPES: &[Recipe] = &[
    Recipe {
        item_id: -1351326566, // STP_Metal Arrow
        amount: 5,
        ingredients: &[ing(FEATHER, 1), ing(STICK, 1), ing(METAL_SHARD, 1)],
    },
    Recipe {
        item_id: -1159981804, // STP_Steel Pickaxe
        amount: 1,
        ingredients: &[ing(METAL_SHARD, 5), ing(STICK, 5), ing(ROPE, 2)],
    },
    Recipe {
        item_id: -1114026992, // BR_Bandage — ADR-064 enm. 1 E1.7, la primera receta propia
        amount: 1,
        ingredients: &[ing(CLOTH, 2)],
    },
    Recipe {
        item_id: -270636157, // STP_Stone Arrow
        amount: 5,
        ingredients: &[ing(FEATHER, 1), ing(STICK, 1), ing(STONE_SHARD, 1)],
    },
    Recipe {
        item_id: -7892144, // STP_Wooden Bow
        amount: 1,
        ingredients: &[ing(ROPE, 2), ing(STICK, 2), ing(CLOTH, 1)],
    },
    Recipe {
        item_id: -7174886, // STP_Antibiotics
        amount: 1,
        ingredients: &[ing(MEDICINAL_CORN, 3)],
    },
    Recipe {
        item_id: -5283776, // STP_Wooden Torch
        amount: 1,
        ingredients: &[ing(STICK, 4), ing(CLOTH, 2)],
    },
    Recipe {
        item_id: -52379, // STP_Wooden Spear
        amount: 1,
        ingredients: &[ing(STICK, 5)],
    },
    Recipe {
        item_id: -20407, // STP_Rope
        amount: 1,
        ingredients: &[ing(CLOTH, 2)],
    },
    Recipe {
        item_id: 2211292, // STP_Hunting Axe
        amount: 1,
        ingredients: &[ing(METAL_SHARD, 5), ing(STICK, 5), ing(ROPE, 2)],
    },
    Recipe {
        item_id: 4548792, // STP_Wooden Arrow
        amount: 5,
        ingredients: &[ing(FEATHER, 1), ing(STICK, 1)],
    },
    Recipe {
        item_id: 5085425, // STP_Stone Spear
        amount: 1,
        ingredients: &[ing(STONE_SHARD, 5), ing(STICK, 5), ing(ROPE, 2)],
    },
];

/// La receta de un `item_id`, o `None` si el servidor no la conoce (`unknown_recipe`).
pub fn crafting_spec(item_id: i32) -> Option<&'static Recipe> {
    RECIPES.iter().find(|r| r.item_id == item_id)
}

#[cfg(test)]
mod tests {
    use super::*;

    // El oráculo se embebe en compilación: la suite no depende del cwd desde el que corre cargo.
    const ORACLE: &str = include_str!("../../../docs/data/crafting-recipes.json");

    #[test]
    fn the_table_mirrors_the_shared_json_oracle_exactly() {
        let doc: serde_json::Value = serde_json::from_str(ORACLE).expect("JSON válido");
        let recipes = doc["recipes"].as_array().expect("campo recipes");
        assert_eq!(
            recipes.len(),
            RECIPES.len(),
            "el JSON tiene {} recetas y la tabla {}",
            recipes.len(),
            RECIPES.len()
        );
        for (json, rust) in recipes.iter().zip(RECIPES) {
            let name = json["name"].as_str().unwrap_or("?");
            assert_eq!(
                json["item_id"].as_i64().unwrap() as i32,
                rust.item_id,
                "{name}: item_id"
            );
            assert_eq!(
                json["amount"].as_u64().unwrap() as u16,
                rust.amount,
                "{name}: amount"
            );
            let ings: Vec<Ingredient> = json["ingredients"]
                .as_array()
                .unwrap()
                .iter()
                .map(|i| {
                    ing(
                        i["item_id"].as_i64().unwrap() as i32,
                        i["count"].as_u64().unwrap() as u16,
                    )
                })
                .collect();
            assert_eq!(ings.as_slice(), rust.ingredients, "{name}: ingredientes");
        }
    }

    #[test]
    fn the_table_is_sorted_by_item_id_and_every_recipe_is_craftable() {
        for w in RECIPES.windows(2) {
            assert!(
                w[0].item_id < w[1].item_id,
                "desordenada en {}",
                w[1].item_id
            );
        }
        for r in RECIPES {
            assert!(
                r.amount >= 1 && !r.ingredients.is_empty(),
                "{} no es craftable",
                r.item_id
            );
        }
    }

    #[test]
    fn the_bandage_costs_two_cloth() {
        let r = crafting_spec(-1114026992).expect("la venda está en la tabla");
        assert_eq!(r.amount, 1);
        assert_eq!(r.ingredients, &[ing(CLOTH, 2)]);
        assert!(crafting_spec(0).is_none());
    }
}
