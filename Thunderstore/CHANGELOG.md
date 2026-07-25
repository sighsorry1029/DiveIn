# Changelog

## 1.1.9

- Fixed swimming equipment bypass being disabled by a false branch-target validation failure, which caused repeated startup warnings and left vanilla swimming restrictions active.
- Changed the synchronized Water Equipment Blacklist to preserve restrictions per listed item: listed armor and accessories no longer stow unrelated hand equipment, while listed hand items retain vanilla hand-item hiding.
- Improved shallow-water flee behavior so configured creatures keep retreating until they clear the exit-depth buffer without skipping vanilla sleep, riding, event, or despawn processing.
- Restored vanilla serpent stopping distance and swoop movement while using bounded local steering lookahead for smoother underwater pursuit.
- Hardened `DiveIn.yaml` reloads and validation: rapid saves are no longer dropped, duplicate content is ignored, non-finite values keep the last valid configuration, and supported depth limits are normalized with warnings.
- Improved swimming exception recovery and key-hint stability by reliably restoring temporary movement state, cleaning failed hint clones, and rebuilding layouts only when their content or visibility changes.
- Improved underwater camera, fog, and surface-material restoration so DiveIn does not overwrite newer environment or mod changes when leaving the water.
- Improved reload and shutdown cleanup, including restoration of configured creatures' original swim depth and removal of stale per-creature tracking state.
- Added a synchronized version-update command and fail-fast Release packaging checks; normal builds no longer deploy to the game unless explicitly requested.

## 1.1.8

- Fixed Space-only ascent stopping just below the surface unless a movement key was also held.
- Players now smoothly level their swimming posture when surfacing, without requiring forward movement input.

## 1.1.7

- Fixed visible water-zone seams and stale surface material state when viewing waves from underwater.
- Improved configured monster avoidance steering stability near obstructed routes.
- Added the per-group `avoidance_steering` field to `DiveIn.yaml`; existing files default to `true`, while `false` keeps depth behavior and makes the group swim directly toward its target.
- Moved `SA_WhiteShark` into the generated `mods_surface_nosteering` sample group to avoid its underwater left-right jitter.
- Improved WeaponHolsterOverhaul compatibility by restoring hidden blocking equipment only once on the initial block input and only when no blocker is already equipped.

## 1.1.6

- Improved shallow-dive camera behavior by keeping the camera below the water surface while the player's head is submerged, reducing abrupt transitions to raw unstyled water and seabed visuals near the surface.

## 1.1.5

- Encumbered players can no longer use Fast Swim; its toggle state is cleared and its key hint is hidden until they are no longer encumbered.
- Added a synced Encumbered Swim Speed Multiplier setting (default `0.5`, range `0.1`-`1.0`) for tuning normal swim speed while encumbered.

## 1.1.4

- Fixed occasional flat square water-surface visuals left behind after diving.
- Improved the view from underwater so the surface keeps wave and water shader details when looking up.

## 1.1.3

- Fast Swim can now use `Toggle` mode, which keeps the existing press-to-toggle behavior, or `Press` mode, which keeps Fast Swim active only while the vanilla run key is held.

## 1.1.2

- Added per-group `DiveIn.yaml` monster tuning fields: `active_min_depth`, `shallow_water_flee_depth`, and `preserve_spawn_depth`.
- Existing `DiveIn.yaml` files remain compatible; missing new fields use defaults of `active_min_depth: 0.25`, `shallow_water_flee_depth: 0`, and `preserve_spawn_depth: false`.
- Newly generated `DiveIn.yaml` files include the new fields for each group, so customized YAML users can copy only the fields they want to tune.
- Changed the default underwater projectile lifetime, speed, and damage multipliers to `0.5` for player-owned projectiles fired underwater.

## 1.1.1

- Added synced underwater projectile multipliers for player-owned projectiles fired underwater, allowing servers to reduce projectile lifetime, speed, and damage in underwater combat.
- Fixed DiveIn swimming key hints so Fast Swim, Descend, and Ascend labels/keys are refreshed correctly instead of inheriting stale vanilla combat hint text.

## 1.1.0

- Added Swim Stamina Drain Base Multiplier so servers can tune vanilla moving swim stamina cost before depth and Fast Swim multipliers.
- Added Multiplicative Swim Stamina Modifiers so swim stamina status effects such as MeadSwimmer and Eikthyr stack multiplicatively during actual swim stamina consumption.
- Moving swim stamina drain now scales from the player's actual vanilla swim drain, including Swim skill, equipment, and status effects.
- Reorganized swim-related config sections into Regen Rate, Stamina Drain, and Swim Speed.
- Optimized swim resource adjustment, underwater visual state handling, swimming key hints, and configured monster dive lifecycle code for simpler and safer behavior.

## 1.0.9

- Added Surface Eitr Regen Rate and Midwater Eitr Regen Rate config options to scale total eitr regeneration while swimming.

## 1.0.8

- Added Midwater Idle Stamina Drain Per Depth so idle underwater stamina drain scales with current liquid depth.
- Minor optimizations and config cleanup.

## 1.0.7

- Split water stamina regeneration into separate Surface and Midwater rates so stamina can recover only after surfacing by default.
- Added depth-scaled idle underwater stamina drain to simulate holding breath.
- Added Fast Swim Stamina Drain Multiplier so Fast Swim stamina cost can be configured separately from Fast Swim speed.
- Renamed stamina and Fast Swim config options for clearer per-depth behavior.
- Grouped stamina and speed config options into Swim Stamina and Swim Speed sections.
- Underwater visual styling is now always enabled; Darkness and Murkiness moved to Player Diving with softer synced defaults.

## 1.0.6

- Improved configured creature underwater AI near the ocean floor.
- Unified underwater route and steering checks into a single move plan cache.
- Removed Dive AI Quality config; underwater AI now uses high-quality behavior by default.
- Improved player ascent when stuck against the ocean floor.
- Added configurable ascend/descend dive keys and localized swimming key hints, including gamepad-aware key hints and live Fast Swim On/Off hints.
- Fixed vanilla hide/show weapon input so hidden weapons can be drawn again while underwater.
- Reworked underwater combat so attack, secondary attack, and guard inputs stop swim movement and take priority.
- Preserved the player's vanilla surface swim depth instead of replacing it with a DiveIn hardcoded value.
- Reworked underwater water-surface rendering to preserve above-surface visuals while reducing sky-through-water and waterline clipping issues.
- Reworked run-swimming so the run key toggles Fast Swim at Swim skill 0, consumes extra stamina, and lets Swim skill speed be configured as a multiplier.
- Setting Swim Run Speed Multiplier to 1 disables Fast Swim and hides its key hint.
- Simplified depth stamina drain config to one per-meter multiplier and applied depth/run stamina drain multiplicatively.

## 1.0.5

- Added underwater visual config options.

## 1.0.4

- Changed the `DiveIn.yaml` schema. Delete the existing `DiveIn.yaml` and restart the game to regenerate it.
- Added more creatures from other mods to the default `DiveIn.yaml`.

## 1.0.2

- Made swim speed increase according to Swim skill level.
- Final swim speed = base swim speed x [1 + (config value - 1) x (Swim skill level / 100)^1.5].

## 1.0.1

- Replaced the incorrect DLL from the initial package.

## 1.0.0

- Initial release.
