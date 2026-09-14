//! ADR-149 R2: el cuerpo por zonas del jugador, con la autoridad donde ya vive la salud (el backend propio).
//!
//! Espejo exacto de `Assets/Scripts/Gameplay/Body/BodyZones.cs` y `BodyState.cs`: mismas 15 zonas (índice estable, APPEND-ONLY),
//! mismas lesiones, mismo hash del sorteo y mismas cifras. La fuente común es `docs/data/body-zones.json`; un test de cada lado
//! lo compara, así que un cambio en un solo lado rompe su suite.

use serde::{Deserialize, Serialize};

pub const ZONE_COUNT: usize = 15;

pub const MIN_WOUND_DAMAGE: f32 = 8.0;
pub const CUT_DAMAGE: f32 = 15.0;
pub const FRACTURE_FALL_DAMAGE: f32 = 25.0;
pub const FRACTURE_BLUNT_DAMAGE: f32 = 30.0;
pub const SCRATCH_HEAL_SECONDS: f32 = 60.0;
pub const BANDAGED_HEAL_SECONDS: f32 = 180.0;
pub const SPLINTED_FRACTURE_HEAL_SECONDS: f32 = 600.0;
pub const BLEED_PER_SECOND: f32 = 0.15;
pub const FRACTURE_SPEED: f32 = 0.55;
pub const SPLINTED_FRACTURE_SPEED: f32 = 0.8;

pub const INJURY_NONE: u8 = 0;
pub const INJURY_SCRATCH: u8 = 1;
pub const INJURY_CUT: u8 = 2;
pub const INJURY_FRACTURE: u8 = 3;

const INJURY_MASK: u8 = 0b0000_0111;
const BANDAGED_BIT: u8 = 1 << 3;
const SPLINTED_BIT: u8 = 1 << 4;

const HEAD: usize = 0;
const CHEST: usize = 1;
const UPPER_ARM_L: usize = 3;
const THIGH_L: usize = 9;

/// Pesos sobre 100 por zona. Caída: pies y espinillas. Genérico: el tronco más, las puntas menos. Robapieles: el zarpazo
/// frontal llega a brazos, pecho y cabeza.
const FALL_WEIGHTS: [u8; ZONE_COUNT] = [0, 0, 0, 0, 0, 0, 0, 5, 5, 5, 5, 20, 20, 20, 20];
const GENERIC_WEIGHTS: [u8; ZONE_COUNT] = [8, 20, 16, 6, 6, 6, 6, 4, 4, 6, 6, 4, 4, 2, 2];
const PHANTOM_HIT_WEIGHTS: [u8; ZONE_COUNT] = [20, 30, 0, 13, 12, 13, 12, 0, 0, 0, 0, 0, 0, 0, 0];

/// Causa del daño, leída del `DamageType` del vendor que manda el cliente (`report_damage.cause`) o fijada por el servidor.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DamageCause {
    Fall,
    Blunt,
    NoWound,
    PhantomHit,
    Other,
}

impl DamageCause {
    pub fn from_client(cause: &str) -> Self {
        match cause {
            "Fall" => Self::Fall,
            "Blunt" => Self::Blunt,
            "Poison" | "Radiation" => Self::NoWound,
            _ => Self::Other,
        }
    }
}

pub fn weights_for(cause: DamageCause) -> &'static [u8; ZONE_COUNT] {
    match cause {
        DamageCause::Fall => &FALL_WEIGHTS,
        DamageCause::PhantomHit => &PHANTOM_HIT_WEIGHTS,
        _ => &GENERIC_WEIGHTS,
    }
}

fn hash(mut x: u32) -> u32 {
    x ^= x >> 16;
    x = x.wrapping_mul(0x7feb_352d);
    x ^= x >> 15;
    x = x.wrapping_mul(0x846c_a68b);
    x ^= x >> 16;
    x
}

/// Sorteo por causa (ADR-149 D2 c), determinista: misma semilla, misma zona, igual que `BodyZoneResolver.Draw` en C#.
pub fn draw_zone(cause: DamageCause, seed: u32) -> usize {
    let weights = weights_for(cause);
    let mut roll = hash(seed) % 100;
    for (i, &w) in weights.iter().enumerate() {
        let w = u32::from(w);
        if roll < w {
            return i;
        }
        roll -= w;
    }
    CHEST
}

pub fn is_leg(zone: usize) -> bool {
    zone >= THIGH_L
}

pub fn is_limb(zone: usize) -> bool {
    zone >= UPPER_ARM_L
}

