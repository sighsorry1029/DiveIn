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

- Unlike vanilla Valheim, stamina regeneration while swimming or diving can be enabled through config.
- Extra stamina drain scales linearly per meter of current liquid depth and stacks multiplicatively with run-swimming.
- Pressing the vanilla run key in water toggles Fast Swim, doubling overall swim speed by default and consuming extra stamina. Swim skill still improves base swim speed by a configurable amount.
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

[2a - Player Diving]

## Comma-separated item prefab names that remain restricted in water. Everything not listed is allowed in water by default. Example: BowFineWood,ShieldBronzeBuckler. [Synced with Server]
# Setting type: String
# Default value:
Water Equipment Blacklist =

## Multiplier applied to vanilla stamina regeneration while swimming or diving in water. 0 matches vanilla swimming behavior (effective stamina regeneration stays at 0), 1 matches vanilla normal non-swimming stamina regeneration timing and rate. [Synced with Server]
# Setting type: Single
# Default value: 0.5
# Acceptable value range: From 0 to 1
Water Stamina Regen Rate = 0.5

## Additional moving swim stamina drain percent per meter of current liquid depth. 1 means 30% extra at 30m; 2.5 means 75% extra at 30m. Applied multiplicatively with run-swimming stamina drain. [Synced with Server]
# Setting type: Single
# Default value: 2.5
# Acceptable value range: From 0 to 5
Water Depth Stamina Drain Multiplier = 2.5

## Base swim speed multiplier at Swim skill 100. 1.5 means +50%. [Synced with Server]
# Setting type: Single
# Default value: 1.5
# Acceptable value range: From 1 to 3
Swim Skill Speed Multiplier = 1.5

## Swim speed multiplier while Fast Swim is toggled on with the vanilla run key. Swim skill separately increases base swim speed, and extra stamina drain scales with this multiplier. [Synced with Server]
# Setting type: Single
# Default value: 2
# Acceptable value range: From 1 to 3
Swim Run Speed Multiplier = 2

[3 - Underwater Visuals]

## Whether underwater fog and reversed water surface styling are applied while submerged. [Not Synced with Server]
# Setting type: Toggle
# Default value: On
# Acceptable values: Off, On
Enable Underwater Visual Styling = On

## Underwater darkness added per meter of swim depth. 1 means 1% per meter, so 30m gives 30%. [Not Synced with Server]
# Setting type: Single
# Default value: 2
# Acceptable value range: From 0 to 10
Darkness Factor = 2

## Underwater fog density added per meter of swim depth. 1 means 1% per meter, so 30m adds 30%. [Not Synced with Server]
# Setting type: Single
# Default value: 1
# Acceptable value range: From 0 to 10
Murkiness Factor = 1

```

### Git
The player diving implementation includes code derived and modified from `UnderTheSea` <br>
https://github.com/searica/UnderTheSea <br>
https://github.com/sighsorry1029/DiveIn
