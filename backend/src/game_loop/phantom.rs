//! ADR-016 / 038 / 040 / 041 / 043 / 047 / 048 — the robapieles (`phantom`): the host-only AI that
//! walks the fake peers, hunts them, hears for them and lets them strike.
//!
//! Lifted VERBATIM out of `game_loop.rs`. The module is private to `game_loop`; `pub(super)`
//! reproduces exactly the access these items had while they shared one module, including what
//! `game_loop::tests` needs.
//!
//! The game loop consumes a minimal surface: `PhantomDriver::{new, add, rebind_unbound_victims,
//! sync_population, wake_for_noises, step}` and the `&[PhantomAttack]` that `step` returns.
//! Everything else is internal.

use super::*;

/// ADR-016 slice 2: phantom walk speed (m/s). Calibrated to human walking so the client's
/// velocity-derived locomotion (ADR-013) reads it as walking, not teleporting — the proxy
/// teleport guards only trip on chunk-displacement-scale jumps, far above this. Calibrable.
pub(super) const PHANTOM_WALK_SPEED: f32 = 3.0;
/// ADR-016 slice 2: on a head-on block the phantom re-orients by a random turn in
/// [90°, 270°] — never straight back into the wall, never a no-op — so it never stalls and
/// wanders the maze (no smart pathing yet).
pub(super) const PHANTOM_TURN_MIN: f32 = std::f32::consts::FRAC_PI_2;
pub(super) const PHANTOM_TURN_MAX: f32 = std::f32::consts::PI * 1.5;
/// Initial heading (yaw, radians) of a freshly driven phantom: +X (east of the host spawn).
pub(super) const PHANTOM_INITIAL_HEADING: f32 = std::f32::consts::FRAC_PI_2;
/// ADR-016 slice 4: how long the phantom holds the FAKED "pickup" animation. Aligned with
/// ADR-011's ~1s trigger-flank window (the real gesture's duration is client-owned, via the
/// Animator exitTime). Movement is paused for this window so it reads like a player that is
/// input-locked while picking up. PURE presentation — no real pickup state is ever touched.
pub(super) const PHANTOM_PICKUP_GESTURE: Duration = Duration::from_millis(1000);
/// ADR-016 slice 4: cooldown between faked pickups while patrolling (theater — no item needed).
///
/// ADR-050 point 15: 6 s was one gesture every six seconds, each freezing the creature for a
/// full second. Between this, the stare tell and the organic pauses, WANDER spent more than half
/// its time standing still, which reads as a broken AI rather than as a player pottering about.
/// The gesture is also gated on somebody being near enough to SEE it (`PHANTOM_THEATRE_RANGE`):
/// miming for an empty corridor is cost with no audience.
pub(super) const PHANTOM_PICKUP_INTERVAL: Duration = Duration::from_secs(25);
/// ADR-050: how close a real player has to be for the fake-pickup theatre to be worth performing.
/// Wider than the sight radius on purpose — it should already be miming when you round the corner,
/// not start the instant it notices you.
pub(super) const PHANTOM_THEATRE_RANGE: f32 = 30.0;
/// ADR-016 (tell phase): behavioral tell #2 — the phantom periodically goes UNNATURALLY STILL
/// (a "stare"): it stops dead, holding idle + a fixed facing for `PHANTOM_STARE_DURATION`, on a
/// near-perfect `PHANTOM_STARE_INTERVAL` cadence. Subtle (rare, brief) but learnable — humans
/// don't freeze like a metronome. Calibrable: raise the interval / lower the duration to make it
/// subtler. PURELY BEHAVIORAL — no wire flag; observable only by watching, never by reading packets.
pub(super) const PHANTOM_STARE_INTERVAL: Duration = Duration::from_secs(30);
pub(super) const PHANTOM_STARE_DURATION: Duration = Duration::from_millis(2500);
/// ADR-016 — TERRITORY: how far (m) from its anchor a patrolling creature will wander before its
/// heading is re-aimed home. Only acts in WANDER; the hunting states ignore it entirely.
///
/// ADR-050 point 14: this was 9 m and declared TEMPORARY in its own comment — a play-test crutch
/// so the creature stayed on screen, conditioned on the world→backend collision migration landing.
/// That migration landed three ADRs ago (018 collision, 026 player spawn, 040 navigation), and at
/// 9 m the creatures did not patrol, they orbited: a ring of sentries pinned to their spawn cells.
/// 45 m is a territory it can actually walk — big enough to leave and come back to a corridor,
/// small enough that a creature drawn for a block stays roughly in that block.
pub(super) const PHANTOM_WANDER_RADIUS: f32 = 45.0;

// ADR-016 slice 2 — detection + chase. D1=(a): distance + a forward view cone ONLY, with NO
// geometry line-of-sight. The phantom's collision is against the BACKEND world, not the
// rendered ChunkStreamer, so a geometric raycast would test the WRONG walls; until the
// world→backend migration, distance+angle is the honest prototype. Enter chase within
// DETECT_RADIUS while the target is inside the cone; leave past LOSE_RADIUS (hysteresis so it
// doesn't flicker at the edge). Chase moves faster than the wander walk.
pub(super) const PHANTOM_DETECT_RADIUS: f32 = 15.0;
pub(super) const PHANTOM_LOSE_RADIUS: f32 = 25.0;
pub(super) const PHANTOM_DETECT_HALF_FOV: f32 = std::f32::consts::FRAC_PI_3; // 60° half → 120° cone

/// ADR-080 point 2 — A LIT TORCH IS SEEN FROM FURTHER AWAY. `light_on` has ridden the pose relay
/// since ADR-042 and the perception model never consulted it: carrying the brightest object in the
/// game had no cost. A candidate holding a lit wieldable inside the SAME 120° cone, with the same
/// thin line of sight the eye uses, is detected out to this radius instead of the bare 15 m.
///
/// The crouch multiplier deliberately does NOT apply: crouching does not put out a torch. Turning
/// the light off is the counterplay, and that is the whole point.
pub(super) const PHANTOM_LIGHT_DETECT_RADIUS: f32 = 38.0;

/// ADR-080 point 1 — head-tracking clamp, in degrees. `pitch` is quantized to `i8` and the client
/// (`ProxyPitchHook`) spreads anything past ~30° into a spine lean, so a full ±90° would fold the
/// proxy in half when the target stands right under it. ±60 keeps the stare readable.
pub(super) const PHANTOM_LOOK_PITCH_CLAMP_DEG: f32 = 60.0;

/// ADR-080 point 4 — degrees the stalking band swings towards the target's back per replan while
/// the creature sits inside its victim's view cone. Small on purpose: an instant teleport to the
/// blind side reads as cheating, whereas drifting out of view reads as a thing that knows it is
/// being watched.
pub(super) const PHANTOM_FLANK_STEP_DEG: f32 = 22.0;

/// ADR-080 point 4 — how far inside the target's own view cone counts as "being looked at" for the
/// flanking decision. Wider than the kill cone: the point is to leave the field of view entirely,
/// not to hug its edge.
pub(super) const PHANTOM_FLANK_TRIGGER_HALF_FOV_DEG: f32 = 75.0;

/// ADR-092 — how far from where it gave up the creature goes to lie in wait (m), and how long it
/// waits there before coming back. The spot has to be OUT OF SIGHT of where it last saw you — the
/// whole trick is that you looked, saw nothing, and relaxed.
pub(super) const PHANTOM_LURK_MIN_DISTANCE: f32 = 12.0;
pub(super) const PHANTOM_LURK_MAX_DISTANCE: f32 = 20.0;
pub(super) const PHANTOM_LURK_MIN_SECONDS: f32 = 6.0;
pub(super) const PHANTOM_LURK_MAX_SECONDS: f32 = 14.0;
/// ADR-092 — how often a lost hunt ends in a feint instead of a shrug. A hunter usually does; the
/// rest mostly do not. Never always: a rule is learnt exactly like "it's gone" was.
pub(super) const PHANTOM_LURK_CHANCE_HUNTER: f32 = 0.6;
pub(super) const PHANTOM_LURK_CHANCE: f32 = 0.25;
/// ADR-092 — arrival radius at the lurk spot.
pub(super) const PHANTOM_LURK_ARRIVE: f32 = 1.2;

/// ADR-091 — `buttons` bit 0 is "aiming" (ADR-044, `RemoteButtons.Aiming`). Mirrored here rather
/// than imported: the driver reads the pose, it does not own the bit layout.
pub(super) const PHANTOM_AIM_BIT: u16 = 1 << 0;
/// ADR-091 — how close the target has to be looking at the creature for the aim to count. Much
/// narrower than the STATUE cone: pointing a weapon at something means pointing AT it.
pub(super) const PHANTOM_JUKE_LOOK_HALF_FOV: f32 = 12.0_f32 * std::f32::consts::PI / 180.0;
/// ADR-091 — the band in which being aimed at earns a sidestep. Closer and the strike is what
/// matters; further and a dodge at that range is theatre nobody can see.
pub(super) const PHANTOM_JUKE_MIN: f32 = 4.0;
pub(super) const PHANTOM_JUKE_MAX: f32 = 14.0;
/// ADR-091 — how long (s) the sidestep holds, how far (rad) it deflects, and how long (s) before it
/// may happen again. 0.4 s at 7 m/s is under three metres sideways — enough to spoil a shot, not
/// enough to stop the charge. The cooldown is what keeps this from becoming un-hittable: ADR-041's
/// "the weapon resolves the encounter" still has to be true, just not free.
pub(super) const PHANTOM_JUKE_SECONDS: f32 = 0.4;
pub(super) const PHANTOM_JUKE_DEFLECT: f32 = 70.0_f32 * std::f32::consts::PI / 180.0;
pub(super) const PHANTOM_JUKE_COOLDOWN: f32 = 2.0;
/// ADR-091 — a non-hunter only sometimes bothers; a hunter always does. Scaled by `impulse()` like
/// every other temperament roll so the erratic ones dodge more.
pub(super) const PHANTOM_JUKE_CHANCE: f32 = 0.5;

/// ADR-089 — how far from YOUR claim marker a creature that lost you will still go and check it
/// (same layer only). Past this, the walk is longer than the hunt and the ordinary give-up is right.
pub(super) const PHANTOM_HOME_RECALL_RADIUS: f32 = 120.0;
/// ADR-089 — a wandering creature this close to ANY claim marker on its layer gravitates towards it.
pub(super) const PHANTOM_HOME_PATROL_RADIUS: f32 = 80.0;
/// ADR-089 — the ring it patrols around a marker (m): close enough to be at your door, far enough
/// that it reads as prowling rather than as camping the spawn.
pub(super) const PHANTOM_HOME_RING_MIN: f32 = 25.0;
pub(super) const PHANTOM_HOME_RING_MAX: f32 = 40.0;
/// ADR-089 — probability that a wandering creature's NEXT heading (after a pause) aims at the ring
/// instead of being random. Half, not always: a base that reliably pulls every creature in range
/// becomes a visible mechanic, and this is meant to read as something that happens to you.
pub(super) const PHANTOM_HOME_PATROL_BIAS: f32 = 0.5;

/// ADR-088 — the target has to be MOVING for interception to mean anything. Below this the velocity
/// estimate (one tick of position delta) is mostly noise, and aiming ahead of a standing player just
/// aims beside them.
pub(super) const PHANTOM_INTERCEPT_MIN_SPEED: f32 = 1.5;
/// ADR-088 — the most lead (s) the creature will ever take. Your velocity is measured over ONE tick;
/// projecting it five seconds out is a guess, not an intercept. Two seconds reaches the next crossing
/// of a corridor and no further.
pub(super) const PHANTOM_INTERCEPT_MAX_LEAD: f32 = 2.0;

/// ADR-080 point 4 — how close to the flank point counts as having arrived. Generous on purpose:
/// the goal is "out of your view at roughly band distance", not a coordinate, and a tight radius
/// against a 10 Hz step would have it shuffling to hit an exact spot.
pub(super) const PHANTOM_FLANK_ARRIVE: f32 = 1.5;

/// ADR-080 point 3 — cooldown (s) of the missed-swing roar. Its own budget, well under the 6 s
/// global one: the whole point is that it lands on the swing that did NOT connect, and the global
/// cooldown is already spent by the reveal scream that opened the charge.
pub(super) const PHANTOM_MISS_VOCAL_COOLDOWN: f32 = 2.5;

/// ADR-080 point 5 — the band inside which another creature answers a scream. Nearer than 60 m and
/// the "answer" reads as an echo of the same thing; past 220 m nobody would hear it anyway.
pub(super) const PHANTOM_CHORUS_MIN_DISTANCE: f32 = 60.0;
pub(super) const PHANTOM_CHORUS_MAX_DISTANCE: f32 = 220.0;
/// ADR-080 point 5 — the answer is delayed by a deterministic beat in this window, so several
/// creatures never answer in unison (which would read as one big thing, the same trap the staggered
/// breath already avoids).
pub(super) const PHANTOM_CHORUS_DELAY_MIN: f32 = 0.8;
pub(super) const PHANTOM_CHORUS_DELAY_MAX: f32 = 3.5;

// ADR-016 slice 3a — Stalker FSM (Wander / Spotted / Stalk / Sprint). Peek/Search land in 3b.
pub(super) const PHANTOM_STALK_DISTANCE: f32 = 9.0; // STALK keeps roughly this gap from the player
/// ADR-050 point 16 — speed (m/s) used by STALK to CLOSE the gap, as opposed to the walk it uses
/// for everything else.
///
/// STALK used to close at `PHANTOM_WALK_SPEED` (3.0) against STP's 5.5 m/s run, so running in a
/// straight line escaped a stalker every single time and the fase never got to resolve into
/// anything. That 3.0 was inherited from the wander constant, whose comment justifies the value by
/// animation fidelity (the client reads it as walking, ADR-013) and says nothing about pursuit.
/// 4.6 keeps the pressure on without ever catching you: the lunge is still what closes distance.
pub(super) const PHANTOM_STALK_CLOSE_SPEED: f32 = 4.6;
/// ADR-050 point 8 — the gap a SATED creature holds instead of `PHANTOM_STALK_DISTANCE`. It is not
/// hunting you, it is accompanying you, and 9 m reads as being harassed where this reads as being
/// followed. The difference matters because the sated band is the one meant to be unnerving rather
/// than threatening.
pub(super) const PHANTOM_FOLLOW_DISTANCE: f32 = 14.0;
/// ADR-050 point 8 — seconds by which a sated creature lags the pose it is copying.
///
/// The delay IS the effect. A perfect mirror reads as a network bug; something that crouches a beat
/// after you crouch reads as being imitated, which is the single most unsettling thing a thing
/// wearing a stolen face can do.
pub(super) const PHANTOM_MIMIC_DELAY: f32 = 0.8;
/// Top sprint speed (vs walk 3.0). Cut 10 % twice, 9.0 → 8.1 → 7.29, so chases LAST.
///
/// The number that matters is the ratio to the player, not the absolute: STP's run speed is 5.5 m/s
/// (`FPS_Player.prefab` `_forwardSpeed`), so this is now 1.33× (was 1.64×). Outrunning it is still
/// impossible, which is the design — what changes is the CLOSING speed: 3.5 → 1.79 m/s, so a chase
/// starting at 15 m goes from 4.3 s to **8.4 s** of being hunted with it behind you.
///
/// Below ~1.2× this stops being a chase and becomes an escort, so this is close to the floor.
pub(super) const PHANTOM_SPRINT_SPEED: f32 = 7.29;
pub(super) const PHANTOM_SPRINT_RAMP: f32 = 1.5; // seconds to ramp WALK → SPRINT

// ── ADR-050 point 5: THE CHASE HAS A PULSE ───────────────────────────────────────────────────────
//
// A chase from 15 m lasted 8,4 s and was a straight line at a constant speed: it either reached you
// or it did not, and nothing happened in between that you could read or use. Lowering the top speed
// was the obvious fix and is rejected in ADR-050 (A) — at 1.2× the player's run it stops being a
// hunt and becomes an escort. Instead the speed stays and the ENDURANCE is finite: it comes at you
// flat out, blows, has to slow to a heavy walk while it gets its breath back, and comes again.
//
// The same 15 m now takes ~25-30 s and has a shape: it closes, you hear it fail, you buy ground,
// it starts again. That gap is where a door, a corner or a decision fits.
//
// CRITICAL: being winded is NOT an exit from the chase. The two ways out stay exactly what they
// were — outrun it past LOSE_RADIUS, or break its line of sight — because a lunge that ends on a
// timer is what the 2026-08-03 pass deliberately removed.
/// Seconds of flat-out running before it has to breathe.
pub(super) const PHANTOM_SPRINT_BURST_SECONDS: f32 = 5.0;
/// Seconds of heavy-walking recovery before the next burst.
pub(super) const PHANTOM_SPRINT_RECOVER_SECONDS: f32 = 3.0;
/// Speed (m/s) while winded. Below the player's run (5.5) on purpose — this is the window where
/// ground is bought back — but above a walk, so it is still visibly coming.
pub(super) const PHANTOM_SPRINT_WINDED_SPEED: f32 = 3.5;
pub(super) const PHANTOM_SPOTTED_MIN: f32 = 3.0; // SPOTTED stare duration range (s)
pub(super) const PHANTOM_SPOTTED_MAX: f32 = 8.0;
pub(super) const PHANTOM_STALK_PATIENCE: f32 = 25.0; // seconds stalking before it lunges
pub(super) const PHANTOM_WANDER_PAUSE_MIN: f32 = 3.0; // WANDER "looking at a wall" pause range (s)
pub(super) const PHANTOM_WANDER_PAUSE_MAX: f32 = 12.0;
// ADR-050 point 15: 0.007/tick fires every ~14 s for an average 7,5 s pause, i.e. it stood still
// about a third of WANDER on this timer alone, on top of the gesture and the stare. Halved.
pub(super) const PHANTOM_WANDER_PAUSE_CHANCE: f32 = 0.003; // per-tick (≈9% over 3 s at 10 Hz)
pub(super) const PHANTOM_SPRINT_RANDOM_CHANCE: f32 = 0.008; // per-tick unpredictable lunge

// ADR-016 slice 3b-P1 — STATUE (weeping-angel: freezes while observed) + sound detection.
// All inputs come from data the backend already has (player yaw; target speed derived from the
// position delta — peers send no velocity/move_state). NO wire/IPC change.
pub(super) const PHANTOM_STATUE_RANGE: f32 = 20.0; // only freezes if the watching player is within this (m)
pub(super) const PHANTOM_STATUE_LOOK_HALF_FOV: f32 = std::f32::consts::FRAC_PI_6; // 30° half → 60° player cone
/// Wider cone to LEAVE the freeze than to enter it (45° half → 90°). Hysteresis, not a retune: a
/// single edge at 10 Hz made a player standing on the boundary toggle STATUE↔STALK every tick, and
/// each toggle is a full reveal + scream (ADR-038 derives `revealed` from the state).
pub(super) const PHANTOM_STATUE_RELEASE_HALF_FOV: f32 = std::f32::consts::FRAC_PI_4;
/// After releasing a freeze, the creature will not freeze again for this long (s). The cone
/// hysteresis handles jitter; this handles the deliberate look-away-look-back, which otherwise
/// re-armed the whole reveal on demand.
pub(super) const PHANTOM_STATUE_COOLDOWN: f32 = 6.0;
/// How long a lunge stays committed after landing its blow (s), before bouncing back to STALK.
///
/// It used to bounce on the SAME tick, so the real form appeared and vanished around a single
/// frame of contact — the disguise recomposing mid-strike is exactly the "cambia y desvanbio todo
/// el rato" flicker. Now it charges through, still revealed, and cannot strike again inside the
/// window. Longer than `PHANTOM_PICKUP_GESTURE` (1 s) on purpose: the gesture freeze must finish
/// INSIDE the commitment, or the bounce would land the instant the pose unfroze.
pub(super) const PHANTOM_STRIKE_RECOVERY: f32 = 2.5;
/// A hesitating lunge holds still for this long (s) before it actually comes. Short on purpose: it
/// is a beat, not a reprieve, and anything past ~1 s starts reading as the AI having frozen.
pub(super) const PHANTOM_HESITATE_MIN: f32 = 0.3;
pub(super) const PHANTOM_HESITATE_MAX: f32 = 0.85;
/// How far the creature can REACH to strike (m), as opposed to how close its body can travel.
///
/// The two used to be one number (1.5 m) and that is the "pegado a la pared no puede hacer nada"
/// bug: press yourself against geometry and the 0.5 m body cannot occupy the cells that would bring
/// it inside 1.5 m, so it parked at ~2 m and stared forever. ADR-040's straight-line rule fixed the
/// PATHFINDER half of that; this is the other half. An arm has reach, a body has volume — treating
/// "can I get there" and "can I hit you" as the same question is what was wrong.
///
/// Gated on a clear segment, so the extra reach never becomes a strike through a wall.
pub(super) const PHANTOM_ATTACK_REACH: f32 = 2.4;
/// A lunge that has been grinding this many ticks without landing anything gives up and re-stalks
/// (2.5 s at 10 Hz). Belt and braces next to the reach fix: geometry the resolver hates in a way
/// nobody predicted must never leave the creature pinned to a wall forever, which is the state that
/// reads as the game being broken rather than as the creature being bad at doorways.
pub(super) const PHANTOM_SPRINT_GIVEUP_TICKS: u8 = 25;
/// Seconds a lunge will keep coming with NO clear line to its target before it gives up and
/// searches. Halved while the target is crouched.
///
/// This is the hiding mechanic and the number that decides whether cover is worth using. Too short
/// and any doorway shakes it; too long and hiding is pointless. 5 s is long enough that you have to
/// commit to a hiding place rather than jink around a pillar, and short enough that a real corner
/// saves you.
pub(super) const PHANTOM_SPRINT_BLIND_SECONDS: f32 = 5.0;

// ── ADR-048: the creature's voice ────────────────────────────────────────────────────────────────
/// The disguise dropping — emitted on entering SPRINT. Migrated here from the client, which used to
/// infer it from the `revealed` edge: now every client hears it at the same instant instead of each
/// one deducing it from its own reception of the level.
pub(super) const VOCAL_REVEAL: u8 = 0;
/// A shriek while HUNTING A NOISE and closing on somebody it has not seen. This is the one the
/// disguise must survive — it is still wearing a stolen face while it makes it.
pub(super) const VOCAL_SEARCH_SHRIEK: u8 = 1;
/// A grunt of reaction the moment it hears something worth walking toward. Also disguised.
pub(super) const VOCAL_NOISE_GRUNT: u8 = 2;
/// Low, quiet, rhythmic — what a corridor sounds like when it is occupied and the thing in it has
/// not decided yet. Emitted while STALKing, on its own slow timer.
pub(super) const VOCAL_STALK_BREATH: u8 = 3;
/// THE ANSWER. Emitted the instant it hears a shot from FAR away, and played on a curve as wide as
/// the shot itself: you fire, and a second later something enormous answers from out there. It does
/// not tell you where — that is the point. You learn that you are not alone, not where it is.
pub(super) const VOCAL_DISTANT_ANSWER: u8 = 4;
/// After a kill. Falls the whole way, unhurried: it is done with you.
pub(super) const VOCAL_SATED_ROAR: u8 = 5;
/// ADR-050 — THE HUNGRY MOAN. Low, and it means something specific: this one has gone past
/// shadowing you and is deciding when. It is the only reading the player gets of a state that is
/// otherwise pure behaviour, and it is deliberately not a warning of an imminent charge — it says
/// the animal in front of you is now the kind that eats, not that it moves in three seconds.
pub(super) const VOCAL_HUNGRY_MOAN: u8 = 6;
/// ADR-050 — the sound of it running out of breath mid-charge. Does real work: it is how the player
/// learns, without a UI, that the next few seconds are the ones to spend running.
pub(super) const VOCAL_WINDED: u8 = 7;

// ── ADR-051: the skin comes off as an EVENT, not as a level ──────────────────────────────────────
/// How long it stands there screaming before it tears out of the skin.
///
/// This is the tell, and its length is the design: long enough that you have time to understand
/// what is about to happen and start running, short enough that it never becomes a free escape. A
/// creature that unmasked instantly was a jump cut; one that announces it for a beat and a half is
/// a threat you watched arrive.
pub(super) const PHANTOM_UNMASK_SECONDS: f32 = 1.6;
/// ADR-051 — the scream while it comes apart. Its own voice because it is not the reveal scream
/// (`VOCAL_REVEAL`, which now lands a beat later, on the tear itself) — this one is still coming
/// out of something that looks like a player.
pub(super) const VOCAL_UNMASK_SCREAM: u8 = 8;

/// ADR-080 point 3 — the roar of a swing that did NOT land: at arm's length with the blow locked
/// out by `strike_recover`. Until now that frame was silent, so a claw that grazed you and a claw
/// that missed by ten metres sounded identical (which is to say, not at all).
pub(super) const VOCAL_STRIKE_MISS: u8 = 9;

// ── ADR-050 point 9: the grab ────────────────────────────────────────────────────────────────────
/// How long the victim has to break free before the grab becomes a kill.
///
/// 2.5 s is long enough to be a scene you can act inside and short enough that it never becomes a
/// negotiation. The client is told this number rather than holding its own copy, so there is one
/// owner of it (the old `GrabTime` const in `PhantomAttackHandler` was a second, silent one).
pub(super) const PHANTOM_GRAB_SECONDS: f32 = 2.5;
/// Seconds of stalking cooldown a creature eats after being shaken off, so a failed grab cannot
/// immediately become another one. It is still hungry — that is the point — but you get the room to
/// run that surviving is supposed to buy.
pub(super) const PHANTOM_GRAB_RECOVERY: f32 = 4.0;

// ── ADR-050 point 6: the flight response ─────────────────────────────────────────────────────────
/// Seconds a startled creature spends putting distance between itself and whatever made the noise.
pub(super) const PHANTOM_FLEE_SECONDS: f32 = 6.0;
/// Speed (m/s) while bolting. Faster than a walk and slower than a charge: it is leaving, not
/// racing, and it must still be catchable-up-to so the player can see what happened.
pub(super) const PHANTOM_FLEE_SPEED: f32 = 6.0;
/// How far ahead of itself a fleeing creature aims. It steers to a point this far along the vector
/// AWAY from the noise, so the existing navigation carries it around geometry instead of pressing
/// it into the first wall behind it.
pub(super) const PHANTOM_FLEE_LOOKAHEAD: f32 = 25.0;
/// A noise heard from beyond this (m) answers with the long roar instead of the close-up grunt.
/// Under it the creature is near enough that a grunt reads as "it is RIGHT THERE", which is scarier
/// at that range than a roar would be.
pub(super) const PHANTOM_ANSWER_MIN_DISTANCE: f32 = 60.0;

// ── Rage: a gunshot does not just attract it, it angers it ───────────────────────────────────────
/// How long a heard shot keeps a creature enraged (s).
pub(super) const PHANTOM_RAGE_SECONDS: f32 = 45.0;
/// A shot closer than this enrages it MUCH harder — firing next to one is a mistake you feel.
pub(super) const PHANTOM_RAGE_CLOSE_DISTANCE: f32 = 35.0;
/// ADR-050 point 12 — HEARING AND BEING ANGERED ARE NOT THE SAME RADIUS. A rifle carries 500 m
/// (`NoiseReporter`), and rage used to ride that same distance, so ONE trigger pull put every awake
/// creature in the world into a 45-90 s rage with its patience cut to 40 % — a single shot was a
/// server-wide event. Past this, a noise still ATTRACTS (it walks over to look, which is the whole
/// of ADR-041) but does not anger. Close, it does both.
pub(super) const PHANTOM_RAGE_MAX_DISTANCE: f32 = 70.0;
/// Patience multiplier while enraged: it runs out of it more than twice as fast.
pub(super) const PHANTOM_RAGE_PATIENCE: f32 = 0.4;
/// Unpredictable-lunge multiplier while enraged.
pub(super) const PHANTOM_RAGE_IMPULSE: f32 = 2.5;
/// Movement multiplier while enraged. Small on purpose: rage should change what it DECIDES far more
/// than how fast it moves, or the speed tuning below becomes meaningless.
pub(super) const PHANTOM_RAGE_SPEED: f32 = 1.15;
/// ADR-075 point 5 — INSISTING ENFURECES. A provoked flee ADDS rage instead of refreshing it, so a
/// ceiling exists for the same reason `blocked_ticks` needs a saturation policy: without one, a
/// long enough burst would leave the creature enraged for the rest of the session.
pub(super) const PHANTOM_RAGE_CEILING_SECONDS: f32 = 180.0;
/// How long a flee must have been running before a second noise can turn it around. Below this, a
/// second shot from the SAME burst that started the flee would look like it never fled at all —
/// the fear has to read as real before it can be overridden.
pub(super) const PHANTOM_PROVOKE_MIN_FLEE_SECONDS: f32 = 1.0;

// ── ADR-050: HUNGER, the slow state that governs everything else ─────────────────────────────────
//
// This replaces the post-kill `calm_for` timer, which was already half of this idea (60 s of
// docility after eating) but as a one-shot rather than as a cycle. The problem it solves is bigger
// than satiety, though: the creature used to decide whether to charge you with a per-tick dice
// roll, and at 1.6 %/tick against a 25 s patience that roll ended ~98 % of all stalks. Nothing
// about an encounter was legible, because nothing about it was a decision — "it hangs around a bit
// and then attacks" was the literal implementation.
//
// Hunger is host-only and backend-only. It NEVER crosses the wire, not even derived: ADR-016 §1's
// invariant holds, and the player is meant to read it by watching behaviour, not from a HUD.
/// Seconds from fully sated to famished. Long on purpose — this is the creature's day, not a
/// cooldown, and a player should be able to meet the same one twice and find it different.
pub(super) const PHANTOM_HUNGER_DRAIN_SECONDS: f32 = 420.0;
/// Above this it will NOT charge, whatever its patience does. It follows, watches and imitates.
pub(super) const PHANTOM_HUNGER_SATED: f32 = 0.66;
/// Below this it hunts in earnest.
pub(super) const PHANTOM_HUNGER_HUNTING: f32 = 0.33;
/// Patience multiplier while sated: it will shadow you for a very long time before committing.
pub(super) const PHANTOM_CALM_PATIENCE: f32 = 3.0;
/// Unpredictable-lunge multiplier while sated.
pub(super) const PHANTOM_CALM_IMPULSE: f32 = 0.15;
/// Separates the hunger draw from every other consumer of the seed, for the same reason
/// `PHANTOM_TRAIT_SALT` and `PHANTOM_DRAW_SALT` exist: without it, how hungry a creature is would
/// correlate with where it lives, and "the ones by the flooded stair are always starving" is a
/// pattern players would learn long before anyone traced it to a shared hash stream.
pub(super) const PHANTOM_HUNGER_SALT: u64 = 0x4A11_BEEF_5A7E_D000;
/// The breath uses a SHORT shared cooldown: it is ambience, so it must not sit on the budget and
/// swallow the scream of a lunge that starts two seconds later. It still cannot fire DURING one
/// (the shared cooldown is what stops that), which is the asymmetry worth having.
pub(super) const PHANTOM_BREATH_COOLDOWN: f32 = 1.5;
/// Seconds between stalking breaths, randomised per breath inside this band.
pub(super) const PHANTOM_BREATH_MIN: f32 = 7.0;
pub(super) const PHANTOM_BREATH_MAX: f32 = 15.0;
/// Minimum seconds between ANY two vocalisations from the same creature. In the BACKEND on purpose
/// (ADR-048 point 7): a limit living in the client is a limit the client can remove, and the cost
/// of a creature screaming at 10 Hz is paid by everyone who can hear it.
pub(super) const PHANTOM_VOCAL_COOLDOWN: f32 = 6.0;
/// How close a searching creature has to get to a player before it shrieks (m). Wider than the
/// sight radius (15 m) is the point: you hear it coming before it can possibly have seen you.
pub(super) const PHANTOM_VOCAL_SEARCH_RANGE: f32 = 18.0;
pub(super) const PHANTOM_STATUE_MAX: f32 = 6.0; // max seconds frozen → then it lunges (SPRINT)
pub(super) const PHANTOM_RUN_SPEED_THRESHOLD: f32 = 4.5; // target speed (m/s) read as "running" (above walk)
pub(super) const PHANTOM_SOUND_BONUS: f32 = 8.0; // extra detect radius (m) when the player is running
pub(super) const PHANTOM_SPEED_SANITY_MAX: f32 = 30.0; // ignore deltas above this (teleport/chunk-displace)
pub(super) const PHANTOM_SPOTTED_SOUND_MIN: f32 = 1.0; // shorter stare when alerted by noise (s)
pub(super) const PHANTOM_SPOTTED_SOUND_MAX: f32 = 2.0;
// Fluidity (slice 3b-P1 follow-up): ease `heading` toward the player instead of snapping at
// 10 Hz (which reads as lag). rad/s — STALK tracks, SPRINT tracks hard, STATUE turns its head.
// ADR-041 — noise as a stimulus. A gunshot is the loudest thing in the game and until now it did
// not exist for the AI at all.
/// Hard clamp on a reported loudness. The client owns the weapon table, but a forged or buggy value
/// must not turn one shot into a world-wide summons.
pub(super) const PHANTOM_NOISE_MAX_LOUDNESS: f32 = 600.0;
/// Localization error as a fraction of distance. A rifle at 500 m is HEARD — physically it carries
/// for kilometres and a long corridor acts as a waveguide — but it is not LOCATED. At 50 m this
/// lands almost on top of you; at 500 m it lands ~40 m out and the creature has to search. That
/// gap is the whole design: exact positions at long range would be an aimbot with a delay.
pub(super) const PHANTOM_NOISE_ERROR_FRAC: f32 = 0.08;
/// Travel speed toward a noise: a fast walk, NOT a sprint. 500 m at this speed is ~1.9 minutes of
/// dread; covering the same ground at a run would read as homing.
pub(super) const PHANTOM_NOISE_TRAVEL_SPEED: f32 = 4.5;
/// A noise goes cold after this long in transit without a fresh one. Without it the phantom would
/// cross the map chasing a shot fired five minutes ago.
///
/// ADR-050 point 13: this was 90 s, which at 4.5 m/s is 405 m of reach against a rifle audible at
/// 500 m — every creature between those two numbers set off, walked for a minute and a half and
/// gave up short. ADR-041's long approach was not reachable at all. 130 s covers the loudest
/// weapon in the game with margin for the detour a real route takes around geometry.
pub(super) const PHANTOM_NOISE_EXPIRY: f32 = 130.0;
/// Patience once it ARRIVES. The 12 s of a normal SEARCH is far too short after a journey of
/// minutes — arriving and immediately shrugging would waste the whole approach.
pub(super) const PHANTOM_NOISE_SEARCH_PATIENCE: f32 = 30.0;
// ADR-040 perception. Sound stops being a binary "is it sprinting?" and becomes three tiers, which
// is what makes crouching a real choice instead of a cosmetic pose.
/// Below this planar speed you are standing still and make no noise at all.
pub(super) const PHANTOM_WALK_NOISE_SPEED: f32 = 0.6;
/// How far a STANDING walk carries. A run still carries `DETECT + SOUND_BONUS`.
pub(super) const PHANTOM_WALK_HEAR_RADIUS: f32 = 9.0;
/// Crouching shrinks the VISUAL radius to this fraction — it does not make you invisible, it makes
/// you something it has to be close to notice. Combined with the existing 120° cone, crouching
/// behind it is genuinely safe; crouching in front of its face is not.
pub(super) const PHANTOM_CROUCH_SIGHT_FACTOR: f32 = 0.55;
// ADR-040 Fase 4 — SEARCH.
/// How long it hunts around the spot it last saw you before giving up and going back to wandering.
pub(super) const PHANTOM_SEARCH_MAX: f32 = 12.0;
/// It treats the remembered spot as reached inside this radius.
pub(super) const PHANTOM_SEARCH_ARRIVE: f32 = 2.0;
/// Speed while investigating: slower than a walk. It is looking, not commuting.
pub(super) const PHANTOM_SEARCH_SPEED: f32 = 2.2;
// ADR-040 — replan policy. Not tuning knobs pulled from thin air: 0.6 s is ~6 entity ticks, short
// enough that a route stays honest while you move and long enough that the amortized search cost is
// 1.67/s. The drift threshold is under one chunk-cell pair, so the phantom re-routes when you round
// a corner but not when you strafe.
pub(super) const PHANTOM_REPLAN_INTERVAL: f32 = 0.6;
pub(super) const PHANTOM_REPLAN_GOAL_DRIFT: f32 = 4.0;
/// ADR-043 — how many steps the replans of the active movers are spread over, capping the searches
/// in any one step at ceil(N / stride). 3 against `PHANTOM_ACTIVE_CAP` = 6 means at most 2.
///
/// Needed because `PHANTOM_REPLAN_INTERVAL` is a fixed 0.6 s: movers that woke on the same tick
/// keep their `nav_age` in phase forever and all come due together, so the cost is a burst, not
/// the average.
pub(super) const PHANTOM_REPLAN_STRIDE: u64 = 3;
/// A waypoint counts as reached inside this radius. Below ~1 m the phantom can orbit a waypoint it
/// keeps almost-touching; this is under half a cell, so it never skips a corner either.
pub(super) const PHANTOM_WAYPOINT_ARRIVE: f32 = 1.25;

