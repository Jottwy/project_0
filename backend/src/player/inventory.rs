//! Inventory, items, and item stacks.
//! See ARCHITECTURE_V1.md §11.1 and CLAUDE_CODE_INSTRUCTIONS.md Task 1.7.

use serde::{Deserialize, Serialize};

/// Number of inventory slots (4x5 grid in the Unity UI).
pub const SLOT_COUNT: usize = 20;
/// Maximum stack size per slot.
pub const MAX_STACK: u16 = 99;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum StabilizerTier {
    T1,
    T2,
    T3,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", content = "tier", rename_all = "snake_case")]
pub enum Item {
    Metal,
    Circuit,
    Battery,
    Cable,
    Food,
    Water,
    Medicine,
    Tool, // Reusable crafting tool (not consumed)
    Stabilizer(StabilizerTier),
}

impl Item {
    /// Stable string id shared with the Unity client (see IPC schema).
    pub fn type_name(&self) -> &'static str {
        match self {
            Item::Metal => "metal",
            Item::Circuit => "circuit",
            Item::Battery => "battery",
            Item::Cable => "cable",
            Item::Food => "food",
            Item::Water => "water",
            Item::Medicine => "medicine",
            Item::Tool => "tool",
            Item::Stabilizer(StabilizerTier::T1) => "stabilizer_t1",
            Item::Stabilizer(StabilizerTier::T2) => "stabilizer_t2",
            Item::Stabilizer(StabilizerTier::T3) => "stabilizer_t3",
        }
    }
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
pub struct ItemStack {
    pub item: Item,
    pub quantity: u16,
}

impl ItemStack {
    pub fn new(item: Item, quantity: u16) -> Self {
        Self { item, quantity }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Inventory {
    pub slots: Vec<Option<ItemStack>>,
}

impl Default for Inventory {
    fn default() -> Self {
        Self {
            slots: vec![None; SLOT_COUNT],
        }
    }
}

impl Inventory {
    pub fn new() -> Self {
        Self::default()
    }

    /// Total quantity of a given item across all slots.
    pub fn count(&self, item: Item) -> u32 {
        self.slots
            .iter()
            .filter_map(|s| s.as_ref())
            .filter(|s| s.item == item)
            .map(|s| s.quantity as u32)
            .sum()
    }

    /// Add items, stacking into existing slots first, then filling empties.
    /// Returns the quantity that did not fit.
    pub fn add(&mut self, item: Item, mut quantity: u16) -> u16 {
        // Top up existing stacks.
        for slot in self.slots.iter_mut() {
            if quantity == 0 {
                break;
            }
            if let Some(stack) = slot {
                if stack.item == item && stack.quantity < MAX_STACK {
                    let space = MAX_STACK - stack.quantity;
                    let moved = space.min(quantity);
                    stack.quantity += moved;
                    quantity -= moved;
                }
            }
        }
        // Fill empty slots.
        for slot in self.slots.iter_mut() {
            if quantity == 0 {
                break;
            }
            if slot.is_none() {
                let moved = quantity.min(MAX_STACK);
                *slot = Some(ItemStack::new(item, moved));
                quantity -= moved;
            }
        }
        quantity
    }

    /// Remove up to `quantity` of `item`. Returns how many were actually removed.
    pub fn remove(&mut self, item: Item, mut quantity: u16) -> u16 {
        let mut removed = 0;
        for slot in self.slots.iter_mut() {
            if quantity == 0 {
                break;
            }
            if let Some(stack) = slot {
                if stack.item == item {
                    let take = stack.quantity.min(quantity);
                    stack.quantity -= take;
                    quantity -= take;
                    removed += take;
                    if stack.quantity == 0 {
                        *slot = None;
                    }
                }
            }
        }
        removed
    }
}