/// La lesión que abre un golpe, pura (`BodyState.InjuryFor`).
pub fn injury_for(damage: f32, cause: DamageCause, zone: usize) -> u8 {
    if !(damage >= MIN_WOUND_DAMAGE) || cause == DamageCause::NoWound {
        return INJURY_NONE;
    }
    if cause == DamageCause::Fall && is_leg(zone) && damage >= FRACTURE_FALL_DAMAGE {
        return INJURY_FRACTURE;
    }
    if cause == DamageCause::Blunt && is_limb(zone) && damage >= FRACTURE_BLUNT_DAMAGE {
        return INJURY_FRACTURE;
    }
    if damage >= CUT_DAMAGE {
        INJURY_CUT
    } else {
        INJURY_SCRATCH
    }
}

/// Tratamientos de la primera rebanada (`BodyTreatment`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Treatment {
    Bandage = 1,
    Splint = 2,
}

impl Treatment {
    pub fn from_u8(value: u8) -> Option<Self> {
        match value {
            1 => Some(Self::Bandage),
            2 => Some(Self::Splint),
            _ => None,
        }
    }
}

/// Una zona no sana, tal como se guarda (ADR-149, tabla de impacto: `PlayerSnapshot.body`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct BodyZoneSave {
    pub zone: u8,
    pub state: u8,
    /// Segundos de curación acumulados, redondeados.
    pub heal_s: u16,
}

/// El cuerpo del jugador: un byte por zona (lesión 3 bits + venda + férula) y su temporizador de curación.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
pub struct BodyState {
    zones: [u8; ZONE_COUNT],
    heal: [f32; ZONE_COUNT],
    /// Cambió algo desde el último `take_dirty`: toca mandar `body_state` al cliente.
    #[serde(skip)]
    dirty: bool,
}

impl BodyState {
    pub fn raw(&self) -> [u8; ZONE_COUNT] {
        self.zones
    }

    pub fn injury_of(&self, zone: usize) -> u8 {
        self.zones[zone] & INJURY_MASK
    }

    pub fn is_bandaged(&self, zone: usize) -> bool {
        self.zones[zone] & BANDAGED_BIT != 0
    }

    pub fn is_splinted(&self, zone: usize) -> bool {
        self.zones[zone] & SPLINTED_BIT != 0
    }

    pub fn is_bleeding(&self) -> bool {
        (0..ZONE_COUNT).any(|z| self.injury_of(z) == INJURY_CUT && !self.is_bandaged(z))
    }

    /// Una lesión menor que la que hay no cambia nada; una igual o peor la sustituye y quita venda y férula.
    pub fn apply_damage(&mut self, zone: usize, damage: f32, cause: DamageCause) -> u8 {
        if zone >= ZONE_COUNT {
            return INJURY_NONE;
        }
        let injury = injury_for(damage, cause, zone);
        if injury == INJURY_NONE || injury < self.injury_of(zone) {
            return INJURY_NONE;
        }
        self.set(zone, injury);
        injury
    }

    pub fn can_treat(&self, zone: usize, treatment: Treatment) -> bool {
        if zone >= ZONE_COUNT {
            return false;
        }
        let injury = self.injury_of(zone);
        match treatment {
            Treatment::Bandage => {
                (injury == INJURY_SCRATCH || injury == INJURY_CUT) && !self.is_bandaged(zone)
            }
            Treatment::Splint => injury == INJURY_FRACTURE && !self.is_splinted(zone),
        }
    }

    pub fn treat(&mut self, zone: usize, treatment: Treatment) -> bool {
        if !self.can_treat(zone, treatment) {
            return false;
        }
        let bit = match treatment {
            Treatment::Bandage => BANDAGED_BIT,
            Treatment::Splint => SPLINTED_BIT,
        };
        self.set(zone, self.zones[zone] | bit);
        true
    }

    /// Avanza la curación. Devuelve la salud que se pierde por sangrado en este paso.
    pub fn tick(&mut self, dt: f32) -> f32 {
        let mut bleed = 0.0;
        for zone in 0..ZONE_COUNT {
            match self.injury_of(zone) {
                INJURY_SCRATCH => {
                    let limit = if self.is_bandaged(zone) {
                        BANDAGED_HEAL_SECONDS
                    } else {
                        SCRATCH_HEAL_SECONDS
                    };
                    self.heal_step(zone, limit, dt);
                }
                INJURY_CUT => {
                    if self.is_bandaged(zone) {
                        self.heal_step(zone, BANDAGED_HEAL_SECONDS, dt);
                    } else {
                        bleed += BLEED_PER_SECOND * dt;
                    }
                }
                INJURY_FRACTURE => {
                    if self.is_splinted(zone) {
                        self.heal_step(zone, SPLINTED_FRACTURE_HEAL_SECONDS, dt);
                    }
                }
                _ => {}
            }
        }
        bleed
    }