/// El mismo radio, en WG3. **Y no es el mismo número porque la celda no es la misma.**
///
/// El de arriba son 1,25 m «menos de media celda», y esa celda mide 2,5 m. La de WG3 mide **0,5**, así
/// que ese radio son DOS CELDAS Y MEDIA: el robapieles da por alcanzados varios waypoints de golpe,
/// **se salta la esquina**, se lanza contra la pared, el resolutor lo frena, replanifica y lo vuelve a
/// intentar. Visto jugando como andar a tirones, y es exactamente el fallo del que avisa el comentario
/// de arriba — sólo que su «nunca» valía para su celda y no para ésta.
///
/// 0,60 m es poco más de una celda: no se puede quedar parado entre dos, y no llega a saltarse la
/// siguiente. **Es la tercera constante de esta migración que se hereda mal por venir en celdas** —
/// las otras dos fueron la ventana de navegación y la altura de planta. Cuando un número esté escrito
/// en celdas, convertirlo, no copiarlo.
pub(super) const PHANTOM_WAYPOINT_ARRIVE_WG3: f32 = 0.60;
/// Consecutive fully-blocked steps after which a hunting phantom is treated as WEDGED: it force-
/// replans (ignoring the stagger) and stops trusting the straight-line shortcut.
///
/// 3 at 10 Hz = 0.3 s, which is under the reaction time anyone would read as hesitation, and well
/// above the single blocked step a normal wall-slide produces while rounding a corner. Making it 1
/// would throw away a good plan every time the creature grazed geometry.
pub(super) const PHANTOM_BLOCKED_REPLAN_TICKS: u8 = 3;
/// ADR-051 follow-up — how many ticks the pathfinder KEEPS the wheel after the creature stops
/// being wedged.
///
/// Play-test, first session: `blocked_ticks` in `stalk_move` reached **255** (a saturated `u8`, so
/// twenty-five seconds of getting nowhere) and the sprint gave up wedged 17 times out of 38. The
/// cause is an oscillation, not a wall. `segment_is_clear` tests a LINE with no body radius while
/// the resolver moves a 0.5 m body, so against an inside corner: the line reads clear → the plan is
/// thrown away → the straight bearing walks into the corner → three blocked ticks → the pathfinder
/// takes over → one good step un-wedges it → **straight back to the line that put it there**.
///
/// Holding the route for a moment after recovering breaks the cycle: it gets far enough round the
/// corner that the shortcut is genuinely clear next time it is offered. That is what reads as
/// running THROUGH a corridor instead of hunting for the middle of it.
pub(super) const PHANTOM_WEDGE_HYSTERESIS_TICKS: u8 = 12;
/// Consecutive blocked steps after which a STALKING creature stops trying to hold its band and
/// goes looking for another way round. SPRINT has had this since ADR-040
/// (`PHANTOM_SPRINT_GIVEUP_TICKS`); STALK never did, which is exactly how a creature reached 255.
pub(super) const PHANTOM_STALK_GIVEUP_TICKS: u8 = 40;

// ── ADR-075 point 2 — the hunt PRESSES ──────────────────────────────────────────────────────────
/// HUNTING closes to this distance between lunges instead of holding the 9 m stalk band. Above the
/// strike reach (2.4 m) on purpose: the re-lunge is still the only thing that hits, but the
/// recovery no longer looks like a creature politely waiting at conversation distance — which was
/// the play-test complaint, verbatim.
pub(super) const PHANTOM_HUNT_PRESS_DISTANCE: f32 = 4.0;
/// Search speed after LOSING A HUNT (the noise channel keeps its own 4.5). Faster than the idle
/// investigation amble: it just had you, and 2.2 m/s read as instant indifference.
pub(super) const PHANTOM_SEARCH_HUNT_SPEED: f32 = 3.2;
/// Hunger scaling of the post-hunt search patience: base × (1 + (1 − hunger) × THIS), so a
/// starving creature searches up to 2.5× longer. Capped at `PHANTOM_NOISE_SEARCH_PATIENCE`.
pub(super) const PHANTOM_SEARCH_HUNGER_BONUS: f32 = 1.5;
/// Rage multiplier on the same post-hunt patience, under the same cap.
pub(super) const PHANTOM_SEARCH_RAGE_MULT: f32 = 1.5;
/// ADR-075 point 2c — anti-camping creep. After the target has been effectively still this long,
/// a non-sated stalker stops honouring the full band…
pub(super) const PHANTOM_CREEP_AFTER_SECONDS: f32 = 6.0;
/// …and shrinks it at this rate (metres per still second)…
pub(super) const PHANTOM_CREEP_RATE: f32 = 0.5;
/// …down to this floor. STATUE still wins when the player turns to look, so what the creep
/// punishes is camping with your back turned, not standing still as such.
pub(super) const PHANTOM_CREEP_MIN_BAND: f32 = 4.0;

// ── ADR-075 point 3 — predictive tracking ───────────────────────────────────────────────────────
/// How far ahead a lost target's last observed velocity is projected before SEARCH is even
/// entered. Long enough to land somewhere you would plausibly have reached by the time the
/// creature arrives, short enough that a turn you made after being seen is not chased forever.
pub(super) const PHANTOM_SEARCH_LEAD_SECONDS: f32 = 2.5;
/// Below this speed, projecting is noise, not prediction — a nearly-stationary target gets no
/// lead point at all.
pub(super) const PHANTOM_SEARCH_LEAD_MIN_SPEED: f32 = 1.0;

// ── ADR-075 point 4 — Peek ──────────────────────────────────────────────────────────────────────
/// SEARCH pauses to look, once, when the goal is this close and hidden around a corner — the
/// piece of the design that was declared but never built (see the enum doc on `Peek`).
pub(super) const PHANTOM_PEEK_TRIGGER_DISTANCE: f32 = 6.0;
pub(super) const PHANTOM_PEEK_MIN: f32 = 1.2;
pub(super) const PHANTOM_PEEK_MAX: f32 = 2.0;

// ── ADR-076 — the disguised ambush and the knockdown it lands ──────────────────────────────────
/// A running player's pace, deliberately short of `PHANTOM_SPRINT_SPEED` (7.29): until the blow
/// lands this has to look like a person, not a monster.
pub(super) const PHANTOM_AMBUSH_SPEED: f32 = 5.5;
/// Ambush distance window. Below the floor there is no room to build up "someone is running at
/// you" before contact; beyond the ceiling the run itself would take long enough to become its
/// own tell.
pub(super) const PHANTOM_AMBUSH_MIN_DISTANCE: f32 = 5.0;
pub(super) const PHANTOM_AMBUSH_MAX_DISTANCE: f32 = 16.0;
/// Per-tick chance, scaled by `impulse()` exactly like the ordinary lunge roll — hunger, rage and
/// temperament all weigh in on whether THIS moment is the one it tries the trick.
pub(super) const PHANTOM_AMBUSH_CHANCE: f32 = 0.02;
/// Cooldown after a FAILED ambush (lost, wedged, or timed out) before another may be attempted.
pub(super) const PHANTOM_AMBUSH_COOLDOWN: f32 = 30.0;
/// A run that has not connected or failed by this many seconds gives up on its own.
pub(super) const PHANTOM_AMBUSH_MAX_SECONDS: f32 = 6.0;
/// How long the knockdown holds the player down (s). Carried in `Knockdown`'s `damage`-shaped
/// field, same trick `GrabStart` already uses.
pub(super) const PHANTOM_KNOCKDOWN_SECONDS: f32 = 2.0;
/// Shove speed on connect (m/s), same carrier as `Knockback`.
pub(super) const PHANTOM_KNOCKDOWN_FORCE: f32 = 6.0;

// ── ADR-053: it gives your own voice back ────────────────────────────────────────────────────────
//
// It already steals the name, the face and the posture. The voice was the one thing left, and it
// costs almost nothing because the whole path exists: the host relays voice, `send_unreliable_as`
// can stamp a frame with somebody else's id, and the client plays whatever a proxy emits at that
// proxy's position. So the creature says your own words back at you, from wherever it is standing.
//
// The backend never decodes a byte of it — no codec, no interpretation, no storage beyond one
// scrap that the next thing you say overwrites.
/// Seconds between echoes from one creature, randomised inside the band. Long: this must land as
/// "…did I just hear myself?" and not as a parrot. Anything frequent turns it into a mechanic the
/// player calibrates against instead of a thing that happens to them.
pub(super) const PHANTOM_ECHO_MIN: f32 = 45.0;
pub(super) const PHANTOM_ECHO_MAX: f32 = 110.0;
/// It only does this while DRESSED and at a distance — see `maybe_echo_voice`.
pub(super) const PHANTOM_ECHO_MIN_DISTANCE: f32 = 12.0;

// ── ADR-053: it remembers where you hid ──────────────────────────────────────────────────────────
//
// The cheap, legible half of "let it learn". No model and no training: a creature keeps a short
// list of the places a hunt of ITS OWN ended, and checks them before giving up next time. That is
// the whole mechanism, and it produces the thing people actually mean by a smart monster — the
// second time you use the same corner, it looks there first.
//
// It is per-creature and lives on the mover, so it dies when that creature is retired and it is
// never shared: the one that lives by the flooded stair learns your habits, the one three blocks
// over does not. That asymmetry is the point.
/// How many hiding places one creature carries. Four, not forty: a creature that remembers
/// everywhere you have ever been stops being a hunter and becomes a checklist, and the search would
/// never end.
pub(super) const PHANTOM_HIDEOUT_MEMORY: usize = 4;
/// How far from a search's own goal a remembered spot may be and still be worth a detour (m).
/// Beyond this it is a memory of a different part of the level and chasing it would read as random.
pub(super) const PHANTOM_HIDEOUT_RECALL_RADIUS: f32 = 40.0;
/// Two remembered spots checked per hunt, at most. This is the difference between "it knows your
/// hiding places" and "it sweeps the building", and only the first one is frightening.
pub(super) const PHANTOM_HIDEOUT_CHECKS_PER_HUNT: u8 = 2;
/// A new hiding place closer than this to one already remembered just refreshes it. Without it the
/// four slots fill with four corners of the same room.
pub(super) const PHANTOM_HIDEOUT_MERGE_RADIUS: f32 = 6.0;
/// Fraction of the intended step a creature must actually gain, along the direction it meant to go,
/// for the step to count as progress. Below this it is grinding, not travelling.
///
/// 0.25 and not something near 1.0 because a legitimate wall-slide while rounding a corner does
/// give up most of its forward component for a tick or two; the failure being detected is sustained
/// near-zero progress, not a single scraped step.
pub(super) const PHANTOM_MIN_STEP_PROGRESS: f32 = 0.25;
pub(super) const PHANTOM_TURN_SPEED_STALK: f32 = 8.0;
pub(super) const PHANTOM_TURN_SPEED_SPRINT: f32 = 15.0;
pub(super) const PHANTOM_TURN_SPEED_STATUE: f32 = 3.0;
// ADR-016 slice 1 (phantom damage) — host-only (joiners = Fase 7 debt). Damage flows through the
// PhantomAttack channel, NEVER the pickup path (ADR-016 invariant).
pub(super) const PHANTOM_ATTACK_DAMAGE: f32 = 35.0; // frontal SPRINT hit (non-lethal; bounces to STALK)
/// ADR-050 point 11 — the cone that decides a FRONTAL blow (survivable) from one taken from behind
/// (lethal). Its own constant, and wider than what it replaced.
///
/// This used to reuse `PHANTOM_STATUE_LOOK_HALF_FOV` (±30°), and that reuse was a coincidence of
/// implementation rather than a decision: no ADR ever justified that width for this question. The
/// consequence was a 31° turn of the head separating "you take 35 and live" from "you die with no
/// warning". Freezing when watched and being caught facing away are different questions and now
/// have different numbers.
pub(super) const PHANTOM_KILL_CONE_HALF_FOV: f32 = std::f32::consts::FRAC_PI_3; // 60° half → 120°
pub(super) const PHANTOM_KNOCKBACK_RANGE: f32 = 3.0; // STATUE→SPRINT shove only within this (m)
pub(super) const PHANTOM_KNOCKBACK_FORCE: f32 = 3.0; // shove speed (m/s); client applies via SetVelocity

// ─── ADR-043 — population: which of the world's robapieles are actually simulated ───
/// How often the active population is reconciled with the draw. 1 Hz, not the 10 Hz entity tick:
/// the set of nearby blocks changes at walking pace (a 200 m block takes ~40 s to cross), so ten
/// scans a second would repeat the same answer nine times.
pub(super) const PHANTOM_POPULATION_SYNC_INTERVAL: f32 = 1.0;
/// A drawn phantom wakes up when a real player is within this of it (m).
pub(super) const PHANTOM_ACTIVATE_RADIUS: f32 = 150.0;
/// …and only sleeps again past THIS (m). The gap is hysteresis, and it is not decoration: with a
/// single threshold, a player standing near it would spawn and despawn the same creature every
/// second, which on the client is an avatar blinking in and out at the edge of view distance.
pub(super) const PHANTOM_DEACTIVATE_RADIUS: f32 = 200.0;
/// ADR-050 — NOTHING WAKES UP THIS CLOSE TO A PLAYER (m).
///
/// Reported from play-test: "cuando me salgo y vuelvo a entrar en la partida las entidades
/// spawnean en mi cara". The activation scan only had an upper bound, so on the first tick of a
/// session — when no creature is awake yet and the whole neighbourhood is eligible at once —
/// anything the draw had anchored a few metres from where you happen to reconnect materialised
/// on top of you. Mid-session it is rarer but the same hole: walk into a fresh block and its
/// creature can pop in at arm's length.
///
/// A creature ALREADY awake is unaffected; this gates waking only, so it can still walk right up
/// to you. What it stops is one appearing there out of nothing.
pub(super) const PHANTOM_MIN_SPAWN_DISTANCE: f32 = 35.0;
/// Hard cap on simultaneously simulated phantoms. Nearest to a player wins when it binds.
///
/// Sized against the chunk cache, not guessed: `GRID_CACHE_MAX_CHUNKS` (256) holds ~16 movers'
/// A\* working sets, so this leaves better than 2× headroom. Raising it past that headroom returns
/// the mutual-eviction thrash the cache cap was raised to remove — the two constants are one
/// decision (see `GRID_CACHE_MAX_CHUNKS`).
pub(super) const PHANTOM_ACTIVE_CAP: usize = 6;
/// ADR-047 D5 — how many sleeping robapieles ONE noise may wake, on top of the global
/// `PHANTOM_ACTIVE_CAP`. Two, not "everything in earshot": a 500 m gunshot sweeps ~785.000 m², so
/// an uncapped wake would summon a crowd from a single trigger pull and spend the step budget
/// ADR-043 measured. Two is enough for the noise to mean something and few enough that the worst
/// case of a shot stays a number you can state.
pub(super) const PHANTOM_NOISE_ACTIVATE_MAX: usize = 2;

// ─── ADR-016: phantom driver — movement (2) + faked pickup (4) + victim identity + tell ───

/// ADR-016 (identity phase): pick the name the phantom (robapieles) impersonates. The victim is
/// the first REAL (non-phantom) connected peer; its name is cloned (the phantom keeps its OWN
/// unique id — the id mismatch is the intended subtle tell #1). Returns `(name, bound)`: `bound`
/// is true when a real victim was found, false → host-name fallback (solo), which
/// `rebind_unbound_victims` later upgrades to a real peer once one connects.
///
/// ADR-043 — `slot` spreads the victims across the real peers instead of every creature wearing
/// the same face. It used to be a bare `.find()` over `peers`, which was harmless with one phantom
/// and self-defeating with a populated world: two of them in sight at once, both called the same
/// thing, and the whole point of ADR-016's disguise evaporates. With more phantoms than players
/// names still repeat — unavoidable, and fine, because they are spread across the map.
///
/// The list comes from `real_peer_names` (id-ordered) rather than from iterating `peers` directly,
/// because `HashMap` order is arbitrary and would re-cast everyone from tick to tick.
pub(super) fn choose_victim_name_for(net: &NetworkManager, slot: usize) -> (String, bool) {
    let names = net.real_peer_names();
    match names.is_empty() {
        true => (net.local_name.clone(), false),
        false => (names[slot % names.len()].clone(), true),
    }
}

/// ADR-050 — how full a creature is when the world first simulates it, DERIVED and never rolled.
///
/// Same discipline as `PhantomTraits::derive` and for the same reason: two players who meet this
/// creature must meet it at the same point of its cycle, and one that despawns because everybody
/// walked away and comes back later has to still be itself rather than a fresh roll. A hand-placed
/// debug phantom has no anchor, so it falls back to its id.
///
/// Spread over the WHOLE range, not centred: the population must contain animals at every point of
/// the cycle at once. Seeding them all near one value would give the world synchronised "feeding
/// hours", where every creature everywhere turns dangerous at the same time.
pub(super) fn derive_hunger(world_seed: u64, anchor: Option<PhantomAnchor>, id: PeerId) -> f32 {
    let key = match anchor {
        Some(((bx, bz), layer, index)) => {
            ((bx as i64 as u64) << 40)
                ^ ((bz as i64 as u64) << 16)
                ^ ((layer as u64) << 8)
                ^ index as u64
        }
        None => id as u64,
    };
    let mut z = world_seed ^ PHANTOM_HUNGER_SALT ^ key.wrapping_mul(0x9E37_79B9_7F4A_7C15);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^= z >> 31;
    (z >> 11) as f32 / (1u64 << 53) as f32
}

/// ADR-016 — nearest REAL target (non-phantom) to `from` in XZ: the host's own local player
/// (`host_player_pos`/`host_player_rot`, keyed by `net.local_id`) plus every real peer. Returns
/// `(id, position, distance, yaw_deg)` of the closest. The `id` lets the caller look up the
/// target's derived speed (sound detection, slice 3b-P1) and the `yaw` lets it test whether the
/// player is looking back (STATUE). The host player is always a candidate → `Some` in practice.
/// ADR-040 perception: is this target crouching? Remote peers carry it in `PeerConnection.crouch`
/// (relayed since ADR-020, so no wire work was needed for stealth); the host's own player is not a
/// peer, so its value is handed into `step`.
pub(super) fn target_is_crouched(net: &NetworkManager, tid: PeerId, host_crouch: bool) -> bool {
    if tid == net.local_id {
        return host_crouch;
    }
    net.peers.get(&tid).is_some_and(|p| p.crouch)
}

/// ADR-091 — is this target AIMING a weapon? Same split as `target_is_crouched`: peers carry the
/// bit in `PeerConnection.buttons` (ADR-044), the host's own player is handed in.
pub(super) fn target_is_aiming(net: &NetworkManager, tid: PeerId, host_buttons: u16) -> bool {
    let buttons = match tid == net.local_id {
        true => host_buttons,
        false => net.peers.get(&tid).map(|p| p.buttons).unwrap_or(0),
    };
    buttons & PHANTOM_AIM_BIT != 0
}

/// ADR-080 point 2 — is this target carrying a LIT wieldable? Exact twin of `target_is_crouched`,
/// down to why the host needs a separate channel: `light_on` has been relayed per peer since
/// ADR-042, but the host's own player is not a peer.
pub(super) fn target_has_light_on(net: &NetworkManager, tid: PeerId, host_light_on: bool) -> bool {
    if tid == net.local_id {
        return host_light_on;
    }
    net.peers.get(&tid).is_some_and(|p| p.light_on)
}

/// ADR-041 — displace a heard position by an error that grows with distance, DETERMINISTICALLY.
///
/// Deterministic and not per-tick random on purpose: a wandering estimate would make the phantom
/// zigzag toward a point that keeps moving, which looks like a bug rather than like uncertainty.
/// The same shot always resolves to the same spot for the same phantom.
pub(super) fn blur_noise(source: Vec3, dist: f32, id: PeerId) -> Vec3 {
    let radius = dist * PHANTOM_NOISE_ERROR_FRAC;
    if radius <= 0.01 {
        return source;
    }
    let mut h = (source.x as i32 as u32).wrapping_mul(0x9E37_79B9)
        ^ (source.z as i32 as u32).wrapping_mul(0x85EB_CA6B)
        ^ (id as u32).wrapping_mul(0xC2B2_AE35);
    h ^= h >> 15;
    h = h.wrapping_mul(0x2545_F491);
    h ^= h >> 13;
    let angle = (h as f32 / u32::MAX as f32) * std::f32::consts::TAU;
    Vec3::new(
        source.x + angle.cos() * radius,
        source.y,
        source.z + angle.sin() * radius,
    )
}

/// A DEAD player is not a target. `dead` is relayed for peers (ADR-028 post-E3) and the host's own
/// flag is handed into `step`, for the same reason `crouch` is: the local player is not a peer.
///
/// Without this the creature kept hunting a corpse — and because `sync_population` only ever retires
/// a phantom in WANDER, one locked onto a dead player stayed anchored over the body, freezing and
/// lunging at it, until that player respawned. The damage router already skipped the dead victim
/// (so nothing was ever applied), which is exactly why the bug was invisible in the logs and visible
/// on screen: the blows were dropped one layer BELOW the behaviour that produced them.
///
/// Returns `None` when nobody is alive. Every FSM branch already handles a missing target (WANDER
/// stops detecting, the hunt states fall to SEARCH/WANDER), so losing the target reads as "it lost
/// you" — it walks to where it killed you and gives up. No new state, no special case.
/// Separates the personality draw from every other consumer of the world seed, for the same reason
/// `PHANTOM_DRAW_SALT` exists: without it a creature's temperament would correlate with WHERE it
/// lives, and "the ones in the pillar halls are always the aggressive ones" is a pattern players
/// would learn long before anyone traced it to a shared hash stream.
pub(super) const PHANTOM_TRAIT_SALT: u64 = 0x5EED_C0DE_B0DA_1701;

/// Per-creature temperament. Every robapieles used to run on the same five constants, so a
/// populated world was N copies of one animal: identical stare, identical patience, identical
/// trigger. These multipliers make one a predator that commits almost instantly and the next a
/// thing that trails you down a corridor without ever deciding.
///
/// DERIVED FROM THE SEED AND THE ANCHOR, never rolled at spawn. That is what keeps it consistent
/// with everything else about a drawn phantom (`phantom_spawn`): two players meet the SAME
/// character, and one that despawns and comes back is still itself rather than a re-roll. A
/// hand-placed debug phantom has no anchor, so it falls back to its id.
///
/// The ranges straddle 1.0 on purpose — this is VARIANCE, not a difficulty knob. Widening them
/// makes encounters more varied; shifting their centre is what would make the game harder.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(super) struct PhantomTraits {
    /// Scales the SPOTTED stare. Low = notices you and moves on it almost at once.
    pub(super) spotted_scale: f32,
    /// Scales STALK patience. Low = runs out of patience fast.
    pub(super) patience_scale: f32,
    /// Scales the unpredictable-lunge roll. High = erratic, lunges for no reason.
    pub(super) impulse_scale: f32,
    /// Scales how long it will hold a STATUE freeze before giving up on the game.
    pub(super) statue_scale: f32,
    /// Chance that a lunge opens with a beat of stillness instead of leaving immediately.
    pub(super) hesitate_chance: f32,
    /// A HUNTER. Roughly one in eight, and fixed for that creature forever, so the danger of a place
    /// is learnable: the thing that lives by the flooded stair is always the one that does not wait.
    /// It barely stalks, never freezes to play the statue game, and does not hesitate.
    pub(super) is_hunter: bool,
}

impl PhantomTraits {
    pub(super) fn derive(world_seed: u64, anchor: Option<PhantomAnchor>, id: PeerId) -> Self {
        let key = match anchor {
            Some(((bx, bz), layer, index)) => {
                ((bx as i64 as u64) << 40)
                    ^ ((bz as i64 as u64) << 16)
                    ^ ((layer as u64) << 8)
                    ^ index as u64
            }
            None => id as u64,
        };
        // splitmix64 finaliser: cheap, and it decorrelates the low bits, which matters because the
        // five traits below are read from five different 16-bit slices of the SAME word.
        let mut z = world_seed ^ PHANTOM_TRAIT_SALT ^ key.wrapping_mul(0x9E37_79B9_7F4A_7C15);
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^= z >> 31;

        let unit = |shift: u32| ((z >> shift) & 0xFFFF) as f32 / 65535.0;
        let span = |shift: u32, lo: f32, hi: f32| lo + unit(shift) * (hi - lo);
        // Every range is centred on 1.0 (midpoint = mean for a uniform draw). Asserted, because
        // getting this wrong silently retunes the whole game: the first version had impulse at
        // 0.30..2.20, whose mean is 1.25, i.e. every creature in the world 25 % twitchier than the
        // constant says — a difficulty change disguised as variance.
        Self {
            spotted_scale: span(0, 0.45, 1.55),
            patience_scale: span(13, 0.40, 1.60),
            impulse_scale: span(26, 0.30, 1.70),
            statue_scale: span(39, 0.60, 1.40),
            hesitate_chance: span(48, 0.0, 0.55),
            // Its own bit slice, so making a creature a hunter does not also drag its four scales
            // toward one end — the two axes have to stay independent or "hunter" would just mean
            // "the aggressive tail of the distribution" and the variety would collapse.
            is_hunter: unit(9) < 0.125,
        }
    }
}

/// How much closer a NEW candidate must be before a creature abandons the one it is already on.
///
/// Recomputing "nearest" every tick with no commitment is what made two players a problem: standing
/// at similar range, the target flipped between them at 10 Hz, so the heading jittered,
/// `last_known_player_pos` jumped back and forth and the A* plan was thrown away every single tick.
/// It also made "it has chosen YOU" unreadable, which is most of the tension in a chase.
pub(super) const PHANTOM_TARGET_SWITCH_MARGIN: f32 = 0.7;
/// Distance penalty (m) applied per OTHER creature already hunting a candidate, when acquiring or
/// switching. Not a hard rule — a player who is genuinely much closer still gets picked — but with
/// six creatures and two players it is the difference between 3/3 and everything on one person.
pub(super) const PHANTOM_CROWDING_PENALTY: f32 = 12.0;

/// Pick who this creature hunts: the nearest living player, but STICKY, and biased away from
/// players other creatures are already on.
///
/// `current` is who it hunted last tick. It keeps that target unless the target is gone/dead or
/// another is closer by more than `PHANTOM_TARGET_SWITCH_MARGIN` — the true distance is used for
/// the incumbent so a creature never talks itself out of the player it is standing next to.
///
/// `pursuers` counts how many OTHER creatures are on each id; it only ever penalises CANDIDATES, so
/// it can break a tie at acquisition without ever yanking a committed hunter off its prey.
pub(super) fn choose_target(
    net: &NetworkManager,
    host_player_pos: Vec3,
    host_player_rot: f32,
    host_player_dead: bool,
    from: Vec3,
    current: Option<PeerId>,
    pursuers: &HashMap<PeerId, usize>,
) -> Option<(PeerId, Vec3, f32, f32)> {
    let mut best: Option<(PeerId, Vec3, f32, f32)> = None;
    let mut best_score = f32::INFINITY;

    let mut consider = |id: PeerId, pos: Vec3, rot: f32| {
        let d = from.distance_xz(pos);
        let is_current = current == Some(id);
        // The incumbent is scored on raw distance (no crowding penalty, no margin): it already has
        // this player, so the question is only whether somebody else is CLEARLY better.
        let score = match is_current {
            true => d,
            false => {
                d + PHANTOM_TARGET_SWITCH_MARGIN.mul_add(
                    current.is_some() as i32 as f32,
                    PHANTOM_CROWDING_PENALTY * pursuers.get(&id).copied().unwrap_or(0) as f32,
                )
            }
        };
        if score < best_score {
            best_score = score;
            best = Some((id, pos, d, rot));
        }
    };

    if !host_player_dead {
        consider(net.local_id, host_player_pos, host_player_rot);
    }
    for p in net.peers.values() {
        if net.phantom_ids.contains(&p.id) || p.dead {
            continue;
        }
        consider(p.id, Vec3::from_array(p.position), p.rotation);
    }
    best
}

/// ADR-016 slice 3b-P1 (STATUE): is the PHANTOM inside the player's forward HORIZONTAL cone —
/// i.e. is the player looking at it? `player_yaw` is degrees (Unity yaw, 0 = +Z). Pitch is not
/// available per-peer (and is discarded for the host), so this is the horizontal cone only:
/// looking up/down does not count. No geometry occlusion (consistent with D1=(a)).
pub(super) fn player_is_looking_at(player_pos: Vec3, player_yaw: f32, phantom_pos: Vec3) -> bool {
    player_is_looking_at_within(
        player_pos,
        player_yaw,
        phantom_pos,
        PHANTOM_STATUE_LOOK_HALF_FOV,
    )
}

/// `player_is_looking_at` with an explicit cone, so ENTERING and LEAVING the freeze can use
/// different ones.
///
/// A single hard edge at 10 Hz is a flicker generator: standing where the creature sits exactly on
/// the cone boundary, the smallest mouse drift toggled STATUE↔STALK every tick — and since
/// `revealed` is derived from the state (ADR-038), every one of those toggles was a full reveal,
/// disguise-drop and SCREAM. Widening the release cone means you have to look meaningfully AWAY to
/// release it, not merely jitter.
pub(super) fn player_is_looking_at_within(
    player_pos: Vec3,
    player_yaw: f32,
    phantom_pos: Vec3,
    half_fov: f32,
) -> bool {
    let dx = phantom_pos.x - player_pos.x;
    let dz = phantom_pos.z - player_pos.z;
    let len = (dx * dx + dz * dz).sqrt();
    if len < f32::EPSILON {
        return true; // on top of each other → counts as looked-at
    }
    let yaw = player_yaw.to_radians();
    // Player forward unit dir (Unity yaw): (sin, cos). dot with the unit to-phantom vector.
    let dot = (yaw.sin() * dx + yaw.cos() * dz) / len;
    dot >= half_fov.cos()
}

/// ADR-016 (fluidity): angularly ease `current` heading toward `target` (both yaw radians) by
/// factor `t` in [0,1], via normalize-lerp of the unit direction vectors (nlerp — naturally takes
/// the shorter arc, no angle-wrap special-casing). Returns the blended yaw. Smooths the phantom's
/// turn-to-face so it tracks the player without a 10 Hz snap.
pub(super) fn lerp_heading(current: f32, target: f32, t: f32) -> f32 {
    let t = t.clamp(0.0, 1.0);
    // dir = (sin(yaw), cos(yaw)) — the Unity yaw convention used throughout the driver.
    let mx = current.sin() * (1.0 - t) + target.sin() * t;
    let mz = current.cos() * (1.0 - t) + target.cos() * t;
    let len = (mx * mx + mz * mz).sqrt();
    if len > 0.001 {
        mx.atan2(mz).rem_euclid(std::f32::consts::TAU)
    } else {
        current // ~diametrically opposite at t≈0.5 → keep current; next tick resolves it
    }
}

/// ADR-016 slice 2: is `target` inside the phantom's forward view cone (heading ± HALF_FOV)?
/// Distance is checked by the caller; this is angle ONLY, with no geometry occlusion (D1=(a)).
pub(super) fn in_view_cone(heading: f32, from: Vec3, target: Vec3) -> bool {
    let tx = target.x - from.x;
    let tz = target.z - from.z;
    let len = (tx * tx + tz * tz).sqrt();
    if len < f32::EPSILON {
        return true; // target is on top of the phantom → counts as seen
    }
    // Heading unit dir: yaw 0 = +Z, so dir = (sin, _, cos). dot with the unit to-target vector.
    let dot = (heading.sin() * tx + heading.cos() * tz) / len;
    dot >= PHANTOM_DETECT_HALF_FOV.cos()
}

/// ADR-016 slice 3a — the robapieles' behavioral FSM. Drives how it relates to the nearest real
/// player. PURELY BEHAVIORAL — no wire flag; the state is observable only by watching, never by
/// reading packets. As of ADR-075/076 the corner-peek and the disguised ambush that were once
/// documented as missing are both built (`Peek`, `Ambush`) — see their own doc comments below.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub(super) enum PhantomState {
    /// Erratic patrol: walks its heading, pauses to "look at walls", fakes pickups, does the
    /// metronomic stare tell, and stays near spawn via the observation leash.
    Wander,
    /// Saw the player: freezes and stares straight at them for a randomized window, then STALK.
    Spotted,
    /// Shadows the player at a held distance; its patience runs out into a SPRINT.
    Stalk,
    /// Weeping-angel freeze (slice 3b-P1): the player is looking at it, so it goes dead still
    /// until they look away (→ STALK) or it tires of the game (→ SPRINT). Entered from STALK;
    /// never interrupts a committed SPRINT.
    Statue,
    /// Lunges straight at the player with a ramped speed; "attacks" (anim-only) at point blank.
    Sprint,
    /// ADR-076 — the disguised charge. Entered only from `Stalk`, only while hungry, only when
    /// the player is NOT looking (the same cone STATUE reads, in reverse), and only once per
    /// hunt. Runs at `PHANTOM_AMBUSH_SPEED` — a jogging player's pace, deliberately short of
    /// `Sprint`'s ramp — because until the moment it connects this is meant to read as a person
    /// running toward you, maybe to help. Does NOT reveal (the entire point), and unlike `Peek`
    /// it IS on `hear_noises`' committed-lunge list: this is a charge in progress, not a pause.
    /// Connecting knocks the player down and moves straight into `Unmasking`, still wearing the
    /// skin, exactly as `Statue`'s own timeout does. A failed run costs a cooldown and returns to
    /// `Stalk` — NEVER to `Hunting`, which would reveal what the failure was supposed to hide.
    Ambush,
    /// ADR-040 Fase 4 — lost you, and goes to look where it last saw you instead of forgetting on
    /// the spot. This is the counterweight to the pathfinding: without it, a creature that always
    /// routes optimally toward your exact position is a homing missile. With it, hiding works, and
    /// the tension moves from "can it reach me" to "does it know where I went".
    Search,
    /// ADR-075 point 4 — the corner-peek. `Search` was documented from the day it shipped as
    /// missing exactly this: closing on its goal with no straight line to it (a wall between the
    /// creature and the spot), it stops and looks before committing round the bend — breathing,
    /// facing the corner, perception running the whole time. Entered only from `Search`, and only
    /// once per hunt; expires back into `Search` with a fresh leg, or into `Stalk` if the pause
    /// itself catches you. NOT interrupted-only-by-noise like a committed lunge — it is a beat
    /// inside a search, not a commitment on top of one.
    Peek,
    /// ADR-050 point 6 — IT GOT SCARED. A gunshot close to a creature that has already eaten sends
    /// it away from the noise instead of toward it. The same trigger pull produces two different
    /// animals depending on how full the one that heard it is, and that is the clearest reading of
    /// the hunger model the player ever gets: you fire, and either something comes, or something
    /// bolts.
    Flee,
    /// ADR-092 — THE FEINT. A hunt it gave up on, but not really: it walks out of sight of where it
    /// last saw you, goes still and silent for a while, and then comes back to look again. Clothed
    /// the whole time (`phantom_reveals` → false), no head-tracking (there is nobody to track), and
    /// distractible by noise like `Search`. Entered ONLY from `Search`'s give-up, at most once per
    /// hunt.
    Lurk,
    /// ADR-050 point 9 — it has hold of you and is killing you, but not instantly: for
    /// `PHANTOM_GRAB_SECONDS` you are held and ALIVE, and struggling free is the way out. This is
    /// the only state whose exit the victim controls directly.
    Grab,
    /// ADR-051 point 2 — THE WARNING. Hungry, watched, and about to tear out of the stolen skin: it
    /// stands dead still, facing you, screaming, for `PHANTOM_UNMASK_SECONDS`.
    ///
    /// It does NOT reveal here, and that is the whole point of the state existing. What makes this
    /// second and a half work is that the thing screaming at you still looks like one of your own.
    Unmasking,
    /// ADR-051 point 5 — the unmasked hunt. Where a `Sprint` that failed to kill goes, instead of
    /// back to `Stalk`. Stalks you like `Stalk` does but REVEALED, and never puts the skin back on:
    /// the only way out is losing you, which is what makes the disguise falling a one-way door for
    /// as long as the chase lasts.
    Hunting,
}

