# DiveIn

Adds diving and swimming for players and configured creatures.<br>

Normally mobs can't follow you underwater. So players with diving mod players can just attack mobs from above or below without getting hit.
This mod changes that. <br>Also added a diving mechanic for players with configurable stamina drain according to depth

![](https://i.ibb.co/xKPFy4bv/serpentchase.gif) <br>
![](https://i.ibb.co/pHZrL1W/serpentchase2.gif) <br>
Configured mobs can attack you! No more hiding under their bellies.

![](https://i.ibb.co/tpjzD9NN/Idle.gif) <br>
![](https://i.ibb.co/hxdbQ6dg/idle2.gif) <br>
Idle mobs would swim within the configured range

![](https://i.ibb.co/YBx60Bsw/fishchase.gif) <br>
Recommend to use with RtDOcean. There is configured sample for it.

### Player Diving

- Unlike vanilla Valheim, stamina regeneration while swimming can be enabled separately for surface swimming and midwater diving.
- Idle stamina drain while your head is underwater can scale by current liquid depth to represent holding your breath.
- Extra stamina drain scales linearly per 1m of current liquid depth and stacks multiplicatively with Fast Swim.
- Pressing the vanilla run key in water toggles Fast Swim, doubling overall swim speed by default and consuming configurable extra stamina. Swim skill still improves base swim speed by a configurable amount.
- Swimming key hint labels are localized for supported Valheim languages.
- Surface swimming keeps the player's vanilla swim depth and only changes depth while diving.
- Attacking, secondary attacking, and guarding underwater stop swim movement so combat input takes priority.
- Use equipment in water except that is in blacklist.

### Creature Diving

- Configured monster prefabs stop avoiding water and can navigate underwater.
- Idle monsters use passive depth profiles and slowly drift within their assigned depth range.
- Alerted or chasing monsters adjust depth toward their target within global depth limits.
- Underwater pathing uses route checks and steering avoidance samples instead of blindly forcing success.
- Underwater creature movement uses high-quality move-plan checks by default.

### DiveIn.yaml

This file defines which monsters can dive and what passive depth band each monster uses.

```
# Monster dive configuration for DiveIn.
# Unknown keys and duplicate keys are treated as errors and keep the previous applied settings.

surface_patrol: # You can use any group name. Add your own groups
  passive_min_depth: 0 # Shallowest passive dive depth used while the monster has no target and is not alerted.
  passive_center_depth: 10 # Center depth used by the passive sine-wave swimming pattern.
  passive_max_depth: 20 # Deepest passive dive depth used while the monster has no target and is not alerted.
  active_depth_adjust_speed: 2 # How quickly this group adjusts swim depth while alerted or chasing a target.
  prefabs: # Monster prefab names assigned to this passive profile group.
    - Leech
    - Abomination
    - Serpent
    - BonemawSerpent

mid_water:
  passive_min_depth: 0
  passive_center_depth: 15
  passive_max_depth: 30
  active_depth_adjust_speed: 2
  prefabs: []

deep_patrol:
  passive_min_depth: 10
  passive_center_depth: 20
  passive_max_depth: 30
  active_depth_adjust_speed: 2
  prefabs: []

## Mod prefabs sample

mods_surface:
  passive_min_depth: 0
  passive_center_depth: 10
  passive_max_depth: 20
  active_depth_adjust_speed: 2
  prefabs:
    - Neck_RtD
    - Animal_Dolphin_RtD
    - Animal_Cod_RtD
    ...
```
Notes:

- If the same prefab appears in multiple groups, only the first assignment is kept.
- Invalid YAML keeps the previously applied settings.
- When the server is the source of truth, clients use the synced server YAML instead of their local file.

## Config
```
[1 - General]

## If on, the configuration is locked and can be changed by server admins only. [Synced with Server]
# Setting type: Toggle
# Default value: On
# Acceptable values: Off, On
Lock Configuration = On

[2 - Player Diving]

## Client-side key used to ascend while swimming underwater. [Not Synced with Server]
# Setting type: KeyboardShortcut
# Default value: Space
Dive Ascend Key = Space

## Client-side key used to descend while swimming. [Not Synced with Server]
# Setting type: KeyboardShortcut
# Default value: LeftControl
Dive Descend Key = LeftControl

## Comma-separated item prefab names that remain restricted in water. Everything not listed is allowed in water by default. Example: BowFineWood,ShieldBronzeBuckler. [Synced with Server]
# Setting type: String
# Default value:
Water Equipment Blacklist =

## Underwater darkness added per meter of swim depth. 1 means 1% per meter, so 30m gives 30%. [Synced with Server]
# Setting type: Single
# Default value: 0.5
# Acceptable value range: From 0 to 3
Darkness Factor = 0.5

## Underwater fog density added per meter of swim depth. 1 means 1% per meter, so 30m adds 30%. [Synced with Server]
# Setting type: Single
# Default value: 0.25
# Acceptable value range: From 0 to 3
Murkiness Factor = 0.25

[3 - Swim Resources]

## Multiplier applied to vanilla stamina regeneration while swimming on the surface with your head above water. 0 matches vanilla swimming behavior, 1 matches normal non-swimming stamina regeneration timing and rate. [Synced with Server]
# Setting type: Single
# Default value: 0.5
# Acceptable value range: From 0 to 1
Surface Stamina Regen Rate = 0.5

## Multiplier applied to vanilla stamina regeneration while your head is underwater. 0 makes stamina recover only after surfacing. [Synced with Server]
# Setting type: Single
# Default value: 0
# Acceptable value range: From 0 to 1
Midwater Stamina Regen Rate = 0

## Multiplier applied to vanilla eitr regeneration while swimming on the surface with your head above water. 0 disables eitr regeneration while surface swimming, 1 keeps vanilla eitr regeneration. [Synced with Server]
# Setting type: Single
# Default value: 0.7
# Acceptable value range: From 0 to 1
Surface Eitr Regen Rate = 0.7

## Multiplier applied to vanilla eitr regeneration while your head is underwater. 0 makes eitr recover only after surfacing. [Synced with Server]
# Setting type: Single
# Default value: 0.3
# Acceptable value range: From 0 to 1
Midwater Eitr Regen Rate = 0.3

## Idle stamina drained per second per 1m of current liquid depth while your head is underwater. 0 disables idle underwater stamina drain. Example: 0.1 drains 3 stamina per second at 30m depth. [Synced with Server]
# Setting type: Single
# Default value: 0.02
# Acceptable value range: From 0 to 1
Midwater Idle Stamina Drain Per Depth = 0.02

## Additional moving swim stamina drain percent per 1m of current liquid depth. 1 means 30% extra at 30m; 2.5 means 75% extra at 30m. Applied multiplicatively with base and Fast Swim stamina drain. [Synced with Server]
# Setting type: Single
# Default value: 2.5
# Acceptable value range: From 0 to 5
Swim Stamina Drain Multiplier Per Depth = 2.5

## If on, status-effect swim stamina use modifiers stack multiplicatively during actual swim stamina consumption. Example: -50% and -60% leaves 20% cost instead of 0%. Tooltips keep vanilla display behavior. [Synced with Server]
# Setting type: Toggle
# Default value: On
Multiplicative Swim Stamina Modifiers = On

## Multiplier applied to vanilla moving swim stamina drain before depth and Fast Swim multipliers. 1 keeps vanilla cost, 0.5 halves it, 2 doubles it. [Synced with Server]
# Setting type: Single
# Default value: 1
# Acceptable value range: From 0.1 to 2
Swim Stamina Drain Base Multiplier = 1

[4 - Swim Speed]

## Base swim speed multiplier at Swim skill 100. 1.5 means +50%. [Synced with Server]
# Setting type: Single
# Default value: 1.5
# Acceptable value range: From 1 to 3
Swim Skill Speed Multiplier = 1.5

## Swim speed multiplier while Fast Swim is toggled on with the vanilla run key. Swim skill separately increases base swim speed. [Synced with Server]
# Setting type: Single
# Default value: 2
# Acceptable value range: From 1 to 3
Fast Swim Speed Multiplier = 2

## Moving swim stamina drain multiplier while Fast Swim is toggled on. Applied multiplicatively with base and depth stamina drain. [Synced with Server]
# Setting type: Single
# Default value: 2
# Acceptable value range: From 1 to 5
Fast Swim Stamina Drain Multiplier = 2

```

### Git
The player diving implementation includes code derived and modified from `UnderTheSea` <br>
https://github.com/searica/UnderTheSea <br>
https://github.com/sighsorry1029/DiveIn