    pub fn leg_speed_multiplier(&self) -> f32 {
        let mut speed: f32 = 1.0;
        for zone in THIGH_L..ZONE_COUNT {
            if self.injury_of(zone) == INJURY_FRACTURE {
                let s = if self.is_splinted(zone) {
                    SPLINTED_FRACTURE_SPEED
                } else {
                    FRACTURE_SPEED
                };
                speed = speed.min(s);
            }
        }
        speed
    }

    pub fn clear(&mut self) {
        for zone in 0..ZONE_COUNT {
            if self.zones[zone] != 0 {
                self.set(zone, 0);
            }
        }
    }

    /// Consume la marca de cambio: `true` si hay que avisar al cliente.
    pub fn take_dirty(&mut self) -> bool {
        std::mem::take(&mut self.dirty)
    }

    /// Solo las zonas no sanas, en orden de zona (determinista).
    pub fn to_save(&self) -> Vec<BodyZoneSave> {
        (0..ZONE_COUNT)
            .filter(|&z| self.zones[z] != 0)
            .map(|z| BodyZoneSave {
                zone: z as u8,
                state: self.zones[z],
                heal_s: self.heal[z].round().clamp(0.0, 65535.0) as u16,
            })
            .collect()
    }

    /// Restaura desde el guardado; ignora zonas fuera de rango y bits de lesión desconocidos. Marca para avisar al cliente.
    pub fn from_save(saved: &[BodyZoneSave]) -> Self {
        let mut body = Self::default();
        for entry in saved {
            let zone = usize::from(entry.zone);
            if zone >= ZONE_COUNT || entry.state & INJURY_MASK > INJURY_FRACTURE {
                continue;
            }
            body.zones[zone] = entry.state & (INJURY_MASK | BANDAGED_BIT | SPLINTED_BIT);
            body.heal[zone] = f32::from(entry.heal_s);
        }
        body.dirty = true;
        body
    }

    fn heal_step(&mut self, zone: usize, limit: f32, dt: f32) {
        self.heal[zone] += dt;
        if self.heal[zone] >= limit {
            self.set(zone, 0);
        }
    }