/// ADR-038: the two states where the stolen skin stops holding — the phantom shows its real form.
/// SINGLE SOURCE OF TRUTH for the reveal: anyone adding a `PhantomState` has to decide here, and
/// the decision is covered by `phantom_reveals_only_in_sprint_and_statue`. Purely cosmetic — the
/// flag rides the pose relay and never gates damage, detection or collision.
pub(super) fn phantom_reveals(state: PhantomState) -> bool {
    // ADR-050 point 10: `Grab` reveals — it is holding you at arm's length and there is no
    // ambiguity left to protect.
    //
    // ADR-051 points 1 and 2 — TWO CHANGES FROM THE PLAY-TEST, both about when the skin comes off:
    //
    // `Statue` is OUT. It used to reveal, and `Statue` is entered by LOOKING AT the creature from
    // close by, with hunger playing no part — so a sated one, the kind ADR-050 built to follow you
    // around and copy you, tore out of its skin just because you turned to look at it, and put it
    // back on when you turned away. The disguise was falling to a camera angle instead of to an
    // intention. It now freezes and stares WEARING the skin, which is worse for you.
    //
    // `Unmasking` is also out, deliberately: that state is the warning, and it only works while the
    // thing screaming at you still looks like one of your own.
    matches!(
        state,
        PhantomState::Sprint | PhantomState::Grab | PhantomState::Hunting
    )
}

/// ADR-080 point 1 — the states in which the creature's HEAD follows its target, sealed as `pitch`
/// (ADR-021). Every other state writes 0 and looks straight ahead, which is what the phantom did in
/// every state until now — including while holding you at arm's length.
///
/// Exhaustive `match` on purpose, exactly like the reveal test's: a new `PhantomState` must not be
/// able to compile without someone deciding whether that state stares at you.
pub(super) fn phantom_tracks_with_head(state: PhantomState) -> bool {
    match state {
        // Committed to a target: it is watching, closing, freezing, tearing or holding you.
        // `Statue` included deliberately — ADR-038 freezes the BODY, and a frozen body whose head
        // keeps following you is the entire image that state is trying to produce.
        PhantomState::Spotted
        | PhantomState::Stalk
        | PhantomState::Statue
        | PhantomState::Sprint
        | PhantomState::Ambush
        | PhantomState::Unmasking
        | PhantomState::Hunting
        | PhantomState::Grab => true,
        // No target to track, or deliberately not looking at you: patrolling, searching a place it
        // guessed, peeking round a corner at a GOAL rather than at a player, or running away.
        PhantomState::Wander
        | PhantomState::Search
        | PhantomState::Peek
        | PhantomState::Flee
        | PhantomState::Lurk => false,
    }
}

/// ADR-080 point 1 — the head angle towards `tpos`, in the quantized `i8` degrees of ADR-021
/// (POSITIVE = looking DOWN, the convention that ADR fixed and `ProxyPitchHook` renders).
///
/// `dist` is the XZ distance the tick already computed, so this costs one `atan2` and no square
/// root. Both bodies report their pose at the player pivot convention (`PLAYER_BASE_Y`), so the
/// vertical difference needs no eye-height correction to be right for equal footing — the only case
/// where it matters is a target on another layer, where the sign is what carries the information.
pub(super) fn quantize_look_pitch(from: Vec3, tpos: Vec3, dist: f32) -> i8 {
    // Point blank: an atan2 against a ~0 baseline swings wildly frame to frame. Look level.
    if dist < 0.2 {
        return 0;
    }
    let degrees = (from.y - tpos.y).atan2(dist).to_degrees();
    degrees.clamp(-PHANTOM_LOOK_PITCH_CLAMP_DEG, PHANTOM_LOOK_PITCH_CLAMP_DEG) as i8
}

/// ADR-080 point 5 — the deterministic 0..1 that spreads chorus answers apart in time.
///
/// Same mixing chain as `derive_hunger`, and deterministic for the same reason: a play-test has to
/// be repeatable, and `rand` here would mean the same encounter never sounds the same twice — which
/// makes "did the answer come from over there?" impossible to check.
pub(super) fn chorus_delay_fraction(key: u64) -> f32 {
    let mut z = key.wrapping_mul(0x9E37_79B9_7F4A_7C15);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^= z >> 31;
    (z >> 11) as f32 / (1u64 << 53) as f32
}

/// ADR-088 — WHERE TO RUN TO, to meet a moving target instead of following it. `None` when the
/// target is not really moving, or when the projected point is somewhere the TARGET could not get to
/// (a wall between them and it): then the caller keeps aiming at the contact stance, which is exactly
/// what it did before this existed.
///
/// Solved by fixed-point iteration — three rounds of `t = |p(t) − from| / speed` with
/// `p(t) = tpos + v·t` — which converges in a couple of steps for any closing speed above the
/// target's, and is capped at `PHANTOM_INTERCEPT_MAX_LEAD` either way.
///
/// NAVIGATION ONLY. The strike, `has_line` and the attack distance all keep measuring against the
/// player and the contact stance (ADR-082); feeding them this point would hand out reach for free.
pub(super) fn intercept_point(
    cache: &mut crate::world::grid_gen::GridGenChunkCache,
    layer: u8,
    from: Vec3,
    tpos: Vec3,
    vel: (f32, f32),
    closing_speed: f32,
) -> Option<Vec3> {
    let speed = (vel.0 * vel.0 + vel.1 * vel.1).sqrt();
    if speed < PHANTOM_INTERCEPT_MIN_SPEED || closing_speed <= speed {
        return None; // standing still, or it cannot out-run them: nothing to cut off
    }
    let mut t = from.distance_xz(tpos) / closing_speed;
    let mut p = tpos;
    for _ in 0..3 {
        t = t.min(PHANTOM_INTERCEPT_MAX_LEAD);
        p = Vec3::new(tpos.x + vel.0 * t, tpos.y, tpos.z + vel.1 * t);
        t = from.distance_xz(p) / closing_speed;
    }
    // Only a place the TARGET can actually reach counts: the thin line from them to the point. A
    // runner heading into a wall is about to turn, and guessing which way is not interception.
    if !crate::world::grid_gen::segment_is_clear(cache, layer, tpos, p) {
        return None;
    }
    Some(p)
}

/// ADR-080 point 6 — the `buttons` bits a sated creature may copy: lean left/right (2-3, ADR-044)
/// and the spray jet (4, ADR-078 phase A). Bits 0-1 are aim/reload and stay masked OUT — a phantom
/// carries no weapon, so mirroring an aim would be a tell rather than a disguise.
pub(super) const PHANTOM_MIMIC_BUTTON_MASK: u16 = 0b1_1100;

/// ADR-016 slice 1 (phantom damage) — what `PhantomDriver::step` produced this tick, and for
/// WHOM. Returned to the game loop, which routes each one to the backend that owns that player's
/// health (ADR-047).
///
/// ADR-047 — `victim` is part of the TYPE, not an optional extra. The three construction sites
/// cannot compile without naming it, and the two `let (_, tpos, …)` bindings that used to throw
/// the target id away stop compiling too. That is the whole point: `choose_target` has
/// always been able to pick a REMOTE peer, and the old victim-less enum left the consumer with
/// nothing to branch on, so every hit landed on the host's own player — a phantom chasing a
/// joiner damaged the host. A type that cannot express the broken state beats a guard someone
/// can delete.
///
/// The damage path stays SEPARATE from the pickup theater (ADR-016 invariant intact).
#[derive(Clone, Copy, PartialEq, Debug)]
pub(super) struct PhantomAttack {
    /// Whose health this is for. May be this backend's own local player or a remote peer; the
    /// consumer routes on it and NEVER falls back to the local player (see ADR-047 D1).
    pub(super) victim: PeerId,
    pub(super) kind: PhantomAttackKind,
}

/// ADR-016 slice 1 — what kind of blow landed. Split out of `PhantomAttack` so the victim can be
/// mandatory without repeating it per variant.
#[derive(Clone, Copy, PartialEq, Debug)]
pub(super) enum PhantomAttackKind {
    // ADR-043: there is no `None` variant. "Nothing happened this tick" is an EMPTY list now that
    // `step` returns one entry per attacker. Keeping the variant would leave something nothing
    // constructs any more — a trap, because `attack == None` would silently never fire.
    /// Frontal point-blank hit: non-lethal `damage` to health; the phantom bounces back to STALK.
    Hit(f32),
    /// Point-blank from BEHIND: lethal (the loop applies 100 dmg → the existing death/respawn).
    Kill,
    /// A shove the CLIENT applies (dx, dz m/s via the motor's SetVelocity). The backend never
    /// mutates the player pose for this — it's client-authoritative and would be overwritten by
    /// the next input (ADR-009), so the backend only signals the direction/force.
    Knockback(f32, f32),
    /// ADR-050 point 9 — IT HAS YOU, AND YOU ARE STILL ALIVE. Carries how many seconds the victim
    /// has to break out.
    ///
    /// Before this, a blow from behind was `Kill`: the backend applied 100 damage in the same tick
    /// it decided, and the client's grab animation ran afterwards as an epilogue for 0.9 s without
    /// reading a single input. There was no instant at which you were held and alive, so there was
    /// nothing to escape from. Now the death is deferred and the window is real.
    GrabStart(f32),
    /// ADR-050 point 9 — you got it off you. The creature returns to stalking and does NOT feed, so
    /// it is still hungry and still around: you won, but only this exchange.
    GrabRelease,
    /// ADR-076 — the disguised ambush connecting. `f32` is seconds of stun (same trick as
    /// `GrabStart`: reused field, not a new one), `(f32, f32)` is the shove in the same (dx, dz)
    /// carrier `Knockback` already uses. ZERO health damage — this is the one strike kind that
    /// never touches `player.stats`, by design (ADR-076 point 3: "solo aturde").
    Knockdown(f32, f32, f32),
    /// ADR-094 Enmienda 7 — THE SEIZURE. A faceling child has you from behind: it turns you to
    /// face it, holds the camera on its face while it screams, shoves you off and leaves you
    /// deafened and slowed.
    ///
    /// Carries NOTHING, and that is the design. Every part of it — how long the turn takes, how
    /// close the face comes, how long your ears ring, how much slower you move — is presentation
    /// the client owns and Joel calibrates in play-test, exactly like ADR-042's light values and
    /// ADR-048's voice curves. The backend's job is the one thing only it can decide: WHETHER it
    /// happens, from a geometry no client can be trusted with.
    ///
    /// WHICH child grabs you is not carried either, and cannot be: ADR-016 §1 keeps the creature's
    /// id off the wire entirely. The client already knows where every faceling is (they are peers),
    /// so it picks the nearest one itself — which is the right answer anyway, since "the nearest
    /// one behind me" is exactly what the backend used to choose the attacker.
    ///
    /// Zero damage in this variant on purpose: the 10 the play-test asked for rides along as a
    /// separate `Hit`, so a peer too old to know kind 6 degrades to "took a hit from something"
    /// rather than to a silent no-op or, worse, to reading a duration as damage.
    Seize,
}

/// ADR-047 — THE single gate every noise passes through, whichever door it came in by: the local
/// IPC `report_noise` action, or a joiner's `NoiseReport` packet. Two parallel validations is how
/// a trusted path and an untrusted one drift apart, so there is exactly one.
///
/// Clamped, not trusted (ADR-041): a garbage loudness must not turn one shot into a world-wide
/// summons. Returns the sanitised pair, or `None` if the report is unusable.
pub(super) fn sanitize_noise(position: [f32; 3], loudness: f32) -> Option<([f32; 3], f32)> {
    if !position.iter().all(|c| c.is_finite()) {
        return None;
    }
    if !loudness.is_finite() || loudness <= 0.0 {
        return None;
    }
    Some((position, loudness.min(PHANTOM_NOISE_MAX_LOUDNESS)))
}

/// ADR-047 — stable, greppable name for an attack kind, used in the MPTRACE lines that make a
/// mis-routed or undeliverable blow VISIBLE. The original bug survived this long precisely because
/// no impossible path logged anything.
pub(super) fn phantom_attack_kind_name(kind: PhantomAttackKind) -> &'static str {
    match kind {
        PhantomAttackKind::Hit(_) => "hit",
        PhantomAttackKind::Kill => "kill",
        PhantomAttackKind::Knockback(_, _) => "knockback",
        PhantomAttackKind::GrabStart(_) => "grab_start",
        PhantomAttackKind::GrabRelease => "grab_release",
        PhantomAttackKind::Knockdown(_, _, _) => "knockdown",
        PhantomAttackKind::Seize => "seize",
    }
}

/// Host-only driver for phantom peers (the robapieles). Each phantom wanders: it steps along
/// its heading and, on a head-on block, turns to a new heading — so it bumps walls and turns
/// instead of clipping through them. Every step is resolved through `resolve_move_grid_gen` +
/// `GridGenChunkCache`, so the phantom collides against the SAME grid_gen world Unity renders
/// (not world::generator), generated on-demand even far from the host. Periodically the phantom
/// also FAKES a pickup (slice 4): it freezes and
/// flips its animation field to "pickup" for ~1s, then resumes. It does a behavioral TELL #2
/// (an unnatural, near-metronomic "stare"), and clones a real peer's NAME (victim identity;
/// keeps its own id). It DETECTS the nearest real player (distance + forward cone, no geometry
/// LOS — D1=(a)) and hunts through the six-state FSM below, which replaced slice 2's `chasing`
/// bool: the lunge runs at `PHANTOM_SPRINT_SPEED` and the theater is dropped while hunting.
///
/// SAFETY INVARIANT (ADR-016 slice 4): the faked pickup is PURE THEATER — it touches ONLY the
/// phantom's `animation` field (via `update_player_state`). It NEVER calls `process_stp_pickup`,
/// NEVER inserts into `pending_pickups`, NEVER touches `stp_items`, NEVER credits an inventory,
/// NEVER emits a `stp_pickup_granted`. The phantom has no effect on real game state beyond its
/// own presentation field. (Calling the real pickup path would delete/reserve a real world item.)
///
/// The `GridGenChunkCache` lives here (a host-only game-loop local), not in World/NetworkManager:
/// it is sim-only host state, the least invasive home. The phantom POSE itself lives in its
/// `PeerConnection` (the render source). (world::collision's `SimChunkCache` is unchanged but no
/// longer has a production consumer; it remains for tests / future world::generator entities.)
pub(super) struct PhantomDriver {
    /// Needed by `PhantomTraits::derive`, which runs in `add_anchored` where `NetworkManager` (the
    /// usual home of `world_seed`) is not in hand.
    pub(super) world_seed: u64,
    pub(super) grid_cache: GridGenChunkCache,
    /// ADR-108 — el ráster de WG3, cuando WG3 manda. `None` en el camino de siempre.
    ///
    /// **Es un caché aparte y no un reemplazo del de arriba**: `settle_items_tick` sigue usando el de
    /// `grid_gen` para la caída de objetos, y mezclarlos sería cambiar dos cosas a la vez en el
    /// fichero más delicado del backend.
    pub(super) wg3: Option<crate::world::wg3::collision::Wg3CollisionCache>,
    /// Buffer del camino crudo de WG3, reutilizado por el mismo motivo que `nav_cells`: una búsqueda
    /// por replanificación y por mover no puede asignar.
    pub(super) nav_world: Vec<Vec3>,
    pub(super) movers: Vec<PhantomMover>,
    /// ADR-089 — every claim marker standing in the world this step: `(owner, position, layer)`.
    /// Derived from `net.stp_buildings` once per step (ADR-081: the claims ARE the markers, there is
    /// no other table), so the FSM reads a slice instead of scanning the roster per mover.
    pub(super) homes: Vec<(PeerId, Vec3, u8)>,
    /// Last-tick XZ position of each real target (host + peers), keyed by id. Used to derive each
    /// target's speed (sound detection, slice 3b-P1) — peers never send velocity/move_state, so a
    /// position delta is the only uniform "is it running?" signal. Rebuilt each tick from the
    /// current targets, so disconnected ids drop out automatically.
    pub(super) prev_target_pos: HashMap<PeerId, Vec3>,
    /// ADR-040 — A* buffers, allocated ONCE and reused by every search, so pathfinding performs no
    /// heap work inside the tick. Lives on the driver (not per-mover) because only one search runs
    /// at a time.
    pub(super) nav_scratch: crate::world::grid_gen::NavScratch,
    /// Reusable cell-path buffer handed to `find_path`, for the same reason.
    pub(super) nav_cells: Vec<crate::world::grid_gen::CellCoord>,
    /// Cumulative A\* searches. Proves the replan policy holds the cost bound; ADR-043 promotes it
    /// out of `#[cfg(test)]` because the same number is what the host's step instrumentation logs,
    /// and a load test needs to see it in a real session, not only in a unit test.
    pub(super) nav_replans: u64,
    /// ADR-043 — steps taken, the phase source for the replan stagger. Wrapping: it is only ever
    /// read modulo the stride.
    pub(super) step_counter: u64,
    /// ADR-043 — WORST step duration (µs) since the last report, reset on every report.
    ///
    /// The peak and not a sample, because the thing worth knowing is whether a step ever blew the
    /// budget, and `MissedTickBehavior::Skip` guarantees that an overrun is invisible otherwise: it
    /// drops ticks silently, `dt` stays a hardcoded 0.1 s, and the only symptom is the AI running
    /// in slow motion — which in play-test looks like a design choice, not like a fault.
    pub(super) step_peak_us: u64,
    /// ADR-053 — `(phantom, victim)` pairs whose stolen voice should be played back this tick.
    /// Produced by the driver, drained by the game loop, which is the half that owns the sockets.
    pub(super) voice_echoes: Vec<(PeerId, PeerId)>,
    /// ADR-043 — every attack produced this tick, one entry per mover that landed one. A single
    /// return value made the LAST attacker of the tick win and silently dropped the rest, which
    /// with a populated world is two creatures reaching you together and only one of them
    /// counting. Owned by the driver and cleared per step so the fan-out costs no allocation.
    pub(super) attacks: Vec<PhantomAttack>,
    /// ADR-043 — seconds until the active population is reconciled against the draw again.
    pub(super) population_sync_in: f32,
    /// ADR-043 — monotonic counter handing each new mover its `victim_slot`. Monotonic and not
    /// `movers.len()` on purpose: indices shift when a creature deactivates, and reusing one would
    /// silently re-cast a survivor as somebody else the next time a neighbour despawned.
    pub(super) next_victim_slot: usize,
    /// ADR-043 — density multiplier applied to the draw. Held here so the pure draw stays a pure
    /// function of its arguments. P0-2: no longer read from env here — `new` sets the neutral
    /// default (1.0) and the caller overwrites it from `net.phantom_density_scale` right after
    /// construction, so the value can travel in the HandshakeAck and the save file instead of
    /// being fixed per-process (a joiner's own env never reached this before P0-2 either, since
    /// only the host ever calls the population draw — but the value is meant to be a property of
    /// the world, not of whichever machine happens to be host).
    pub(super) density_scale: f32,
    /// ADR-043 — max simultaneously simulated phantoms (`PHANTOM_ACTIVE_CAP`, env-overridable).
    pub(super) active_cap: usize,
    /// ADR-050/051 — the three knobs worth turning between play-tests, read once at construction
    /// like the two above and for the same reason ADR-043 gives: a value that changed mid-session
    /// would make the creature's behaviour depend on when you looked.
    ///
    /// These live on the driver rather than being `const` because they are the ones whose right
    /// value is a matter of feel, and feel is calibrated by playing, not by recompiling. Everything
    /// else stays a constant on purpose — a knob nobody turns is just a slower constant.
    pub(super) hunger_drain_seconds: f32,
    pub(super) sated_threshold: f32,
    pub(super) unmask_seconds: f32,
    /// Size of the building roster the blocked-cell overlay was last built from.
    pub(super) built_count: usize,
    /// Seconds until the overlay is rebuilt regardless of size. A place + a demolish in the same
    /// window leave the count unchanged, so size alone would miss it.
    pub(super) built_resync_in: f32,
}

/// ADR-043 — identity of a drawn phantom: `(block, layer, index within the block)`.
///
/// The index is what lets a block hold more than one. Without it, `PHANTOM_DENSITY_SCALE` above 1.0
/// would be a no-op — measured on the deployed binary: scale 8 with a cap of 24 woke exactly ONE
/// creature, because the block, not the cap, was the limit.
pub(super) type PhantomAnchor = ((i32, i32), u8, u8);

/// ADR-043 — a drawn phantom the population reconciler is considering waking up this scan.
pub(super) struct PhantomCandidate {
    /// Distance from the nearest real player, in XZ. Sorted on, so the cap keeps the creatures
    /// somebody is most likely to actually meet.
    pub(super) distance: f32,
    /// `(block, layer)` it was drawn from — becomes the mover's `anchor`.
    pub(super) anchor: PhantomAnchor,
    /// Raw drawn position, before `spawn_phantom` snaps it to a walkable cell.
    pub(super) position: [f32; 3],
}

/// Per-phantom state: which peer, its heading (yaw, radians), the faked-pickup gesture (slice 4),
/// the "stare" tell (tell phase), and the victim-name binding (identity phase).
pub(super) struct PhantomMover {
    pub(super) id: PeerId,
    /// ADR-043 — the `(block, layer)` this creature was drawn from, or `None` for one placed by
    /// hand (`DEBUG_SPAWN_PHANTOM`). The population reconciler uses it as the identity of a drawn
    /// phantom: it is what stops the same block spawning a second copy on the next scan, and
    /// `None` is what keeps a hand-placed one exempt from being reconciled away.
    ///
    /// Deliberately NOT the same thing as `spawn_pos`, which re-anchors as the creature travels
    /// (ADR-041). The anchor is where the world says it lives; `spawn_pos` is where it currently
    /// considers home.
    pub(super) anchor: Option<PhantomAnchor>,
    /// Which real player this one impersonates, as an index into `NetworkManager::real_peer_names`.
    /// Assigned once at spawn and never reshuffled.
    ///
    /// Before ADR-043 the victim was resolved with a single `.find()` over `peers` and handed to
    /// EVERY mover, so a populated world was N avatars all wearing the same name — the disguise
    /// ADR-016 exists to protect, defeating itself the moment two were in sight.
    pub(super) victim_slot: usize,
    pub(super) heading: f32,
    /// Spawn position; the observation leash (PHANTOM_WANDER_RADIUS) re-aims the heading here
    /// when the phantom drifts too far, so it stays in view during play-test.
    pub(super) spawn_pos: Vec3,
    /// `Some(t)` while faking a pickup: movement is paused and `animation` is held at
    /// "pickup" until `t`. `None` while walking. PURE presentation — never real pickup state.
    pub(super) pickup_until: Option<Instant>,
    /// Earliest instant the next faked pickup may begin.
    pub(super) next_pickup_at: Instant,
    /// `Some(t)` while doing the "stare" tell: frozen, unnaturally still ("idle") until `t`.
    /// `None` while walking. The tell is its near-metronomic regularity.
    pub(super) stare_until: Option<Instant>,
    /// Earliest instant the next stare tell may begin.
    pub(super) next_stare_at: Instant,
    /// `true` once the name was bound to a real victim (so it is not rebound). `false` = still
    /// on the host-name fallback; adopts a real peer's name when one connects.
    pub(super) victim_bound: bool,
    /// ADR-016 slice 3a — current FSM state. Replaces the slice-2 `chasing` bool.
    pub(super) state: PhantomState,
    /// Seconds spent in the current `state` (reset to 0 on every transition). Drives the SPOTTED
    /// stare length, the STALK patience, and the SPRINT speed ramp.
    pub(super) state_timer: f32,
    /// Randomized SPOTTED stare length (s), drawn in [SPOTTED_MIN, SPOTTED_MAX] on entry.
    pub(super) spotted_duration: f32,
    /// Last position the player was seen at while STALK/SPRINT — recorded now so slice 3b's
    /// SEARCH/PEEK need no further step() rework.
    pub(super) last_known_player_pos: Option<Vec3>,
    /// WANDER organic pause: remaining time (s) it stands still "looking at a wall". `is_paused`
    /// gates it.
    pub(super) wander_pause_timer: f32,
    pub(super) is_paused: bool,
    /// Smoothed turn target (yaw radians): STALK/SPRINT/STATUE ease `heading` toward this instead
    /// of snapping each tick (fluidity), so it tracks the player without 10 Hz rotational jerk.
    pub(super) heading_target: f32,
    /// ADR-040 — string-pulled route the phantom is currently walking, in world space. Empty means
    /// "no plan": the steering falls back to heading straight at the target, which is exactly the
    /// pre-ADR-040 behaviour, so a failed or stale plan degrades to the old code instead of freezing.
    pub(super) nav_waypoints: Vec<Vec3>,
    /// Index of the waypoint being walked toward.
    pub(super) nav_cursor: usize,
    /// Goal the current plan was built for. A plan is stale once the target has drifted away from
    /// it, which is what stops the phantom from running at where you WERE.
    pub(super) nav_goal: Option<Vec3>,
    /// Seconds since the current plan was built.
    pub(super) nav_age: f32,
    /// ADR-041 — how long this SEARCH may run. Normally `PHANTOM_SEARCH_MAX`; a noise
    /// investigation gets a longer window, because arriving after minutes of travel and shrugging
    /// immediately would waste the entire approach.
    pub(super) search_patience: f32,
    /// Travel speed for the current SEARCH. A noise investigation walks; a normal search ambles.
    pub(super) search_speed: f32,
    /// Seconds left before the noise being investigated goes cold. `None` = not a noise search.
    pub(super) noise_expiry: Option<f32>,
    /// Consecutive steps in which the resolver advanced the creature by nothing at all, i.e. it is
    /// pressed into geometry. Reset to 0 by any step that moves.
    ///
    /// Exists because STALK and SPRINT — the two states where being stuck is most visible — were
    /// the only ones that never looked at whether their step landed. WANDER re-orients on a block
    /// and SEARCH drops its plan; the two hunting states just kept pushing into the same corner at
    /// 10 Hz, which is what "se queda pegado en las esquinas" looks like from the outside.
    pub(super) blocked_ticks: u8,
    /// Ticks left of "keep following the route even though nothing is blocking right now".
    /// See `PHANTOM_WEDGE_HYSTERESIS_TICKS`.
    pub(super) wedge_cooldown: u8,
    /// ADR-053 — places where a hunt of this creature's ended. Checked before giving up on the
    /// next one, which is what makes reusing a hiding place a bad idea.
    pub(super) hideouts: Vec<Vec3>,
    /// ADR-089 — whether this hunt has already sent the creature to check the target's claim. One
    /// visit per hunt, like the hideout checks: a creature that loops to your door forever is a
    /// mechanic, not a habit.
    pub(super) home_checked_this_hunt: bool,
    /// How many remembered spots the CURRENT hunt has already been to. Reset on entering SEARCH.
    pub(super) hideouts_checked: u8,
    /// ADR-075 point 2c — seconds the current target has been effectively still (below walking-
    /// noise speed). Drives the anti-camping creep of the stalk band; reset whenever the target
    /// moves, a lunge starts, or the hunt is lost.
    pub(super) target_still_for: f32,
    /// ADR-075 point 3 — the target's velocity at the moment it was last actually observed
    /// (STALK/SPRINT/HUNTING/UNMASKING all write it alongside `last_known_player_pos`). Read once,
    /// on entering SEARCH, to aim the first leg of the sweep ahead of where you were rather than
    /// straight at it.
    pub(super) last_seen_vel: Option<(f32, f32)>,
    /// ADR-075 point 3 — the EXACT last-known point, held in reserve while SEARCH investigates the
    /// predicted lead point first. `None` once the exact point has been visited (or was the goal
    /// from the start, when there was nothing to predict from).
    pub(super) search_fallback: Option<Vec3>,
    /// ADR-075 point 4 — how long the CURRENT peek lasts, rolled once on entering `Peek`.
    pub(super) peek_for: f32,
    /// Whether the CURRENT search has already spent its one peek. Reset wherever a search starts
    /// fresh: re-acquisition, give-up, and `enter_search_after_hunt`. A detour to a hideout does
    /// NOT reset it — one peek per hunt-search, hideouts included.
    pub(super) peeked_this_search: bool,
    /// ADR-076 — seconds left before another `Ambush` may be attempted. Ages in
    /// `resolve_mover_tick` alongside `strike_recover`/`enraged_for`.
    pub(super) ambush_cooldown: f32,
    /// ADR-076 — whether the CURRENT hunt has already spent its one ambush. Reset on a kill
    /// (`tick_grab`) and wherever the hunt itself ends (give-up, lost to Wander).
    pub(super) ambushed_this_hunt: bool,
    /// Seconds left of the post-strike commitment. While positive the lunge holds (still revealed)
    /// and cannot land a second blow; at zero it bounces to STALK.
    pub(super) strike_recover: f32,
    /// Seconds left before this one may freeze into STATUE again.
    pub(super) statue_cooldown: f32,
    /// This creature's temperament, fixed for as long as it exists and reproducible from the seed.
    pub(super) traits: PhantomTraits,
    /// Who this creature is hunting, carried between ticks so the choice is STICKY. `None` = has
    /// not committed to anyone (patrolling, or its target died/left).
    pub(super) target_id: Option<PeerId>,
    /// ADR-048 — a vocalisation decided THIS tick, sealed at the end of the step alongside
    /// `revealed`. Staged rather than written where it is decided for exactly the reason ADR-038
    /// gives for the reveal seal: the FSM has many early `continue`s, so a per-branch write would
    /// silently miss paths, and `hear_noises` runs before the FSM even starts.
    pub(super) pending_vocal: Option<u8>,
    /// Monotonic wrapping counter, mirrored onto the peer each tick. NEVER lands back on 0 after
    /// its first bump: 0 is the client's "has never vocalised" sentinel, and wrapping onto it would
    /// silently swallow one scream every 255.
    pub(super) vocal_seq: u8,
    /// Which voice the last bump was.
    pub(super) vocal_kind: u8,
    /// Seconds until this creature may vocalise again.
    pub(super) vocal_cooldown: f32,
    /// Seconds of RAGE left. Set by hearing a shot — a gunshot does not merely attract this thing,
    /// it angers it — and much longer/harder when the shot went off close by.
    pub(super) enraged_for: f32,
    /// ADR-050 — HOW FULL IT IS, 0.0 famished .. 1.0 just fed. The slow state that governs whether
    /// this is an animal that follows you or one that eats you.
    ///
    /// Drains on its own clock and refills to 1.0 on a kill, which is the breathing room the victim
    /// gets on respawn and what stops a death from looping straight back into another one — the job
    /// `calm_for` used to do, now as a cycle instead of as a one-shot timer.
    pub(super) hunger: f32,
    /// Seconds this lunge has spent with no clear line to its target. THE hiding mechanic: a sprint
    /// no longer ends on a timer, it ends when you break line of sight or outrun it.
    pub(super) sprint_blind_for: f32,
    /// Seconds until the next stalking breath. Randomised per breath rather than fixed: a
    /// metronomic one would become a clock the player can read, and the whole point of this sound
    /// is that you cannot tell how close it is or what it is about to do.
    pub(super) breath_in: f32,
    /// ADR-053 — seconds until this creature may throw your own voice back at you again.
    pub(super) echo_cooldown: f32,
    /// Copy of the driver's calibration knob, so `is_sated` needs no driver. See that method.
    pub(super) sated_threshold: f32,
    /// ADR-050 point 9 — seconds left before the grab becomes a kill, and WHO is being held.
    /// The victim is part of the state rather than re-resolved per tick: `choose_target` could
    /// legitimately pick somebody closer mid-grab, and killing a player the creature is not
    /// actually holding is exactly the class of mis-routing ADR-047 was written to close.
    pub(super) grab_timer: f32,
    pub(super) grab_victim: Option<PeerId>,
    /// ADR-050 point 8 — the pose being copied while sated, and how long until it is worn.
    /// `(crouch, held_item, seconds_left)`. A tiny one-slot buffer rather than a queue: at 10 Hz a
    /// queue would hold eight samples to reproduce a lag anyone can get from one.
    /// ADR-080 point 6 widened it to `(crouch, held_item, buttons, seconds_left)`.
    pub(super) mimic_pending: Option<(bool, i32, u16, f32)>,
    /// What it is wearing right now, i.e. the last pending sample whose delay ran out.
    pub(super) mimic_worn: (bool, i32, u16),
    /// ADR-050 — where a startled creature is running TO. Resolved once on the scare, from the
    /// noise position, and held: recomputing it per tick against a stimulus that no longer exists
    /// would make it wander in a circle instead of clearing the area.
    pub(super) flee_goal: Option<Vec3>,
    /// ADR-050 — seconds of flat-out running left in this burst. Refilled on entering SPRINT and
    /// after every recovery; spent only while actually charging (not while hesitating or winded).
    pub(super) stamina: f32,
    /// ADR-050 — seconds of heavy-walking recovery left. `> 0` means blown: it keeps coming, but
    /// slowly, and this is the window the player is meant to spend.
    pub(super) winded_for: f32,
    /// Seconds of stillness left at the START of a lunge. `revealed` is already true in SPRINT
    /// (ADR-038), so the beat lands AFTER the disguise drops and the scream: it reveals, screams,
    /// hangs there for a moment, and only then comes at you. Without it, reveal and charge are the
    /// same instant and there is nothing to read.
    pub(super) hesitate_timer: f32,
    /// ADR-080 point 1 — the angle towards the current target, in the `i8` degrees of ADR-021
    /// (positive = looking DOWN), staged by the preamble for `seal_cosmetics`.
    ///
    /// The angle is staged UNCONDITIONALLY and the state gate is applied at the SEAL, not here: the
    /// preamble runs before the FSM, so gating at stage time would dress the stare with LAST tick's
    /// state — a creature that spots you this tick would keep looking straight ahead for one more
    /// frame, and one that lost you would hold the stare for one frame too long. `revealed` is
    /// sealed from the post-tick state for exactly this reason (ADR-038).
    pub(super) look_pitch: i8,
    /// ADR-080 point 4 — WHICH SIDE it is walking around you: `-1.0`, `+1.0`, or `0.0` for "not
    /// currently flanking". Remembered rather than recomputed because a creature sitting dead centre
    /// in your view has two equally good ways out, and picking again every tick at 10 Hz would make
    /// it shuffle left-right on the spot instead of leaving your field of view.
    pub(super) flank_offset: f32,
    /// ADR-080 point 5 — seconds until this creature answers a distant scream, if it is going to.
    /// `None` = nothing to answer. Audio only: it never moves the creature and never chains.
    pub(super) chorus_pending: Option<f32>,
    /// ADR-091 — seconds of sidestep left, which way (±1), cooldown until the next one, and how
    /// many it has rolled (the seed of the deterministic side pick).
    pub(super) juke_for: f32,
    pub(super) juke_side: f32,
    pub(super) juke_cooldown: f32,
    pub(super) juke_rolls: u32,
    /// ADR-092 — where it is going to lie in wait, how long it still has to wait once there, and
    /// whether this hunt has already spent its one feint.
    pub(super) lurk_goal: Option<Vec3>,
    pub(super) lurk_for: f32,
    pub(super) lurked_this_hunt: bool,
    /// ADR-092 — counts the feint rolls (seed of the deterministic decision).
    pub(super) lurk_rolls: u32,
}

