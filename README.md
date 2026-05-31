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
- Extra stamina drain can scale with depth. Deeper water becomes more expensive than staying near the surface.
- Use equipment in water except that is in blacklist.

### Creature Diving

- Configured monster prefabs stop avoiding water and can navigate underwater.
- Idle monsters use passive depth profiles and slowly drift within their assigned depth range.
- Alerted or chasing monsters adjust depth toward their target within global depth limits.
- Underwater pathing uses route checks and steering avoidance samples instead of blindly forcing success.
- A single `Dive AI Quality` slider trades CPU cost for smoother underwater movement.

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
# Acceptable value range: From 0 to 2
Water Stamina Regen Rate = 0.5

## Depth in meters below the surface where extra swim stamina drain begins. [Synced with Server]
# Setting type: Single
# Default value: 3
# Acceptable value range: From 0 to 50
Water Depth Stamina Drain Start = 3

## Depth in meters below the surface where the maximum extra swim stamina drain multiplier is reached. [Synced with Server]
# Setting type: Single
# Default value: 30
# Acceptable value range: From 0.25 to 300
Water Depth Stamina Drain Full = 30

## Maximum multiplier applied to vanilla moving swim stamina drain at or below the full depth. [Synced with Server]
# Setting type: Single
# Default value: 1.5
# Acceptable value range: From 1 to 5
Water Depth Stamina Drain Max Multiplier = 1.5

## Final swim speed while swimming and holding the run key = base swim speed x [1 + (this value - 1) x (Swim skill level / 100)^1.5]. [Synced with Server]
# Setting type: Single
# Default value: 1.5
# Acceptable value range: From 1 to 3
Swim Run Speed Multiplier = 1.5

[3 - Performance]

## Single quality slider for underwater AI behavior. 0 = minimum CPU/minimum smoothness, 100 = maximum CPU/maximum smoothness. Internally adjusts route check cache time, steer cache time, cache cell size, and avoidance sample count. [Synced with Server]
# Setting type: Single
# Default value: 50
# Acceptable value range: From 0 to 100
Dive AI Quality = 50
```

### Git
The player diving implementation includes code derived and modified from `UnderTheSea` <br>
https://github.com/searica/UnderTheSea <br>
https://github.com/sighsorry1029/DiveIn