    fn set(&mut self, zone: usize, value: u8) {
        self.heal[zone] = 0.0;
        if self.zones[zone] == value {
            return;
        }
        self.zones[zone] = value;
        self.dirty = true;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const ORACLE: &str = include_str!("../../../docs/data/body-zones.json");

    fn oracle() -> serde_json::Value {
        serde_json::from_str(ORACLE).expect("body-zones.json no es JSON válido")
    }

    fn f(v: &serde_json::Value, key: &str) -> f32 {
        v[key]
            .as_f64()
            .unwrap_or_else(|| panic!("falta {key} en el oráculo")) as f32
    }

    #[test]
    fn the_constants_match_the_shared_oracle() {
        let o = oracle();
        assert_eq!(o["zone_count"].as_u64(), Some(ZONE_COUNT as u64));
        assert_eq!(f(&o, "min_wound_damage"), MIN_WOUND_DAMAGE);
        assert_eq!(f(&o, "cut_damage"), CUT_DAMAGE);
        assert_eq!(f(&o, "fracture_fall_damage"), FRACTURE_FALL_DAMAGE);
        assert_eq!(f(&o, "fracture_blunt_damage"), FRACTURE_BLUNT_DAMAGE);
        assert_eq!(f(&o, "scratch_heal_seconds"), SCRATCH_HEAL_SECONDS);
        assert_eq!(f(&o, "bandaged_heal_seconds"), BANDAGED_HEAL_SECONDS);
        assert_eq!(
            f(&o, "splinted_fracture_heal_seconds"),
            SPLINTED_FRACTURE_HEAL_SECONDS
        );
        assert_eq!(f(&o, "bleed_per_second"), BLEED_PER_SECOND);
        assert_eq!(f(&o, "fracture_speed"), FRACTURE_SPEED);
        assert_eq!(f(&o, "splinted_fracture_speed"), SPLINTED_FRACTURE_SPEED);
        for (name, cause) in [
            ("fall", DamageCause::Fall),
            ("generic", DamageCause::Other),
            ("phantom_hit", DamageCause::PhantomHit),
        ] {
            let expected: Vec<u8> = o["weights"][name]
                .as_array()
                .unwrap()
                .iter()
                .map(|w| w.as_u64().unwrap() as u8)
                .collect();
            assert_eq!(
                expected.as_slice(),
                weights_for(cause).as_slice(),
                "pesos de {name}"
            );
        }
    }

    #[test]
    fn the_draws_and_injuries_match_the_shared_goldens() {
        let o = oracle();
        for d in o["draws"].as_array().unwrap() {
            let cause = match d["table"].as_str().unwrap() {
                "fall" => DamageCause::Fall,
                "phantom_hit" => DamageCause::PhantomHit,
                _ => DamageCause::Other,
            };
            let seed = d["seed"].as_u64().unwrap() as u32;
            assert_eq!(
                draw_zone(cause, seed) as u64,
                d["zone"].as_u64().unwrap(),
                "sorteo {d}"
            );
        }
        for c in o["injuries"].as_array().unwrap() {
            let cause = DamageCause::from_client(c["cause"].as_str().unwrap());
            let got = injury_for(f(c, "damage"), cause, c["zone"].as_u64().unwrap() as usize);
            assert_eq!(u64::from(got), c["injury"].as_u64().unwrap(), "lesión {c}");
        }
    }

    #[test]
    fn a_bandaged_cut_stops_bleeding_and_heals() {
        let mut body = BodyState::default();
        assert_eq!(body.apply_damage(5, 16.0, DamageCause::Other), INJURY_CUT);
        assert!(body.take_dirty());
        assert!(body.is_bleeding());
        assert!((body.tick(2.0) - BLEED_PER_SECOND * 2.0).abs() < 1e-5);
        assert_eq!(
            body.apply_damage(5, 10.0, DamageCause::Other),
            INJURY_NONE,
            "un rasguño no tapa un corte"
        );
        assert!(!body.treat(5, Treatment::Splint));
        assert!(body.treat(5, Treatment::Bandage));
        assert!(!body.treat(5, Treatment::Bandage));
        assert_eq!(body.tick(1.0), 0.0);
        body.tick(BANDAGED_HEAL_SECONDS);
        assert_eq!(body.injury_of(5), INJURY_NONE);
    }

    #[test]
    fn a_fracture_slows_and_a_splint_helps() {
        let mut body = BodyState::default();
        body.apply_damage(12, 30.0, DamageCause::Fall);
        assert_eq!(body.leg_speed_multiplier(), FRACTURE_SPEED);
        assert!(body.treat(12, Treatment::Splint));
        assert_eq!(body.leg_speed_multiplier(), SPLINTED_FRACTURE_SPEED);
        body.apply_damage(12, 30.0, DamageCause::Fall);
        assert!(!body.is_splinted(12), "otro golpe igual quita la férula");
        body.clear();
        assert_eq!(body.leg_speed_multiplier(), 1.0);
        assert!(body.raw().iter().all(|&z| z == 0));
    }

    #[test]
    fn out_of_range_zones_and_non_wounds_do_nothing() {
        let mut body = BodyState::default();
        assert_eq!(body.apply_damage(15, 50.0, DamageCause::Other), INJURY_NONE);
        assert_eq!(
            body.apply_damage(HEAD, f32::NAN, DamageCause::Other),
            INJURY_NONE
        );
        assert_eq!(
            body.apply_damage(HEAD, 50.0, DamageCause::NoWound),
            INJURY_NONE
        );
        assert!(!body.take_dirty());
    }

    #[test]
    fn the_save_keeps_only_hurt_zones_and_round_trips() {
        let mut body = BodyState::default();
        body.apply_damage(13, 16.0, DamageCause::Other);
        body.treat(13, Treatment::Bandage);
        body.tick(40.4);
        body.apply_damage(11, 30.0, DamageCause::Fall);
        let saved = body.to_save();
        assert_eq!(saved.len(), 2);
        assert_eq!(saved[0].zone, 11, "en orden de zona");
        assert_eq!(saved[1].heal_s, 40);

        let json = serde_json::to_string(&saved).unwrap();
        let back: Vec<BodyZoneSave> = serde_json::from_str(&json).unwrap();
        let mut restored = BodyState::from_save(&back);
        assert!(restored.take_dirty(), "restaurar avisa al cliente");
        assert_eq!(restored.raw(), body.raw());
        let bogus = [
            BodyZoneSave {
                zone: 99,
                state: 2,
                heal_s: 0,
            },
            BodyZoneSave {
                zone: 1,
                state: 7,
                heal_s: 0,
            },
        ];
        assert!(
            BodyState::from_save(&bogus).raw().iter().all(|&z| z == 0),
            "lo desconocido no entra"
        );
    }
}