/// The creature's temperament, resolved to the number the FSM actually needs, RIGHT NOW.
///
/// These live on the mover and not on `PhantomDriver` because they read NOTHING but this mover.
/// They took an `i: usize` only because everything used to share one `impl`, and that index is what
/// forced `self.patience_of(i)` on a loop that already had the mover in hand — the caller ended up
/// reaching back through the driver to ask a creature about itself.
impl PhantomMover {
    /// How long this creature will shadow you before committing (s).
    ///
    /// Temperament, hunter-ness, rage and satiety all land here rather than being multiplied in at
    /// the call site. Four separate multiplications scattered through the FSM is how one of them
    /// ends up forgotten in a branch and a creature quietly behaves like a different animal.
    pub(super) fn patience(&self) -> f32 {
        let mut p = PHANTOM_STALK_PATIENCE * self.traits.patience_scale;
        if self.traits.is_hunter {
            p *= 0.25; // a hunter does not shadow you, it arrives
        }
        if self.enraged_for > 0.0 {
            p *= PHANTOM_RAGE_PATIENCE;
        }
        // ADR-075 point 5 — a PROVOKED saciada is the only sated creature that can ever be
        // enraged, and the calm multiplier must not also apply, or the two would cancel into
        // 30 s+ of patience and the provocation would never actually be read.
        if self.is_sated() && self.enraged_for <= 0.0 {
            p *= PHANTOM_CALM_PATIENCE;
        }
        p
    }

    /// ADR-050 — is it too full to hunt? The single question the whole redesign turns on, asked in
    /// one place so no branch can drift into its own idea of "sated".
    ///
    /// The threshold is copied onto the mover at birth rather than read off the driver: this is
    /// called from `PhantomMover` methods that have no driver in hand, and threading one through
    /// would put a `&PhantomDriver` in the signature of half the type for a single float.
    pub(super) fn is_sated(&self) -> bool {
        self.hunger > self.sated_threshold
    }

    /// ADR-050 — is it hungry enough to be actively dangerous?
    pub(super) fn is_hunting_hungry(&self) -> bool {
        self.hunger < PHANTOM_HUNGER_HUNTING
    }

    /// Per-tick chance multiplier for an unpredictable lunge.
    pub(super) fn impulse(&self) -> f32 {
        let mut k = self.traits.impulse_scale;
        if self.traits.is_hunter {
            k *= 3.0;
        }
        if self.enraged_for > 0.0 {
            k *= PHANTOM_RAGE_IMPULSE;
        }
        // ADR-075 point 5 — same bridge as `patience()`: a provoked sated creature must not also
        // be muffled by the calm multiplier, or the provocation would take minutes to matter.
        if self.is_sated() && self.enraged_for <= 0.0 {
            k *= PHANTOM_CALM_IMPULSE;
        }
        // ADR-050 point 4 — THE DICE STOP BEING THE ENGINE. Scaling by how empty it is means a full
        // creature effectively never rolls a lunge and a starving one rolls as often as it used to.
        // The roll goes from being the CAUSE of an attack to being the seasoning on one, which is
        // the whole difference between "it hangs around and then attacks" and a decision the player
        // can read coming.
        k * (1.0 - self.hunger)
    }

    /// Movement MULTIPLIER — not a speed, despite sitting next to `search_speed`. Only rage moves
    /// it; see `PHANTOM_RAGE_SPEED` for why the effect is deliberately small.
    pub(super) fn speed_scale(&self) -> f32 {
        match self.enraged_for > 0.0 {
            true => PHANTOM_RAGE_SPEED,
            false => 1.0,
        }
    }

    /// Is this mover pressed into geometry right now? Drives the two overrides in `steer_heading`.
    pub(super) fn is_wedged(&self) -> bool {
        self.blocked_ticks >= PHANTOM_BLOCKED_REPLAN_TICKS
    }

    /// Should the PATHFINDER keep the wheel this tick? True while wedged, and for
    /// `PHANTOM_WEDGE_HYSTERESIS_TICKS` after recovering — see that constant for the oscillation
    /// this exists to break. Without the tail, one good step hands control straight back to the
    /// straight-line shortcut that caused the wedge.
    pub(super) fn prefers_route(&self) -> bool {
        self.is_wedged() || self.wedge_cooldown > 0
    }

    /// Record how much of the step the creature actually got, and drop the route the moment it is
    /// wedged so the next tick re-plans instead of grinding along the same wall.
    ///
    /// `advance` is the realised displacement PROJECTED ON THE INTENDED DIRECTION, not the raw
    /// distance moved, and that distinction is the whole detector. The obvious signal —
    /// `MoveResult::blocked`, "neither axis advanced" — never fires in the case people actually
    /// report: pressed against a wall the resolver SLIDES, so the creature keeps moving at full
    /// speed sideways while getting no closer, forever. Projected advance reads a slide as ~0,
    /// which is what it is worth.
    ///
    /// Clearing the plan HERE and not only raising the counter matters: a stale route whose next
    /// waypoint sits on the far side of the obstacle is precisely what keeps aiming the creature at
    /// it. `steer_heading` reads `blocked_ticks` for the rest (force the replan past the stagger,
    /// distrust the straight line).
    pub(super) fn note_step_progress(&mut self, advance: f32, intended: f32) {
        // Not trying to travel (STALK holding its distance band) is not being stuck.
        if intended <= 1e-4 {
            self.blocked_ticks = 0;
            return;
        }
        if advance >= intended * PHANTOM_MIN_STEP_PROGRESS {
            // Recovered — but the route keeps the wheel for a while yet, or the shortcut that
            // wedged it takes over again on the very next tick.
            if self.is_wedged() {
                self.wedge_cooldown = PHANTOM_WEDGE_HYSTERESIS_TICKS;
            }
            self.blocked_ticks = 0;
            return;
        }
        self.blocked_ticks = self.blocked_ticks.saturating_add(1);
        if self.blocked_ticks >= PHANTOM_BLOCKED_REPLAN_TICKS {
            self.nav_waypoints.clear();
        }
    }

    /// Commit this mover to a lunge, from whichever state decided on one.
    ///
    /// A single door into SPRINT because the entry now carries state (the hesitation roll) and
    /// three copies of that would drift: a lunge entered through the copy someone forgot to update
    /// would silently never hesitate, and nothing would fail.
    pub(super) fn enter_sprint(&mut self) {
        self.state = PhantomState::Sprint;
        self.state_timer = 0.0;
        // ADR-050 point 5: a fresh charge always opens with a full burst, whatever the last one
        // left behind. Re-entering SPRINT is a new decision, not a continuation.
        self.stamina = PHANTOM_SPRINT_BURST_SECONDS;
        self.winded_for = 0.0;
        // ADR-075: the lunge is the creep's payoff — the camping clock does not survive it.
        self.target_still_for = 0.0;
        // ADR-048 point 6: the reveal-scream is now EMITTED, not inferred by each client from the
        // `revealed` edge, so everyone hears it at the same instant.
        self.try_vocalize(VOCAL_REVEAL);
        // Rolled per lunge, not per creature: `hesitate_chance` is the temperament, this is what it
        // does THIS time. A creature that always paused would be as readable as one that never did.
        // A hunter does not hesitate, and neither does something you just shot at.
        let may_hesitate = !self.traits.is_hunter && self.enraged_for <= 0.0;
        self.hesitate_timer =
            match may_hesitate && rand::random::<f32>() < self.traits.hesitate_chance {
                true => {
                    PHANTOM_HESITATE_MIN
                        + rand::random::<f32>() * (PHANTOM_HESITATE_MAX - PHANTOM_HESITATE_MIN)
                }
                false => 0.0,
            };
    }

    /// ADR-048 — stage a vocalisation, if this creature is not still catching its breath.
    ///
    /// Silently drops the request when on cooldown rather than queueing it: a scream that arrives
    /// six seconds after the thing that caused it is worse than no scream, because the player will
    /// place it wrong. Also drops a SECOND request in the same tick — the first one wins, so a
    /// creature that hears a noise and lunges on the same tick screams once, not twice.
    /// ADR-053 — file a place where a hunt ended. Merges into a nearby memory rather than adding a
    /// new one, so four slots do not fill with four corners of the same room, and evicts oldest
    /// first so the list tracks recent habits instead of ancient ones.
    pub(super) fn remember_hideout(&mut self, spot: Vec3) {
        if let Some(existing) = self
            .hideouts
            .iter_mut()
            .find(|h| h.distance_xz(spot) <= PHANTOM_HIDEOUT_MERGE_RADIUS)
        {
            *existing = spot; // same place, fresher fix
            return;
        }
        if self.hideouts.len() >= PHANTOM_HIDEOUT_MEMORY {
            self.hideouts.remove(0);
        }
        self.hideouts.push(spot);
    }

    /// ADR-053 — the nearest remembered spot worth a detour from `near`, if this hunt has any
    /// checks left. `None` means give up, which is what keeps hiding a real escape.
    pub(super) fn recall_hideout(&self, near: Vec3) -> Option<Vec3> {
        if self.hideouts_checked >= PHANTOM_HIDEOUT_CHECKS_PER_HUNT {
            return None;
        }
        self.hideouts
            .iter()
            .copied()
            // Not the spot it is already standing on: it just searched here.
            .filter(|h| {
                let d = h.distance_xz(near);
                d > PHANTOM_SEARCH_ARRIVE && d <= PHANTOM_HIDEOUT_RECALL_RADIUS
            })
            .min_by(|a, b| a.distance_xz(near).total_cmp(&b.distance_xz(near)))
    }

    pub(super) fn try_vocalize(&mut self, kind: u8) {
        self.try_vocalize_for(kind, PHANTOM_VOCAL_COOLDOWN);
    }

    /// `try_vocalize` with an explicit cooldown, so an AMBIENT voice does not spend the same budget
    /// as a dramatic one. A breath must not be able to mute the scream of a lunge two seconds later.
    pub(super) fn try_vocalize_for(&mut self, kind: u8, cooldown: f32) {
        if self.vocal_cooldown > 0.0 || self.pending_vocal.is_some() {
            return;
        }
        self.pending_vocal = Some(kind);
        self.vocal_cooldown = cooldown;
    }
}

/// The per-TICK half of what a state arm needs: what is the same for every mover this step.
///
/// Grouped instead of passed as five parameters because six state methods would each repeat the
/// same five, and a sixth input would become a six-site edit — which is how one arm ends up reading
/// a stale copy of something the others already updated.
///
/// Deliberately does NOT carry `host_player_pos`/`_rot`/`_dead`: those feed `choose_target` in the
/// preamble and never reach an arm. Measured before writing this down, not assumed — they appear
/// exactly zero times inside the FSM.
struct TickCtx<'a> {
    net: &'a mut NetworkManager,
    dt: f32,
    now: Instant,
    /// Planar speed of every real target this tick, for ADR-040's sound tiers. Derived from the
    /// position delta because peers send no velocity.
    target_speeds: &'a HashMap<PeerId, f32>,
    /// ADR-075 point 3 — the SAME per-tick delta as `target_speeds`, kept as a vector instead of
    /// collapsed to a scalar. `target_speeds` exists for the sound tiers, which only ever needed
    /// magnitude; predictive tracking needs direction too, so this is the one place that keeps it.
    target_vels: &'a HashMap<PeerId, (f32, f32)>,
    /// The HOST player's crouch. Remote peers carry their own on `PeerConnection` (ADR-020 relays
    /// it), but the host's local player is not a peer, so it has to be handed in.
    host_crouch: bool,
    /// ADR-050 point 8 — the HOST player's held item, handed in for exactly the same reason as its
    /// crouch: imitation reads the target's pose off `PeerConnection`, and the host does not have
    /// one. Without this a sated creature would copy remote joiners and stand there empty-handed
    /// next to the host, which is the one case a solo play-test can actually see.
    host_held_item: i32,
    /// ADR-080 point 2 — the HOST player's lit-wieldable flag. Same reason as the two above: peers
    /// carry `light_on` on `PeerConnection` since ADR-042, the host does not.
    host_light_on: bool,
    /// ADR-080 point 6 — the HOST player's sustained-state bits, for the imitation. Same reason
    /// again; masked to the copyable bits at the point of use, not here.
    host_buttons: u16,
}

/// The per-MOVER half: what the preamble already resolved about THIS creature before dispatching.
///
/// `Copy` — five scalars and a tuple — so an arm destructures it without ceremony and nothing here
/// can drift out of sync with the mover it describes.
#[derive(Clone, Copy)]
struct MoverTick {
    /// Index into `PhantomDriver::movers`. Still an index rather than a `&mut PhantomMover` because
    /// the arms also need `&mut self` for the driver's nav buffers and the attack fan-out; handing
    /// out the mover would borrow the driver twice.
    i: usize,
    id: PeerId,
    /// Where the creature is right now, read off its own peer.
    from: Vec3,
    /// grid_gen layer derived from its own Y, so collision works on any level with no hardcoding.
    layer: u8,
    /// Who it committed to this tick: `(id, position, distance, yaw)`. `None` = nobody in range.
    target: Option<(PeerId, Vec3, f32, f32)>,
}

/// ADR-093 (E5): `base` tal cual fuera del Level 4; dentro de la reserva, la densidad PROPIA de
/// la zona (`REGION_PHANTOM_DENSITY_MULT`) escalada además por el epoch. Vive fuera del `impl`
/// porque es pura (no necesita `&self`) y los dos call-sites de `phantom_spawn::draw_into` la
/// comparten sin duplicar la comprobación.
///
/// Los dos factores responden a preguntas distintas y por eso son dos constantes y no una: el
/// multiplicador dice cuánto vale la reserva frente al laberinto y no cambia nunca; el escalado
/// por epoch dice cuánto se ha torcido la región desde que se abrió la puerta.
pub(super) fn level4_scaled_density(
    base: f32,
    block: (i32, i32),
    layer: u8,
    level4_epoch: u32,
) -> f32 {
    if crate::world::level4_layout::block_is_in_region(block, layer) {
        base * crate::world::level4_layout::REGION_PHANTOM_DENSITY_MULT
            * crate::world::level4_layout::density_scale_for_epoch(level4_epoch)
    } else {
        base
    }
}

/// ADR-093: ¿sirve este punto sorteado, o cayó fuera de la reserva?
///
/// El sorteo reparte sobre el BLOQUE entero (4×4 chunks) y la reserva ocupa 9 de esos 16, así
/// que ~44 % de lo que sale del bombo aterriza en el laberinto vecino — a 10 km del spawn, al
/// otro lado de un perímetro macizo, donde ningún jugador va a estar nunca. Sin este descarte,
/// subir `REGION_PHANTOM_DENSITY_MULT` paga IA por criaturas inalcanzables y la constante deja
/// de significar "cuántos hay en la zona".
///
/// Se descarta en el BUCLE, nunca filtrando el vector: la identidad de cada criatura es
/// `(bloque, capa, ÍNDICE en el sorteo)`, y compactar la lista renumeraría a todas las
/// siguientes — el mismo robapieles cambiaría de identidad entre dos escaneos.
///
/// Fuera de la reserva devuelve siempre `true`: el laberinto no tiene dentro ni fuera.
pub(super) fn level4_spot_is_usable(block: (i32, i32), layer: u8, pos: [f32; 3]) -> bool {
    if !crate::world::level4_layout::block_is_in_region(block, layer) {
        return true;
    }
    crate::world::grid_gen::level4::world_pos_to_region_cell(pos).is_some()
}

impl PhantomDriver {
    pub(super) fn new(world_seed: u64) -> Self {
        Self {
            world_seed,
            // ADR-108 — lo enciende el bucle cuando WG3 manda; aquí no se sabe todavía.
            wg3: None,
            nav_world: Vec::new(),
            // ADR-033: el fantasma colisiona contra la MISMA densidad por zona que
            // se renderiza. Es el consumidor que hereda el cambio sin divergencia
            // (a diferencia del jugador real, que sigue contra world::generator —
            // deuda aceptada y documentada en el ADR).
            grid_cache: GridGenChunkCache::with_rules(
                world_seed,
                crate::world::zone_density::rules_for,
            ),
            movers: Vec::new(),
            prev_target_pos: HashMap::new(),
            nav_scratch: crate::world::grid_gen::NavScratch::new(),
            nav_cells: Vec::new(),
            nav_replans: 0,
            step_counter: 0,
            step_peak_us: 0,
            attacks: Vec::new(),
            voice_echoes: Vec::new(),
            homes: Vec::new(),
            population_sync_in: 0.0, // reconcile on the very first entity tick
            next_victim_slot: 0,
            // ADR-043 — the load-test levers. Read ONCE at construction, not per tick: a value
            // that could change mid-session would make the world's population depend on when you
            // looked, and the draw's whole promise is that it does not.
            // P0-2: density_scale's neutral default — the caller (game_loop::run) overwrites it
            // right after construction from net.phantom_density_scale. See the field's doc-comment.
            density_scale: 1.0,
            active_cap: env_tuning("PHANTOM_ACTIVE_CAP", PHANTOM_ACTIVE_CAP),
            hunger_drain_seconds: env_tuning(
                "PHANTOM_HUNGER_DRAIN_SECONDS",
                PHANTOM_HUNGER_DRAIN_SECONDS,
            )
            .max(1.0),
            sated_threshold: env_tuning("PHANTOM_HUNGER_SATED", PHANTOM_HUNGER_SATED)
                .clamp(0.0, 1.0),
            unmask_seconds: env_tuning("PHANTOM_UNMASK_SECONDS", PHANTOM_UNMASK_SECONDS).max(0.0),
            built_count: usize::MAX, // forces a build on the first step
            built_resync_in: 0.0,
        }
    }

    /// Player-built pieces are NOT in the generator's output, so without this the phantom walks
    /// straight through a wall you built to protect yourself — the one failure that reads as the
    /// game being broken rather than as the creature being scary.
    ///
    /// Approximation, deliberately: each piece blocks the 2.5 m cell it stands in. The backend
    /// knows a piece's `def_id`, position and rotation but NOT its size — footprints live in
    /// Unity's building definitions — so anything exact would need a new wire field and an ADR.
    /// One cell is right for walls and floors, which is what people actually build to hide behind.
    ///
    /// Feeding the CACHE means navigation inherits it for free: the phantom plans around your wall
    /// instead of planning through it and then bumping.
    pub(super) fn sync_built_cells(&mut self, net: &NetworkManager, dt: f32) {
        self.built_resync_in -= dt;
        if net.stp_buildings.len() == self.built_count && self.built_resync_in > 0.0 {
            return;
        }
        self.built_count = net.stp_buildings.len();
        self.built_resync_in = 2.0;
        let cells: std::collections::HashSet<(i32, i32)> = net
            .stp_buildings
            .iter()
            .map(|b| crate::world::grid_gen::cell_of(Vec3::from_array(b.position)))
            .collect();
        self.grid_cache.set_blocked_cells(cells);
    }

    /// ADR-043 — reconcile the SIMULATED population against the world's draw, once a second.
    ///
    /// The world holds infinitely many robapieles and simulates a handful: the ones a real player
    /// could plausibly meet. Everything else is a hash that has not been asked yet. Without this
    /// gate the host would pay full AI — pathfinding included — for creatures nobody can see, and
    /// the step budget goes with it.
    ///
    /// Deactivation is deliberately NOT the mirror image of activation:
    /// - the radii differ (hysteresis), so a player loitering at the boundary does not make an
    ///   avatar blink in and out on every client;
    /// - only a phantom in WANDER may be put away. One that is stalking, searching or charging has
    ///   left its anchor by definition, and deleting it would teleport it back there the moment it
    ///   re-activated — which reads as a bug, not as having lost it. Escaping already has its own
    ///   designed mechanic: SEARCH and its 12 s surrender (ADR-040).
    pub(super) fn sync_population(
        &mut self,
        net: &mut NetworkManager,
        host_player_pos: Vec3,
        dt: f32,
    ) {
        self.population_sync_in -= dt;
        if self.population_sync_in > 0.0 {
            return;
        }
        self.population_sync_in = PHANTOM_POPULATION_SYNC_INTERVAL;

        // Every REAL player: the host's own local player (which is not a peer) plus real peers.
        // Collected up front so no borrow of `net` survives into the mutation below.
        let players: Vec<Vec3> = std::iter::once(host_player_pos)
            .chain(
                net.peers
                    .iter()
                    .filter(|(id, _)| !net.is_phantom(**id))
                    .map(|(_, p)| Vec3::from_array(p.position)),
            )
            .collect();

        // ── Put away the ones nobody is near any more ──
        let mut retired: Vec<PeerId> = Vec::new();
        for m in &self.movers {
            if m.anchor.is_none() || m.state != PhantomState::Wander {
                continue; // hand-placed, or busy with a player — see the doc comment
            }
            let Some(peer) = net.peers.get(&m.id) else {
                continue;
            };
            let here = Vec3::from_array(peer.position);
            let layer = world_pos_to_layer(here.y);
            let far = players.iter().all(|p| {
                world_pos_to_layer(p.y) != layer || p.distance_xz(here) > PHANTOM_DEACTIVATE_RADIUS
            });
            if far {
                retired.push(m.id);
            }
        }
        for id in &retired {
            net.despawn_phantom(*id);
        }
        self.movers.retain(|m| !retired.contains(&m.id));

        // ── Wake up the ones somebody walked near ──
        if self.movers.len() >= self.active_cap {
            return;
        }
        let taken: std::collections::HashSet<PhantomAnchor> =
            self.movers.iter().filter_map(|m| m.anchor).collect();
        let mut seen_blocks: std::collections::HashSet<((i32, i32), u8)> =
            std::collections::HashSet::new();
        let mut candidates: Vec<PhantomCandidate> = Vec::new();
        let mut drawn: Vec<[f32; 3]> = Vec::new();

        for p in &players {
            // ADR-043 D-ACTIVACIÓN: the player's OWN layer only. Distance in XZ alone would wake a
            // creature standing on layer 1 directly under your feet — paying AI for something that
            // can neither reach you nor be seen by you.
            let layer = world_pos_to_layer(p.y);
            let (bx0, bz0) = phantom_spawn::block_of(
                p.x - PHANTOM_ACTIVATE_RADIUS,
                p.z - PHANTOM_ACTIVATE_RADIUS,
            );
            let (bx1, bz1) = phantom_spawn::block_of(
                p.x + PHANTOM_ACTIVATE_RADIUS,
                p.z + PHANTOM_ACTIVATE_RADIUS,
            );
            for bx in bx0..=bx1 {
                for bz in bz0..=bz1 {
                    if !seen_blocks.insert(((bx, bz), layer)) {
                        continue; // already offered by another player this scan
                    }
                    phantom_spawn::draw_into(
                        net.world_seed,
                        (bx, bz),
                        layer,
                        level4_scaled_density(
                            self.density_scale,
                            (bx, bz),
                            layer,
                            net.level4.epoch,
                        ),
                        &mut drawn,
                    );
                    for (index, pos) in drawn.iter().copied().enumerate() {
                        if !level4_spot_is_usable((bx, bz), layer, pos) {
                            continue; // sorteado fuera de la reserva — inalcanzable, no despertar
                        }
                        let key = ((bx, bz), layer, index as u8);
                        if taken.contains(&key) {
                            continue; // this one is already awake
                        }
                        // The block is a coarse filter; the radius is the real test, and it is
                        // measured against the drawn spot rather than the block, or a corner of a
                        // 200 m block would count as "near" from 280 m away.
                        let d = p.distance_xz(Vec3::from_array(pos));
                        // ADR-050: a floor as well as a ceiling — see PHANTOM_MIN_SPAWN_DISTANCE.
                        // Measured against EVERY player, not just this one, or a creature could
                        // still wake in somebody else's face while being far enough from you.
                        if d <= PHANTOM_ACTIVATE_RADIUS
                            && players.iter().all(|q| {
                                q.distance_xz(Vec3::from_array(pos)) >= PHANTOM_MIN_SPAWN_DISTANCE
                            })
                        {
                            candidates.push(PhantomCandidate {
                                distance: d,
                                anchor: key,
                                position: pos,
                            });
                        }
                    }
                }
            }
        }

        // Nearest first, so when the cap binds it keeps the creatures a player is most likely to
        // actually run into rather than whichever block the scan happened to reach first.
        candidates.sort_by(|a, b| a.distance.total_cmp(&b.distance));
        for cand in candidates {
            if self.movers.len() >= self.active_cap {
                break;
            }
            let (victim_name, victim_bound) = choose_victim_name_for(net, self.next_victim_slot);
            let id = net.spawn_phantom(&victim_name, cand.position);
            // The SNAPPED position is the anchor for the leash, not the raw draw: `spawn_phantom`
            // may have moved it up to 7.5 m to find a walkable cell, and leashing it to a spot
            // inside a wall would have it pressing against that wall forever.
            let spawn_pos = net
                .peers
                .get(&id)
                .map(|p| Vec3::from_array(p.position))
                .unwrap_or_else(|| Vec3::from_array(cand.position));
            // …and that snap is exactly why the minimum distance has to be re-checked HERE rather
            // than only on the drawn spot: a draw sitting a safe 36 m away can be pulled to 28 m by
            // the search for a walkable cell, which is back inside the radius this exists to keep
            // clear. Cheaper to undo the spawn than to guess the resolver's margin.
            if players
                .iter()
                .any(|p| p.distance_xz(spawn_pos) < PHANTOM_MIN_SPAWN_DISTANCE)
            {
                net.despawn_phantom(id);
                continue;
            }
            self.add_anchored(
                id,
                PHANTOM_INITIAL_HEADING,
                spawn_pos,
                victim_bound,
                Some(cand.anchor),
            );
        }
    }

    /// ADR-047 D5 — a noise WAKES population, it does not only steer the already-awake.
    ///
    /// The contradiction this closes: ADR-041 designs a 500 m gunshot whose point is a ~2,8 min
    /// journey toward you, but only simulated phantoms can hear (`hear_noises` iterates `movers`)
    /// and ADR-043 only ever wakes them within `PHANTOM_ACTIVATE_RADIUS` = 150 m of a player.
    /// Past that the creature is a hash nobody has asked yet, so the long approach could not
    /// happen at all. Neither ADR noticed the other.
    ///
    /// Runs BEFORE the 1 Hz reconcile gate and on every entity tick, because `hear_noises` drains
    /// `pending_noises` on the very next `step` — waiting for the reconcile would find the queue
    /// already empty. It reads the queue WITHOUT draining it, exactly like `sync_population` runs
    /// before `step` so a freshly woken creature gets a full tick (ADR-043's own ordering choice).
    /// The one it wakes goes to SEARCH in that same `step`, so the next reconcile will not retire
    /// it — ADR-043's "only retire in WANDER" rule already protects a traveller.
    ///
    /// TWO caps, and they are the load-bearing part: `PHANTOM_NOISE_ACTIVATE_MAX` per noise, and
    /// the global `active_cap` on top. A 500 m radius sweeps ~785.000 m²; without them one shot
    /// would wake everything inside it and blow the step budget ADR-043 measured and protected.
    pub(super) fn wake_for_noises(&mut self, net: &mut NetworkManager) {
        if net.pending_noises.is_empty() || self.movers.len() >= self.active_cap {
            return;
        }
        // Cloned so the spawn below can borrow `net` mutably. Only allocates on a tick where
        // somebody actually made a noise, which is rare by construction.
        let noises = net.pending_noises.clone();
        let mut taken: std::collections::HashSet<PhantomAnchor> =
            self.movers.iter().filter_map(|m| m.anchor).collect();
        let mut drawn: Vec<[f32; 3]> = Vec::new();

        for (raw, loudness) in noises {
            if self.movers.len() >= self.active_cap {
                return;
            }
            let source = Vec3::from_array(raw);
            // The noise's OWN layer: same reason as ADR-043's per-layer activation, and it agrees
            // with the layer test `hear_noises` applies — waking something that then cannot hear
            // the noise would be pure cost.
            let layer = world_pos_to_layer(source.y);
            let (bx0, bz0) = phantom_spawn::block_of(source.x - loudness, source.z - loudness);
            let (bx1, bz1) = phantom_spawn::block_of(source.x + loudness, source.z + loudness);

            let mut candidates: Vec<PhantomCandidate> = Vec::new();
            for bx in bx0..=bx1 {
                for bz in bz0..=bz1 {
                    phantom_spawn::draw_into(
                        net.world_seed,
                        (bx, bz),
                        layer,
                        level4_scaled_density(
                            self.density_scale,
                            (bx, bz),
                            layer,
                            net.level4.epoch,
                        ),
                        &mut drawn,
                    );
                    for (index, pos) in drawn.iter().copied().enumerate() {
                        if !level4_spot_is_usable((bx, bz), layer, pos) {
                            continue; // sorteado fuera de la reserva — inalcanzable, no despertar
                        }
                        let key = ((bx, bz), layer, index as u8);
                        if taken.contains(&key) {
                            continue; // already awake, or already claimed by an earlier noise
                        }
                        let d = source.distance_xz(Vec3::from_array(pos));
                        if d <= loudness {
                            candidates.push(PhantomCandidate {
                                distance: d,
                                anchor: key,
                                position: pos,
                            });
                        }
                    }
                }
            }

            // Nearest to the SOURCE first: the ones that would plausibly have heard it loudest.
            candidates.sort_by(|a, b| a.distance.total_cmp(&b.distance));
            candidates.truncate(PHANTOM_NOISE_ACTIVATE_MAX);
            for cand in candidates {
                if self.movers.len() >= self.active_cap {
                    break;
                }
                let (victim_name, victim_bound) =
                    choose_victim_name_for(net, self.next_victim_slot);
                let id = net.spawn_phantom(&victim_name, cand.position);
                let spawn_pos = net
                    .peers
                    .get(&id)
                    .map(|p| Vec3::from_array(p.position))
                    .unwrap_or_else(|| Vec3::from_array(cand.position));
                taken.insert(cand.anchor);
                info!(
                    "MPTRACE step=PH_NOISE event=phantom_woken_by_noise phantom_id={} dist={:.0} loudness={:.0}",
                    id, cand.distance, loudness
                );
                self.add_anchored(
                    id,
                    PHANTOM_INITIAL_HEADING,
                    spawn_pos,
                    victim_bound,
                    Some(cand.anchor),
                );
            }
        }
    }

    pub(super) fn add(&mut self, id: PeerId, heading: f32, spawn_pos: Vec3, victim_bound: bool) {
        self.add_anchored(id, heading, spawn_pos, victim_bound, None);
    }

    /// `add`, plus the `(block, layer)` the world drew this one from (ADR-043). The un-anchored
    /// `add` remains for the hand-placed debug phantom, which must stay exempt from the population
    /// reconciler — it was put somewhere on purpose and nothing should tidy it away.
    pub(super) fn add_anchored(
        &mut self,
        id: PeerId,
        heading: f32,
        spawn_pos: Vec3,
        victim_bound: bool,
        anchor: Option<PhantomAnchor>,
    ) {
        let now = Instant::now();
        let victim_slot = self.next_victim_slot;
        self.next_victim_slot = self.next_victim_slot.wrapping_add(1);
        self.movers.push(PhantomMover {
            id,
            anchor,
            victim_slot,
            heading,
            spawn_pos,
            pickup_until: None,
            next_pickup_at: now + PHANTOM_PICKUP_INTERVAL,
            stare_until: None,
            next_stare_at: now + PHANTOM_STARE_INTERVAL,
            victim_bound,
            state: PhantomState::Wander,
            state_timer: 0.0,
            spotted_duration: 0.0,
            last_known_player_pos: None,
            wander_pause_timer: 0.0,
            is_paused: false,
            heading_target: heading,
            nav_waypoints: Vec::new(),
            nav_cursor: 0,
            nav_goal: None,
            nav_age: 0.0,
            search_patience: PHANTOM_SEARCH_MAX,
            search_speed: PHANTOM_SEARCH_SPEED,
            noise_expiry: None,
            blocked_ticks: 0,
            wedge_cooldown: 0,
            hideouts: Vec::new(),
            hideouts_checked: 0,
            target_still_for: 0.0,
            last_seen_vel: None,
            search_fallback: None,
            peek_for: 0.0,
            peeked_this_search: false,
            ambush_cooldown: 0.0,
            ambushed_this_hunt: false,
            home_checked_this_hunt: false,
            strike_recover: 0.0,
            statue_cooldown: 0.0,
            traits: PhantomTraits::derive(self.world_seed, anchor, id),
            hesitate_timer: 0.0,
            target_id: None,
            pending_vocal: None,
            vocal_seq: 0,
            vocal_kind: 0,
            vocal_cooldown: 0.0,
            enraged_for: 0.0,
            hunger: derive_hunger(self.world_seed, anchor, id),
            sprint_blind_for: 0.0,
            stamina: PHANTOM_SPRINT_BURST_SECONDS,
            winded_for: 0.0,
            flee_goal: None,
            grab_timer: 0.0,
            grab_victim: None,
            // Staggered at birth like the breath, so several creatures never echo in chorus.
            echo_cooldown: PHANTOM_ECHO_MIN
                + rand::random::<f32>() * (PHANTOM_ECHO_MAX - PHANTOM_ECHO_MIN),
            sated_threshold: self.sated_threshold,
            mimic_pending: None,
            mimic_worn: (false, 0, 0),
            // Staggered at birth, not zeroed: several creatures waking on the same tick would
            // otherwise breathe in unison, which reads as one big thing rather than as several.
            breath_in: PHANTOM_BREATH_MIN
                + rand::random::<f32>() * (PHANTOM_BREATH_MAX - PHANTOM_BREATH_MIN),
            // ADR-080: looks straight ahead, has not drifted, has nothing to answer.
            look_pitch: 0,
            flank_offset: 0.0,
            chorus_pending: None,
            juke_for: 0.0,
            juke_side: 1.0,
            juke_cooldown: 0.0,
            juke_rolls: 0,
            lurk_goal: None,
            lurk_for: 0.0,
            lurked_this_hunt: false,
            lurk_rolls: 0,
        });
    }

