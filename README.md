# DiveIn

`DiveIn` is a Valheim underwater expansion mod. It adds player diving, underwater camera and visual handling, relaxed equipment restrictions in water, and YAML-driven monster dive AI.

Current version: `1.0.0`

## Features

### 1. Player Diving

- Press `Crouch` while swimming to dive downward.
- Press `Jump` while below the surface to ascend.
- Hold `Run` underwater to swim faster.
- Diving is blocked in very shallow water or when the player is effectively grounded.
- Player diving is handled locally, so each client controls diving for their own character.

### 2. Underwater Stamina Tuning

- Unlike vanilla Valheim, stamina regeneration while swimming or diving can be enabled through config.
- Extra stamina drain can scale with depth.
- With default settings, deeper water becomes more expensive than staying near the surface.

### 3. Relaxed Equipment Restrictions in Water

- The mod bypasses vanilla swimming equipment restrictions for the local player by default.
- Most equipment that vanilla blocks while swimming can still be used underwater.
- Items listed in `Water Equipment Blacklist` keep the vanilla restriction.

### 4. Underwater Camera and Visuals

- Adjusts camera follow behavior so the camera stays usable below the surface.
- Changes fog color and density underwater for a clearer submerged look.
- Applies water-surface rendering fixes to reduce below-surface visual glitches.
- `Underwater Camera Min Water Distance` lets each client tune camera behavior if needed.

### 5. Monster Dive AI

- Configured monster prefabs stop avoiding water and can navigate underwater.
- Idle monsters use passive depth profiles and slowly drift within their assigned depth range.
- Alerted or chasing monsters adjust depth toward their target within global depth limits.
- Underwater pathing uses route checks and steering avoidance samples instead of blindly forcing success.
- A single `Dive AI Quality` slider trades CPU cost for smoother underwater movement.

### 6. Server Sync and Hot Reload

- Main gameplay config values are synchronized through `ServerSync`.
- Monster dive YAML data is also synchronized from the server authority.
- Clients with a different `DiveIn` version are disconnected.
- Both the `cfg` file and monster YAML file are watched and reloaded on change.

## Installation

### Requirement

- `BepInExPack Valheim 5.4.2202` or newer

### Install Steps

1. Place `DiveIn.dll` in `BepInEx/plugins`.
2. Launch the game once.
3. Confirm that these files are created:

- `BepInEx/config/sighsorry.DiveIn.cfg`
- `BepInEx/config/DiveIn.yaml`

For multiplayer, the server and all clients must use the same `DiveIn` version.

## Default Controls

- `Crouch`: Dive down
- `Jump`: Ascend
- `Run`: Faster underwater movement

## Configuration Files

### `sighsorry.DiveIn.cfg`

Important entries:

- `Lock Configuration`
  Controls whether synced settings are locked to server admins.
- `Water Equipment Blacklist`
  Comma-separated item prefab names that should remain restricted in water.
- `Water Stamina Regen Rate`
  Stamina regeneration multiplier while swimming or diving.
- `Water Depth Stamina Drain Start`
  Depth where extra swim stamina drain begins.
- `Water Depth Stamina Drain Full`
  Depth where the maximum extra drain multiplier is reached.
- `Water Depth Stamina Drain Max Multiplier`
  Maximum extra stamina drain multiplier at deep water.
- `Swim Run Speed Multiplier`
  Speed multiplier while holding `Run` underwater.
- `Dive AI Quality`
  Global quality/performance slider for underwater monster AI.
- `Underwater Camera Min Water Distance`
  Client-only camera override used to improve underwater camera behavior.
- `Log Underwater Visual State`
  Client-only debug logging for camera, fog, and water-surface visual state.
- `Log Overlap Fields`
  Debug logging for monster AI field overlap issues.

`Underwater Camera Min Water Distance` and the debug logging entries are client-only and are not synchronized by the server.

### `DiveIn.yaml`

This file defines which monsters can dive and what passive depth band each monster uses.

- `global`
  Global min/max chase depth and swim-depth adjustment speed.
- `groups`
  Named passive depth profiles.
- `prefabs`
  Exact Valheim monster prefab names assigned to a group.

Default example:

```yaml
global:
  swim_depth_min: 0.25
  swim_depth_max: 30
  swim_depth_adjust_speed: 20

groups:
  surface_patrol:
    passive_min_depth: 0
    passive_center_depth: 10
    passive_max_depth: 20
    prefabs:
      - Serpent

  mid_water:
    passive_min_depth: 0
    passive_center_depth: 15
    passive_max_depth: 30
    prefabs: []

  deep_patrol:
    passive_min_depth: 10
    passive_center_depth: 20
    passive_max_depth: 30
    prefabs: []
```

Notes:

- If the same prefab appears in multiple groups, only the first assignment is kept.
- Invalid YAML keeps the previously applied settings.
- When the server is the source of truth, clients use the synced server YAML instead of their local file.

## Compatibility Notes

- Player diving, underwater camera handling, and relaxed water equipment restrictions are currently always enabled, with tuning handled through config values.
- Monster dive AI only applies to configured `MonsterAI`-based prefabs.
- If `MonsterDB` is detected, extra overlap debug logging can be enabled to inspect field interactions.

## License

This project is licensed under `GPL-3.0`.

The player diving implementation includes code derived and modified from `UnderTheSea`, which is also licensed under GPL-3.0.