    /// ADR-041 — drain the noises reported this tick and turn the ones a phantom could hear into an
    /// investigation. Loudness IS the radius: the client decides how far its weapon carries.
    pub(super) fn hear_noises(&mut self, net: &mut NetworkManager) {
        if net.pending_noises.is_empty() {
            return;
        }
        let noises = std::mem::take(&mut net.pending_noises);
        for (raw, loudness) in noises {
            let source = Vec3::from_array(raw);
            let source_layer = world_pos_to_layer(source.y);
            for i in 0..self.movers.len() {
                let id = self.movers[i].id;
                let from = match net.peers.get(&id) {
                    Some(p) => Vec3::from_array(p.position),
                    None => continue,
                };
                // ADR-047 D7 — distance is measured in XZ (`distance_xz`), so without a layer test
                // a shot on layer 0 summoned creatures from every floor stacked above and below it.
                // Same reasoning as the per-layer comparison `sync_population` already does.
                if world_pos_to_layer(from.y) != source_layer {
                    continue;
                }
                let dist = from.distance_xz(source);
                if dist > loudness {
                    continue;
                }

                // ADR-075 point 5 — INSISTING ENFURECES, handled AHEAD of the immunity list below.
                // A fleeing creature used to be flatly immune to every later noise — the right fix
                // for the burst-of-fire bug (many noises restarting the same flee on the same
                // trigger pull), but it also meant no amount of gunfire could ever turn one around.
                // Close enough, and given a beat to actually be running (not the SAME burst that
                // started the flee), a second noise turns it: rage ACCUMULATES rather than resets
                // — this is the only path that can ever enrage a sated creature — and it goes to
                // investigate instead of running. Anything else (too far, too soon) still falls
                // through to plain immunity, exactly as before this point existed.
                if self.movers[i].state == PhantomState::Flee {
                    if dist <= PHANTOM_RAGE_MAX_DISTANCE
                        && self.movers[i].state_timer >= PHANTOM_PROVOKE_MIN_FLEE_SECONDS
                    {
                        self.movers[i].enraged_for = (self.movers[i].enraged_for
                            + PHANTOM_RAGE_SECONDS * 2.0)
                            .min(PHANTOM_RAGE_CEILING_SECONDS);
                        self.movers[i].flee_goal = None;
                        self.movers[i].last_known_player_pos = Some(blur_noise(source, dist, id));
                        self.movers[i].state = PhantomState::Search;
                        self.movers[i].state_timer = 0.0;
                        self.movers[i].search_patience = PHANTOM_NOISE_SEARCH_PATIENCE;
                        self.movers[i].search_speed = PHANTOM_NOISE_TRAVEL_SPEED;
                        self.movers[i].noise_expiry = Some(PHANTOM_NOISE_EXPIRY);
                        self.movers[i].nav_waypoints.clear();
                        info!(
                            "MPTRACE step=PH_FLEE event=phantom_provoked phantom_id={} dist={:.0} enraged_for={:.1}",
                            id, dist, self.movers[i].enraged_for
                        );
                    }
                    continue;
                }

                // A committed lunge or a freeze is NOT interrupted by a noise somewhere else. It
                // already has a target in front of it; being distractible there would read as
                // stupidity, not as curiosity.
                //
                // ADR-050: `Grab` — a creature that let go of the player it is killing because
                // somebody fired a gun across the map would be dropping the one thing it committed
                // to.
                // ADR-051: `Unmasking` and `Hunting` join the list. The first is a committed beat
                // that a distant gunshot must not cancel halfway through; the second is a creature
                // that has already torn out of its skin for YOU and has no business wandering off
                // to investigate a noise.
                if matches!(
                    self.movers[i].state,
                    PhantomState::Sprint
                        | PhantomState::Statue
                        | PhantomState::Grab
                        | PhantomState::Unmasking
                        | PhantomState::Hunting
                        // ADR-076: a committed lunge, same reasoning as SPRINT — this one just
                        // happens to still be wearing the skin while it runs.
                        | PhantomState::Ambush
                ) {
                    continue;
                }
                let goal = blur_noise(source, dist, id);

                // ADR-050 point 6 — THE SAME SHOT MAKES TWO DIFFERENT ANIMALS. A creature that has
                // already eaten and hears a gun go off nearby bolts; a hungry one comes to look.
                // This is the clearest reading of the hunger model the player ever gets, and it is
                // free: no UI, no wire, just which way the thing runs.
                if self.movers[i].is_sated() && dist <= PHANTOM_RAGE_MAX_DISTANCE {
                    let away = Vec3::new(from.x - source.x, 0.0, from.z - source.z);
                    let len = (away.x * away.x + away.z * away.z).sqrt();
                    // Standing exactly on the noise: pick its current heading rather than dividing
                    // by zero. Any direction is away when the shot came from underfoot.
                    let dir = match len > 0.01 {
                        true => Vec3::new(away.x / len, 0.0, away.z / len),
                        false => {
                            let h = self.movers[i].heading;
                            Vec3::new(h.sin(), 0.0, h.cos())
                        }
                    };
                    self.movers[i].flee_goal = Some(Vec3::new(
                        from.x + dir.x * PHANTOM_FLEE_LOOKAHEAD,
                        from.y,
                        from.z + dir.z * PHANTOM_FLEE_LOOKAHEAD,
                    ));
                    self.movers[i].state = PhantomState::Flee;
                    self.movers[i].state_timer = 0.0;
                    self.movers[i].nav_waypoints.clear();
                    self.movers[i].pickup_until = None;
                    self.movers[i].stare_until = None;
                    info!(
                        "MPTRACE step=PH_FLEE event=phantom_startled phantom_id={} dist={:.0} hunger={:.2}",
                        id, dist, self.movers[i].hunger
                    );
                    continue;
                }

                self.movers[i].last_known_player_pos = Some(goal);
                self.movers[i].state = PhantomState::Search;
                self.movers[i].state_timer = 0.0;
                self.movers[i].search_patience = PHANTOM_NOISE_SEARCH_PATIENCE;
                self.movers[i].search_speed = PHANTOM_NOISE_TRAVEL_SPEED;
                self.movers[i].noise_expiry = Some(PHANTOM_NOISE_EXPIRY);

                // THE THEATRE STOPS. Reported from play-test: you shoot, they start coming, and
                // then one stops dead to mime picking something up. The fake-pickup and stare
                // freezes are checked at the TOP of the step loop, so they hold in EVERY state —
                // a creature that began a gesture in WANDER kept performing it for a full second
                // after being told to come for you, and with the gesture on a 6 s cycle it looked
                // like the trip kept resetting. A hunt cancels the act.
                self.movers[i].pickup_until = None;
                self.movers[i].stare_until = None;

                // A gunshot does not just attract this thing, it ANGERS it — and firing close to
                // one is a mistake you get to feel. Rage is refreshed, never accumulated: two shots
                // do not make it twice as angry, they keep it angry twice as long.
                //
                // ADR-050 point 12: only within `PHANTOM_RAGE_MAX_DISTANCE`. Farther out the shot
                // still redirects it (the SEARCH above already happened) but leaves it calm — being
                // enraged by something half a kilometre away is what turned one trigger pull into a
                // world-wide event.
                if dist <= PHANTOM_RAGE_MAX_DISTANCE {
                    let close = dist <= PHANTOM_RAGE_CLOSE_DISTANCE;
                    self.movers[i].enraged_for = match close {
                        true => PHANTOM_RAGE_SECONDS * 2.0,
                        false => PHANTOM_RAGE_SECONDS,
                    };
                }

                // Far away it ANSWERS, and that answer is the whole point of the mechanic: you fire,
                // and something enormous replies from out there. Close by, a grunt reads better —
                // at that range "it is RIGHT THERE" beats "it is somewhere".
                let voice = match dist >= PHANTOM_ANSWER_MIN_DISTANCE {
                    true => VOCAL_DISTANT_ANSWER,
                    false => VOCAL_NOISE_GRUNT,
                };
                self.movers[i].try_vocalize(voice);
                // The plan is deliberately NOT thrown away. A second shot is new information about
                // the same hunt, not a new hunt: the creature should keep walking and re-aim, and
                // the replan policy already rebuilds the route on its own when the goal drifts more
                // than PHANTOM_REPLAN_GOAL_DRIFT. Clearing here made every shot in a burst restart
                // the approach from scratch, which is what "it resets instead of tracking" looked
                // like in play-test.
                info!(
                    "MPTRACE step=PH_NOISE event=phantom_investigates phantom_id={} dist={:.0} goal=({:.1},{:.1}) error={:.1}",
                    id,
                    dist,
                    goal.x,
                    goal.z,
                    dist * PHANTOM_NOISE_ERROR_FRAC
                );
            }
        }
    }

    /// ADR-040 — the heading to walk this tick: the bearing to the next waypoint of a string-pulled
    /// route, replanned on a policy, falling back to the straight bearing when there is no plan.
    ///
    /// The fallback matters as much as the pathfinding: a failed search, a target standing in a
    /// cell grid_gen calls solid, or a goal outside the window all degrade to EXACTLY the
    /// pre-ADR-040 behaviour rather than freezing the creature. The worst case is the old code.
    ///
    /// At most ONE search per call, so the per-step cost stays the bounded thing it was measured to
    /// be. A plan is rebuilt when it is older than `PHANTOM_REPLAN_INTERVAL`, when the target has
    /// drifted `PHANTOM_REPLAN_GOAL_DRIFT` from the goal it was built for, or when it ran out.
    pub(super) fn steer_heading(
        &mut self,
        i: usize,
        layer: u8,
        from: Vec3,
        target: Vec3,
        dt: f32,
    ) -> f32 {
        let heading = self.steer_heading_raw(i, layer, from, target, dt);
        // ADR-082 piece B: the route (or the straight bearing) says WHERE to go; the whiskers say
        // how to get there without scraping. Applied here, at the single point every pursuing state
        // takes its heading from, rather than at the seven places a step is applied.
        if self.wg3.is_some() {
            return self.steer_around_walls_wg3(layer, from, heading);
        }
        crate::world::grid_gen::steer_around_walls(&mut self.grid_cache, layer, from, heading)
    }

    /// The pre-ADR-082 `steer_heading`, verbatim: route planning and waypoint following, with no
    /// idea that walls have sides. Kept separate so the whisker filter above is one wrapper instead
    /// of an edit at each of this function's three exits.
    fn steer_heading_raw(&mut self, i: usize, layer: u8, from: Vec3, target: Vec3, dt: f32) -> f32 {
        use crate::world::grid_gen::{cell_of, find_path, string_pull};

        let straight = |to: Vec3| {
            (to.x - from.x)
                .atan2(to.z - from.z)
                .rem_euclid(std::f32::consts::TAU)
        };

        self.movers[i].nav_age += dt;

        // LINE OF TRAVEL FIRST. A pathfinder must never come BETWEEN the creature and a player it
        // can already walk straight to. Two ways that bites, both seen in play-test: a player
        // pressed against a wall quantizes into a cell grid_gen calls solid, so the search returns
        // best effort and the route stops a cell short — the phantom parks at ~2 m and stares,
        // and the point-blank strike (dist < 1.5) never fires; and a route's last waypoint is a
        // cell CENTRE, which can sit up to half a cell diagonal away from where you actually are.
        // Checking the straight line is cheap and settles both: if it is clear, take it.
        //
        // …UNLESS the creature is wedged. ADR-075 fixed the root cause here: the shortcut used to
        // test a SEGMENT with no body radius while the resolver moves a 0.5 m body, so against an
        // inside corner the line read clear, the plan got thrown away, and the straight bearing
        // walked into the corner every single tick — the self-reinforcing corner-sticking loop.
        // `segment_is_clear_for_body` probes the line at the body's width, so that corner now
        // rejects the shortcut and the route survives. The wedge gate and its cooldown STAY as the
        // belt to this suspender: three probe rails are still not the resolver, and while wedged
        // the pathfinder must win regardless of what any shortcut claims
        // (see PHANTOM_WEDGE_HYSTERESIS_TICKS).
        self.movers[i].wedge_cooldown = self.movers[i].wedge_cooldown.saturating_sub(1);
        if !self.movers[i].prefers_route() && self.straight_is_clear(layer, from, target) {
            self.movers[i].nav_waypoints.clear();
            self.movers[i].nav_cursor = 0;
            self.movers[i].nav_goal = None;
            return straight(target);
        }

        // ADR-043 — REPLAN STAGGER, the lever ADR-040 wrote down for the day there were several.
        // `PHANTOM_REPLAN_INTERVAL` is 0.6 s = 6 steps, so movers that woke on the same tick keep
        // their `nav_age` in phase and every one of them comes due on the SAME step: the cost is
        // not the average, it is N searches in one 100 ms slot. Offsetting the permission by the
        // mover index spreads them over `PHANTOM_REPLAN_STRIDE` steps, capping the burst at
        // ceil(N / stride). A mover denied its turn keeps its previous route, and one with no route
        // at all falls back to the straight bearing — i.e. exactly the pre-ADR-040 behaviour, for
        // at most 0.2 s.
        //
        // A WEDGED mover skips the stagger. The stagger exists to spread a COST, and it can deny a
        // turn for up to 0.2 s; a creature grinding into a wall is the one case where waiting out
        // its turn is exactly wrong, and it is bounded by the same `active_cap` as everything else.
        let may_replan = self.movers[i].prefers_route()
            || (self.step_counter + i as u64).is_multiple_of(PHANTOM_REPLAN_STRIDE);
        let stale = may_replan
            && (self.movers[i].nav_waypoints.is_empty()
                || self.movers[i].nav_cursor >= self.movers[i].nav_waypoints.len()
                || self.movers[i].nav_age >= PHANTOM_REPLAN_INTERVAL
                || self.movers[i]
                    .nav_goal
                    .is_none_or(|g| g.distance_xz(target) > PHANTOM_REPLAN_GOAL_DRIFT));

        if stale {
            // ADR-041 — LONG-RANGE TRAVEL. The A* window is ±24 cells (±60 m); a noise 500 m away
            // is far outside it, and widening the window would cost 160k cells per search instead
            // of 2.4k. Instead the search is aimed at a SUB-GOAL on the bearing, just inside the
            // window: the same machinery, the same cost, and the journey keeps real local obstacle
            // avoidance the whole way instead of degrading to a blind straight line.
            // Stay inside the window edge (2 cells of margin).
            let reach = (crate::world::grid_gen::NAV_WINDOW_CELLS - 2) as f32
                * crate::world::grid_gen::CELL_SIZE_M;
            let far = from.distance_xz(target);
            let nav_target = if far > reach {
                let dx = (target.x - from.x) / far;
                let dz = (target.z - from.z) / far;
                Vec3::new(from.x + dx * reach, from.y, from.z + dz * reach)
            } else {
                target
            };
            // ADR-108 — con WG3 mandando, la ruta sale de SU navegación. Y sale más corta de
            // escribir: `wg3::nav::find_path` devuelve posiciones de mundo, no celdas, así que no
            // hace falta la conversión que `string_pull` hace aquí — el suavizado sí, y lo hace
            // `simplify` con una comprobación de recta que además mira COTAS.
            if let Some(cache) = self.wg3.take() {
                let mut raw = std::mem::take(&mut self.nav_world);
                crate::world::wg3::nav::find_path(&cache, from, nav_target, &mut raw);
                crate::world::wg3::nav::simplify(
                    &cache,
                    from,
                    &raw,
                    crate::world::grid_gen::PHANTOM_BODY_RADIUS,
                    &mut self.movers[i].nav_waypoints,
                );
                self.nav_world = raw;
                self.wg3 = Some(cache);
            } else {
                let start = cell_of(from);
                let goal = cell_of(nav_target);
                find_path(
                    &mut self.grid_cache,
                    layer,
                    start,
                    goal,
                    &mut self.nav_scratch,
                    &mut self.nav_cells,
                );
                let cells = std::mem::take(&mut self.nav_cells);
                string_pull(
                    &mut self.grid_cache,
                    layer,
                    from.y,
                    &cells,
                    &mut self.movers[i].nav_waypoints,
                );
                self.nav_cells = cells; // give the buffer back; no per-search allocation
            }
            self.movers[i].nav_cursor = 0;
            self.movers[i].nav_goal = Some(target);
            self.movers[i].nav_age = 0.0;
            self.nav_replans += 1;
        }

        // Consume waypoints already reached. Done AFTER a possible replan so a fresh plan whose
        // first waypoint is underfoot does not cost a wasted tick.
        while self.movers[i].nav_cursor < self.movers[i].nav_waypoints.len() {
            let wp = self.movers[i].nav_waypoints[self.movers[i].nav_cursor];
            let arrive = if self.wg3.is_some() {
                PHANTOM_WAYPOINT_ARRIVE_WG3
            } else {
                PHANTOM_WAYPOINT_ARRIVE
            };
            if from.distance_xz(wp) <= arrive {
                self.movers[i].nav_cursor += 1;
            } else {
                break;
            }
        }

        match self.movers[i].nav_waypoints.get(self.movers[i].nav_cursor) {
            Some(wp) => straight(*wp),
            None => straight(target), // no plan (or plan exhausted): the old straight bearing
        }
    }

    /// ADR-016 (identity phase): once a real (non-phantom) peer is connected, any phantom still
    /// on its fallback name adopts that peer's NAME — cloning the victim's identity while keeping
    /// its OWN unique id (never the victim's id, which would collide the client's `_active[id]`).
    /// The rename rides the existing roster/PeerList + ADR-015 relay (no schema). One-shot per
    /// phantom; cheap no-op once all are bound or no real peer exists.
    /// ADR-043 — each unbound phantom takes the victim of ITS OWN `victim_slot`, not one name
    /// resolved once and stamped on all of them. That old shape was invisible with a single debug
    /// phantom and self-defeating with a populated world: several creatures in view, all wearing
    /// the same name, which is precisely the disguise giving itself away.
    pub(super) fn rebind_unbound_victims(&mut self, net: &mut NetworkManager) {
        if self.movers.iter().all(|m| m.victim_bound) {
            return;
        }
        let names = net.real_peer_names();
        if names.is_empty() {
            return;
        }
        for m in self.movers.iter_mut().filter(|m| !m.victim_bound) {
            let victim_name = names[m.victim_slot % names.len()].clone();
            if let Some(peer) = net.peers.get_mut(&m.id) {
                peer.name = victim_name.clone();
            }
            m.victim_bound = true;
            info!(
                "MPTRACE step=PH5 event=phantom_victim_bound phantom_id={} victim_name={}",
                m.id, victim_name
            );
        }
    }

    // ─── The FSM, one method per state ───────────────────────────────────────────────────────────
    //
    // Each takes the mover's tick facts and the shared per-tick context, and is dispatched from the
    // `match` at the bottom of `step`. They return instead of `continue`-ing, which is exactly
    // equivalent: that `match` is the LAST statement in the loop body, so there has never been any
    // code after it for a `continue` to skip. Anything added after the match in `step` would break
    // that equivalence silently — put it in the post-loop pass instead.

    /// SPOTTED — it has seen you, and stares. Frozen, facing you, for a randomised window before it
    /// commits to the shadow: to STALK when the stare runs out, back to WANDER if you got far
    /// enough away, or straight into a lunge on an unpredictable roll.
    fn tick_spotted(&mut self, mt: MoverTick, ctx: &mut TickCtx<'_>) {
        let MoverTick {
            i,
            id,
            from,
            target,
            ..
        } = mt;
        let still = match target {
            Some((_, _, dist, _)) => dist <= PHANTOM_DETECT_RADIUS * 1.5,
            None => false,
        };
        if !still {
            self.movers[i].state = PhantomState::Wander;
            self.movers[i].state_timer = 0.0;
            return;
        }
        let (_, tpos, _dist, _) = target.unwrap(); // still ⇒ Some
        let heading = (tpos.x - from.x)
            .atan2(tpos.z - from.z)
            .rem_euclid(std::f32::consts::TAU);
        self.movers[i].heading = heading;

        // Stare done → STALK (checked before the random lunge so it's deterministic
        // once the window passes).
        if self.movers[i].state_timer >= self.movers[i].spotted_duration {
            self.movers[i].state = PhantomState::Stalk;
            self.movers[i].state_timer = 0.0;
            info!(
                "MPTRACE step=PH_STALK event=phantom_stalk phantom_id={}",
                id
            );
            return;
        }
        // Unpredictable lunge mid-stare (scarier when imprevisible), scaled by how
        // erratic this particular creature is. ADR-050 point 4: never while sated —
        // the same gate STALK applies, or a full creature could still open with a
        // charge and the whole hunger model would be invisible on first contact.
        if !self.movers[i].is_sated()
            && rand::random::<f32>() < PHANTOM_SPRINT_RANDOM_CHANCE * self.movers[i].impulse()
        {
            self.movers[i].enter_sprint();
            info!(
                "MPTRACE step=PH_SPRINT event=phantom_sprint phantom_id={} note=from_spotted_random",
                id
            );
            return;
        }
        let yaw = heading.to_degrees().rem_euclid(360.0);
        if let Some(peer) = ctx.net.peers.get_mut(&id) {
            peer.update_player_state(from.to_array(), yaw, "idle".into());
        }
    }

    /// STATUE — the weeping-angel freeze. Dead still while the player keeps looking. They look away
    /// → STALK (it resumes the hunt, never back to WANDER); it tires after `PHANTOM_STATUE_MAX` →
    /// SPRINT, shoving the player if point-blank; loses them past `LOSE_RADIUS` → WANDER. Never
    /// reached mid-SPRINT: a committed lunge is not frozen.
    fn tick_statue(&mut self, mt: MoverTick, ctx: &mut TickCtx<'_>) {
        let MoverTick {
            i,
            id,
            from,
            target,
            ..
        } = mt;
        let lost = match target {
            Some((_, _, dist, _)) => dist > PHANTOM_LOSE_RADIUS,
            None => true,
        };
        if lost {
            self.movers[i].state = PhantomState::Wander;
            self.movers[i].state_timer = 0.0;
            return;
        }
        // ADR-047: `tid` is BOUND, not discarded. It used to be `_` here and in SPRINT,
        // and that discard is where the mis-routed damage came from.
        let (tid, tpos, dist, tyaw) = target.unwrap();
        self.movers[i].last_known_player_pos = Some(tpos);

        // Tired of the game → lunge (checked before the look test so the timeout always
        // wins once it elapses). If point-blank, also SHOVE the player — the client
        // applies the impulse (SetVelocity); the backend only signals the direction.
        if self.movers[i].state_timer >= PHANTOM_STATUE_MAX * self.movers[i].traits.statue_scale {
            let dx = tpos.x - from.x;
            let dz = tpos.z - from.z;
            let len = (dx * dx + dz * dz).sqrt();
            if dist < PHANTOM_KNOCKBACK_RANGE && len > 0.001 {
                self.attacks.push(PhantomAttack {
                    victim: tid,
                    kind: PhantomAttackKind::Knockback(
                        dx / len * PHANTOM_KNOCKBACK_FORCE,
                        dz / len * PHANTOM_KNOCKBACK_FORCE,
                    ),
                });
            }
            // ADR-050 point 4: a SATED creature still gets bored of the game and still shoves you
            // if you are close enough — but it does not follow the shove with a charge. It goes
            // back to shadowing, WEARING THE SKIN (ADR-051 point 4: no hunger, no unmasking, no
            // matter how long you stared at it). Being knocked on your back by something that then
            // simply resumes walking behind you is the most unsettling thing this state can do.
            if self.movers[i].is_sated() {
                self.movers[i].state = PhantomState::Stalk;
                self.movers[i].state_timer = 0.0;
                self.movers[i].statue_cooldown = PHANTOM_STATUE_COOLDOWN;
                info!(
                    "MPTRACE step=PH_STALK event=phantom_statue_bored_sated phantom_id={} knockback={}",
                    id,
                    dist < PHANTOM_KNOCKBACK_RANGE
                );
                return;
            }
            // ADR-051 points 2-3: hungry and still being watched → it does not charge yet. It comes
            // APART first: still, screaming, still wearing the face, for a beat and a half. The
            // charge is what happens at the end of that.
            self.movers[i].state = PhantomState::Unmasking;
            self.movers[i].state_timer = 0.0;
            self.movers[i].vocal_cooldown = 0.0; // this one always gets to be heard
            self.movers[i].try_vocalize(VOCAL_UNMASK_SCREAM);
            info!(
                "MPTRACE step=PH_UNMASK event=phantom_unmask_begins phantom_id={} hunger={:.2} knockback={}",
                id,
                self.movers[i].hunger,
                dist < PHANTOM_KNOCKBACK_RANGE
            );
            return;
        }
        // Player looked away → resume stalking. WIDER cone than the one that froze it
        // (hysteresis): you have to look meaningfully away, not merely jitter on the
        // boundary, and the cooldown stops an immediate re-freeze.
        if !player_is_looking_at_within(tpos, tyaw, from, PHANTOM_STATUE_RELEASE_HALF_FOV) {
            self.movers[i].state = PhantomState::Stalk;
            self.movers[i].state_timer = 0.0;
            self.movers[i].statue_cooldown = PHANTOM_STATUE_COOLDOWN;
            info!(
                "MPTRACE step=PH_STALK event=phantom_statue_release phantom_id={}",
                id
            );
            return;
        }
        // Frozen in place, but it SLOWLY turns its head toward you (creepier than a
        // fixed facing): position held, only the heading eases toward the player.
        let to_player = (tpos.x - from.x)
            .atan2(tpos.z - from.z)
            .rem_euclid(std::f32::consts::TAU);
        self.movers[i].heading_target = to_player;
        let t = (PHANTOM_TURN_SPEED_STATUE * ctx.dt).min(1.0);
        self.movers[i].heading =
            lerp_heading(self.movers[i].heading, self.movers[i].heading_target, t);
        let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
        if let Some(peer) = ctx.net.peers.get_mut(&id) {
            peer.update_player_state(from.to_array(), yaw, "idle".into());
        }
    }

    /// SEARCH (ADR-040 Fase 4) — it lost you and walks, navigating, to the last place it saw you.
    /// Slower than a walk: it is looking, not commuting. Re-acquiring you resumes the hunt; running
    /// out of patience returns it to WANDER and it FORGETS, which is what makes hiding a real escape
    /// and not just a delay.
    /// ADR-075 point 2 — EVERY hunt-loss entry into SEARCH goes through here, and "every" matters
    /// twice over. First the feel: patience and speed scale with hunger and rage, so a creature
    /// that just lost you keeps looking in proportion to how much it wanted you — breaking line of
    /// sight stops reading as instant amnesia. Second, a latent bug this retires:
    /// `search_patience`/`search_speed`/`noise_expiry` are sticky mover fields reset only on the
    /// give-up path, so a hunt that entered SEARCH directly could inherit the 30 s patience and
    /// 4.5 m/s stride of an unrelated noise errand that had ended in re-acquisition.
    ///
    /// ADR-075 point 3 — this is also where the predictive lead is aimed. `last_known_player_pos`
    /// is still the point you were ACTUALLY seen at; `last_seen_vel` is the velocity at that same
    /// instant. If it clears the minimum speed, the goal becomes a point ahead of you on that
    /// heading — checked against the SAME steering shortcut the creature already trusts to walk,
    /// so a lead point behind a wall never gets chased into geometry — with the exact point held
    /// in `search_fallback` for the moment SEARCH arrives and finds nobody there.
    fn enter_search_after_hunt(&mut self, i: usize, layer: u8) {
        let m = &mut self.movers[i];
        let hunger_factor = 1.0 + (1.0 - m.hunger) * PHANTOM_SEARCH_HUNGER_BONUS;
        let rage_factor = match m.enraged_for > 0.0 {
            true => PHANTOM_SEARCH_RAGE_MULT,
            false => 1.0,
        };
        m.search_patience =
            (PHANTOM_SEARCH_MAX * hunger_factor * rage_factor).min(PHANTOM_NOISE_SEARCH_PATIENCE);
        m.search_speed = PHANTOM_SEARCH_HUNT_SPEED;
        m.noise_expiry = None; // a lost hunt is not a noise errand, whatever ran before it
        m.target_still_for = 0.0;
        m.peeked_this_search = false; // ADR-075: a fresh hunt earns a fresh peek
        m.home_checked_this_hunt = false; // ADR-089: and a fresh visit to your door
        m.state = PhantomState::Search;
        m.state_timer = 0.0;

        let exact = match m.last_known_player_pos {
            Some(p) => p,
            None => return, // nothing to predict from, and SEARCH needs no fallback either
        };
        let vel = m.last_seen_vel.unwrap_or((0.0, 0.0));
        let speed = (vel.0 * vel.0 + vel.1 * vel.1).sqrt();
        if speed < PHANTOM_SEARCH_LEAD_MIN_SPEED {
            return; // near-stationary: predicting would be aiming at noise
        }
        let dir = (vel.0 / speed, vel.1 / speed);
        // Two lead distances tried in order, both derived from the SAME observed heading — the
        // shorter one is what a target that slowed down after being seen would actually reach.
        for lead_seconds in [
            PHANTOM_SEARCH_LEAD_SECONDS,
            PHANTOM_SEARCH_LEAD_SECONDS * 0.5,
        ] {
            let predicted = Vec3::new(
                exact.x + dir.0 * speed * lead_seconds,
                exact.y,
                exact.z + dir.1 * speed * lead_seconds,
            );
            if crate::world::grid_gen::segment_is_clear(
                &mut self.grid_cache,
                layer,
                exact,
                predicted,
            ) {
                let m = &mut self.movers[i];
                m.last_known_player_pos = Some(predicted);
                m.search_fallback = Some(exact);
                return;
            }
        }
        // Neither lead point is reachable in a straight line: go straight to the exact spot, same
        // as before ADR-075. No fallback needed — there is nothing held in reserve.
    }

    fn tick_search(&mut self, mt: MoverTick, ctx: &mut TickCtx<'_>) {
        let MoverTick {
            i,
            id,
            from,
            layer,
            target,
        } = mt;
        // ADR-048 — IT SHRIEKS AS IT CLOSES ON YOU, without having seen you. The range
        // is deliberately WIDER than its sight (18 m vs 15), so following a shot toward
        // your position announces itself before it can possibly have spotted anything.
        // Disguise intact: this is the sound of the thing that still looks like a
        // player. The cooldown keeps it to one per approach rather than a siren.
        if let Some((_, _, dist, _)) = target {
            if dist <= PHANTOM_VOCAL_SEARCH_RANGE {
                self.movers[i].try_vocalize(VOCAL_SEARCH_SHRIEK);
            }
        }

        // Re-acquire on sight (crouching still shrinks the radius — stealth applies
        // while it hunts, not only while it patrols).
        if let Some((tid, tpos, dist, _)) = target {
            let crouched = target_is_crouched(ctx.net, tid, ctx.host_crouch);
            let sight = if crouched {
                PHANTOM_DETECT_RADIUS * PHANTOM_CROUCH_SIGHT_FACTOR
            } else {
                PHANTOM_DETECT_RADIUS
            };
            if dist <= sight && in_view_cone(self.movers[i].heading, from, tpos) {
                self.movers[i].last_known_player_pos = Some(tpos);
                self.movers[i].state = PhantomState::Stalk;
                self.movers[i].state_timer = 0.0;
                self.movers[i].hideouts_checked = 0; // see the give-up path
                self.movers[i].peeked_this_search = false; // ADR-075: a fresh hunt earns a fresh peek
                info!(
                    "MPTRACE step=PH_SEARCH event=phantom_reacquired phantom_id={} dist={:.2}",
                    id, dist
                );
                return;
            }
        }

        let goal = match self.movers[i].last_known_player_pos {
            Some(g) => g,
            None => {
                self.movers[i].state = PhantomState::Wander;
                self.movers[i].state_timer = 0.0;
                return;
            }
        };

        let dist_to_goal = from.distance_xz(goal);

        // ADR-075 point 4 — PEEK. Within earshot of the goal but with no straight line to it (a
        // corner sits between the creature and the spot), pause once per search before rounding
        // it. The THIN line is the right test here — the question is "can I see the goal", not
        // "can my body get there", which is exactly what `segment_is_clear_for_body` would answer.
        if !self.movers[i].peeked_this_search
            && dist_to_goal > PHANTOM_SEARCH_ARRIVE
            && dist_to_goal <= PHANTOM_PEEK_TRIGGER_DISTANCE
            && !crate::world::grid_gen::segment_is_clear(&mut self.grid_cache, layer, from, goal)
        {
            self.movers[i].peeked_this_search = true;
            self.movers[i].peek_for =
                PHANTOM_PEEK_MIN + rand::random::<f32>() * (PHANTOM_PEEK_MAX - PHANTOM_PEEK_MIN);
            self.movers[i].state = PhantomState::Peek;
            self.movers[i].state_timer = 0.0;
            self.movers[i].try_vocalize(VOCAL_STALK_BREATH);
            info!(
                "MPTRACE step=PH_SEARCH event=phantom_peeks phantom_id={} dist={:.2}",
                id, dist_to_goal
            );
            return;
        }

        // ADR-041: a noise in transit goes cold on its own clock, independent of the
        // arrival patience — otherwise a phantom that never reaches the spot would
        // walk toward a five-minute-old shot forever.
        let noise_cold = match self.movers[i].noise_expiry.as_mut() {
            Some(left) => {
                *left -= ctx.dt;
                *left <= 0.0
            }
            None => false,
        };

        let arrived = dist_to_goal <= PHANTOM_SEARCH_ARRIVE;

        // ADR-075 point 3 — SWEEP STEP 2: the predicted lead point turned up nobody. Fall back to
        // the EXACT spot you were last seen at, held in reserve since the hunt was lost. Checked
        // before hideouts on purpose — a predicted miss should not skip straight to old habits.
        if arrived {
            if let Some(fallback) = self.movers[i].search_fallback.take() {
                self.movers[i].last_known_player_pos = Some(fallback);
                self.movers[i].nav_waypoints.clear();
                self.movers[i].state_timer = 0.0;
                info!(
                    "MPTRACE step=PH_SEARCH event=phantom_search_falls_back phantom_id={} spot=({:.1},{:.1})",
                    id, fallback.x, fallback.z
                );
                return;
            }
        }

        // ADR-053 — BEFORE GIVING UP, CHECK WHERE YOU HID LAST TIME. Only on arrival: running out
        // of patience or losing the trail still ends the hunt, so a detour can never make a search
        // unbounded. This is the whole "it learns" mechanic — no model, a list of four places.
        if arrived && self.movers[i].noise_expiry.is_none() {
            if let Some(spot) = self.movers[i].recall_hideout(from) {
                self.movers[i].hideouts_checked += 1;
                self.movers[i].last_known_player_pos = Some(spot);
                self.movers[i].nav_waypoints.clear();
                // Fresh patience for the detour, or a long walk here would eat the whole window
                // and it would arrive only to give up on the doorstep.
                self.movers[i].state_timer = 0.0;
                info!(
                    "MPTRACE step=PH_SEARCH event=phantom_checks_hideout phantom_id={} spot=({:.1},{:.1}) checked={}",
                    id, spot.x, spot.z, self.movers[i].hideouts_checked
                );
                return;
            }
        }

        // ADR-089 — AND THEN, CHECK WHERE YOU LIVE. After the remembered hideouts (the fresh trail
        // beats the habit, ADR-075's order) and only once per hunt: your claim marker, if it is on
        // this layer and not a hike away. A hunt that ended near your base starts its next leg at
        // your door.
        if arrived
            && self.movers[i].noise_expiry.is_none()
            && !self.movers[i].home_checked_this_hunt
        {
            if let Some(home) = self.home_of_target(i, from, layer) {
                self.movers[i].home_checked_this_hunt = true;
                self.movers[i].last_known_player_pos = Some(home);
                self.movers[i].nav_waypoints.clear();
                self.movers[i].state_timer = 0.0;
                info!(
                    "MPTRACE step=PH_SEARCH event=phantom_checks_your_home phantom_id={} home=({:.1},{:.1})",
                    id, home.x, home.z
                );
                return;
            }
        }

        // ADR-092 — OR PRETEND TO GIVE UP. A real hunt (not a noise errand), a creature that is not
        // sated, one feint per hunt, and a temperament roll: instead of shrugging into Wander it
        // walks out of sight of where it last saw you, waits, and comes back. Decided here, on the
        // same condition the give-up fires on, so a feint can never keep a search alive by itself.
        if (noise_cold || self.movers[i].state_timer > self.movers[i].search_patience || arrived)
            && self.movers[i].noise_expiry.is_none()
            && !self.movers[i].is_sated()
            && !self.movers[i].lurked_this_hunt
            && self.movers[i].last_known_player_pos.is_some()
        {
            let m = &mut self.movers[i];
            m.lurk_rolls = m.lurk_rolls.wrapping_add(1);
            let key = ((m.id as u64) << 40) ^ ((m.lurk_rolls as u64) << 8) ^ 0x5A;
            let chance = match m.traits.is_hunter {
                true => PHANTOM_LURK_CHANCE_HUNTER,
                false => PHANTOM_LURK_CHANCE,
            };
            if chorus_delay_fraction(key) < chance {
                if let Some(spot) = self.pick_lurk_spot(i, from, layer) {
                    let m = &mut self.movers[i];
                    m.lurked_this_hunt = true;
                    m.lurk_goal = Some(spot);
                    let t = chorus_delay_fraction(key ^ 0xA5A5);
                    m.lurk_for = PHANTOM_LURK_MIN_SECONDS
                        + t * (PHANTOM_LURK_MAX_SECONDS - PHANTOM_LURK_MIN_SECONDS);
                    m.nav_waypoints.clear();
                    m.state = PhantomState::Lurk;
                    m.state_timer = 0.0;
                    info!(
                        "MPTRACE step=PH_LURK event=phantom_feints phantom_id={} spot=({:.1},{:.1}) wait={:.1}",
                        id, spot.x, spot.z, m.lurk_for
                    );
                    return;
                }
            }
        }

        // Swept the spot, out of patience, or the trail went cold → give up and forget.
        if noise_cold || self.movers[i].state_timer > self.movers[i].search_patience || arrived {
            // ADR-053: file where this hunt died. Next time it starts here instead of ending here.
            // Noise investigations are excluded — that goal is a blurred gunshot, not a place you
            // chose to hide, and remembering it would fill the list with map noise.
            if self.movers[i].noise_expiry.is_none() {
                self.movers[i].remember_hideout(goal);
            }
            info!(
                "MPTRACE step=PH_SEARCH event=phantom_gives_up phantom_id={} searched_for={:.1}s cold={} hideouts={}",
                id, self.movers[i].state_timer, noise_cold, self.movers[i].hideouts.len()
            );
            self.movers[i].last_known_player_pos = None;
            self.movers[i].state = PhantomState::Wander;
            self.movers[i].state_timer = 0.0;
            // Both exits of SEARCH clear the per-hunt counter, so the NEXT hunt gets its own
            // detours. Done on the way out rather than on the way in because SEARCH is entered
            // from five places and one of them would eventually be forgotten.
            self.movers[i].hideouts_checked = 0;
            self.movers[i].search_patience = PHANTOM_SEARCH_MAX;
            self.movers[i].search_speed = PHANTOM_SEARCH_SPEED;
            self.movers[i].noise_expiry = None;
            // ADR-075/076: no reserve point, no spent peek, and no spent ambush carries over into
            // the next hunt.
            self.movers[i].search_fallback = None;
            self.movers[i].peeked_this_search = false;
            self.movers[i].ambushed_this_hunt = false;
            self.movers[i].home_checked_this_hunt = false; // ADR-089: next hunt may check again
            self.movers[i].lurked_this_hunt = false; // ADR-092: and may feint again
                                                     // ADR-041: RE-ANCHOR the observation leash. It only acts in WANDER, so it
                                                     // never fought the journey — but without this the phantom would finish a
                                                     // 500 m trip and immediately start walking all the way back to its spawn.
            self.movers[i].spawn_pos = from;
            return;
        }

        let heading = self.steer_heading(i, layer, from, goal, ctx.dt);
        self.movers[i].heading_target = heading;
        let t = (PHANTOM_TURN_SPEED_STALK * ctx.dt).min(1.0);
        self.movers[i].heading =
            lerp_heading(self.movers[i].heading, self.movers[i].heading_target, t);
        let h = self.movers[i].heading;
        let dir = Vec3::new(h.sin(), 0.0, h.cos());
        let speed = self.movers[i].search_speed * self.movers[i].speed_scale();
        let desired = Vec3::new(
            from.x + dir.x * speed * ctx.dt,
            from.y,
            from.z + dir.z * speed * ctx.dt,
        );
        let moved = self.resolve_ex(layer, from, desired);
        // ADR-075: SEARCH now feeds the projected-advance detector too — it was the one travelling
        // state that did not, so an endless wall-slide here was invisible (a full block cleared the
        // route below, but a diagonal slide never registered as anything). Same mechanism, one more
        // call site; the 2026-08-03 invariant — the detector itself does not change — holds.
        let advance = (moved.pos.x - from.x) * dir.x + (moved.pos.z - from.z) * dir.z;
        self.movers[i].note_step_progress(advance, speed * ctx.dt);
        if moved.blocked {
            // Wedged against geometry: drop the plan so the next tick re-routes instead
            // of pressing into the same wall (the defect STALK/SPRINT used to have).
            self.movers[i].nav_waypoints.clear();
        }
        let yaw = h.to_degrees().rem_euclid(360.0);
        if let Some(peer) = ctx.net.peers.get_mut(&id) {
            peer.update_player_state(moved.pos.to_array(), yaw, "idle".into());
        }
    }

    /// PEEK (ADR-075 point 4) — the corner-peek `Search` was always missing. Dead still, facing
    /// the goal it cannot see past, breathing while it looks. Perception runs exactly as it does
    /// mid-search (this is a PAUSE inside a hunt, not a blind spot in one), so stepping into view
    /// here still gets you spotted straight into `Stalk`. Expires back into `Search` on its own
    /// clock — one peek, then it commits round the corner like the design always meant to.
    fn tick_peek(&mut self, mt: MoverTick, ctx: &mut TickCtx<'_>) {
        let MoverTick {
            i,
            id,
            from,
            target,
            ..
        } = mt;

        if let Some((tid, tpos, dist, _)) = target {
            let crouched = target_is_crouched(ctx.net, tid, ctx.host_crouch);
            let sight = if crouched {
                PHANTOM_DETECT_RADIUS * PHANTOM_CROUCH_SIGHT_FACTOR
            } else {
                PHANTOM_DETECT_RADIUS
            };
            if dist <= sight && in_view_cone(self.movers[i].heading, from, tpos) {
                self.movers[i].last_known_player_pos = Some(tpos);
                self.movers[i].state = PhantomState::Stalk;
                self.movers[i].state_timer = 0.0;
                self.movers[i].hideouts_checked = 0;
                info!(
                    "MPTRACE step=PH_PEEK event=phantom_reacquired phantom_id={} dist={:.2}",
                    id, dist
                );
                return;
            }
        }

        // Turn to face whatever it is peeking at. `unwrap_or(from)` is a can't-happen guard, not a
        // real branch: this state is only ever entered with a goal in hand.
        let goal = self.movers[i].last_known_player_pos.unwrap_or(from);
        let to_goal = (goal.x - from.x)
            .atan2(goal.z - from.z)
            .rem_euclid(std::f32::consts::TAU);
        self.movers[i].heading_target = to_goal;
        let t = (PHANTOM_TURN_SPEED_STALK * ctx.dt).min(1.0);
        self.movers[i].heading =
            lerp_heading(self.movers[i].heading, self.movers[i].heading_target, t);
        let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
        if let Some(peer) = ctx.net.peers.get_mut(&id) {
            peer.update_player_state(from.to_array(), yaw, "idle".into());
        }

        if self.movers[i].state_timer >= self.movers[i].peek_for {
            self.movers[i].state = PhantomState::Search;
            self.movers[i].state_timer = 0.0;
        }
    }

    /// UNMASKING (ADR-051 point 2) — the warning. Dead still, facing you, screaming, and STILL
    /// WEARING THE SKIN. At the end of it the skin tears and it comes straight at you.
    ///
    /// It cannot move and it cannot hit you here, and both are load-bearing: if the warning could
    /// reach you it would be a feint, and a feint is something players learn to ignore. Losing you
    /// during it puts the skin back on and nothing happened.
    fn tick_unmasking(&mut self, mt: MoverTick, ctx: &mut TickCtx<'_>) {
        let MoverTick {
            i,
            id,
            from,
            target,
            ..
        } = mt;

        // You got away mid-warning: it never tears. Back to hunting you clothed.
        let lost = match target {
            Some((_, _, dist, _)) => dist > PHANTOM_LOSE_RADIUS,
            None => true,
        };
        if lost {
            self.movers[i].state = PhantomState::Stalk;
            self.movers[i].state_timer = 0.0;
            return;
        }
        let (tid, tpos, _, _) = target.unwrap();
        self.movers[i].last_known_player_pos = Some(tpos);
        self.movers[i].last_seen_vel = ctx.target_vels.get(&tid).copied();

        if self.movers[i].state_timer >= self.unmask_seconds {
            // THE TEAR. Straight into the lunge, which is the first state that reveals — so the
            // `false→true` edge the client decorates lands exactly here, on this frame.
            //
            // BUG FIX — the tear was SILENT. `enter_sprint` emits `VOCAL_REVEAL`, but this state
            // screams on entry and `PHANTOM_VOCAL_COOLDOWN` is 6 s against an unmask beat of 1.6,
            // so the shared budget swallowed the scream every single time: the climax of the whole
            // sequence made no sound. Cleared here for the same reason the kill clears it — some
            // sounds are the event, not ambience.
            self.movers[i].vocal_cooldown = 0.0;
            self.movers[i].enter_sprint();
            info!(
                "MPTRACE step=PH_UNMASK event=phantom_skin_breaks phantom_id={} hunger={:.2}",
                id, self.movers[i].hunger
            );
            return;
        }

        // Stares straight through you while it comes apart. Faster head turn than STATUE: this is
        // not curiosity any more.
        let to_player = (tpos.x - from.x)
            .atan2(tpos.z - from.z)
            .rem_euclid(std::f32::consts::TAU);
        self.movers[i].heading_target = to_player;
        let t = (PHANTOM_TURN_SPEED_SPRINT * ctx.dt).min(1.0);
        self.movers[i].heading = lerp_heading(self.movers[i].heading, to_player, t);
        let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
        if let Some(peer) = ctx.net.peers.get_mut(&id) {
            peer.update_player_state(from.to_array(), yaw, "idle".into());
        }
    }

    /// HUNTING (ADR-051 point 5) — the unmasked chase between lunges.
    ///
    /// Identical shadowing to `Stalk`, with one difference that is the entire reason it exists: it
    /// is REVEALED and it never re-dresses. A failed lunge used to drop back into `Stalk`, which is
    /// a clothed state, so the skin flickered back on after every miss — "se cambia de piel todo el
    /// rato". Now the disguise falling is a one-way door until you break contact.
    fn tick_hunting(&mut self, mt: MoverTick, ctx: &mut TickCtx<'_>) {
        let MoverTick {
            i,
            id,
            from,
            layer,
            target,
        } = mt;
        let dist_opt = target.map(|(_, _, d, _)| d);
        if dist_opt.is_none_or(|d| d > PHANTOM_LOSE_RADIUS) {
            // THE ONLY WAY THE SKIN GOES BACK ON: it lost you. Both destinations are clothed
            // states, so the recomposition rides the state change and never a flag (invariant 1).
            if self.movers[i].last_known_player_pos.is_some() {
                self.enter_search_after_hunt(i, layer); // ADR-075
            } else {
                self.movers[i].state = PhantomState::Wander;
                self.movers[i].state_timer = 0.0;
            }
            info!(
                "MPTRACE step=PH_HUNT event=phantom_redresses phantom_id={} note=lost_contact",
                id
            );
            return;
        }
        let (tid, tpos, dist, tyaw_h) = target.unwrap();
        self.movers[i].last_known_player_pos = Some(tpos);
        self.movers[i].last_seen_vel = ctx.target_vels.get(&tid).copied();

        // ADR-075 — the same wedge escape STALK got on 2026-08-05. A hunt ground into geometry had
        // no exit of its own (only the 3-tick route drop), so a hunter stuck on a corner milled
        // there until the player left LOSE_RADIUS. Going to SEARCH re-dresses it — a state change,
        // never a flag (invariant 1) — and re-routes the approach, same as the stalk hatch.
        if self.movers[i].blocked_ticks >= PHANTOM_STALK_GIVEUP_TICKS {
            self.movers[i].blocked_ticks = 0;
            self.movers[i].nav_waypoints.clear();
            self.enter_search_after_hunt(i, layer); // ADR-075 (last_known written just above)
            info!(
                "MPTRACE step=PH_SEARCH event=phantom_hunt_gave_up phantom_id={} reason=wedged",
                id
            );
            return;
        }

        // No patience gate and no STATUE here: it is past pretending. It re-lunges as soon as the
        // strike recovery is spent, which is what makes an unmasked creature relentless rather than
        // merely visible.
        if self.movers[i].strike_recover <= 0.0 && self.movers[i].winded_for <= 0.0 {
            self.movers[i].enter_sprint();
            info!(
                "MPTRACE step=PH_SPRINT event=phantom_sprint phantom_id={} note=from_hunting dist={:.1}",
                id, dist
            );
            return;
        }

        // ADR-082: presses towards the CONTACT STANCE, same as the lunge. Without it, an unmasked
        // creature pressing on somebody backed into a wall aims at a cell the router cannot reach
        // and stalls a cell short — with its skin off, in plain sight, which is the worst possible
        // moment for it to look broken.
        let press_to = crate::world::grid_gen::contact_stance(
            &mut self.grid_cache,
            layer,
            from,
            tpos,
            crate::world::grid_gen::PHANTOM_BODY_RADIUS,
        )
        .unwrap_or(tpos);
        // ADR-088: same interception as the lunge, at the press speed this state actually moves at.
        let press_to = match ctx.target_vels.get(&tid).copied() {
            Some(vel) => intercept_point(
                &mut self.grid_cache,
                layer,
                from,
                tpos,
                vel,
                PHANTOM_STALK_CLOSE_SPEED,
            )
            .unwrap_or(press_to),
            None => press_to,
        };
        let to_player = self.steer_heading(i, layer, from, press_to, ctx.dt);
        // ADR-091: same sidestep as the lunge. Hunting never strikes itself (it re-lunges), so the
        // dodge is always allowed here.
        let to_player = self.juke_heading(i, ctx, from, tid, tpos, tyaw_h, dist, to_player, true);
        self.movers[i].heading_target = to_player;
        let t = (PHANTOM_TURN_SPEED_STALK * ctx.dt).min(1.0);
        self.movers[i].heading =
            lerp_heading(self.movers[i].heading, self.movers[i].heading_target, t);
        let heading = self.movers[i].heading;
        // ADR-075 point 2a — it PRESSES to point blank while it recovers instead of holding the
        // 9 m stalk band ("se te queda mirando a distancia considerable", the play-test words).
        // The floor stays above the strike reach: the re-lunge is still the only thing that hits.
        let (move_dir, speed) = match dist > PHANTOM_HUNT_PRESS_DISTANCE {
            true => (heading, PHANTOM_STALK_CLOSE_SPEED),
            false => (heading, 0.0),
        };
        let dir = Vec3::new(move_dir.sin(), 0.0, move_dir.cos());
        let desired = Vec3::new(
            from.x + dir.x * speed * ctx.dt,
            from.y,
            from.z + dir.z * speed * ctx.dt,
        );
        let resolved = self.resolve_pos(layer, from, desired);
        let advance = (resolved.x - from.x) * dir.x + (resolved.z - from.z) * dir.z;
        self.movers[i].note_step_progress(advance, speed * ctx.dt);
        let yaw = heading.to_degrees().rem_euclid(360.0);
        if let Some(peer) = ctx.net.peers.get_mut(&id) {
            peer.update_player_state(resolved.to_array(), yaw, "idle".into());
        }
    }

    /// GRAB (ADR-050 point 9) — it has hold of you, and the death is on a clock you can beat.
    ///
    /// This state is where the kill actually lives now. It used to be atomic: the blow from behind
    /// applied 100 damage in the same tick it was decided, and the client's grab was an epilogue
    /// that ran afterwards for 0.9 s and read no input at all — there was never an instant where
    /// you were held and alive, so there was nothing to escape. Here the creature holds still on
    /// top of the victim and one of three things happens: the victim struggles free
    /// (`net.pending_struggles`, ADR-050's `report_struggle`), the timer runs out and it feeds, or
    /// the victim goes away and it lets go.
    fn tick_grab(&mut self, mt: MoverTick, ctx: &mut TickCtx<'_>) {
        let MoverTick { i, id, from, .. } = mt;

        let Some(victim) = self.movers[i].grab_victim else {
            self.movers[i].state = PhantomState::Stalk;
            self.movers[i].state_timer = 0.0;
            return;
        };

        // BROKE FREE. Drained per victim, so one player's struggle can never release the creature
        // holding somebody else.
        if ctx.net.pending_struggles.remove(&victim) {
            self.attacks.push(PhantomAttack {
                victim,
                kind: PhantomAttackKind::GrabRelease,
            });
            // ADR-051 point 5: it does not get the skin back for having been shaken off. You are
            // still being hunted, and now by something you can see.
            self.movers[i].state = PhantomState::Hunting;
            self.movers[i].state_timer = 0.0;
            self.movers[i].grab_victim = None;
            self.movers[i].grab_timer = 0.0;
            // It does NOT feed. Still hungry, still there — you won the exchange, not the fight.
            // The recovery is what buys the room to run that surviving is supposed to be worth.
            self.movers[i].strike_recover = PHANTOM_GRAB_RECOVERY;
            self.movers[i].statue_cooldown = PHANTOM_GRAB_RECOVERY;
            info!(
                "MPTRACE step=PH_GRAB event=phantom_grab_broken phantom_id={} victim_id={}",
                id, victim
            );
            return;
        }

        self.movers[i].grab_timer -= ctx.dt;
        if self.movers[i].grab_timer <= 0.0 {
            self.attacks.push(PhantomAttack {
                victim,
                kind: PhantomAttackKind::Kill,
            });
            // SATED. Everything the old inline kill did, moved here with it: it stops hunting, goes
            // docile, re-anchors where it ended up, and roars once. The roar is doing real work —
            // it is the only way the player who just died learns, on respawn, that the thing which
            // killed them is not still coming. Rage does not survive a kill either.
            self.movers[i].hunger = 1.0;
            self.movers[i].enraged_for = 0.0;
            self.movers[i].ambushed_this_hunt = false; // ADR-076: the hunt is over, fed and done
            self.movers[i].state = PhantomState::Wander;
            self.movers[i].state_timer = 0.0;
            self.movers[i].grab_victim = None;
            self.movers[i].last_known_player_pos = None;
            self.movers[i].nav_waypoints.clear();
            self.movers[i].spawn_pos = from;
            self.movers[i].vocal_cooldown = 0.0; // this one always gets to be heard
            self.movers[i].try_vocalize(VOCAL_SATED_ROAR);
            info!(
                "MPTRACE step=PH_GRAB event=phantom_kill phantom_id={} victim_id={} note=grab_expired",
                id, victim
            );
            return;
        }

        // Holding. It does not move and it does not re-target: the pose is frozen on the victim,
        // and `revealed` is true throughout (ADR-050 point 10), so what the player is looking at
        // for these seconds is the real thing.
        let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
        if let Some(peer) = ctx.net.peers.get_mut(&id) {
            peer.update_player_state(from.to_array(), yaw, "pickup".into());
        }
    }

    /// FLEE (ADR-050 point 6) — it got startled and is clearing the area. Runs to a point away from
    /// whatever made the noise for `PHANTOM_FLEE_SECONDS`, then re-anchors where it ended up and
    /// goes back to patrolling. It does NOT reveal: a peer that bolts from a gunshot is exactly what
    /// a real player would do, and keeping the disguise on means you never quite know whether you
    /// just startled a teammate or the thing wearing his face.
    fn tick_flee(&mut self, mt: MoverTick, ctx: &mut TickCtx<'_>) {
        let MoverTick {
            i, id, from, layer, ..
        } = mt;

        // Done being scared: settle here rather than walking all the way home. Same re-anchor
        // `tick_search` and the post-kill path do, and for the same reason.
        if self.movers[i].state_timer >= PHANTOM_FLEE_SECONDS {
            self.movers[i].state = PhantomState::Wander;
            self.movers[i].state_timer = 0.0;
            self.movers[i].flee_goal = None;
            self.movers[i].spawn_pos = from;
            self.movers[i].nav_waypoints.clear();
            info!(
                "MPTRACE step=PH_FLEE event=phantom_calmed_down phantom_id={} pos=({:.1},{:.1})",
                id, from.x, from.z
            );
            return;
        }

        // No goal means nothing to run from — should not happen, but a creature standing still in
        // FLEE would read as a freeze, so fall back to patrolling rather than to nothing.
        let Some(goal) = self.movers[i].flee_goal else {
            self.movers[i].state = PhantomState::Wander;
            self.movers[i].state_timer = 0.0;
            return;
        };

        let heading = self.steer_heading(i, layer, from, goal, ctx.dt);
        self.movers[i].heading_target = heading;
        let t = (PHANTOM_TURN_SPEED_SPRINT * ctx.dt).min(1.0);
        self.movers[i].heading =
            lerp_heading(self.movers[i].heading, self.movers[i].heading_target, t);
        let h = self.movers[i].heading;
        let dir = Vec3::new(h.sin(), 0.0, h.cos());
        let speed = PHANTOM_FLEE_SPEED * self.movers[i].speed_scale();
        let desired = Vec3::new(
            from.x + dir.x * speed * ctx.dt,
            from.y,
            from.z + dir.z * speed * ctx.dt,
        );
        let moved = self.resolve_ex(layer, from, desired);
        if moved.blocked {
            // Cornered. Drop the route so the next tick re-plans instead of grinding: an animal
            // pressed into a dead end should look for another way out, not vibrate against it.
            self.movers[i].nav_waypoints.clear();
        }
        let yaw = h.to_degrees().rem_euclid(360.0);
        if let Some(peer) = ctx.net.peers.get_mut(&id) {
            peer.update_player_state(moved.pos.to_array(), yaw, "idle".into());
        }
    }

    /// STALK — it shadows the player at a held gap, breathing. Patience (or an unpredictable roll)
    /// runs out into a SPRINT; being looked at from close by freezes it into STATUE; losing the
    /// player past `LOSE_RADIUS` sends it to SEARCH the last-known spot, or to WANDER with no
    /// memory at all.
    fn tick_stalk(&mut self, mt: MoverTick, ctx: &mut TickCtx<'_>) {
        let MoverTick {
            i,
            id,
            from,
            layer,
            target,
        } = mt;
        let dist_opt = target.map(|(_, _, d, _)| d);
        if dist_opt.is_none_or(|d| d > PHANTOM_LOSE_RADIUS) {
            // ADR-040 Fase 4: it does not forget on the spot any more — it goes to look
            // where it last saw you. Only with no memory at all does it resume wandering.
            if self.movers[i].last_known_player_pos.is_some() {
                self.enter_search_after_hunt(i, layer); // ADR-075: scaled patience, sticky fields reset
            } else {
                self.movers[i].state = PhantomState::Wander;
                self.movers[i].state_timer = 0.0;
            }
            return;
        }
        let (tid, tpos, dist, tyaw) = target.unwrap();
        self.movers[i].last_known_player_pos = Some(tpos);
        self.movers[i].last_seen_vel = ctx.target_vels.get(&tid).copied();

        // ADR-075 point 2c — how long the target has been effectively still, off the same derived
        // speed the sound tiers already use. Below walking-noise speed counts as camping.
        let tspeed = ctx.target_speeds.get(&tid).copied().unwrap_or(0.0);
        if tspeed < PHANTOM_WALK_NOISE_SPEED {
            self.movers[i].target_still_for += ctx.dt;
        } else {
            self.movers[i].target_still_for = 0.0;
        }

        // ADR-051 follow-up — STALK GETS AN ESCAPE HATCH. Play-test: `blocked_ticks` hit 255 here,
        // a saturated u8, i.e. twenty-five seconds pressed into geometry with no way out. SPRINT
        // has had `PHANTOM_SPRINT_GIVEUP_TICKS` since ADR-040; this state never did, so a creature
        // that wedged while shadowing you stayed wedged until you walked out of LOSE_RADIUS.
        // Going to SEARCH is the honest answer: it knows roughly where you are and has to find
        // another way, which is exactly what a thing stuck behind a wall should do.
        if self.movers[i].blocked_ticks >= PHANTOM_STALK_GIVEUP_TICKS {
            self.movers[i].blocked_ticks = 0;
            self.movers[i].nav_waypoints.clear();
            self.enter_search_after_hunt(i, layer); // ADR-075 (last_known written just above)
            info!(
                "MPTRACE step=PH_SEARCH event=phantom_stalk_gave_up phantom_id={} reason=wedged",
                id
            );
            return;
        }

        // ADR-048 voice 3 — it breathes while it shadows you. Ambient, so it takes the
        // SHORT cooldown and can never mute the scream of the lunge that follows.
        self.movers[i].breath_in -= ctx.dt;
        if self.movers[i].breath_in <= 0.0 {
            self.movers[i].breath_in = PHANTOM_BREATH_MIN
                + rand::random::<f32>() * (PHANTOM_BREATH_MAX - PHANTOM_BREATH_MIN);
            // ADR-050: the same slot carries a different sound depending on how empty it is. This
            // is the ONLY channel through which hunger reaches the player, so it rides the ambient
            // rhythm rather than announcing itself — you notice the corridor started sounding
            // wrong, not that a state machine changed band.
            let voice = match self.movers[i].is_hunting_hungry() {
                true => VOCAL_HUNGRY_MOAN,
                false => VOCAL_STALK_BREATH,
            };
            self.movers[i].try_vocalize_for(voice, PHANTOM_BREATH_COOLDOWN);
        }

        // STATUE (weeping angel): the player is looking at it (horizontal cone) and is
        // close → freeze. Entered only from STALK; a committed SPRINT is never frozen.
        // A hunter never plays the statue game — it is not pretending to be scenery,
        // it is coming. That single exclusion is most of what makes one feel different.
        if dist < PHANTOM_STATUE_RANGE
            && !self.movers[i].traits.is_hunter
            && self.movers[i].statue_cooldown <= 0.0
            && player_is_looking_at(tpos, tyaw, from)
        {
            self.movers[i].state = PhantomState::Statue;
            self.movers[i].state_timer = 0.0;
            info!(
                "MPTRACE step=PH_STATUE event=phantom_statue phantom_id={} dist={:.2}",
                id, dist
            );
            return;
        }

        // ADR-076 — THE AMBUSH ROLL. Only while genuinely hungry (not merely "not sated" —
        // `is_hunting_hungry` is the tighter band), only once per hunt, only off cooldown, only
        // in range, and only when the player is NOT looking (the STATUE cone read backwards —
        // check above already returned if it WAS looking, so reaching here already means "no").
        // Scaled by `impulse()` exactly like the ordinary lunge roll: the same temperament that
        // makes a creature erratic makes it more likely to try the trick.
        if self.movers[i].is_hunting_hungry()
            && !self.movers[i].ambushed_this_hunt
            && self.movers[i].ambush_cooldown <= 0.0
            && self.movers[i].strike_recover <= 0.0
            && (PHANTOM_AMBUSH_MIN_DISTANCE..=PHANTOM_AMBUSH_MAX_DISTANCE).contains(&dist)
            && rand::random::<f32>() < PHANTOM_AMBUSH_CHANCE * self.movers[i].impulse()
        {
            self.movers[i].ambushed_this_hunt = true;
            self.movers[i].state = PhantomState::Ambush;
            self.movers[i].state_timer = 0.0;
            // Silence is the whole design here — no vocal on entry. It has to sound like nothing
            // is happening yet.
            info!(
                "MPTRACE step=PH_AMBUSH event=phantom_ambush_begins phantom_id={} dist={:.2}",
                id, dist
            );
            return;
        }

        // ADR-050 point 4 — THE HARD GATE. A sated creature does not charge, and no amount of
        // elapsed patience changes that: it shadows you, breathes, and waits to get hungry. This is
        // the line that removes "it attacks out of nowhere", because when it finally does come, the
        // reason existed for minutes beforehand and was visible in how it behaved.
        //
        // ADR-075 point 5 — the ONE exception: a PROVOKED saciada (rage from a second noise while
        // fleeing, the only path that can enrage a sated creature — see `hear_noises`) can still
        // charge. `patience()`/`impulse()` already suppress their own calm multiplier under the
        // same condition, so the gate and the numbers behind it agree with each other.
        if (!self.movers[i].is_sated() || self.movers[i].enraged_for > 0.0)
            && (self.movers[i].state_timer > self.movers[i].patience()
                || rand::random::<f32>()
                    < PHANTOM_SPRINT_RANDOM_CHANCE * 2.0 * self.movers[i].impulse())
        {
            self.movers[i].enter_sprint();
            info!(
                "MPTRACE step=PH_SPRINT event=phantom_sprint phantom_id={} dist={:.2}",
                id, dist
            );
            return;
        }

        // Maintain STALK_DISTANCE: close in if too far, ease back if too near (it
        // backs away while still facing you — unsettling), else hold.
        // ADR-050 point 8: a sated creature accompanies you at arm's length instead of breathing
        // down your neck. Same state, same code — a wider band is the whole difference between
        // being followed and being hunted.
        //
        // ADR-075 point 5: a PROVOKED saciada falls through to the tighter band (and is eligible
        // for the creep below) — same bridge condition as the gate and the two multipliers. A
        // creature you just made angry does not get to keep its polite fourteen metres.
        //
        // ADR-080 point 4 moved this ABOVE the steering (it reads only mover state, never the
        // heading): the flank point sits ON this band, so the radius has to exist before the route
        // to it can be planned.
        let band = match self.movers[i].is_sated() && self.movers[i].enraged_for <= 0.0 {
            true => PHANTOM_FOLLOW_DISTANCE,
            false => {
                // ADR-075 point 2c — CREEP-IN. A player glued to a wall used to earn a polite
                // statue at nine metres forever (the dead band held, and only SPRINT ever
                // closes). After a few still seconds the band starts shrinking toward its floor:
                // it drifts nearer, slowly, while you are not moving — and turning to face it
                // still freezes it through STATUE above.
                let still = self.movers[i].target_still_for;
                match still >= PHANTOM_CREEP_AFTER_SECONDS {
                    true => (PHANTOM_STALK_DISTANCE
                        - (still - PHANTOM_CREEP_AFTER_SECONDS) * PHANTOM_CREEP_RATE)
                        .max(PHANTOM_CREEP_MIN_BAND),
                    false => PHANTOM_STALK_DISTANCE,
                }
            }
        };
        // ADR-080 point 4: resolved BEFORE the steering, because when a flank is on it is the
        // flank point — not the player — that the route is planned to.
        let flank_goal = self.resolve_flank_goal(i, from, tpos, tyaw, band);
        // ADR-040: navigated heading. Where this used to point straight at the player —
        // and grind into whatever wall was in between — it now follows a string-pulled
        // route. With no plan it falls back to the straight bearing, i.e. the old code.
        let to_player = self.steer_heading(i, layer, from, flank_goal.unwrap_or(tpos), ctx.dt);
        // Ease toward the player instead of snapping (a 10 Hz snap reads as lag);
        // movement follows the smoothed heading → a curved, less robotic track.
        self.movers[i].heading_target = to_player;
        let t = (PHANTOM_TURN_SPEED_STALK * ctx.dt).min(1.0);
        self.movers[i].heading =
            lerp_heading(self.movers[i].heading, self.movers[i].heading_target, t);
        let heading = self.movers[i].heading;
        // ADR-080 point 4 — IT DOES NOT WAIT WHERE YOU LEFT IT. While the creature sits inside the
        // target's own view cone it walks around them, at band radius, towards the nearest edge of
        // that cone. Preference, never a compulsion: `steer_heading` already falls back to the
        // straight bearing when the route is unreachable, so a flank the geometry forbids simply
        // degrades to the approach this code has always done.
        let (move_dir, speed) = match flank_goal {
            Some(goal) => match from.distance_xz(goal) > PHANTOM_FLANK_ARRIVE {
                true => (heading, PHANTOM_STALK_CLOSE_SPEED),
                // Already out of sight at band distance: hold. Standing still just outside what
                // somebody can see is the whole point of having walked there.
                false => (heading, 0.0),
            },
            None if dist > band + 2.0 => {
                // ADR-050 point 16: closing is faster than wandering, or running away always worked.
                (heading, PHANTOM_STALK_CLOSE_SPEED)
            }
            None if dist < band => (
                (heading + std::f32::consts::PI).rem_euclid(std::f32::consts::TAU),
                PHANTOM_WALK_SPEED * 0.6,
            ),
            None => (heading, 0.0),
        };
        let dir = Vec3::new(move_dir.sin(), 0.0, move_dir.cos());
        let desired = Vec3::new(
            from.x + dir.x * speed * ctx.dt,
            from.y,
            from.z + dir.z * speed * ctx.dt,
        );
        let resolved = self.resolve_pos(layer, from, desired);
        // Wedge detection. `intended` is 0 while STALK holds its distance band, which
        // `note_step_progress` reads as "not trying to travel" rather than as stuck.
        let advance = (resolved.x - from.x) * dir.x + (resolved.z - from.z) * dir.z;
        self.movers[i].note_step_progress(advance, speed * ctx.dt);
        let yaw = heading.to_degrees().rem_euclid(360.0);
        if let Some(peer) = ctx.net.peers.get_mut(&id) {
            peer.update_player_state(resolved.to_array(), yaw, "idle".into());
        }
        if ctx.net.session_start.elapsed().as_millis() % 1000 < 120 {
            info!(
                "MPTRACE step=PH_STALK event=phantom_stalk_move phantom_id={} pos=({:.2},{:.2},{:.2}) dist={:.2} blocked_ticks={}",
                id, resolved.x, resolved.y, resolved.z, dist, self.movers[i].blocked_ticks
            );
        }
    }

    /// The WANDER detection rule, as one method: sight (distance + forward cone, shrunk by crouch),
    /// sound (three speed tiers, no cone, muted by crouch) and the lit torch (ADR-080). Returns
    /// `(detected, by_sound)`. Pulled out of `tick_wander` verbatim for ADR-092, whose `Lurk` has to
    /// notice a player exactly the way a patrolling creature does — it is hiding, not blind.
    fn detect_from_cover(
        &mut self,
        i: usize,
        id: PeerId,
        from: Vec3,
        layer: u8,
        target: Option<(PeerId, Vec3, f32, f32)>,
        ctx: &mut TickCtx<'_>,
    ) -> (bool, bool) {
        match target {
            Some((tid, tpos, dist, _)) => {
                let crouched = target_is_crouched(ctx.net, tid, ctx.host_crouch);
                // SIGHT: the cone is unchanged (behind it is behind it), but a
                // crouching target has to be much closer before it registers.
                let sight_radius = if crouched {
                    PHANTOM_DETECT_RADIUS * PHANTOM_CROUCH_SIGHT_FACTOR
                } else {
                    PHANTOM_DETECT_RADIUS
                };
                let normal =
                    dist <= sight_radius && in_view_cone(self.movers[i].heading, from, tpos);
                // SOUND: three tiers, and crouching mutes them all. This is the channel
                // that ignores the cone, so silencing it is what makes sneaking up
                // BEHIND the creature actually work.
                let speed = ctx.target_speeds.get(&tid).copied().unwrap_or(0.0);
                let hear_radius = if crouched || speed < PHANTOM_WALK_NOISE_SPEED {
                    0.0
                } else if speed > PHANTOM_RUN_SPEED_THRESHOLD {
                    PHANTOM_DETECT_RADIUS + PHANTOM_SOUND_BONUS
                } else {
                    PHANTOM_WALK_HEAR_RADIUS
                };
                let sound = hear_radius > 0.0 && dist <= hear_radius;
                // ADR-080 point 2 — LIGHT: a lit torch inside the same cone reads from much
                // further out. Unlike sound, this one DOES need geometry: a lamp two rooms away
                // through a wall is not visible, and the thin line (the one the eye uses, never
                // the body-radius variant — ADR-075 forbids it for sight predicates) is the
                // correct probe. The crouch factor is deliberately absent: crouching does not
                // put out a torch. Checked last and only when it can change the answer, so the
                // common case pays nothing for it.
                let light = !normal
                    && !sound
                    && dist <= PHANTOM_LIGHT_DETECT_RADIUS
                    && target_has_light_on(ctx.net, tid, ctx.host_light_on)
                    && in_view_cone(self.movers[i].heading, from, tpos)
                    && crate::world::grid_gen::segment_is_clear(
                        &mut self.grid_cache,
                        layer,
                        from,
                        tpos,
                    );
                if light {
                    info!(
                        "MPTRACE step=PH_LIGHT event=phantom_sees_your_light phantom_id={} target_id={} dist={:.1}",
                        id, tid, dist
                    );
                }
                // `by_sound` stays false for a light sighting: it IS a sighting, so it gets the
                // longer sight-stare rather than the twitchier sound one.
                (normal || sound || light, sound && !normal && !light)
            }
            None => (false, false),
        }
    }

    /// WANDER — erratic patrol and the detection gate. Keeps the slice-4 fake-pickup imitation, the
    /// metronomic stare tell, the organic "looking at a wall" pauses and the play-test observation
    /// leash. Detection is sight (distance + forward cone, shrunk by crouch) OR sound (three speed
    /// tiers, no cone, muted by crouch) — the sound channel is what makes sneaking up BEHIND one
    /// work at all.
    fn tick_wander(&mut self, mt: MoverTick, ctx: &mut TickCtx<'_>) {
        let MoverTick {
            i,
            id,
            from,
            layer,
            target,
        } = mt;
        // Detection: normally distance + forward cone. A RUNNING target (speed from
        // delta) is HEARD — detected from farther (DETECT + SOUND_BONUS) AND from any
        // direction (no cone). Sound-only detection reacts faster (a shorter stare).
        let (detected, by_sound) = self.detect_from_cover(i, id, from, layer, target, ctx);
        if detected {
            self.movers[i].state = PhantomState::Spotted;
            self.movers[i].state_timer = 0.0;
            let (lo, hi) = if by_sound {
                (PHANTOM_SPOTTED_SOUND_MIN, PHANTOM_SPOTTED_SOUND_MAX)
            } else {
                (PHANTOM_SPOTTED_MIN, PHANTOM_SPOTTED_MAX)
            };
            // Scaled by temperament: the same sighting makes one creature react almost
            // at once and another hold the stare for the best part of ten seconds.
            self.movers[i].spotted_duration =
                (lo + rand::random::<f32>() * (hi - lo)) * self.movers[i].traits.spotted_scale;
            self.movers[i].is_paused = false;
            info!(
                "MPTRACE step=PH_SPOTTED event=phantom_spotted phantom_id={} dur={:.1} by_sound={}",
                id, self.movers[i].spotted_duration, by_sound
            );
            return;
        }

        // Slice 4: start a faked-pickup gesture when the cooldown elapsed. Stamp the
        // "pickup" flank now and freeze; the top-of-loop gesture freeze holds the pose
        // for the rest of the window. (Anim-only — ADR-016 invariant.)
        // ADR-050 point 15: only perform it when somebody could plausibly be watching. The cooldown
        // is still armed either way, so a creature that spent its whole cycle alone does not owe a
        // burst of gestures the moment a player finally walks in.
        let has_audience = target.is_some_and(|(_, _, dist, _)| dist <= PHANTOM_THEATRE_RANGE);
        if ctx.now >= self.movers[i].next_pickup_at {
            // Re-arm whether or not it performs, so a creature that spent its whole cooldown alone
            // does not owe a gesture the instant somebody walks in.
            self.movers[i].next_pickup_at = ctx.now + PHANTOM_PICKUP_INTERVAL;
            if has_audience {
                self.movers[i].pickup_until = Some(ctx.now + PHANTOM_PICKUP_GESTURE);
                info!(
                    "MPTRACE step=PH4 event=phantom_fake_pickup phantom_id={} note=animation_field_only_no_real_pickup",
                    id
                );
                let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                if let Some(peer) = ctx.net.peers.get_mut(&id) {
                    peer.update_player_state(from.to_array(), yaw, "pickup".into());
                }
                return;
            }
        }

        // Tell #2: metronomic unnatural stillness (the tell is its regularity).
        if self.movers[i]
            .stare_until
            .is_some_and(|until| ctx.now >= until)
        {
            self.movers[i].stare_until = None;
        }
        if self.movers[i].stare_until.is_none() && ctx.now >= self.movers[i].next_stare_at {
            self.movers[i].stare_until = Some(ctx.now + PHANTOM_STARE_DURATION);
            self.movers[i].next_stare_at = ctx.now + PHANTOM_STARE_INTERVAL;
            info!(
                "MPTRACE step=PH6 event=phantom_tell_stare phantom_id={} note=behavioral_tell_unnatural_stillness",
                id
            );
        }
        if self.movers[i].stare_until.is_some() {
            let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
            if let Some(peer) = ctx.net.peers.get_mut(&id) {
                peer.update_player_state(from.to_array(), yaw, "idle".into());
            }
            return;
        }

        // Organic pause: stand still "looking at a wall". Count down if paused; else a
        // small per-tick chance to start one. On resume, turn to a new heading.
        if self.movers[i].is_paused {
            self.movers[i].wander_pause_timer -= ctx.dt;
            if self.movers[i].wander_pause_timer <= 0.0 {
                self.movers[i].is_paused = false;
                // ADR-089: near somebody's base, the next leg sometimes points at its doorstep
                // instead of anywhere. Still Wander — same walk, same pauses, same leash — it just
                // keeps drifting back to where you live.
                let ring = match rand::random::<f32>() < PHANTOM_HOME_PATROL_BIAS {
                    true => self.home_patrol_point(from, layer),
                    false => None,
                };
                self.movers[i].heading = match ring {
                    Some(goal) => (goal.x - from.x)
                        .atan2(goal.z - from.z)
                        .rem_euclid(std::f32::consts::TAU),
                    None => {
                        let turn = PHANTOM_TURN_MIN
                            + rand::random::<f32>() * (PHANTOM_TURN_MAX - PHANTOM_TURN_MIN);
                        (self.movers[i].heading + turn).rem_euclid(std::f32::consts::TAU)
                    }
                };
            } else {
                let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                if let Some(peer) = ctx.net.peers.get_mut(&id) {
                    peer.update_player_state(from.to_array(), yaw, "idle".into());
                }
                return;
            }
        } else if rand::random::<f32>() < PHANTOM_WANDER_PAUSE_CHANCE {
            self.movers[i].is_paused = true;
            self.movers[i].wander_pause_timer = PHANTOM_WANDER_PAUSE_MIN
                + rand::random::<f32>() * (PHANTOM_WANDER_PAUSE_MAX - PHANTOM_WANDER_PAUSE_MIN);
            let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
            if let Some(peer) = ctx.net.peers.get_mut(&id) {
                peer.update_player_state(from.to_array(), yaw, "idle".into());
            }
            return;
        }

        // Observation leash (WANDER-only, play-test crutch): re-aim toward spawn if it
        // drifted past the radius so it stays in view. STALK/SPRINT ignore it.
        if from.distance_xz(self.movers[i].spawn_pos) > PHANTOM_WANDER_RADIUS {
            let dx = self.movers[i].spawn_pos.x - from.x;
            let dz = self.movers[i].spawn_pos.z - from.z;
            if dx * dx + dz * dz > f32::EPSILON {
                self.movers[i].heading = dx.atan2(dz).rem_euclid(std::f32::consts::TAU);
            }
        }

        // Walk the heading; a full block (neither axis advanced) re-orients so it
        // never stalls at a wall. A slide keeps the heading and hugs the wall.
        let heading = self.movers[i].heading;
        let dir = Vec3::new(heading.sin(), 0.0, heading.cos());
        let desired = Vec3::new(
            from.x + dir.x * PHANTOM_WALK_SPEED * ctx.dt,
            from.y,
            from.z + dir.z * PHANTOM_WALK_SPEED * ctx.dt,
        );
        let resolved = self.resolve_pos(layer, from, desired);
        let blocked = (resolved.x - from.x).abs() < 1e-4 && (resolved.z - from.z).abs() < 1e-4;
        if blocked {
            let turn =
                PHANTOM_TURN_MIN + rand::random::<f32>() * (PHANTOM_TURN_MAX - PHANTOM_TURN_MIN);
            self.movers[i].heading = (heading + turn).rem_euclid(std::f32::consts::TAU);
        }
        let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
        if let Some(peer) = ctx.net.peers.get_mut(&id) {
            peer.update_player_state(resolved.to_array(), yaw, "idle".into());
        }
        if ctx.net.session_start.elapsed().as_millis() % 1000 < 120 {
            info!(
                "MPTRACE step=PH2 event=phantom_move phantom_id={} pos=({:.2},{:.2},{:.2}) yaw={:.1} blocked={} grid_chunks={}",
                id, resolved.x, resolved.y, resolved.z, yaw, blocked, self.grid_cache.len()
            );
        }
    }

    /// SPRINT — the committed lunge: ramps WALK→SPRINT straight at the player and strikes at reach.
    /// The two ways out are both things the PLAYER does, not a clock: outrun it past
    /// `LOSE_RADIUS * 1.2`, or break its line of sight for `PHANTOM_SPRINT_BLIND_SECONDS` (halved
    /// while crouching). A strike from the front is a non-lethal hit; from behind it is a kill, and
    /// a kill leaves the creature sated.
    fn tick_sprint(&mut self, mt: MoverTick, ctx: &mut TickCtx<'_>) {
        let MoverTick {
            i,
            id,
            from,
            layer,
            target,
        } = mt;
        let dist_opt = target.map(|(_, _, d, _)| d);
        if dist_opt.is_none_or(|d| d > PHANTOM_LOSE_RADIUS * 1.2) {
            // ADR-040 Fase 4 — same as STALK: a lost lunge becomes a search, not
            // amnesia. This is what makes breaking line of sight a tactic rather than
            // an off switch.
            if self.movers[i].last_known_player_pos.is_some() {
                self.enter_search_after_hunt(i, layer); // ADR-075
            } else {
                self.movers[i].state = PhantomState::Wander;
                self.movers[i].state_timer = 0.0;
            }
            // Losing the target ends the commitment with it.
            self.movers[i].strike_recover = 0.0;
            self.movers[i].sprint_blind_for = 0.0;
            return;
        }
        // ADR-047: `tid` BOUND (see STATUE) — the strike below must name its victim.
        let (tid, tpos, dist, tyaw) = target.unwrap();
        self.movers[i].last_known_player_pos = Some(tpos);
        self.movers[i].last_seen_vel = ctx.target_vels.get(&tid).copied();

        // Ground down against geometry for seconds without landing anything: stop
        // pushing and go back to stalking. The creature re-approaches from somewhere
        // else instead of standing in the corner, which is the visible failure.
        if self.movers[i].blocked_ticks >= PHANTOM_SPRINT_GIVEUP_TICKS {
            // ADR-051 point 5: back to HUNTING, not to Stalk. Stalk is a clothed state, and
            // dropping into it after every failed lunge is precisely what made the skin flicker on
            // and off through a chase.
            self.movers[i].state = PhantomState::Hunting;
            self.movers[i].state_timer = 0.0;
            self.movers[i].blocked_ticks = 0;
            // BUG FIX — this used to clear `strike_recover`, and `tick_hunting` re-lunges the
            // instant that timer is spent. So a lunge that gave up wedged went straight back into
            // SPRINT on the very next tick, ground into the same wall for another 25 ticks, gave
            // up again… a Sprint↔Hunting loop that never reached the player and screamed
            // `VOCAL_REVEAL` on every bounce, because `enter_sprint` vocalises. Giving up has to
            // COST something: it circles for a beat and comes at you from somewhere else.
            self.movers[i].strike_recover = PHANTOM_STRIKE_RECOVERY;
            self.movers[i].sprint_blind_for = 0.0;
            info!(
                "MPTRACE step=PH_STALK event=phantom_sprint_gave_up phantom_id={} reason=wedged",
                id
            );
            return;
        }

        // A COMMITTED HUNT ENDS WHEN YOU ESCAPE IT, NOT ON A CLOCK.
        //
        // It used to bounce back to STALK a couple of seconds after each blow, which is
        // what "ataca, no ataca" looked like from the outside: the thing that was on you
        // suddenly strolled again for no reason you could see or influence. Now the
        // lunge holds, and the two ways out are both things the PLAYER does — outrun it
        // (the LOSE_RADIUS check above) or break its line (below). That is what makes
        // finding a place to hide worth anything.
        //
        // No clear line to you = going blind. `segment_is_clear` is the same test the
        // steering already trusts, so a corner that stops the creature seeing you is
        // exactly the corner the geometry says it is.
        //
        // ADR-082: measured to the CONTACT STANCE, not to the player. Pressed against a wall your
        // own position quantizes into a cell grid_gen calls solid, so the line to it was never
        // clear, `sprint_blind_for` ran out and the lunge gave up — hugging geometry worked as a
        // perfect hiding place while standing in the open. Falling back to `tpos` when there is no
        // stance keeps a genuine corner working exactly as before.
        let contact = crate::world::grid_gen::contact_stance(
            &mut self.grid_cache,
            layer,
            from,
            tpos,
            crate::world::grid_gen::PHANTOM_BODY_RADIUS,
        );
        let line_to = contact.unwrap_or(tpos);
        let has_line =
            crate::world::grid_gen::segment_is_clear(&mut self.grid_cache, layer, from, line_to);
        if has_line {
            self.movers[i].sprint_blind_for = 0.0;
        } else {
            self.movers[i].sprint_blind_for += ctx.dt;
            // Crouching cuts the grace roughly in half: staying low behind cover loses
            // it faster than standing behind cover, which is the payoff stealth already
            // gets everywhere else (ADR-040 perception).
            let blind_limit = match target_is_crouched(ctx.net, tid, ctx.host_crouch) {
                true => PHANTOM_SPRINT_BLIND_SECONDS * 0.5,
                false => PHANTOM_SPRINT_BLIND_SECONDS,
            };
            if self.movers[i].sprint_blind_for >= blind_limit {
                self.movers[i].sprint_blind_for = 0.0;
                self.movers[i].strike_recover = 0.0;
                self.enter_search_after_hunt(i, layer); // ADR-075 (last_known kept fresh above)
                info!(
                    "MPTRACE step=PH_SEARCH event=phantom_lost_the_line phantom_id={} note=player_broke_line_of_sight",
                    id
                );
                return;
            }
        }
        // The beat before it comes. It is already revealed and has already screamed
        // (both ride SPRINT, ADR-038), so this is the moment where you see WHAT it is
        // before it moves — which is also what makes the reveal readable at all.
        // Faces you while it holds, so it never reads as the AI having frozen.
        if self.movers[i].hesitate_timer > 0.0 {
            self.movers[i].hesitate_timer -= ctx.dt;
            let to_player = (tpos.x - from.x)
                .atan2(tpos.z - from.z)
                .rem_euclid(std::f32::consts::TAU);
            self.movers[i].heading_target = to_player;
            let t = (PHANTOM_TURN_SPEED_SPRINT * ctx.dt).min(1.0);
            self.movers[i].heading = lerp_heading(self.movers[i].heading, to_player, t);
            let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
            if let Some(peer) = ctx.net.peers.get_mut(&id) {
                peer.update_player_state(from.to_array(), yaw, "idle".into());
            }
            return;
        }

        // ADR-040: navigated heading (see STALK). The lunge routes around geometry
        // instead of pinning itself to a wall between it and you.
        //
        // ADR-082: it closes on the CONTACT STANCE. Aiming at a player standing inside a cell the
        // pathfinder calls solid made the A* return best-effort and stop a cell short — the "se
        // queda pillado a una distancia" of the play-test. The stance is a place the body can
        // actually be, so the route reaches it and the arm covers the rest.
        //
        // ADR-088: …unless they are RUNNING, in which case it cuts them off. The intercept point is
        // where the route goes; the stance stays the fallback and the thing the strike measures to.
        let nav_goal = match ctx.target_vels.get(&tid).copied() {
            Some(vel) => intercept_point(
                &mut self.grid_cache,
                layer,
                from,
                tpos,
                vel,
                PHANTOM_SPRINT_SPEED,
            )
            .unwrap_or(line_to),
            None => line_to,
        };
        let to_player = self.steer_heading(i, layer, from, nav_goal, ctx.dt);
        // ADR-091: the sidestep, applied to the route heading. Not while a blow is possible (in
        // reach with the strike ready): the claw wins over the dodge, always.
        let strike_possible = dist < PHANTOM_ATTACK_REACH && self.movers[i].strike_recover <= 0.0;
        let to_player = self.juke_heading(
            i,
            ctx,
            from,
            tid,
            tpos,
            tyaw,
            dist,
            to_player,
            !strike_possible,
        );
        // Aggressive turn smoothing (faster than STALK) — tracks hard but never snaps.
        self.movers[i].heading_target = to_player;
        let t = (PHANTOM_TURN_SPEED_SPRINT * ctx.dt).min(1.0);
        self.movers[i].heading =
            lerp_heading(self.movers[i].heading, self.movers[i].heading_target, t);
        let heading = self.movers[i].heading;

        // Point-blank "attack". The pickup gesture is the VISUAL only (ADR-016
        // invariant — the DAMAGE rides the separate PhantomAttack channel, never the
        // pickup path). Front (player looking) = non-lethal hit; behind = kill.
        // The lunge STAYS COMMITTED after the blow instead of bouncing on the same
        // tick. The bounce is what made the real form appear and vanish around a single
        // frame of contact; now it charges through, still revealed, and the second
        // condition stops it re-striking inside that window.
        // REACH, not travel distance, and a clear line so the extra reach cannot strike
        // through geometry. See `PHANTOM_ATTACK_REACH`.
        //
        // ADR-082 — THE DISTANCE IS TO YOU, THE CLEAR LINE IS TO THE STANCE. Requiring the line all
        // the way to `tpos` meant that pressing yourself against a wall made the strike impossible
        // FOREVER (your position quantizes into a solid cell, so that line is never clear), which is
        // the "sigue sin atacar cuando te pegas a una pared" of the play-test. Measuring the
        // distance against the stance instead would have been the wrong fix in the other direction:
        // it would hand out free reach. So: `dist` stays yours, the line stops where the body can.
        let in_reach = dist < PHANTOM_ATTACK_REACH
            && crate::world::grid_gen::segment_is_clear(&mut self.grid_cache, layer, from, line_to);
        // ADR-080 point 3 — THE SWING THAT DOES NOT LAND MAKES A SOUND. Being at arm's length
        // while the blow is locked out (recovering from the previous one) was completely silent:
        // the claw that grazed you registered as nothing at all. Its own short cooldown, because
        // the global 6 s budget was already spent by the reveal scream that opened this charge.
        if in_reach && self.movers[i].strike_recover > 0.0 {
            self.movers[i].try_vocalize_for(VOCAL_STRIKE_MISS, PHANTOM_MISS_VOCAL_COOLDOWN);
        }
        if in_reach && self.movers[i].strike_recover <= 0.0 {
            self.movers[i].pickup_until = Some(ctx.now + PHANTOM_PICKUP_GESTURE);
            self.movers[i].strike_recover = PHANTOM_STRIKE_RECOVERY;
            // ADR-050 point 11: its OWN cone, not the one STATUE freezes on. See
            // `PHANTOM_KILL_CONE_HALF_FOV` for why sharing that constant was a defect.
            if player_is_looking_at_within(tpos, tyaw, from, PHANTOM_KILL_CONE_HALF_FOV) {
                self.attacks.push(PhantomAttack {
                    victim: tid,
                    kind: PhantomAttackKind::Hit(PHANTOM_ATTACK_DAMAGE),
                });
                info!(
                    "MPTRACE step=PH_SPRINT event=phantom_hit phantom_id={} victim_id={} dmg={:.0}",
                    id, tid, PHANTOM_ATTACK_DAMAGE
                );
            } else {
                // ADR-050 point 9: from behind it TAKES HOLD instead of killing outright. The death
                // still comes, but `tick_grab` owns it now, and until then the victim is alive and
                // can do something about it.
                self.attacks.push(PhantomAttack {
                    victim: tid,
                    kind: PhantomAttackKind::GrabStart(PHANTOM_GRAB_SECONDS),
                });
                self.movers[i].state = PhantomState::Grab;
                self.movers[i].state_timer = 0.0;
                self.movers[i].grab_timer = PHANTOM_GRAB_SECONDS;
                self.movers[i].grab_victim = Some(tid);
                info!(
                    "MPTRACE step=PH_GRAB event=phantom_grab_start phantom_id={} victim_id={} window={:.1}",
                    id, tid, PHANTOM_GRAB_SECONDS
                );
            }
            let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
            if let Some(peer) = ctx.net.peers.get_mut(&id) {
                peer.update_player_state(from.to_array(), yaw, "pickup".into());
            }
            return;
        }

        // ADR-050 point 5 — BURST AND RECOVERY. Spent only here, past the hesitation and the strike:
        // a creature holding its beat or landing a blow is not sprinting, and charging it for the
        // stamina would make a lunge that connects blow out sooner than one that misses.
        // The countdown itself lives in `resolve_mover_tick` with the other timers — see the fix
        // note there. This only READS it, or the recovery would drain twice as fast in SPRINT as
        // in any other state.
        let winded = if self.movers[i].winded_for > 0.0 {
            true
        } else {
            self.movers[i].stamina -= ctx.dt;
            if self.movers[i].stamina <= 0.0 {
                self.movers[i].stamina = 0.0;
                self.movers[i].winded_for = PHANTOM_SPRINT_RECOVER_SECONDS;
                // BUG FIX — this was inaudible in practice. The burst lasts
                // `PHANTOM_SPRINT_BURST_SECONDS` (5 s) and the shared vocal budget is 6 s, so the
                // REVEAL scream that opens every lunge still had a second to run when the creature
                // blew: the gasp was dropped nearly every time, and the recovery window it exists
                // to announce was invisible. It is the event, so it clears the budget first.
                self.movers[i].vocal_cooldown = 0.0;
                self.movers[i].try_vocalize(VOCAL_WINDED);
                info!(
                    "MPTRACE step=PH_SPRINT event=phantom_winded phantom_id={} dist={:.1}",
                    id, dist
                );
                true
            } else {
                false
            }
        };

        let ramp = (self.movers[i].state_timer / PHANTOM_SPRINT_RAMP).clamp(0.0, 1.0);
        let top = match winded {
            true => PHANTOM_SPRINT_WINDED_SPEED,
            false => PHANTOM_WALK_SPEED + (PHANTOM_SPRINT_SPEED - PHANTOM_WALK_SPEED) * ramp,
        };
        let speed = top * self.movers[i].speed_scale();
        let dir = Vec3::new(heading.sin(), 0.0, heading.cos());
        let desired = Vec3::new(
            from.x + dir.x * speed * ctx.dt,
            from.y,
            from.z + dir.z * speed * ctx.dt,
        );
        let resolved = self.resolve_pos(layer, from, desired);
        // A lunge always intends to travel, so any step that gains nothing toward the
        // player is the creature grinding along geometry.
        let advance = (resolved.x - from.x) * dir.x + (resolved.z - from.z) * dir.z;
        self.movers[i].note_step_progress(advance, speed * ctx.dt);
        let yaw = heading.to_degrees().rem_euclid(360.0);
        if let Some(peer) = ctx.net.peers.get_mut(&id) {
            peer.update_player_state(resolved.to_array(), yaw, "idle".into());
        }
        if ctx.net.session_start.elapsed().as_millis() % 1000 < 120 {
            info!(
                "MPTRACE step=PH_SPRINT event=phantom_sprint_move phantom_id={} pos=({:.2},{:.2},{:.2}) speed={:.1} dist={:.2} blocked_ticks={}",
                id, resolved.x, resolved.y, resolved.z, speed, dist, self.movers[i].blocked_ticks
            );
        }
    }

    /// AMBUSH (ADR-076) — the disguised charge. Structurally `Sprint` with the costume still on:
    /// same steering, same wedge give-up, same thin-line strike check — but no ramp, no stamina,
    /// no hesitation (a hesitating "player" would look wrong, not tense), and it NEVER reveals.
    ///
    /// A failed run — lost, wedged, or timed out — costs `PHANTOM_AMBUSH_COOLDOWN` and ARMS
    /// `strike_recover` before returning to `Stalk`. That arming matters: without it, a `Stalk`
    /// re-entered with `state_timer` reset but `patience()` already exceeded would fire
    /// `enter_sprint()` on the very next tick — the failed trick would hand you a free reveal.
    /// Never returns to `Hunting` for the same reason: `Hunting` is revealed, and a failed ambush
    /// must cost nothing but the cooldown, or the trick would be worse than not trying it.
    fn tick_ambush(&mut self, mt: MoverTick, ctx: &mut TickCtx<'_>) {
        let MoverTick {
            i,
            id,
            from,
            layer,
            target,
        } = mt;

        let fail = |driver: &mut Self| {
            driver.movers[i].ambush_cooldown = PHANTOM_AMBUSH_COOLDOWN;
            // See the doc comment: this is what stops a failed run from handing out a free
            // reveal the instant Stalk re-evaluates its own (already-expired) patience.
            driver.movers[i].strike_recover = PHANTOM_STRIKE_RECOVERY;
            driver.movers[i].state = PhantomState::Stalk;
            driver.movers[i].state_timer = 0.0;
        };

        let dist_opt = target.map(|(_, _, d, _)| d);
        if dist_opt.is_none_or(|d| d > PHANTOM_LOSE_RADIUS)
            || self.movers[i].state_timer > PHANTOM_AMBUSH_MAX_SECONDS
        {
            fail(self);
            info!(
                "MPTRACE step=PH_AMBUSH event=phantom_ambush_failed phantom_id={} reason=lost_or_timeout",
                id
            );
            return;
        }
        // No front/behind split, unlike SPRINT's own strike — a knockdown does not care which way
        // you were facing — so the yaw component of `target` goes unused here.
        let (tid, tpos, dist, _) = target.unwrap();
        self.movers[i].last_known_player_pos = Some(tpos);
        self.movers[i].last_seen_vel = ctx.target_vels.get(&tid).copied();

        if self.movers[i].blocked_ticks >= PHANTOM_SPRINT_GIVEUP_TICKS {
            self.movers[i].blocked_ticks = 0;
            fail(self);
            info!(
                "MPTRACE step=PH_AMBUSH event=phantom_ambush_failed phantom_id={} reason=wedged",
                id
            );
            return;
        }

        // Thin line, same reasoning as SPRINT's own strike check: extra reach must never punch
        // through geometry. Unlike SPRINT there is no front/behind split — this is one kind, and
        // it does not care which way you are facing.
        //
        // ADR-082: to the contact stance, for the same reason and with the same fallback as SPRINT.
        // An ambush that cannot land on somebody with their back to a wall is an ambush that fails
        // in exactly the spot people back into when something charges them.
        let ambush_line_to = crate::world::grid_gen::contact_stance(
            &mut self.grid_cache,
            layer,
            from,
            tpos,
            crate::world::grid_gen::PHANTOM_BODY_RADIUS,
        )
        .unwrap_or(tpos);
        let in_reach = dist < PHANTOM_ATTACK_REACH
            && crate::world::grid_gen::segment_is_clear(
                &mut self.grid_cache,
                layer,
                from,
                ambush_line_to,
            );
        if in_reach {
            let dx = tpos.x - from.x;
            let dz = tpos.z - from.z;
            let len = (dx * dx + dz * dz).sqrt();
            let (ix, iz) = match len > 0.001 {
                true => (
                    dx / len * PHANTOM_KNOCKDOWN_FORCE,
                    dz / len * PHANTOM_KNOCKDOWN_FORCE,
                ),
                false => (0.0, 0.0),
            };
            self.attacks.push(PhantomAttack {
                victim: tid,
                kind: PhantomAttackKind::Knockdown(PHANTOM_KNOCKDOWN_SECONDS, ix, iz),
            });
            // CRITICAL — armed on the SAME tick as the knockdown, not on entering `Unmasking`.
            // The chronology this buys: knockdown t=0 → skin tears t=1.6 (`tick_unmasking`'s own
            // clock) → `enter_sprint()` → player back up at t=2.0 → first possible strike at
            // t=2.5. Without this, the Sprint born from `Unmasking` could land on a player who is
            // still on the ground — a blow from behind on someone who cannot move is a `GrabStart`
            // by the SAME rule Sprint always uses, and a knockdown would be a death sentence
            // instead of the scare ADR-076 designed.
            self.movers[i].strike_recover = PHANTOM_STRIKE_RECOVERY;
            // Ritual entry into UNMASKING, identical to STATUE's own timeout — still dressed,
            // still screaming, the tear still a beat and a half away.
            self.movers[i].state = PhantomState::Unmasking;
            self.movers[i].state_timer = 0.0;
            self.movers[i].vocal_cooldown = 0.0;
            self.movers[i].try_vocalize(VOCAL_UNMASK_SCREAM);
            info!(
                "MPTRACE step=PH_AMBUSH event=phantom_knockdown phantom_id={} victim_id={}",
                id, tid
            );
            return;
        }

        // Steering identical to SPRINT (same shortcut, same A* fallback), sprint-speed turning
        // (a jogger still turns like a person, just not like a predator mid-lunge), and a FIXED
        // speed — no ramp, no stamina. It either arrives looking human, or it does not arrive.
        let to_player = self.steer_heading(i, layer, from, tpos, ctx.dt);
        self.movers[i].heading_target = to_player;
        let t = (PHANTOM_TURN_SPEED_SPRINT * ctx.dt).min(1.0);
        self.movers[i].heading =
            lerp_heading(self.movers[i].heading, self.movers[i].heading_target, t);
        let heading = self.movers[i].heading;
        let speed = PHANTOM_AMBUSH_SPEED * self.movers[i].speed_scale();
        let dir = Vec3::new(heading.sin(), 0.0, heading.cos());
        let desired = Vec3::new(
            from.x + dir.x * speed * ctx.dt,
            from.y,
            from.z + dir.z * speed * ctx.dt,
        );
        let resolved = self.resolve_pos(layer, from, desired);
        let advance = (resolved.x - from.x) * dir.x + (resolved.z - from.z) * dir.z;
        self.movers[i].note_step_progress(advance, speed * ctx.dt);
        let yaw = heading.to_degrees().rem_euclid(360.0);
        if let Some(peer) = ctx.net.peers.get_mut(&id) {
            // "run", not "idle": it is disguised, not motionless — the relay's animation field is
            // cosmetic-only and this is exactly what a sprinting player looks like from outside.
            peer.update_player_state(resolved.to_array(), yaw, "run".into());
        }
    }

    /// Advance every phantom one step at `dt` (entity-tick delta). Reads the phantom's
    /// current pose from its PeerConnection, resolves a walk-step through sim-only collision,
    /// and writes the resolved pose (grounded Y from the resolver + facing) back. A fully
    /// blocked step re-orients the heading so the phantom never stalls at a wall.
    // The five `host_player_*` parameters are the local player, which is NOT a peer and so cannot
    // be read off the roster. They are passed explicitly rather than stashed on the driver so the
    // inputs to a tick stay visible in the signature — the same reasoning `TickCtx` documents for
    // deliberately NOT carrying them. Grouping them into a struct would satisfy the lint and is
    // worth doing the next time this signature is opened for another reason; it is not worth
    // touching forty-six call sites for on its own.
    /// ADR-108 — **el único punto donde se decide contra qué mundo choca el robapieles.**
    ///
    /// Con WG3 mandando resuelve contra su ráster, con la misma lógica de deslizamiento que usa el
    /// jugador. Sin él, el camino de `grid_gen` de siempre, byte a byte.
    ///
    /// El `layer` sólo lo usa la rama de WG2: en WG3 no hay capas, y ésa es justamente la razón de que
    /// una escalera se pueda subir (ADR-106 D4).
    fn resolve_ex(
        &mut self,
        layer: u8,
        from: Vec3,
        desired: Vec3,
    ) -> crate::world::grid_gen::MoveResult {
        match &self.wg3 {
            Some(cache) => {
                // **La puerta de ENTIDAD, no la del jugador.** La del jugador conserva la Y que le
                // llega, y la del robapieles la calcula su FSM con las cotas de WG2: en un mundo de
                // WG3 esa Y no cae en ningún suelo, los pies acaban dentro del forjado y la criatura
                // se queda quieta justo donde el jugador pasa sin enterarse.
                let r = crate::world::collision::Level0Collision::resolve_move_wg3_entity(
                    cache,
                    from,
                    desired,
                    // ADR-040 — el robapieles es un disco de 0,5 m, no un jugador de 0,35. Con el
                    // radio del jugador pasa por huecos donde su modelo no cabe, y desde fuera se ve
                    // rozando y atravesando paredes.
                    crate::world::grid_gen::PHANTOM_BODY_RADIUS,
                );
                use crate::world::collision::CollisionResultKind as K;
                crate::world::grid_gen::MoveResult {
                    pos: r.position,
                    blocked: r.kind == K::Blocked,
                    // Deslizó si se movió por un solo eje. El resolutor del jugador ya lo distingue
                    // con su propio nombre, y ADR-040 lo necesita: es el disparador que hace que el
                    // fantasma REPLANIFIQUE en vez de molerse contra la pared.
                    slid: matches!(r.kind, K::SlidX | K::SlidZ),
                }
            }
            None => resolve_move_grid_gen_ex(&mut self.grid_cache, layer, from, desired),
        }
    }

    /// ADR-108 — los BIGOTES de ADR-082, sondeando el mundo de WG3.
    ///
    /// **Sin esto la criatura se ve tonta, y ése era el síntoma exacto.** El filtro de ADR-082 es lo
    /// que la aparta de una pared antes de rozarla y lo que le hace buscar el hueco más cercano
    /// cuando tiene algo delante; su versión de `grid_gen` sondea la rejilla vieja, así que con WG3
    /// mandando estaba **preguntándole a un mundo que ya no existe**: contestaba «libre» donde había
    /// muro y «bloqueado» donde había paso, y la criatura se lanzaba contra las paredes.
    ///
    /// La forma se copia entera de `grid_gen::steer_around_walls` —mismo orden de sondas, mismos
    /// ángulos, mismo empujón— y sólo cambia a quién se pregunta. Copiarla y no compartirla duele,
    /// pero la de allí está atada a su caché y a su capa; el día que WG2 se retire, ésta se queda.
    fn steer_around_walls_wg3(&mut self, layer: u8, from: Vec3, heading: f32) -> f32 {
        use crate::world::grid_gen::{
            PHANTOM_WALL_MARGIN, PHANTOM_WALL_NUDGE_DEG, PHANTOM_WHISKER_ANGLES_DEG,
            PHANTOM_WHISKER_LENGTH,
        };
        const BODY_RADIUS: f32 = 0.35;

        let probe = |me: &mut Self, angle: f32, length: f32| {
            let to = Vec3::new(
                from.x + angle.sin() * length,
                from.y,
                from.z + angle.cos() * length,
            );
            me.straight_is_clear(layer, from, to)
        };

        // El caso común —un pasillo abierto— cuesta esta sonda y devuelve el rumbo sin tocar.
        if probe(self, heading, PHANTOM_WHISKER_LENGTH) {
            let side = BODY_RADIUS + PHANTOM_WALL_MARGIN;
            let left = probe(self, heading - std::f32::consts::FRAC_PI_2, side);
            let right = probe(self, heading + std::f32::consts::FRAC_PI_2, side);
            let nudge = PHANTOM_WALL_NUDGE_DEG.to_radians();
            return match (left, right) {
                (false, true) => (heading + nudge).rem_euclid(std::f32::consts::TAU),
                (true, false) => (heading - nudge).rem_euclid(std::f32::consts::TAU),
                // Los dos lados apretados —un pasillo— o los dos libres: se mantiene la línea.
                // Empujar dentro de un pasillo la haría zigzaguear por todos los corredores.
                _ => heading,
            };
        }

        // Con algo delante, el hueco más cercano: desviación más corta primero, alternando lados para
        // que un obstáculo simétrico no se resuelva siempre igual.
        for degrees in PHANTOM_WHISKER_ANGLES_DEG {
            let rad = degrees.to_radians();
            for signed in [rad, -rad] {
                let candidate = (heading + signed).rem_euclid(std::f32::consts::TAU);
                if probe(self, candidate, PHANTOM_WHISKER_LENGTH) {
                    return candidate;
                }
            }
        }
        heading
    }

    /// ADR-108 — ¿se puede ir en línea recta, andando? Mismo dispatch que el movimiento.
    ///
    /// La de WG3 mira además la COTA: dos puntos pueden verse y estar en plantas distintas, así que
    /// una recta por encima de un atrio pasaría por libre porque abajo hay suelo — a tres metros.
    fn straight_is_clear(&mut self, layer: u8, a: Vec3, b: Vec3) -> bool {
        match &self.wg3 {
            Some(cache) => crate::world::wg3::nav::segment_is_clear(
                cache,
                a,
                b,
                crate::world::grid_gen::PHANTOM_BODY_RADIUS,
            ),
            None => crate::world::grid_gen::segment_is_clear_for_body(
                &mut self.grid_cache,
                layer,
                a,
                b,
                crate::world::grid_gen::PHANTOM_BODY_RADIUS,
            ),
        }
    }

    /// Igual, cuando sólo interesa a dónde se llegó.
    fn resolve_pos(&mut self, layer: u8, from: Vec3, desired: Vec3) -> Vec3 {
        self.resolve_ex(layer, from, desired).pos
    }

    #[allow(clippy::too_many_arguments)]
    pub(super) fn step(
        &mut self,
        net: &mut NetworkManager,
        dt: f32,
        host_player_pos: Vec3,
        host_player_rot: f32,
        // ADR-040 perception: the HOST player's crouch. Remote peers carry their own in
        // `PeerConnection.crouch` (ADR-020 already relays it), but the host's local player is not a
        // peer, so it has to be handed in. Passed explicitly rather than stashed on the driver so
        // the input to a tick stays visible in the signature.
        host_player_crouch: bool,
        // The host player's own death, handed in for the same reason as its crouch: it is not a
        // peer, so `choose_target` cannot read it off the roster.
        host_player_dead: bool,
        // ADR-050 point 8: and its held item, for the imitation. Same reason again.
        host_player_held_item: i32,
        // ADR-080: its lit-wieldable flag (point 2, detection) and its sustained-state bits
        // (point 6, imitation). Same reason as every other `host_player_*` here.
        host_player_light_on: bool,
        host_player_buttons: u16,
    ) -> &[PhantomAttack] {
        let now = Instant::now();
        self.step_counter = self.step_counter.wrapping_add(1);

        // ADR-041: stimuli first — a shot reported this tick redirects the phantom before the FSM
        // gets to act on stale intentions.
        self.hear_noises(net);
        // Your walls exist for the AI too — collision AND pathfinding, via the same cache.
        self.sync_built_cells(net, dt);

        // ADR-016 slice 3b-P1 — derive each real target's XZ speed from its last-tick position
        // (peers send no velocity/move_state, so this is the uniform "is it running?" signal for
        // sound detection). Teleport / chunk-displacement deltas are clamped out. Computed once
        // per tick; the map is rebuilt from the current targets so stale ids drop out.
        let mut cur_positions: Vec<(PeerId, Vec3)> = Vec::with_capacity(net.peers.len() + 1);
        cur_positions.push((net.local_id, host_player_pos));
        for p in net.peers.values() {
            if !net.phantom_ids.contains(&p.id) {
                cur_positions.push((p.id, Vec3::from_array(p.position)));
            }
        }
        let mut target_speeds: HashMap<PeerId, f32> = HashMap::with_capacity(cur_positions.len());
        // ADR-075 point 3 — the vector this same delta already carries, kept instead of discarded.
        // `target_speeds` only ever needed the magnitude (the sound tiers); predictive tracking is
        // the first consumer that needs the heading too.
        let mut target_vels: HashMap<PeerId, (f32, f32)> =
            HashMap::with_capacity(cur_positions.len());
        for (tid, cur) in &cur_positions {
            let (speed, vel) = match self.prev_target_pos.get(tid) {
                Some(prev) => {
                    let (dx, dz) = (cur.x - prev.x, cur.z - prev.z);
                    let d = (dx * dx + dz * dz).sqrt() / dt;
                    if d > PHANTOM_SPEED_SANITY_MAX {
                        (0.0, (0.0, 0.0)) // teleport / chunk displacement — not a footstep
                    } else {
                        (d, (dx / dt, dz / dt))
                    }
                }
                None => (0.0, (0.0, 0.0)),
            };
            target_speeds.insert(*tid, speed);
            target_vels.insert(*tid, vel);
        }
        self.prev_target_pos = cur_positions.into_iter().collect();

        // ADR-016 slice 1 / ADR-043 — every attack produced this tick, in mover order; the game
        // loop applies them to the host. A single slot used to mean the last attacker won, so two
        // creatures reaching you together counted as one — invisible with the one debug phantom,
        // wrong the moment the world is populated.
        self.attacks.clear();
        // Same discipline as `attacks`: cleared per step so a consumer that misses one tick cannot
        // leave the driver growing a list forever.
        self.voice_echoes.clear();
        // ADR-089: the claims standing this step. `owner_id == 0` is "unknown" (pre-ADR-081 save)
        // and never a home; the layer comes from the marker's own Y, like everything else here.
        self.homes.clear();
        for b in &net.stp_buildings {
            if b.def_id == crate::game_loop::CLAIM_MARKER_DEF_ID && b.owner_id != 0 {
                let pos = Vec3::from_array(b.position);
                self.homes
                    .push((b.owner_id, pos, world_pos_to_layer(pos.y)));
            }
        }
        for i in 0..self.movers.len() {
            // Built here and not above the loop: the seal pass and the step-cost trace below still
            // read `net` directly, so the reborrow has to end with the iteration.
            let mut ctx = TickCtx {
                net: &mut *net,
                dt,
                now,
                target_speeds: &target_speeds,
                target_vels: &target_vels,
                host_crouch: host_player_crouch,
                host_held_item: host_player_held_item,
                host_light_on: host_player_light_on,
                host_buttons: host_player_buttons,
            };
            // `None` = this creature is done for the tick: it has no peer any more, or it is frozen
            // mid-gesture. Both used to be `continue`s in the middle of the preamble.
            let Some(mt) = self.resolve_mover_tick(
                i,
                &mut ctx,
                host_player_pos,
                host_player_rot,
                host_player_dead,
            ) else {
                continue;
            };
            // ADR-016 slice 3a — Stalker FSM. Bound to a local because `state` is Copy, so the
            // match holds no borrow of `self` across the arms.
            let state = self.movers[i].state;
            // Each arm is documented on its own method; the transitions between them are the
            // `state = …` writes inside those methods, not anything this dispatch decides.
            match state {
                PhantomState::Wander => self.tick_wander(mt, &mut ctx),
                PhantomState::Spotted => self.tick_spotted(mt, &mut ctx),
                PhantomState::Stalk => self.tick_stalk(mt, &mut ctx),
                PhantomState::Statue => self.tick_statue(mt, &mut ctx),
                PhantomState::Sprint => self.tick_sprint(mt, &mut ctx),
                PhantomState::Ambush => self.tick_ambush(mt, &mut ctx),
                PhantomState::Search => self.tick_search(mt, &mut ctx),
                PhantomState::Peek => self.tick_peek(mt, &mut ctx),
                PhantomState::Flee => self.tick_flee(mt, &mut ctx),
                PhantomState::Lurk => self.tick_lurk(mt, &mut ctx),
                PhantomState::Grab => self.tick_grab(mt, &mut ctx),
                PhantomState::Unmasking => self.tick_unmasking(mt, &mut ctx),
                PhantomState::Hunting => self.tick_hunting(mt, &mut ctx),
            }
        }

        // ADR-080 point 5: the chorus reads the screams this tick staged and hands OTHER creatures
        // a delayed answer. Between the FSM and the seal on purpose — after every state has had its
        // say about `pending_vocal`, before those get consumed into `vocal_seq`.
        self.spread_chorus(net, dt);

        self.seal_cosmetics(net);
        self.report_step_cost(net, now);

        &self.attacks
    }

    /// ADR-092 — a place to lie in wait: 12–20 m from here, walkable for the body, and with NO thin
    /// line of sight to where it last saw you. Eight deterministic bearings (offset by id so two
    /// creatures giving up in the same room do not pick the same corner), three radii each. `None`
    /// when the geometry offers nothing — then it gives up for real, as before.
    pub(super) fn pick_lurk_spot(&mut self, i: usize, from: Vec3, layer: u8) -> Option<Vec3> {
        use crate::world::grid_gen::{is_walkable_grid_gen, segment_is_clear};
        let last_seen = self.movers[i].last_known_player_pos?;
        let id = self.movers[i].id;
        let base = (id as f32 * 0.618_034).fract() * std::f32::consts::TAU;
        for k in 0..8 {
            let bearing = base + k as f32 * std::f32::consts::FRAC_PI_4;
            for r in [16.0, PHANTOM_LURK_MIN_DISTANCE, PHANTOM_LURK_MAX_DISTANCE] {
                let p = Vec3::new(
                    from.x + bearing.sin() * r,
                    from.y,
                    from.z + bearing.cos() * r,
                );
                if !is_walkable_grid_gen(&mut self.grid_cache, p, layer) {
                    continue;
                }
                // Reachable for the body from here, and hidden from the last sighting.
                if !self.straight_is_clear(layer, from, p) {
                    continue;
                }
                if segment_is_clear(&mut self.grid_cache, layer, p, last_seen) {
                    continue; // you could see it from there: not a hiding place
                }
                return Some(p);
            }
        }
        None
    }

    /// LURK (ADR-092) — walk to the spot, go still and silent, then come back to look. See the
    /// variant's doc for the rules; the one thing to hold onto is that it NEVER reveals and NEVER
    /// makes a sound here: the feint only works while it still looks like one of you.
    fn tick_lurk(&mut self, mt: MoverTick, ctx: &mut TickCtx<'_>) {
        let MoverTick {
            i,
            id,
            from,
            layer,
            target,
        } = mt;

        // Hiding, not blind: the Wander rule — a player who walks into its cone, runs past it, or
        // lights a torch gets the stalk directly, because it was never really done with you.
        let (detected, _) = self.detect_from_cover(i, id, from, layer, target, ctx);
        if detected {
            self.movers[i].lurk_goal = None;
            self.movers[i].state = PhantomState::Stalk;
            self.movers[i].state_timer = 0.0;
            info!(
                "MPTRACE step=PH_LURK event=phantom_feint_pays_off phantom_id={}",
                id
            );
            return;
        }

        let Some(goal) = self.movers[i].lurk_goal else {
            // No spot (should not happen: entry requires one). Back to the ordinary search.
            self.enter_search_after_hunt(i, layer);
            return;
        };

        if from.distance_xz(goal) > PHANTOM_LURK_ARRIVE {
            // Walking there, at a walk: this is a creature that has "given up", and it walks like
            // one. Same steering and resolver as everything else.
            let heading = self.steer_heading(i, layer, from, goal, ctx.dt);
            self.movers[i].heading_target = heading;
            let t = (PHANTOM_TURN_SPEED_STALK * ctx.dt).min(1.0);
            self.movers[i].heading = lerp_heading(self.movers[i].heading, heading, t);
            let h = self.movers[i].heading;
            let dir = Vec3::new(h.sin(), 0.0, h.cos());
            let desired = Vec3::new(
                from.x + dir.x * PHANTOM_WALK_SPEED * ctx.dt,
                from.y,
                from.z + dir.z * PHANTOM_WALK_SPEED * ctx.dt,
            );
            let resolved = self.resolve_pos(layer, from, desired);
            let advance = (resolved.x - from.x) * dir.x + (resolved.z - from.z) * dir.z;
            self.movers[i].note_step_progress(advance, PHANTOM_WALK_SPEED * ctx.dt);
            let yaw = h.to_degrees().rem_euclid(360.0);
            if let Some(peer) = ctx.net.peers.get_mut(&id) {
                peer.update_player_state(resolved.to_array(), yaw, "idle".into());
            }
            // Wedged on the way: it is not worth a second mechanism. Give the feint up quietly.
            if self.movers[i].blocked_ticks >= PHANTOM_STALK_GIVEUP_TICKS {
                self.movers[i].blocked_ticks = 0;
                self.movers[i].lurk_goal = None;
                self.enter_search_after_hunt(i, layer);
            }
            return;
        }

        // There. Still, silent, facing where it last saw you — and counting.
        if let Some(last) = self.movers[i].last_known_player_pos {
            let face = (last.x - from.x)
                .atan2(last.z - from.z)
                .rem_euclid(std::f32::consts::TAU);
            let t = (PHANTOM_TURN_SPEED_STALK * ctx.dt).min(1.0);
            self.movers[i].heading = lerp_heading(self.movers[i].heading, face, t);
        }
        let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
        if let Some(peer) = ctx.net.peers.get_mut(&id) {
            peer.update_player_state(from.to_array(), yaw, "idle".into());
        }
        self.movers[i].lurk_for -= ctx.dt;
        if self.movers[i].lurk_for <= 0.0 {
            // Back for another look, with hunt patience and hunt speed. `lurked_this_hunt` stays
            // set: one feint per hunt.
            self.movers[i].lurk_goal = None;
            self.enter_search_after_hunt(i, layer);
            info!(
                "MPTRACE step=PH_LURK event=phantom_comes_back phantom_id={}",
                id
            );
        }
    }

    /// ADR-091 — THE SIDESTEP. Given the heading the route wants, returns the heading to travel on:
    /// unchanged almost always; deflected by `PHANTOM_JUKE_DEFLECT` to a remembered side while a
    /// juke is live. Arms a new one when the target is aiming AT this creature inside the band, the
    /// cooldown has run out, and the temperament roll passes.
    ///
    /// `allowed` is the caller's say on timing: the lunge passes `false` while it hesitates or while
    /// a strike is possible (in reach with the blow ready) — the blow always wins over the dodge.
    #[allow(clippy::too_many_arguments)] // same call as `step`: grouping these is worth doing once, not here
    fn juke_heading(
        &mut self,
        i: usize,
        ctx: &TickCtx<'_>,
        from: Vec3,
        tid: PeerId,
        tpos: Vec3,
        tyaw: f32,
        dist: f32,
        heading: f32,
        allowed: bool,
    ) -> f32 {
        let m = &mut self.movers[i];
        if m.juke_for <= 0.0
            && allowed
            && m.juke_cooldown <= 0.0
            && (PHANTOM_JUKE_MIN..=PHANTOM_JUKE_MAX).contains(&dist)
            && target_is_aiming(ctx.net, tid, ctx.host_buttons)
            && player_is_looking_at_within(tpos, tyaw, from, PHANTOM_JUKE_LOOK_HALF_FOV)
        {
            m.juke_rolls = m.juke_rolls.wrapping_add(1);
            let key = ((m.id as u64) << 32) ^ m.juke_rolls as u64;
            let roll = chorus_delay_fraction(key);
            let passes = m.traits.is_hunter || roll < PHANTOM_JUKE_CHANCE * m.impulse();
            if passes {
                m.juke_for = PHANTOM_JUKE_SECONDS;
                m.juke_cooldown = PHANTOM_JUKE_COOLDOWN;
                // The side comes from the same hash, decorrelated from the pass/fail bit.
                m.juke_side = if (key.wrapping_mul(0x9E37_79B9_7F4A_7C15) >> 63) == 0 {
                    1.0
                } else {
                    -1.0
                };
                info!(
                    "MPTRACE step=PH_JUKE event=phantom_jukes phantom_id={} dist={:.1} side={}",
                    m.id, dist, m.juke_side
                );
            } else {
                // A failed roll still spends the cooldown: otherwise it re-rolls every tick and the
                // chance stops meaning anything.
                m.juke_cooldown = PHANTOM_JUKE_COOLDOWN;
            }
        }
        match m.juke_for > 0.0 {
            true => {
                (heading + m.juke_side * PHANTOM_JUKE_DEFLECT).rem_euclid(std::f32::consts::TAU)
            }
            false => heading,
        }
    }

    /// ADR-089 — the claim marker of the player this creature is hunting, if it is worth a visit:
    /// same layer, within `PHANTOM_HOME_RECALL_RADIUS` of where the creature stands. `None` when the
    /// target owns nothing nearby — most hunts, most of the time.
    pub(super) fn home_of_target(&self, i: usize, from: Vec3, layer: u8) -> Option<Vec3> {
        let tid = self.movers[i].target_id?;
        self.homes
            .iter()
            .filter(|(owner, _, l)| *owner == tid && *l == layer)
            .map(|(_, pos, _)| *pos)
            .filter(|pos| pos.distance_xz(from) <= PHANTOM_HOME_RECALL_RADIUS)
            .min_by(|a, b| a.distance_xz(from).total_cmp(&b.distance_xz(from)))
    }

    /// ADR-089 — a point on the patrol ring of the nearest claim marker on this layer, if one is
    /// within `PHANTOM_HOME_PATROL_RADIUS`. Whose base it is does not matter to a wanderer: it is
    /// prowling a place where players live, not hunting anyone yet.
    pub(super) fn home_patrol_point(&self, from: Vec3, layer: u8) -> Option<Vec3> {
        let (_, home, _) = self
            .homes
            .iter()
            .filter(|(_, _, l)| *l == layer)
            .filter(|(_, pos, _)| pos.distance_xz(from) <= PHANTOM_HOME_PATROL_RADIUS)
            .min_by(|a, b| a.1.distance_xz(from).total_cmp(&b.1.distance_xz(from)))?;
        let bearing = rand::random::<f32>() * std::f32::consts::TAU;
        let r = PHANTOM_HOME_RING_MIN
            + rand::random::<f32>() * (PHANTOM_HOME_RING_MAX - PHANTOM_HOME_RING_MIN);
        Some(Vec3::new(
            home.x + bearing.sin() * r,
            from.y,
            home.z + bearing.cos() * r,
        ))
    }

    /// ADR-080 point 4 — where to walk to in order to get out of your victim's field of view while
    /// keeping the stalking band. `None` = not being looked at, so nothing to do: approach exactly
    /// as `Stalk` always has.
    ///
    /// The goal is expressed relative to WHERE THE TARGET IS LOOKING, never accumulated per tick.
    /// An accumulated swing plus a bearing that moves as the creature walks double-counts, and the
    /// result is something that orbits you forever; aiming just past the edge of the cone instead
    /// means that if you keep turning to track it, it keeps sliding around, and if you look away it
    /// simply stops. The side is remembered (`flank_offset`) so a creature sitting dead centre does
    /// not shuffle between two equally good exits.
    fn resolve_flank_goal(
        &mut self,
        i: usize,
        from: Vec3,
        tpos: Vec3,
        tyaw: f32,
        band: f32,
    ) -> Option<Vec3> {
        // Where the target is looking, and where the creature sits relative to that. Unity yaw:
        // 0 = +Z, so forward = (sin, cos) — the same convention the whole driver uses.
        let view = tyaw.to_radians().rem_euclid(std::f32::consts::TAU);
        let bearing = (from.x - tpos.x)
            .atan2(from.z - tpos.z)
            .rem_euclid(std::f32::consts::TAU);
        // Signed shortest angle from the target's forward to the creature: sign says which side.
        let mut rel = bearing - view;
        while rel > std::f32::consts::PI {
            rel -= std::f32::consts::TAU;
        }
        while rel < -std::f32::consts::PI {
            rel += std::f32::consts::TAU;
        }
        if rel.abs() >= PHANTOM_FLANK_TRIGGER_HALF_FOV_DEG.to_radians() {
            self.movers[i].flank_offset = 0.0; // already out of sight — nothing to flank
            return None;
        }
        // Which way round. Keeps whichever side it committed to; picks the nearer one when starting
        // (and `rel == 0.0` exactly resolves to +1 rather than to a coin toss every tick).
        let side = match self.movers[i].flank_offset {
            s if s != 0.0 => s,
            _ => {
                let s = if rel < 0.0 { -1.0 } else { 1.0 };
                self.movers[i].flank_offset = s;
                s
            }
        };
        // Just past the edge of the cone, at band radius: the nearest place it can stand where you
        // are not looking at it. `PHANTOM_FLANK_STEP_DEG` of margin so it clears the boundary
        // instead of straddling it, which at 10 Hz would flicker in and out of the flank.
        let goal_angle = view
            + side * (PHANTOM_FLANK_TRIGGER_HALF_FOV_DEG + PHANTOM_FLANK_STEP_DEG).to_radians();
        Some(Vec3::new(
            tpos.x + goal_angle.sin() * band,
            from.y,
            tpos.z + goal_angle.cos() * band,
        ))
    }

    /// ADR-080 point 5 — THE WORLD ANSWERS. A reveal or unmask scream staged this tick makes other
    /// creatures, far enough away to be a separate voice, answer after a beat.
    ///
    /// Deliberately AUDIO ONLY. It does not set targets, does not move anybody, does not wake
    /// sleepers and — the load-bearing part — an answer NEVER seeds another chorus: only the two
    /// scream kinds do, and `VOCAL_DISTANT_ANSWER` is not one of them. That is what keeps a
    /// revelation from cascading into a pack, which is the swarm ADR-053 rejected by another door.
    fn spread_chorus(&mut self, net: &NetworkManager, dt: f32) {
        // Who screamed, and from where. Positions come off the roster because that is where the
        // post-tick pose lives.
        let mut screams: Vec<(PeerId, Vec3, u8)> = Vec::new();
        for m in &self.movers {
            let Some(kind) = m.pending_vocal else {
                continue;
            };
            if kind != VOCAL_REVEAL && kind != VOCAL_UNMASK_SCREAM {
                continue;
            }
            if let Some(peer) = net.peers.get(&m.id) {
                screams.push((m.id, Vec3::from_array(peer.position), m.vocal_seq));
            }
        }

        if !screams.is_empty() {
            // Snapshot first: `movers` is borrowed immutably above, and a listener needs its own
            // position to measure the distance to a screamer.
            let listeners: Vec<(usize, PeerId, Vec3, PhantomState, bool)> = self
                .movers
                .iter()
                .enumerate()
                .filter_map(|(j, m)| {
                    net.peers
                        .get(&m.id)
                        .map(|p| (j, m.id, Vec3::from_array(p.position), m.state, p.revealed))
                })
                .collect();
            for (j, listener_id, listener_pos, state, revealed) in listeners {
                // Only a creature WEARING ITS SKIN and not committed to anything answers: one that
                // is hunting you has better things to do, and one already revealed answering would
                // just add noise on top of its own.
                if revealed || !matches!(state, PhantomState::Wander | PhantomState::Search) {
                    continue;
                }
                if self.movers[j].chorus_pending.is_some() {
                    continue; // already has an answer queued; it does not stack
                }
                for (screamer_id, spos, seq) in &screams {
                    if *screamer_id == listener_id {
                        continue;
                    }
                    // Same layer only: a voice from another floor is not the scene this builds.
                    if world_pos_to_layer(spos.y) != world_pos_to_layer(listener_pos.y) {
                        continue;
                    }
                    let d = listener_pos.distance_xz(*spos);
                    if !(PHANTOM_CHORUS_MIN_DISTANCE..=PHANTOM_CHORUS_MAX_DISTANCE).contains(&d) {
                        continue;
                    }
                    // Deterministic delay from (listener, screamer, seq): the same scream always
                    // gets the same answer from the same creature, and two of them never answer on
                    // the same tick. `rand` here would make a play-test unrepeatable for free.
                    let key =
                        ((listener_id as u64) << 32) ^ ((*screamer_id as u64) << 8) ^ (*seq as u64);
                    let t = chorus_delay_fraction(key);
                    let delay = PHANTOM_CHORUS_DELAY_MIN
                        + t * (PHANTOM_CHORUS_DELAY_MAX - PHANTOM_CHORUS_DELAY_MIN);
                    self.movers[j].chorus_pending = Some(delay);
                    // Logged like every other behaviour in this driver, and for a specific reason:
                    // the chorus is heard and never seen, so without a line in the log a play-test
                    // cannot tell "it answered" from "I imagined it".
                    info!(
                        "MPTRACE step=PH_CHORUS event=phantom_will_answer phantom_id={} screamer_id={} dist={:.0} delay={:.2}",
                        listener_id, screamer_id, d, delay
                    );
                    break; // one answer per creature per scream, whoever was heard first
                }
            }
        }

        // Age the queued answers and let the ones that come due speak. Uses the ambient budget on
        // purpose: an answer must never be able to mute a nearby lunge's scream.
        for m in &mut self.movers {
            let Some(left) = m.chorus_pending else {
                continue;
            };
            let left = left - dt;
            m.chorus_pending = match left <= 0.0 {
                true => {
                    m.try_vocalize_for(VOCAL_DISTANT_ANSWER, PHANTOM_BREATH_COOLDOWN);
                    None
                }
                false => Some(left),
            };
        }
    }

    /// Everything that has to happen to a creature BEFORE its state gets a say: read its pose off
    /// its peer, work out which layer it collides against, pick who it is hunting, age its timers,
    /// and hold the gesture freeze.
    ///
    /// `None` means the tick is over for this one — either its peer is gone, or it is frozen
    /// mid-gesture and the FSM must not run. Those were the two `continue`s buried in the middle of
    /// the old preamble, and an `Option` is what lets them stay skips now that the code is a call.
    ///
    /// `host_pos`/`host_rot`/`host_dead` are parameters and not `TickCtx` fields on purpose: they
    /// feed `choose_target` here and never reach a state arm, so putting them in the context would
    /// advertise an input the arms do not have.
    fn resolve_mover_tick(
        &mut self,
        i: usize,
        ctx: &mut TickCtx<'_>,
        host_pos: Vec3,
        host_rot: f32,
        host_dead: bool,
    ) -> Option<MoverTick> {
        let id = self.movers[i].id;
        let from = Vec3::from_array(ctx.net.peers.get(&id)?.position);
        // The grid_gen layer to collide against, derived from the phantom's own Y (works on
        // every layer without a hardcoded layer).
        let current_layer = world_pos_to_layer(from.y);

        // Nearest REAL player (the host's own local player + any real peer). D1=(a): distance
        // + a forward view cone only, NO geometry line-of-sight (collision = grid_gen world).
        // Who the OTHER creatures are already on. Rebuilt per mover (≤ `active_cap` = 6, so it
        // is a handful of increments) and excluding this one, because a creature must never
        // penalise a player for its own presence — that alone would make it drift off you.
        let mut pursuers: HashMap<PeerId, usize> = HashMap::new();
        for (j, m) in self.movers.iter().enumerate() {
            if j == i {
                continue;
            }
            if let Some(t) = m.target_id {
                *pursuers.entry(t).or_insert(0) += 1;
            }
        }
        let target = choose_target(
            ctx.net,
            host_pos,
            host_rot,
            host_dead,
            from,
            self.movers[i].target_id,
            &pursuers,
        );
        // Remembered for next tick: this is what makes the choice sticky instead of a fresh
        // "nearest" every 100 ms.
        self.movers[i].target_id = target.map(|(tid, _, _, _)| tid);
        self.movers[i].state_timer += ctx.dt;
        // Ticked HERE, above the gesture freeze, and not inside the SPRINT branch: the freeze
        // skips the whole FSM for a second after a strike, so a timer advanced down
        // there would stall exactly across the window it exists to cover.
        self.movers[i].strike_recover = (self.movers[i].strike_recover - ctx.dt).max(0.0);
        // ADR-091: the sidestep and its cooldown age here, with every other per-tick timer.
        self.movers[i].juke_for = (self.movers[i].juke_for - ctx.dt).max(0.0);
        self.movers[i].juke_cooldown = (self.movers[i].juke_cooldown - ctx.dt).max(0.0);
        self.movers[i].statue_cooldown = (self.movers[i].statue_cooldown - ctx.dt).max(0.0);
        self.movers[i].vocal_cooldown = (self.movers[i].vocal_cooldown - ctx.dt).max(0.0);
        self.movers[i].enraged_for = (self.movers[i].enraged_for - ctx.dt).max(0.0);
        self.movers[i].ambush_cooldown = (self.movers[i].ambush_cooldown - ctx.dt).max(0.0);
        // BUG FIX — `winded_for` used to be ticked ONLY inside `tick_sprint`, which was fine while
        // SPRINT was the only state that could hold it. ADR-051 added `Hunting`, and a lunge that
        // gives up wedged leaves SPRINT *while still out of breath*: nothing downstream decremented
        // the timer, so it froze above zero forever, `tick_hunting`'s re-lunge gate
        // (`winded_for <= 0.0`) could never open, and the creature circled you permanently visible
        // and permanently harmless. Ticked here with its siblings, it drains wherever the FSM is.
        if self.movers[i].winded_for > 0.0 {
            self.movers[i].winded_for = (self.movers[i].winded_for - ctx.dt).max(0.0);
            if self.movers[i].winded_for == 0.0 {
                self.movers[i].stamina = PHANTOM_SPRINT_BURST_SECONDS;
            }
        }
        // ADR-050: hunger drains on its own slow clock, wherever the FSM happens to be. Ticked here
        // with the other timers and not inside a state arm for the same reason they are: the gesture
        // freeze skips the whole FSM, and a creature would otherwise stop getting hungry every time
        // it mimed a pickup.
        self.movers[i].hunger =
            (self.movers[i].hunger - ctx.dt / self.hunger_drain_seconds).max(0.0);
        // ADR-053 — IT SAYS YOUR OWN WORDS BACK. Decided in the preamble because every state can do
        // it and the FSM arms are full of early exits, exactly like the reveal seal.
        self.movers[i].echo_cooldown = (self.movers[i].echo_cooldown - ctx.dt).max(0.0);
        if self.movers[i].echo_cooldown <= 0.0 {
            // Reset either way: a creature that had no chance to do it does not bank one up.
            self.movers[i].echo_cooldown =
                PHANTOM_ECHO_MIN + rand::random::<f32>() * (PHANTOM_ECHO_MAX - PHANTOM_ECHO_MIN);
            // DRESSED and AT A DISTANCE. Both are what make it work: the horror is a voice you know
            // coming out of a corridor, from a figure that still looks like your friend. From
            // something already revealed and on top of you it would just be noise, and while it is
            // hunting it would step on the sounds that matter.
            let far_enough = target.is_some_and(|(_, _, d, _)| d >= PHANTOM_ECHO_MIN_DISTANCE);
            if far_enough && !phantom_reveals(self.movers[i].state) {
                if let Some((tid, _, _, _)) = target {
                    if ctx.net.voice_echo.contains_key(&tid) {
                        self.voice_echoes.push((id, tid));
                        info!(
                            "MPTRACE step=PH_ECHO event=phantom_repeats_you phantom_id={} victim_id={}",
                            id, tid
                        );
                    }
                }
            }
        }

        // ADR-050 point 8 — IMITATION. Sampled HERE and not in `seal_cosmetics` because the target
        // may be the host's own local player, which is not a peer: reading it off `net.peers` found
        // nothing and a sated creature stood there copying no one. This is the same reason
        // `host_crouch` is handed into the tick at all.
        // ADR-080 point 6: the copied pose grows to include the LEAN and the SPRAY jet — bits 2-4
        // of `buttons`, which ADR-044 already relays. Bits 0-1 (aim/reload) stay masked out: the
        // phantom still carries no weapon, so aiming one would be a tell, not a disguise.
        let observed = match (self.movers[i].is_sated(), target.map(|(tid, _, _, _)| tid)) {
            (true, Some(tid)) if tid == ctx.net.local_id => Some((
                ctx.host_crouch,
                ctx.host_held_item,
                ctx.host_buttons & PHANTOM_MIMIC_BUTTON_MASK,
            )),
            (true, Some(tid)) => ctx
                .net
                .peers
                .get(&tid)
                .map(|p| (p.crouch, p.held_item, p.buttons & PHANTOM_MIMIC_BUTTON_MASK)),
            _ => None,
        };
        match observed {
            Some(pose) => match self.movers[i].mimic_pending {
                // Only restart the clock when the target actually DID something. Re-arming it every
                // tick against an unchanged pose would mean the delay never elapses and the
                // creature ends up copying nothing at all.
                Some((crouch, held, buttons, _)) if (crouch, held, buttons) == pose => {}
                _ => {
                    self.movers[i].mimic_pending =
                        Some((pose.0, pose.1, pose.2, PHANTOM_MIMIC_DELAY))
                }
            },
            // Out of the sated band, or nobody to copy: drop the act. Both fields fall back to
            // their defaults, so the disguise recomposes with no reset logic — the same discipline
            // that makes `revealed` a derived level rather than a latch (STATE.md invariant 1).
            None => {
                self.movers[i].mimic_pending = None;
                self.movers[i].mimic_worn = (false, 0, 0);
            }
        }
        // When the beat elapses, what was observed becomes what is worn, and `seal_cosmetics` puts
        // it on the wire.
        if let Some((crouch, held, buttons, left)) = self.movers[i].mimic_pending {
            let left = left - ctx.dt;
            self.movers[i].mimic_pending = match left <= 0.0 {
                true => {
                    self.movers[i].mimic_worn = (crouch, held, buttons);
                    None
                }
                false => Some((crouch, held, buttons, left)),
            };
        }

        // ADR-080 point 1 — HEAD-TRACKING. Staged here, next to the imitation and for the same
        // reason: this is the one place that runs before the FSM on EVERY path, so a state with an
        // early exit cannot leave a stale stare behind. The state GATE lives at the seal, because
        // this runs before the FSM and would otherwise dress the stare with last tick's state.
        self.movers[i].look_pitch = match target {
            Some((_, tpos, dist, _)) => quantize_look_pitch(from, tpos, dist),
            None => 0,
        };

        // ── Gesture freeze (ANY state): the faked-pickup imitation and the SPRINT "attack"
        // are PURE THEATER — only the `animation` field. While active, freeze in place holding
        // "pickup" (the trigger flank the proxy edge-detects, ADR-011). NOTHING real is
        // touched: no process_stp_pickup, no pending_pickups, no stp_items, no grant. ──
        if self.movers[i]
            .pickup_until
            .is_some_and(|until| ctx.now >= until)
        {
            self.movers[i].pickup_until = None;
        }
        // ADR-050 point 15 — THE ACT DOES NOT SURVIVE SEEING YOU. This freeze returns `None`, which
        // skips the WHOLE FSM, so while it was miming the creature could not detect anything: a
        // blind second every six, cronometrable from outside and free to walk past. A patrolling
        // creature drops the gesture the moment a player is close enough to matter, exactly like
        // `hear_noises` already drops it for a noise. Scoped to WANDER on purpose: in SPRINT this
        // same field holds the STRIKE animation, and cancelling that would delete the blow's pose.
        if self.movers[i].state == PhantomState::Wander
            && self.movers[i].pickup_until.is_some()
            && target.is_some_and(|(_, _, dist, _)| dist <= PHANTOM_DETECT_RADIUS)
        {
            self.movers[i].pickup_until = None;
            self.movers[i].stare_until = None;
        }
        // ADR-050 point 9 — GRAB IS EXEMPT FROM THE FREEZE, and this is not tidiness. The strike
        // that opens a grab sets `pickup_until` for its own animation, and this freeze skips the
        // WHOLE FSM: `tick_grab` would not run for that first second, so the escape window would
        // not tick down and — far worse — a struggle reported in that second would never be
        // drained. Mashing the instant it grabs you is the most natural thing a player can do, and
        // it would have been silently ignored. `tick_grab` holds its own pose anyway, so nothing
        // is lost by exempting it.
        // `Unmasking` is exempt for the same reason as `Grab`: the freeze skips the WHOLE FSM, so a
        // leftover gesture would run out the warning beat without `tick_unmasking` ever executing —
        // the creature would stand in a pickup pose and then simply be revealed, with the tell the
        // state exists to deliver never happening. Both states hold their own pose anyway.
        if self.movers[i].pickup_until.is_some()
            && !matches!(
                self.movers[i].state,
                PhantomState::Grab | PhantomState::Unmasking
            )
        {
            let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
            if let Some(peer) = ctx.net.peers.get_mut(&id) {
                peer.update_player_state(from.to_array(), yaw, "pickup".into());
            }
            return None;
        }

        Some(MoverTick {
            i,
            id,
            from,
            layer: current_layer,
            target,
        })
    }

    /// ADR-038 — seal the cosmetic real-form flag from the POST-tick state, in ONE place. The
    /// state methods have several early exits (lost target, point-blank strike, gesture freeze), so
    /// a per-branch seal would silently miss paths and leave a stale disguise on exactly the frames
    /// that matter. Derived level, not a latch: the flag falls back to false on its own when the
    /// phantom returns to WANDER/STALK, so the disguise recomposes without any reset logic. Written
    /// HERE and not in `update_player_state` on purpose — that method stays untouched so the other
    /// five pose fields keep inheriting their defaults for the phantom
    /// (`.claude/rules/pose-relay-wire-rust.md`, step 6).
    ///
    /// ADR-048 seals the VOICE in the same place and for the same reason. `hear_noises` runs before
    /// the FSM and the state methods themselves have many early exits, so a write at the decision
    /// site would miss paths; staging it and sealing here cannot.
    fn seal_cosmetics(&mut self, net: &mut NetworkManager) {
        for m in &mut self.movers {
            if let Some(peer) = net.peers.get_mut(&m.id) {
                peer.revealed = phantom_reveals(m.state);
                peer.crouch = m.mimic_worn.0;
                peer.held_item = m.mimic_worn.1;
                // ADR-080 point 6: the lean and the spray jet ride along, already masked to the
                // bits a phantom may wear (`PHANTOM_MIMIC_BUTTON_MASK`).
                peer.buttons = m.mimic_worn.2;
                // ADR-080 point 1: the stare. The ANGLE is staged by `resolve_mover_tick` on every
                // path (so no early exit can leave a stale one) and the STATE GATE is applied here,
                // against the POST-tick state — exactly like `revealed` above. Gating at stage time
                // would look at last tick's state and lag the stare by a frame in both directions.
                peer.pitch = match phantom_tracks_with_head(m.state) {
                    true => m.look_pitch,
                    false => 0,
                };
                if let Some(kind) = m.pending_vocal.take() {
                    // Wrapping, but never back onto 0: the client treats 0 as "has never
                    // vocalised", so landing there would silently swallow one scream every 255.
                    m.vocal_seq = match m.vocal_seq.wrapping_add(1) {
                        0 => 1,
                        n => n,
                    };
                    m.vocal_kind = kind;
                    info!(
                        "MPTRACE step=PH_VOCAL event=phantom_vocalised phantom_id={} kind={} seq={}",
                        m.id, kind, m.vocal_seq
                    );
                }
                peer.vocal_seq = m.vocal_seq;
                peer.vocal_kind = m.vocal_kind;
            }
        }
    }

    /// ADR-043 — the measurement the whole populated world rests on. Before this, ADR-040's
    /// "2 ms per step" budget, ADR-041's 23.5 µs per chunk and the cost of an A* search were all
    /// documented numbers with NO code behind them: no constant, no assert, no benchmark in the
    /// tree. "How many creatures does the world hold" was therefore unanswerable except by
    /// opinion. These five counters make it a reading.
    ///
    /// `step_us` is the PEAK since the last report, not this step: the throttle samples ~1 step
    /// in 10, so an instantaneous value would miss precisely the spike worth seeing.
    fn report_step_cost(&mut self, net: &NetworkManager, tick_start: Instant) {
        self.step_peak_us = self
            .step_peak_us
            .max(tick_start.elapsed().as_micros() as u64);
        if net.session_start.elapsed().as_millis() % 1000 < 120 {
            info!(
                "MPTRACE step=PH_BUDGET event=phantom_step_budget movers={} step_peak_us={} searches={} chunk_regens={} cached_chunks={} body_valve={}",
                self.movers.len(),
                self.step_peak_us,
                self.nav_replans,
                self.grid_cache.generated_count(),
                self.grid_cache.len(),
                self.grid_cache.degraded_body_check_count()
            );
            self.step_peak_us = 0;
        }
    }
}
